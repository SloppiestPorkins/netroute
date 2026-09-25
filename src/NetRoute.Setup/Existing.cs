using System;
using System.Globalization;
using System.IO;
using System.ServiceProcess;

namespace NetRoute.Setup;

/// <summary>What setup can do about the copy of NetRoute already on this PC.</summary>
public enum SetupAction
{
    Install,
    Update,
    Repair,
    Downgrade,
    Remove
}

/// <summary>
/// The NetRoute already on this PC, if there is one.
///
/// <para>Three things are looked at, because any of them can be true on its own. The uninstall
/// key is what Settings &gt; Apps shows and is the only place a version is written down. The
/// service is what actually protects the PC. And the files are what an earlier build left behind
/// when it was installed by INSTALL-NETROUTE.cmd, which registered nothing at all — that install
/// has no version, so setup calls it what it is rather than guessing a number.</para>
/// </summary>
internal sealed class Existing
{
    public string Version { get; private set; }
    public Version Parsed { get; private set; }
    public string InstallDir { get; private set; }
    public DateTime? InstalledOn { get; private set; }
    public bool ServiceInstalled { get; private set; }
    public bool ServiceRunning { get; private set; }
    public bool FilesPresent { get; private set; }

    /// <summary>An install from the old script: files, but nothing registered with Windows.</summary>
    public bool Unregistered => Version == null;

    /// <summary>Looks for one. Null when this is a clean PC.</summary>
    public static Existing Find()
    {
        var found = new Existing
        {
            FilesPresent = File.Exists(Machine.ServiceExe) || File.Exists(Machine.AppExe),
            ServiceInstalled = Machine.Status(Machine.ServiceName) != null,
            ServiceRunning = Machine.Status(Machine.ServiceName) == ServiceControllerStatus.Running,
            InstallDir = Machine.InstallDir
        };

        using (var hklm = Machine.Hklm())
        using (var key = hklm.OpenSubKey(Machine.UninstallKeyPath))
        {
            if (key != null)
            {
                found.Version = key.GetValue("DisplayVersion") as string;
                found.InstallDir = key.GetValue("InstallLocation") as string ?? found.InstallDir;
                if (DateTime.TryParseExact(key.GetValue("InstallDate") as string ?? "", "yyyyMMdd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var on))
                {
                    found.InstalledOn = on;
                }
            }
        }

        Version parsed;
        found.Parsed = System.Version.TryParse(found.Version ?? "", out parsed) ? parsed : null;
        return found.FilesPresent || found.ServiceInstalled || found.Version != null ? found : null;
    }

    /// <summary>What this setup would do by default: replace a older copy, repair the same one, or go back.</summary>
    public SetupAction Suggested()
    {
        if (Parsed == null)
        {
            return SetupAction.Update;   // the unregistered script install: replace it
        }
        var mine = System.Version.Parse(Machine.Version);
        return mine > Parsed ? SetupAction.Update : mine == Parsed ? SetupAction.Repair : SetupAction.Downgrade;
    }

    /// <summary>The line under the heading: which version is here, since when, and whether it is running.</summary>
    public string Describe()
    {
        var what = Version != null
            ? "NetRoute " + Version + " is already installed"
            : "An earlier NetRoute is already installed";
        if (InstalledOn.HasValue)
        {
            what += ", from " + InstalledOn.Value.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
        }
        what += ServiceRunning ? ". It is running now." : ServiceInstalled ? ". Its service is stopped." : ". Its service isn't registered.";
        return what;
    }
}
