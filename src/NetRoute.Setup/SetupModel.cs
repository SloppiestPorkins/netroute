using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Windows;

namespace NetRoute.Setup;

/// <summary>The setup window's state: which page is showing, the options, and the progress list.</summary>
public sealed class SetupModel : Observable
{
    private readonly bool _uninstall;
    private readonly string _existingVersion;
    private readonly bool _hasFiles;
    private readonly string _verb;
    private SetupEngine _engine;

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

    public SetupModel(bool uninstall)
    {
        _uninstall = uninstall;
        _existingVersion = Machine.InstalledVersion();
        _hasFiles = File.Exists(Machine.ServiceExe);
        _verb = uninstall ? "Remove"
            : _existingVersion == null ? (_hasFiles ? "Update" : "Install")
            : _existingVersion == Machine.Version ? "Repair" : "Update";

        foreach (var step in uninstall ? SetupEngine.UninstallSteps() : SetupEngine.InstallSteps())
        {
            Steps.Add(step);
        }

        DriverAlreadyRunning = Machine.Status(Machine.DriverService) == ServiceControllerStatus.Running;
        if (Machine.MullvadDaemons().Any(n => Machine.StartMode(n) != ServiceStartMode.Disabled))
        {
            MullvadNote = "Mullvad VPN is installed. Its background service will be turned off, because only one program " +
                          "can use the driver at a time. Removing NetRoute turns it back on.";
        }
        StartWithWindows = !_hasFiles || File.Exists(Machine.StartupLink) || File.Exists(Machine.OldStartupLink);
        DesktopShortcut = File.Exists(Machine.DesktopLink);

        PrimaryCommand = new Command(OnPrimary);
        SecondaryCommand = new Command(OnSecondary);
        ShowWelcome();
    }

    public ObservableCollection<StepItem> Steps { get; } = new ObservableCollection<StepItem>();
    public ObservableCollection<string> Warnings { get; } = new ObservableCollection<string>();
    public Command PrimaryCommand { get; }
    public Command SecondaryCommand { get; }

    public bool IsUninstall => _uninstall;
    public bool IsInstall => !_uninstall;
    public bool DriverAlreadyRunning { get; }
    public string WindowTitle => _uninstall ? "Remove NetRoute" : "NetRoute Setup";

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

    public string PrimaryText => Page == "Welcome" ? _verb : Page == "Failed" ? "Close" : "Finish";
    public bool ShowPrimary => Page != "Progress";
    public string SecondaryText => Page == "Welcome" ? "Cancel" : "Open log";
    public bool ShowSecondary => Page == "Welcome" || Page == "Failed";
    public bool CanLaunch => !_uninstall && Page == "Done";

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
        LogPath = _uninstall
            ? Path.Combine(Path.GetTempPath(), $"NetRoute-uninstall-{stamp}.log")
            : Path.Combine(Machine.DataDir, "logs", $"setup-{stamp}.log");
        var log = new Log(LogPath);
        log.Write($"NetRoute setup {Machine.Version}: {_verb.ToLowerInvariant()} on {Environment.OSVersion}");
        _engine = new SetupEngine(log);
        try
        {
            if (_uninstall)
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
        Heading = _verb + " NetRoute";
        Subheading = _uninstall
            ? "Version " + (_existingVersion ?? Machine.Version)
            : "Version " + Machine.Version + (_existingVersion != null && _existingVersion != Machine.Version ? "  ·  replaces " + _existingVersion
                : _existingVersion == null && _hasFiles ? "  ·  replaces the build from INSTALL-NETROUTE.cmd" : "");
        Intro = _uninstall
            ? "This stops NetRoute, puts Windows' network settings back the way they were, and removes the program."
            : _verb == "Install"
                ? "Keeps your games on one internet connection and your downloads on the other."
                : "Your apps and settings are kept. Protection pauses for a few seconds while NetRoute updates.";
        Footer = _uninstall
            ? "If NetRoute installed Mullvad's split-tunnel driver, that's removed too. Mullvad VPN itself is left alone."
            : $"Installs to {Machine.InstallDir}  ·  Includes Mullvad's split-tunnel driver (MPL-2.0)";
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
            Heading = _uninstall ? "NetRoute is removed" : "NetRoute is " + Past();
            DoneMessage = _uninstall
                ? "Normal Windows networking is restored." + (restart ? " Restart your PC to finish removing the split-tunnel driver." : "")
                : _verb == "Install"
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

    private string Progressive() => _verb == "Remove" ? "Removing" : _verb == "Repair" ? "Repairing" : _verb == "Update" ? "Updating" : "Installing";

    private string Past() => _verb == "Repair" ? "repaired" : _verb == "Update" ? "updated" : "installed";

    private void OnPrimary()
    {
        if (Page == "Welcome")
        {
            Start();
            return;
        }
        if (Succeeded && !_uninstall && LaunchWhenDone)
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
            case "progress":
                Page = "Progress";
                Heading = Progressive() + " NetRoute…";
                Footer = "Please wait. This takes under a minute.";
                for (var i = 0; i < Steps.Count; i++)
                {
                    Steps[i].State = i < 3 ? StepState.Done : i == 3 ? StepState.Running : StepState.Pending;
                }
                Steps[0].Detail = $"Version {Machine.Version} is ready to install.";
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
