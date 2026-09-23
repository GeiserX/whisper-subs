using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Controller;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// The per-target translation pass: the English pass must be untouched, and every non-English
/// target is planned by pure helpers in <see cref="SubtitleManager"/>.
/// </summary>
public class TranslationTargetsTests
{
    // ── English pass invariant ─────────────────────────────────────────────

    // The English pass still calls TranscribeAsync(audio, source, ct, translate: true) with no target,
    // which lands in BuildTranscribeArguments unchanged. This pins the full whisper-cli vector for a
    // representative translation run, so any drift in the command an existing install emits is caught.
    [Fact]
    public void EnglishPass_WhisperArgumentVector_IsUnchanged()
    {
        var args = WhisperProvider.BuildTranscribeArguments(
            "/m/ggml-large-v3.bin", "/tmp/item_translate.wav", "es", 8, translate: true,
            "/vad/ggml-silero-v5.1.2.bin", "/tmp/out", "Hola.", new VadTuning(Threshold: 0.5f), 42);

        Assert.Equal(new[]
        {
            "-m", "/m/ggml-large-v3.bin",
            "-f", "/tmp/item_translate.wav",
            "-l", "es",
            "-t", "8",
            "-mc", "0",
            "-sns",
            "--print-progress",
            "--translate",
            "--vad",
            "--vad-model", "/vad/ggml-silero-v5.1.2.bin",
            "--vad-threshold", "0.5",
            "--max-len", "42",
            "--split-on-word",
            "-osrt",
            "-of", "/tmp/out",
            "--prompt", "Hola.",
        }, args);
    }

