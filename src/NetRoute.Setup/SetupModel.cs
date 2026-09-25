using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Windows;

namespace NetRoute.Setup;

/// <summary>One of the things setup can do about what is already here, as offered on the first page.</summary>
public sealed class ActionChoice : Observable
{
    private readonly Action<SetupAction> _choose;
    private bool _isSelected;

    internal ActionChoice(SetupAction action, string title, string detail, Action<SetupAction> choose)
    {
        Action = action;
        Title = title;
        Detail = detail;
        _choose = choose;
    }

    internal SetupAction Action { get; }
    public string Title { get; }
    public string Detail { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value) && value)
            {
                _choose(Action);
            }
        }
    }
}

/// <summary>The setup window's state: which page is showing, the options, and the progress list.</summary>
public sealed class SetupModel : Observable
{
    private readonly Existing _existing;
    private SetupEngine _engine;
    private SetupAction _action;

    private string _page = "Welcome";
    private string _heading;
    private string _subheading;
    private string _intro;
    private string _footer;
    private string _doneMessage;
    private string _mullvadNote;
    private string _logPath;
    private string _error;
    private bool _setUpDriver = true;
    private bool _startWithWindows;
    private bool _desktopShortcut;
    private bool _launchWhenDone = true;
    private bool _removeSettings;
    private bool _succeeded;
    private bool _failed;
    private double _progress;

    public SetupModel(SetupAction action)
    {
        _existing = Existing.Find();
        _action = _existing == null
            ? (action == SetupAction.Remove ? SetupAction.Remove : SetupAction.Install)
            : action == SetupAction.Install ? _existing.Suggested() : action;

        if (_existing != null && Payload.Present)
        {
            // Both ways out of an existing install, on the page rather than buried in Settings > Apps.
            var replace = _existing.Suggested();
            Choices.Add(new ActionChoice(replace,
                replace == SetupAction.Repair ? "Repair version " + Machine.Version : Verb(replace) + " to " + Machine.Version,
                replace == SetupAction.Update
                    ? "Your apps and settings are kept. Protection pauses for a few seconds while the files are swapped, then comes back on by itself."
                    : replace == SetupAction.Repair
                        ? "Puts every file back, and registers the service and the driver again. Nothing you have set up is touched."
                        : "Puts the older version back in place of " + _existing.Version
                          + ". Your apps and settings are kept, but anything only the newer one understood is dropped.",
                Choose));
            Choices.Add(new ActionChoice(SetupAction.Remove, "Remove NetRoute",
                "Stops NetRoute, puts Windows' network settings back the way they were, and removes the program.", Choose));
            var current = Choices.FirstOrDefault(c => c.Action == _action) ?? Choices[0];
            _action = current.Action;
            current.IsSelected = true;
        }

        BuildSteps();
        DriverAlreadyRunning = Machine.Status(Machine.DriverService) == ServiceControllerStatus.Running;
        if (Machine.MullvadDaemons().Any(n => Machine.StartMode(n) != ServiceStartMode.Disabled))
        {
            MullvadNote = "Mullvad VPN is installed. Its background service will be turned off, because only one program " +
                          "can use the driver at a time. Removing NetRoute turns it back on.";
        }
        StartWithWindows = _existing == null || File.Exists(Machine.StartupLink) || File.Exists(Machine.OldStartupLink);
        DesktopShortcut = File.Exists(Machine.DesktopLink);

        PrimaryCommand = new Command(OnPrimary);
        SecondaryCommand = new Command(OnSecondary);
        ShowWelcome();
    }

    public ObservableCollection<StepItem> Steps { get; } = new ObservableCollection<StepItem>();
    public ObservableCollection<string> Warnings { get; } = new ObservableCollection<string>();
    public ObservableCollection<ActionChoice> Choices { get; } = new ObservableCollection<ActionChoice>();
    public Command PrimaryCommand { get; }
    public Command SecondaryCommand { get; }

    public bool IsUninstall => _action == SetupAction.Remove;
    public bool IsInstall => !IsUninstall;
    public bool HasChoices => Choices.Count > 0;
    public bool IsFirstInstall => _existing == null;
    public string DriverFootnote => "Installs Mullvad's Microsoft-signed split-tunnel driver (98 KB). No VPN app and no account.";
    public bool DriverAlreadyRunning { get; }
    public string WindowTitle => IsUninstall ? "Remove NetRoute" : "NetRoute Setup";

    /// <summary>The older version is about to be put back: worth saying out loud before it happens.</summary>
    public string DowngradeNote => _action == SetupAction.Downgrade && Page == "Welcome"
        ? "You have " + _existing.Version + ", which is newer than this installer's " + Machine.Version + "."
        : null;

    private void Choose(SetupAction action)
    {
        if (_action == action)
        {
            return;
        }
        _action = action;
        BuildSteps();
        ShowWelcome();
        Raise(nameof(IsUninstall));
        Raise(nameof(IsInstall));
        Raise(nameof(WindowTitle));
        Raise(nameof(DowngradeNote));
        Raise(nameof(PrimaryText));
    }

