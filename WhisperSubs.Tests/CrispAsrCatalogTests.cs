using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

public class CrispAsrCatalogTests
{
    [Fact]
    public void Version_IsThePinnedUpstreamTag()
    {
        Assert.Equal("v0.8.35", CrispAsrCatalog.Version);
        Assert.Equal("https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.35", CrispAsrCatalog.ReleaseBaseUrl);
    }

    [Fact]
    public void LinuxX64Variants_ContainsExpectedEntriesInOrder()
    {
        Assert.Equal(
            new[] { "cpu", "cpu-legacy", "vulkan", "cuda12", "cuda13", "hip" },
            CrispAsrCatalog.LinuxX64Variants.Select(v => v.Id).ToArray());
    }

    // The design makes CPU the default on purpose (no iGPU speed-up for Canary, no driver dependency).
    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-arm64")]
    [InlineData("win-x64")]
    public void GetAvailableVariants_CpuIsTheOnlyDefault(string platform)
    {
        var defaults = CrispAsrCatalog.GetAvailableVariants(platform).Where(v => v.IsDefault).ToArray();
        var only = Assert.Single(defaults);
        Assert.Equal("cpu", only.Id);
    }

    [Fact]
    public void GetAvailableVariants_PerPlatform()
    {
        Assert.Equal(6, CrispAsrCatalog.GetAvailableVariants("linux-x64").Length);
        Assert.Equal(new[] { "cpu", "vulkan" }, CrispAsrCatalog.GetAvailableVariants("win-x64").Select(v => v.Id).ToArray());
        Assert.Equal(new[] { "cpu" }, CrispAsrCatalog.GetAvailableVariants("linux-arm64").Select(v => v.Id).ToArray());
        Assert.Equal(new[] { "cpu" }, CrispAsrCatalog.GetAvailableVariants("osx-arm64").Select(v => v.Id).ToArray());
    }

    // The macOS archive is arm64-only, so Intel Macs and unknown platforms get nothing to download.
    [Theory]
    [InlineData("osx-x64")]
    [InlineData("win-x86")]
    [InlineData("freebsd-x64")]
    public void GetAvailableVariants_UnpublishedPlatform_ReturnsEmpty(string platform)
    {
        Assert.Empty(CrispAsrCatalog.GetAvailableVariants(platform));
    }

    [Theory]
    [InlineData("linux-x64", "cpu", "crispasr-linux-x86_64.tar.gz")]
    [InlineData("linux-x64", "cpu-legacy", "crispasr-linux-x86_64-cpu-legacy.tar.gz")]
    [InlineData("linux-x64", "vulkan", "crispasr-linux-x86_64-vulkan.tar.gz")]
    [InlineData("linux-x64", "cuda12", "crispasr-linux-x86_64-cuda.tar.gz")]
    [InlineData("linux-x64", "cuda13", "crispasr-linux-x86_64-cuda13.tar.gz")]
    [InlineData("linux-x64", "hip", "crispasr-linux-x86_64-hip.tar.gz")]
    [InlineData("linux-arm64", "cpu", "crispasr-linux-arm64.tar.gz")]
    [InlineData("osx-arm64", "cpu", "crispasr-macos.tar.gz")]
    [InlineData("win-x64", "cpu", "crispasr-windows-x86_64-cpu.zip")]
    [InlineData("win-x64", "vulkan", "crispasr-windows-x86_64-vulkan.zip")]
    public void GetAssetName_ReturnsTheUpstreamAsset(string platform, string variant, string expected)
    {
        Assert.Equal(expected, CrispAsrCatalog.GetAssetName(platform, variant));
    }

