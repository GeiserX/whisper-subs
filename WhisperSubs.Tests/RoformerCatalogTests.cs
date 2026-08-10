using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Providers;
using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

public class RoformerCatalogTests
{
    [Fact]
    public void Variants_ContainsExpectedEntries()
    {
        Assert.Equal(3, RoformerCatalog.Variants.Length);
        Assert.Contains(RoformerCatalog.Variants, v => v.Id == "cpu");
        Assert.Contains(RoformerCatalog.Variants, v => v.Id == "vulkan");
        Assert.Contains(RoformerCatalog.Variants, v => v.Id == "cuda12");
    }

    [Fact]
    public void Variants_CpuIsDefault()
    {
        var cpu = Assert.Single(RoformerCatalog.Variants, v => v.Id == "cpu");
        Assert.True(cpu.IsDefault);
    }

    [Theory]
    [InlineData("linux-x64", "cpu", "BSRoformer-linux-x64-cpu.tar.xz")]
    [InlineData("linux-x64", "vulkan", "BSRoformer-linux-vulkan.tar.xz")]
    [InlineData("linux-x64", "cuda12", "BSRoformer-linux-cuda-12.9.1.tar.xz")]
    [InlineData("linux-arm64", "cpu", "BSRoformer-linux-arm64-cpu.tar.xz")]
    [InlineData("osx-arm64", "cpu", "BSRoformer-macos-arm64.tar.xz")]
    [InlineData("osx-x64", "cpu", "BSRoformer-macos-x64.tar.xz")]
    [InlineData("win-x64", "cpu", "BSRoformer-windows-x64-msvc.zip")]
    public void GetAssetName_ReturnsCorrectName(string platform, string variant, string expected)
    {
        Assert.Equal(expected, RoformerCatalog.GetAssetName(platform, variant));
    }

    [Theory]
    [InlineData("win-x64", "vulkan", "BSRoformer-windows-vulkan.zip")]
    [InlineData("win-x64", "cuda12", "BSRoformer-windows-cuda-12.9.1.zip")]
    public void GetAssetName_WindowsGpuVariants_ReturnTheUpstreamZip(string platform, string variant, string expected)
    {
        Assert.Equal(expected, RoformerCatalog.GetAssetName(platform, variant));
    }

    [Fact]
    public void GetAssetName_UnsupportedCombination_Throws()
    {
        Assert.Throws<NotSupportedException>(() => RoformerCatalog.GetAssetName("linux-arm64", "cuda12"));
        Assert.Throws<NotSupportedException>(() => RoformerCatalog.GetAssetSha256("linux-arm64", "cuda12"));
        Assert.Throws<NotSupportedException>(() => RoformerCatalog.GetAssetSizeBytes("linux-arm64", "cuda12"));
    }

    [Theory]
    [InlineData("linux-x64", "cpu", "bc0f20237b9ed263582ebd0844dfc7dbb61309a67c31f4a8d7ba156e21292c77", 640204)]
    [InlineData("linux-x64", "vulkan", "bee2e9b5dd322b8d4fe1081583ce41709c2c1341550bbb557073c1216fef3e85", 5900692)]
    [InlineData("linux-x64", "cuda12", "448289d5162062ebd2fb7af3c1a1d88297438aef15b0e8369f6b101e82b4983c", 237738556)]
    [InlineData("linux-arm64", "cpu", "f77537a1b990d1d48036bc2c8ce8c354530ba2476903ec6177ed0d1fd60079ea", 583476)]
    [InlineData("osx-arm64", "cpu", "d0fc45181d31dcceea99c3828378f4a7376e52e7fdbc43e29a07e806785b4c9a", 636996)]
    [InlineData("osx-x64", "cpu", "9672c9c10128df822395fb7ef2a1a52f5ed7becbae2af349351a38a1af333379", 780976)]
    [InlineData("win-x64", "cpu", "e002811d56605bce6a51c275cf8f9ba447a3707771289ea6fbcca7f4d3e9ba1f", 671031)]
    [InlineData("win-x64", "vulkan", "a47653911eb17f68e65bce15aed6c4f1bd277a5fbd48293c4e54f0b005b869a7", 22779026)]
    [InlineData("win-x64", "cuda12", "7b8b93bc180e16f15124e4b739b0a9e9d038669e21fdf975f63823c02127bd70", 140056366)]
    public void GetAssetSha256_ReturnsPinnedDigest(
        string platform,
        string variant,
        string expectedSha256,
        long expectedSizeBytes)
    {
        Assert.Equal(expectedSha256, RoformerCatalog.GetAssetSha256(platform, variant));
        Assert.Equal(expectedSizeBytes, RoformerCatalog.GetAssetSizeBytes(platform, variant));
    }

