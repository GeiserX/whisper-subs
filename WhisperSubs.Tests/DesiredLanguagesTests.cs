using System.Collections.Generic;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Tests for the desired-languages allow-list helpers (issue #83):
/// <see cref="SubtitleInventory.ParseDesiredLanguages"/> and
/// <see cref="SubtitleInventory.IsLanguageDesired"/>. Both are pure static methods, so they are
/// exercised directly. Parsing normalizes every token through <c>NormalizeLang</c> (so "english",
/// "eng" and "en" all collapse to "en"), drops placeholder tags ("und"/"unknown"/"auto") and
/// blank input, and yields a case-insensitive set. <c>IsLanguageDesired</c> treats an empty set as
/// "no filter" and an unclassifiable language as "don't block".
/// </summary>
public class DesiredLanguagesTests
{
    // -------------------------------------------------------------------------
    // ParseDesiredLanguages
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseDesiredLanguages_CommaSeparatedCodes_ReturnsBothLanguages()
    {
        // "en, es" → the two ISO 639-1 codes, nothing else.
        var result = SubtitleInventory.ParseDesiredLanguages("en, es");

        Assert.Equal(2, result.Count);
        Assert.Contains("en", result);
        Assert.Contains("es", result);
    }

    [Fact]
    public void ParseDesiredLanguages_SpaceSeparatedFullNames_NormalizesToCodes()
    {
        // Full English words separated by a space normalize to their 639-1 codes.
        var result = SubtitleInventory.ParseDesiredLanguages("english spanish");

        Assert.Equal(2, result.Count);
        Assert.Contains("en", result);
        Assert.Contains("es", result);
    }

    [Fact]
    public void ParseDesiredLanguages_MixedSeparators_SplitsOnAll()
    {
        // Semicolon and pipe are valid separators alongside comma/space.
        var result = SubtitleInventory.ParseDesiredLanguages("en;fr|de");

        Assert.Equal(3, result.Count);
        Assert.Contains("en", result);
        Assert.Contains("fr", result);
        Assert.Contains("de", result);
    }

    [Fact]
    public void ParseDesiredLanguages_SameLanguageDifferentSpellings_Deduplicates()
    {
        // "en", "english" and "eng" all normalize to "en" → a single entry.
        var result = SubtitleInventory.ParseDesiredLanguages("en, english, eng");

        Assert.Single(result);
        Assert.Contains("en", result);
    }

    [Fact]
    public void ParseDesiredLanguages_PlaceholderTokens_AreDropped()
    {
        // "auto" and "und" are detect/undetermined placeholders → normalize to null → dropped.
        // (Note: "xyz" is an unmapped-but-non-placeholder token; NormalizeLang returns it
        //  unchanged, so it is KEPT. Only the placeholders are filtered here.)
        var result = SubtitleInventory.ParseDesiredLanguages("en, auto, und");

        Assert.Single(result);
        Assert.Contains("en", result);
        Assert.DoesNotContain("auto", result);
        Assert.DoesNotContain("und", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void ParseDesiredLanguages_BlankInput_ReturnsEmptySet(string? raw)
    {
        // null / empty / whitespace-only → empty set (callers read empty as "no filter").
        var result = SubtitleInventory.ParseDesiredLanguages(raw);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseDesiredLanguages_ResultingSet_IsCaseInsensitive()
    {
        // The returned HashSet uses OrdinalIgnoreCase, so a differently-cased lookup still hits.
        var result = SubtitleInventory.ParseDesiredLanguages("en, es");

        Assert.Contains("EN", result);
    }

    [Fact]
    public void ParseDesiredLanguages_RegionQualifiedTag_StripsRegion()
    {
        // "pt-BR" → base language "pt" (region suffix dropped by NormalizeLang).
        var result = SubtitleInventory.ParseDesiredLanguages("pt-BR");

        Assert.Single(result);
        Assert.Contains("pt", result);
    }

    // -------------------------------------------------------------------------
    // IsLanguageDesired
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("xyz")]
    public void IsLanguageDesired_EmptySet_AlwaysTrue(string language)
    {
        // An empty allow-list means "no filter configured" → every language is desired.
        var desired = new HashSet<string>();

        Assert.True(SubtitleInventory.IsLanguageDesired(language, desired));
    }

    [Theory]
    [InlineData("en")]      // already 639-1
    [InlineData("eng")]     // 639-2
    [InlineData("english")] // full word
    public void IsLanguageDesired_LanguageInSet_AcrossForms_ReturnsTrue(string language)
    {
        // All three spellings normalize to "en", which is in the set.
        var desired = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "en" };

        Assert.True(SubtitleInventory.IsLanguageDesired(language, desired));
    }

    [Theory]
    [InlineData("es")]
    [InlineData("ko")]
    public void IsLanguageDesired_LanguageNotInSet_ReturnsFalse(string language)
    {
        // Concrete language that normalizes outside the {"en"} allow-list → not desired.
        var desired = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "en" };

        Assert.False(SubtitleInventory.IsLanguageDesired(language, desired));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("und")]
    [InlineData(null)]
    [InlineData("")]
    public void IsLanguageDesired_UnclassifiableLanguage_ReturnsTrue(string? language)
    {
        // A language that doesn't normalize (placeholder/blank) can't be classified against the
        // allow-list, so it is not blocked — auto-detection still proceeds.
        var desired = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "en" };

        Assert.True(SubtitleInventory.IsLanguageDesired(language, desired));
    }

    [Fact]
    public void IsLanguageDesired_MultiLanguageSet_MemberTrue_NonMemberFalse()
    {
        // {"es","fr"}: "fr" is in the set, "de" is not.
        var desired = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "es", "fr" };

        Assert.True(SubtitleInventory.IsLanguageDesired("fr", desired));
        Assert.False(SubtitleInventory.IsLanguageDesired("de", desired));
    }

    [Fact]
    public void IsLanguageDesired_EndToEnd_WithParsedSet_FiltersCorrectly()
    {
        // Wire the two helpers together as the real call sites do.
        Assert.False(SubtitleInventory.IsLanguageDesired("ko", SubtitleInventory.ParseDesiredLanguages("en,es")));
        Assert.True(SubtitleInventory.IsLanguageDesired("es", SubtitleInventory.ParseDesiredLanguages("en,es")));
    }
}
