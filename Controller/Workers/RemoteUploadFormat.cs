using System;
using System.Collections.Generic;
using System.Globalization;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// Pure policy for the audio format a REMOTE worker is uploaded in (issue #138).
    /// <para>
    /// The plugin extracts 16 kHz mono s16le PCM WAV — 32,000 bytes per audio-second, i.e. 1.92 MB per
    /// minute. Hosted providers cap uploads (OpenAI 25 MB; Groq 25 MB for a direct upload on BOTH tiers), so a 40-minute
    /// title at 76.8 MB is refused with HTTP 413 and a 2-hour film has no chance. Re-encoding for the
    /// upload fixes that without touching the extracted WAV that the LOCAL whisper-cli path uses.
    /// </para>
    /// <para>
    /// DEFAULT IS <see cref="Wav"/> — i.e. off. This is not timidity: whisper.cpp's own
    /// <c>whisper-server</c> decodes WAV only, and this project's worker image ships with
    /// <c>INSTALL_FFMPEG=false</c>/<c>CONVERT=false</c>, so sending it anything else would break every
    /// self-hosted worker. Compression is opt-in per worker, for hosted endpoints that document support.
    /// </para>
    /// </summary>
    public static class RemoteUploadFormat
    {
        public const string Wav = "wav";
        public const string Flac = "flac";
        public const string Opus = "opus";

        /// <summary>Opus bitrate used for uploads. 24 kbps mono keeps a 2-hour film near 20 MB.</summary>
        public const int OpusBitrateKbps = 24;

        private static readonly HashSet<string> Supported =
            new(StringComparer.OrdinalIgnoreCase) { Wav, Flac, Opus };

        /// <summary>
        /// Normalizes a configured codec value. Anything unrecognised, empty, or null falls back to
        /// <see cref="Wav"/> — an unknown codec must never silently produce an upload a worker cannot decode.
        /// </summary>
        public static string Normalize(string? codec)
        {
            var trimmed = (codec ?? string.Empty).Trim();
            return Supported.Contains(trimmed) ? trimmed.ToLowerInvariant() : Wav;
        }

        /// <summary>True when the configured codec requires a re-encode before upload.</summary>
        public static bool RequiresReencode(string? codec)
            => !string.Equals(Normalize(codec), Wav, StringComparison.Ordinal);

        /// <summary>
        /// Multipart file name for a codec. Providers routinely sniff the format from the EXTENSION, so a
        /// FLAC body named "audio.wav" gets rejected or mis-decoded — the name must match the bytes.
        /// </summary>
        public static string FileName(string? codec) => Normalize(codec) switch
        {
            Flac => "audio.flac",
            Opus => "audio.ogg",
            _ => "audio.wav",
        };

        /// <summary>Content-Type for a codec, matching <see cref="FileName"/>.</summary>
        public static string ContentType(string? codec) => Normalize(codec) switch
        {
            Flac => "audio/flac",
            Opus => "audio/ogg",
            _ => "audio/wav",
        };

        /// <summary>File extension (with dot) for the temporary re-encoded upload.</summary>
        public static string Extension(string? codec) => Normalize(codec) switch
        {
            Flac => ".flac",
            Opus => ".ogg",
            _ => ".wav",
        };

        /// <summary>
        /// FFmpeg arguments that re-encode the extracted WAV for upload.
        /// <para>
        /// FLAC MUST pass <c>-sample_fmt s16</c>. Without it ffmpeg's FLAC encoder defaults to 24-bit
        /// (sample_fmt s32) and losslessly compresses samples 1.5x wider than the 16-bit source — measured
        /// on a real title, that produces a file LARGER than the PCM input (19,713,918 vs 19,200,102 bytes),
        /// i.e. the "fix" would make the 413 worse. Groq's own published command omits this flag.
        /// </para>
        /// </summary>
        public static string BuildFfmpegArguments(string sourceWavPath, string targetPath, string? codec)
        {
            if (string.IsNullOrWhiteSpace(sourceWavPath))
            {
                throw new ArgumentException("Source path is required", nameof(sourceWavPath));
            }
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ArgumentException("Target path is required", nameof(targetPath));
            }

            var codecArgs = Normalize(codec) switch
            {
                // -sample_fmt s16: see remarks. Lossless, bit-exact duration, ~53% of PCM (measured).
                Flac => "-c:a flac -sample_fmt s16",
                // Ogg Opus. Mandatory pre-skip in the container keeps duration sample-accurate on decode;
                // ~9% of PCM at 24 kbps (measured).
                Opus => string.Format(CultureInfo.InvariantCulture, "-c:a libopus -b:a {0}k", OpusBitrateKbps),
                _ => throw new ArgumentException($"Codec '{codec}' needs no re-encode", nameof(codec)),
            };

            // -vn: no video. 16 kHz mono matches what whisper consumes anyway, and the source is already
            // in that shape, so this is a straight re-encode with no resampling surprise.
            return string.Format(
                CultureInfo.InvariantCulture,
                "-i \"{0}\" -vn -ac 1 -ar 16000 {1} -y \"{2}\"",
                sourceWavPath,
                codecArgs,
                targetPath);
        }
    }
}
