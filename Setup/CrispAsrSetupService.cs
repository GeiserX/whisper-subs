using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WhisperSubs.Controller.Workers;

namespace WhisperSubs.Setup
{
    /// <summary>
    /// Downloads, extracts and validates the CrispASR binary and the Canary GGUF model used for the
    /// non-English translation targets. Mirrors <see cref="VocalSeparationSetupService"/> (a pinned
    /// third-party release archive, own progress/lock state) and reuses its static download helpers.
    /// Everything lives under <c>&lt;data&gt;/crispasr/</c>: <c>bin/</c> for the extracted archive,
    /// <c>models/</c> for the GGUF, <c>cache/</c> for whatever the binary fetches on its own.
    /// Validation runs <c>--help</c>, then a real Canary inference on one second of silence when a
    /// model is present, because <c>--help</c> succeeds on a GPU build that has no usable device.
    /// </summary>
    public class CrispAsrSetupService
    {
        /// <summary>How long the one-second inference probe may take (model load dominates).</summary>
        internal static readonly TimeSpan InferenceProbeTimeout = TimeSpan.FromMinutes(3);

        private readonly ILogger _logger;
        private readonly string _dataPath;

        private static readonly HttpClient SharedHttpClient = CreateHttpClient();

        private static string _currentOperation = "";
        private static double _progress;
        private static string _progressMessage = "";
        private static bool _isRunning;
        private static string? _error;
        private static readonly object _lock = new();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WhisperSubs-Jellyfin-Plugin");
            return client;
        }

        public static DownloadProgress CurrentProgress
        {
            get
            {
                lock (_lock)
                {
                    return new DownloadProgress
                    {
                        Operation = _currentOperation,
                        Percent = _progress,
                        Message = _progressMessage,
                        IsRunning = _isRunning,
                        Error = _error
                    };
                }
            }
        }

        /// <summary>Atomically acquires the download lock. Returns false if a download is already running.</summary>
        public static bool TryAcquire(string operation, string initialMessage)
        {
            lock (_lock)
            {
                if (_isRunning) return false;
                _isRunning = true;
                _error = null;
                _currentOperation = operation;
                _progress = 0;
                _progressMessage = initialMessage;
                return true;
            }
        }

        public CrispAsrSetupService(ILogger logger, string dataPath)
        {
            _logger = logger;
            _dataPath = dataPath;
        }

        public string RootDirectory => Path.Combine(_dataPath, "crispasr");

        /// <summary>Extracted fresh on every (re)download so no library from a previous variant lingers.</summary>
        public string BinDirectory => Path.Combine(RootDirectory, "bin");
        public string ModelsDirectory => Path.Combine(RootDirectory, "models");

        /// <summary>
        /// Passed as <c>--cache-dir</c> on every run: the binary fetches companion models on its own,
        /// even without <c>--auto-download</c>, and this keeps that inside the managed tree.
        /// </summary>
        public string CacheDirectory => Path.Combine(RootDirectory, "cache");

        public static string GetPlatformIdentifier() => WhisperSetupService.GetPlatformIdentifier();

        /// <summary>Finds the extracted crispasr executable (the archive nests it in a folder).</summary>
        public string? FindInstalledBinary() => FindInstalledBinary(BinDirectory, GetPlatformIdentifier());

        internal static string? FindInstalledBinary(string directory, string platform)
        {
            var exeName = CrispAsrCatalog.ExecutableFileName(platform);
            if (!Directory.Exists(directory)) return null;
            return Directory.GetFiles(directory, exeName, SearchOption.AllDirectories).FirstOrDefault();
        }

