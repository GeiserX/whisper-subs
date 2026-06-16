using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

public class SubtitleInventoryTests
{
    // -------------------------------------------------------------------------
    // HasUsableSubtitle
    // -------------------------------------------------------------------------

    [Fact]
    public void HasUsableSubtitle_EmbeddedEnglishTextSub_DesiredEn_ReturnsTrue()
    {
        // Case 1: embedded text sub with container language tag "eng", desired "en".
        var streams = new[]
        {
            new SubtitleStreamInfo { Language = "eng", IsExternal = false, IsTextSubtitle = true }
        };

        Assert.True(SubtitleInventory.HasUsableSubtitle(streams, "en"));
    }

    [Fact]
    public void HasUsableSubtitle_ExternalEnglishSrt_DesiredEn_ReturnsTrue()
    {
        // Case 2: external .en.srt sidecar.
        var streams = new[]
        {
            new SubtitleStreamInfo
            {
                Language = "en",
                IsExternal = true,
                IsTextSubtitle = true,
                Path = "/m/Movie.en.srt"
            }
        };

        Assert.True(SubtitleInventory.HasUsableSubtitle(streams, "en"));
    }

    [Fact]
    public void HasUsableSubtitle_KoreanMovie_OnlySpanishSub_DesiredKo_ReturnsFalse()
    {
        // Case 3: wrong language present.
        var streams = new[]
        {
            new SubtitleStreamInfo { Language = "spa", IsTextSubtitle = true }
        };

        Assert.False(SubtitleInventory.HasUsableSubtitle(streams, "ko"));
    }

    [Fact]
    public void HasUsableSubtitle_OnlyForcedEnglishSub_IgnoreForcedTrue_ReturnsFalse()
    {
        // Case 4a: forced sub does not satisfy when ignoreForced is true (the default).
        var streams = new[]
        {
            new SubtitleStreamInfo { Language = "en", IsForced = true, IsTextSubtitle = true }
        };

        Assert.False(SubtitleInventory.HasUsableSubtitle(streams, "en", ignoreForced: true));
    }

    [Fact]
    public void HasUsableSubtitle_OnlyForcedEnglishSub_IgnoreForcedFalse_ReturnsTrue()
    {
        // Case 4b: when forced subs are allowed, the forced English sub counts.
        var streams = new[]
        {
            new SubtitleStreamInfo { Language = "en", IsForced = true, IsTextSubtitle = true }
        };

        Assert.True(SubtitleInventory.HasUsableSubtitle(streams, "en", ignoreForced: false));
    }

    [Fact]
    public void HasUsableSubtitle_OnlyImageEnglishSub_RequireTextTrue_ReturnsFalse()
    {
        // Case 5a: image sub (PGS/VOBSUB) does not satisfy when text is required.
        var streams = new[]
        {
            new SubtitleStreamInfo { Language = "en", IsTextSubtitle = false }
        };

        Assert.False(SubtitleInventory.HasUsableSubtitle(streams, "en", requireText: true));
    }

    [Fact]
    public void HasUsableSubtitle_OnlyImageEnglishSub_RequireTextFalse_ReturnsTrue()
    {
        // Case 5b: when text is not required, the image English sub counts.
        var streams = new[]
        {
            new SubtitleStreamInfo { Language = "en", IsTextSubtitle = false }
        };

        Assert.True(SubtitleInventory.HasUsableSubtitle(streams, "en", requireText: false));
    }

    [Fact]
    public void HasUsableSubtitle_PluginGeneratedEnglishSub_DesiredEn_ReturnsFalse()
    {
        // Case 6a: the plugin's own .generated.srt output must not self-satisfy a fresh request.
        var streams = new[]
        {
            new SubtitleStreamInfo
            {
                Language = "en",
                IsExternal = true,
                IsTextSubtitle = true,
                Path = "/m/Movie.en.generated.srt"
            }
        };

        Assert.False(SubtitleInventory.HasUsableSubtitle(streams, "en"));
    }

    [Fact]
    public void HasUsableSubtitle_PluginTranslatedEnglishSub_DesiredEn_ReturnsFalse()
    {
        // Case 6b: the plugin's .translated.srt output also must not self-satisfy.
        var streams = new[]
        {
            new SubtitleStreamInfo
            {
                Language = "en",
                IsExternal = true,
                IsTextSubtitle = true,
                Path = "/m/Movie.en.translated.srt"
            }
        };

        Assert.False(SubtitleInventory.HasUsableSubtitle(streams, "en"));
    }

