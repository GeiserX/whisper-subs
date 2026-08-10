using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Setup
{
    /// <summary>
    /// Downloads, extracts and validates the standalone BSRoformer.cpp vocal-separation CLI and its
    /// GGUF model, mirroring <see cref="WhisperSetupService"/>'s download/validate/fallback pattern.
    /// Two differences from whisper-cli's own setup: (1) the upstream release is a THIRD PARTY's
    /// (chenmozhijin/BSRoformer.cpp), pinned via <see cref="RoformerCatalog.Version"/>, not this
    /// project's own CI output; (2) the release assets are ARCHIVES (tar.xz/zip), so the binary is
    /// extracted rather than downloaded raw. Progress/lock state is tracked separately from
    /// <see cref="WhisperSetupService"/> so a vocal-separation download never blocks or is confused
    /// with a whisper-cli/model download in the setup UI.
    /// </summary>
    public class VocalSeparationSetupService
    {
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

        public VocalSeparationSetupService(ILogger logger, string dataPath)
        {
            _logger = logger;
            _dataPath = dataPath;
        }

        public string RootDirectory => Path.Combine(_dataPath, "vocal-separation");

        // Extracted fresh on every (re)download so a stale sibling library from a previous variant
        // never lingers next to the new binary; kept separate from ModelsDirectory, which is not touched.
        public string BinDirectory => Path.Combine(RootDirectory, "bin");
        public string ModelsDirectory => Path.Combine(RootDirectory, "models");

        public static string GetPlatformIdentifier() => WhisperSetupService.GetPlatformIdentifier();

        /// <summary>
        /// Searches <see cref="BinDirectory"/> for the extracted bs_roformer-cli executable. The
        /// archive's internal layout isn't guaranteed (some upstream builds nest it in a subfolder
        /// alongside shared libraries the runtime needs), so this is a recursive search rather than a
        /// fixed path — unlike whisper-cli's own statically-linked, flatly-named binary.
        /// </summary>
        public string? FindInstalledBinary()
        {
            var platform = GetPlatformIdentifier();
            var exeName = RoformerCatalog.ExecutableFileName(platform);
            if (!Directory.Exists(BinDirectory)) return null;
            return Directory.GetFiles(BinDirectory, exeName, SearchOption.AllDirectories).FirstOrDefault();
        }

        /// <summary>Checks whether the auto-downloaded binary and model exist, or config already points at valid files.</summary>
        [ExcludeFromCodeCoverage(Justification = "Requires Plugin.Instance (Jellyfin runtime)")]
        public RoformerSetupStatus GetStatus()
        {
            var config = Plugin.Instance.Configuration;
            var autoBinaryPath = FindInstalledBinary();
            var configBinaryValid = !string.IsNullOrEmpty(config.VocalSeparationBinaryPath)
                                    && File.Exists(config.VocalSeparationBinaryPath);

            var configModelValid = !string.IsNullOrEmpty(config.VocalSeparationModelPath)
                                   && File.Exists(config.VocalSeparationModelPath);
            string? autoModelPath = null;
            if (!configModelValid && Directory.Exists(ModelsDirectory))
            {
                autoModelPath = Directory.GetFiles(ModelsDirectory, "*.gguf")
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .FirstOrDefault();
            }

            var binaryOk = autoBinaryPath != null || configBinaryValid;
            var modelOk = autoModelPath != null || configModelValid;

            return new RoformerSetupStatus
            {
                BinaryFound = binaryOk,
                BinaryPath = configBinaryValid ? config.VocalSeparationBinaryPath : autoBinaryPath,
                ModelFound = modelOk,
                ModelPath = configModelValid ? config.VocalSeparationModelPath : autoModelPath,
                Platform = GetPlatformIdentifier(),
                SetupComplete = binaryOk && modelOk,
                InstalledVariant = config.VocalSeparationBinaryVariant,
                Gpu = WhisperSetupService.DetectGpu()
            };
        }

        /// <summary>
        /// Downloads a BSRoformer.cpp GGUF model from HuggingFace and applies it to the config.
        /// Caller must call TryAcquire("roformer-model", ...) first.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "HTTP download + Plugin.Instance")]
        public async Task DownloadModelAsync(string? quantKey, CancellationToken cancellationToken)
        {
            var option = RoformerModelCatalog.Resolve(quantKey);
            try
            {
                Directory.CreateDirectory(ModelsDirectory);

                var url = $"{RoformerModelCatalog.HuggingFaceBaseUrl}/{option.FileName}";
                var destPath = Path.Combine(ModelsDirectory, option.FileName);
                var tempPath = destPath + ".downloading";

                _logger.LogInformation("Downloading vocal-separation model {Model} from {Url}", option.FileName, url);

                using var response = await SharedHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1;

                await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                {
                    var buffer = new byte[81920];
                    long downloaded = 0;
                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        downloaded += bytesRead;
                        if (totalBytes > 0)
                        {
                            var pct = (double)downloaded / totalBytes * 100;
                            var dlMB = downloaded / (1024.0 * 1024.0);
                            var totMB = totalBytes / (1024.0 * 1024.0);
                            lock (_lock)
                            {
                                _progress = pct;
                                _progressMessage = $"Downloading {option.FileName}: {dlMB:F1} / {totMB:F1} MB ({pct:F1}%)";
                            }
                        }
                    }
                    await fileStream.FlushAsync(cancellationToken);
                }

                var actualBytes = new FileInfo(tempPath).Length;
                if (actualBytes < option.SizeMB * 1024.0 * 1024.0 * 0.5)
                {
                    // GGUF quant sizes vary more than whisper's own catalog (metadata overhead is a
                    // larger fraction at these small sizes), so the corruption check uses a looser
                    // 50% floor rather than the 90% used for whisper's larger models.
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    throw new InvalidOperationException(
                        $"Downloaded model is {actualBytes / (1024.0 * 1024.0):F0} MB but expected ~{option.SizeMB} MB. The file may be corrupted or truncated.");
                }

                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tempPath, destPath);

                var sha256 = WhisperSetupService.ComputeSha256(destPath);
                _logger.LogInformation("Vocal-separation model {Model} SHA256: {Hash}", option.FileName, sha256);

                var config = Plugin.Instance.Configuration;
                config.VocalSeparationModelPath = destPath;
                config.VocalSeparationModelQuant = option.Key;
                Plugin.Instance.SaveConfiguration();

                lock (_lock)
                {
                    _progress = 100;
                    _progressMessage = $"Model {option.FileName} downloaded successfully.";
                }
                _logger.LogInformation("Vocal-separation model downloaded to {Path} and config updated", destPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_lock)
                {
                    _error = ex.Message;
                    _progressMessage = $"Error downloading model: {ex.Message}";
                }
                _logger.LogError(ex, "Error downloading vocal-separation model {Model}", option.FileName);
                throw;
            }
            finally
            {
                lock (_lock) { _isRunning = false; }
            }
        }

        /// <summary>
        /// Downloads the bs_roformer-cli archive for <paramref name="variant"/>, extracts it, validates
        /// it launches, and on failure walks <see cref="RoformerCatalog.GetFallbackVariant"/> down to
        /// "cpu". Caller must call TryAcquire("roformer-binary", ...) first; this method owns releasing
        /// that lock.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "HTTP download + Plugin.Instance + process validation")]
        public async Task DownloadBinaryAsync(string variant, CancellationToken cancellationToken)
        {
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
                            _progressMessage = $"bs_roformer-cli downloaded but may not work: {originalError} " +
                                $"(fallback '{currentVariant}' also failed: {ex.Message})";
                        }
                        return;
                    }

                    if (validationError == null)
                    {
                        var config = Plugin.Instance.Configuration;
                        config.VocalSeparationBinaryPath = FindInstalledBinary() ?? "";
                        config.VocalSeparationBinaryVariant = currentVariant;
                        Plugin.Instance.SaveConfiguration();

                        lock (_lock)
                        {
                            _progress = 100;
                            _progressMessage = "bs_roformer-cli downloaded successfully.";
                        }
                        _logger.LogInformation("bs_roformer-cli ({Variant}) downloaded and config updated", currentVariant);
                        return;
                    }

                    _logger.LogWarning("bs_roformer-cli validation warning: {Error}", validationError);
                    originalError ??= validationError;

                    var fallbackVariant = RoformerCatalog.GetFallbackVariant(currentVariant);
                    if (fallbackVariant == null)
                    {
                        lock (_lock)
                        {
                            _progress = 100;
                            _progressMessage = $"bs_roformer-cli downloaded but may not work: {validationError}";
                            _error = validationError;
                        }
                        _logger.LogWarning("bs_roformer-cli downloaded but NOT applied to config: {Error}", validationError);
                        return;
                    }

                    _logger.LogInformation("bs_roformer-cli '{Variant}' failed validation — falling back to {Fallback}", currentVariant, fallbackVariant);
                    lock (_lock)
                    {
                        _progressMessage = $"'{currentVariant}' failed ({validationError}). Downloading {fallbackVariant} fallback...";
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
                    _progressMessage = $"Error downloading bs_roformer-cli: {ex.Message}";
                }
                _logger.LogError(ex, "Error downloading bs_roformer-cli binary");
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
            var assetName = RoformerCatalog.GetAssetName(platform, variant);
            var url = $"{RoformerCatalog.ReleaseBaseUrl}/{assetName}";

            _logger.LogInformation("Downloading bs_roformer-cli from {Url} for platform {Platform}", url, platform);

            Directory.CreateDirectory(RootDirectory);
            var archivePath = Path.Combine(RootDirectory, assetName);

            using (var response = await SharedHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? -1;

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

                var buffer = new byte[81920];
                long downloaded = 0;
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    downloaded += bytesRead;
                    if (totalBytes > 0)
                    {
                        var pct = (double)downloaded / totalBytes * 100;
                        var dlMB = downloaded / (1024.0 * 1024.0);
                        var totMB = totalBytes / (1024.0 * 1024.0);
                        lock (_lock)
                        {
                            _progress = pct;
                            _progressMessage = $"Downloading bs_roformer-cli ({variant}): {dlMB:F1} / {totMB:F1} MB ({pct:F1}%)";
                        }
                    }
                }
                await fileStream.FlushAsync(cancellationToken);

                if (totalBytes > 0 && downloaded != totalBytes)
                {
                    throw new InvalidOperationException($"Download incomplete: received {downloaded} of {totalBytes} bytes.");
                }
            }

            try
            {
                // Wipe any previous extraction so a variant switch never leaves a stale sibling
                // library next to the freshly extracted binary.
                if (Directory.Exists(BinDirectory)) Directory.Delete(BinDirectory, recursive: true);
                Directory.CreateDirectory(BinDirectory);

                await ExtractArchiveAsync(archivePath, BinDirectory, cancellationToken);
            }
            finally
            {
                try { if (File.Exists(archivePath)) File.Delete(archivePath); } catch { /* best-effort cleanup */ }
            }

            var exePath = FindInstalledBinary();
            if (exePath == null)
            {
                var exeName = RoformerCatalog.ExecutableFileName(platform);
                return $"Extracted archive but could not find {exeName} inside it.";
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var chmod = System.Diagnostics.Process.Start("chmod", new[] { "+x", exePath });
                chmod?.WaitForExit(5000);
            }

            var sha256 = WhisperSetupService.ComputeSha256(exePath);
            _logger.LogInformation("bs_roformer-cli ({Variant}) SHA256: {Hash}", variant, sha256);

            return ValidateBinary(exePath, variant);
        }

        /// <summary>
        /// Extracts a downloaded archive into <paramref name="destDir"/>. ZIP uses .NET's built-in
        /// <see cref="ZipFile"/>; tar.xz (the Linux/macOS asset format) has no built-in .NET decoder,
        /// so it shells out to the system's <c>tar</c> (GNU tar auto-detects xz compression from the
        /// file itself, no explicit flag needed) — present on essentially all Debian-based images,
        /// including Jellyfin's official container.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Spawns tar / uses filesystem APIs")]
        private async Task ExtractArchiveAsync(string archivePath, string destDir, CancellationToken cancellationToken)
        {
            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archivePath, destDir, overwriteFiles: true);
                return;
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
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

            using var process = new System.Diagnostics.Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not run 'tar' to extract the bs_roformer-cli archive. Install it in your " +
                    "container (e.g. 'apt-get install -y tar xz-utils').", ex);
            }

            var stderrTask = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                var stderr = await stderrTask;
                throw new InvalidOperationException(
                    $"'tar' failed extracting the bs_roformer-cli archive (exit {process.ExitCode}): {stderr}. " +
                    "Ensure 'xz-utils' is installed in your container (e.g. 'apt-get install -y xz-utils').");
            }
        }

        /// <summary>
        /// Probes the extracted binary to check it can actually launch. Returns null on success, or a
        /// user-friendly error message on failure (e.g. missing shared libraries).
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Spawns binary process for validation")]
        private string? ValidateBinary(string binaryPath, string variant)
        {
            try
            {
                using var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = binaryPath,
                        Arguments = "--help",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(10000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return null; // Timeout is OK — GPU init can be slow, binary exists and launched
                }

                var stderr = stderrTask.GetAwaiter().GetResult();
                _ = stdoutTask.GetAwaiter().GetResult();

                if (process.ExitCode == 127)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        stderr, @"error while loading shared libraries:\s*(\S+?):");
                    var lib = match.Success ? match.Groups[1].Value : "a shared library";
                    var installHint = WhisperSetupService.GetInstallHint(lib);
                    var isCpu = string.Equals(variant, "cpu", StringComparison.OrdinalIgnoreCase);
                    var suggestion = isCpu
                        ? $"Install it in your container ({installHint})."
                        : $"Try the CPU variant, or install the library in your container ({installHint}).";
                    return $"Missing {lib}. {suggestion}";
                }

                if (process.ExitCode == 132 || process.ExitCode == 134 || process.ExitCode == 135)
                {
                    return "The binary crashed on launch (illegal instruction) — this CPU or GPU driver "
                         + "likely doesn't support what this build requires. Falling back to a more compatible build.";
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("bs_roformer-cli validation probe failed: {Error}", ex.Message);
                return null; // Can't probe — don't block the download
            }
        }
    }

    public class RoformerSetupStatus
    {
        public bool BinaryFound { get; set; }
        public string? BinaryPath { get; set; }
        public bool ModelFound { get; set; }
        public string? ModelPath { get; set; }
        public string Platform { get; set; } = "";
        public bool SetupComplete { get; set; }
        public string InstalledVariant { get; set; } = "";
        public GpuInfo Gpu { get; set; } = new();
    }
}
