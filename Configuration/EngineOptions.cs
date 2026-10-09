using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace WhisperSubs.Configuration
{
    /// <summary>Where language detection runs.</summary>
    public enum DetectionDevice
    {
        /// <summary>The GPU after a self-check on an English clip; the CPU when the check fails.</summary>
        Auto,
        /// <summary>The GPU, without the self-check.</summary>
        Gpu,
        /// <summary>The CPU (--no-gpu).</summary>
        Cpu,
    }

    /// <summary>What a forced line in a language Qwen3-ASR cannot transcribe gets on a Qwen3-ASR title.</summary>
    public enum UncoveredForcedLineAction
    {
        /// <summary>This server's Whisper model transcribes it.</summary>
        Whisper,
        /// <summary>It is left out.</summary>
        Skip,
    }

    /// <summary>An akou row's job settings: priority, seconds of audio per job, and the languages[] bound.</summary>
    public sealed record AkouJobOptions(int Priority, int WindowSeconds, IReadOnlyList<string> Languages)
    {
        /// <summary>Priority -5, 600 s windows, no language bound.</summary>
        public static AkouJobOptions Default { get; } = new(EngineOptions.DefaultAkouPriority, EngineOptions.DefaultWindowSeconds, Array.Empty<string>());
    }

    /// <summary>How this server's Whisper runs language detection: device, threads (0 = automatic) and the per-chunk timeout.</summary>
    public sealed record DetectionSettings(DetectionDevice Device, int Threads, TimeSpan FileTimeout)
    {
        /// <summary>The defaults: Auto, automatic threads, 300 s a chunk.</summary>
        public static DetectionSettings Default { get; } = new(DetectionDevice.Auto, 0, TimeSpan.FromSeconds(EngineOptions.DefaultDetectionTimeoutSeconds));

        /// <summary>The settings as configured, each value clamped to its range.</summary>
        public static DetectionSettings From(PluginConfiguration config)
            => new(config.DetectionDevice, EngineOptions.DetectionThreads(config.DetectionThreadCount),
                EngineOptions.DetectionTimeout(config.DetectionTimeoutSeconds));
    }

    /// <summary>
    /// The engine and detection settings as the code uses them: every value clamped to its range, so a
    /// hand-edited or older configuration can never reach an engine out of range. Pure.
    /// </summary>
    internal static class EngineOptions
    {
        internal const int DefaultDetectionBatchSize = 32;
        internal const int DefaultDetectionTimeoutSeconds = 300;
        internal const float DefaultForcedLanguageMinProbability = 0.3f;
        internal const int DefaultAkouPriority = -5;
        internal const int DefaultWindowSeconds = 600;

        private static readonly Regex VulkanDevicePattern = new(@"^\d{1,2}(,\d{1,2})*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex LanguageCodePattern = new("^[a-z]{2,3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static int DetectionThreads(int value) => Math.Clamp(value, 0, 256);

        internal static float ForcedLanguageMinProbability(float value)
            => float.IsNaN(value) || value < 0f || value > 1f ? DefaultForcedLanguageMinProbability : value;

        internal static int DetectionBatchSize(int value) => value <= 0 ? DefaultDetectionBatchSize : Math.Min(value, 64);

        internal static TimeSpan DetectionTimeout(int seconds)
            => TimeSpan.FromSeconds(seconds <= 0 ? DefaultDetectionTimeoutSeconds : Math.Clamp(seconds, 30, 3600));

        internal static int MaxFullModelEngines(int value) => Math.Clamp(value, 1, 8);

        internal static int WindowSeconds(int value) => value <= 0 ? DefaultWindowSeconds : Math.Clamp(value, 60, 7200);

        internal static int AkouPriority(int value) => Math.Clamp(value, -10, 10);

        /// <summary>The device list for GGML_VK_VISIBLE_DEVICES, or "" for the engine's own choice.</summary>
        internal static string VulkanDevice(string? value)
        {
            var v = (value ?? "").Replace(" ", "", StringComparison.Ordinal);
            return VulkanDevicePattern.IsMatch(v) ? v : "";
        }

        /// <summary>Lower-case ISO codes, in order, without duplicates; anything else is dropped.</summary>
        internal static IReadOnlyList<string> Languages(IEnumerable<string>? values)
            => (values ?? Enumerable.Empty<string>())
                .Select(v => (v ?? "").Trim().ToLower(CultureInfo.InvariantCulture))
                .Where(v => LanguageCodePattern.IsMatch(v))
                .Distinct(StringComparer.Ordinal)
                .ToList();
    }
}
