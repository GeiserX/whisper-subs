using System;
using System.Linq;
using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

public class Qwen3CatalogTests
{
    [Fact]
    public void Models_AreQ8AndQ4KWithPinnedSizesAndDigests()
    {
        Assert.Equal(new[] { "q8_0", "q4_k" }, Qwen3Catalog.Models.Select(m => m.Key).ToArray());

        var q8 = Qwen3Catalog.Models[0];
        Assert.Equal("qwen3-asr-1.7b-q8_0.gguf", q8.FileName);
        Assert.Equal(2506723200, q8.SizeBytes);
        Assert.Equal("9851ab996591a2d0cb0efb216002764b509c86bd40c95e613d7b65b8e69c8a6e", q8.Sha256);

        var q4 = Qwen3Catalog.Models[1];
        Assert.Equal("qwen3-asr-1.7b-q4_k.gguf", q4.FileName);
        Assert.Equal(1490915200, q4.SizeBytes);
        Assert.Equal("ec197cef7ccc589fdcae1becc3f4a3de119d0a41e790b898b519b1a048dad8d4", q4.Sha256);
    }

    [Fact]
    public void Models_OnlyTheDefaultIsRecommended()
    {
        var recommended = Assert.Single(Qwen3Catalog.Models, m => m.IsRecommended);
        Assert.Equal(Qwen3Catalog.DefaultKey, recommended.Key);
        Assert.Equal("q8_0", Qwen3Catalog.DefaultKey);
    }

    [Fact]
    public void Models_SizeMbAgreesWithSizeBytes()
    {
        foreach (var model in Qwen3Catalog.Models)
        {
            Assert.Equal(Math.Round(model.SizeBytes / 1_000_000.0), model.SizeMB);
        }
    }

    [Fact]
    public void BaseUrl_IsPinnedToARevisionNotABranch()
    {
        Assert.Matches("^[0-9a-f]{40}$", Qwen3Catalog.HuggingFaceRevision);
        Assert.Equal("https://huggingface.co/cstr/qwen3-asr-1.7b-GGUF/resolve/" + Qwen3Catalog.HuggingFaceRevision, Qwen3Catalog.HuggingFaceBaseUrl);
        Assert.DoesNotContain("/main", Qwen3Catalog.HuggingFaceBaseUrl);
    }

    [Theory]
    [InlineData(null, "q8_0")]
    [InlineData("", "q8_0")]
    [InlineData("nope", "q8_0")]
    [InlineData("Q4_K", "q4_k")]
    [InlineData("q8_0", "q8_0")]
    public void Resolve_FallsBackToTheDefault(string? key, string expected)
    {
        Assert.Equal(expected, Qwen3Catalog.Resolve(key).Key);
    }

    [Theory]
    [InlineData("qwen3", true)]
    [InlineData(" QWEN3 ", true)]
    [InlineData("whisper", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("canary", false)]
    public void IsSelected_OnlyForTheQwen3Key(string? engine, bool expected)
    {
        Assert.Equal(expected, Qwen3Catalog.IsSelected(engine));
    }

    // The saved key for the default engine must be the one a fresh config carries, or an untouched
    // install would read as "not whisper".
    [Fact]
    public void FreshConfig_SelectsWhisper()
    {
        var config = new WhisperSubs.Configuration.PluginConfiguration();
        Assert.Equal(Qwen3Catalog.WhisperEngineKey, config.TranscriptionEngine);
        Assert.False(Qwen3Catalog.IsSelected(config.TranscriptionEngine));
        Assert.Equal("", config.Qwen3ModelPath);
    }
}
