using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
            _overlap = overlap;
            _chunkSize = chunkSize;
            _realtimeFactor = realtimeFactor;
            _minTimeoutSeconds = minTimeoutSeconds;
            _maxTimeoutHours = maxTimeoutHours;
        }

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
            
            // On Linux, set LD_LIBRARY_PATH to the binary's directory so it can find shipped
            // shared libraries (e.g. libggml.so.0 or libggml.so.0.15.1).
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var binDir = Path.GetDirectoryName(_binaryPath);
                if (!string.IsNullOrEmpty(binDir))
                {
                    var currentLdPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
                    startInfo.Environment["LD_LIBRARY_PATH"] = binDir + (string.IsNullOrEmpty(currentLdPath) ? "" : ":" + currentLdPath);
                }
            }
            
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

            var audioBytes = new FileInfo(inputWavPath).Length;
            var deadline = TranscriptionTimeout.Compute(
                audioBytes, _realtimeFactor, _minTimeoutSeconds, _maxTimeoutHours, BytesPerSecondMono16Bit);

            _logger.LogInformation("Running vocal separation: {Path} {Arguments}", _binaryPath,
                string.Join(" ", startInfo.ArgumentList));

            try
            {
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
                _ = process.StandardOutput.ReadToEndAsync();

                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                    if (cancellationToken.IsCancellationRequested) throw;
                    _logger.LogWarning("Vocal separation timed out after {Deadline}; continuing with original audio.", deadline);
                    return false;
                }

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
    }
}
