using System.IO;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Pins the pure <see cref="SubtitleNaming"/> engine — the shared contract every write/read touchpoint
/// builds against. Asserts the BRAND-FIRST default outputs, template expansion edge cases (opaque
/// {name}, empty {type}, no double/leading/trailing dot), ownership detection (new label + legacy
/// anchors, case-insensitive), classification, and template validation/fallback.
/// </summary>
public class SubtitleNamingTests
{
    private const string Default = SubtitleNaming.DefaultTemplate;   // {name}.{lang}.{label}{.type}
    private const string Label = SubtitleNaming.DefaultLabel;        // WhisperSubs

    // ---- BuildBaseName — BRAND-FIRST default outputs -------------------------------------------

    [Fact]
    public void BuildBaseName_Default_Full_IsBrandFirst()
    {
        Assert.Equal("Episode.en.WhisperSubs", SubtitleNaming.BuildBaseName(Default, "Episode", "en", "WhisperSubs", ""));
    }

    [Fact]
    public void BuildBaseName_Default_Translated_AppendsTranslated()
    {
        Assert.Equal("Episode.en.WhisperSubs.translated", SubtitleNaming.BuildBaseName(Default, "Episode", "en", "WhisperSubs", "translated"));
    }

    [Fact]
    public void BuildBaseName_Default_Forced_AppendsForced()
    {
        Assert.Equal("Episode.en.WhisperSubs.forced", SubtitleNaming.BuildBaseName(Default, "Episode", "en", "WhisperSubs", "forced"));
    }

    // ---- BuildBaseName — {name} is opaque -----------------------------------------------------

    [Fact]
    public void BuildBaseName_NameWithDots_IsOpaque_NeverSplit()
    {
        // A release-tagged filename keeps every dot; the token is substituted literally.
        Assert.Equal(
            "Movie.2024.1080p.en.WhisperSubs",
            SubtitleNaming.BuildBaseName(Default, "Movie.2024.1080p", "en", "WhisperSubs", ""));
    }

    // ---- BuildBaseName — presets --------------------------------------------------------------

    [Theory]
    [InlineData("{name}.{lang}.{label}{.type}", "", "Movie.en.WhisperSubs")]                 // brand-first (default)
    [InlineData("{name}.{lang}.{label}{.type}", "translated", "Movie.en.WhisperSubs.translated")]
    [InlineData("{name}.{lang}{.type}.{label}", "", "Movie.en.WhisperSubs")]                 // label-at-end
    [InlineData("{name}.{lang}{.type}.{label}", "translated", "Movie.en.translated.WhisperSubs")]
    [InlineData("{name}.{lang}.{label}.generated", "", "Movie.en.WhisperSubs.generated")]    // keep .generated
    public void BuildBaseName_Presets_ProduceExpected(string template, string type, string expected)
    {
        Assert.Equal(expected, SubtitleNaming.BuildBaseName(template, "Movie", "en", "WhisperSubs", type));
    }

    // ---- BuildBaseName — empty segment hygiene ------------------------------------------------

    [Fact]
    public void BuildBaseName_EmptyType_NoDoubleDot()
    {
        // Label-at-end template: {.type} collapses to nothing, and the resulting ".." is collapsed.
        Assert.Equal("Movie.en.WhisperSubs", SubtitleNaming.BuildBaseName("{name}.{lang}{.type}.{label}", "Movie", "en", "WhisperSubs", ""));
    }

    [Fact]
    public void BuildBaseName_EmptyLeadingToken_NoLeadingDot()
    {
        Assert.Equal("en.WhisperSubs", SubtitleNaming.BuildBaseName("{name}.{lang}.{label}", "", "en", "WhisperSubs", ""));
    }

    [Fact]
    public void BuildBaseName_EmptyTrailingToken_NoTrailingDot()
    {
        Assert.Equal("Movie.WhisperSubs", SubtitleNaming.BuildBaseName("{name}.{lang}.{label}", "Movie", "", "WhisperSubs", ""));
    }

    // ---- BuildMediaAdjacentPath ---------------------------------------------------------------

    [Fact]
    public void BuildMediaAdjacentPath_UsesMediaDir_AndBaseName_PlusExtension()
    {
        var media = Path.Combine("/media", "Movies", "Film (2024)", "Film (2024).mkv");
        var expected = Path.Combine("/media", "Movies", "Film (2024)", "Film (2024).es.WhisperSubs.srt");
        Assert.Equal(expected, SubtitleNaming.BuildMediaAdjacentPath(media, Default, "es", "WhisperSubs", "", ".srt"));
    }