    [Fact]
    public void GetAvailableVariants_LinuxX64_ReturnsAll()
    {
        var variants = RoformerCatalog.GetAvailableVariants("linux-x64");
        Assert.Equal(3, variants.Length);
    }

    [Theory]
    [InlineData("linux-arm64")]
    [InlineData("osx-arm64")]
    [InlineData("osx-x64")]
    public void GetAvailableVariants_OtherPlatforms_ReturnsCpuOnly(string platform)
    {
        var variants = RoformerCatalog.GetAvailableVariants(platform);
        Assert.Single(variants);
        Assert.Equal("cpu", variants[0].Id);
    }

    [Fact]
    public void GetAvailableVariants_WindowsX64_ReturnsAll()
    {
        var variants = RoformerCatalog.GetAvailableVariants("win-x64");
        Assert.Equal(3, variants.Length);
        Assert.Contains(variants, v => v.Id == "cpu");
        Assert.Contains(variants, v => v.Id == "vulkan");
        Assert.Contains(variants, v => v.Id == "cuda12");
    }

    [Fact]
    public void GetAvailableVariants_UnknownPlatform_ReturnsEmpty()
    {
        Assert.Empty(RoformerCatalog.GetAvailableVariants("unknown"));
    }

    [Theory]
    [InlineData("cuda12", "cpu")]
    [InlineData("vulkan", "cpu")]
    [InlineData("cpu", null)]
    [InlineData("unknown", null)]
    public void GetFallbackVariant_ReturnsExpected(string variant, string? expected)
    {
        Assert.Equal(expected, RoformerCatalog.GetFallbackVariant(variant));
    }

    [Theory]
    [InlineData("win-x64", "bs_roformer-cli.exe")]
    [InlineData("linux-x64", "bs_roformer-cli")]
    [InlineData("linux-arm64", "bs_roformer-cli")]
    [InlineData("osx-arm64", "bs_roformer-cli")]
    public void ExecutableFileName_ReturnsExpected(string platform, string expected)
    {
        Assert.Equal(expected, RoformerCatalog.ExecutableFileName(platform));
    }
}

public class RoformerModelCatalogTests
{
    [Fact]
    public void Models_ContainsExpectedQuantizations()
    {
        Assert.Equal(4, RoformerModelCatalog.Models.Length);
        Assert.Contains(RoformerModelCatalog.Models, m => m.Key == "q4_0");
        Assert.Contains(RoformerModelCatalog.Models, m => m.Key == "q5_1");
        Assert.Contains(RoformerModelCatalog.Models, m => m.Key == "q8_0");
        Assert.Contains(RoformerModelCatalog.Models, m => m.Key == "fp16");
    }

    [Fact]
    public void Models_OnlyQ8_0IsRecommended()
    {
        var recommended = RoformerModelCatalog.Models.Where(m => m.IsRecommended).ToArray();
        var only = Assert.Single(recommended);
        Assert.Equal("q8_0", only.Key);
    }

    [Fact]
    public void Resolve_KnownKey_ReturnsMatch()
    {
        var option = RoformerModelCatalog.Resolve("fp16");
        Assert.Equal("BSRoformer-anvuew-FP16.gguf", option.FileName);
    }

