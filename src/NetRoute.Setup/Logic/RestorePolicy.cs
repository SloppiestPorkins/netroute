namespace NetRoute.Setup;

/// <summary>How putting Windows' network settings back went during an uninstall.</summary>
internal enum RestoreOutcome
{
    /// <summary>Nothing of NetRoute's to put back (no saved state, nothing to run).</summary>
    NothingToRestore,

    /// <summary>The restore ran, reported success, and the saved state is gone.</summary>
    Restored,

    /// <summary>The restore ran and failed, could not run, or its success cannot be confirmed.</summary>
    Failed
}

/// <summary>
/// Decides, from facts only, whether Windows' network settings were put back. Kept free of the
/// machine so the rule that protects the user can be tested: a restore that did not demonstrably
/// work must never be treated as one that did, because the next step deletes the program that
/// could have done it.
/// </summary>
internal static class RestorePolicy
{
    /// <param name="cliExists">netroute.exe is still in the install folder.</param>
    /// <param name="stateFileExistedBefore">route-state.json was present before the restore ran.</param>
    /// <param name="cliExitCode">The restore command's exit code, or null if it never ran.</param>
    /// <param name="stateFileExistsAfter">route-state.json is still present afterwards.</param>
    public static RestoreOutcome Decide(bool cliExists, bool stateFileExistedBefore, int? cliExitCode, bool stateFileExistsAfter)
    {
        if (!cliExists)
        {
            // Without the program nothing can restore. Only "nothing was saved" lets that pass.
            return stateFileExistedBefore ? RestoreOutcome.Failed : RestoreOutcome.NothingToRestore;
        }
        if (cliExitCode != 0)
        {
            return RestoreOutcome.Failed;
        }
        // A restore that returns 0 deletes the state it has just used. State left behind means
        // the restore did not finish what it set out to do, whatever it said.
        return stateFileExistsAfter ? RestoreOutcome.Failed : RestoreOutcome.Restored;
    }

    /// <summary>
    /// The line to run in an administrator PowerShell to put every IPv4 connection back on
    /// Windows' automatic metric (its default). Deliberately independent of NetRoute's files.
    /// </summary>
    public const string ManualCommand = "Get-NetIPInterface -AddressFamily IPv4 | Set-NetIPInterface -AutomaticMetric Enabled";

    /// <summary>What to tell the person when the restore did not complete.</summary>
    public static string ManualInstructions(string stateFilePath)
        => "Windows' network settings could not be confirmed as put back. To do it by hand, open PowerShell as administrator and run:  "
           + ManualCommand
           + "  (this puts every connection back on Windows' automatic setting; if you had set a connection's priority yourself, set it again afterwards). "
           + "NetRoute's record of the original settings has been kept at " + stateFilePath + ".";

    /// <summary>Whether a removal may go on past a failed restore. Only when the person said so.</summary>
    public static bool MayContinue(RestoreOutcome outcome, bool force)
        => outcome != RestoreOutcome.Failed || force;
}
