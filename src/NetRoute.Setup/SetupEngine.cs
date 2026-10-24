using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Threading;

namespace NetRoute.Setup;

/// <summary>
/// Install, update, repair and uninstall.
///
/// <para>The same steps scripts\install-netroute.ps1 and uninstall-netroute.ps1 took, plus the
/// driver, which used to be a separate script and a separate Mullvad VPN install.</para>
///
/// <para>Two rules run through all of it. An update never leaves the PC unprotected: the old
/// version is backed up, and if anything fails it is put back and started again. And the
/// split-tunnel driver is never stopped: unloading it while it holds state crashes Windows on
/// purpose, so uninstall only unregisters it and lets the next restart drop it.</para>
/// </summary>
internal sealed class SetupEngine
{
    private readonly Log _log;

    public SetupEngine(Log log) => _log = log;

    public List<string> Warnings { get; } = new List<string>();
    public bool RestartNeeded { get; private set; }
    public bool RolledBack { get; private set; }

    private RestoreOutcome _restore = RestoreOutcome.NothingToRestore;
    private string _deferredSelfDelete;

    /// <summary>
    /// True when a removal finished but Windows' network settings could not be confirmed as put
    /// back. The run is not a success: the person is left with NetRoute's metrics in place and no
    /// NetRoute to undo them, so this has to reach both the window and the exit code.
    /// </summary>
    public bool RestoreIncomplete => _restore == RestoreOutcome.Failed;

    /// <summary>How to put the settings back by hand, for when <see cref="RestoreIncomplete"/>.</summary>
    public string RestoreAdvice => RestorePolicy.ManualInstructions(Machine.RouteStatePath);

    /// <summary>What the rollback managed to undo, in the person's words, or null if none ran.</summary>
    public string RollbackSummary { get; private set; }

    public static List<StepItem> InstallSteps() => new List<StepItem>
    {
        new StepItem("Unpacking NetRoute"),
        new StepItem("Closing the running NetRoute"),
        new StepItem("Installing files"),
        new StepItem("Split-tunnel driver"),
        new StepItem("Starting the NetRoute service"),
        new StepItem("Shortcuts and command line"),
        new StepItem("Registering with Windows")
    };

    public static List<StepItem> UninstallSteps() => new List<StepItem>
    {
        new StepItem("Stopping NetRoute"),
        new StepItem("Restoring Windows' network settings"),
        new StepItem("Removing the service"),
        new StepItem("Split-tunnel driver"),
        new StepItem("Removing files and shortcuts")
    };

    // ======================================================================= install

