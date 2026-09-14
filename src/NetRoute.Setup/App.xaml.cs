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
/// <item>/uninstall             remove NetRoute (the default when there is no payload)</item>
/// <item>/quiet                 no window; exit code 0 on success, 1 on failure</item>
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

        var uninstall = Has("/uninstall") || !Payload.Present;
        var screenshot = Value("/screenshot");
        var self = Process.GetCurrentProcess().MainModule.FileName;

        if (screenshot == null)
        {
            if (!Machine.IsAdmin)
            {
                Shutdown(Relaunch(self, args, elevate: true));
                return;
            }
            // The uninstaller lives in the folder it deletes, so it runs from a copy in %TEMP%.
            if (uninstall && !Has("/relocated") && self.StartsWith(Machine.InstallDir + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var copy = Path.Combine(Path.GetTempPath(), "NetRoute-Uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                File.Copy(self, copy, true);
                Shutdown(Relaunch(copy, args.Concat(new[] { "/relocated" }).ToArray(), elevate: false));
                return;
            }
        }

        var model = new SetupModel(uninstall);
        void CleanUp()
        {
            if (uninstall && Has("/relocated"))
            {
                DeleteSelfLater(self);
            }
        }

        if (Has("/quiet") || Has("/S"))
        {
            var ok = model.Execute(model.CurrentOptions());
            CleanUp();
            Shutdown(ok ? 0 : 1);
            return;
        }

        var window = new SetupWindow(model, screenshot, Value("/page"));
        MainWindow = window;
        window.Closed += (_, __) =>
        {
            CleanUp();
            Shutdown(model.Failed ? 1 : 0);
        };
        window.Show();
    }

    private static int Relaunch(string exe, string[] args, bool elevate)
    {
        var arguments = string.Join(" ", args.Select(a => a.Length == 0 || a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a));
        try
        {
            // Not elevating: inherit this process's (already elevated) token rather than asking again.
            using (Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = elevate, Verb = elevate ? "runas" : "" }))
            {
            }
            return 0;
        }
        catch (Win32Exception)
        {
            return 1223;   // the user declined the administrator prompt
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
