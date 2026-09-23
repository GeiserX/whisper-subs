using WhisperSubs.Controller;
using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

public class TranslationRouteTests
{
    // English is always Whisper, whatever the source and whether or not Canary is installed.
    [Theory]
    [InlineData("es", true)]
    [InlineData("es", false)]
    [InlineData("en", true)]
    [InlineData("auto", false)]
    [InlineData(null, false)]
    public void EnglishTarget_IsAlwaysWhisper(string? source, bool canaryAvailable)
    {
        Assert.Equal(TranslationEngine.Whisper, TranslationRoute.Decide(source, "en", canaryAvailable).Engine);
        Assert.Equal(TranslationEngine.Whisper, TranslationRoute.Decide(source, " EN ", canaryAvailable).Engine);
    }

    [Fact]
    public void EnglishAudio_ToCanaryTarget_WithCanary_IsCanary()
    {
        var decision = TranslationRoute.Decide("en", "nl", canaryAvailable: true);
        Assert.Equal(TranslationEngine.Canary, decision.Engine);
        Assert.Contains("'nl'", decision.Reason);
    }

    // The model card excludes Latvian only from a benchmark comparison (competing models lack it);
    // English to Latvian is evaluated like every other pair, so lv is an ordinary Canary target.
    [Fact]
    public void Latvian_IsAnOrdinaryCanaryTarget()
    {
        Assert.Equal(TranslationEngine.Canary, TranslationRoute.Decide("en", "lv", canaryAvailable: true).Engine);
        Assert.Equal(TranslationEngine.EngineMissing, TranslationRoute.Decide("en", "lv", canaryAvailable: false).Engine);
    }

    [Fact]
    public void EveryCatalogTarget_RoutesToCanaryFromEnglish()
    {
        foreach (var target in CanaryCatalog.Targets)
        {
            Assert.Equal(TranslationEngine.Canary, TranslationRoute.Decide("EN", target.Code.ToUpperInvariant(), canaryAvailable: true).Engine);
        }
    }

    [Fact]
    public void EnglishAudio_CanaryNotInstalled_IsEngineMissingWithSetupHint()
    {
        var decision = TranslationRoute.Decide("en", "de", canaryAvailable: false);
        Assert.Equal(TranslationEngine.EngineMissing, decision.Engine);
        Assert.Contains("not installed", decision.Reason);
        Assert.Contains("CrispASR worker", decision.Reason);
    }

    // A non-English source to a non-English target is the pair the spike showed failing silently.
    // It is the normal case for most of a library, so it skips instead of failing the item.
    [Theory]
    [InlineData("fr", "es", true)]
    [InlineData("es", "fr", true)]
    [InlineData("es", "de", true)]
    [InlineData("es", "nl", false)]   // a missing engine does not matter when the audio is not English
    public void NonEnglishAudio_ToCanaryTarget_IsSkipped(string source, string target, bool canaryAvailable)
    {
        var decision = TranslationRoute.Decide(source, target, canaryAvailable);
        Assert.Equal(TranslationEngine.SkipSourceNotEnglish, decision.Engine);
        Assert.Contains($"The audio is '{source}'", decision.Reason);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownSource_ToCanaryTarget_IsSkipped(string? source)
    {
        var decision = TranslationRoute.Decide(source, "nl", canaryAvailable: true);
        Assert.Equal(TranslationEngine.SkipSourceNotEnglish, decision.Engine);
        Assert.Contains("could not be determined", decision.Reason);
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("zh")]
    [InlineData("xx")]
    public void TargetOutsideTheCatalog_IsUnsupported(string target)
    {
        // Config corruption stays an error whatever the audio language or install state.
        foreach (var (source, canary) in new[] { ("en", true), ("es", true), ("en", false) })
        {
            var decision = TranslationRoute.Decide(source, target, canary);
            Assert.Equal(TranslationEngine.Unsupported, decision.Engine);
            Assert.Contains("not a language", decision.Reason);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void MissingTarget_IsUnsupported(string? target)
    {
        Assert.Equal(TranslationEngine.Unsupported, TranslationRoute.Decide("en", target, canaryAvailable: true).Engine);
    }
}
