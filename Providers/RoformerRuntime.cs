using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WhisperSubs.Providers
{
    /// <summary>Shared native-loader setup for BSRoformer.cpp validation and inference.</summary>
    internal static class RoformerRuntime
    {
        internal static string? GetLibraryPathVariable(string platform) => platform switch
        {
            var value when value.StartsWith("linux-", StringComparison.OrdinalIgnoreCase) => "LD_LIBRARY_PATH",
            var value when value.StartsWith("osx-", StringComparison.OrdinalIgnoreCase) => "DYLD_LIBRARY_PATH",
            _ => null
        };

        internal static void ConfigureLibraryPath(ProcessStartInfo startInfo, string binaryPath)
        {
            var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "linux-current"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "osx-current"
                    : "other";
            var variable = GetLibraryPathVariable(platform);
            var binDirectory = Path.GetDirectoryName(binaryPath);
            if (variable == null || string.IsNullOrEmpty(binDirectory)) return;

            var current = Environment.GetEnvironmentVariable(variable) ?? "";
            startInfo.Environment[variable] = binDirectory
                + (string.IsNullOrEmpty(current) ? "" : Path.PathSeparator + current);
        }
    }
}
