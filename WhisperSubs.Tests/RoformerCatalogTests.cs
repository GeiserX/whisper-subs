using System;
using System.Linq;
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
}
