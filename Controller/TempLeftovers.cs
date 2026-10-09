using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Controller
{
    /// <summary>
    /// Removes what killed jobs left in the temp folder. A job deletes its own audio and work folder when it
    /// ends, but a job killed with Jellyfin runs no cleanup: a night of OOM restarts left 36 GB of
    /// <c>whispersubs_*</c> folders in the container. Anything of ours older than this process's start
    /// cannot belong to a live job, since every live job was started by this process.
    /// </summary>
    internal static class TempLeftovers
    {
        // <guid>_<guid>.wav (full pass), with _translate or _canary for those passes.
        private static readonly Regex JobAudio = new(
            "^[0-9a-f]{8}(-?[0-9a-f]{4}){3}-?[0-9a-f]{12}_[0-9a-f]{8}(-?[0-9a-f]{4}){3}-?[0-9a-f]{12}(_translate|_canary)?\\.wav$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// True for a name the plugin gives its temp files and folders: <c>whispersubs_*</c> (forced-pass and
        /// probe folders, re-encoded uploads) and a job's extracted audio. Pure.
        /// </summary>
        internal static bool IsOurs(string name)
            => name.StartsWith("whispersubs_", StringComparison.Ordinal) || JobAudio.IsMatch(name);

        /// <summary>
        /// Deletes every entry of ours directly in <paramref name="tempDirectory"/> last written before
        /// <paramref name="olderThanUtc"/>, and returns how many it deleted and their size. Never throws.
        /// </summary>
        internal static (int Count, long Bytes) Sweep(string tempDirectory, DateTime olderThanUtc, ILogger logger)
        {
            var count = 0;
            long bytes = 0;
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(tempDirectory))
                {
                    var name = Path.GetFileName(path);
                    if (!IsOurs(name)) continue;
                    try
                    {
                        if (Directory.Exists(path))
                        {
                            if (Directory.GetLastWriteTimeUtc(path) >= olderThanUtc) continue;
                            var size = SizeOf(path);
                            Directory.Delete(path, recursive: true);
                            bytes += size;
                        }
                        else
                        {
                            var info = new FileInfo(path);
                            if (info.LastWriteTimeUtc >= olderThanUtc) continue;
                            bytes += info.Length;
                            info.Delete();
                        }
                        count++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not remove leftover temp entry {Path}", path);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not list the temp folder {Path} for leftovers", tempDirectory);
            }
            if (count > 0)
            {
                logger.LogInformation("Removed {Count} temp entries left by killed jobs ({MB} MB)", count, bytes / (1024 * 1024));
            }
            return (count, bytes);
        }

        private static long SizeOf(string directory)
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { /* gone meanwhile */ }
            }
            return total;
        }
    }
}
