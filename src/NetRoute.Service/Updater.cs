using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using NetRoute.Core.Config;
using NetRoute.Ipc;

namespace NetRoute.Service;

public interface IUpdateSource
{
    /// <summary>The newer version, if one was found: what it is, and how far along fetching it is.</summary>
    UpdateDto? Available { get; }

    UpdateSettingsDto Settings();

    Task<UpdateDto?> CheckAsync(CancellationToken ct);

    Task<UpdateDto?> DownloadAsync(CancellationToken ct);
}

public sealed class NoUpdates : IUpdateSource
{
    public UpdateDto? Available => null;

    public UpdateSettingsDto Settings() => new("0.0.0", null, false, null, null);

    public Task<UpdateDto?> CheckAsync(CancellationToken ct) => Task.FromResult<UpdateDto?>(null);

    public Task<UpdateDto?> DownloadAsync(CancellationToken ct) => Task.FromResult<UpdateDto?>(null);
}

/// <summary>
/// Finds a newer NetRoute, fetches it, and proves it is the file the feed promised. It never
/// installs anything.
///
/// <para>The feed is a small JSON document the user points NetRoute at — nothing is configured
/// out of the box, so a fresh install never calls anywhere:</para>
/// <code>{ "version": "1.1.0", "url": "https://…/NetRoute-Setup-1.1.0.exe", "sha256": "…", "notes": "…" }</code>
///
/// <para>The hash is what makes this safe to automate. NetRoute's installer is not code-signed,
/// so the only thing that ties the bytes on disk to the version the user chose to trust is the
/// digest in a document fetched over TLS from an address they set themselves. No digest, no
/// download: the update is shown as a link and the user goes and gets it. A download that hashes
/// differently is deleted rather than kept, because a half-right installer is worse than none.</para>
///
/// <para>Installing is deliberately somewhere else. The service runs as LocalSystem, and a
/// service that can silently replace its own program is a far better target than one that cannot.
/// The app asks the user, Windows asks for administrator, and setup — which already knows how to
/// stop the service, keep a backup and roll back — does the work.</para>
/// </summary>
public sealed class Updater(ILogger<Updater> logger) : BackgroundService, IUpdateSource
{
    private static readonly Version Current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>Big enough for a self-contained build with the driver, small enough to notice nonsense.</summary>
    private const long MaxBytes = 400L * 1024 * 1024;

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetRoute", "updates");

    private readonly ConfigStore _store = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task? _download;

    public UpdateDto? Available { get; private set; }

    public DateTimeOffset? CheckedAt { get; private set; }

