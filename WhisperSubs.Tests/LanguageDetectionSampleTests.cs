using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Covers <see cref="SubtitleManager.ClampDetectionSeconds"/> — the pure helper that bounds the
/// audio window sent for a forced-subtitle language-DETECTION probe to a short leading window,
/// so a long/noisy chunk can't drive a slow or runaway whisper decode. See
/// <see cref="WhisperSubs.Configuration.PluginConfiguration.LanguageDetectionSampleSeconds"/>.
/// </summary>
public class LanguageDetectionSampleTests
{
    [Fact]
    public void Configured_LessThanChunk_ClampsToConfigured()
    {
        var seconds = SubtitleManager.ClampDetectionSeconds(configured: 15, chunkSeconds: 45.0);
        Assert.Equal(15.0, seconds);
    }

    [Fact]
    public void Configured_GreaterThanChunk_ClampsToChunkLength()
    {
        var seconds = SubtitleManager.ClampDetectionSeconds(configured: 15, chunkSeconds: 8.0);
        Assert.Equal(8.0, seconds);
    }

    [Fact]
    public void Configured_EqualsChunk_ReturnsChunkLength()
    {
        var seconds = SubtitleManager.ClampDetectionSeconds(configured: 30, chunkSeconds: 30.0);
        Assert.Equal(30.0, seconds);
    }

    [Fact]
    public void Zero_MeansWholeChunk()
    {
        var seconds = SubtitleManager.ClampDetectionSeconds(configured: 0, chunkSeconds: 62.0);
        Assert.Equal(62.0, seconds);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-15)]
    public void Negative_TreatedAsWholeChunk(int configured)
    {
        var seconds = SubtitleManager.ClampDetectionSeconds(configured, chunkSeconds: 45.0);
        Assert.Equal(45.0, seconds);
    }

    [Fact]
    public void DefaultConfigValue_ClampsA62SecondChunkToDefaultWindow()
    {
        // A fresh PluginConfiguration defaults LanguageDetectionSampleSeconds to whisper's own ~30s
        // language-detection window, so an oversized 62s chunk is bounded to 30s for detection.
        var cfg = new WhisperSubs.Configuration.PluginConfiguration();
        Assert.Equal(30, cfg.LanguageDetectionSampleSeconds);
        var seconds = SubtitleManager.ClampDetectionSeconds(cfg.LanguageDetectionSampleSeconds, chunkSeconds: 62.0);
        Assert.Equal(30.0, seconds);
    }
}
