using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Cross-boundary backward-compatibility lock for the <see cref="SubtitleNaming"/> engine: existing
/// libraries built before the configurable-naming feature (issue naming-contract) have subtitle
/// sidecars using the legacy <c>.generated.</c>/<c>.translated.</c> anchors. This suite pins that
/// upgrading MUST NOT re-transcribe those files — they stay recognised as plugin-owned — while the
/// NEW <c>.{label}.</c> naming is recognised at the same time, including on a mixed library and with
/// a custom (non-default) label. Pure engine only: no <c>Plugin.Instance</c>, no filesystem.
/// </summary>
public class SubtitleNamingBackCompatTests
{
    private const string Default = SubtitleNaming.DefaultTemplate;   // {name}.{lang}.{label}{.type}
    private const string Label = SubtitleNaming.DefaultLabel;        // WhisperSubs

    // ---- Legacy anchors survive upgrade ---------------------------------------------------------

    [Fact]
    public void LegacyGeneratedFile_StillDetectedAsOwned()
    {
        Assert.True(SubtitleNaming.IsPluginOwnedSubtitle("Movie.es.generated.srt", Label));
        Assert.Equal(SubtitleNaming.OwnedKind.Full, SubtitleNaming.Classify("Movie.es.generated.srt", Label));
    }

    [Fact]
    public void LegacyTranslatedFile_StillDetectedAsOwned()
    {
        Assert.True(SubtitleNaming.IsPluginOwnedSubtitle("Movie.en.translated.srt", Label));
        Assert.Equal(SubtitleNaming.OwnedKind.Translated, SubtitleNaming.Classify("Movie.en.translated.srt", Label));
    }

    [Fact]
    public void LegacyForcedFile_ClassifiedForced()
    {
        Assert.True(SubtitleNaming.IsPluginOwnedSubtitle("Movie.es.forced.generated.srt", Label));
        Assert.Equal(SubtitleNaming.OwnedKind.Forced, SubtitleNaming.Classify("Movie.es.forced.generated.srt", Label));
    }

    // ---- New {label} naming is recognised alongside legacy --------------------------------------

    [Theory]
    [InlineData("Movie.en.WhisperSubs.srt", "Full")]
    [InlineData("Movie.en.WhisperSubs.translated.srt", "Translated")]
    [InlineData("Movie.es.WhisperSubs.forced.srt", "Forced")]
    public void NewLabelFile_DetectedAsOwned(string fileName, string expected)
    {
        Assert.True(SubtitleNaming.IsPluginOwnedSubtitle(fileName, Label));
        Assert.Equal(expected, SubtitleNaming.Classify(fileName, Label).ToString());
    }

    // ---- Mixed library (pre-upgrade + post-upgrade + real user subs) ----------------------------

    [Theory]
    [InlineData("Movie1.es.generated.srt", true)]                      // legacy full
    [InlineData("Movie2.en.translated.srt", true)]                     // legacy translated
    [InlineData("Movie3.es.forced.generated.srt", true)]               // legacy forced
    [InlineData("Movie4.en.WhisperSubs.srt", true)]                    // new full
    [InlineData("Movie5.en.WhisperSubs.translated.srt", true)]         // new translated
    [InlineData("Movie6.es.WhisperSubs.forced.srt", true)]             // new forced
    [InlineData("Movie.en.srt", false)]                                // plain user subtitle
    [InlineData("Movie.en.forced.srt", false)]                         // user forced sub, no label/legacy anchor
    public void MixedLibrary_BothKindsRecognized(string fileName, bool expectedOwned)
    {
        Assert.Equal(expectedOwned, SubtitleNaming.IsPluginOwnedSubtitle(fileName, Label));
    }

    // ---- Custom (non-default) label: legacy anchor stays label-independent ----------------------

    [Fact]
    public void CustomLabel_LegacyStillOwned()
    {
        const string customLabel = "MySubs";
        Assert.True(SubtitleNaming.IsPluginOwnedSubtitle("Movie.en.MySubs.srt", customLabel));
        Assert.True(SubtitleNaming.IsPluginOwnedSubtitle("Movie.es.generated.srt", customLabel));
    }

    // ---- Real user subtitles are never mistaken for plugin output --------------------------------

    [Theory]
    [InlineData("Movie.en.srt")]
    [InlineData("Movie.en.sdh.srt")]
    [InlineData("Movie.es.forced.srt")]
    public void UserSubtitle_NotOwned(string fileName)
    {
        Assert.False(SubtitleNaming.IsPluginOwnedSubtitle(fileName, Label));
    }

    // ---- Tail case: {label} is the last token before the extension -------------------------------

    [Fact]
    public void TailCase_LabelBeforeExtension_Owned()
    {
        // The dot before ".srt" supplies the trailing dot so ".WhisperSubs." matches at the tail.
        Assert.True(SubtitleNaming.IsPluginOwnedSubtitle("Movie.en.WhisperSubs.srt", Label));
    }

    // ---- Roundtrip: whatever BuildBaseName produces, IsPluginOwnedSubtitle/Classify recognise ----

    [Theory]
    [InlineData("", "Full")]
    [InlineData("translated", "Translated")]
    [InlineData("forced", "Forced")]
    public void Roundtrip_BuildThenDetect(string type, string expected)
    {
        var builtName = SubtitleNaming.BuildBaseName(Default, "Movie", "en", Label, type);
        var fileName = builtName + ".srt";

        Assert.True(SubtitleNaming.IsPluginOwnedSubtitle(fileName, Label));
        Assert.Equal(expected, SubtitleNaming.Classify(fileName, Label).ToString());
    }
}
