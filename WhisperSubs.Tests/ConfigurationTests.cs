using System.Text.Json;
using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

public class ConfigurationTests
{
    [Fact]
    public void JobTimeoutDefaults_AreSafe()
    {
        // v4.0 resilience: per-call deadline policy defaults (closes the 70h-stuck class).
        var config = new PluginConfiguration();
        Assert.Equal(6.0, config.JobTimeoutRealtimeFactor);
        Assert.Equal(60, config.JobMinTimeoutSeconds);
        Assert.Equal(12, config.JobMaxTimeoutHours);
    }

    [Fact]
    public void WorkerPoolDefaults_AreSimpleAndEmpty()
    {
        // Simple by default: no extra workers + local worker on ⇒ today's single-server behaviour (v4.0).
        var config = new PluginConfiguration();
        Assert.NotNull(config.Workers);
        Assert.Empty(config.Workers);
        Assert.True(config.EnableLocalWorker);
    }

    [Fact]
    public void WhisperWorker_Defaults()
    {
        var w = new WhisperWorker();
        Assert.True(w.Enabled);
        Assert.Equal(1, w.MaxConcurrency);
        Assert.Equal(0, w.CostWeight);
        Assert.True(w.CanTranslate);
    }

    [Fact]
    public void RequestQueueDefaults_AreSafeAndOptIn()
    {
        var config = new PluginConfiguration();
        // The whole user-request path is off by default; existing installs are unchanged (#112).
        Assert.False(config.AllowUserRequests);
        Assert.False(config.AutoApproveUserRequests);
        Assert.Equal(PriorityTier.High, config.AdminRequestTier);
        Assert.Equal(PriorityTier.Medium, config.UserRequestTier);
        Assert.Equal(PriorityTier.Background, config.BackgroundSweepTier);
        Assert.Equal(5, config.UserRequestDailyQuota);
        Assert.Equal(24, config.UserRequestQuotaWindowHours);
        Assert.Equal(3, config.UserRequestActiveCap);
        Assert.Equal(200, config.UserRequestMaxItemsPerRequest);
        Assert.Equal(500, config.UserRequestGlobalCap);
    }

    [Fact]
    public void RequestTiers_AbsentFromJson_KeepSafeDefaults()
    {
        // Existing installs' config has no request keys — an upgrade must not enable the feature.
        var config = JsonSerializer.Deserialize<PluginConfiguration>("{}");
        Assert.NotNull(config);
        Assert.False(config!.AllowUserRequests);
        Assert.Equal(PriorityTier.High, config.AdminRequestTier);
        Assert.Equal(PriorityTier.Medium, config.UserRequestTier);
    }

    [Fact]
    public void RequestTier_SerializesByName_AndRoundTrips()
    {
        // Tiers serialize by NAME over the config REST API ([JsonStringEnumConverter]); the config page
        // relies on the string value, not an integer.
        var json = JsonSerializer.Serialize(new PluginConfiguration { AdminRequestTier = PriorityTier.Critical });
        Assert.Contains("\"Critical\"", json);
        var back = JsonSerializer.Deserialize<PluginConfiguration>(json);
        Assert.Equal(PriorityTier.Critical, back!.AdminRequestTier);
    }

    [Fact]
    public void AudioLanguageSelection_DefaultsToAll()
    {
        // Default All = transcribe every audio-track language (existing behavior) — an upgrade must not
        // silently start dropping secondary-audio subtitles.
        var config = new PluginConfiguration();
        Assert.Equal(AudioLanguageSelection.All, config.AudioLanguageSelection);
    }

    [Fact]
    public void AudioLanguageSelection_AbsentFromJson_KeepsAll()
    {
        // Existing installs' config has no key — deserialization must land on the safe All default.
        var config = JsonSerializer.Deserialize<PluginConfiguration>("{}");
        Assert.NotNull(config);
        Assert.Equal(AudioLanguageSelection.All, config!.AudioLanguageSelection);
    }

    [Fact]
    public void AudioLanguageSelection_SerializesByName_AndRoundTrips()
    {
        // Serializes by NAME over the config REST API ([JsonStringEnumConverter]); the config page uses
        // the string option value.
        var json = JsonSerializer.Serialize(new PluginConfiguration { AudioLanguageSelection = AudioLanguageSelection.PrimaryOnly });
        Assert.Contains("\"PrimaryOnly\"", json);
        var back = JsonSerializer.Deserialize<PluginConfiguration>(json);
        Assert.Equal(AudioLanguageSelection.PrimaryOnly, back!.AudioLanguageSelection);
    }

