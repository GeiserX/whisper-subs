using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace WhisperSubs.Providers
{
    /// <summary>
    /// Language detection runs on the GPU unless that GPU gives wrong answers. whisper.cpp v1.8.4 on Vulkan
    /// (Intel UHD 770, Mesa 25) answered every chunk with one language at p = 0.010, a flat distribution,
    /// while the same binary with --no-gpu was right; v1.9.5 matched the CPU on every chunk. So before the
    /// first detection with a given whisper-cli and model, one run on a short English clip (the JFK sample
    /// that ships with whisper.cpp, public domain) has to come back English with real confidence. If it does
    /// not, detection with that pair runs with --no-gpu for the rest of the process. A replaced binary, model,
    /// or engine behind a wrapper script is checked again (<see cref="Key"/>).
    /// </summary>
    /// <summary>One GPU self-check as the settings page shows it.</summary>
    public sealed record GpuCheckResult(string Binary, string Model, bool Passed, string Answer, DateTime CheckedAtUtc);

    internal static class GpuDetectionCheck
    {
        internal const string ExpectedLanguage = "en";

        /// <summary>
        /// The clip is clear English: on the CPU large-v3 answers en at 0.94 and base at 0.96. A broken backend answers
        /// about 0.01 for every language.
        /// </summary>
        internal const float MinProbability = 0.5f;

        internal const string ClipResource = "WhisperSubs.Providers.Resources.gpu-check-en.wav";

        private static readonly ConcurrentDictionary<string, bool> Verdicts = new(StringComparer.Ordinal);

        private static readonly Regex DetectedLine =
            new(@"auto-detected language:\s*(?<lang>\w+)\s*\(p\s*=\s*(?<p>[\d.]+)\)", RegexOptions.Compiled);

        /// <summary>
        /// True when the self-check run's output says English with at least <see cref="MinProbability"/>. Pure.
        /// </summary>
        internal static bool Passed(string? output)
        {
            if (string.IsNullOrEmpty(output)) return false;
            var m = DetectedLine.Match(output);
            if (!m.Success) return false;
            var lang = (WhisperLanguages.CodeFor(m.Groups["lang"].Value) ?? m.Groups["lang"].Value).ToLowerInvariant();
            return lang == ExpectedLanguage
                && float.TryParse(m.Groups["p"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
                && p >= MinProbability;
        }

        /// <summary>What the self-check run answered, for the log. Pure.</summary>
        internal static string Answer(string? output)
        {
            var m = DetectedLine.Match(output ?? "");
            return m.Success ? $"{m.Groups["lang"].Value} (p = {m.Groups["p"].Value})" : "no language";
        }

        /// <summary>
        /// The verdict's key: the binary and the model, each with its modification time and size, plus what the
        /// binary answers to --version. The configured binary is often a wrapper script that execs the real
        /// whisper-cli, and swapping the engine behind it leaves the wrapper untouched; the version answer
        /// (v1.9.5 prints "whisper.cpp version: 1.9.5", v1.8.4 rejects the flag and prints its usage) changes
        /// with the engine. Pure apart from reading file metadata.
        /// </summary>
        internal static string Key(string executable, string model, string versionOutput)
            => string.Join("|", executable, Stamp(executable), model, Stamp(model), Fingerprint(versionOutput));

        private static string Stamp(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists
                    ? info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + info.Length.ToString(CultureInfo.InvariantCulture)
                    : "0";
            }
            catch (Exception) { return "0"; }
        }

        private static string Fingerprint(string text)
            => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text ?? "")));

        internal static bool TryGet(string key, out bool gpuOk) => Verdicts.TryGetValue(key, out gpuOk);

        internal static void Record(string key, bool gpuOk) => Verdicts[key] = gpuOk;

        /// <summary>Keeps the verdict and what the check saw, for the settings page.</summary>
        internal static void Record(string key, bool gpuOk, string executable, string model, string answer)
        {
            Verdicts[key] = gpuOk;
            Results[executable + "|" + model] = new GpuCheckResult(executable, model, gpuOk, answer, DateTime.UtcNow);
        }

        private static readonly ConcurrentDictionary<string, GpuCheckResult> Results = new(StringComparer.Ordinal);

        /// <summary>The latest self-check per binary and model, newest first.</summary>
        internal static System.Collections.Generic.IReadOnlyList<GpuCheckResult> LatestResults()
            => System.Linq.Enumerable.ToList(System.Linq.Enumerable.OrderByDescending(Results.Values, r => r.CheckedAtUtc));

        /// <summary>Writes the English clip to a new temp file and returns its path; the caller deletes it.</summary>
        internal static string WriteClip()
        {
            // The whispersubs_ prefix lets TempLeftovers clear it if the process dies mid-check.
            var path = Path.Combine(Path.GetTempPath(), "whispersubs_gpucheck_" + Guid.NewGuid().ToString("N") + ".wav");
            using var source = typeof(GpuDetectionCheck).Assembly.GetManifestResourceStream(ClipResource)
                ?? throw new InvalidOperationException("The GPU self-check clip is missing from the plugin assembly.");
            using var target = File.Create(path);
            source.CopyTo(target);
            return path;
        }
    }
}
