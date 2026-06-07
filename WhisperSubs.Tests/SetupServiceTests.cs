using System.Reflection;
using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

[Collection("SetupServiceTests")]
public class SetupServiceTests
{
    [Fact]
    public void GetPlatformIdentifier_ReturnsNonEmpty()
    {
        var platform = WhisperSetupService.GetPlatformIdentifier();
        Assert.False(string.IsNullOrEmpty(platform));
    }

    [Fact]
    public void GetPlatformIdentifier_ReturnsKnownFormat()
    {
        var platform = WhisperSetupService.GetPlatformIdentifier();
        // Must match one of the known platform patterns
        var knownPrefixes = new[] { "linux-", "win-", "osx-" };
        Assert.Contains(knownPrefixes, p => platform.StartsWith(p));
    }

    [Fact]
    public void TryAcquire_FirstCall_Succeeds()
    {
        // Reset static state by completing any prior operation
        ForceReleaseDownloadLock();

        Assert.True(WhisperSetupService.TryAcquire("test", "Starting test..."));

        // Verify state
        var progress = WhisperSetupService.CurrentProgress;
        Assert.True(progress.IsRunning);
        Assert.Equal("test", progress.Operation);
        Assert.Equal("Starting test...", progress.Message);
        Assert.Equal(0, progress.Percent);
        Assert.Null(progress.Error);

        // Clean up
        ForceReleaseDownloadLock();
    }

    [Fact]
    public void TryAcquire_SecondCall_Fails()
    {
        ForceReleaseDownloadLock();

        Assert.True(WhisperSetupService.TryAcquire("first", "First op"));
        Assert.False(WhisperSetupService.TryAcquire("second", "Second op"));

        // Verify first operation is still active
        var progress = WhisperSetupService.CurrentProgress;
        Assert.Equal("first", progress.Operation);

        ForceReleaseDownloadLock();
    }

    [Fact]
    public void TryAcquire_AfterRelease_CanReacquire()
    {
        ForceReleaseDownloadLock();

        Assert.True(WhisperSetupService.TryAcquire("first", "First"));
        ForceReleaseDownloadLock();
        Assert.True(WhisperSetupService.TryAcquire("second", "Second"));

        var progress = WhisperSetupService.CurrentProgress;
        Assert.Equal("second", progress.Operation);

        ForceReleaseDownloadLock();
    }

    [Fact]
    public void CurrentProgress_WhenIdle_IsNotRunning()
    {
        ForceReleaseDownloadLock();

        // Also clear any leftover state from concurrent tests
        var progress = WhisperSetupService.CurrentProgress;
        Assert.False(progress.IsRunning);
    }

