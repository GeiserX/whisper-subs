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

    // -------------------------------------------------------------------------
    // IsUsableStream — single source of truth for "is this a usable candidate?"
    // (language-agnostic; callers add the language match)
    // -------------------------------------------------------------------------

    [Fact]
    public void IsUsableStream_NullStream_ReturnsFalse()
    {
        // Case 12a: a null stream is never usable.
        Assert.False(SubtitleInventory.IsUsableStream(null));
    }

    [Fact]
    public void IsUsableStream_TextNonForcedNormalPath_ReturnsTrue()
    {
        // Case 12b: the happy path — text, non-forced, ordinary external path.
        var s = new SubtitleStreamInfo
        {
            Language = "en",
            IsForced = false,
            IsTextSubtitle = true,
            Path = "/m/Movie.en.srt"
        };

        Assert.True(SubtitleInventory.IsUsableStream(s));
    }

    [Fact]
    public void IsUsableStream_ForcedText_IgnoreForcedTrue_ReturnsFalse()
    {
        // Case 12c: forced text track is excluded when ignoreForced is true (the default).
        var s = new SubtitleStreamInfo { Language = "en", IsForced = true, IsTextSubtitle = true };

        Assert.False(SubtitleInventory.IsUsableStream(s, ignoreForced: true));
    }

    [Fact]
    public void IsUsableStream_ForcedText_IgnoreForcedFalse_ReturnsTrue()
    {
        // Case 12d: when forced subs are allowed, the forced text track is usable.
        var s = new SubtitleStreamInfo { Language = "en", IsForced = true, IsTextSubtitle = true };

        Assert.True(SubtitleInventory.IsUsableStream(s, ignoreForced: false));
    }

    [Fact]
    public void IsUsableStream_ImageSub_RequireTextTrue_ReturnsFalse()
    {
        // Case 12e: image sub (PGS/VOBSUB) is excluded when text is required (the default).
        var s = new SubtitleStreamInfo { Language = "en", IsTextSubtitle = false };

        Assert.False(SubtitleInventory.IsUsableStream(s, requireText: true));
    }

    [Fact]
    public void IsUsableStream_ImageSub_RequireTextFalse_ReturnsTrue()
    {
        // Case 12f: when text is not required, the image sub is usable.
        var s = new SubtitleStreamInfo { Language = "en", IsTextSubtitle = false };

        Assert.True(SubtitleInventory.IsUsableStream(s, requireText: false));
    }

    [Theory]
    // Case 12g: the plugin's own generated/translated outputs are never usable candidates,
    // even though they are text and non-forced.
    [InlineData("/m/Movie.en.generated.srt")]
    [InlineData("/m/Movie.en.translated.srt")]
    public void IsUsableStream_PluginGeneratedPath_ReturnsFalse(string path)
    {
        var s = new SubtitleStreamInfo
        {
            Language = "en",
            IsForced = false,
            IsTextSubtitle = true,
            Path = path
        };

        Assert.False(SubtitleInventory.IsUsableStream(s));
    }

    [Fact]
    public void IsUsableStream_TextNonForcedNullPath_ReturnsTrue()
    {
        // Case 12h: a null path is not a generated path — embedded text tracks stay usable.
        var s = new SubtitleStreamInfo
        {
            Language = "en",
            IsForced = false,
            IsTextSubtitle = true,
            Path = null
        };

        Assert.True(SubtitleInventory.IsUsableStream(s));
    }

    // -------------------------------------------------------------------------
    // NormalizeLang — "auto" placeholder
    // -------------------------------------------------------------------------

    [Fact]
    public void NormalizeLang_Auto_ReturnsNull()
    {
        // Case 13: "auto" is whisper's detect-language placeholder, not a concrete language;
        // it must normalize to null (and survive trim + case folding).
        Assert.Null(SubtitleInventory.NormalizeLang("auto"));
        Assert.Null(SubtitleInventory.NormalizeLang("AUTO"));
        Assert.Null(SubtitleInventory.NormalizeLang(" auto "));
    }

    // -------------------------------------------------------------------------
    // NormalizeLang — canonical-table parity guard
    // (locks the table now that SubtitleManager delegates to it)
    // -------------------------------------------------------------------------

    [Theory]
    // Case 14: every representative 639-2 code maps to its expected 2-letter result AND that
    // result is idempotent (normalizing the result again is a no-op). Both /B and /T variants
    // for French (fra/fre), German (deu/ger) and Chinese (zho/chi) collapse identically.
    [InlineData("eng", "en")]
    [InlineData("kor", "ko")]
    [InlineData("spa", "es")]
    [InlineData("fra", "fr")]
    [InlineData("fre", "fr")]
    [InlineData("deu", "de")]
    [InlineData("ger", "de")]
    [InlineData("por", "pt")]
    [InlineData("zho", "zh")]
    [InlineData("chi", "zh")]
    public void NormalizeLang_CanonicalTable_RoundTripsConsistently(string code, string expected)
    {
        var once = SubtitleInventory.NormalizeLang(code);

        Assert.Equal(expected, once);
        // Idempotence: the 2-letter result normalizes to itself.
        Assert.Equal(once, SubtitleInventory.NormalizeLang(once));
    }

    // -------------------------------------------------------------------------
    // HasUsableSubtitle — still correct now that it delegates to IsUsableStream
    // -------------------------------------------------------------------------

    [Fact]
    public void HasUsableSubtitle_MixedList_RealExternalWins_ReturnsTrue()
    {
        // Case 15a: forced + image + generated all fail IsUsableStream, but the genuine
        // external en.srt is usable — desired "en" is satisfied.
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
    public void HasUsableSubtitle_MixedListMinusRealExternal_ReturnsFalse()
    {
        // Case 15b: drop the genuine external sub from the Case 15a list — nothing usable remains.
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
            }
        };

        Assert.False(SubtitleInventory.HasUsableSubtitle(streams, "en"));
    }

    // -------------------------------------------------------------------------
    // HasUsableSubtitle — polish gaps (default requireText, region round-trip, und handling)
    // -------------------------------------------------------------------------

    [Fact]
    public void HasUsableSubtitle_TwoArgOverload_ImageOnly_ReturnsFalse()
    {
        // Case 16a: the 2-arg overload defaults requireText:true, so an image-only English
        // track does not satisfy the need. Locks the default.
        var streams = new[]
        {
            new SubtitleStreamInfo { Language = "en", IsTextSubtitle = false }
        };

        Assert.False(SubtitleInventory.HasUsableSubtitle(streams, "en"));
    }

    [Fact]
    public void HasUsableSubtitle_StreamRegionTag_DesiredIso6392_ReturnsTrue()
    {
        // Case 16b: stream tagged "en-US" (region) vs desired "eng" (639-2) — both normalize
        // to "en" end-to-end.
        var streams = new[]
        {
            new SubtitleStreamInfo { Language = "en-US", IsTextSubtitle = true }
        };

        Assert.True(SubtitleInventory.HasUsableSubtitle(streams, "eng"));
    }

    [Fact]
    public void HasUsableSubtitle_StreamUndetermined_DesiredEn_ReturnsFalse()
    {
        // Case 16c: an "und"-tagged text track never satisfies a concrete desired language.
        var streams = new[]
        {
            new SubtitleStreamInfo { Language = "und", IsTextSubtitle = true }
        };

        Assert.False(SubtitleInventory.HasUsableSubtitle(streams, "en"));
    }

    [Fact]
    public void HasUsableSubtitle_UndAndEnTextTracks_DesiredEn_ReturnsTrue()
    {
        // Case 16d: an "und" track is skipped but a real "en" track in the same list wins.
        var streams = new[]
        {
            new SubtitleStreamInfo { Language = "und", IsTextSubtitle = true },
            new SubtitleStreamInfo { Language = "en", IsTextSubtitle = true }
        };

        Assert.True(SubtitleInventory.HasUsableSubtitle(streams, "en"));
    }

    // -------------------------------------------------------------------------
    // UsableSubtitleExtensions — the single source of truth for the filename-scan
    // paths (scheduled task + translation "auto" fallback). Issue #83.
    // -------------------------------------------------------------------------

    [Fact]
    public void UsableSubtitleExtensions_RequireText_OnlyTextFormats()
    {
        // Case 17a: requireText:true (default skip behavior) — text sidecars only, no image.
        var exts = SubtitleInventory.UsableSubtitleExtensions(requireText: true);

        Assert.Contains(".srt", exts);
        Assert.Contains(".ass", exts);
        Assert.Contains(".ssa", exts);
        Assert.Contains(".vtt", exts);
        // The whole point of the toggle being OFF: image sidecars do NOT count.
        Assert.DoesNotContain(".sub", exts);
        Assert.DoesNotContain(".sup", exts);
    }

    [Fact]
    public void UsableSubtitleExtensions_AllowImage_IncludesImageSidecars()
    {
        // Case 17b: requireText:false (CountImageSubtitlesAsPresent on) — image sidecars count too.
        var exts = SubtitleInventory.UsableSubtitleExtensions(requireText: false);

        // Still includes every text format...
        Assert.Contains(".srt", exts);
        Assert.Contains(".ass", exts);
        Assert.Contains(".ssa", exts);
        Assert.Contains(".vtt", exts);
        // ...plus the image sidecars (VOBSUB .sub, PGS .sup).
        Assert.Contains(".sub", exts);
        Assert.Contains(".sup", exts);
    }

    [Fact]
    public void UsableSubtitleExtensions_ImageSidecar_GatedStrictlyByRequireText()
    {
        // Case 17c: the inversion guard. `.sub` (image) must count IFF requireText is false.
        // If a caller ever drops the "!" on `!CountImageSubtitlesAsPresent`, this flips and fails —
        // catching the silent feature-inversion the unit test for the config flag can't see.
        Assert.DoesNotContain(".sub", SubtitleInventory.UsableSubtitleExtensions(requireText: true));
        Assert.Contains(".sub", SubtitleInventory.UsableSubtitleExtensions(requireText: false));
    }
}