    private void BuildSteps()
    {
        Steps.Clear();
        foreach (var step in IsUninstall ? SetupEngine.UninstallSteps() : SetupEngine.InstallSteps())
        {
            Steps.Add(step);
        }
    }

    public string Page
    {
        get => _page;
        set
        {
            if (Set(ref _page, value))
            {
                Raise(nameof(PrimaryText));
                Raise(nameof(ShowPrimary));
                Raise(nameof(SecondaryText));
                Raise(nameof(ShowSecondary));
                Raise(nameof(CanLaunch));
                Raise(nameof(DowngradeNote));
            }
        }
    }

    public string Heading { get => _heading; set => Set(ref _heading, value); }
    public string Subheading { get => _subheading; set => Set(ref _subheading, value); }
    public string Intro { get => _intro; set => Set(ref _intro, value); }
    public string Footer { get => _footer; set => Set(ref _footer, value); }
    public string DoneMessage { get => _doneMessage; set => Set(ref _doneMessage, value); }
    public string MullvadNote { get => _mullvadNote; set => Set(ref _mullvadNote, value); }
    public string LogPath { get => _logPath; set => Set(ref _logPath, value); }
    public string Error { get => _error; set => Set(ref _error, value); }
    public bool SetUpDriver { get => _setUpDriver; set => Set(ref _setUpDriver, value); }
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public bool DesktopShortcut { get => _desktopShortcut; set => Set(ref _desktopShortcut, value); }
    public bool LaunchWhenDone { get => _launchWhenDone; set => Set(ref _launchWhenDone, value); }
    public bool RemoveSettings { get => _removeSettings; set => Set(ref _removeSettings, value); }
    public bool Succeeded { get => _succeeded; private set => Set(ref _succeeded, value); }
    public bool Failed { get => _failed; private set => Set(ref _failed, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    public string PrimaryText => Page == "Welcome" ? Verb(_action) : Page == "Failed" ? "Close" : "Finish";
    public bool ShowPrimary => Page != "Progress";
    public string SecondaryText => Page == "Welcome" ? "Cancel" : "Open log";
    public bool ShowSecondary => Page == "Welcome" || Page == "Failed";
    public bool CanLaunch => !IsUninstall && Page == "Done";

    public SetupOptions CurrentOptions() => new SetupOptions
    {
        SetUpDriver = SetUpDriver || DriverAlreadyRunning,
        StartWithWindows = StartWithWindows,
        DesktopShortcut = DesktopShortcut,
        RemoveSettings = RemoveSettings
    };

    /// <summary>Runs setup to the end. Used by the window (on a worker thread) and by /quiet.</summary>
    public bool Execute(SetupOptions options)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        LogPath = IsUninstall
            ? Path.Combine(Path.GetTempPath(), "NetRoute-uninstall-" + stamp + ".log")
            : Path.Combine(Machine.DataDir, "logs", "setup-" + stamp + ".log");
        var log = new Log(LogPath);
        log.Write($"NetRoute setup {Machine.Version}: {Verb(_action).ToLowerInvariant()} on {Environment.OSVersion}");
        if (_existing != null)
        {
            log.Write("Already here: " + _existing.Describe());
        }
        _engine = new SetupEngine(log);
        try
        {
            if (IsUninstall)
            {
                _engine.Uninstall(options, Steps, p => Progress = p);
            }
            else
            {
                _engine.Install(options, Steps, p => Progress = p);
            }
            Succeeded = true;
        }
        catch (Exception ex)
        {
            log.Write("Setup failed: " + ex);
            Error = ex is SetupException ? ex.Message : "Something went wrong: " + ex.Message;
            Failed = true;
        }
        return Succeeded;
    }

    private void ShowWelcome()
    {
        Heading = Verb(_action) + " NetRoute";
        Subheading = IsUninstall
            ? "Version " + (_existing != null && _existing.Version != null ? _existing.Version : Machine.Version)
            : "Version " + Machine.Version
              + (_existing == null ? ""
                  : _existing.Version == null ? "  ·  replaces the build from INSTALL-NETROUTE.cmd"
                  : _existing.Version == Machine.Version ? "  ·  already installed"
                  : "  ·  replaces " + _existing.Version);
        Intro = IsUninstall
            ? "This stops NetRoute, puts Windows' network settings back the way they were, and removes the program."
            : _existing == null
                ? "Keeps your games on one internet connection and your downloads on the other."
                : _existing.Describe();
        Footer = IsUninstall
            ? "If NetRoute installed Mullvad's split-tunnel driver, that's removed too. Mullvad VPN itself is left alone."
            : "Installs to " + Machine.InstallDir + "  ·  Includes Mullvad's split-tunnel driver (MPL-2.0)";
    }

    private async void Start()
    {
        Page = "Progress";
        Heading = Progressive() + " NetRoute…";
        Footer = "Please wait. This takes under a minute.";
        var options = CurrentOptions();
        await Task.Run(() => Execute(options));
        ShowResult();
    }

    private void ShowResult()
    {
        foreach (var warning in _engine?.Warnings ?? Enumerable.Empty<string>())
        {
            Warnings.Add(warning);
        }
        var restart = _engine?.RestartNeeded == true;
        if (Succeeded)
        {
            Heading = IsUninstall ? "NetRoute is removed" : "NetRoute is " + Past();
            DoneMessage = IsUninstall
                ? "Normal Windows networking is restored." + (restart ? " Restart your PC to finish removing the split-tunnel driver." : "")
                : _action == SetupAction.Install
                    ? "Open NetRoute and choose which connection is for games and which is for downloads. It stays in the tray from now on."
                    : "Your apps and settings were kept, and protection is back on." + (restart ? " Restart your PC to finish the update." : "");
            Page = "Done";
        }
        else
        {
            Heading = "Setup couldn't finish";
            DoneMessage = Error + (_engine?.RolledBack == true ? " Your previous version was put back and is running again." : "");
            Page = "Failed";
        }
        Footer = LogPath == null ? null : "Log: " + LogPath;
    }

    private static string Verb(SetupAction action)
    {
        switch (action)
        {
            case SetupAction.Remove: return "Remove";
            case SetupAction.Repair: return "Repair";
            case SetupAction.Update: return "Update";
            case SetupAction.Downgrade: return "Go back";
            default: return "Install";
        }
    }

    private string Progressive()
    {
        switch (_action)
        {
            case SetupAction.Remove: return "Removing";
            case SetupAction.Repair: return "Repairing";
            case SetupAction.Update: return "Updating";
            case SetupAction.Downgrade: return "Going back to " + Machine.Version + " of";
            default: return "Installing";
        }
    }

    private string Past()
    {
        switch (_action)
        {
            case SetupAction.Repair: return "repaired";
            case SetupAction.Update: return "updated";
            case SetupAction.Downgrade: return "back on " + Machine.Version;
            default: return "installed";
        }
    }

    private void OnPrimary()
    {
        if (Page == "Welcome")
        {
            Start();
            return;
        }
        if (Succeeded && !IsUninstall && LaunchWhenDone)
        {
            try
            {
                // Through Explorer, so the app runs as the signed-in user rather than elevated like setup.
                Process.Start("explorer.exe", "\"" + Machine.AppExe + "\"");
            }
            catch (Exception)
            {
                // It's in the Start menu either way.
            }
        }
        Application.Current.MainWindow?.Close();
    }

    private void OnSecondary()
    {
        if (Page == "Welcome")
        {
            Application.Current.MainWindow?.Close();
            return;
        }
        if (LogPath != null && File.Exists(LogPath))
        {
            Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true });
        }
    }

    /// <summary>Fills a page with sample state, for /screenshot. Changes nothing on the PC.</summary>
    public void Preview(string page)
    {
        switch (page)
        {
            case "welcome":
                // What the first page looks like when NetRoute is already on the PC.
                Choices.Clear();
                Choices.Add(new ActionChoice(SetupAction.Update, "Update to " + Machine.Version,
                    "Your apps and settings are kept. Protection pauses for a few seconds while the files are swapped, then comes back on by itself.",
                    Choose));
                Choices.Add(new ActionChoice(SetupAction.Remove, "Remove NetRoute",
                    "Stops NetRoute, puts Windows' network settings back the way they were, and removes the program.", Choose));
                Choices[0].IsSelected = true;
                Raise(nameof(HasChoices));
                Subheading = "Version " + Machine.Version + "  ·  replaces 1.0.0";
                Intro = "NetRoute 1.0.0 is already installed, from 14 September 2026. It is running now.";
                break;
            case "progress":
                Page = "Progress";
                Heading = Progressive() + " NetRoute…";
                Footer = "Please wait. This takes under a minute.";
                for (var i = 0; i < Steps.Count; i++)
                {
                    Steps[i].State = i < 3 ? StepState.Done : i == 3 ? StepState.Running : StepState.Pending;
                }
                Steps[0].Detail = "Version " + Machine.Version + " is ready to install.";
                Steps[1].Detail = "Stopped the service. Windows' network settings are back to normal until it restarts.";
                Steps[2].Detail = "Installed to " + Machine.InstallDir + ".";
                Progress = 0.62;
                break;
            case "done":
            case "failed":
                var ok = page == "done";
                for (var i = 0; i < Steps.Count; i++)
                {
                    Steps[i].State = ok || i < 4 ? StepState.Done : StepState.Failed;
                }
                Succeeded = ok;
                Failed = !ok;
                Error = "The NetRoute service didn't start. The reason is in Event Viewer > Windows Logs > Application (source NetRoute.Service).";
                LogPath = Path.Combine(Machine.DataDir, "logs", "setup-preview.log");
                ShowResult();
                break;
        }
    }
}
