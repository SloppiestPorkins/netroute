namespace NetRoute.Setup;

/// <summary>
/// The uninstaller runs from a copy in %TEMP% so it can delete its own folder, but when it was
/// asked to wait for that copy (so a /quiet run can report a real exit code) the original
/// "Uninstall NetRoute.exe" is still running, and Windows will not delete a running program.
/// That file is removed by a small script after this process ends, instead of being queued for
/// the next restart, which would wrongly report that a restart is needed.
/// </summary>
internal static class SelfDelete
{
    public static bool IsLeftToWaitingParent(string file, string uninstallerExe)
        => string.Equals(file, uninstallerExe, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>A batch script that retries for about a minute, then removes the folder if empty.</summary>
    public static string Script(string file, string folder, string scriptPath)
        => "@echo off\r\n"
           + "set tries=0\r\n"
           + ":again\r\n"
           + $"del /f /q \"{file}\" >nul 2>&1\r\n"
           + $"if not exist \"{file}\" goto gone\r\n"
           + "set /a tries+=1\r\n"
           + "if %tries% geq 60 goto end\r\n"
           + "ping -n 2 127.0.0.1 >nul\r\n"
           + "goto again\r\n"
           + ":gone\r\n"
           + $"rmdir \"{folder}\" >nul 2>&1\r\n"
           + ":end\r\n"
           + $"del /f /q \"{scriptPath}\" >nul 2>&1\r\n";
}
