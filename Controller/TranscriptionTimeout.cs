using System;

namespace WhisperSubs.Controller
{
    /// <summary>
    /// Pure per-call transcription-timeout policy (v4.0 resilience foundation). A remote worker call is
    /// bounded by a deadline derived from the AUDIO LENGTH, not a fixed wall-clock timeout: a short
    /// language-detection chunk gets a small deadline; a feature-length film gets a large one — but both
    /// are bounded. This closes the "70-hour stuck task" class of bug, where an unreachable/stalled
    /// endpoint blocked for the fixed 30-minute <c>HttpClient.Timeout</c> on every one of hundreds of
    /// per-chunk calls across a library scan.
    /// </summary>
    public static class TranscriptionTimeout
    {
        // The plugin extracts audio as 16 kHz mono signed-16-bit PCM WAV, i.e. 16000 samples/s x 2 bytes
        // = 32000 bytes of audio per second (the ~44-byte header is negligible). So audio-seconds can be
        // estimated straight from the WAV file size without probing it.
        internal const double BytesPerAudioSecond = 32000.0;

        /// <summary>
        /// The deadline for a single transcription/detection call: estimated audio-seconds x
        /// <paramref name="realtimeFactor"/>, clamped to
        /// [<paramref name="minSeconds"/>, <paramref name="maxHours"/>]. The factor is a generous upper
        /// bound on how much slower than real-time a healthy worker may run before it is presumed hung —
        /// so a slow-but-working large-v3 pass is never guillotined, while a dead endpoint is cut off fast.
        /// Non-positive settings fall back to safe defaults (factor 6, floor 60s, cap 12h).
        /// </summary>
        public static TimeSpan Compute(long audioBytes, double realtimeFactor, int minSeconds, int maxHours)
        {
            double factor = realtimeFactor > 0 ? realtimeFactor : 6.0;
            double min = minSeconds > 0 ? minSeconds : 60;
            double maxSeconds = (maxHours > 0 ? maxHours : 12) * 3600.0;

            double audioSeconds = Math.Max(0, audioBytes) / BytesPerAudioSecond;
            double seconds = audioSeconds * factor;

            if (seconds < min) seconds = min;
            if (seconds > maxSeconds) seconds = maxSeconds;

            return TimeSpan.FromSeconds(seconds);
        }
    }
}