    [Fact]
    public void BuildMediaAdjacentPath_Translated_IncludesType()
    {
        var media = Path.Combine("/media", "Show", "Episode.mkv");
        var expected = Path.Combine("/media", "Show", "Episode.en.WhisperSubs.translated.srt");
        Assert.Equal(expected, SubtitleNaming.BuildMediaAdjacentPath(media, Default, "en", "WhisperSubs", "translated", ".srt"));
    }

    // ---- IsPluginOwnedSubtitle ----------------------------------------------------------------

    [Fact]
    public void IsPluginOwnedSubtitle_NewLabel_True()
    {
        Assert.True(SubtitleNaming.IsPluginOwnedSubtitle("Movie.en.WhisperSubs.srt", Label));
    }

    [Fact]
    public void IsPluginOwnedSubtitle_TailCase_TrueViaExtensionDot()
    {
        // The dot before ".srt" supplies the trailing dot so ".WhisperSubs." matches at the tail.
        Assert.True(SubtitleNaming.IsPluginOwnedSubtitle("Episode.en.WhisperSubs.srt", "WhisperSubs"));
    }

    [Theory]
    [InlineData("Movie.es.generated.srt")]
    [InlineData("Movie.en.translated.srt")]
    [InlineData("Movie.es.forced.generated.srt")]
    public void IsPluginOwnedSubtitle_LegacyAnchors_True(string fileName)
    {
        Assert.True(SubtitleNaming.IsPluginOwnedSubtitle(fileName, Label));
    }

    [Fact]
    public void IsPluginOwnedSubtitle_CaseInsensitive()
    {
        Assert.True(SubtitleNaming.IsPluginOwnedSubtitle("Movie.en.WHISPERSUBS.srt", "whispersubs"));
        Assert.True(SubtitleNaming.IsPluginOwnedSubtitle("Movie.es.GENERATED.srt", Label));
    }

    [Theory]
    [InlineData("Movie.en.srt")]           // a plain user subtitle
    [InlineData("Movie.forced.srt")]       // user forced sub — no plugin anchor, no label
    [InlineData("Movie.mkv")]
    public void IsPluginOwnedSubtitle_UserSubtitle_False(string fileName)
    {
        Assert.False(SubtitleNaming.IsPluginOwnedSubtitle(fileName, Label));
    }

    // ---- Classify -----------------------------------------------------------------------------

    [Fact]
    public void Classify_Full()
    {
        Assert.Equal(SubtitleNaming.OwnedKind.Full, SubtitleNaming.Classify("Movie.en.WhisperSubs.srt", Label));
    }

    [Fact]
    public void Classify_Forced()
    {
        Assert.Equal(SubtitleNaming.OwnedKind.Forced, SubtitleNaming.Classify("Movie.es.WhisperSubs.forced.srt", Label));
        Assert.Equal(SubtitleNaming.OwnedKind.Forced, SubtitleNaming.Classify("Movie.es.forced.generated.srt", Label));
    }

    [Fact]
    public void Classify_Translated()
    {
        Assert.Equal(SubtitleNaming.OwnedKind.Translated, SubtitleNaming.Classify("Movie.en.WhisperSubs.translated.srt", Label));
        Assert.Equal(SubtitleNaming.OwnedKind.Translated, SubtitleNaming.Classify("Movie.en.translated.srt", Label));
    }

    [Fact]
    public void Classify_None_ForUserSubtitle()
    {
        Assert.Equal(SubtitleNaming.OwnedKind.None, SubtitleNaming.Classify("Movie.en.srt", Label));
    }

    // ---- ValidateTemplate ---------------------------------------------------------------------

    [Fact]
    public void ValidateTemplate_Valid_Ok()
    {
        var (ok, error) = SubtitleNaming.ValidateTemplate(Default);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("{lang}.{label}", "{name}")]
    [InlineData("{name}.{label}", "{lang}")]
    [InlineData("{name}.{lang}", "{label}")]
    public void ValidateTemplate_Missing_NamesMissingToken(string template, string missing)
    {
        var (ok, error) = SubtitleNaming.ValidateTemplate(template);
        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains(missing, error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateTemplate_NullOrEmpty_NotOk(string? template)
    {
        var (ok, _) = SubtitleNaming.ValidateTemplate(template);
        Assert.False(ok);
    }

    // ---- EffectiveTemplate --------------------------------------------------------------------

    [Fact]
    public void EffectiveTemplate_Valid_ReturnsIt()
    {
        const string custom = "{name}.{lang}{.type}.{label}";
        Assert.Equal(custom, SubtitleNaming.EffectiveTemplate(custom));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{name}.{lang}")]   // missing {label}
    public void EffectiveTemplate_Invalid_FallsBackToDefault(string? template)
    {
        Assert.Equal(SubtitleNaming.DefaultTemplate, SubtitleNaming.EffectiveTemplate(template));
    }
}