    public void Install(SetupOptions options, IList<StepItem> steps, Action<double> progress)
    {
        var dir = Machine.InstallDir;
        var staging = dir + ".new";
        var backup = dir + ".old";
        // What this run did, which is all a rollback needs to know. A fresh install that fails has
        // to leave a clean machine; an update that fails has to leave the old version working.
        var facts = new RollbackFacts
        {
            HadInstall = File.Exists(Machine.ServiceExe),
            ServiceCreated = Machine.Status(Machine.ServiceName) == null,
            DriverCreated = Machine.Status(Machine.DriverService) == null
        };

        Do(steps[0], () =>
        {
            DeleteTree(staging);
            Payload.Extract(staging, p => progress(0.02 + p * 0.43));
            return $"Version {Machine.Version} is ready to install.";
        });

        try
        {
            Do(steps[1], () =>
            {
                var closed = CloseApp();
                facts.ServiceWasRunning = StopService(Machine.ServiceName);
                progress(0.5);
                if (!facts.ServiceWasRunning && closed == 0)
                {
                    steps[1].State = StepState.Skipped;
                    return "Nothing was running.";
                }
                return facts.ServiceWasRunning
                    ? "Stopped the service. Windows' network settings are back to normal until it restarts."
                    : "Closed the NetRoute app.";
            });

            Do(steps[2], () =>
            {
                DeleteTree(backup);
                if (Directory.Exists(dir))
                {
                    CopyTree(dir, backup);
                    facts.BackupExists = true;
                }
                facts.FilesChanged = true;
                CopyTree(staging, dir);
                RemoveExtraFiles(dir, staging);
                progress(0.72);
                return "Installed to " + dir + ".";
            });

            Do(steps[3], () => SetUpDriver(options, steps[3]));
            progress(0.82);

            Do(steps[4], () =>
            {
                RegisterService();
                StartService(Machine.ServiceName);
                progress(0.9);
                return "Running. It starts with Windows and restarts itself if it ever stops.";
            });
        }
        catch (Exception)
        {
            facts.DaemonsTurnedOff = Machine.ReadStates("Service.").Any();
            RollBack(facts, dir, backup);
            DeleteTree(staging);
            throw;
        }

        Do(steps[5], () =>
        {
            Machine.CreateShortcut(Machine.StartMenuLink, Machine.AppExe, null);
            Machine.DeleteFile(Machine.OldStartupLink);
            if (options.StartWithWindows)
            {
                Machine.CreateShortcut(Machine.StartupLink, Machine.AppExe, "--minimized");
            }
            else
            {
                Machine.DeleteFile(Machine.StartupLink);
            }
            if (options.DesktopShortcut)
            {
                Machine.CreateShortcut(Machine.DesktopLink, Machine.AppExe, null);
            }
            else
            {
                Machine.DeleteFile(Machine.DesktopLink);
            }
            Machine.AddToPath(dir);
            progress(0.95);
            return "Start menu" + (options.StartWithWindows ? ", starts when you sign in" : "") + (options.DesktopShortcut ? ", desktop" : "")
                   + ". Type netroute in a new terminal.";
        });

        Do(steps[6], () =>
        {
            Register();
            DeleteTree(staging);
            DeleteTree(backup);
            progress(1);
            return "NetRoute is listed in Settings > Apps, where you can remove it.";
        });
    }

    /// <summary>
    /// Undoes what this run managed to do: the previous version put back and restarted after a
    /// failed update, or a clean machine after a failed fresh install.
    ///
    /// <para>What to undo is decided by <see cref="RollbackPlan"/> from facts alone, so the rule
    /// can be checked without a machine to damage. Each step is attempted on its own, because a
    /// step that cannot be done must not stop the ones after it -- the point is to get as close to
    /// where we started as possible, and then say how close that was.</para>
    /// </summary>
    private void RollBack(RollbackFacts facts, string dir, string backup)
    {
        _log.Write("== Rolling back");
        var done = new List<string>();
        var failed = new List<string>();

        foreach (var step in RollbackPlan.For(facts))
        {
            try
            {
                var said = Undo(step, dir, backup);
                if (said != null)
                {
                    done.Add(said);
                }
            }
            catch (Exception ex)
            {
                _log.Write("   Rollback problem at " + step + ": " + ex);
                failed.Add(Describe(step));
            }
        }

        RollbackSummary = Summarise(facts.HadInstall, done, failed);
        _log.Write("   " + RollbackSummary);
    }