    public UpdateSettingsDto Settings()
    {
        var config = _store.Load();
        return new UpdateSettingsDto(Current.ToString(3), config.UpdateFeedUrl, config.AutomaticUpdates, CheckedAt, Available);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A minute in, so a PC that has just started is not racing setup for the network.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = _store.Load();
                if (config.AutomaticUpdates && !string.IsNullOrWhiteSpace(config.UpdateFeedUrl))
                {
                    await CheckAsync(stoppingToken);
                    if (Available is { State: UpdateState.Available })
                    {
                        await DownloadAsync(stoppingToken);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation(ex, "Update check failed.");
            }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    // ------------------------------------------------------------------ check

    public async Task<UpdateDto?> CheckAsync(CancellationToken ct)
    {
        var config = _store.Load();
        CheckedAt = DateTimeOffset.Now;

        if (string.IsNullOrWhiteSpace(config.UpdateFeedUrl))
        {
            return Available = null;
        }
        if (!Secure(config.UpdateFeedUrl))
        {
            return Available = Failed("0.0.0", config.UpdateFeedUrl!, "Updates are only fetched over https. Change the update address.");
        }

        string json;
        try
        {
            using var http = Http();
            json = await http.GetStringAsync(config.UpdateFeedUrl, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Reading the update feed failed.");
            return Available = Failed("0.0.0", config.UpdateFeedUrl!, "NetRoute couldn't reach the update address.");
        }

        var found = Read(json, Current);
        if (found is null || found.State == UpdateState.Failed)
        {
            return Available = found;
        }

        // Already fetched and checked on an earlier run: don't fetch it twice.
        var file = Destination(found);
        if (found.Sha256 is { Length: > 0 } sha && File.Exists(file) && Hash(file).Equals(sha, StringComparison.OrdinalIgnoreCase))
        {
            return Available = found with { State = UpdateState.Ready, ReadyPath = file, Fraction = 1 };
        }
        return Available = found;
    }

    /// <summary>
    /// What the feed says, as an update or as nothing. Separate from fetching it so the rules —
    /// what a feed must contain, and that only a higher version counts — can be tested on their own.
    /// </summary>
    public static UpdateDto? Read(string json, Version current)
    {
        JsonElement root;
        try
        {
            // A feed saved by a Windows editor often starts with a byte order mark, and
            // System.Text.Json will not look past one.
            using var document = JsonDocument.Parse(json.Trim().TrimStart('\uFEFF'));
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Failed("0.0.0", "", "The update address didn't answer with an update document.");
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out var versionText) || !Version.TryParse(versionText.GetString(), out var version)
            || !root.TryGetProperty("url", out var urlText) || urlText.GetString() is not { Length: > 0 } url)
        {
            return Failed("0.0.0", "", "The update address didn't answer with a version and a download link.");
        }

        if (version <= current)
        {
            return null;
        }

        return new UpdateDto(version.ToString(3), url, root.TryGetProperty("notes", out var n) ? n.GetString() : null)
        {
            Sha256 = root.TryGetProperty("sha256", out var h) ? h.GetString()?.Trim() : null,
            SizeBytes = root.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0
        };
    }

    // --------------------------------------------------------------- download

    public async Task<UpdateDto?> DownloadAsync(CancellationToken ct)
    {
        if (Available is not { } update)
        {
            return null;
        }
        if (update.State is UpdateState.Ready or UpdateState.Downloading)
        {
            return update;
        }
        if (!Secure(update.Url))
        {
            return Available = Failed(update.Version, update.Url, "The download link isn't an https address, so NetRoute won't fetch it.");
        }
        if (update.Sha256 is not { Length: 64 })
        {
            return Available = Failed(update.Version, update.Url,
                "The update address didn't publish a sha256 for this file, so NetRoute can't tell whether a download is the real one. Use the link instead.");
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_download is { IsCompleted: false })
            {
                return Available;
            }
            Available = update with { State = UpdateState.Downloading, Fraction = 0 };
            _download = Task.Run(() => FetchAsync(update, ct), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
        return Available;
    }

    private async Task FetchAsync(UpdateDto update, CancellationToken ct)
    {
        var target = Destination(update);
        var part = target + ".part";
        try
        {
            Directory.CreateDirectory(Folder);
            Protect(Folder);

            using var http = Http();
            using var response = await http.GetAsync(update.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.SizeBytes;
            if (total > MaxBytes)
            {
                throw new InvalidOperationException($"The download is {total / 1024 / 1024} MB, which is larger than NetRoute expects.");
            }

            using (var source = await response.Content.ReadAsStreamAsync(ct))
            using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[128 * 1024];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (done > MaxBytes)
                    {
                        throw new InvalidOperationException("The download kept going past the size NetRoute expects.");
                    }
                    Available = update with
                    {
                        State = UpdateState.Downloading,
                        SizeBytes = total,
                        Fraction = total > 0 ? Math.Min(1, (double)done / total) : 0
                    };
                }
            }

            var actual = Hash(part);
            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                logger.LogWarning("The update download hashed {Actual}, not {Expected}.", actual, update.Sha256);
                Available = Failed(update.Version, update.Url,
                    "What was downloaded isn't the file the update address described, so NetRoute deleted it.");
                return;
            }

            File.Delete(target);
            File.Move(part, target);
            Tidy(target);
            logger.LogInformation("NetRoute {Version} is downloaded and ready at {Path}.", update.Version, target);
            Available = update with { State = UpdateState.Ready, ReadyPath = target, Fraction = 1, SizeBytes = new FileInfo(target).Length };
        }
        catch (OperationCanceledException)
        {
            Available = update;   // the service is stopping; nothing to say
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Downloading the update failed.");
            Available = Failed(update.Version, update.Url, "The download didn't finish: " + ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(part);
            }
            catch (IOException)
            {
                // It is in %ProgramData%; the next run overwrites it.
            }
        }
    }

    // ----------------------------------------------------------------- pieces

    private static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"NetRoute/{Current.ToString(3)}");
        return http;
    }

    private static bool Secure(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static string Destination(UpdateDto update)
        => Path.Combine(Folder, $"NetRoute-Setup-{update.Version}.exe");

    private static UpdateDto Failed(string version, string url, string problem)
        => new(version, url, null) { State = UpdateState.Failed, Problem = problem };

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Only SYSTEM and administrators may write here. The file is an installer that will be run
    /// elevated, so a standard account being able to swap it would hand that account the machine.
    /// </summary>
    private static void Protect(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            }
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch (Exception)
        {
            // Inherited permissions from %ProgramData% still keep standard users out of SYSTEM's files.
        }
    }

    /// <summary>Keeps the one installer that is current; older ones are megabytes doing nothing.</summary>
    private static void Tidy(string keep)
    {
        try
        {
            foreach (var file in Directory.GetFiles(Folder, "NetRoute-Setup-*.exe")
                         .Where(f => !f.Equals(keep, StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(file);
            }
        }
        catch (IOException)
        {
            // Not worth failing an update over.
        }
    }
}
