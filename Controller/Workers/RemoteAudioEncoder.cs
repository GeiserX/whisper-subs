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
        private const string BundledFfmpeg = "/usr/lib/jellyfin-ffmpeg/ffmpeg";

        /// <summary>
        /// Produces the file to upload. Returns <paramref name="sourceWavPath"/> unchanged when the worker
        /// uses WAV (the default) or when re-encoding is impossible — never fails a transcription just
        /// because compression could not run; the caller then uploads the original and, if it is too big,
        /// gets the normal pre-flight/413 path with a clear message.
        /// </summary>
        /// <returns>
        /// The path to upload, whether it is a temporary file the caller must delete, and the codec the
        /// returned file is ACTUALLY in. The last one matters: every fallback below returns the untouched
        /// source WAV, and labelling those bytes with the configured codec would send RIFF/WAVE data as
        /// <c>audio.ogg</c> — the exact mislabelling that makes providers reject or mis-decode an upload.
        /// </returns>
        public static async Task<(string Path, bool IsTemporary, string EffectiveCodec)> PrepareUploadAsync(
            string sourceWavPath, string? codec, ILogger logger, CancellationToken cancellationToken)
        {
            if (!RemoteUploadFormat.RequiresReencode(codec))
            {
                return (sourceWavPath, false, RemoteUploadFormat.Wav);
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
                        FileName = FindFfmpeg(),
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
                try
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // WaitForExitAsync abandons the wait but does NOT stop ffmpeg. Without this the process
                    // keeps running at full speed and TryDelete unlinks a file it still holds open, so the
                    // space stays allocated (on Unraid /tmp that is RAM) until the orphan exits.
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception)
                    {
                        // Already gone, or not permitted - nothing further we can do.
                    }

                    throw;
                }

                await Task.WhenAll(stderr, stdout).ConfigureAwait(false);

                if (process.ExitCode != 0 || !File.Exists(target) || new FileInfo(target).Length == 0)
                {
                    logger.LogWarning(
                        "Re-encoding the upload to {Codec} failed (exit {ExitCode}); uploading the original WAV instead.",
                        RemoteUploadFormat.Normalize(codec), process.ExitCode);
                    TryDelete(target);
                    return (sourceWavPath, false, RemoteUploadFormat.Wav);
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
                    return (sourceWavPath, false, RemoteUploadFormat.Wav);
                }

                logger.LogInformation(
                    "Prepared {Codec} upload: {UploadSize} (from {SourceSize})",
                    RemoteUploadFormat.Normalize(codec),
                    RemoteErrorGuidance.FormatBytes(uploadBytes),
                    RemoteErrorGuidance.FormatBytes(sourceBytes));

                return (target, true, RemoteUploadFormat.Normalize(codec));
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
                return (sourceWavPath, false, RemoteUploadFormat.Wav);
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

        /// <summary>
        /// Jellyfin's bundled ffmpeg when present, otherwise the bare name so the OS resolves it from PATH.
        /// Never null: if ffmpeg is genuinely absent, Process.Start throws and the caller falls back to
        /// uploading the original WAV.
        /// </summary>
        private static string FindFfmpeg()
            => File.Exists(BundledFfmpeg) ? BundledFfmpeg : "ffmpeg";
    }
}
