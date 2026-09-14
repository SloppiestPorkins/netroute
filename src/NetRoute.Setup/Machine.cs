using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;

namespace NetRoute.Setup;

/// <summary>Where NetRoute lives, and thin wrappers over the Windows pieces setup touches.</summary>
internal static class Machine
{
    public const string ServiceName = "NetRoute";

    /// <summary>Mullvad's service name for the driver. Reused, so a PC never has two copies registered.</summary>
    public const string DriverService = "mullvad-split-tunnel";

    public const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NetRoute";

    /// <summary>What setup changed outside NetRoute's own folder, so uninstall can put it back.</summary>
    public const string StateKeyPath = @"SOFTWARE\NetRoute\Setup";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetRoute");
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetRoute");
    public static string AppExe => Path.Combine(InstallDir, "NetRoute.App.exe");
    public static string ServiceExe => Path.Combine(InstallDir, "NetRoute.Service.exe");
    public static string CliExe => Path.Combine(InstallDir, "netroute.exe");
    public static string UninstallerExe => Path.Combine(InstallDir, "Uninstall NetRoute.exe");
    public static string DriverFile => Path.Combine(InstallDir, "driver", "mullvad-split-tunnel.sys");
    public static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "NetRoute.lnk");
    public static string StartupLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "NetRoute.lnk");
    public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "NetRoute.lnk");

    /// <summary>Where INSTALL-NETROUTE.cmd put its sign-in shortcut. Replaced by the all-users one.</summary>
    public static string OldStartupLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "NetRoute.lnk");

    public static string Version => typeof(Machine).Assembly.GetName().Version.ToString(3);

    public static string Sc => Path.Combine(Environment.SystemDirectory, "sc.exe");
    public static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    public static bool IsAdmin
    {
        get
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
    }

    public static RegistryKey Hklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public static string InstalledVersion()
    {
        using (var hklm = Hklm())
        using (var key = hklm.OpenSubKey(UninstallKeyPath))
        {
            return key?.GetValue("DisplayVersion") as string;
        }
    }

    // ---- setup's own record of what it changed ----

    public static string ReadState(string name)
    {
        using (var hklm = Hklm())
        using (var key = hklm.OpenSubKey(StateKeyPath))
        {
            return key?.GetValue(name) as string;
        }
    }

    public static List<KeyValuePair<string, string>> ReadStates(string prefix)
    {
        using (var hklm = Hklm())
        using (var key = hklm.OpenSubKey(StateKeyPath))
        {
            return key == null
                ? new List<KeyValuePair<string, string>>()
                : key.GetValueNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(n => new KeyValuePair<string, string>(n.Substring(prefix.Length), key.GetValue(n) as string))
                    .ToList();
        }
    }

    /// <summary>Records a value. With <paramref name="firstTimeOnly"/>, an earlier record (the true original) wins.</summary>
    public static void WriteState(string name, string value, bool firstTimeOnly)
    {
        using (var hklm = Hklm())
        using (var key = hklm.CreateSubKey(StateKeyPath))
        {
            if (!firstTimeOnly || key.GetValue(name) == null)
            {
                key.SetValue(name, value ?? "");
            }
        }
    }

    // ---- services ----

    public static ServiceControllerStatus? Status(string name)
    {
        try
        {
            using (var service = new ServiceController(name))
            {
                return service.Status;
            }
        }
        catch (InvalidOperationException)
        {
            return null;   // not installed
        }
    }

    public static ServiceStartMode? StartMode(string name)
    {
        try
        {
            using (var service = new ServiceController(name))
            {
                return service.StartType;
            }
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Mullvad VPN's own background services (not the driver). They'd take the driver's only handle.</summary>
    public static List<string> MullvadDaemons()
    {
        var names = new List<string>();
        foreach (var service in ServiceController.GetServices())
        {
            using (service)
            {
                if (service.DisplayName.IndexOf("Mullvad", StringComparison.OrdinalIgnoreCase) >= 0
                    && service.ServiceName.IndexOf("split", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    names.Add(service.ServiceName);
                }
            }
        }
        return names;
    }

    /// <summary>The file the driver service loads, as a normal path.</summary>
    public static string DriverImagePath()
    {
        using (var hklm = Hklm())
        using (var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + DriverService))
        {
            var raw = key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }
            raw = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (raw.StartsWith(@"\??\", StringComparison.Ordinal))
            {
                return raw.Substring(4);
            }
            if (raw.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(windows, raw.Substring(@"\SystemRoot\".Length));
            }
            return Path.IsPathRooted(raw) ? raw : Path.Combine(windows, raw);
        }
    }

    // ---- processes ----

    /// <summary>Runs a program hidden, logging the command and everything it prints.</summary>
    public static int Run(string file, string arguments, Log log, out string output, int timeoutMs = 120000)
    {
        log.Write($"> {Path.GetFileName(file)} {arguments}");
        var info = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using (var process = Process.Start(info))
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill();
                }
                catch (Exception)
                {
                    // Already gone.
                }
                throw new SetupException($"{Path.GetFileName(file)} didn't finish in {timeoutMs / 1000} seconds.");
            }
            output = (stdout.Result + Environment.NewLine + stderr.Result).Trim();
            if (output.Length > 0)
            {
                log.Write("  " + output.Replace(Environment.NewLine, Environment.NewLine + "  "));
            }
            log.Write($"  exit code {process.ExitCode}");
            return process.ExitCode;
        }
    }

    // ---- shell ----

    public static void CreateShortcut(string link, string target, string arguments)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link));
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        dynamic shell = Activator.CreateInstance(shellType);
        try
        {
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = target;
            shortcut.Arguments = arguments ?? "";
            shortcut.WorkingDirectory = Path.GetDirectoryName(target);
            shortcut.Description = "Keep your apps on the network you chose";
            shortcut.IconLocation = target + ",0";
            shortcut.Save();
            Marshal.FinalReleaseComObject(shortcut);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    public static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // A stale shortcut is harmless.
        }
    }

    // ---- PATH ----

    private const string EnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>
    /// Edits the machine PATH as stored, keeping entries like %SystemRoot% unexpanded. Going
    /// through Environment.SetEnvironmentVariable would expand them for good.
    /// </summary>
    public static void EditPath(Func<List<string>, List<string>> edit)
    {
        using (var hklm = Hklm())
        using (var key = hklm.OpenSubKey(EnvironmentKey, writable: true))
        {
            if (key == null)
            {
                return;
            }
            var current = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            var entries = current.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            var edited = edit(entries);
            if (!edited.SequenceEqual(entries))
            {
                key.SetValue("Path", string.Join(";", edited), RegistryValueKind.ExpandString);
                BroadcastEnvironmentChange();
            }
        }
    }

    private static bool SamePath(string a, string b)
        => string.Equals(a.Trim().TrimEnd('\\'), b.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static void AddToPath(string directory)
        => EditPath(entries => entries.Any(e => SamePath(e, directory)) ? entries : entries.Concat(new[] { directory }).ToList());

    public static void RemoveFromPath(string directory)
        => EditPath(entries => entries.Where(e => !SamePath(e, directory)).ToList());

    /// <summary>Tells open programs (Explorer, so new terminals) that PATH changed.</summary>
    private static void BroadcastEnvironmentChange()
        => SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, UIntPtr.Zero, "Environment", 0x0002, 5000, out _);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

    // ---- files in use ----

    /// <summary>Deletes a file (or empty folder) at the next restart. For a loaded driver.</summary>
    public static bool DeleteAtRestart(string path) => MoveFileEx(path, null, 0x4 /* MOVEFILE_DELAY_UNTIL_REBOOT */);

    /// <summary>Replaces a file at the next restart.</summary>
    public static bool ReplaceAtRestart(string source, string target) => MoveFileEx(source, target, 0x1 | 0x4 /* REPLACE_EXISTING | DELAY_UNTIL_REBOOT */);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string replacement, int flags);
}
