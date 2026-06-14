using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Setup
{
    public class WhisperSetupService
    {
        private readonly ILogger _logger;
        private readonly string _dataPath;

        // Shared HttpClient — reused across all requests to avoid socket exhaustion.
        private static readonly HttpClient SharedHttpClient = CreateHttpClient();

        // Static progress tracking — polled by the API endpoint.
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

        /// <summary>
        /// Atomically acquires the download lock. Returns false if a download is already running.
        /// Must be called (and succeed) before invoking DownloadModelAsync / DownloadBinaryAsync.
        /// </summary>
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

        public WhisperSetupService(ILogger logger, string dataPath)
        {
            _logger = logger;
            _dataPath = dataPath;
        }

        public string WhisperDirectory => Path.Combine(_dataPath, "whisper");
        public string ModelsDirectory => Path.Combine(_dataPath, "whisper", "models");

        // Silero VAD model lives in its own subdir so it never collides with the
        // transcription-model selection logic (which picks the largest .bin in ModelsDirectory).
        public string VadDirectory => Path.Combine(_dataPath, "whisper", "vad");
        public string VadModelPath => Path.Combine(VadDirectory, ModelCatalog.VadModelFileName);

        public string BinaryPath => Path.Combine(WhisperDirectory,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "whisper-cli.exe" : "whisper-cli");

        public static string GetPlatformIdentifier()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return RuntimeInformation.OSArchitecture == Architecture.X64 ? "win-x64" : "win-x86";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        }

        /// <summary>
        /// Checks whether the auto-downloaded binary and model exist.
        /// Also checks if the current config already points to valid files.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Requires Plugin.Instance (Jellyfin runtime)")]
        public SetupStatus GetStatus()
        {
            var config = Plugin.Instance.Configuration;
            var autoBinaryExists = File.Exists(BinaryPath);
            var configBinaryValid = !string.IsNullOrEmpty(config.WhisperBinaryPath)
                                    && File.Exists(config.WhisperBinaryPath);

            // Check for any model in the auto-download directory (largest first for determinism)
            string? autoModelPath = null;
            if (Directory.Exists(ModelsDirectory))
            {
                var bins = Directory.GetFiles(ModelsDirectory, "*.bin")
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .ToArray();
                if (bins.Length > 0) autoModelPath = bins[0];
            }

            var configModelValid = !string.IsNullOrEmpty(config.WhisperModelPath)
                                   && File.Exists(config.WhisperModelPath);

            // Also check if whisper-cli is discoverable in PATH
            var inPath = IsWhisperInPath();

            var binaryOk = autoBinaryExists || configBinaryValid || inPath;
            var modelOk = autoModelPath != null || configModelValid;

            return new SetupStatus
            {
                BinaryFound = binaryOk,
                BinaryFoundInPath = inPath && !autoBinaryExists && !configBinaryValid,
                BinaryPath = configBinaryValid ? config.WhisperBinaryPath
                           : autoBinaryExists ? BinaryPath
                           : inPath ? "whisper-cli (PATH)"
                           : null,
                ModelFound = modelOk,
                ModelPath = configModelValid ? config.WhisperModelPath : autoModelPath,
                Platform = GetPlatformIdentifier(),
                SetupComplete = binaryOk && modelOk,
                Gpu = DetectGpu()
            };
        }

        /// <summary>
        /// Downloads a whisper model from HuggingFace and saves it to the models directory.
        /// After download, automatically updates the plugin configuration.
        /// </summary>
        /// <summary>
        /// Downloads a whisper model. Caller must call TryAcquire("model", ...) first.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "HTTP download + Plugin.Instance")]
        public async Task DownloadModelAsync(string modelFileName, CancellationToken cancellationToken)
        {
            try
            {
                Directory.CreateDirectory(ModelsDirectory);

                var url = $"{ModelCatalog.HuggingFaceBaseUrl}/{modelFileName}";
                var destPath = Path.Combine(ModelsDirectory, modelFileName);
                var tempPath = destPath + ".downloading";

                _logger.LogInformation("Downloading model {Model} from {Url}", modelFileName, url);

                using var response = await SharedHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1;

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

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
                            _progressMessage = $"Downloading {modelFileName}: {dlMB:F1} / {totMB:F1} MB ({pct:F1}%)";
                        }
                    }
                }

                // Close the file stream before moving
                await fileStream.FlushAsync(cancellationToken);
                fileStream.Close();

                // Reject truncated downloads
                if (totalBytes > 0 && downloaded != totalBytes)
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    throw new InvalidOperationException(
                        $"Download incomplete: received {downloaded} of {totalBytes} bytes.");
                }

                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tempPath, destPath);

                // Verify downloaded file size against catalog
                var catalogEntry = ModelCatalog.Models.FirstOrDefault(m =>
                    string.Equals(m.FileName, modelFileName, StringComparison.OrdinalIgnoreCase));
                if (catalogEntry != null)
                {
                    var actualSizeMB = new FileInfo(destPath).Length / (1024.0 * 1024.0);
                    if (actualSizeMB < catalogEntry.SizeMB * 0.9)
                    {
                        File.Delete(destPath);
                        throw new InvalidOperationException(
                            $"Downloaded file is {actualSizeMB:F0} MB but expected ~{catalogEntry.SizeMB} MB. " +
                            "The file may be corrupted or truncated.");
                    }
                    _logger.LogInformation("Model size verified: {Actual:F1} MB (expected ~{Expected} MB)",
                        actualSizeMB, catalogEntry.SizeMB);
                }

                // Compute and log SHA256 for audit
                var sha256 = ComputeSha256(destPath);
                _logger.LogInformation("Model {Model} SHA256: {Hash}", modelFileName, sha256);

                // Auto-apply to plugin config
                var config = Plugin.Instance.Configuration;
                config.WhisperModelPath = destPath;
                Plugin.Instance.SaveConfiguration();

                lock (_lock)
                {
                    _progress = 100;
                    _progressMessage = $"Model {modelFileName} downloaded successfully.";
                }

                _logger.LogInformation("Model downloaded to {Path} and config updated", destPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_lock)
                {
                    _error = ex.Message;
                    _progressMessage = $"Error downloading model: {ex.Message}";
                }
                _logger.LogError(ex, "Error downloading model {Model}", modelFileName);
                throw;
            }
            finally
            {
                lock (_lock) { _isRunning = false; }
            }
        }

        /// <summary>
        /// Downloads the Silero VAD ggml model used by whisper-cli's native --vad. Small (~865 KB).
        /// Caller must call TryAcquire("vad", ...) first. Sets config.VadModelPath on success.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "HTTP download + Plugin.Instance")]
        public async Task DownloadVadModelAsync(CancellationToken cancellationToken)
        {
            try
            {
                Directory.CreateDirectory(VadDirectory);

                var destPath = VadModelPath;
                var tempPath = destPath + ".downloading";

                _logger.LogInformation("Downloading Silero VAD model from {Url}", ModelCatalog.VadModelUrl);

                using var response = await SharedHttpClient.GetAsync(ModelCatalog.VadModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
                            lock (_lock)
                            {
                                _progress = (double)downloaded / totalBytes * 100;
                                _progressMessage = $"Downloading Silero VAD model: {downloaded / 1024.0:F0} / {totalBytes / 1024.0:F0} KB";
                            }
                        }
                    }
                    await fileStream.FlushAsync(cancellationToken);
                }

                // Reject truncated downloads (the model is ~865 KB; allow 10% slack).
                var actualBytes = new FileInfo(tempPath).Length;
                if (actualBytes < ModelCatalog.VadModelSizeBytes * 0.9)
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    throw new InvalidOperationException(
                        $"Downloaded VAD model is {actualBytes} bytes but expected ~{ModelCatalog.VadModelSizeBytes}. File may be corrupted.");
                }

                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tempPath, destPath);

                var sha256 = ComputeSha256(destPath);
                _logger.LogInformation("VAD model SHA256: {Hash}", sha256);

                // Plugin.Instance can be transiently null during reload/teardown; the file is on
                // disk regardless and ResolveVadModelPath finds it at the default location, so a
                // missed config write self-recovers.
                var plugin = Plugin.Instance;
                if (plugin != null)
                {
                    plugin.Configuration.VadModelPath = destPath;
                    plugin.SaveConfiguration();
                }

                lock (_lock)
                {
                    _progress = 100;
                    _progressMessage = "Silero VAD model downloaded successfully.";
                }
                _logger.LogInformation("VAD model downloaded to {Path}", destPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_lock)
                {
                    _error = ex.Message;
                    _progressMessage = $"Error downloading VAD model: {ex.Message}";
                }
                _logger.LogError(ex, "Error downloading Silero VAD model");
                throw;
            }
            finally
            {
                lock (_lock) { _isRunning = false; }
            }
        }

        /// <summary>
        /// Resolves the Silero VAD model path: the configured path if it exists, else the default
        /// vad/ location if present, else null (not downloaded yet).
        /// </summary>
        public string? ResolveVadModelPath(string? configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                return configuredPath;
            return File.Exists(VadModelPath) ? VadModelPath : null;
        }

        /// <summary>
        /// Detects which GPU backends are likely available on the host.
        /// </summary>
        public static GpuInfo DetectGpu()
        {
            var info = new GpuInfo();

            // NVIDIA: check for /dev/nvidia0 or nvidia-smi
            if (File.Exists("/dev/nvidia0") || File.Exists("/dev/nvidiactl"))
            {
                info.HasNvidia = true;
            }
            else
            {
                try
                {
                    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "nvidia-smi", RedirectStandardOutput = true,
                        RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
                    });
                    if (p != null) { p.WaitForExit(3000); info.HasNvidia = p.ExitCode == 0; }
                }
                catch { /* not available */ }
            }

            // CUDA userspace libraries — containers often have the GPU device passed
            // through but lack the CUDA runtime libraries.
            var cudaPaths = new[]
            {
                "/usr/local/cuda/lib64/libcudart.so",
                "/usr/lib/x86_64-linux-gnu/libcuda.so.1",
                "/usr/lib64/libcuda.so.1",
                "/usr/lib/libcuda.so.1"
            };
            info.HasCudaLibrary = Array.Exists(cudaPaths, File.Exists);

            // AMD ROCm: check for /dev/kfd (ROCm kernel driver)
            if (File.Exists("/dev/kfd"))
            {
                info.HasAmdGpu = true;
            }
            else
            {
                try
                {
                    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "rocm-smi", RedirectStandardOutput = true,
                        RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
                    });
                    if (p != null) { p.WaitForExit(3000); info.HasAmdGpu = p.ExitCode == 0; }
                }
                catch { /* not available */ }
            }

            // ROCm userspace library
            var rocmPaths = new[]
            {
                "/opt/rocm/lib/libamdhip64.so",
                "/usr/lib/x86_64-linux-gnu/libamdhip64.so",
                "/usr/lib64/libamdhip64.so",
                "/usr/lib/libamdhip64.so"
            };
            info.HasRocmLibrary = Array.Exists(rocmPaths, File.Exists);

            // Intel / Vulkan: check for /dev/dri/renderD128
            if (File.Exists("/dev/dri/renderD128"))
            {
                info.HasRenderDevice = true;
            }

            // Vulkan userspace library — required at runtime by the vulkan binary.
            // The render device alone is not sufficient; containers often have /dev/dri
            // passed through but lack the libvulkan.so.1 userspace library.
            var vulkanPaths = new[]
            {
                "/lib/x86_64-linux-gnu/libvulkan.so.1",
                "/usr/lib/x86_64-linux-gnu/libvulkan.so.1",
                "/usr/lib/libvulkan.so.1",
                "/lib64/libvulkan.so.1",
                "/usr/lib64/libvulkan.so.1",
                "/lib/aarch64-linux-gnu/libvulkan.so.1"
            };
            info.HasVulkanLibrary = Array.Exists(vulkanPaths, File.Exists);

            // OpenMP runtime detection. The cpu/cuda12/vulkan/rocm binaries link libgomp for
            // performance; if it's missing the plugin auto-falls back to the self-contained
            // noavx build (compiled with -DGGML_OPENMP=OFF), so this is informational, not a blocker.
            var openmpPaths = new[]
            {
                "/lib/x86_64-linux-gnu/libgomp.so.1",
                "/usr/lib/x86_64-linux-gnu/libgomp.so.1",
                "/usr/lib/libgomp.so.1",
                "/lib64/libgomp.so.1",
                "/usr/lib64/libgomp.so.1",
                "/lib/aarch64-linux-gnu/libgomp.so.1"
            };
            info.HasOpenMP = Array.Exists(openmpPaths, File.Exists);

            // CPU AVX support. Our x64 cpu/cuda12/vulkan binaries are compiled with AVX/AVX2;
            // on a CPU that lacks those instructions they crash with SIGILL (illegal instruction,
            // exit 132). Only the *-noavx builds (SSE4.2) are safe there. ARM has no AVX concept,
            // and its binaries are built without it, so treat ARM as "AVX not required" (HasAvx=true
            // semantically means "the AVX-requiring builds are safe to run").
            info.HasAvx = RuntimeInformation.OSArchitecture == Architecture.Arm64
                || DetectCpuHasAvx();

            // Recommend variant based on detection — require both device AND userspace library.
            // GPU binaries are also AVX-compiled, so a CPU without AVX must use a *-noavx variant.
            if (info.HasNvidia && info.HasCudaLibrary) info.RecommendedVariant = info.HasAvx ? "cuda12" : "cuda12-noavx";
            else if (info.HasAmdGpu && info.HasRocmLibrary) info.RecommendedVariant = "rocm";
            else if (info.HasRenderDevice && info.HasVulkanLibrary) info.RecommendedVariant = info.HasAvx ? "vulkan" : "vulkan-noavx";
            else info.RecommendedVariant = info.HasAvx ? "cpu" : "noavx";

            return info;
        }

        /// <summary>
        /// Downloads the whisper-cli binary from the whisper-subs GitHub release matching the
        /// current plugin version, validates it can launch, and on failure walks the
        /// <see cref="GetFallbackVariant"/> chain to a more-compatible build. Caller must call
        /// TryAcquire("binary", ...) first; this method owns releasing that lock.
        /// </summary>
        /// <param name="variant">A binary variant id from <see cref="BinaryCatalog"/> (e.g. "cpu",
        /// "noavx", "cuda12", "cuda12-noavx", "vulkan", "vulkan-noavx", "rocm").</param>
        [ExcludeFromCodeCoverage(Justification = "HTTP download + Plugin.Instance + process validation")]
        public async Task DownloadBinaryAsync(string variant, CancellationToken cancellationToken)
        {
            try
            {
                var currentVariant = variant;
                string? originalError = null;

                // Iterative fallback walk (cuda12 -> cpu -> noavx, etc.). The chain is a finite DAG
                // converging on a variant with no fallback, so this loop always terminates.
                while (true)
                {
                    string? validationError;
                    try
                    {
                        validationError = await DownloadAndValidateVariantAsync(currentVariant, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && originalError != null)
                    {
                        // A *fallback* download itself failed (e.g. the asset 404s). Surface the
                        // ORIGINAL validation reason so the user sees why the primary was rejected,
                        // not just the secondary download error.
                        _logger.LogError(ex, "Fallback download '{Variant}' failed; surfacing original validation error", currentVariant);
                        lock (_lock)
                        {
                            _progress = 100;
                            _error = originalError;
                            _progressMessage = $"whisper-cli downloaded but may not work: {originalError} " +
                                $"(fallback '{currentVariant}' also failed: {ex.Message})";
                        }
                        return;
                    }

                    if (validationError == null)
                    {
                        var config = Plugin.Instance.Configuration;
                        config.WhisperBinaryPath = BinaryPath;
                        Plugin.Instance.SaveConfiguration();

                        lock (_lock)
                        {
                            _progress = 100;
                            _progressMessage = "whisper-cli downloaded successfully.";
                        }

                        _logger.LogInformation("Binary downloaded to {Path} and config updated", BinaryPath);
                        return;
                    }

                    _logger.LogWarning("Binary validation warning: {Error}", validationError);
                    originalError ??= validationError;

                    var fallbackVariant = GetFallbackVariant(currentVariant);
                    if (fallbackVariant == null)
                    {
                        lock (_lock)
                        {
                            _progress = 100;
                            _progressMessage = $"whisper-cli downloaded but may not work: {validationError}";
                            _error = validationError;
                        }
                        _logger.LogWarning("Binary downloaded to {Path} but NOT applied to config: {Error}", BinaryPath, validationError);
                        return;
                    }

                    _logger.LogInformation("Binary '{Variant}' failed validation — auto-downloading fallback ({Fallback})", currentVariant, fallbackVariant);
                    lock (_lock)
                    {
                        _progressMessage = $"'{currentVariant}' binary failed ({validationError}). Downloading {fallbackVariant} fallback...";
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
                    _progressMessage = $"Error downloading binary: {ex.Message}";
                }
                _logger.LogError(ex, "Error downloading whisper-cli binary");
                throw;
            }
            finally
            {
                lock (_lock) { _isRunning = false; }
            }
        }

        /// <summary>
        /// Downloads and validates a single binary variant. Returns null if the binary launches
        /// successfully, or a user-friendly error string if validation failed (e.g. missing
        /// shared library). Does NOT touch the <c>_isRunning</c> lock — the caller owns it.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "HTTP download + process validation")]
        private async Task<string?> DownloadAndValidateVariantAsync(string variant, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(WhisperDirectory);

            var platform = GetPlatformIdentifier();
            var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "3.3.0.0";
            var assetName = BinaryCatalog.GetAssetName(platform, variant);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) assetName += ".exe";

            var url = $"https://github.com/GeiserX/whisper-subs/releases/download/v{version}/{assetName}";

            _logger.LogInformation("Downloading whisper-cli from {Url} for platform {Platform}", url, platform);

            using var response = await SharedHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var tempPath = BinaryPath + ".downloading";

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
                            _progressMessage = $"Downloading whisper-cli ({variant}): {dlMB:F1} / {totMB:F1} MB ({pct:F1}%)";
                        }
                    }
                }

                await fileStream.FlushAsync(cancellationToken);

                // Reject truncated downloads
                if (totalBytes > 0 && downloaded != totalBytes)
                {
                    throw new InvalidOperationException(
                        $"Download incomplete: received {downloaded} of {totalBytes} bytes.");
                }
            }

            if (File.Exists(BinaryPath)) File.Delete(BinaryPath);
            File.Move(tempPath, BinaryPath);

            // Make executable on Unix
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var chmod = System.Diagnostics.Process.Start("chmod", new[] { "+x", BinaryPath });
                chmod?.WaitForExit(5000);
            }

            // Compute and log SHA256 for audit
            var sha256 = ComputeSha256(BinaryPath);
            _logger.LogInformation("Binary {Variant} SHA256: {Hash}", variant, sha256);

            // Validate the binary can actually run (catches missing shared libraries)
            return ValidateBinary(BinaryPath, variant);
        }

        /// <summary>
        /// Probes the downloaded binary to check it can actually launch.
        /// Returns null on success, or a user-friendly error message on failure
        /// (e.g. missing shared libraries like libvulkan.so.1).
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Spawns binary process for validation")]
        private string? ValidateBinary(string binaryPath, string variant)
        {
            // AVX gate: `--help` exits before any AVX instruction executes, so an AVX-compiled
            // binary on a non-AVX CPU would *pass* the probe and only crash later during real
            // transcription (SIGILL / exit 132). Catch it here so the cpu->noavx fallback fires
            // at download time. ARM hosts have no AVX concept and their binaries don't use it.
            if (VariantRequiresAvx(variant)
                && RuntimeInformation.OSArchitecture != Architecture.Arm64
                && !DetectCpuHasAvx())
            {
                _logger.LogWarning("Variant '{Variant}' requires AVX but this CPU lacks it — will fall back", variant);
                return "This CPU does not support AVX instructions, which this binary requires. "
                     + "Falling back to the compatibility (noavx) build.";
            }

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

                // Exit code 127 = missing shared library (Linux dynamic linker error)
                if (process.ExitCode == 127)
                {
                    // Extract the missing library name from stderr
                    var match = System.Text.RegularExpressions.Regex.Match(
                        stderr, @"error while loading shared libraries:\s*(\S+?):");
                    var lib = match.Success ? match.Groups[1].Value : "a shared library";
                    var installHint = GetInstallHint(lib);
                    var isCpu = string.Equals(variant, "cpu", StringComparison.OrdinalIgnoreCase);
                    var suggestion = isCpu
                        ? $"Install it in your container ({installHint})."
                        : $"Try the CPU variant, or install the library in your container ({installHint}).";
                    return $"Missing {lib}. {suggestion}";
                }

                // 128+N = killed by signal N. 132 = SIGILL (illegal instruction) — the binary used
                // a CPU instruction (e.g. AVX) this CPU doesn't support. 134 = SIGABRT, 135 = SIGBUS.
                // Treat these as a validation failure so the fallback chain steps to a safer build.
                if (process.ExitCode == 132 || process.ExitCode == 134 || process.ExitCode == 135)
                {
                    return "The binary crashed on launch (illegal instruction) — this CPU likely "
                         + "lacks an instruction set (e.g. AVX) the build requires. "
                         + "Falling back to the compatibility (noavx) build.";
                }

                return null; // Any other exit code is fine (--help may return non-zero on some builds)
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Binary validation probe failed: {Error}", ex.Message);
                return null; // Can't probe — don't block the download
            }
        }

        /// <summary>
        /// Maps a binary variant to the next more-compatible variant when the current one fails
        /// to launch (missing GPU userspace lib or missing libgomp). GPU variants degrade to a
        /// CPU build; the OpenMP-linked CPU build degrades to the self-contained "noavx" build.
        /// Returns null when there is no further fallback ("noavx" is the terminal sink).
        /// The chain is acyclic and converges on "noavx" -> null, so callers can walk it safely.
        /// </summary>
        internal static string? GetFallbackVariant(string variant) => variant switch
        {
            "cuda12" or "vulkan" => "cpu",
            "cuda12-noavx" or "vulkan-noavx" or "rocm" => "noavx",
            // The cpu build (x64 and arm64) links libgomp (OpenMP) for performance. If that
            // library is missing (minimal containers), fall back to the self-contained noavx
            // build, compiled with -DGGML_OPENMP=OFF, which has no such dependency. The arm64
            // asset is resolved by platform, so "noavx" covers both architectures.
            "cpu" => "noavx",
            _ => null
        };

        /// <summary>
        /// Whether the given variant's prebuilt binary was compiled with AVX/AVX2 instructions.
        /// These crash with SIGILL (exit 132) on CPUs that lack AVX. The "*-noavx" builds use
        /// only SSE4.2 and are safe everywhere. ARM builds have no AVX and are always safe.
        /// </summary>
        internal static bool VariantRequiresAvx(string variant) => variant switch
        {
            "cpu" or "cuda12" or "vulkan" => true,
            _ => false   // noavx, cuda12-noavx, vulkan-noavx, rocm, unknown
        };

        /// <summary>
        /// Parses Linux /proc/cpuinfo content and returns true if the CPU advertises AVX.
        /// Pure/testable — the file read happens in <see cref="DetectCpuHasAvx"/>.
        /// </summary>
        internal static bool CpuInfoHasAvx(string cpuInfoContent)
        {
            if (string.IsNullOrEmpty(cpuInfoContent)) return false;

            foreach (var line in cpuInfoContent.Split('\n'))
            {
                if (!line.StartsWith("flags", StringComparison.OrdinalIgnoreCase)) continue;

                // flags are space-separated; match "avx" as a whole token (avoid matching "avx512..."
                // substrings only — though those imply avx too, an exact-token check is clearest).
                var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var token in tokens)
                {
                    if (string.Equals(token, "avx", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Reads /proc/cpuinfo and reports whether the host CPU supports AVX. Returns true on any
        /// non-Linux platform or read failure (fail-open: don't wrongly steer users away from the
        /// faster build when we can't tell — the download-time validation still catches a real crash).
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Reads host /proc/cpuinfo")]
        private static bool DetectCpuHasAvx()
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return true;
                if (!File.Exists("/proc/cpuinfo")) return true;
                return CpuInfoHasAvx(File.ReadAllText("/proc/cpuinfo"));
            }
            catch
            {
                return true; // can't determine — don't block the default recommendation
            }
        }

        /// <summary>
        /// Returns an apt-install hint for a missing shared library.
        /// </summary>
        private static string GetInstallHint(string libraryName) => libraryName switch
        {
            "libgomp.so.1" => "e.g. 'apt install libgomp1'",
            "libvulkan.so.1" => "e.g. 'apt install libvulkan1'",
            "libcuda.so.1" or "libcudart.so.12" => "e.g. install the NVIDIA CUDA toolkit",
            "libamdhip64.so" => "e.g. install the AMD ROCm runtime",
            _ => $"e.g. 'apt install' the package providing {libraryName}"
        };

        private static string ComputeSha256(string filePath)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static bool IsWhisperInPath()
        {
            // "main" excluded — too generic a name, risks matching wrong binaries on Windows
            foreach (var name in new[] { "whisper-cli", "whisper" })
            {
                try
                {
                    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = name,
                        Arguments = "--help",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    if (p != null)
                    {
                        // Drain redirected streams to avoid deadlock
                        p.StandardOutput.ReadToEnd();
                        p.StandardError.ReadToEnd();

                        if (!p.WaitForExit(5000))
                        {
                            try { p.Kill(); } catch { }
                            continue;
                        }
                        if (p.ExitCode == 0) return true;
                    }
                }
                catch { /* not found in PATH */ }
            }
            return false;
        }
    }

    public class SetupStatus
    {
        public bool BinaryFound { get; set; }
        public bool BinaryFoundInPath { get; set; }
        public string? BinaryPath { get; set; }
        public bool ModelFound { get; set; }
        public string? ModelPath { get; set; }
        public string Platform { get; set; } = "";
        public bool SetupComplete { get; set; }
        public GpuInfo Gpu { get; set; } = new();
    }

    public class GpuInfo
    {
        public bool HasNvidia { get; set; }
        public bool HasCudaLibrary { get; set; }
        public bool HasAmdGpu { get; set; }
        public bool HasRocmLibrary { get; set; }
        public bool HasRenderDevice { get; set; }
        public bool HasVulkanLibrary { get; set; }
        public bool HasOpenMP { get; set; }
        /// <summary>True if the CPU supports AVX (or is ARM, where AVX is N/A). When false, only
        /// the "*-noavx" binary variants will run — the others crash with SIGILL.</summary>
        public bool HasAvx { get; set; } = true;
        public string RecommendedVariant { get; set; } = "cpu";
    }

    public class DownloadProgress
    {
        public string Operation { get; set; } = "";
        public double Percent { get; set; }
        public string Message { get; set; } = "";
        public bool IsRunning { get; set; }
        public string? Error { get; set; }
    }
}