    // A null or "en" target must reach the same code path as before (here: the missing-model check);
    // any other target is refused before a process could be spawned.
    [Theory]
    [InlineData(null)]
    [InlineData("en")]
    [InlineData(" EN ")]
    public async Task WhisperProvider_NullOrEnglishTarget_KeepsTheExistingPath(string? target)
    {
        var provider = new WhisperProvider(NullLogger<WhisperProvider>.Instance, "/nonexistent/model.bin");
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            provider.TranscribeAsync("/tmp/a.wav", "es", CancellationToken.None, translate: true, targetLanguage: target));
    }

    [Fact]
    public async Task WhisperProvider_NonEnglishTarget_IsNotSupported()
    {
        var provider = new WhisperProvider(NullLogger<WhisperProvider>.Instance, "/nonexistent/model.bin");
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.TranscribeAsync("/tmp/a.wav", "en", CancellationToken.None, translate: true, targetLanguage: "nl"));
        Assert.Contains("Canary", ex.Message);
    }

    [Fact]
    public async Task RemoteWhisperProvider_NonEnglishTarget_IsNotSupported()
    {
        var provider = new RemoteWhisperProvider(NullLogger<RemoteWhisperProvider>.Instance, "https://worker.example", "whisper-large-v3");
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.TranscribeAsync("/nonexistent/a.wav", "en", CancellationToken.None, translate: true, targetLanguage: "de"));
    }

    // ── Target list normalization ──────────────────────────────────────────

    [Fact]
    public void NormalizeTranslationTargets_TrimsLowersDedupesAndDropsEnglish()
    {
        Assert.Equal(new[] { "nl", "de", "xx" },
            SubtitleManager.NormalizeTranslationTargets(new[] { " NL ", "en", "de", "", "nl", "EN", "xx" }));
        Assert.Empty(SubtitleManager.NormalizeTranslationTargets(null));
        Assert.Empty(SubtitleManager.NormalizeTranslationTargets(new List<string>()));
    }

    // ── Source and audio languages ─────────────────────────────────────────

    [Fact]
    public void ResolveTranslationSource_EnglishTrackWins()
    {
        Assert.Equal("en", SubtitleManager.ResolveTranslationSource(new[] { "fr", "EN" }, null));
    }

    [Fact]
    public void ResolveTranslationSource_TaggedNonEnglish_IsTheFirstTag()
    {
        Assert.Equal("fr", SubtitleManager.ResolveTranslationSource(new[] { "FR", "de" }, ("en", 0.99f)));
    }

    [Theory]
    [InlineData("en", 0.3f, "en")]
    [InlineData("en", 0.97f, "en")]
    [InlineData("es", 0.8f, "es")]
    [InlineData("en", 0.29f, "auto")]   // below the English pass's own confidence bar
    [InlineData("", 0.9f, "auto")]
    public void ResolveTranslationSource_UntaggedAudio_UsesAConfidentProbe(string language, float probability, string expected)
    {
        Assert.Equal(expected, SubtitleManager.ResolveTranslationSource(new[] { "auto" }, (language, probability)));
    }

    [Fact]
    public void ResolveTranslationSource_NothingKnown_IsAuto()
    {
        Assert.Equal("auto", SubtitleManager.ResolveTranslationSource(new[] { "auto" }, null));
        Assert.Equal("auto", SubtitleManager.ResolveTranslationSource(Array.Empty<string>(), null));
    }

    [Fact]
    public void AudioLanguagesForTargets_CombinesTagsAndAConfidentProbe()
    {
        var tagged = SubtitleManager.AudioLanguagesForTargets(new[] { "en", "es", "auto" }, null);
        Assert.True(tagged.SetEquals(new[] { "en", "es" }));
        Assert.Contains("ES", tagged);   // case-insensitive

        Assert.True(SubtitleManager.AudioLanguagesForTargets(new[] { "auto" }, ("nl", 0.9f)).SetEquals(new[] { "nl" }));
        Assert.Empty(SubtitleManager.AudioLanguagesForTargets(new[] { "auto" }, ("nl", 0.1f)));
        Assert.Empty(SubtitleManager.AudioLanguagesForTargets(new[] { "auto" }, null));
    }

    // The audio tag stays "bul" (it names files), but compared with a target it is Bulgarian.
    [Fact]
    public void AudioLanguagesForTargets_BulgarianTag_MatchesTheBgTarget()
    {
        var audio = SubtitleManager.AudioLanguagesForTargets(new[] { "en", "bul" }, null);
        Assert.Contains("bg", audio);

        var plan = Assert.Single(SubtitleManager.PlanTranslationTargets(
            new[] { "bg" }, audio, "en", _ => true, _ => false, _ => false, force: true));
        Assert.Equal("the audio is already in 'bg'", plan.SkipReason);
    }

    // ── Plan ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<SubtitleManager.TranslationTargetPlan> Plan(
        IReadOnlyList<string> targets,
        string source = "en",
        IEnumerable<string>? audio = null,
        bool canary = true,
        Func<string, bool>? owned = null,
        Func<string, bool>? usable = null,
        bool force = false)
        => SubtitleManager.PlanTranslationTargets(
            targets,
            new HashSet<string>(audio ?? new[] { "en" }, StringComparer.OrdinalIgnoreCase),
            source,
            _ => canary,
            owned ?? (_ => false),
            usable ?? (_ => false),
            force);

    [Fact]
    public void Plan_NoTargets_PlansNothing()
    {
        Assert.Empty(Plan(Array.Empty<string>()));
    }

    [Fact]
    public void Plan_EnglishAudio_RoutesEveryTargetToCanaryInConfigOrder()
    {
        var plans = Plan(new[] { "nl", "de", "lv" });
        Assert.Equal(new[] { "nl", "de", "lv" }, plans.Select(p => p.Target));
        Assert.All(plans, p =>
        {
            Assert.Null(p.SkipReason);
            Assert.Equal(TranslationEngine.Canary, p.Route.Engine);
        });
    }

    [Fact]
    public void Plan_AudioAlreadyInTheTarget_IsSkipped()
    {
        var plans = Plan(new[] { "es", "nl" }, audio: new[] { "en", "es" });
        Assert.Contains("audio is already in 'es'", plans[0].SkipReason);
        Assert.Null(plans[1].SkipReason);
    }

    // The plugin's own translated file counts even under force, exactly like the English pass.
    [Fact]
    public void Plan_OwnedTranslationExists_IsSkippedEvenWhenForced()
    {
        var plans = Plan(new[] { "nl", "de" }, owned: t => t == "nl", force: true);
        Assert.Contains("already exists", plans[0].SkipReason);
        Assert.Null(plans[1].SkipReason);
    }

    [Fact]
    public void Plan_UsableSubtitleExists_IsSkippedUnlessForced()
    {
        Assert.Contains("usable 'nl' subtitle", Plan(new[] { "nl" }, usable: t => t == "nl")[0].SkipReason);
        Assert.Null(Plan(new[] { "nl" }, usable: t => t == "nl", force: true)[0].SkipReason);
    }

    // Non-English audio is the normal case for most of a library: the target skips, it never fails.
    [Fact]
    public void Plan_NonEnglishAudio_IsSkippedNotFailed()
    {
        var plan = Assert.Single(Plan(new[] { "nl" }, source: "fr", audio: new[] { "fr" }));
        Assert.Equal(TranslationEngine.SkipSourceNotEnglish, plan.Route.Engine);
        Assert.Contains("'fr'", plan.SkipReason);
        Assert.Equal(SubtitleManager.GenerationOutcome.Skipped, SubtitleManager.PlannedOutcome(plan));
    }

    [Fact]
    public void Plan_UnknownAudio_IsSkipped()
    {
        var plan = Assert.Single(Plan(new[] { "nl" }, source: "auto", audio: Array.Empty<string>()));
        Assert.Equal(SubtitleManager.GenerationOutcome.Skipped, SubtitleManager.PlannedOutcome(plan));
    }

    [Fact]
    public void Plan_CanaryMissing_IsEngineMissingAndFails()
    {
        var plan = Assert.Single(Plan(new[] { "de" }, canary: false));
        Assert.Null(plan.SkipReason);
        Assert.Equal(TranslationEngine.EngineMissing, plan.Route.Engine);
        Assert.Equal(SubtitleManager.GenerationOutcome.Failed, SubtitleManager.PlannedOutcome(plan));
        Assert.Contains("not installed", SubtitleManager.TargetFailureMessage(plan, "A Title"));
    }

    // A target outside the catalog is config corruption: it fails even for English audio.
    [Fact]
    public void Plan_TargetOutsideTheCatalog_Fails()
    {
        var plan = Assert.Single(Plan(new[] { "xx" }));
        Assert.Equal(TranslationEngine.Unsupported, plan.Route.Engine);
        Assert.Equal(SubtitleManager.GenerationOutcome.Failed, SubtitleManager.PlannedOutcome(plan));
    }

    [Fact]
    public void Plan_CanaryRoute_LeavesTheOutcomeToTheRun()
    {
        Assert.Null(SubtitleManager.PlannedOutcome(Assert.Single(Plan(new[] { "nl" }))));
    }

    // A satisfied target is skipped before its route matters, so a title that already has the
    // subtitle never fails just because no engine can serve it.
    [Fact]
    public void Plan_SkipWinsOverAFailingRoute()
    {
        var plan = Assert.Single(Plan(new[] { "nl" }, canary: false, usable: _ => true));
        Assert.Contains("usable 'nl' subtitle", plan.SkipReason);
        Assert.Equal(TranslationEngine.EngineMissing, plan.Route.Engine);
        Assert.Equal(SubtitleManager.GenerationOutcome.Skipped, SubtitleManager.PlannedOutcome(plan));
    }

    // ── Item outcome ───────────────────────────────────────────────────────

    // Spanish audio, targets [nl], and the English pass skipped because the title already ships an
    // English subtitle: every pass skips, so the item succeeds and nothing is generated.
    [Fact]
    public void Item_SpanishAudioWithEnglishSubtitle_SucceedsWithNothingGenerated()
    {
        var plans = Plan(new[] { "nl" }, source: "es", audio: new[] { "es" }, canary: false);
        var outcomes = new List<SubtitleManager.GenerationOutcome> { SubtitleManager.GenerationOutcome.Skipped };
        outcomes.AddRange(plans.Select(p => SubtitleManager.PlannedOutcome(p)!.Value));

        Assert.All(outcomes, o => Assert.Equal(SubtitleManager.GenerationOutcome.Skipped, o));
        Assert.False(SubtitleManager.AllAttemptsFailed(outcomes));
    }

    // English audio, targets [nl], no engine: the English pass skips (English audio), the nl target
    // fails, so the item fails and the error names the missing engine.
    [Fact]
    public void Item_EnglishAudioWithoutEngine_FailsNamingTheEngine()
    {
        var plan = Assert.Single(Plan(new[] { "nl" }, canary: false));
        var outcomes = new[] { SubtitleManager.GenerationOutcome.Skipped, SubtitleManager.PlannedOutcome(plan)!.Value };

        Assert.True(SubtitleManager.AllAttemptsFailed(outcomes));
        var message = SubtitleManager.TargetFailureMessage(plan, "A Title");
        Assert.Contains("'nl'", message);
        Assert.Contains("Canary engine", message);
    }

    [Fact]
    public void AllAttemptsFailed_NeedsAtLeastOneAttemptAndNoSuccess()
    {
        var skip = SubtitleManager.GenerationOutcome.Skipped;
        var ok = SubtitleManager.GenerationOutcome.Succeeded;
        var fail = SubtitleManager.GenerationOutcome.Failed;
        Assert.False(SubtitleManager.AllAttemptsFailed(Array.Empty<SubtitleManager.GenerationOutcome>()));
        Assert.False(SubtitleManager.AllAttemptsFailed(new[] { skip, skip }));
        Assert.False(SubtitleManager.AllAttemptsFailed(new[] { ok, fail }));
        Assert.True(SubtitleManager.AllAttemptsFailed(new[] { skip, fail }));
    }

    // ── "Already translated" per language ─────────────────────────────────

    // With no extra targets the English pass keeps its original rule: ANY owned translated file.
    [Fact]
    public void HasOwnedTranslation_WithoutPerLanguage_AnyOwnedFileCounts()
    {
        Assert.True(SubtitleManager.HasOwnedTranslation(new[] { "Movie.nl.WhisperSubs.translated.srt" }, "Movie", "en", perLanguage: false));
        Assert.False(SubtitleManager.HasOwnedTranslation(Array.Empty<string>(), "Movie", "en", perLanguage: false));
    }

    [Theory]
    [InlineData("Movie.en.WhisperSubs.translated.srt", "en", true)]
    [InlineData("Movie.en.translated.srt", "en", true)]                  // legacy name
    [InlineData("Movie-en-WhisperSubs.translated.srt", "en", true)]      // custom separators
    [InlineData("Movie.nl.WhisperSubs.translated.srt", "en", false)]
    [InlineData("Movie.nl.WhisperSubs.translated.srt", "nl", true)]
    [InlineData("It.en.WhisperSubs.translated.srt", "it", false)]        // media name is not a language
    [InlineData("MOVIE.NL.WhisperSubs.translated.srt", "nl", true)]
    public void HasOwnedTranslation_PerLanguage_MatchesTheLanguageToken(string fileName, string language, bool expected)
    {
        var mediaBaseName = fileName.StartsWith("It.", StringComparison.Ordinal) ? "It" : "Movie";
        Assert.Equal(expected, SubtitleManager.HasOwnedTranslation(new[] { fileName }, mediaBaseName, language, perLanguage: true));
    }

    // ── Canary availability ────────────────────────────────────────────────

    [Theory]
    [InlineData("/b/crispasr", "/m/c.gguf", true)]
    [InlineData("", "/m/c.gguf", false)]
    [InlineData("/b/crispasr", "", false)]
    [InlineData(null, null, false)]
    [InlineData("/missing/crispasr", "/m/c.gguf", false)]
    public void IsCanaryInstalled_NeedsBothFiles(string? binary, string? model, bool expected)
    {
        var present = new HashSet<string> { "/b/crispasr", "/m/c.gguf" };
        Assert.Equal(expected, SubtitleProviderFactory.IsCanaryInstalled(binary, model, present.Contains));
    }
}
