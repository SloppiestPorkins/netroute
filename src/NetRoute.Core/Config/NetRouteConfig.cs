using System.Text.Json;
using System.Text.Json.Serialization;
using NetRoute.Core.Policy;

namespace NetRoute.Core.Config;

/// <summary>An hour range, which may wrap past midnight (22 to 2 is four hours of evening).</summary>
public sealed record QuietHours(int FromHour, int ToHour)
{
    public bool Contains(DateTime now) => FromHour == ToHour
        ? false
        : FromHour < ToHour
            ? now.Hour >= FromHour && now.Hour < ToHour
            : now.Hour >= FromHour || now.Hour < ToHour;

    public override string ToString() => $"{FromHour:00}:00 to {ToHour:00}:00";
}

public sealed record NetRouteConfig
{
    public int Version { get; init; } = 1;

    /// <summary>True once the user has been through first-run setup (§52).</summary>
    public bool SetupCompleted { get; init; }

    public List<RoleBinding> RoleBindings { get; init; } = [];
    public List<AppRule> AppRules { get; init; } = [];

    /// <summary>Global pause. Enforcement stops but rules are retained (§41).</summary>
    public bool EnforcementPaused { get; init; }

    /// <summary>When set, the pause ends by itself at this time. Null means paused until resumed.</summary>
    public DateTimeOffset? PausedUntil { get; init; }

    /// <summary>
    /// Keep Windows' own download services (Windows Update, Store, Xbox app) on the Downloads
    /// network too. They aren't apps a user can pick, so this is a switch rather than a rule.
    /// </summary>
    public bool RouteSystemDownloads { get; init; } = true;

    /// <summary>
    /// Block Downloads apps while a Gaming app is running. Off by default: with two connections
    /// the downloads are already on the other line. It is what makes NetRoute useful on a PC
    /// with only one connection, where separating traffic isn't possible at all.
    /// </summary>
    public bool PauseDownloadsWhileGaming { get; init; }

    /// <summary>Hours when Downloads apps are blocked whatever else is happening, e.g. your usual gaming evening.</summary>
    public QuietHours? DownloadQuietHours { get; init; }

    /// <summary>
    /// Where to look for a newer NetRoute, as JSON: version, url, notes. Empty by default, since
    /// a build nobody published has nowhere to look, and a check nobody asked for is a call home.
    /// </summary>
    public string? UpdateFeedUrl { get; init; }

    /// <summary>
    /// Check that address daily and fetch what it finds, so the update is ready when the user
    /// wants it. Installing is still a click: nothing replaces NetRoute while you are playing.
    /// </summary>
    public bool AutomaticUpdates { get; init; } = true;

    public RoleBinding? BindingFor(RoleId role)
        => RoleBindings.FirstOrDefault(b => b.Role == role);
}

/// <summary>
/// Loads and saves configuration as JSON under %ProgramData%\NetRoute.
///
/// <para>ProgramData rather than the user profile because the enforcement service owns
/// the policy state (§42) and runs outside any interactive user session.</para>
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly object _gate = new();

    public ConfigStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetRoute",
            "config.json");
    }

    public string FilePath => _path;

    public NetRouteConfig Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new NetRouteConfig();
            }

            try
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<NetRouteConfig>(json, SerializerOptions) ?? new NetRouteConfig();
            }
            catch (JsonException)
            {
                // A corrupt config must not take enforcement down with it. Preserve the
                // bad file for diagnosis and start clean rather than crash-looping the
                // service, which would leave the user with no enforcement and no UI.
                var quarantine = _path + ".corrupt";
                try
                {
                    File.Copy(_path, quarantine, overwrite: true);
                }
                catch (IOException)
                {
                    // Best effort only.
                }
                return new NetRouteConfig();
            }
        }
    }

    public void Save(NetRouteConfig config)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

            // Write-then-replace so a crash mid-save cannot truncate the live config.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(config, SerializerOptions));
            File.Move(temp, _path, overwrite: true);
        }
    }
}