    [Fact]
    public void HasUsableSubtitle_MixedList_RealExternalSubAmongUnusableOnes_ReturnsTrue()
    {
        // Case 7: forced + image + generated all fail, but one genuine external en.srt wins.
        var streams = new[]
        {
            new SubtitleStreamInfo { Language = "en", IsForced = true, IsTextSubtitle = true },
            new SubtitleStreamInfo { Language = "en", IsTextSubtitle = false },
            new SubtitleStreamInfo
            {
                Language = "en",
                IsExternal = true,
                IsTextSubtitle = true,
                Path = "/m/Movie.en.generated.srt"
            },
            new SubtitleStreamInfo
            {
                Language = "en",
                IsExternal = true,
                IsTextSubtitle = true,
                Path = "/m/Movie.en.srt"
            }
        };

        Assert.True(SubtitleInventory.HasUsableSubtitle(streams, "en"));
    }

    [Fact]
    public void HasUsableSubtitle_EmptyList_ReturnsFalse()
    {
        // Case 8a: no streams at all.
        Assert.False(SubtitleInventory.HasUsableSubtitle(new SubtitleStreamInfo[0], "en"));
    }

    [Fact]
    public void HasUsableSubtitle_NullList_ReturnsFalse()
    {
        // Case 8b: null collection.
        Assert.False(SubtitleInventory.HasUsableSubtitle(null, "en"));
    }

    [Theory]
    // Case 9: matching works across ISO 639-1, 639-2, and full-word spellings on both sides.
    [InlineData("english", "en")] // stream full word, desired 639-1
    [InlineData("en", "eng")]     // stream 639-1, desired 639-2
    [InlineData("kor", "korean")] // stream 639-2, desired full word
    public void HasUsableSubtitle_LanguageFormsMatch_ReturnsTrue(string streamLang, string desired)
    {
        var streams = new[]
        {
            new SubtitleStreamInfo { Language = streamLang, IsTextSubtitle = true }
        };

        Assert.True(SubtitleInventory.HasUsableSubtitle(streams, desired));
    }

    // -------------------------------------------------------------------------
    // NormalizeLang
    // -------------------------------------------------------------------------

    [Theory]
    // Case 10: full mapping coverage.
    [InlineData("eng", "en")]
    [InlineData("en", "en")]
    [InlineData("english", "en")]
    [InlineData("ENGLISH", "en")]      // case-insensitive
    [InlineData("kor", "ko")]
    [InlineData("korean", "ko")]
    [InlineData("ko", "ko")]
    [InlineData("spa", "es")]
    [InlineData("fre", "fr")]
    [InlineData("fra", "fr")]
    [InlineData("french", "fr")]
    [InlineData("pt-BR", "pt")]        // region stripped
    [InlineData("en-US", "en")]        // region stripped
    [InlineData("  eng  ", "en")]      // trimmed
    [InlineData("xyz", "xyz")]         // unmapped code returns itself
    public void NormalizeLang_MapsToCanonicalCode(string input, string expected)
    {
        Assert.Equal(expected, SubtitleInventory.NormalizeLang(input));
    }

    [Theory]
    // Case 10 (continued): empty / undetermined inputs normalize to null.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("und")]
    [InlineData("unknown")]
    public void NormalizeLang_EmptyOrUndetermined_ReturnsNull(string? input)
    {
        Assert.Null(SubtitleInventory.NormalizeLang(input));
    }

    // -------------------------------------------------------------------------
    // IsPluginGeneratedPath
    // -------------------------------------------------------------------------

    [Theory]
    // Case 11a: plugin-generated outputs (case-insensitive on the marker).
    [InlineData("/m/Movie.en.generated.srt")]
    [InlineData("/m/Movie.ko.forced.generated.srt")]
    [InlineData("/m/Movie.en.translated.srt")]
    [InlineData("/m/Movie.EN.GENERATED.srt")]
    public void IsPluginGeneratedPath_GeneratedOutputs_ReturnsTrue(string path)
    {
        Assert.True(SubtitleInventory.IsPluginGeneratedPath(path));
    }

    [Theory]
    // Case 11b: ordinary user subtitle paths and empties are not plugin-generated.
    [InlineData("/m/Movie.en.srt")]
    [InlineData(null)]
    [InlineData("")]
    public void IsPluginGeneratedPath_PlainOrEmpty_ReturnsFalse(string? path)
    {
        Assert.False(SubtitleInventory.IsPluginGeneratedPath(path));
    }
}
