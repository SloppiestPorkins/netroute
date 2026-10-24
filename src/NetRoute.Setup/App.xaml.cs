using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace NetRoute.Setup;

/// <summary>
/// Command line:
/// <list type="bullet">
/// <item>(none)                 install, or update/repair when NetRoute is already installed</item>
/// <item>/uninstall, /remove   remove NetRoute (the default when there is no payload)</item>
/// <item>/update, /repair      pick what to do about an existing install, instead of being asked</item>
/// <item>/quiet                 no window; 0 on success, 3010 if a restart is needed, 1 on failure (also when
/// a removal finished but could not put Windows' network settings back). The exit code is the real
/// result: an elevated or relocated copy does the work and this process waits for it.</item>
/// <item>/force                 with /uninstall: carry on removing even if Windows' network settings could not
/// be put back. The saved record of the originals is kept either way.</item>
/// <item>/extract &lt;dir&gt;       unpack the payload only (no admin needed), for checking a build</item>
/// <item>/screenshot &lt;png&gt; [/page welcome|progress|done|failed]   render a page and exit (no admin needed)</item>
/// </list>
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        string Value(string flag)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        DispatcherUnhandledException += (_, a) =>
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "NetRoute-setup-crash.log"), $"[{DateTime.Now:u}] {a.Exception}\n\n");
            MessageBox.Show("NetRoute setup hit an unexpected problem:\n\n" + a.Exception.Message, "NetRoute setup", MessageBoxButton.OK, MessageBoxImage.Error);
            a.Handled = true;
            Shutdown(1);
        };

        if (Value("/extract") is string target)
        {
            try
            {
                Payload.Extract(target, _ => { });
                Shutdown(0);
            }
            catch (Exception)
            {
                Shutdown(1);
            }
            return;
        }

        var uninstall = Has("/uninstall") || Has("/remove") || !Payload.Present;
        var action = uninstall ? SetupAction.Remove
            : Has("/repair") ? SetupAction.Repair
            : Has("/update") ? SetupAction.Update
            : SetupAction.Install;   // the model turns this into update, repair or go-back when NetRoute is already here
        var quiet = Has("/quiet") || Has("/S");
        var screenshot = Value("/screenshot");
        var self = Process.GetCurrentProcess().MainModule.FileName;

        if (screenshot == null)
        {
            if (!Machine.IsAdmin)
            {
                Shutdown(Relaunch(self, args, elevate: true, wait: quiet));
                return;
            }
            // The uninstaller lives in the folder it deletes, so it runs from a copy in %TEMP%.
            if (uninstall && !Has("/relocated") && self.StartsWith(Machine.InstallDir + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var copy = Path.Combine(Path.GetTempPath(), "NetRoute-Uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                File.Copy(self, copy, true);
                // A quiet run waits for the copy so its exit code is the real result. That keeps
                // this exe running, so the copy leaves it for a script to delete afterwards.
                Shutdown(Relaunch(copy, args.Concat(new[] { "/relocated" }).ToArray(), elevate: false, wait: quiet));
                return;
            }
        }

        var model = new SetupModel(action) { Force = Has("/force") };
        void CleanUp()
        {
            model.FinishDeferredDeletes();
            if (uninstall && Has("/relocated"))
            {
                DeleteSelfLater(self);
            }
        }

        if (quiet)
        {
            var ok = model.Execute(model.CurrentOptions());
            CleanUp();
            // 3010 is what Windows installers return for "done, but restart to finish", so winget
            // and any script around this one already know what it means.
            Shutdown(SetupExit.For(ok, model.RestartNeeded, model.Incomplete));
            return;
        }

        var window = new SetupWindow(model, screenshot, Value("/page"));
        MainWindow = window;
        window.Closed += (_, __) =>
        {
            CleanUp();
            Shutdown(model.Failed || model.Incomplete ? SetupExit.Failed : SetupExit.Success);
        };
        window.Show();
    }

    /// <summary>
    /// Hands the work to another copy of setup. With <paramref name="wait"/> (a /quiet run) this
    /// process stays until the copy is done and exits with the copy's exit code, so the code an
    /// automated caller sees is the code of the work, not of the hand-over. Interactive runs
    /// return at once: the copy has the window, and nobody is reading an exit code.
    /// </summary>
    private static int Relaunch(string exe, string[] args, bool elevate, bool wait)
    {
        var arguments = string.Join(" ", args.Select(a => a.Length == 0 || a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a));
        try
        {
            // Not elevating: inherit this process's (already elevated) token rather than asking again.
            using (var child = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = elevate, Verb = elevate ? "runas" : "" }))
            {
                if (!wait)
                {
                    return child == null ? SetupExit.Failed : SetupExit.Success;
                }
                if (child == null)
                {
                    return SetupExit.ForChild(started: false, childExitCode: null);
                }
                child.WaitForExit();
                return SetupExit.ForChild(started: true, childExitCode: child.ExitCode);
            }
        }
        catch (Win32Exception)
        {
            return SetupExit.ElevationDeclined;
        }
    }

    private static void DeleteSelfLater(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & del /f /q \"{path}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Exception)
        {
            // A leftover copy in %TEMP% is harmless.
        }
    }
}