    private string Undo(RollbackStep step, string dir, string backup)
    {
        switch (step)
        {
            case RollbackStep.StopNewService:
                // A new version that started and then failed is holding its own files open.
                try
                {
                    StopService(Machine.ServiceName);
                }
                catch (SetupException ex)
                {
                    _log.Write("   " + ex.Message);
                }
                return null;

            case RollbackStep.RestoreOldFiles:
                if (!Directory.Exists(backup))
                {
                    return null;
                }
                CopyTree(backup, dir);
                RemoveExtraFiles(dir, backup);
                DeleteTree(backup);
                RolledBack = true;
                return "put the previous version back";

            case RollbackStep.DeleteNewFiles:
                // Nothing was here before this run, so leaving half an install behind would be
                // worse than leaving nothing: the next attempt would see an install to update.
                DeleteInstallFolder(dir);
                Machine.RemoveFromPath(dir);
                Machine.DeleteFile(Machine.StartMenuLink);
                Machine.DeleteFile(Machine.StartupLink);
                Machine.DeleteFile(Machine.DesktopLink);
                return "removed the files it had copied";

            case RollbackStep.DeleteServiceWeCreated:
                if (Machine.Status(Machine.ServiceName) != null)
                {
                    Sc($"delete {Machine.ServiceName}");
                }
                return "unregistered the service it had created";

            case RollbackStep.RemoveDriverWeCreated:
                // Never stopped, only unregistered: unloading it while it holds state is a
                // deliberate bugcheck. See the notes on this class.
                if (Machine.Status(Machine.DriverService) != null)
                {
                    Sc($"delete {Machine.DriverService}");
                    RestartNeeded = true;
                }
                Machine.DeleteState("DriverCreated");
                Machine.DeleteState("DriverStart");
                return "unregistered the split-tunnel driver it had registered";

            case RollbackStep.ReenableMullvadDaemons:
                foreach (var entry in Machine.ReadStates("Service."))
                {
                    var name = entry.Key.Substring("Service.".Length);
                    if (!string.IsNullOrEmpty(entry.Value))
                    {
                        Machine.Run(Machine.Sc, $"config {name} start= {StartFlag(entry.Value)}", _log, out _, 30000);
                    }
                    Machine.DeleteState(entry.Key);
                }
                return "turned Mullvad VPN's services back on";

            case RollbackStep.ForgetSetupState:
                using (var hklm = Machine.Hklm())
                {
                    hklm.DeleteSubKeyTree(@"SOFTWARE\NetRoute", false);
                    hklm.DeleteSubKeyTree(Machine.UninstallKeyPath, false);
                }
                return null;

            case RollbackStep.RestoreRecoveryActions:
                // StopService strips these so a restarting service cannot hold its files open
                // mid-copy. Normally RegisterService puts them back at the end of the install --
                // which never happened, so the old service would never restart itself again.
                if (Machine.Status(Machine.ServiceName) != null)
                {
                    Sc($"failure {Machine.ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000");
                }
                return "restored the service's restart settings";

            case RollbackStep.RestartOldService:
                StartService(Machine.ServiceName);
                return "started it again";

            default:
                return null;
        }
    }

    private static string StartFlag(string startMode)
        => string.Equals(startMode, "Automatic", StringComparison.OrdinalIgnoreCase) ? "auto"
            : string.Equals(startMode, "Disabled", StringComparison.OrdinalIgnoreCase) ? "disabled"
            : "demand";

    private static string Describe(RollbackStep step) => step switch
    {
        RollbackStep.RestoreOldFiles => "putting the previous version back",
        RollbackStep.DeleteNewFiles => "removing the files it had copied",
        RollbackStep.DeleteServiceWeCreated => "unregistering the service",
        RollbackStep.RemoveDriverWeCreated => "unregistering the split-tunnel driver",
        RollbackStep.ReenableMullvadDaemons => "turning Mullvad VPN's services back on",
        RollbackStep.RestoreRecoveryActions => "restoring the service's restart settings",
        RollbackStep.RestartOldService => "starting the previous version again",
        _ => step.ToString()
    };

    /// <summary>
    /// Says what state the machine was left in. Never claims more than was done: a rollback that
    /// could not finish is the case where someone has to be told, not reassured.
    /// </summary>
    private static string Summarise(bool hadInstall, List<string> done, List<string> failed)
    {
        var where = hadInstall ? "Your previous version was left in place." : "Nothing was left installed.";
        if (failed.Count > 0)
        {
            return "Undoing the attempt did not finish: " + string.Join(", ", failed)
                   + " could not be done. Run setup again, and choose Repair if NetRoute is still listed.";
        }
        return done.Count == 0 ? where : "Undone: " + string.Join(", ", done) + ". " + where;
    }

