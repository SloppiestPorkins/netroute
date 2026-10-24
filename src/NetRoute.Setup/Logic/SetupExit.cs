namespace NetRoute.Setup;

/// <summary>
/// What setup's process exit code means. It is the only thing an automated caller (winget, a
/// script, Settings > Apps' quiet uninstall) has, so every path that ends the process goes
/// through here.
/// </summary>
internal static class SetupExit
{
    public const int Success = 0;
    public const int Failed = 1;

    /// <summary>Done, but a restart finishes the job. What Windows installers return for that.</summary>
    public const int RestartNeeded = 3010;

    /// <summary>The user declined the administrator prompt.</summary>
    public const int ElevationDeclined = 1223;

    /// <summary>
    /// The result of a run. <paramref name="incomplete"/> is a run that finished its steps but
    /// could not do part of the job (a forced removal that could not put Windows' network
    /// settings back). That is not a success, and must not look like one to a script.
    /// </summary>
    public static int For(bool succeeded, bool restartNeeded, bool incomplete = false)
        => !succeeded || incomplete ? Failed : restartNeeded ? RestartNeeded : Success;

    /// <summary>
    /// What the process that handed the work to a child (the elevated copy, or the copy in
    /// %TEMP% that an uninstaller runs from) should exit with. A child that could not be
    /// started, or whose code could not be read, is a failure rather than a silent 0.
    /// </summary>
    public static int ForChild(bool started, int? childExitCode)
        => !started ? Failed : childExitCode ?? Failed;
}