    [Fact]
    public void GetInstallHint_KnownLibraries()
    {
        var method = typeof(WhisperSetupService).GetMethod(
            "GetInstallHint",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.Contains("libgomp1", (string)method!.Invoke(null, new object[] { "libgomp.so.1" })!);
        Assert.Contains("libvulkan1", (string)method.Invoke(null, new object[] { "libvulkan.so.1" })!);
        Assert.Contains("CUDA", (string)method.Invoke(null, new object[] { "libcuda.so.1" })!);
        Assert.Contains("CUDA", (string)method.Invoke(null, new object[] { "libcudart.so.12" })!);
        Assert.Contains("ROCm", (string)method.Invoke(null, new object[] { "libamdhip64.so" })!);
    }

    [Fact]
    public void GetInstallHint_UnknownLibrary_FallbackMessage()
    {
        var method = typeof(WhisperSetupService).GetMethod(
            "GetInstallHint",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (string)method!.Invoke(null, new object[] { "libfoo.so" })!;
        Assert.Contains("libfoo.so", result);
        Assert.Contains("apt install", result);
    }

    [Theory]
    [InlineData("cuda12", "cpu")]
    [InlineData("vulkan", "cpu")]
    [InlineData("cuda12-noavx", "noavx")]
    [InlineData("vulkan-noavx", "noavx")]
    [InlineData("rocm", "noavx")]
    [InlineData("cpu", "noavx")]        // #70: OpenMP-linked cpu build falls back to self-contained noavx
    [InlineData("noavx", null)]         // already the most-compatible build — no further fallback
    [InlineData("foobar", null)]        // unknown variant — no fallback
    [InlineData("", null)]              // empty string — no fallback
    [InlineData("CPU", null)]           // switch is case-SENSITIVE — uppercase does NOT match
    [InlineData("rocm-noavx", null)]    // not a real variant — no fallback
    public void GetFallbackVariant_MapsToExpectedFallback(string variant, string? expected)
    {
        Assert.Equal(expected, WhisperSetupService.GetFallbackVariant(variant));
    }

    [Fact]
    public void GetFallbackVariant_NullInput_ReturnsNull()
    {
        Assert.Null(WhisperSetupService.GetFallbackVariant(null!));
    }

    [Fact]
    public void GetFallbackVariant_ChainTerminatesWithoutCycles()
    {
        var startingVariants = new[] { "cpu", "noavx", "cuda12", "cuda12-noavx", "vulkan", "vulkan-noavx", "rocm" };

        foreach (var start in startingVariants)
        {
            var visited = new HashSet<string>();
            var current = start;
            var hops = 0;

            while (current != null)
            {
                Assert.True(
                    visited.Add(current),
                    $"Fallback cycle detected starting from '{start}': revisited variant '{current}'");
                hops++;
                Assert.True(
                    hops < 10,
                    $"Fallback chain starting from '{start}' did not terminate within 10 hops");
                current = WhisperSetupService.GetFallbackVariant(current);
            }
        }
    }

    [Fact]
    public void DetectGpu_ReturnsValidInfo()
    {
        var gpu = WhisperSetupService.DetectGpu();
        Assert.NotNull(gpu);
        Assert.False(string.IsNullOrEmpty(gpu.RecommendedVariant));
        // Recommendation must be a real catalog variant. Includes *-noavx because a host
        // without AVX (or our detection) is steered to the compatibility builds.
        var validVariants = new[] { "cpu", "noavx", "cuda12", "cuda12-noavx", "vulkan", "vulkan-noavx", "rocm" };
        Assert.Contains(gpu.RecommendedVariant, validVariants);
    }

    [Theory]
    [InlineData("cpu", true)]
    [InlineData("cuda12", true)]
    [InlineData("vulkan", true)]
    [InlineData("noavx", false)]
    [InlineData("cuda12-noavx", false)]
    [InlineData("vulkan-noavx", false)]
    [InlineData("rocm", false)]
    [InlineData("unknown", false)]
    public void VariantRequiresAvx_OnlyNonNoavxBuilds(string variant, bool requiresAvx)
    {
        Assert.Equal(requiresAvx, WhisperSetupService.VariantRequiresAvx(variant));
    }

    [Fact]
    public void CpuInfoHasAvx_DetectsAvxFlag()
    {
        // Real /proc/cpuinfo "flags" line containing avx
        var withAvx = "processor\t: 0\nflags\t\t: fpu vme de pse tsc msr pae mce cx8 sse sse2 avx avx2 fma\n";
        Assert.True(WhisperSetupService.CpuInfoHasAvx(withAvx));
    }

    [Fact]
    public void CpuInfoHasAvx_NoAvxFlag_ReturnsFalse()
    {
        // A CPU that lacks AVX (e.g. older Atom/Celeron common in NAS boxes)
        var noAvx = "processor\t: 0\nflags\t\t: fpu vme de pse tsc msr pae mce cx8 sse sse2 sse4_1 sse4_2\n";
        Assert.False(WhisperSetupService.CpuInfoHasAvx(noAvx));
    }

    [Fact]
    public void CpuInfoHasAvx_DoesNotMatchAvxSubstrings()
    {
        // Must match the whole "avx" token, not "avx512vl" alone implying nothing about plain avx.
        // A line with only avx512 tokens (no bare "avx") should NOT report avx.
        var only512 = "flags\t\t: fpu sse2 avx512f avx512dq avx512bw\n";
        Assert.False(WhisperSetupService.CpuInfoHasAvx(only512));
    }

    [Theory]
    [InlineData("")]
    [InlineData("processor : 0\nmodel name : Some CPU\n")]  // no flags line at all
    public void CpuInfoHasAvx_EmptyOrNoFlags_ReturnsFalse(string content)
    {
        Assert.False(WhisperSetupService.CpuInfoHasAvx(content));
    }

    [Fact]
    public void Constructor_SetsDataPath()
    {
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<WhisperSetupService>();
        var service = new WhisperSetupService(logger, "/tmp/test-data");

        Assert.Equal("/tmp/test-data/whisper", service.WhisperDirectory);
        Assert.Equal("/tmp/test-data/whisper/models", service.ModelsDirectory);
        Assert.Contains("whisper-cli", service.BinaryPath);
    }

    /// <summary>
    /// Force-releases the static download lock by setting _isRunning to false via reflection.
    /// </summary>
    private static void ForceReleaseDownloadLock()
    {
        var field = typeof(WhisperSetupService).GetField("_isRunning",
            BindingFlags.NonPublic | BindingFlags.Static);
        field?.SetValue(null, false);
    }
}

public class SetupStatusTests
{
    [Fact]
    public void DefaultValues()
    {
        var status = new SetupStatus();

        Assert.False(status.BinaryFound);
        Assert.False(status.BinaryFoundInPath);
        Assert.Null(status.BinaryPath);
        Assert.False(status.ModelFound);
        Assert.Null(status.ModelPath);
        Assert.Equal("", status.Platform);
        Assert.False(status.SetupComplete);
        Assert.NotNull(status.Gpu);
    }

    [Fact]
    public void CanSetAllProperties()
    {
        var gpu = new GpuInfo { HasNvidia = true };
        var status = new SetupStatus
        {
            BinaryFound = true,
            BinaryFoundInPath = true,
            BinaryPath = "/usr/bin/whisper-cli",
            ModelFound = true,
            ModelPath = "/models/test.bin",
            Platform = "linux-x64",
            SetupComplete = true,
            Gpu = gpu
        };

        Assert.True(status.BinaryFound);
        Assert.True(status.BinaryFoundInPath);
        Assert.Equal("/usr/bin/whisper-cli", status.BinaryPath);
        Assert.True(status.ModelFound);
        Assert.Equal("/models/test.bin", status.ModelPath);
        Assert.Equal("linux-x64", status.Platform);
        Assert.True(status.SetupComplete);
        Assert.Same(gpu, status.Gpu);
    }
}

public class GpuInfoTests
{
    [Fact]
    public void DefaultValues()
    {
        var gpu = new GpuInfo();

        Assert.False(gpu.HasNvidia);
        Assert.False(gpu.HasCudaLibrary);
        Assert.False(gpu.HasAmdGpu);
        Assert.False(gpu.HasRocmLibrary);
        Assert.False(gpu.HasRenderDevice);
        Assert.False(gpu.HasVulkanLibrary);
        Assert.False(gpu.HasOpenMP);
        Assert.Equal("cpu", gpu.RecommendedVariant);
    }

    [Fact]
    public void CanSetAllProperties()
    {
        var gpu = new GpuInfo
        {
            HasNvidia = true,
            HasCudaLibrary = true,
            HasAmdGpu = true,
            HasRocmLibrary = true,
            HasRenderDevice = true,
            HasVulkanLibrary = true,
            HasOpenMP = true,
            RecommendedVariant = "cuda12"
        };

        Assert.True(gpu.HasNvidia);
        Assert.True(gpu.HasCudaLibrary);
        Assert.True(gpu.HasAmdGpu);
        Assert.True(gpu.HasRocmLibrary);
        Assert.True(gpu.HasRenderDevice);
        Assert.True(gpu.HasVulkanLibrary);
        Assert.True(gpu.HasOpenMP);
        Assert.Equal("cuda12", gpu.RecommendedVariant);
    }
}

public class DownloadProgressTests
{
    [Fact]
    public void DefaultValues()
    {
        var progress = new DownloadProgress();

        Assert.Equal("", progress.Operation);
        Assert.Equal(0, progress.Percent);
        Assert.Equal("", progress.Message);
        Assert.False(progress.IsRunning);
        Assert.Null(progress.Error);
    }

    [Fact]
    public void CanSetAllProperties()
    {
        var progress = new DownloadProgress
        {
            Operation = "model",
            Percent = 50.5,
            Message = "Downloading...",
            IsRunning = true,
            Error = "Some error"
        };

        Assert.Equal("model", progress.Operation);
        Assert.Equal(50.5, progress.Percent);
        Assert.Equal("Downloading...", progress.Message);
        Assert.True(progress.IsRunning);
        Assert.Equal("Some error", progress.Error);
    }
}
