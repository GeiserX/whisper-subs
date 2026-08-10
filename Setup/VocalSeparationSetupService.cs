using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WhisperSubs.Providers;

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
            return FindInstalledBinary(BinDirectory, platform);
        }

        /// <summary>Checks whether the auto-downloaded binary and model exist, or config already points at valid files.</summary>
        [ExcludeFromCodeCoverage(Justification = "Requires Plugin.Instance (Jellyfin runtime)")]
        public RoformerSetupStatus GetStatus()
        {
            var config = Plugin.Instance.Configuration;
            var configBinaryValid = !string.IsNullOrEmpty(config.VocalSeparationBinaryPath)
                                    && File.Exists(config.VocalSeparationBinaryPath);

            var configModelValid = !string.IsNullOrEmpty(config.VocalSeparationModelPath)
                                   && File.Exists(config.VocalSeparationModelPath);

            // Runtime inference consumes the configured paths, not whichever file happens to be
            // discoverable in the managed directories. Report readiness using that same invariant.
            var binaryOk = configBinaryValid;
            var modelOk = configModelValid;

            return new RoformerSetupStatus
            {
                BinaryFound = binaryOk,
                BinaryPath = configBinaryValid ? config.VocalSeparationBinaryPath : null,
                ModelFound = modelOk,
                ModelPath = configModelValid ? config.VocalSeparationModelPath : null,
                Platform = GetPlatformIdentifier(),
                SetupComplete = binaryOk && modelOk,
                InstalledVariant = config.VocalSeparationBinaryVariant,
                InstalledModelQuant = configModelValid ? config.VocalSeparationModelQuant : "",
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
            var destPath = Path.Combine(ModelsDirectory, option.FileName);
            var tempPath = destPath + ".downloading";
            try
            {
                Directory.CreateDirectory(ModelsDirectory);

                var url = $"{RoformerModelCatalog.HuggingFaceBaseUrl}/{option.FileName}";

                _logger.LogInformation("Downloading vocal-separation model {Model} from {Url}", option.FileName, url);

                using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                downloadCts.CancelAfter(TimeSpan.FromHours(2));
                var downloadToken = downloadCts.Token;

                using var response = await SharedHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, downloadToken);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                ValidateContentLength(totalBytes, option.SizeBytes, option.FileName);

                await using (var stream = await response.Content.ReadAsStreamAsync(downloadToken))
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                {
                    var buffer = new byte[81920];
                    long downloaded = 0;
                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer, downloadToken)) > 0)
                    {
                        EnsureDownloadSize(downloaded + bytesRead, option.SizeBytes, option.FileName);
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), downloadToken);
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
                    await fileStream.FlushAsync(downloadToken);

                    if (totalBytes > 0 && downloaded != totalBytes)
                    {
                        throw new InvalidOperationException(
                            $"Download incomplete: received {downloaded} of {totalBytes} bytes.");
                    }
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

                VerifySha256(tempPath, option.Sha256, option.FileName);
                VerifyGgufMagic(tempPath, option.FileName);

                var modelBackupPath = PromoteDownloadedFile(tempPath, destPath);

                var sha256 = WhisperSetupService.ComputeSha256(destPath);
                _logger.LogInformation("Vocal-separation model {Model} SHA256: {Hash}", option.FileName, sha256);

                var config = Plugin.Instance.Configuration;
                var previousModelPath = config.VocalSeparationModelPath;
                var previousModelQuant = config.VocalSeparationModelQuant;
                try
                {
                    config.VocalSeparationModelPath = destPath;
                    config.VocalSeparationModelQuant = option.Key;
                    Plugin.Instance.SaveConfiguration();
                    CompleteDownloadedFilePromotion(modelBackupPath);
                }
                catch (Exception configurationError)
                {
                    config.VocalSeparationModelPath = previousModelPath;
                    config.VocalSeparationModelQuant = previousModelQuant;
                    try
                    {
                        RollbackDownloadedFilePromotion(destPath, modelBackupPath);
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

                lock (_lock)
                {
                    _progress = 100;
                    _progressMessage = $"Model {option.FileName} downloaded successfully.";
                }
                _logger.LogInformation("Vocal-separation model downloaded to {Path} and config updated", destPath);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                var timeout = new TimeoutException("The vocal-separation model download did not finish within 2 hours.", ex);
                lock (_lock)
                {
                    _error = timeout.Message;
                    _progressMessage = $"Error downloading model: {timeout.Message}";
                }
                _logger.LogError(timeout, "Timed out downloading vocal-separation model {Model}", option.FileName);
                throw timeout;
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
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort cleanup */ }
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
                            _progressMessage = $"bs_roformer-cli could not be validated; the previous installation was preserved: {originalError} " +
                                $"(fallback '{currentVariant}' also failed: {ex.Message})";
                        }
                        return;
                    }

                    if (validationError == null)
                    {
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
                            _progressMessage = $"bs_roformer-cli could not be validated; the previous installation was preserved: {validationError}";
                            _error = validationError;
                        }
                        _logger.LogWarning("bs_roformer-cli failed validation and was not installed: {Error}", validationError);
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
            var assetSizeBytes = RoformerCatalog.GetAssetSizeBytes(platform, variant);
            var url = $"{RoformerCatalog.ReleaseBaseUrl}/{assetName}";
            _logger.LogInformation("Downloading bs_roformer-cli from {Url} for platform {Platform}", url, platform);

            Directory.CreateDirectory(RootDirectory);
            var archivePath = Path.Combine(RootDirectory, assetName + ".downloading");
            var stagingDirectory = Path.Combine(RootDirectory, "bin.staging-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                downloadCts.CancelAfter(TimeSpan.FromHours(2));
                var downloadToken = downloadCts.Token;

                try
                {
                    using var response = await SharedHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, downloadToken);
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength ?? -1;
                    ValidateContentLength(totalBytes, assetSizeBytes, assetName);

                    await using var stream = await response.Content.ReadAsStreamAsync(downloadToken);
                    await using var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

                    var buffer = new byte[81920];
                    long downloaded = 0;
                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer, downloadToken)) > 0)
                    {
                        EnsureDownloadSize(downloaded + bytesRead, assetSizeBytes, assetName);
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), downloadToken);
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
                    await fileStream.FlushAsync(downloadToken);

                    if (totalBytes > 0 && downloaded != totalBytes)
                    {
                        throw new InvalidOperationException($"Download incomplete: received {downloaded} of {totalBytes} bytes.");
                    }
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The bs_roformer-cli download did not finish within 2 hours.", ex);
                }

                VerifySha256(archivePath, RoformerCatalog.GetAssetSha256(platform, variant), assetName);

                Directory.CreateDirectory(stagingDirectory);
                await ExtractArchiveAsync(archivePath, assetName, stagingDirectory, cancellationToken);

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    RepairGgmlLibraryLinks(stagingDirectory, platform);
                }

                var stagedExePath = FindInstalledBinary(stagingDirectory, platform);
                if (stagedExePath == null)
                {
                    var exeName = RoformerCatalog.ExecutableFileName(platform);
                    return $"Extracted archive but could not find {exeName} inside it.";
                }

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    using var chmod = Process.Start("chmod", new[] { "+x", stagedExePath });
                    if (chmod == null || !chmod.WaitForExit(5000) || chmod.ExitCode != 0)
                    {
                        try { chmod?.Kill(entireProcessTree: true); } catch { }
                        return "Could not mark bs_roformer-cli as executable.";
                    }
                }

                var binarySha256 = WhisperSetupService.ComputeSha256(stagedExePath);
                _logger.LogInformation("bs_roformer-cli ({Variant}) SHA256: {Hash}", variant, binarySha256);

                var validationError = ValidateBinary(stagedExePath, variant);
                if (validationError != null) return validationError;

                var binaryBackupDirectory = PromoteStagedDirectory(stagingDirectory);
                var config = Plugin.Instance.Configuration;
                var previousBinaryPath = config.VocalSeparationBinaryPath;
                var previousBinaryVariant = config.VocalSeparationBinaryVariant;
                try
                {
                    config.VocalSeparationBinaryPath = FindInstalledBinary()
                        ?? throw new InvalidOperationException("Installed bs_roformer-cli could not be located after promotion.");
                    config.VocalSeparationBinaryVariant = variant;
                    Plugin.Instance.SaveConfiguration();
                    CompleteDirectoryPromotion(binaryBackupDirectory);
                }
                catch (Exception configurationError)
                {
                    config.VocalSeparationBinaryPath = previousBinaryPath;
                    config.VocalSeparationBinaryVariant = previousBinaryVariant;
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

        /// <summary>
        /// Extracts a downloaded archive into <paramref name="destDir"/>. ZIP uses .NET's built-in
        /// <see cref="ZipFile"/>; tar.xz (the Linux/macOS asset format) has no built-in .NET decoder,
        /// so it shells out to the system's <c>tar</c> (GNU tar auto-detects xz compression from the
        /// file itself, no explicit flag needed) — present on essentially all Debian-based images,
        /// including Jellyfin's official container.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Spawns tar / uses filesystem APIs")]
        private async Task ExtractArchiveAsync(
            string archivePath,
            string sourceAssetName,
            string destDir,
            CancellationToken cancellationToken)
        {
            if (IsZipArchiveName(sourceAssetName))
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
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            using (var extractCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                extractCts.CancelAfter(TimeSpan.FromMinutes(10));
                try
                {
                    await process.WaitForExitAsync(extractCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    process.WaitForExit(5000);
                    throw new InvalidOperationException(
                        "'tar' did not finish extracting the bs_roformer-cli archive within 10 minutes and was stopped.");
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    process.WaitForExit(5000);
                    throw;
                }
            }

            _ = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
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
                var startInfo = new ProcessStartInfo
                {
                    FileName = binaryPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("--help");
                RoformerRuntime.ConfigureLibraryPath(startInfo, binaryPath);

                using var process = new Process { StartInfo = startInfo };

                process.Start();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(10000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    process.WaitForExit(5000);
                    return "bs_roformer-cli did not respond to --help within 10 seconds.";
                }

                var stderr = stderrTask.GetAwaiter().GetResult();
                _ = stdoutTask.GetAwaiter().GetResult();

                if (process.ExitCode == 0) return null;

                if (process.ExitCode == 127 || stderr.Contains("Library not loaded", StringComparison.OrdinalIgnoreCase))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        stderr, @"(?:error while loading shared libraries:\s*|Library not loaded:\s*)(\S+?)(?::|\r?$)",
                        System.Text.RegularExpressions.RegexOptions.Multiline);
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
                    return $"The binary crashed on launch (exit {process.ExitCode}). This CPU, GPU driver, or native library "
                         + "likely doesn't support the selected build. Falling back to a more compatible build.";
                }

                var detail = stderr.Trim();
                if (detail.Length > 500) detail = detail[..500] + "…";
                return $"bs_roformer-cli --help exited with code {process.ExitCode}"
                     + (string.IsNullOrEmpty(detail) ? "." : $": {detail}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "bs_roformer-cli validation probe failed");
                return $"Could not launch bs_roformer-cli for validation: {ex.Message}";
            }
        }

        /// <summary>
        /// Creates the SONAME aliases omitted from upstream v0.1.0 archives for every packaged GGML
        /// component (base, CPU, Vulkan, and so on), on Linux and macOS.
        /// </summary>
        internal void RepairGgmlLibraryLinks(string binDirectory, string platform)
        {
            if (!Directory.Exists(binDirectory)) return;

            foreach (var targetFile in Directory.GetFiles(binDirectory, "libggml*", SearchOption.AllDirectories))
            {
                var linkName = GetGgmlSonameLinkName(Path.GetFileName(targetFile), platform);
                if (linkName == null) continue;

                var linkPath = Path.Combine(Path.GetDirectoryName(targetFile) ?? binDirectory, linkName);
                try
                {
                    if (!File.Exists(linkPath))
                    {
                        File.CreateSymbolicLink(linkPath, Path.GetFileName(targetFile));
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.Copy(targetFile, linkPath, overwrite: false);
                        _logger.LogWarning(ex,
                            "Could not create GGML library link {Link}; copied {Target} instead", linkPath, targetFile);
                    }
                    catch (Exception copyError)
                    {
                        _logger.LogWarning(copyError,
                            "Could not create or copy GGML library alias {Link} for {Target}", linkPath, targetFile);
                    }
                }
            }
        }

        internal static string? GetGgmlSonameLinkName(string fileName, string platform)
        {
            var pattern = platform.StartsWith("osx-", StringComparison.OrdinalIgnoreCase)
                ? @"^(?<stem>libggml(?:-[A-Za-z0-9_-]+)?)\.(?<major>\d+)\.\d+(?:\.\d+)*\.dylib$"
                : platform.StartsWith("linux-", StringComparison.OrdinalIgnoreCase)
                    ? @"^(?<stem>libggml(?:-[A-Za-z0-9_-]+)?)\.so\.(?<major>\d+)\.\d+(?:\.\d+)*$"
                    : "a^";
            var match = System.Text.RegularExpressions.Regex.Match(fileName, pattern);
            if (!match.Success) return null;

            return platform.StartsWith("osx-", StringComparison.OrdinalIgnoreCase)
                ? $"{match.Groups["stem"].Value}.{match.Groups["major"].Value}.dylib"
                : $"{match.Groups["stem"].Value}.so.{match.Groups["major"].Value}";
        }

        private static string? FindInstalledBinary(string directory, string platform)
        {
            var exeName = RoformerCatalog.ExecutableFileName(platform);
            if (!Directory.Exists(directory)) return null;
            return Directory.GetFiles(directory, exeName, SearchOption.AllDirectories).FirstOrDefault();
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
                        throw new AggregateException("Failed to install bs_roformer-cli and restore the previous installation.", promotionError, restoreError);
                    }
                }
                throw;
            }

            return hadPreviousInstall ? backupDirectory : null;
        }

        private void CompleteDirectoryPromotion(string? backupDirectory)
        {
            if (backupDirectory != null && Directory.Exists(backupDirectory))
            {
                try
                {
                    Directory.Delete(backupDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not remove previous bs_roformer-cli backup at {Path}", backupDirectory);
                }
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
                throw new InvalidOperationException("Failed to restore the previous bs_roformer-cli installation.", ex);
            }
        }

        internal static void VerifySha256(string filePath, string expectedSha256, string assetName)
        {
            var actualSha256 = WhisperSetupService.ComputeSha256(filePath);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"SHA-256 verification failed for {assetName}: expected {expectedSha256}, received {actualSha256}.");
            }
        }

        internal static bool IsZipArchiveName(string assetName)
            => assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        internal static void ValidateContentLength(long contentLength, long expectedBytes, string assetName)
        {
            if (contentLength > 0 && contentLength != expectedBytes)
            {
                throw new InvalidDataException(
                    $"Unexpected size for {assetName}: server declared {contentLength} bytes; expected {expectedBytes}.");
            }
        }

        internal static void EnsureDownloadSize(long downloadedBytes, long maximumBytes, string assetName)
        {
            if (downloadedBytes > maximumBytes)
            {
                throw new InvalidDataException(
                    $"Download for {assetName} exceeded its pinned size of {maximumBytes} bytes and was stopped.");
            }
        }

        internal static void VerifyGgufMagic(string filePath, string assetName)
        {
            Span<byte> magic = stackalloc byte[4];
            using var stream = File.OpenRead(filePath);
            if (stream.Read(magic) != magic.Length
                || magic[0] != (byte)'G'
                || magic[1] != (byte)'G'
                || magic[2] != (byte)'U'
                || magic[3] != (byte)'F')
            {
                throw new InvalidDataException($"Downloaded model {assetName} is not a GGUF file.");
            }
        }

        internal static string? PromoteDownloadedFile(string tempPath, string destPath)
        {
            var backupPath = destPath + ".backup-" + Guid.NewGuid().ToString("N");
            var hadPreviousFile = File.Exists(destPath);
            if (hadPreviousFile) File.Move(destPath, backupPath);

            try
            {
                File.Move(tempPath, destPath);
            }
            catch (Exception promotionError)
            {
                if (hadPreviousFile && File.Exists(backupPath) && !File.Exists(destPath))
                {
                    try
                    {
                        File.Move(backupPath, destPath);
                    }
                    catch (Exception restoreError)
                    {
                        throw new AggregateException("Failed to install the model and restore the previous file.", promotionError, restoreError);
                    }
                }
                throw;
            }

            return hadPreviousFile ? backupPath : null;
        }

        internal static void CompleteDownloadedFilePromotion(string? backupPath)
        {
            if (backupPath != null && File.Exists(backupPath))
            {
                try { File.Delete(backupPath); } catch { /* the new verified model is already active */ }
            }
        }

        internal static void RollbackDownloadedFilePromotion(string destPath, string? backupPath)
        {
            try
            {
                if (File.Exists(destPath)) File.Delete(destPath);
                if (backupPath != null && File.Exists(backupPath)) File.Move(backupPath, destPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to restore the previous vocal-separation model.", ex);
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
        public string InstalledModelQuant { get; set; } = "";
        public GpuInfo Gpu { get; set; } = new();
    }
}
