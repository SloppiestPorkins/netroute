using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NetRoute.Windows.Split;

/// <summary>
/// The process registry the split-tunnel driver needs when it is initialised: every
/// process with its parent and NT device image path. After that the driver tracks process
/// creation itself.
///
/// <para>Mirrors Mullvad's client: processes that cannot be opened are skipped, image paths
/// come from GetProcessImageFileNameW (already in device form), and a "parent" created
/// after its child is treated as a recycled PID and dropped. Without that last step a
/// long-dead game's PID reused by an unrelated process could inherit the game's routing.</para>
/// </summary>
public static class ProcessSnapshot
{
    public static IReadOnlyList<ProcessEntry> Capture()
    {
        var raw = new List<(uint Pid, uint Parent, long Created, string? Path)>();

        using var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
        for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
        {
            using var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, entry.th32ProcessID);
            if (process.IsInvalid)
            {
                continue;   // System, Idle, protected processes: same as Mullvad's client
            }

            GetProcessTimes(process, out var created, out _, out _, out _);
            raw.Add((entry.th32ProcessID, entry.th32ParentProcessID, created, ImagePath(process)));
        }

        var createdByPid = raw.ToDictionary(r => r.Pid, r => r.Created);
        return raw.Select(r =>
        {
            var parent = r.Parent;
            if (parent != 0 && createdByPid.TryGetValue(parent, out var parentCreated) && parentCreated > r.Created)
            {
                parent = 0;
            }
            return new ProcessEntry(r.Pid, parent, r.Path);
        }).ToList();
    }

    private static string? ImagePath(SafeProcessHandle process)
    {
        var buffer = new StringBuilder(1024);
        var length = K32GetProcessImageFileNameW(process, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, (int)length) : null;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint K32GetProcessImageFileNameW(SafeProcessHandle process, StringBuilder buffer, int size);
}

/// <summary>Converts Win32 paths to the NT device paths the driver matches on.</summary>
public static class DevicePaths
{
    /// <summary>
    /// "C:\Windows\System32\curl.exe" becomes "\Device\HarddiskVolume3\Windows\System32\curl.exe".
    /// Uses GetFinalPathNameByHandle (resolves links, canonical case) and falls back to a
    /// QueryDosDevice drive-letter mapping for files that cannot be opened.
    /// </summary>
    public static string ToNtPath(string win32Path)
    {
        try
        {
            using var file = File.OpenHandle(win32Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new StringBuilder(1024);
            var length = GetFinalPathNameByHandleW(file, buffer, buffer.Capacity, VOLUME_NAME_NT);
            if (length > 0 && length < buffer.Capacity)
            {
                return buffer.ToString(0, (int)length);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var full = Path.GetFullPath(win32Path);
        var drive = full[..2];
        var target = new StringBuilder(1024);
        if (full.Length < 3 || full[1] != ':' || QueryDosDeviceW(drive, target, target.Capacity) == 0)
        {
            throw new ArgumentException($"Cannot map {win32Path} to a device path.");
        }
        return target.ToString() + full[2..];
    }

    private const uint VOLUME_NAME_NT = 0x2;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, int size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDeviceW(string deviceName, StringBuilder target, int size);
}
