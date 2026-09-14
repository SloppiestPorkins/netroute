using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace NetRoute.Setup;

/// <summary>NetRoute itself, embedded in the setup program as payload.zip by scripts\build-installer.ps1.</summary>
internal static class Payload
{
    private const string ResourceName = "payload.zip";

    /// <summary>False for the uninstaller, which is this program built without a payload.</summary>
    public static bool Present => typeof(Payload).Assembly.GetManifestResourceInfo(ResourceName) != null;

    public static void Extract(string directory, Action<double> progress)
    {
        var root = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
        using (var stream = typeof(Payload).Assembly.GetManifestResourceStream(ResourceName)
                            ?? throw new SetupException("This setup program doesn't contain NetRoute. Download it again."))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            var total = Math.Max(1, zip.Entries.Sum(e => e.Length));
            long done = 0;
            foreach (var entry in zip.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SetupException("This setup program is damaged. Download it again.");
                }
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                entry.ExtractToFile(target, true);
                done += entry.Length;
                progress((double)done / total);
            }
        }
    }
}
