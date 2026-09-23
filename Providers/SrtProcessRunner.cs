using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WhisperSubs.Controller;

namespace WhisperSubs.Providers
{
    /// <summary>
    /// Runs a local speech engine that writes <c>&lt;prefix&gt;.srt</c> (whisper-cli, crispasr) and
    /// returns the SRT text. Shared so both engines get the same progress reporting, cancellation
    /// with partial-output recovery, exit-code handling and read-back. The engine-specific parts
    /// (how a failed exit or a missing output is described) are passed in.
    /// </summary>
    internal static class SrtProcessRunner
    {
        /// <param name="startInfo">Fully prepared process (file name, arguments, working directory).</param>
        /// <param name="engineName">Used in log lines only.</param>
        /// <param name="tempSrtPath">Where the engine writes its SRT (<c>-of</c> prefix + ".srt"). Deleted afterwards.</param>
        /// <param name="alternateSrtPath">A second location to read the SRT from, or null.</param>
        /// <param name="buildExitException">Maps (exit code, stderr) to the exception thrown for a non-zero exit.</param>
        /// <param name="describeMissingOutput">Maps stderr to a message when the engine exited 0 but wrote nothing; null keeps the generic error.</param>
        [ExcludeFromCodeCoverage(Justification = "Spawns an engine process")]
        internal static async Task<string> RunAsync(
            ILogger logger,
            ProcessStartInfo startInfo,
            string engineName,
            string tempSrtPath,
            string? alternateSrtPath,
            Func<int, string, Exception> buildExitException,
            Func<string, string?> describeMissingOutput,
            CancellationToken cancellationToken)
        {
            try
            {
                logger.LogInformation("Running: {Executable} {Arguments} (cwd: {WorkingDirectory})",
                    startInfo.FileName, string.Join(" ", startInfo.ArgumentList), startInfo.WorkingDirectory);

                using var process = new Process { StartInfo = startInfo };

                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        logger.LogDebug("{Engine} output: {Output}", engineName, e.Data);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuilder.AppendLine(e.Data);

                        // Progress lines drive the per-file bar; everything else is the engine's own
                        // diagnostic output. Both engines write ALL of it (model load, system info,
                        // VAD, timings) to stderr by design, so it is logged at Debug rather than as a
                        // warning on a healthy run. A genuine failure still surfaces through the
                        // exit-code path below with the full captured stderr. (Issue #95.)
                        if (WhisperProvider.TryParseProgress(e.Data, out var pct))
                        {
                            SubtitleQueueService.Instance.ReportFileProgress(pct);
                        }
                        else
                        {
                            logger.LogDebug("{Engine} stderr: {Error}", engineName, e.Data);
                        }
                    }
                };

                // Reset the per-file progress bar at the start of every engine run. This is the single
                // choke point every transcription and translation path funnels through, reached by
                // both the scheduled task and the manual Generate loop, so the bar never shows the
                // previous run's stale 100%.
                SubtitleQueueService.Instance.ResetFileProgress();

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }

                    // Return whatever partial SRT the engine wrote before being killed.
                    if (File.Exists(tempSrtPath))
                    {
                        var partial = await File.ReadAllTextAsync(tempSrtPath);
                        if (!string.IsNullOrWhiteSpace(partial))
                        {
                            logger.LogInformation("Cancelled — returning partial SRT ({Bytes} bytes)", partial.Length);
                            return partial;
                        }
                    }

                    throw;
                }

                // Flush async stdout/stderr pipe buffers so errorBuilder is complete.
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    throw buildExitException(process.ExitCode, errorBuilder.ToString());
                }

                if (File.Exists(tempSrtPath))
                {
                    var srtContent = await File.ReadAllTextAsync(tempSrtPath, cancellationToken);
                    logger.LogInformation("Successfully generated subtitle file");
                    return srtContent;
                }

                if (alternateSrtPath != null && File.Exists(alternateSrtPath))
                {
                    return await File.ReadAllTextAsync(alternateSrtPath, cancellationToken);
                }

                // An engine can exit 0 having done nothing (whisper-cli's argument parser prints the
                // error and exit(0)s), so its stderr complaint is the only real diagnosis available.
                // Prefer it over the path-not-found message. (Issue #153.)
                var missingOutput = describeMissingOutput(errorBuilder.ToString());
                if (missingOutput != null) throw new InvalidOperationException(missingOutput);

                throw new FileNotFoundException($"Subtitle file not found at expected location: {tempSrtPath}");
            }
            finally
            {
                if (File.Exists(tempSrtPath))
                {
                    try { File.Delete(tempSrtPath); }
                    catch { }
                }
            }
        }
    }
}