    [Theory]
    [InlineData("linux-x64", "cpu", "2a9982f69c8ee714cab81d8697ef8fb76878ee76eeb878cf65ddce1e443f9ff9", 39924568)]
    [InlineData("linux-x64", "cpu-legacy", "37e5a1adf91b06c400a9da391cfa05aeec9e2aa7e393677c2227b1043a619c8d", 39718726)]
    [InlineData("linux-x64", "vulkan", "c94ebfa24da74b3a5c8a6ea13be16e99e7c885b75721e3e9db3883f9406ced21", 73850252)]
    [InlineData("linux-x64", "cuda12", "7413c6100cd419d39be5c76c7d0a41c0bad36cb82e9c96e4b701765ed8a5027c", 165318168)]
    [InlineData("linux-x64", "cuda13", "f6d116bb75c740aac94ce7e0366d1583d08a7f6c0b07ec05a6b396a70521b30e", 128658689)]
    [InlineData("linux-x64", "hip", "bb0ccb4b8cbf813873f1d9faed77a9fd4ab371aa0f32732ec74dcffe519337bb", 107165881)]
    [InlineData("linux-arm64", "cpu", "5b1ae7df881d09bed899ad67e8cabb7241f5664c891a64071a952fb742747238", 33301701)]
    [InlineData("osx-arm64", "cpu", "0fa1aca0f102c2ea428a357eef143843a19bc122020bfa71cc3b4f984d8ecdf8", 16830142)]
    [InlineData("win-x64", "cpu", "fb812ef200ffcf7de55e9be900a39a440c83a3facb09d7df9020d5a3b56ff280", 8462806)]
    [InlineData("win-x64", "vulkan", "8c4f6483d3dad9ab4066f3853b48306e16e5b4010d1bf42054c81bf439166b3f", 36826421)]
    public void GetAssetSha256AndSize_ReturnPinnedValues(string platform, string variant, string sha256, long size)
    {
        Assert.Equal(sha256, CrispAsrCatalog.GetAssetSha256(platform, variant));
        Assert.Equal(size, CrispAsrCatalog.GetAssetSizeBytes(platform, variant));
    }

    // Every offered variant must resolve to an asset, a digest and a size; a variant added to a list
    // without its pin would otherwise only fail when an admin clicks Download.
    [Fact]
    public void EveryOfferedVariant_HasAssetDigestAndSize()
    {
        foreach (var platform in new[] { "linux-x64", "linux-arm64", "osx-arm64", "win-x64" })
        {
            foreach (var variant in CrispAsrCatalog.GetAvailableVariants(platform))
            {
                Assert.False(string.IsNullOrEmpty(CrispAsrCatalog.GetAssetName(platform, variant.Id)));
                Assert.Matches("^[0-9a-f]{64}$", CrispAsrCatalog.GetAssetSha256(platform, variant.Id));
                Assert.True(CrispAsrCatalog.GetAssetSizeBytes(platform, variant.Id) > 0);
            }
        }
    }

    [Theory]
    [InlineData("linux-arm64", "vulkan")]
    [InlineData("osx-x64", "cpu")]
    [InlineData("win-x64", "cuda12")]
    public void UnpublishedCombination_Throws(string platform, string variant)
    {
        Assert.Throws<NotSupportedException>(() => CrispAsrCatalog.GetAssetName(platform, variant));
        Assert.Throws<NotSupportedException>(() => CrispAsrCatalog.GetAssetSha256(platform, variant));
        Assert.Throws<NotSupportedException>(() => CrispAsrCatalog.GetAssetSizeBytes(platform, variant));
    }

    [Theory]
    [InlineData("linux-x64", "vulkan", "cpu")]
    [InlineData("linux-x64", "cuda12", "cpu")]
    [InlineData("linux-x64", "cuda13", "cpu")]
    [InlineData("linux-x64", "hip", "cpu")]
    [InlineData("linux-x64", "cpu", "cpu-legacy")]
    [InlineData("linux-x64", "cpu-legacy", null)]
    [InlineData("win-x64", "vulkan", "cpu")]
    [InlineData("win-x64", "cpu", null)]        // no cpu-legacy asset offered on Windows
    [InlineData("linux-arm64", "cpu", null)]
    [InlineData("osx-arm64", "cpu", null)]
    [InlineData("linux-x64", "unknown", null)]
    public void GetFallbackVariant_WalksTowardsTheMostCompatibleBuild(string platform, string variant, string? expected)
    {
        Assert.Equal(expected, CrispAsrCatalog.GetFallbackVariant(platform, variant));
    }

    [Theory]
    [InlineData("linux-x64", "crispasr")]
    [InlineData("osx-arm64", "crispasr")]
    [InlineData("win-x64", "crispasr.exe")]
    public void ExecutableFileName_IsPlatformSpecific(string platform, string expected)
    {
        Assert.Equal(expected, CrispAsrCatalog.ExecutableFileName(platform));
    }
}

