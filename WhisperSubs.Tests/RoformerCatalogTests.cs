using System;
using System.IO;
using System.Linq;
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
    }

    [Theory]
    [InlineData("linux-x64", "cpu")]
    [InlineData("linux-x64", "vulkan")]
    [InlineData("linux-x64", "cuda12")]
    [InlineData("linux-arm64", "cpu")]
    [InlineData("osx-arm64", "cpu")]
    [InlineData("osx-x64", "cpu")]
    [InlineData("win-x64", "cpu")]
    [InlineData("win-x64", "vulkan")]
    [InlineData("win-x64", "cuda12")]
    public void GetAssetSha256_ReturnsPinnedDigest(string platform, string variant)
    {
        Assert.Matches("^[0-9a-f]{64}$", RoformerCatalog.GetAssetSha256(platform, variant));
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
        Assert.Matches("^[0-9a-f]{40}$", RoformerModelCatalog.HuggingFaceRevision);
        Assert.All(RoformerModelCatalog.Models, model =>
        {
            Assert.True(model.SizeBytes > 0);
            Assert.Matches("^[0-9a-f]{64}$", model.Sha256);
        });
    }
}

public class RoformerSetupSafetyTests
{
    private static VocalSeparationSetupService CreateService(string dataPath)
        => new(new NullLogger<VocalSeparationSetupService>(), dataPath);

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
}