    [Fact]
    public void PluginConfiguration_DefaultValues()
    {
        var config = new PluginConfiguration();

        Assert.Equal("", config.WhisperModelPath);
        Assert.Equal("", config.WhisperBinaryPath);
        Assert.Equal("", config.WhisperBinaryVariant);
        Assert.False(config.EnableAutoGeneration);
        Assert.Equal("auto", config.DefaultLanguage);
        Assert.Equal(SubtitleMode.Full, config.SubtitleMode);
        Assert.False(config.EnableLyricsGeneration);
        Assert.False(config.PauseOnPlayback);
        Assert.Equal(0, config.WhisperThreadCount);
        Assert.NotNull(config.EnabledLibraries);
        Assert.Empty(config.EnabledLibraries);

        Assert.False(config.EnableVocalSeparation);
        Assert.Equal("", config.VocalSeparationBinaryPath);
        Assert.Equal("", config.VocalSeparationModelPath);
        Assert.Equal("", config.VocalSeparationBinaryVariant);
        Assert.Equal("", config.VocalSeparationModelQuant);
        Assert.Equal(0, config.VocalSeparationOverlap);
        Assert.Equal(-1, config.VocalSeparationChunkSize);

        // Issue #83: "generate original-language subtitles" defaults ON so behavior is unchanged
        // for existing users; image subs do NOT count as present by default (still generate text).
        Assert.True(config.GenerateOriginalLanguageSubtitles);
        Assert.False(config.CountImageSubtitlesAsPresent);
    }

    [Fact]
    public void VocalSeparation_AbsentFromJson_RemainsDisabledWithSentinels()
    {
        var config = JsonSerializer.Deserialize<PluginConfiguration>("{}");

        Assert.NotNull(config);
        Assert.False(config!.EnableVocalSeparation);
        Assert.Equal("", config.VocalSeparationBinaryPath);
        Assert.Equal("", config.VocalSeparationModelPath);
        Assert.Equal(0, config.VocalSeparationOverlap);
        Assert.Equal(-1, config.VocalSeparationChunkSize);
    }

    [Fact]
    public void VocalSeparation_RoundTripsThroughJson()
    {
        var original = new PluginConfiguration
        {
            EnableVocalSeparation = true,
            VocalSeparationBinaryPath = "/custom/bs_roformer-cli",
            VocalSeparationModelPath = "/custom/model.gguf",
            VocalSeparationBinaryVariant = "vulkan",
            VocalSeparationModelQuant = "q8_0",
            VocalSeparationOverlap = 3,
            VocalSeparationChunkSize = 960000
        };

        var restored = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(original));

        Assert.NotNull(restored);
        Assert.True(restored!.EnableVocalSeparation);
        Assert.Equal(original.VocalSeparationBinaryPath, restored.VocalSeparationBinaryPath);
        Assert.Equal(original.VocalSeparationModelPath, restored.VocalSeparationModelPath);
        Assert.Equal("vulkan", restored.VocalSeparationBinaryVariant);
        Assert.Equal("q8_0", restored.VocalSeparationModelQuant);
        Assert.Equal(3, restored.VocalSeparationOverlap);
        Assert.Equal(960000, restored.VocalSeparationChunkSize);
    }

    [Fact]
    public void GenerationToggles_AbsentFromJson_DefaultToOn()
    {
        // Existing users' saved config has no Generate*/CountImage* keys. Deserialization must
        // default GenerateOriginalLanguageSubtitles to true, or an upgrade would silently stop
        // generating subtitles; CountImageSubtitlesAsPresent stays false (still generate text).
        var config = JsonSerializer.Deserialize<PluginConfiguration>("{}");
        Assert.NotNull(config);
        Assert.True(config!.GenerateOriginalLanguageSubtitles);
        Assert.False(config.CountImageSubtitlesAsPresent);
    }

    [Fact]
    public void GenerationToggles_RoundTripThroughJson()
    {
        var original = new PluginConfiguration
        {
            GenerateOriginalLanguageSubtitles = false,
            CountImageSubtitlesAsPresent = true
        };
        var restored = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(original));
        Assert.NotNull(restored);
        Assert.False(restored!.GenerateOriginalLanguageSubtitles);
        Assert.True(restored.CountImageSubtitlesAsPresent);
    }

    /// <summary>
    /// Issue #105: all seven new VAD tuning fields must default to their sentinel values so an
    /// existing install that upgrades emits NO extra --vad-* flags and the command line is
    /// byte-identical to before the feature existed.
    /// </summary>
    [Fact]
    public void PluginConfiguration_VadTuningDefaults_AllSentinels()
    {
        var config = new PluginConfiguration();

        Assert.Equal("", config.VadModelVersion);
        Assert.Equal(-1f, config.VadThreshold);
        Assert.Equal(-1, config.VadMinSpeechDurationMs);
        Assert.Equal(-1, config.VadMinSilenceDurationMs);
        Assert.Equal(-1f, config.VadMaxSpeechDurationS);
        Assert.Equal(-1, config.VadSpeechPadMs);
        Assert.Equal(-1f, config.VadSamplesOverlap);
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