        /// <summary>
        /// Maps an installed model path back to its catalog key by file name, or "" when the file is
        /// not one of <see cref="CanaryCatalog.Models"/> (a manual override).
        /// </summary>
        internal static string InstalledModelKey(string? modelPath)
        {
            if (string.IsNullOrEmpty(modelPath)) return "";
            var fileName = Path.GetFileName(modelPath);
            return Array.Find(CanaryCatalog.Models, m => string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase))?.Key ?? "";
        }

        /// <summary>Reports whether the configured binary and model exist.</summary>
        [ExcludeFromCodeCoverage(Justification = "Requires Plugin.Instance (Jellyfin runtime)")]
        public CrispAsrSetupStatus GetStatus()
        {
            var config = Plugin.Instance.Configuration;
            var binaryOk = !string.IsNullOrEmpty(config.CrispAsrBinaryPath) && File.Exists(config.CrispAsrBinaryPath);
            var modelOk = !string.IsNullOrEmpty(config.CanaryModelPath) && File.Exists(config.CanaryModelPath);

            return new CrispAsrSetupStatus
            {
                BinaryFound = binaryOk,
                BinaryPath = binaryOk ? config.CrispAsrBinaryPath : null,
                ModelFound = modelOk,
                ModelPath = modelOk ? config.CanaryModelPath : null,
                Platform = GetPlatformIdentifier(),
                SetupComplete = binaryOk && modelOk,
                InstalledVariant = config.CrispAsrBinaryVariant,
                InstalledVersion = config.CrispAsrBinaryVersion,
                PinnedVersion = CrispAsrCatalog.Version,
                InstalledModelQuant = modelOk ? InstalledModelKey(config.CanaryModelPath) : "",
                Gpu = WhisperSetupService.DetectGpu()
            };
        }

        /// <summary>
        /// Downloads a Canary GGUF model from Hugging Face, verifies size, SHA-256 and GGUF magic,
        /// promotes it and applies it to the config. When a binary is already installed, it then runs
        /// the inference probe so a GPU build that cannot run the model is reported now, not on the
        /// first real job. Caller must call TryAcquire("canary-model", ...) first.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "HTTP download + Plugin.Instance + process validation")]
        public async Task DownloadModelAsync(string? quantKey, CancellationToken cancellationToken)
        {
            var option = CanaryCatalog.Resolve(quantKey);
            var destPath = Path.Combine(ModelsDirectory, option.FileName);
            var tempPath = destPath + ".downloading";
            try
            {
                Directory.CreateDirectory(ModelsDirectory);
                var url = $"{CanaryCatalog.HuggingFaceBaseUrl}/{option.FileName}";
                _logger.LogInformation("Downloading Canary model {Model} from {Url}", option.FileName, url);

                using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                downloadCts.CancelAfter(TimeSpan.FromHours(2));
                var downloadToken = downloadCts.Token;

                try
                {
                    await DownloadToFileAsync(url, tempPath, option.SizeBytes, option.FileName, downloadToken);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The Canary model download did not finish within 2 hours.", ex);
                }

                VocalSeparationSetupService.VerifySha256(tempPath, option.Sha256, option.FileName);
                VocalSeparationSetupService.VerifyGgufMagic(tempPath, option.FileName);

                var modelBackupPath = VocalSeparationSetupService.PromoteDownloadedFile(tempPath, destPath);

                var config = Plugin.Instance.Configuration;
                var previousModelPath = config.CanaryModelPath;
                try
                {
                    config.CanaryModelPath = destPath;
                    Plugin.Instance.SaveConfiguration();
                    VocalSeparationSetupService.CompleteDownloadedFilePromotion(modelBackupPath);
                }
                catch (Exception configurationError)
                {
                    config.CanaryModelPath = previousModelPath;
                    try
                    {
                        VocalSeparationSetupService.RollbackDownloadedFilePromotion(destPath, modelBackupPath);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new AggregateException(
                            "Failed to save model configuration and restore the previous model.",
                            configurationError,
                            rollbackError);
                    }
                    throw;
                }

                _logger.LogInformation("Canary model downloaded to {Path} and config updated", destPath);

                var binaryPath = config.CrispAsrBinaryPath;
                if (!string.IsNullOrEmpty(binaryPath) && File.Exists(binaryPath))
                {
                    lock (_lock) { _progressMessage = "Checking that crispasr can run the model..."; }
                    var probeError = await RunInferenceProbeAsync(binaryPath, destPath, config.CrispAsrBinaryVariant, cancellationToken);
                    if (probeError != null)
                    {
                        var message = $"Model {option.FileName} is installed, but the installed crispasr build could not run it: {probeError} " +
                                      "Download the binary again and pick the CPU variant.";
                        lock (_lock)
                        {
                            _progress = 100;
                            _error = message;
                            _progressMessage = message;
                        }
                        _logger.LogWarning("Canary inference probe failed after model download: {Error}", probeError);
                        return;
                    }
                }

                lock (_lock)
                {
                    _progress = 100;
                    _progressMessage = $"Model {option.FileName} downloaded successfully.";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_lock)
                {
                    _error = ex.Message;
                    _progressMessage = $"Error downloading model: {ex.Message}";
                }
                _logger.LogError(ex, "Error downloading Canary model {Model}", option.FileName);
                throw;
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort cleanup */ }
                lock (_lock) { _isRunning = false; }
            }
        }

        /// <summary>
        /// Downloads the crispasr archive for <paramref name="variant"/>, extracts, validates and
        /// installs it; on a validation failure walks <see cref="CrispAsrCatalog.GetFallbackVariant"/>.
        /// The previous install is kept whenever nothing validates. Caller must call
        /// TryAcquire("crispasr-binary", ...) first; this method owns releasing that lock.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "HTTP download + Plugin.Instance + process validation")]
        public async Task DownloadBinaryAsync(string variant, CancellationToken cancellationToken)
        {
            var platform = GetPlatformIdentifier();
            try
            {
                var currentVariant = variant;
                string? originalError = null;

                while (true)
                {
                    string? validationError;
                    try
                    {
                        validationError = await DownloadAndValidateVariantAsync(currentVariant, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && originalError != null)
                    {
                        _logger.LogError(ex, "Fallback download '{Variant}' failed; surfacing original validation error", currentVariant);
                        lock (_lock)
                        {
                            _progress = 100;
                            _error = originalError;
                            _progressMessage = $"crispasr could not be validated; the previous installation was preserved: {originalError} " +
                                $"(fallback '{currentVariant}' also failed: {ex.Message})";
                        }
                        return;
                    }

                    if (validationError == null)
                    {
                        lock (_lock)
                        {
                            _progress = 100;
                            _progressMessage = currentVariant == variant
                                ? "crispasr downloaded successfully."
                                : $"crispasr '{variant}' failed validation ({originalError}); installed the '{currentVariant}' build instead.";
                        }
                        _logger.LogInformation("crispasr ({Variant}) downloaded and config updated", currentVariant);
                        return;
                    }

                    _logger.LogWarning("crispasr validation failed: {Error}", validationError);
                    originalError ??= validationError;

                    var fallbackVariant = CrispAsrCatalog.GetFallbackVariant(platform, currentVariant);
                    if (fallbackVariant == null)
                    {
                        lock (_lock)
                        {
                            _progress = 100;
                            _progressMessage = $"crispasr could not be validated; the previous installation was preserved: {validationError}";
                            _error = validationError;
                        }
                        return;
                    }

                    _logger.LogInformation("crispasr '{Variant}' failed validation, trying {Fallback}", currentVariant, fallbackVariant);
                    lock (_lock)
                    {
                        _progressMessage = $"'{currentVariant}' failed ({validationError}). Downloading {fallbackVariant} instead...";
                        _error = null;
                    }
                    currentVariant = fallbackVariant;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_lock)
                {
                    _error = ex.Message;
                    _progressMessage = $"Error downloading crispasr: {ex.Message}";
                }
                _logger.LogError(ex, "Error downloading crispasr binary");
                throw;
            }
            finally
            {
                lock (_lock) { _isRunning = false; }
            }
        }

        [ExcludeFromCodeCoverage(Justification = "HTTP download + archive extraction + process validation")]
        private async Task<string?> DownloadAndValidateVariantAsync(string variant, CancellationToken cancellationToken)
        {
            var platform = GetPlatformIdentifier();
            var assetName = CrispAsrCatalog.GetAssetName(platform, variant);
            var url = $"{CrispAsrCatalog.ReleaseBaseUrl}/{assetName}";
            _logger.LogInformation("Downloading crispasr from {Url} for platform {Platform}", url, platform);

            Directory.CreateDirectory(RootDirectory);
            var archivePath = Path.Combine(RootDirectory, assetName + ".downloading");
            var stagingDirectory = Path.Combine(RootDirectory, "bin.staging-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    downloadCts.CancelAfter(TimeSpan.FromHours(2));
                    try
                    {
                        await DownloadToFileAsync(url, archivePath, CrispAsrCatalog.GetAssetSizeBytes(platform, variant), assetName, downloadCts.Token);
                    }
                    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException("The crispasr download did not finish within 2 hours.", ex);
                    }
                }

                VocalSeparationSetupService.VerifySha256(archivePath, CrispAsrCatalog.GetAssetSha256(platform, variant), assetName);

                Directory.CreateDirectory(stagingDirectory);
                lock (_lock) { _progressMessage = $"Extracting {assetName}..."; }
                await ExtractArchiveAsync(archivePath, assetName, stagingDirectory, cancellationToken);

                var stagedExePath = FindInstalledBinary(stagingDirectory, platform);
                if (stagedExePath == null)
                {
                    return $"Extracted archive but could not find {CrispAsrCatalog.ExecutableFileName(platform)} inside it.";
                }

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    using var chmod = Process.Start("chmod", new[] { "+x", stagedExePath });
                    if (chmod == null || !chmod.WaitForExit(5000) || chmod.ExitCode != 0)
                    {
                        try { chmod?.Kill(entireProcessTree: true); } catch { }
                        return "Could not mark crispasr as executable.";
                    }
                }

                lock (_lock) { _progressMessage = $"Validating crispasr ({variant})..."; }
                var validationError = await ValidateBinaryAsync(stagedExePath, variant, cancellationToken);
                if (validationError != null) return validationError;

                var binaryBackupDirectory = PromoteStagedDirectory(stagingDirectory);
                var config = Plugin.Instance.Configuration;
                var previousBinaryPath = config.CrispAsrBinaryPath;
                var previousBinaryVariant = config.CrispAsrBinaryVariant;
                var previousBinaryVersion = config.CrispAsrBinaryVersion;
                try
                {
                    config.CrispAsrBinaryPath = FindInstalledBinary()
                        ?? throw new InvalidOperationException("Installed crispasr could not be located after promotion.");
                    config.CrispAsrBinaryVariant = variant;
                    config.CrispAsrBinaryVersion = CrispAsrCatalog.Version;
                    Plugin.Instance.SaveConfiguration();
                    CompleteDirectoryPromotion(binaryBackupDirectory);
                }
                catch (Exception configurationError)
                {
                    config.CrispAsrBinaryPath = previousBinaryPath;
                    config.CrispAsrBinaryVariant = previousBinaryVariant;
                    config.CrispAsrBinaryVersion = previousBinaryVersion;
                    try
                    {
                        RollbackDirectoryPromotion(binaryBackupDirectory);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new AggregateException(
                            "Failed to save binary configuration and restore the previous installation.",
                            configurationError,
                            rollbackError);
                    }
                    throw;
                }
                return null;
            }
            finally
            {
                try { if (File.Exists(archivePath)) File.Delete(archivePath); } catch { /* best-effort cleanup */ }
                try { if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>Streams <paramref name="url"/> to <paramref name="destPath"/>, enforcing the pinned size and reporting progress.</summary>
        [ExcludeFromCodeCoverage(Justification = "HTTP download")]
        private async Task DownloadToFileAsync(string url, string destPath, long expectedBytes, string assetName, CancellationToken cancellationToken)
        {
            using var response = await SharedHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            VocalSeparationSetupService.ValidateContentLength(totalBytes, expectedBytes, assetName);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

            var buffer = new byte[81920];
            long downloaded = 0;
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                VocalSeparationSetupService.EnsureDownloadSize(downloaded + bytesRead, expectedBytes, assetName);
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                downloaded += bytesRead;
                var denominator = totalBytes > 0 ? totalBytes : expectedBytes;
                var pct = (double)downloaded / denominator * 100;
                lock (_lock)
                {
                    _progress = pct;
                    _progressMessage = $"Downloading {assetName}: {downloaded / (1024.0 * 1024.0):F1} / {denominator / (1024.0 * 1024.0):F1} MB ({pct:F1}%)";
                }
            }
            await fileStream.FlushAsync(cancellationToken);

            if (downloaded != expectedBytes)
            {
                throw new InvalidOperationException($"Download incomplete: received {downloaded} of {expectedBytes} bytes.");
            }
        }

        /// <summary>
        /// Extracts a downloaded archive. ZIP uses <see cref="ZipFile"/>; tar.gz goes through the
        /// system <c>tar</c>, which detects the compression itself.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Spawns tar / uses filesystem APIs")]
        private static async Task ExtractArchiveAsync(string archivePath, string sourceAssetName, string destDir, CancellationToken cancellationToken)
        {
            if (VocalSeparationSetupService.IsZipArchiveName(sourceAssetName))
            {
                ZipFile.ExtractToDirectory(archivePath, destDir, overwriteFiles: true);
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "tar",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-xf");
            startInfo.ArgumentList.Add(archivePath);
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(destDir);

            var result = await RunProcessAsync(startInfo, TimeSpan.FromMinutes(10), cancellationToken,
                "Could not run 'tar' to extract the crispasr archive. Install it in your container (e.g. 'apt-get install -y tar gzip').");
            if (result.TimedOut)
            {
                throw new InvalidOperationException("'tar' did not finish extracting the crispasr archive within 10 minutes and was stopped.");
            }
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"'tar' failed extracting the crispasr archive (exit {result.ExitCode}): {result.Stderr}");
            }
        }

        /// <summary>
        /// Validates a staged binary: <c>--help</c> must exit 0, and when a Canary model is already
        /// configured, a one-second inference on silence must exit 0 too. Returns null on success or a
        /// user-facing reason.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Spawns the binary for validation")]
        internal async Task<string?> ValidateBinaryAsync(string binaryPath, string variant, CancellationToken cancellationToken)
        {
            var help = new ProcessStartInfo
            {
                FileName = binaryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            help.ArgumentList.Add("--help");
            try
            {
                var result = await RunProcessAsync(help, TimeSpan.FromSeconds(30), cancellationToken, "Could not launch crispasr.");
                if (result.TimedOut) return "crispasr did not respond to --help within 30 seconds.";
                var failure = DescribeProbeFailure(result.ExitCode, result.Stderr, variant, "--help");
                if (failure != null) return failure;
            }
            catch (InvalidOperationException ex)
            {
                return $"Could not launch crispasr for validation: {ex.InnerException?.Message ?? ex.Message}";
            }

            var modelPath = Plugin.Instance?.Configuration?.CanaryModelPath;
            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            {
                _logger.LogInformation("No Canary model installed yet; crispasr validated with --help only");
                return null;
            }

            return await RunInferenceProbeAsync(binaryPath, modelPath, variant, cancellationToken);
        }

        /// <summary>
        /// Runs a real one-second Canary inference on silence with the pinned cache directory, so a
        /// GPU build without a usable device fails here instead of on the first job, and so the probe
        /// cannot fetch anything outside the managed tree. Exit 0 with any output (an empty SRT is
        /// normal for silence) is a pass.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Spawns the binary for validation")]
        internal async Task<string?> RunInferenceProbeAsync(string binaryPath, string modelPath, string variant, CancellationToken cancellationToken)
        {
            var probeDirectory = Path.Combine(RootDirectory, "probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(probeDirectory);
                Directory.CreateDirectory(CacheDirectory);
                var wavPath = Path.Combine(probeDirectory, "silence.wav");
                await File.WriteAllBytesAsync(wavPath, SyntheticAudio.SilentWav16kMono(1000), cancellationToken);

                var startInfo = new ProcessStartInfo
                {
                    FileName = binaryPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = probeDirectory
                };
                foreach (var arg in BuildValidationArguments(modelPath, wavPath, CacheDirectory, Path.Combine(probeDirectory, "out")))
                {
                    startInfo.ArgumentList.Add(arg);
                }

                _logger.LogInformation("Validating crispasr with a Canary inference: {Binary} {Arguments}",
                    binaryPath, string.Join(" ", startInfo.ArgumentList));

                var result = await RunProcessAsync(startInfo, InferenceProbeTimeout, cancellationToken, "Could not launch crispasr.");
                if (result.TimedOut)
                {
                    return $"crispasr did not finish a one-second Canary inference within {InferenceProbeTimeout.TotalMinutes:F0} minutes.";
                }
                return DescribeProbeFailure(result.ExitCode, result.Stderr, variant, "a Canary inference on silence");
            }
            catch (InvalidOperationException ex)
            {
                return $"Could not launch crispasr for validation: {ex.InnerException?.Message ?? ex.Message}";
            }
            finally
            {
                try { if (Directory.Exists(probeDirectory)) Directory.Delete(probeDirectory, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// The validation inference command line. Same engine flags as a real Canary run (backend,
        /// explicit source and target, no auto-aligner, pinned cache) so validation exercises the
        /// path jobs will take, minus VAD and progress. <c>-l en</c> is needed on top of <c>-sl en</c>:
        /// with <c>-l</c> left at its default of auto, crispasr v0.8.35 downloads Whisper tiny into
        /// the cache for language identification even when the source language is given.
        /// </summary>
        internal static IReadOnlyList<string> BuildValidationArguments(string modelPath, string wavPath, string cacheDirectory, string outputPrefix)
            => new[]
            {
                "--backend", "canary",
                "-m", modelPath,
                "-f", wavPath,
                "-l", "en",
                "-sl", "en",
                "-tl", "es",
                "--no-auto-aligner",
                "--cache-dir", cacheDirectory,
                "-osrt",
                "-of", outputPrefix,
            };

        /// <summary>
        /// Maps a validation probe's exit code and stderr to a user-facing reason, or null on exit 0.
        /// Pure so the mapping is unit-testable.
        /// </summary>
        internal static string? DescribeProbeFailure(int exitCode, string? stderr, string variant, string probeName)
        {
            if (exitCode == 0) return null;
            var text = stderr ?? "";
            var isCpu = variant.StartsWith("cpu", StringComparison.OrdinalIgnoreCase);

            var library = Regex.Match(text, @"(?:error while loading shared libraries:\s*|Library not loaded:\s*)(\S+?)(?::|\r?$)", RegexOptions.Multiline);
            if (exitCode == 127 || library.Success)
            {
                var lib = library.Success ? library.Groups[1].Value : "a shared library";
                var installHint = WhisperSetupService.GetInstallHint(lib);
                return isCpu
                    ? $"Missing {lib}. Install it in your container ({installHint})."
                    : $"Missing {lib}. Try the CPU variant, or install the library in your container ({installHint}).";
            }

            if (exitCode == 132 || exitCode == 134 || exitCode == 135)
            {
                return $"crispasr crashed during {probeName} (exit {exitCode}). This CPU, GPU driver or native library "
                     + "likely does not support the selected build.";
            }

            var detail = text.Trim();
            if (detail.Length > 500) detail = detail[^500..];
            return $"crispasr failed {probeName} with exit code {exitCode}" + (detail.Length == 0 ? "." : $": {detail}");
        }

        private readonly record struct ProcessResult(int ExitCode, string Stderr, bool TimedOut);

        [ExcludeFromCodeCoverage(Justification = "Spawns a process")]
        private static async Task<ProcessResult> RunProcessAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken, string launchError)
        {
            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(launchError, ex);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit(5000);
                if (cancellationToken.IsCancellationRequested) throw;
                return new ProcessResult(-1, "", TimedOut: true);
            }

            _ = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessResult(process.ExitCode, stderr, TimedOut: false);
        }

        internal string? PromoteStagedDirectory(string stagingDirectory)
        {
            var backupDirectory = BinDirectory + ".backup-" + Guid.NewGuid().ToString("N");
            var hadPreviousInstall = Directory.Exists(BinDirectory);
            if (hadPreviousInstall) Directory.Move(BinDirectory, backupDirectory);

            try
            {
                Directory.Move(stagingDirectory, BinDirectory);
            }
            catch (Exception promotionError)
            {
                if (hadPreviousInstall && Directory.Exists(backupDirectory) && !Directory.Exists(BinDirectory))
                {
                    try
                    {
                        Directory.Move(backupDirectory, BinDirectory);
                    }
                    catch (Exception restoreError)
                    {
                        throw new AggregateException("Failed to install crispasr and restore the previous installation.", promotionError, restoreError);
                    }
                }
                throw;
            }

            return hadPreviousInstall ? backupDirectory : null;
        }

        internal void CompleteDirectoryPromotion(string? backupDirectory)
        {
            if (backupDirectory == null || !Directory.Exists(backupDirectory)) return;
            try
            {
                Directory.Delete(backupDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove previous crispasr backup at {Path}", backupDirectory);
            }
        }

        internal void RollbackDirectoryPromotion(string? backupDirectory)
        {
            try
            {
                if (Directory.Exists(BinDirectory)) Directory.Delete(BinDirectory, recursive: true);
                if (backupDirectory != null && Directory.Exists(backupDirectory))
                {
                    Directory.Move(backupDirectory, BinDirectory);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to restore the previous crispasr installation.", ex);
            }
        }
    }

    public class CrispAsrSetupStatus
    {
        public bool BinaryFound { get; set; }
        public string? BinaryPath { get; set; }
        public bool ModelFound { get; set; }
        public string? ModelPath { get; set; }
        public string Platform { get; set; } = "";
        public bool SetupComplete { get; set; }
        public string InstalledVariant { get; set; } = "";
        public string InstalledVersion { get; set; } = "";
        public string PinnedVersion { get; set; } = "";
        public string InstalledModelQuant { get; set; } = "";
        public GpuInfo Gpu { get; set; } = new();

        /// <summary>The selectable translation targets, so the settings page never keeps its own copy of the list.</summary>
        public IReadOnlyList<CanaryTarget> Targets { get; set; } = CanaryCatalog.Targets;
    }
}
