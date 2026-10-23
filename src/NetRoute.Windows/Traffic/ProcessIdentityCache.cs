using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NetRoute.Windows.Traffic;

/// <summary>
/// Maps a process ID to its program path and package family name.
///
/// <para>Cached per process lifetime and keyed with the process's creation time. Without the
/// creation time, a PID that Windows reused for a different program would inherit the old
/// program's identity, and its traffic would be counted against the wrong rule.</para>
///
/// <para>The creation time is re-read only every <see cref="RecheckEvery"/> per process. This is
/// asked for every active process every second, and opening a process handle for each one on that
/// cadence, for ever, is a standing cost to learn something that changes only when a PID is
/// reused, which Windows does not do within seconds. Processes that can't be opened (protected
/// ones) are remembered as unknown for the same interval rather than retried every second.</para>
/// </summary>
public sealed class ProcessIdentityCache
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    public static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(30);

    private readonly Dictionary<int, Entry> _cache = [];

    private sealed record Entry(long Created, string? Path, string? PackageFamilyName, long CheckedAt);

    public (string? Path, string? PackageFamilyName) Get(int pid)
    {
        var tick = Environment.TickCount64;
        if (_cache.TryGetValue(pid, out var known) && tick - known.CheckedAt < RecheckEvery.TotalMilliseconds)
        {
            return (known.Path, known.PackageFamilyName);
        }

        using var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsInvalid)
        {
            // Protected processes can't be opened; nothing is known about them.
            _cache[pid] = new Entry(0, null, null, tick);
            return (null, null);
        }

        GetProcessTimes(process, out var created, out _, out _, out _);
        if (known is not null && known.Created == created)
        {
            _cache[pid] = known with { CheckedAt = tick };
            return (known.Path, known.PackageFamilyName);
        }

        var entry = new Entry(created, ImagePath(process), PackageFamily(process), tick);
        _cache[pid] = entry;
        return (entry.Path, entry.PackageFamilyName);
    }

    /// <summary>Forgets processes that have exited.</summary>
    public void Prune(IEnumerable<int> live)
    {
        var keep = live.ToHashSet();
        foreach (var pid in _cache.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _cache.Remove(pid);
        }
    }

    private static string? ImagePath(SafeProcessHandle process)
    {
        var buffer = new StringBuilder(1024);
        var size = buffer.Capacity;
        return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
    }

    private static string? PackageFamily(SafeProcessHandle process)
    {
        uint length = 0;
        if (GetPackageFamilyName(process, ref length, null) != ERROR_INSUFFICIENT_BUFFER)
        {
            return null;   // APPMODEL_ERROR_NO_PACKAGE for ordinary desktop programs
        }
        var buffer = new StringBuilder((int)length);
        return GetPackageFamilyName(process, ref length, buffer) == 0 ? buffer.ToString() : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, StringBuilder? name);
}