    private string SetUpDriver(SetupOptions options, StepItem step)
    {
        if (!options.SetUpDriver)
        {
            step.State = StepState.Skipped;
            return "Skipped. NetRoute will keep apps off the wrong connection but can't move them onto the right one.";
        }

        try
        {
            // Mullvad VPN's own service would hold the driver's only handle.
            var turnedOff = new List<string>();
            foreach (var daemon in Machine.MullvadDaemons())
            {
                Machine.WriteState("Service." + daemon, Machine.StartMode(daemon)?.ToString(), firstTimeOnly: true);
                StopService(daemon);
                Sc($"config \"{daemon}\" start= disabled");
                turnedOff.Add(daemon);
            }

            if (Machine.Status(Machine.DriverService) == null)
            {
                VerifyDriver(Machine.DriverFile);
                Sc($"create {Machine.DriverService} type= kernel start= auto binPath= \"{Machine.DriverFile}\" DisplayName= \"Mullvad Split Tunnel (used by NetRoute)\"");
                Machine.WriteState("DriverCreated", "1", firstTimeOnly: false);
            }
            else
            {
                Machine.WriteState("DriverStart", Machine.StartMode(Machine.DriverService)?.ToString(), firstTimeOnly: true);
                var image = Machine.DriverImagePath();
                if (image == null || !File.Exists(image))
                {
                    // Registered by a Mullvad VPN that has since been uninstalled.
                    VerifyDriver(Machine.DriverFile);
                    Sc($"config {Machine.DriverService} binPath= \"{Machine.DriverFile}\"");
                }
                // Mullvad registers the driver to start on demand and has its own service start it.
                // With that service off, nothing would load it after a restart.
                Sc($"config {Machine.DriverService} start= auto");
            }

            if (Machine.Status(Machine.DriverService) != ServiceControllerStatus.Running)
            {
                Sc($"start {Machine.DriverService}", allowFailure: true);
                for (var i = 0; i < 20 && Machine.Status(Machine.DriverService) != ServiceControllerStatus.Running; i++)
                {
                    Thread.Sleep(500);
                }
            }

            if (Machine.Status(Machine.DriverService) != ServiceControllerStatus.Running)
            {
                return Warn(step, "Windows didn't load the split-tunnel driver, so NetRoute can't move apps yet. " +
                                  "The reason is in Event Viewer > Windows Logs > System.");
            }
            return "Running, and starts with Windows" + (turnedOff.Count > 0 ? ". Mullvad VPN's background service is off." : ".");
        }
        catch (Exception ex)
        {
            _log.Write(ex.ToString());
            return Warn(step, (ex is SetupException ? ex.Message : "The split-tunnel driver couldn't be set up: " + ex.Message)
                              + " NetRoute still keeps apps off the wrong connection, but can't move them yet.");
        }
    }