public class CrispAsrSetupServiceTests
{
    private static CrispAsrSetupService CreateService(string dataPath)
        => new(NullLogger.Instance, dataPath);

    private static string NewDataPath(string name)
        => Path.Combine(Path.GetTempPath(), "crispasr-" + name + "-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Layout_LivesUnderTheCrispAsrDataFolder()
    {
        var service = CreateService("/data");
        Assert.Equal(Path.Combine("/data", "crispasr"), service.RootDirectory);
        Assert.Equal(Path.Combine("/data", "crispasr", "bin"), service.BinDirectory);
        Assert.Equal(Path.Combine("/data", "crispasr", "models"), service.ModelsDirectory);
        Assert.Equal(Path.Combine("/data", "crispasr", "cache"), service.CacheDirectory);
    }

    // The exact validation command: the engine flags a real Canary run uses (backend, pinned cache,
    // no auto-aligner, explicit -l so crispasr does not fetch a language-ID model), no VAD.
    [Fact]
    public void BuildValidationArguments_IsTheExactProbeCommand()
    {
        var args = CrispAsrSetupService.BuildValidationArguments("/m/canary.gguf", "/p/silence.wav", "/c", "/p/out");
        Assert.Equal(new[]
        {
            "--backend", "canary",
            "-m", "/m/canary.gguf",
            "-f", "/p/silence.wav",
            "-l", "en",
            "-sl", "en",
            "-tl", "es",
            "--no-auto-aligner",
            "--cache-dir", "/c",
            "-osrt",
            "-of", "/p/out",
        }, args);
    }

    [Fact]
    public void DescribeProbeFailure_ExitZero_IsSuccess()
    {
        Assert.Null(CrispAsrSetupService.DescribeProbeFailure(0, "anything on stderr", "vulkan", "--help"));
    }

    [Fact]
    public void DescribeProbeFailure_MissingLibrary_NamesItAndSuggestsCpuForGpuBuilds()
    {
        var gpu = CrispAsrSetupService.DescribeProbeFailure(127,
            "crispasr: error while loading shared libraries: libvulkan.so.1: cannot open shared object file", "vulkan", "--help");
        Assert.NotNull(gpu);
        Assert.StartsWith("Missing libvulkan.so.1.", gpu);
        Assert.Contains("Try the CPU variant", gpu);

        var cpu = CrispAsrSetupService.DescribeProbeFailure(127, "", "cpu", "--help");
        Assert.NotNull(cpu);
        Assert.StartsWith("Missing a shared library.", cpu);
        Assert.DoesNotContain("Try the CPU variant", cpu);
    }

    [Theory]
    [InlineData(132)]
    [InlineData(134)]
    [InlineData(135)]
    public void DescribeProbeFailure_Crash_NamesTheProbe(int exitCode)
    {
        var message = CrispAsrSetupService.DescribeProbeFailure(exitCode, "", "cpu", "a Canary inference on silence");
        Assert.NotNull(message);
        Assert.Contains($"crashed during a Canary inference on silence (exit {exitCode})", message);
    }

    [Fact]
    public void DescribeProbeFailure_OtherExit_KeepsTheTailOfStderr()
    {
        var stderr = new string('x', 600) + "ggml_vulkan: no devices found";
        var message = CrispAsrSetupService.DescribeProbeFailure(1, stderr, "vulkan", "a Canary inference on silence");
        Assert.NotNull(message);
        Assert.StartsWith("crispasr failed a Canary inference on silence with exit code 1: ", message);
        Assert.EndsWith("ggml_vulkan: no devices found", message);

        Assert.Equal("crispasr failed --help with exit code 2.", CrispAsrSetupService.DescribeProbeFailure(2, null, "cpu", "--help"));
    }

    [Theory]
    [InlineData("/x/models/canary-1b-v2-q8_0.gguf", "q8_0")]
    [InlineData("/x/models/CANARY-1B-V2-Q5_0.GGUF", "q5_0")]
    [InlineData("/x/custom.gguf", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void InstalledModelKey_MapsByFileName(string? path, string expected)
    {
        Assert.Equal(expected, CrispAsrSetupService.InstalledModelKey(path));
    }

    [Fact]
    public void FindInstalledBinary_FindsTheNestedExecutable()
    {
        var root = NewDataPath("find");
        try
        {
            Assert.Null(CrispAsrSetupService.FindInstalledBinary(root, "linux-x64"));
            var nested = Path.Combine(root, "crispasr-linux-x86_64");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "crispasr-quantize"), "");
            Assert.Null(CrispAsrSetupService.FindInstalledBinary(root, "linux-x64"));
            File.WriteAllText(Path.Combine(nested, "crispasr"), "");
            Assert.Equal(Path.Combine(nested, "crispasr"), CrispAsrSetupService.FindInstalledBinary(root, "linux-x64"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadProgressLock_ReleasesAfterCatalogFailure()
    {
        var service = CreateService(NewDataPath("progress"));
        Assert.True(CrispAsrSetupService.TryAcquire("test-operation", "Starting"));
        Assert.False(CrispAsrSetupService.TryAcquire("second", "Busy"));
        var running = CrispAsrSetupService.CurrentProgress;
        Assert.True(running.IsRunning);
        Assert.Equal("test-operation", running.Operation);
        Assert.Equal("Starting", running.Message);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.DownloadBinaryAsync("unsupported", CancellationToken.None));

        var finished = CrispAsrSetupService.CurrentProgress;
        Assert.False(finished.IsRunning);
        Assert.Contains("No CrispASR release asset", finished.Error);
        Assert.Contains("Error downloading crispasr", finished.Message);
    }

    [Fact]
    public void PromoteStagedDirectory_ReplacesThePreviousInstall()
    {
        var dataPath = NewDataPath("promote");
        var service = CreateService(dataPath);
        var staging = Path.Combine(service.RootDirectory, "bin.staging-test");
        Directory.CreateDirectory(service.BinDirectory);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(service.BinDirectory, "old"), "old");
        File.WriteAllText(Path.Combine(staging, "new"), "new");
        try
        {
            var backup = service.PromoteStagedDirectory(staging);
            Assert.NotNull(backup);
            service.CompleteDirectoryPromotion(backup);

            Assert.False(File.Exists(Path.Combine(service.BinDirectory, "old")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(service.BinDirectory, "new")));
            Assert.False(Directory.Exists(staging));
            Assert.False(Directory.Exists(backup));
        }
        finally
        {
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public void PromoteStagedDirectory_FirstInstall_HasNoBackup()
    {
        var dataPath = NewDataPath("first");
        var service = CreateService(dataPath);
        var staging = Path.Combine(service.RootDirectory, "bin.staging-test");
        Directory.CreateDirectory(staging);
        try
        {
            Assert.Null(service.PromoteStagedDirectory(staging));
            Assert.True(Directory.Exists(service.BinDirectory));
            service.CompleteDirectoryPromotion(null);
        }
        finally
        {
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public void PromoteStagedDirectory_KeepsThePreviousInstallWhenPromotionFails()
    {
        var dataPath = NewDataPath("restore");
        var service = CreateService(dataPath);
        Directory.CreateDirectory(service.BinDirectory);
        File.WriteAllText(Path.Combine(service.BinDirectory, "known-good"), "old");
        try
        {
            Assert.ThrowsAny<Exception>(() => service.PromoteStagedDirectory(Path.Combine(service.RootDirectory, "missing-staging")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(service.BinDirectory, "known-good")));
        }
        finally
        {
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public void RollbackDirectoryPromotion_RestoresThePreviousInstall()
    {
        var dataPath = NewDataPath("rollback");
        var service = CreateService(dataPath);
        var staging = Path.Combine(service.RootDirectory, "bin.staging-test");
        Directory.CreateDirectory(service.BinDirectory);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(service.BinDirectory, "known-good"), "old");
        File.WriteAllText(Path.Combine(staging, "new"), "new");
        try
        {
            var backup = service.PromoteStagedDirectory(staging);
            service.RollbackDirectoryPromotion(backup);

            Assert.Equal("old", File.ReadAllText(Path.Combine(service.BinDirectory, "known-good")));
            Assert.False(File.Exists(Path.Combine(service.BinDirectory, "new")));
        }
        finally
        {
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public void SetupStatus_CarriesTheCanaryTargets()
    {
        var status = new CrispAsrSetupStatus();
        Assert.Same(CanaryCatalog.Targets, status.Targets);
    }
}
