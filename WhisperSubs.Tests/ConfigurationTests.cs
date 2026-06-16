using System.Text.Json;
using WhisperSubs.Configuration;
using Xunit;

namespace WhisperSubs.Tests;

public class ConfigurationTests
{
    [Fact]
    public void PluginConfiguration_DefaultValues()
    {
        var config = new PluginConfiguration();

        Assert.Equal("", config.WhisperModelPath);
        Assert.Equal("", config.WhisperBinaryPath);
        Assert.False(config.EnableAutoGeneration);
        Assert.Equal("auto", config.DefaultLanguage);
        Assert.Equal(SubtitleMode.Full, config.SubtitleMode);
        Assert.False(config.EnableLyricsGeneration);
        Assert.False(config.PauseOnPlayback);
        Assert.Equal(0, config.WhisperThreadCount);
        Assert.NotNull(config.EnabledLibraries);
        Assert.Empty(config.EnabledLibraries);

        // Issue #83: the two "which subtitles to generate" toggles default ON so behavior is
        // unchanged for existing users (both = generate original-language + English, as before).
        Assert.True(config.GenerateOriginalLanguageSubtitles);
        Assert.True(config.GenerateEnglishSubtitles);
        Assert.False(config.CountImageSubtitlesAsPresent);
    }

    [Fact]
    public void GenerationToggles_AbsentFromJson_DefaultToOn()
    {
        // Existing users' saved config has no Generate* keys. Deserialization must default them to
        // true (true.json round-trip), or an upgrade would silently stop generating subtitles.
        var config = JsonSerializer.Deserialize<PluginConfiguration>("{}");
        Assert.NotNull(config);
        Assert.True(config!.GenerateOriginalLanguageSubtitles);
        Assert.True(config.GenerateEnglishSubtitles);
        Assert.False(config.CountImageSubtitlesAsPresent);
    }

    [Fact]
    public void GenerationToggles_RoundTripThroughJson()
    {
        var original = new PluginConfiguration
        {
            GenerateOriginalLanguageSubtitles = false,
            GenerateEnglishSubtitles = true,
            CountImageSubtitlesAsPresent = true
        };
        var restored = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(original));
        Assert.NotNull(restored);
        Assert.False(restored!.GenerateOriginalLanguageSubtitles);
        Assert.True(restored.GenerateEnglishSubtitles);
        Assert.True(restored.CountImageSubtitlesAsPresent);
    }

    [Fact]
    public void SubtitleMode_HasExpectedValues()
    {
        Assert.Equal(0, (int)SubtitleMode.Full);
        Assert.Equal(1, (int)SubtitleMode.ForcedOnly);
        Assert.Equal(2, (int)SubtitleMode.FullAndForced);
        Assert.Equal(3, (int)SubtitleMode.TranslationOnly);
    }

    [Fact]
    public void SubtitleMode_AllValuesAreDefined()
    {
        var values = Enum.GetValues<SubtitleMode>();
        Assert.Equal(4, values.Length);
    }

    [Theory]
    [InlineData("{\"SubtitleMode\": null}", SubtitleMode.Full)]
    [InlineData("{\"SubtitleMode\": 0}", SubtitleMode.Full)]
    [InlineData("{\"SubtitleMode\": 1}", SubtitleMode.ForcedOnly)]
    [InlineData("{\"SubtitleMode\": 2}", SubtitleMode.FullAndForced)]
    [InlineData("{\"SubtitleMode\": 3}", SubtitleMode.TranslationOnly)]
    [InlineData("{\"SubtitleMode\": 99}", SubtitleMode.Full)]
    [InlineData("{\"SubtitleMode\": -1}", SubtitleMode.Full)]
    [InlineData("{\"SubtitleMode\": \"ForcedOnly\"}", SubtitleMode.ForcedOnly)]
    [InlineData("{\"SubtitleMode\": \"TranslationOnly\"}", SubtitleMode.TranslationOnly)]
    public void SubtitleModeConverter_HandlesEdgeCases(string json, SubtitleMode expected)
    {
        var config = JsonSerializer.Deserialize<PluginConfiguration>(json);
        Assert.NotNull(config);
        Assert.Equal(expected, config!.SubtitleMode);
    }
}