    private void VerifyDriver(string path)
    {
        if (!File.Exists(path))
        {
            throw new SetupException("The split-tunnel driver is missing from this setup program. Download it again.");
        }
        Machine.Run(Machine.PowerShell,
            $"-NoProfile -NonInteractive -Command \"$s = Get-AuthenticodeSignature -LiteralPath '{path}'; '{{0}}|{{1}}' -f $s.Status, $s.SignerCertificate.Subject\"",
            _log, out var output);
        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains("|")) ?? "";
        if (!line.StartsWith("Valid|", StringComparison.Ordinal)
            || (line.IndexOf("Mullvad", StringComparison.OrdinalIgnoreCase) < 0 && line.IndexOf("Amagicom", StringComparison.OrdinalIgnoreCase) < 0))
        {
            throw new SetupException("The bundled split-tunnel driver isn't validly signed by Mullvad, so it wasn't installed.");
        }
    }

    private void RegisterService()
    {
        var binPath = $"binPath= \"\\\"{Machine.ServiceExe}\\\"\"";

        // Every filter NetRoute installs goes through the Base Filtering Engine, so starting
        // before it is up means enforcement is simply off until something restarts us. Depending
        // on it is how WFP consumers are meant to be registered. The trade is that stopping BFE
        // now stops NetRoute too, and the Service Control Manager does not treat that as a
        // failure, so the restart actions below will not bring it back -- but BFE is a core
        // always-on service, and dead-at-boot is the worse of the two failures. The service also
        // retries opening the engine on its own, so a late BFE no longer latches enforcement off.
        const string dependsOnBfe = "depend= BFE";

        if (Machine.Status(Machine.ServiceName) == null)
        {
            Sc($"create {Machine.ServiceName} {binPath} start= auto {dependsOnBfe} DisplayName= \"NetRoute\"");
            Sc($"description {Machine.ServiceName} \"Keeps your apps on the network you chose.\"");
        }
        else
        {
            // Named on an update too, so installs made before this existed pick it up.
            Sc($"config {Machine.ServiceName} {binPath} start= auto {dependsOnBfe}");
        }
        Sc($"failure {Machine.ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000");
    }

    private void Register()
    {
        using (var hklm = Machine.Hklm())
        using (var key = hklm.CreateSubKey(Machine.UninstallKeyPath))
        {
            key.SetValue("DisplayName", "NetRoute");
            key.SetValue("DisplayVersion", Machine.Version);
            key.SetValue("Publisher", "NetRoute");
            key.SetValue("InstallLocation", Machine.InstallDir);
            key.SetValue("DisplayIcon", Machine.AppExe + ",0");
            key.SetValue("UninstallString", $"\"{Machine.UninstallerExe}\" /uninstall");
            key.SetValue("QuietUninstallString", $"\"{Machine.UninstallerExe}\" /uninstall /quiet");
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(DirectorySize(Machine.InstallDir) / 1024), Microsoft.Win32.RegistryValueKind.DWord);
        }
    }

    // ===================================================================== uninstall

    public void Uninstall(SetupOptions options, IList<StepItem> steps, Action<double> progress)
    {
        var dir = Machine.InstallDir;

        Do(steps[0], () =>
        {
            CloseApp();
            var stopped = StopService(Machine.ServiceName);
            progress(0.2);
            return stopped ? "Stopped. Its shutdown turned app moving off." : "Nothing was running.";
        });

        Do(steps[1], () =>
        {
            progress(0.35);

            // Whether this worked decides whether the removal may go on at all: the next steps
            // delete the only program that could put these settings back. So the outcome is
            // judged on evidence -- the program existed, it was asked, it returned 0, and it
            // cleared the state it had just used -- rather than on it saying so (finding 6).
            var cliExists = File.Exists(Machine.CliExe);
            var stateBefore = File.Exists(Machine.RouteStatePath);
            int? code = null;
            string output = null;
            if (cliExists)
            {
                // Resets the driver and restores Windows' default connection even if the service
                // crashed instead of shutting down, and removes NetRoute's WFP sublayers.
                code = Machine.Run(Machine.CliExe, "cleanup-driver --remove-sublayers", _log, out output);
            }

            _restore = RestorePolicy.Decide(cliExists, stateBefore, code, File.Exists(Machine.RouteStatePath));

            if (_restore == RestoreOutcome.NothingToRestore)
            {
                steps[1].State = StepState.Skipped;
                return "Nothing to restore.";
            }
            if (_restore == RestoreOutcome.Restored)
            {
                return "Windows' default connection is back to how it was, and app moving is off.";
            }

            var why = code.HasValue && code.Value != 0 ? ": " + LastLine(output)
                : !cliExists ? ": NetRoute's command line is missing, so nothing could undo them"
                : ": NetRoute's record of the original settings is still there afterwards";

            if (!RestorePolicy.MayContinue(_restore, options.Force))
            {
                // Stop here, with everything still in place, rather than removing the means of
                // recovery. /force says to carry on regardless.
                throw new SetupException(
                    "Windows' network settings couldn't be put back" + why + ". Nothing has been removed, so you can try again. "
                    + RestorePolicy.ManualInstructions(Machine.RouteStatePath)
                    + " To remove NetRoute anyway, run the uninstaller with /force.");
            }
            return Warn(steps[1], "Some network settings couldn't be put back" + why + ". Removal is continuing because /force was given.");
        });

        Do(steps[2], () =>
        {
            progress(0.5);
            if (Machine.Status(Machine.ServiceName) == null)
            {
                steps[2].State = StepState.Skipped;
                return "Not installed.";
            }
            Sc($"delete {Machine.ServiceName}");
            return "Removed.";
        });

        Do(steps[3], () =>
        {
            progress(0.65);
            return RemoveDriver(steps[3]);
        });

        Do(steps[4], () =>
        {
            Machine.DeleteFile(Machine.StartMenuLink);
            Machine.DeleteFile(Machine.StartupLink);
            Machine.DeleteFile(Machine.OldStartupLink);
            Machine.DeleteFile(Machine.DesktopLink);
            Machine.RemoveFromPath(dir);
            DeleteInstallFolder(dir);
            using (var hklm = Machine.Hklm())
            {
                hklm.DeleteSubKeyTree(Machine.UninstallKeyPath, false);
                hklm.DeleteSubKeyTree(@"SOFTWARE\NetRoute", false);
            }
            // Never while the restore is unconfirmed: route-state.json lives here, and it is the
            // record of what the settings were before NetRoute touched them. Deleting it would
            // take away the only way back.
            var keepRecord = RestoreIncomplete;
            if (options.RemoveSettings && !keepRecord)
            {
                DeleteTree(Machine.DataDir);
                DeleteTree(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetRoute"));
            }
            progress(1);
            var settings = keepRecord
                ? "Removed, but your network settings were not confirmed as put back, so NetRoute's record of them has been kept in " + Machine.DataDir + "."
                : options.RemoveSettings ? "Removed, with your settings."
                : "Removed. Your settings are kept in " + Machine.DataDir + ".";
            return settings + (RestartNeeded ? " A restart finishes removing the driver file." : "");
        });
    }

    private string RemoveDriver(StepItem step)
    {
        try
        {
            var created = Machine.ReadState("DriverCreated") == "1";
            string result;
            if (Machine.Status(Machine.DriverService) == null)
            {
                result = "Not installed.";
            }
            else if (created)
            {
                // Never stopped (see the class notes). Deleting the service only marks it; Windows
                // drops it, and the driver, at the next restart.
                Sc($"config {Machine.DriverService} start= disabled", allowFailure: true);
                Sc($"delete {Machine.DriverService}", allowFailure: true);
                if (Machine.Status(Machine.DriverService) == ServiceControllerStatus.Running)
                {
                    RestartNeeded = true;
                }
                result = "Removed. It stays loaded, switched off, until you restart.";
            }
            else
            {
                if (Machine.ReadState("DriverStart") is string start && start.Length > 0)
                {
                    Sc($"config {Machine.DriverService} start= {ScStart(start)}", allowFailure: true);
                }
                result = "Left in place for Mullvad VPN.";
            }

            var restored = new List<string>();
            foreach (var entry in Machine.ReadStates("Service."))
            {
                if (Machine.Status(entry.Key) != null && entry.Value.Length > 0)
                {
                    Sc($"config \"{entry.Key}\" start= {ScStart(entry.Value)}", allowFailure: true);
                    restored.Add(entry.Key);
                }
            }
            return result + (restored.Count > 0 ? " Mullvad VPN's background service is turned back on." : "");
        }
        catch (Exception ex)
        {
            _log.Write(ex.ToString());
            return Warn(step, "The driver's settings couldn't all be put back: " + ex.Message);
        }
    }

    /// <summary>
    /// Starts the script that removes the uninstaller's own file once this process has gone. Called
    /// after the work is finished, from whichever path ends the process, and does nothing when
    /// there was no file left behind.
    /// </summary>
    public void FinishDeferredDeletes()
    {
        if (_deferredSelfDelete == null)
        {
            return;
        }
        try
        {
            var script = Path.Combine(Path.GetTempPath(), "NetRoute-Tidy-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".cmd");
            File.WriteAllText(script, SelfDelete.Script(_deferredSelfDelete, Path.GetDirectoryName(_deferredSelfDelete), script));
            using (Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c \"" + script + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
            }
        }
        catch (Exception ex)
        {
            // Nothing the person needs to act on: a stray uninstaller in Program Files is untidy,
            // not broken, and saying so at the end of a successful removal would only confuse.
            _log.Write("   Could not start the tidy-up script: " + ex.Message);
        }
        finally
        {
            _deferredSelfDelete = null;
        }
    }

    private void DeleteInstallFolder(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }
        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch (Exception)
            {
                // The one file we know will still be running is this uninstaller's original copy,
                // when a /quiet run is waiting on the relocated one for its exit code. Queueing
                // that for the next restart would make the run report "restart needed" for a file
                // nobody is waiting on, so it gets a script that tidies it up straight after.
                if (SelfDelete.IsLeftToWaitingParent(file, Machine.UninstallerExe))
                {
                    _deferredSelfDelete = file;
                    continue;
                }
                // Anything else in use is the loaded driver, most likely.
                if (Machine.DeleteAtRestart(file))
                {
                    RestartNeeded = true;
                }
            }
        }
        foreach (var folder in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Concat(new[] { dir }))
        {
            try
            {
                Directory.Delete(folder);
            }
            catch (Exception)
            {
                Machine.DeleteAtRestart(folder);
            }
        }
    }

    // ======================================================================= helpers

    private void Do(StepItem step, Func<string> action)
    {
        step.State = StepState.Running;
        _log.Write("== " + step.Title);
        try
        {
            var detail = action();
            if (step.State == StepState.Running)
            {
                step.State = StepState.Done;
            }
            step.Detail = detail;
            _log.Write("   " + detail);
        }
        catch (Exception ex)
        {
            step.State = StepState.Failed;
            step.Detail = ex is SetupException ? ex.Message : "Something went wrong: " + ex.Message;
            _log.Write("   FAILED: " + ex);
            throw;
        }
    }

    private string Warn(StepItem step, string message)
    {
        step.State = StepState.Warning;
        Warnings.Add(message);
        return message;
    }

    private void Sc(string arguments, bool allowFailure = false)
    {
        var code = Machine.Run(Machine.Sc, arguments, _log, out var output);
        if (code != 0 && !allowFailure)
        {
            throw new SetupException($"Windows refused a service change ({arguments.Split(' ')[0]}): {LastLine(output)}");
        }
    }

    private static string LastLine(string text)
        => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "no details";

    private static string ScStart(string mode)
    {
        switch (mode)
        {
            case "Automatic": return "auto";
            case "Disabled": return "disabled";
            case "System": return "system";
            case "Boot": return "boot";
            default: return "demand";
        }
    }

    private int CloseApp()
    {
        var closed = 0;
        foreach (var name in new[] { "NetRoute.App", "netroute" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                        closed++;
                    }
                    catch (Exception ex)
                    {
                        _log.Write($"Couldn't close {name} ({process.Id}): {ex.Message}");
                    }
                }
            }
        }
        return closed;
    }

    /// <summary>Stops a service if it's running. True if it was.</summary>
    /// <summary>
    /// Tries a file operation a few times before treating it as a lock.
    ///
    /// <para>Something briefly holding a file it is about to let go of — a virus scanner reading
    /// what was just written, a process on its way out — is common and passes in under a second.
    /// Queuing a replacement for the next restart is the right answer for a file that is genuinely
    /// in use, and the wrong one for a file that was busy for 200 milliseconds.</para>
    /// </summary>
    private static void Retry(Action work)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                work();
                return;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 8)
            {
                Thread.Sleep(250 * attempt);
            }
        }
    }

    private bool StopService(string name)
    {
        if (!(Machine.Status(name) is ServiceControllerStatus status) || status == ServiceControllerStatus.Stopped)
        {
            return false;
        }

        // Take the recovery actions off first. NetRoute is configured to restart itself five
        // seconds after it ever stops, which is right in normal life and wrong here: a service
        // that comes back while setup is copying holds its own files open, and the new version
        // ends up queued for the next restart instead of installed. RegisterService puts the
        // recovery actions back at the end of every install.
        if (name == Machine.ServiceName)
        {
            Machine.Run(Machine.Sc, $"failure {name} reset= 0 actions= \"\"", _log, out _, 30000);
        }

        using (var service = new ServiceController(name))
        {
            try
            {
                if (service.Status != ServiceControllerStatus.StopPending)
                {
                    service.Stop();
                }
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                throw new SetupException($"The {name} service didn't stop within 45 seconds, so nothing was changed. Try again, or restart the PC first.");
            }
            catch (InvalidOperationException ex)
            {
                throw new SetupException($"Windows wouldn't stop the {name} service: {ex.InnerException?.Message ?? ex.Message}");
            }
        }
        WaitForExit(name);
        _log.Write($"Stopped {name}.");
        return true;
    }

    /// <summary>
    /// Waits for the service's process to actually go away.
    ///
    /// <para>Windows reports a service as stopped when it has acknowledged the stop, not when its
    /// process has exited, and for those last moments the program still holds its own files open.
    /// Copying into that gap is how an update ends up queued for the next restart while setup
    /// reports that it installed.</para>
    /// </summary>
    private void WaitForExit(string name)
    {
        var image = name == Machine.ServiceName ? Path.GetFileNameWithoutExtension(Machine.ServiceExe) : null;
        if (image == null)
        {
            return;
        }
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var running = Process.GetProcessesByName(image);
            foreach (var process in running)
            {
                process.Dispose();
            }
            if (running.Length == 0)
            {
                return;
            }
            Thread.Sleep(500);
        }
        _log.Write($"  {image}.exe is still running 15 seconds after the service stopped.");
    }

    private void StartService(string name)
    {
        using (var service = new ServiceController(name))
        {
            try
            {
                if (service.Status == ServiceControllerStatus.Running)
                {
                    return;
                }
                if (service.Status == ServiceControllerStatus.Stopped)
                {
                    service.Start();
                }
            }
            catch (InvalidOperationException ex)
            {
                throw new SetupException($"Windows wouldn't start the NetRoute service: {ex.InnerException?.Message ?? ex.Message}");
            }

            var started = DateTime.UtcNow;
            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(45))
            {
                Thread.Sleep(500);
                service.Refresh();
                if (service.Status == ServiceControllerStatus.Running)
                {
                    _log.Write($"{name} is running.");
                    return;
                }
                if (service.Status == ServiceControllerStatus.Stopped && DateTime.UtcNow - started > TimeSpan.FromSeconds(3))
                {
                    break;   // it started and fell over
                }
            }
        }
        throw new SetupException("The NetRoute service didn't start. The reason is in Event Viewer > Windows Logs > Application (source NetRoute.Service).");
    }

    /// <summary>
    /// Copies every file across, overwriting. A file that's in use and identical is left alone
    /// (the loaded driver, usually); one that's in use and different is swapped at the next restart.
    /// </summary>
    private void CopyTree(string source, string target)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, file.Substring(source.Length).TrimStart('\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            try
            {
                Retry(() => File.Copy(file, destination, true));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (File.Exists(destination) && SameFile(file, destination))
                {
                    continue;
                }
                _log.Write($"  {Path.GetFileName(destination)} is locked: {ex.Message}");
                var pending = destination + ".pending";
                File.Copy(file, pending, true);
                if (!Machine.ReplaceAtRestart(pending, destination))
                {
                    throw new SetupException($"{Path.GetFileName(destination)} is in use and couldn't be updated. Close NetRoute and try again.", ex);
                }
                RestartNeeded = true;
                Warnings.Add($"{Path.GetFileName(destination)} was in use. Restart your PC to finish updating it.");
            }
        }
    }

    /// <summary>Deletes files in <paramref name="target"/> that the new version doesn't have.</summary>
    private void RemoveExtraFiles(string target, string reference)
    {
        foreach (var file in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
        {
            var relative = file.Substring(target.Length).TrimStart('\\');
            if (File.Exists(Path.Combine(reference, relative)) || relative.EndsWith(".pending", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                File.Delete(file);
            }
            catch (Exception)
            {
                Machine.DeleteAtRestart(file);
            }
        }
    }

    private static bool SameFile(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
        {
            return false;
        }
        using (var sha = SHA256.Create())
        using (var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            return sha.ComputeHash(fa).SequenceEqual(sha.ComputeHash(fb));
        }
    }

    private void DeleteTree(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (Exception ex)
        {
            _log.Write($"Couldn't delete {dir}: {ex.Message}");
        }
    }

    private static long DirectorySize(string dir)
        => Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
}
