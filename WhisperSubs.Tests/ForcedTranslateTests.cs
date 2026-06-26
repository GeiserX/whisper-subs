using WhisperSubs.Controller;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

// Issue #95: forced subtitles were transcribing foreign dialogue in its source language and writing
// it into an English-tagged (.eng.forced) file, and re-running whisper's VAD on already-trimmed
// chunks could empty them. These tests pin the two pure pieces of that fix: the English-only gate
// for translation, and the forced-chunk argument shape (translate on, second VAD pass off).
public class ForcedTranslateTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    [InlineData("eng")]
    [InlineData("ENG")]
    [InlineData("english")]
    [InlineData("English")]
    public void LanguageIsEnglish_TrueForEnglishForms(string language)
    {
        Assert.True(SubtitleManager.LanguageIsEnglish(language));
    }

    [Theory]
    [InlineData("es")]
    [InlineData("nn")]   // Norwegian Nynorsk — the language in the issue #95 report
    [InlineData("de")]
    [InlineData("auto")]
    [InlineData("en-US")] // regional tag, not a bare English code — primaries are normalized to "en"
    [InlineData("")]
    [InlineData(null)]
    public void LanguageIsEnglish_FalseForEverythingElse(string? language)
    {
        Assert.False(SubtitleManager.LanguageIsEnglish(language));
    }

    // The exact command shape a forced English chunk now produces: source language on -l, --translate
    // to render it in English, and NO --vad (the chunk is already VAD-trimmed; a second pass could
    // filter it to zero segments and emit an empty subtitle).
    [Fact]
    public void ForcedEnglishChunk_TranslatesWithoutSecondVadPass()
    {
        var args = WhisperProvider.BuildTranscribeArguments(
            modelPath: "/m/model.bin",
            audioPath: "/tmp/foreign_1510_1518.wav",
            language: "nn",
            threadCount: 0,
            translate: true,        // English primary → translate the foreign chunk to English
            vadModelPath: null,     // applyVad:false collapses to a null VAD path → no --vad
            outputPrefix: "/tmp/out",
            langPrompt: null);

        Assert.Contains("--translate", args);
        Assert.DoesNotContain("--vad", args);
        Assert.DoesNotContain("--vad-model", args);

        // -l must be immediately followed by the foreign SOURCE language so --translate knows what
        // it is translating from (IReadOnlyList has no IndexOf, so scan the pairs).
        var pairedSourceLang = false;
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == "-l" && args[i + 1] == "nn") { pairedSourceLang = true; break; }
        }
        Assert.True(pairedSourceLang,
            "forced chunk must pass the foreign SOURCE language to -l so --translate knows what to translate from");
    }

    // A non-English PRIMARY (translateForced=false) keeps the in-source transcription: whisper can
    // only translate into English, so for a non-English-primary title we transcribe the foreign
    // insert in its own language ("fr" here) rather than write mislabeled English. Still no second
    // VAD pass.
    [Fact]
    public void ForcedNonEnglishPrimary_TranscribesWithoutTranslateOrVad()
    {
        var args = WhisperProvider.BuildTranscribeArguments(
            modelPath: "/m/model.bin",
            audioPath: "/tmp/foreign.wav",
            language: "fr",         // the foreign insert's source language under a non-English primary
            threadCount: 0,
            translate: false,       // translateForced=false for a non-English primary → keep source
            vadModelPath: null,
            outputPrefix: "/tmp/out",
            langPrompt: null);

        Assert.DoesNotContain("--translate", args);
        Assert.DoesNotContain("--vad", args);
    }

    // Locks fix #2's invariant: forced chunks pass applyVad:false, and that MUST suppress whisper's
    // VAD even when a VAD model is configured and present on disk — otherwise a short, already-
    // trimmed chunk can be re-filtered to zero segments and write an empty subtitle. (Issue #95.)
    [Fact]
    public void ShouldUseVad_False_WhenApplyVadFalse_EvenWithModelPresent()
    {
        Assert.False(WhisperProvider.ShouldUseVad(applyVad: false, vadModelPath: "/models/vad.bin", vadModelExists: true));
    }

    [Fact]
    public void ShouldUseVad_True_WhenApplyVadTrue_AndModelPresent()
    {
        Assert.True(WhisperProvider.ShouldUseVad(applyVad: true, vadModelPath: "/models/vad.bin", vadModelExists: true));
    }

    [Fact]
    public void ShouldUseVad_False_WhenModelConfiguredButMissingOnDisk()
    {
        Assert.False(WhisperProvider.ShouldUseVad(applyVad: true, vadModelPath: "/models/vad.bin", vadModelExists: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ShouldUseVad_False_WhenNoModelConfigured(string? vadModelPath)
    {
        Assert.False(WhisperProvider.ShouldUseVad(applyVad: true, vadModelPath: vadModelPath, vadModelExists: true));
    }
}
