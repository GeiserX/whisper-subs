using System;
using System.Linq;
using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

public class CanaryCatalogTests
{
    [Fact]
    public void Models_AreQ8AndQ5WithPinnedSizesAndDigests()
    {
        Assert.Equal(new[] { "q8_0", "q5_0" }, CanaryCatalog.Models.Select(m => m.Key).ToArray());

        var q8 = CanaryCatalog.Models[0];
        Assert.Equal("canary-1b-v2-q8_0.gguf", q8.FileName);
        Assert.Equal(1049050784, q8.SizeBytes);
        Assert.Equal("81421a82cdcb23746f6c3e8c2859094f7e8c27dd8ba77d4b8f8440395fbc7c7a", q8.Sha256);

        var q5 = CanaryCatalog.Models[1];
        Assert.Equal("canary-1b-v2-q5_0.gguf", q5.FileName);
        Assert.Equal(720322208, q5.SizeBytes);
        Assert.Equal("e312694d877c0df7efe8ffc974cdf74bf06605c154552af214ee279741325703", q5.Sha256);
    }

    [Fact]
    public void Models_OnlyTheDefaultIsRecommended()
    {
        var recommended = Assert.Single(CanaryCatalog.Models, m => m.IsRecommended);
        Assert.Equal(CanaryCatalog.DefaultKey, recommended.Key);
        Assert.Equal("q8_0", CanaryCatalog.DefaultKey);
    }

    [Fact]
    public void Models_SizeMbAgreesWithSizeBytes()
    {
        foreach (var model in CanaryCatalog.Models)
        {
            Assert.Equal(Math.Round(model.SizeBytes / 1_000_000.0), model.SizeMB);
        }
    }

    [Fact]
    public void BaseUrl_PinsTheRepositoryRevision()
    {
        Assert.Equal(
            "https://huggingface.co/cstr/canary-1b-v2-GGUF/resolve/f4a12db73daa964aa56a188826682f6b11fdc960",
            CanaryCatalog.HuggingFaceBaseUrl);
    }

    [Theory]
    [InlineData("q5_0", "q5_0")]
    [InlineData("Q5_0", "q5_0")]
    [InlineData("q8_0", "q8_0")]
    [InlineData("f16", "q8_0")]
    [InlineData("", "q8_0")]
    [InlineData(null, "q8_0")]
    public void Resolve_FallsBackToTheDefault(string? key, string expected)
    {
        Assert.Equal(expected, CanaryCatalog.Resolve(key).Key);
    }

    [Fact]
    public void Targets_AreTheTwentyFourNonEnglishCanaryLanguages()
    {
        Assert.Equal(
            new[] { "bg", "hr", "cs", "da", "nl", "et", "fi", "fr", "de", "el", "hu", "it", "lv", "lt", "mt", "pl", "pt", "ro", "sk", "sl", "es", "sv", "ru", "uk" },
            CanaryCatalog.Targets.Select(t => t.Code).ToArray());
        Assert.DoesNotContain(CanaryCatalog.Targets, t => t.Code == "en");
        Assert.All(CanaryCatalog.Targets, t => Assert.False(string.IsNullOrWhiteSpace(t.DisplayName)));
        Assert.Equal(24, CanaryCatalog.Targets.Select(t => t.DisplayName).Distinct().Count());
    }

    [Theory]
    [InlineData("nl", true)]
    [InlineData("NL", true)]
    [InlineData(" lv ", true)]
    [InlineData("uk", true)]
    [InlineData("en", false)]
    [InlineData("ja", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTarget_MatchesTheList(string? code, bool expected)
    {
        Assert.Equal(expected, CanaryCatalog.IsTarget(code));
    }
}