    [Fact]
    public void Resolve_KnownKey_IsCaseInsensitive()
    {
        var option = RoformerModelCatalog.Resolve("Q5_1");
        Assert.Equal("q5_1", option.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-quant")]
    public void Resolve_UnknownOrEmptyKey_FallsBackToDefault(string? key)
    {
        var option = RoformerModelCatalog.Resolve(key);
        Assert.Equal(RoformerModelCatalog.DefaultKey, option.Key);
    }

    [Fact]
    public void Models_HavePinnedRevisionSizeAndSha256()
    {
        Assert.Equal("df802a6773d25ba6ef785ff619daa3e510503168", RoformerModelCatalog.HuggingFaceRevision);
        Assert.Collection(
            RoformerModelCatalog.Models,
            model => AssertModelMetadata(
                model,
                "BSRoformer-anvuew-Q4_0.gguf",
                30370912,
                "7b7a7e62ea021170621fe7373fc8f0086ce8b33681c115b1e93f905b4a15eac9"),
            model => AssertModelMetadata(
                model,
                "BSRoformer-anvuew-Q5_1.gguf",
                39855712,
                "6fd0b2a9fc881649a67983f8dc0764f218ec3f8db1a1db2a034da25de764f5c2"),
            model => AssertModelMetadata(
                model,
                "BSRoformer-anvuew-Q8_0.gguf",
                55663712,
                "f0b0093b29ec92aaf6a866973996953a6687df02e0cad68a3833672060c177af"),
            model => AssertModelMetadata(
                model,
                "BSRoformer-anvuew-FP16.gguf",
                102430304,
                "bb25b9fded9780ca19bbc86db17669fb975b142b5e280d850806959130bcd19f"));
        Assert.All(RoformerModelCatalog.Models, model =>
        {
            Assert.False(string.IsNullOrWhiteSpace(model.Key));
            Assert.False(string.IsNullOrWhiteSpace(model.FileName));
            Assert.False(string.IsNullOrWhiteSpace(model.DisplayName));
            Assert.True(model.SizeMB > 0);
            Assert.True(model.SizeBytes > 0);
            Assert.Matches("^[0-9a-f]{64}$", model.Sha256);
            Assert.False(string.IsNullOrWhiteSpace(model.Description));
            _ = model.IsRecommended;
        });
    }

    private static void AssertModelMetadata(
        RoformerModelOption model,
        string expectedFileName,
        long expectedSizeBytes,
        string expectedSha256)
    {
        Assert.Equal(expectedFileName, model.FileName);
        Assert.Equal(expectedSizeBytes, model.SizeBytes);
        Assert.Equal(expectedSha256, model.Sha256);
    }
}

public class RoformerSetupSafetyTests
{
    private static VocalSeparationSetupService CreateService(string dataPath)
        => new(new NullLogger<VocalSeparationSetupService>(), dataPath);

    [Fact]
    public void SetupStatus_StoresEveryReportedValue()
    {
        var gpu = new GpuInfo { HasNvidia = true };
        var status = new RoformerSetupStatus
        {
            BinaryFound = true,
            BinaryPath = "/bin/roformer",
            ModelFound = true,
            ModelPath = "/models/model.gguf",
            Platform = "linux-x64",
            SetupComplete = true,
            InstalledVariant = "cuda12",
            InstalledModelQuant = "q8_0",
            Gpu = gpu
        };

        Assert.True(status.BinaryFound);
        Assert.Equal("/bin/roformer", status.BinaryPath);
        Assert.True(status.ModelFound);
        Assert.Equal("/models/model.gguf", status.ModelPath);
        Assert.Equal("linux-x64", status.Platform);
        Assert.True(status.SetupComplete);
        Assert.Equal("cuda12", status.InstalledVariant);
        Assert.Equal("q8_0", status.InstalledModelQuant);
        Assert.Same(gpu, status.Gpu);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(9, 0)]
    [InlineData(80, 0)]
    public void NormalizeOverlap_EnforcesSupportedRange(int input, int expected)
    {
        Assert.Equal(expected, VocalSeparationProvider.NormalizeOverlap(input));
    }

    [Theory]
    [InlineData("libggml.so.0.15.1", "linux-x64", "libggml.so.0")]
    [InlineData("libggml-base.so.0.15.1", "linux-x64", "libggml-base.so.0")]
    [InlineData("libggml-cpu.so.0.15.1", "linux-arm64", "libggml-cpu.so.0")]
    [InlineData("libggml-vulkan.so.0.15.1", "linux-x64", "libggml-vulkan.so.0")]
    [InlineData("libggml.0.15.1.dylib", "osx-arm64", "libggml.0.dylib")]
    [InlineData("libggml-metal.0.15.1.dylib", "osx-x64", "libggml-metal.0.dylib")]
    [InlineData("libggml.so.0", "linux-x64", null)]
    [InlineData("other.so.0.15.1", "linux-x64", null)]
    [InlineData("libggml.so.0.15.1", "win-x64", null)]
    public void GetGgmlSonameLinkName_MapsVersionedLibraries(
        string fileName,
        string platform,
        string? expected)
    {
        Assert.Equal(expected, VocalSeparationSetupService.GetGgmlSonameLinkName(fileName, platform));
    }

    [Theory]
    [InlineData("linux-x64", "LD_LIBRARY_PATH")]
    [InlineData("linux-arm64", "LD_LIBRARY_PATH")]
    [InlineData("osx-arm64", "DYLD_LIBRARY_PATH")]
    [InlineData("osx-x64", "DYLD_LIBRARY_PATH")]
    [InlineData("win-x64", null)]
    public void GetLibraryPathVariable_IsPlatformSpecific(string platform, string? expected)
    {
        Assert.Equal(expected, RoformerRuntime.GetLibraryPathVariable(platform));
    }

    [Fact]
    public void ConfigureLibraryPath_AddsTheBinaryDirectoryAndPreservesExistingEntries()
    {
        var startInfo = new ProcessStartInfo();
        var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? "linux-current"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "osx-current"
                : "other";
        var variable = RoformerRuntime.GetLibraryPathVariable(platform);
        var binaryPath = Path.Combine(Path.GetTempPath(), "roformer-runtime", "bs_roformer-cli");

        RoformerRuntime.ConfigureLibraryPath(startInfo, binaryPath);

        if (variable == null)
        {
            Assert.DoesNotContain("LD_LIBRARY_PATH", startInfo.Environment.Keys);
            Assert.DoesNotContain("DYLD_LIBRARY_PATH", startInfo.Environment.Keys);
            return;
        }

        var configured = startInfo.Environment[variable];
        Assert.NotNull(configured);
        Assert.StartsWith(Path.GetDirectoryName(binaryPath)!, configured, StringComparison.Ordinal);
        var inherited = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(inherited))
        {
            Assert.EndsWith(Path.PathSeparator + inherited, configured, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProviderConfiguration_RequiresExistingBinaryAndModel()
    {
        var root = Path.Combine(Path.GetTempPath(), "roformer-provider-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var binary = Path.Combine(root, "bs_roformer-cli");
        var model = Path.Combine(root, "model.gguf");
        try
        {
            var provider = new VocalSeparationProvider(
                NullLogger<VocalSeparationProvider>.Instance,
                binary,
                model,
                overlap: 80,
                chunkSize: -1);
            Assert.False(provider.IsConfigured);

            File.WriteAllText(binary, "binary");
            File.WriteAllText(model, "model");
            Assert.True(provider.IsConfigured);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SeparateAsync_FakeCliProducesOutputAndReceivesTuningArguments()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var root = Path.Combine(Path.GetTempPath(), "roformer-provider-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var binary = CreateShellScript(root,
            "printf '%s\\n' \"$@\" > \"${3}.args\"\nprintf output > \"$3\"\n");
        var model = Path.Combine(root, "model.gguf");
        var input = Path.Combine(root, "input.wav");
        var output = Path.Combine(root, "output.wav");
        File.WriteAllText(model, "model");
        File.WriteAllText(input, "input");
        try
        {
            var provider = new VocalSeparationProvider(
                NullLogger<VocalSeparationProvider>.Instance,
                binary,
                model,
                overlap: 8,
                chunkSize: 123,
                minTimeoutSeconds: 1);

            Assert.True(await provider.SeparateAsync(input, output, CancellationToken.None));
            Assert.Equal("output", File.ReadAllText(output));
            var arguments = File.ReadAllLines(output + ".args");
            Assert.Equal(new[] { model, input, output, "--overlap", "8", "--chunk-size", "123" }, arguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SeparateAsync_CancellationTerminatesTheFakeCli()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var root = Path.Combine(Path.GetTempPath(), "roformer-provider-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var binary = CreateShellScript(root, "sleep 30\n");
        var model = Path.Combine(root, "model.gguf");
        var input = Path.Combine(root, "input.wav");
        var output = Path.Combine(root, "output.wav");
        File.WriteAllText(model, "model");
        File.WriteAllText(input, "input");
        try
        {
            var provider = new VocalSeparationProvider(
                NullLogger<VocalSeparationProvider>.Instance,
                binary,
                model,
                overlap: 0,
                chunkSize: -1);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                provider.SeparateAsync(input, output, cancellation.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateShellScript(string directory, string body)
    {
        var path = Path.Combine(directory, "fake-roformer.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return path;
    }

    [Fact]
    public async Task DownloadProgressLock_ReleasesAfterCatalogFailure()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "roformer-progress-" + Guid.NewGuid().ToString("N"));
        var service = CreateService(dataPath);
        Assert.True(VocalSeparationSetupService.TryAcquire("test-operation", "Starting"));
        Assert.False(VocalSeparationSetupService.TryAcquire("second", "Busy"));
        var running = VocalSeparationSetupService.CurrentProgress;
        Assert.True(running.IsRunning);
        Assert.Equal("test-operation", running.Operation);
        Assert.Equal("Starting", running.Message);
        Assert.Equal(0, running.Percent);
        Assert.Null(running.Error);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.DownloadBinaryAsync("unsupported", CancellationToken.None));

        var finished = VocalSeparationSetupService.CurrentProgress;
        Assert.False(finished.IsRunning);
        Assert.Contains("No BSRoformer.cpp release asset", finished.Error);
        Assert.Contains("Error downloading bs_roformer-cli", finished.Message);
    }

    [Fact]
    public void FindInstalledBinary_RecursesWithinManagedBinDirectory()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "roformer-find-" + Guid.NewGuid().ToString("N"));
        var service = CreateService(dataPath);
        try
        {
            Assert.Null(service.FindInstalledBinary());
            var nested = Path.Combine(service.BinDirectory, "archive", "bin");
            Directory.CreateDirectory(nested);
            var executable = Path.Combine(nested,
                RoformerCatalog.ExecutableFileName(VocalSeparationSetupService.GetPlatformIdentifier()));
            File.WriteAllText(executable, "binary");

            Assert.Equal(executable, service.FindInstalledBinary());
            Assert.Equal(Path.Combine(dataPath, "vocal-separation", "models"), service.ModelsDirectory);
        }
        finally
        {
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public void ValidateBinary_ReportsSuccessLibraryCrashGenericAndStartFailures()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var root = Path.Combine(Path.GetTempPath(), "roformer-validate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var service = CreateService(root);
        try
        {
            var success = CreateShellScript(root, "exit 0\n");
            Assert.Null(service.ValidateBinary(success, "cpu"));

            var missingLibrary = CreateShellScript(root,
                "printf 'error while loading shared libraries: libmissing.so.1: not found\\n' >&2\nexit 127\n");
            var missingError = service.ValidateBinary(missingLibrary, "cpu");
            Assert.Contains("Missing libmissing.so.1", missingError);
            Assert.Contains("Install it", missingError);

            var crash = CreateShellScript(root, "exit 134\n");
            Assert.Contains("crashed on launch (exit 134)", service.ValidateBinary(crash, "vulkan"));

            var generic = CreateShellScript(root, "printf 'bad option' >&2\nexit 9\n");
            Assert.Contains("exited with code 9: bad option", service.ValidateBinary(generic, "cpu"));

            var startFailure = service.ValidateBinary(Path.Combine(root, "missing-cli"), "cpu");
            Assert.Contains("Could not launch bs_roformer-cli", startFailure);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompletePromotions_RemovePreviousBackupsAfterCommit()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "roformer-complete-" + Guid.NewGuid().ToString("N"));
        var service = CreateService(dataPath);
        var staging = Path.Combine(service.RootDirectory, "bin.staging-test");
        Directory.CreateDirectory(service.BinDirectory);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(service.BinDirectory, "old"), "old");
        File.WriteAllText(Path.Combine(staging, "new"), "new");
        var model = Path.Combine(dataPath, "model.gguf");
        var incomingModel = Path.Combine(dataPath, "model.downloading");
        File.WriteAllText(model, "old-model");
        File.WriteAllText(incomingModel, "new-model");
        try
        {
            var binaryBackup = service.PromoteStagedDirectory(staging);
            var modelBackup = VocalSeparationSetupService.PromoteDownloadedFile(incomingModel, model);
            Assert.True(Directory.Exists(binaryBackup));
            Assert.True(File.Exists(modelBackup));

            service.CompleteDirectoryPromotion(binaryBackup);
            VocalSeparationSetupService.CompleteDownloadedFilePromotion(modelBackup);

            Assert.False(Directory.Exists(binaryBackup));
            Assert.False(File.Exists(modelBackup));
            Assert.Equal("new-model", File.ReadAllText(model));
        }
        finally
        {
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public void VerifySha256_AcceptsExpectedDigestAndRejectsMismatch()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "hello world");
            VocalSeparationSetupService.VerifySha256(
                path,
                "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9",
                "test asset");
            Assert.Throws<InvalidDataException>(() =>
                VocalSeparationSetupService.VerifySha256(path, new string('0', 64), "test asset"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("BSRoformer-windows-x64-msvc.zip", true)]
    [InlineData("BSRoformer-windows-x64-msvc.zip.downloading", false)]
    [InlineData("BSRoformer-linux-x64-cpu.tar.xz", false)]
    public void IsZipArchiveName_UsesOriginalAssetName(string assetName, bool expected)
    {
        Assert.Equal(expected, VocalSeparationSetupService.IsZipArchiveName(assetName));
    }

    [Fact]
    public void DownloadSizeGuards_RejectUnexpectedHeadersAndStreamingOverflow()
    {
        VocalSeparationSetupService.ValidateContentLength(-1, 100, "asset");
        VocalSeparationSetupService.ValidateContentLength(100, 100, "asset");
        VocalSeparationSetupService.EnsureDownloadSize(100, 100, "asset");

        Assert.Throws<InvalidDataException>(() =>
            VocalSeparationSetupService.ValidateContentLength(101, 100, "asset"));
        Assert.Throws<InvalidDataException>(() =>
            VocalSeparationSetupService.EnsureDownloadSize(101, 100, "asset"));
    }

    [Fact]
    public void VerifyGgufMagic_RejectsNonGgufContent()
    {
        var validPath = Path.GetTempFileName();
        var invalidPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(validPath, new byte[] { (byte)'G', (byte)'G', (byte)'U', (byte)'F', 3, 0, 0, 0 });
            File.WriteAllText(invalidPath, "not a model");

            VocalSeparationSetupService.VerifyGgufMagic(validPath, "valid.gguf");
            Assert.Throws<InvalidDataException>(() =>
                VocalSeparationSetupService.VerifyGgufMagic(invalidPath, "invalid.gguf"));
        }
        finally
        {
            File.Delete(validPath);
            File.Delete(invalidPath);
        }
    }

    [Fact]
    public void PromoteDownloadedFile_RestoresPreviousModelWhenPromotionFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "roformer-model-promote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "model.gguf");
        File.WriteAllText(destination, "known-good");
        try
        {
            Assert.ThrowsAny<Exception>(() => VocalSeparationSetupService.PromoteDownloadedFile(
                Path.Combine(root, "missing.download"), destination));

            Assert.Equal("known-good", File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RollbackDownloadedFilePromotion_RestoresPreviousModelAfterSuccessfulSwap()
    {
        var root = Path.Combine(Path.GetTempPath(), "roformer-model-rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "model.gguf");
        var incoming = Path.Combine(root, "model.downloading");
        File.WriteAllText(destination, "known-good");
        File.WriteAllText(incoming, "new");
        try
        {
            var backup = VocalSeparationSetupService.PromoteDownloadedFile(incoming, destination);
            VocalSeparationSetupService.RollbackDownloadedFilePromotion(destination, backup);

            Assert.Equal("known-good", File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("linux-x64", "libggml.so.0.15.1", "libggml.so.0")]
    [InlineData("linux-x64", "libggml-base.so.0.15.1", "libggml-base.so.0")]
    [InlineData("linux-x64", "libggml-cpu.so.0.15.1", "libggml-cpu.so.0")]
    [InlineData("osx-arm64", "libggml.0.15.1.dylib", "libggml.0.dylib")]
    [InlineData("osx-arm64", "libggml-metal.0.15.1.dylib", "libggml-metal.0.dylib")]
    public void RepairGgmlLibraryLinks_CreatesEveryRequiredAlias(
        string platform,
        string versionedName,
        string aliasName)
    {
        var root = Path.Combine(Path.GetTempPath(), "roformer-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, versionedName), "library");
            CreateService(root).RepairGgmlLibraryLinks(root, platform);

            Assert.True(File.Exists(Path.Combine(root, aliasName)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PromoteStagedDirectory_ReplacesPreviousInstallAfterValidationStage()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "roformer-promote-" + Guid.NewGuid().ToString("N"));
        var service = CreateService(dataPath);
        var staging = Path.Combine(service.RootDirectory, "bin.staging-test");
        Directory.CreateDirectory(service.BinDirectory);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(service.BinDirectory, "old"), "old");
        File.WriteAllText(Path.Combine(staging, "new"), "new");
        try
        {
            service.PromoteStagedDirectory(staging);

            Assert.False(File.Exists(Path.Combine(service.BinDirectory, "old")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(service.BinDirectory, "new")));
            Assert.False(Directory.Exists(staging));
        }
        finally
        {
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public void PromoteStagedDirectory_RestoresPreviousInstallWhenPromotionFails()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "roformer-restore-" + Guid.NewGuid().ToString("N"));
        var service = CreateService(dataPath);
        var missingStaging = Path.Combine(service.RootDirectory, "missing-staging");
        Directory.CreateDirectory(service.BinDirectory);
        File.WriteAllText(Path.Combine(service.BinDirectory, "known-good"), "old");
        try
        {
            Assert.ThrowsAny<Exception>(() => service.PromoteStagedDirectory(missingStaging));

            Assert.Equal("old", File.ReadAllText(Path.Combine(service.BinDirectory, "known-good")));
        }
        finally
        {
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public void RollbackDirectoryPromotion_RestoresPreviousInstallAfterSuccessfulSwap()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "roformer-directory-rollback-" + Guid.NewGuid().ToString("N"));
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
}
