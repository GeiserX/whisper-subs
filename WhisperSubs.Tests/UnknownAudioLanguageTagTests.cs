using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// An audio track tagged with something that names no single language ("mul", "und", "zxx", "mis",
/// blank, or a code whisper-cli does not know) must not reach whisper-cli as "-l &lt;tag&gt;": whisper-cli
/// refuses to run and the title never gets a subtitle. Such a track counts as untagged, so the run
/// falls back to "auto" and whisper detects the language.
/// </summary>
public class UnknownAudioLanguageTagTests
{
    private static string Streams(params string?[] tags)
    {
        var parts = tags.Select(t => t == null
            ? "{\"codec_type\":\"audio\",\"tags\":{\"language\":null}}"
            : "{\"codec_type\":\"audio\",\"tags\":{\"language\":\"" + t + "\"}}");
        return "{\"streams\":[" + string.Join(",", parts) + "]}";
    }

    [Theory]
    [InlineData("mul")]
    [InlineData("MUL")]
    [InlineData("und")]
    [InlineData("zxx")]
    [InlineData("mis")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("qaa")]   // reserved for local use, not a language whisper knows
    [InlineData("xy")]
    [InlineData("bul")]   // whisper knows Bulgarian as "bg"; "-l bul" is refused
    public void TagWithNoWhisperLanguage_IsUnknown(string? tag)
        => Assert.Null(SubtitleInventory.WhisperLanguageForAudioTag(tag));

    [Theory]
    [InlineData("spa", "es")]
    [InlineData("eng", "en")]
    [InlineData("pt-BR", "pt")]
    [InlineData("es", "es")]
    [InlineData("ms", "ms")]
    [InlineData("haw", "haw")]
    [InlineData("malay", "malay")]   // whisper-cli takes full names too; output names stay as they were
    public void KnownTag_PassesThrough(string tag, string expected)
        => Assert.Equal(expected, SubtitleInventory.WhisperLanguageForAudioTag(tag));

    [Theory]
    [InlineData("mul")]
    [InlineData("und")]
    [InlineData("zxx")]
    [InlineData("mis")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("qaa")]
    public void FfprobeTrackWithUnknownTag_IsLeftOut(string? tag)
        => Assert.Empty(SubtitleManager.ParseAudioLanguages(Streams(tag)));

    [Fact]
    public void FfprobeSpanishTrack_StillPassesThrough()
        => Assert.Equal(new[] { "es" }, SubtitleManager.ParseAudioLanguages(Streams("spa")));

    [Fact]
    public void FfprobeMixedTracks_KeepOnlyTheKnownOnes()
        => Assert.Equal(new[] { "es", "en" }, SubtitleManager.ParseAudioLanguages(Streams("mul", "spa", "und", "eng", "spa")));

    [Fact]
    public void FfprobeTrackWithoutTags_IsLeftOut()
        => Assert.Empty(SubtitleManager.ParseAudioLanguages("{\"streams\":[{\"codec_type\":\"audio\"}]}"));
}
