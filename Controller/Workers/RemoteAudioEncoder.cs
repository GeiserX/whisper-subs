using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// Re-encodes the extracted WAV into a smaller upload format for a remote worker (issue #138).
    /// <para>
    /// This deliberately sits at the UPLOAD boundary rather than in the audio-extraction path: extraction is
    /// shared with the LOCAL whisper-cli provider, which must keep receiving byte-identical 16 kHz mono PCM.
    /// Compressing here means a local install is untouched by construction.
    /// </para>
    /// <para>
    /// Process I/O, so excluded from coverage; the argument construction and format policy live in the pure,
    /// unit-tested <see cref="RemoteUploadFormat"/>.
    /// </para>
    /// </summary>
    [ExcludeFromCodeCoverage(Justification = "FFmpeg process I/O; the argument/format policy is tested in RemoteUploadFormatTests")]
    public static class RemoteAudioEncoder
    {
        private static readonly string[] FfmpegCandidates =
        {
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            "ffmpeg",
            "/usr/bin/ffmpeg",
        };

        /// <summary>
        /// Produces the file to upload. Returns <paramref name="sourceWavPath"/> unchanged when the worker
        /// uses WAV (the default) or when re-encoding is impossible — never fails a transcription just
        /// because compression could not run; the caller then uploads the original and, if it is too big,
        /// gets the normal pre-flight/413 path with a clear message.
        /// </summary>
        /// <returns>
        /// The path to upload, and whether it is a temporary file the caller must delete.
        /// </returns>
        public static async Task<(string Path, bool IsTemporary)> PrepareUploadAsync(
            string sourceWavPath, string? codec, ILogger logger, CancellationToken cancellationToken)
        {
            if (!RemoteUploadFormat.RequiresReencode(codec))
            {
                return (sourceWavPath, false);
            }

            var ffmpeg = FindFfmpeg();
            if (ffmpeg is null)
            {
                logger.LogWarning(
                    "Upload format {Codec} is configured but FFmpeg was not found; uploading the original WAV instead.",
                    RemoteUploadFormat.Normalize(codec));
                return (sourceWavPath, false);
            }

            var target = Path.Combine(
                Path.GetTempPath(),
                $"whispersubs_upload_{Guid.NewGuid():N}{RemoteUploadFormat.Extension(codec)}");

            try
            {
                var arguments = RemoteUploadFormat.BuildFfmpegArguments(sourceWavPath, target, codec);
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpeg,
                        Arguments = arguments,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    },
                };

                process.Start();
                // Drain both pipes so a chatty ffmpeg cannot deadlock on a full buffer.
                var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
                var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await Task.WhenAll(stderr, stdout).ConfigureAwait(false);

                if (process.ExitCode != 0 || !File.Exists(target) || new FileInfo(target).Length == 0)
                {
                    logger.LogWarning(
                        "Re-encoding the upload to {Codec} failed (exit {ExitCode}); uploading the original WAV instead.",
                        RemoteUploadFormat.Normalize(codec), process.ExitCode);
                    TryDelete(target);
                    return (sourceWavPath, false);
                }

                var sourceBytes = new FileInfo(sourceWavPath).Length;
                var uploadBytes = new FileInfo(target).Length;

                // A re-encode that GREW the file is never worth uploading. This is not hypothetical: ffmpeg's
                // FLAC encoder defaults to 24-bit, and the vendor-published command (without -sample_fmt s16)
                // measurably produces a file larger than the 16-bit PCM input.
                if (uploadBytes >= sourceBytes)
                {
                    logger.LogWarning(
                        "Re-encoded upload ({UploadBytes} bytes) is not smaller than the source ({SourceBytes} bytes); uploading the original WAV instead.",
                        uploadBytes, sourceBytes);
                    TryDelete(target);
                    return (sourceWavPath, false);
                }

                logger.LogInformation(
                    "Prepared {Codec} upload: {UploadSize} (from {SourceSize})",
                    RemoteUploadFormat.Normalize(codec),
                    RemoteErrorGuidance.FormatBytes(uploadBytes),
                    RemoteErrorGuidance.FormatBytes(sourceBytes));

                return (target, true);
            }
            catch (OperationCanceledException)
            {
                TryDelete(target);
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not re-encode the upload to {Codec}; uploading the original WAV instead.",
                    RemoteUploadFormat.Normalize(codec));
                TryDelete(target);
                return (sourceWavPath, false);
            }
        }

        /// <summary>Deletes a temporary upload file, ignoring failures.</summary>
        public static void TryDelete(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // A leftover temp file is harmless; never fail a transcription over cleanup.
            }
        }

        private static string? FindFfmpeg()
        {
            foreach (var candidate in FfmpegCandidates)
            {
                if (Path.IsPathRooted(candidate))
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    continue;
                }

                // Bare name: let the OS resolve it from PATH at start time.
                return candidate;
            }

            return null;
        }
    }
}
