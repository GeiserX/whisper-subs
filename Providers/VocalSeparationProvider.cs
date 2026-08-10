using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Controller;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Providers
{
    /// <summary>
    /// Runs the standalone BSRoformer.cpp CLI (https://github.com/chenmozhijin/BSRoformer.cpp) to
    /// isolate vocals from an audio file before VAD/transcription. This is an opt-in enhancement
    /// (<see cref="Configuration.PluginConfiguration.EnableVocalSeparation"/>): every method here is
    /// fail-soft — a missing binary/model or a process failure returns <c>false</c> rather than
    /// throwing, so the caller can fall back to transcribing the original, unseparated audio instead
    /// of failing the whole subtitle job over an optional quality enhancement.
    /// </summary>
    public class VocalSeparationProvider
    {
        // BSRoformer.cpp requires 44.1 kHz input (see README); mono is auto-expanded, so the plugin
        // extracts mono to keep the file small. 16-bit PCM -> 2 bytes/sample.
        internal const int RequiredSampleRate = 44100;
        internal const int MaximumOverlap = 8;
        private const double BytesPerSecondMono16Bit = RequiredSampleRate * 2.0;

        private readonly ILogger _logger;
        private readonly string _binaryPath;
        private readonly string _modelPath;
        private readonly int _overlap;
        private readonly int _chunkSize;
        private readonly double _realtimeFactor;
        private readonly int _minTimeoutSeconds;
        private readonly int _maxTimeoutHours;

        public VocalSeparationProvider(
            ILogger logger,
            string binaryPath,
            string modelPath,
            int overlap,
            int chunkSize,
            double realtimeFactor = 6.0,
            int minTimeoutSeconds = 60,
            int maxTimeoutHours = 12)
        {
            _logger = logger;
            _binaryPath = binaryPath ?? "";
            _modelPath = modelPath ?? "";
            _overlap = NormalizeOverlap(overlap);
            _chunkSize = chunkSize;
            _realtimeFactor = realtimeFactor;
            _minTimeoutSeconds = minTimeoutSeconds;
            _maxTimeoutHours = maxTimeoutHours;
        }

        internal static int NormalizeOverlap(int overlap)
            => overlap is >= 1 and <= MaximumOverlap ? overlap : 0;

        /// <summary>True when both the binary and model are configured and present on disk.</summary>
        public bool IsConfigured =>
            !string.IsNullOrEmpty(_binaryPath) && File.Exists(_binaryPath)
            && !string.IsNullOrEmpty(_modelPath) && File.Exists(_modelPath);

        /// <summary>
        /// Runs vocal separation on <paramref name="inputWavPath"/> (must be 44.1 kHz PCM — see
        /// <see cref="RequiredSampleRate"/>), writing the isolated vocal track to
        /// <paramref name="outputWavPath"/>. Returns true on success (output file written and
        /// non-empty); returns false — logging a warning, never throwing — on any failure so the
        /// caller can fall back to the original audio.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Spawns bs_roformer-cli process")]
        public async Task<bool> SeparateAsync(string inputWavPath, string outputWavPath, CancellationToken cancellationToken)
        {
            if (!IsConfigured)
            {
                _logger.LogDebug("Vocal separation skipped: binary or model not configured/found.");
                return false;
            }

            if (!File.Exists(inputWavPath))
            {
                _logger.LogWarning("Vocal separation skipped: input audio not found at {Path}", inputWavPath);
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _binaryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            RoformerRuntime.ConfigureLibraryPath(startInfo, _binaryPath);

            startInfo.ArgumentList.Add(_modelPath);
            startInfo.ArgumentList.Add(inputWavPath);
            startInfo.ArgumentList.Add(outputWavPath);
            if (_overlap > 0)
            {
                startInfo.ArgumentList.Add("--overlap");
                startInfo.ArgumentList.Add(_overlap.ToString(CultureInfo.InvariantCulture));
            }
            if (_chunkSize > 0)
            {
                startInfo.ArgumentList.Add("--chunk-size");
                startInfo.ArgumentList.Add(_chunkSize.ToString(CultureInfo.InvariantCulture));
            }

            try
            {
                var audioBytes = new FileInfo(inputWavPath).Length;
                var deadline = TranscriptionTimeout.Compute(
                    audioBytes, _realtimeFactor, _minTimeoutSeconds, _maxTimeoutHours, BytesPerSecondMono16Bit);

                _logger.LogInformation("Running vocal separation: {Path} {Arguments}", _binaryPath,
                    string.Join(" ", startInfo.ArgumentList));

                using var process = new Process { StartInfo = startInfo };
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(deadline);

                var stderrBuilder = new System.Text.StringBuilder();
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        stderrBuilder.AppendLine(e.Data);
                        _logger.LogDebug("bs_roformer-cli: {Output}", e.Data);
                    }
                };

                process.Start();
                process.BeginErrorReadLine();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();

                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    await TerminateProcessAsync(process, stdoutTask);
                    if (cancellationToken.IsCancellationRequested) throw;
                    _logger.LogWarning("Vocal separation timed out after {Deadline}; continuing with original audio.", deadline);
                    return false;
                }

                await stdoutTask;
                process.WaitForExit(); // flush asynchronous stderr events before reading the builder

                if (process.ExitCode != 0)
                {
                    _logger.LogWarning("Vocal separation failed (exit {Code}): {Error}. Continuing with original audio.",
                        process.ExitCode, stderrBuilder.ToString());
                    return false;
                }

                if (!File.Exists(outputWavPath) || new FileInfo(outputWavPath).Length == 0)
                {
                    _logger.LogWarning("Vocal separation produced no output. Continuing with original audio.");
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vocal separation errored. Continuing with original audio.");
                return false;
            }
        }

        private static async Task TerminateProcessAsync(Process process, Task stdoutTask)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }

            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(exitCts.Token); } catch { /* bounded best-effort reap */ }
            try { await stdoutTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { /* stream closes with process */ }
            if (process.HasExited) process.WaitForExit();
        }
    }
}
