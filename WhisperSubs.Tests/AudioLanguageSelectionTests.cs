using System;
using System.Collections.Generic;
using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Locks the truth table for <see cref="SubtitleManager.SelectAudioLanguages"/> — which detected
/// audio-track languages the per-track (original-language + forced) passes iterate over, given the
/// <see cref="AudioLanguageSelection"/> toggle. The key contract: <c>All</c> is a pass-through and a
/// 0/1-element list is untouched either way, so <c>PrimaryOnly</c> only ever narrows the "auto"
/// multi-language case (a specific default language and the no-tags fallback are single-element).
/// </summary>
public class AudioLanguageSelectionTests
{
    // ---- All: pass-through, including empty and single-element ----------------------------------

    [Fact]
    public void All_ReturnsEveryDetectedLanguage()
    {
        var detected = new[] { "es", "en", "fr" };
        var result = SubtitleManager.SelectAudioLanguages(detected, AudioLanguageSelection.All);
        Assert.Equal(new[] { "es", "en", "fr" }, result);
    }

    [Fact]
    public void All_Empty_ReturnsEmpty()
    {
        var result = SubtitleManager.SelectAudioLanguages(Array.Empty<string>(), AudioLanguageSelection.All);
        Assert.Empty(result);
    }

    [Fact]
    public void All_SingleElement_ReturnsThatElement()
    {
        var result = SubtitleManager.SelectAudioLanguages(new[] { "es" }, AudioLanguageSelection.All);
        Assert.Equal(new[] { "es" }, result);
    }

    // ---- PrimaryOnly: keep only the first/primary track's language ------------------------------

    [Fact]
    public void PrimaryOnly_MultipleLanguages_KeepsOnlyTheFirst()
    {
        var detected = new[] { "es", "en", "fr" };
        var result = SubtitleManager.SelectAudioLanguages(detected, AudioLanguageSelection.PrimaryOnly);
        Assert.Equal(new[] { "es" }, result);
    }

    [Fact]
    public void PrimaryOnly_SingleElement_ReturnsThatElement()
    {
        // A specific DefaultLanguage or a de-duped single audio language resolves to one element —
        // PrimaryOnly leaves it exactly as-is (the toggle's job is only the multi-language case).
        var result = SubtitleManager.SelectAudioLanguages(new[] { "es" }, AudioLanguageSelection.PrimaryOnly);
        Assert.Equal(new[] { "es" }, result);
    }

    [Fact]
    public void PrimaryOnly_Empty_ReturnsEmpty()
    {
        // The no-tags whisper-auto-detect path yields ["auto"], never empty, but guard the empty list
        // so PrimaryOnly can never index element [0] of an empty sequence.
        var result = SubtitleManager.SelectAudioLanguages(Array.Empty<string>(), AudioLanguageSelection.PrimaryOnly);
        Assert.Empty(result);
    }

    [Fact]
    public void PrimaryOnly_AutoFallback_YieldsSingleAuto()
    {
        // ResolveLanguagesAsync returns ["auto"] when no audio language tags exist; PrimaryOnly must not
        // disturb that single whisper-auto-detect pass.
        var result = SubtitleManager.SelectAudioLanguages(new[] { "auto" }, AudioLanguageSelection.PrimaryOnly);
        Assert.Equal(new[] { "auto" }, result);
    }

    [Fact]
    public void PrimaryOnly_PreservesPrimaryEvenWhenSecondaryIsEnglish()
    {
        // The primary track wins regardless of what the secondaries are; the (separate) translation
        // pass is what fills an English gap, and it is deliberately not narrowed by this toggle.
        var detected = new[] { "de", "en" };
        var result = SubtitleManager.SelectAudioLanguages(detected, AudioLanguageSelection.PrimaryOnly);
        Assert.Equal(new[] { "de" }, result);
    }

    // ---- Works over any IReadOnlyList, not just arrays ------------------------------------------

    [Fact]
    public void PrimaryOnly_AcceptsListInput()
    {
        var detected = new List<string> { "ja", "en" };
        var result = SubtitleManager.SelectAudioLanguages(detected, AudioLanguageSelection.PrimaryOnly);
        Assert.Equal(new[] { "ja" }, result);
    }
}
