using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetRoute.Core.Config;
using NetRoute.Ipc;

namespace NetRoute.App;

/// <summary>
/// Running an update that the service has already fetched and checked.
///
/// <para>The hash is checked here as well, immediately before the file is run. The service put it
/// in a folder only SYSTEM and administrators can write to and verified it on the way in, so this
/// is belt and braces — but the next thing that happens is Windows raising this file to
/// administrator, and a second read costs a moment.</para>
/// </summary>
internal static class Upgrade
{
    /// <summary>Runs the installer as administrator. It stops the service, swaps the files and starts it again.</summary>
    public static async Task<string?> StartAsync(UpdateDto update)
    {
        if (update.ReadyPath is not { Length: > 0 } path || !File.Exists(path))
        {
            return "The downloaded update isn't there any more. Check for it again.";
        }

        if (update.Sha256 is { Length: 64 } expected)
        {
            var actual = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(stream));
            });
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                return "The downloaded update isn't the file NetRoute checked. It hasn't been run.";
            }
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, "/update") { UseShellExecute = true, Verb = "runas" });
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "The update needs administrator permission, which wasn't given.";
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return "The update wouldn't start: " + ex.Message;
        }
    }

    /// <summary>The sentence on the banner for each state, written for someone who is mid-game.</summary>
    public static string Describe(UpdateDto update) => update.State switch
    {
        UpdateState.Downloading => $"Getting NetRoute {update.Version}… {update.Fraction:P0}",
        UpdateState.Ready => $"NetRoute {update.Version} is ready to install. Your apps and settings are kept, and protection is off for a few seconds.",
        UpdateState.Failed => update.Problem ?? "The update didn't work out.",
        _ => $"NetRoute {update.Version} is available. {update.Notes}".TrimEnd()
    };

    /// <summary>What the button does next.</summary>
    public static string ButtonText(UpdateDto update) => update.State switch
    {
        UpdateState.Downloading => "Getting it…",
        UpdateState.Ready => "Install now",
        UpdateState.Failed => "Open GitHub",
        _ => update.Sha256 is { Length: 64 } ? "Download" : "Get it"
    };

    /// <summary>
    /// True when the honest answer is a web page rather than a progress bar: either the release
    /// published no digest to check a download against, or fetching it has already gone wrong.
    /// </summary>
    public static bool SendToPage(UpdateDto update)
        => update.State == UpdateState.Failed || update.Sha256 is not { Length: 64 };

    /// <summary>The release's own page on GitHub, where the installer and the notes are.</summary>
    public static string Page(UpdateDto update) => Updates.ReleaseFor(update.Version);
}

/// <summary>
/// The Updates panel: where NetRoute looks, whether it looks by itself, and what it found.
///
/// <para>Nothing is configured out of the box, so this is also where a build gets told where its
/// updates live. An address that is not https is refused here rather than at the point of
/// downloading, where it would be too late to explain why.</para>
/// </summary>
public partial class UpdatesViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty] private string _currentVersion = "";
    [ObservableProperty] private string _feedUrl = "";
    [ObservableProperty] private bool _automatic = true;
    [ObservableProperty] private string _checkedAtText = "";
    [ObservableProperty] private string? _found;
    [ObservableProperty] private string? _notes;
    [ObservableProperty] private string _actionText = "Check now";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private string? _problem;

    private UpdateDto? _update;

    public async Task LoadAsync()
    {
        try
        {
            Show(await main.Client.GetUpdateSettingsAsync());
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Problem = "The NetRoute service isn't answering, so its update settings can't be read.";
        }
    }

    private void Show(UpdateSettingsDto settings)
    {
        CurrentVersion = "You have NetRoute " + settings.CurrentVersion + ".";
        FeedUrl = settings.FeedUrl ?? "";
        Automatic = settings.Automatic;
        CheckedAtText = settings.CheckedAt is { } at
            ? "Last looked " + Format.Ago(at) + "."
            : settings.FeedUrl is null
                ? "The update address is empty, so NetRoute never calls anywhere."
                : "It hasn't looked yet.";
        Apply(settings.Available);
    }

    private void Apply(UpdateDto? update)
    {
        _update = update;
        Problem = update?.State == UpdateState.Failed ? update.Problem : null;
        Found = update is null ? null : Upgrade.Describe(update);
        Notes = update?.Notes;
        ActionText = update is null ? "Check now" : Upgrade.ButtonText(update);
        ShowProgress = update?.State == UpdateState.Downloading;
        Fraction = update?.Fraction ?? 0;
    }

    /// <summary>Saves the address and the automatic setting, then looks straight away.</summary>
    [RelayCommand]
    private async Task Save()
    {
        var url = FeedUrl.Trim();
        if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
        {
            Problem = "That isn't an https address. NetRoute only fetches updates over https.";
            return;
        }
        Problem = null;
        await Run(async () =>
        {
            await main.Client.SetUpdateSettingsAsync(url.Length == 0 ? null : url, Automatic);
            Show(await main.Client.GetUpdateSettingsAsync());
        });
    }

    /// <summary>Check, download or install, depending on where this update has got to.</summary>
    [RelayCommand]
    private async Task Act()
    {
        if (_update is { State: UpdateState.Ready })
        {
            var problem = await Upgrade.StartAsync(_update);
            Problem = problem;
            return;
        }

        if (_update is { } stuck && Upgrade.SendToPage(stuck))
        {
            main.OpenPage(Upgrade.Page(stuck));
            return;
        }

        await Run(async () =>
        {
            var found = _update is { State: UpdateState.Available } && FeedUrl.Trim().Length > 0
                ? await main.Client.DownloadUpdateAsync()
                : await main.Client.CheckForUpdateAsync();
            Apply(found);
            CheckedAtText = "Last looked just now.";
            if (found is null)
            {
                Found = "You are on the newest version.";
            }
        });

        // A download runs in the service; follow it until it lands.
        while (_update is { State: UpdateState.Downloading })
        {
            await Task.Delay(700);
            try
            {
                Apply((await main.Client.GetUpdateSettingsAsync()).Available);
            }
            catch (Exception ex)
            {
                App.Log(ex);
                break;
            }
        }
    }

    private async Task Run(Func<Task> work)
    {
        if (Busy)
        {
            return;
        }
        Busy = true;
        try
        {
            await work();
        }
        catch (ServiceUnavailableException)
        {
            Problem = "The NetRoute service isn't answering.";
        }
        catch (NetRouteServiceException ex)
        {
            Problem = ex.Error.FriendlyMessage;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Problem = "That didn't work: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
