using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// English is not free. A per-title job into English needs a worker that translates into it, and this
/// server's own worker counts only while its Whisper model can translate: a turbo model writes the
/// source language under an English name. The 409 check and the job apply the same rule.
/// </summary>
public class EnglishTargetTests
{
    private const string Turbo = "ggml-large-v3-turbo-q5_0.bin";
    private const string LargeV3 = "ggml-large-v3-q5_0.bin";

    private sealed class FakeProvider : ISubtitleProvider
    {
        public string Name => "Fake";
        public bool RequiresSpeechAlignmentOptIn => false;
        public Task<string> TranscribeAsync(string audioPath, string language, CancellationToken ct, bool translate = false, string? targetLanguage = null)
            => Task.FromResult(string.Empty);
        public Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken ct)
            => Task.FromResult(("en", 1f));
    }

    private static WhisperWorker TranscribeOnlyRow() => new() { ApiUrl = "http://w1", Enabled = true, CanTranslate = false };

    private static ITranscriptionWorker Local() => new TranscriptionWorker("local", "local", new FakeProvider(),
        new WorkerCapabilities { IsLocal = true, TranslateTargets = WorkerTargets.ForLocal(canaryInstalled: false) });

    private static ITranscriptionWorker Remote(string id, params string[] targets) => new TranscriptionWorker(id, id, new FakeProvider(),
        new WorkerCapabilities { IsLocal = false, TranslateTargets = WorkerTargets.Set(targets) });

    [Fact]
    public void TheCatalogsRecommendedModel_CannotTranslate()
    {
        Assert.False(ModelCatalog.IsTranslationCapable(Turbo));
        Assert.True(ModelCatalog.IsTranslationCapable(LargeV3));
    }

    // ── Without a pool: the settings ───────────────────────────────────────

    [Fact]
    public void ServedByConfig_English_NeedsAWorkerThatTranslatesIntoIt()
    {
        // Local worker off, one transcribe-only row: nothing makes English (the reviewed 409 gap).
        Assert.False(WorkerTargets.ServedByConfig("en", canaryInstalled: false, hostsLocal: false, rows: new[] { TranscribeOnlyRow() }));
        // This server's own worker makes it, while its model can translate.
        Assert.True(WorkerTargets.ServedByConfig("en", false, hostsLocal: true, rows: null));
        Assert.False(WorkerTargets.ServedByConfig("en", false, hostsLocal: true, rows: null, localWhisperTranslates: false));
        // A row that lists English, or the single legacy remote server, makes it whatever the local model.
        Assert.True(WorkerTargets.ServedByConfig("en", false, true, new[] { new WhisperWorker { ApiUrl = "http://w2", Enabled = true } }, localWhisperTranslates: false));
        Assert.True(WorkerTargets.ServedByConfig("en", false, hostsLocal: false, rows: null, legacyRemote: true));
        // The local model has no say over a Canary target.
        Assert.True(WorkerTargets.ServedByConfig("es", canaryInstalled: true, hostsLocal: true, rows: null, localWhisperTranslates: false));
    }

    // ── With a pool ────────────────────────────────────────────────────────

    [Fact]
    public void ForTarget_KeepsEnglishOffTheLocalWorkerOnlyWhenItsModelCannotTranslate()
    {
        Assert.True(WorkerJob.ForTarget("en", localWhisperTranslates: false).RemoteOnly);
        Assert.False(WorkerJob.ForTarget("en").RemoteOnly);
        Assert.False(WorkerJob.ForTarget("es", localWhisperTranslates: false).RemoteOnly);

        var localSlot = new WorkerSlot("local", true, 0, Local().Capabilities);
        var remoteSlot = new WorkerSlot("r", true, 0, Remote("r", "en").Capabilities);
        Assert.True(WorkerScheduling.CanServe(localSlot, WorkerJob.ForTarget("en")));
        Assert.False(WorkerScheduling.CanServe(localSlot, WorkerJob.ForTarget("en", localWhisperTranslates: false)));
        Assert.True(WorkerScheduling.CanServe(remoteSlot, WorkerJob.ForTarget("en", localWhisperTranslates: false)));
    }

    [Fact]
    public void LocalOnlyPool_WithATurboModel_CannotServeEnglish()
    {
        var pool = new WorkerPool(new[] { Local() });
        var own = pool.TryAcquire(new JobRequirements(null, null))!.Value;
        Assert.True(new PoolTargetEngines(pool, own).CanServe("en"));
        Assert.False(new PoolTargetEngines(pool, own, localWhisperTranslates: false).CanServe("en"));
    }

    [Fact]
    public async Task EnglishJobOnTheLocalWorker_WithATurboModel_MovesToAWorkerThatTranslates()
    {
        var pool = new WorkerPool(new[] { Local(), Remote("remote", "en") });
        var own = pool.TryAcquire(new JobRequirements(null, null))!.Value;
        Assert.Equal("local", own.Worker.Id);   // cost 0 and first: the dispatcher's usual pick

        var released = 0;
        var engines = new PoolTargetEngines(pool, own, releaseOwnLease: () => { released++; pool.Release(own.Key); },
            localWhisperTranslates: false);
        using var engine = await engines.AcquireAsync("en", "Film", CancellationToken.None);
        Assert.Equal("remote", engine.WorkerName);
        Assert.Equal(1, released);
    }

    // ── The reason, for the 409 and the job ────────────────────────────────

    [Fact]
    public void EnglishMissingReason_NamesTheTurboModelOnlyWhenThatIsTheCause()
    {
        var turbo = TranslationRoute.EnglishMissingReason(localInPool: true, localWhisperTranslates: false, "/models/" + Turbo);
        Assert.Contains(Turbo, turbo);
        Assert.Contains("turbo", turbo);
        Assert.DoesNotContain("/models/", turbo);

        var none = TranslationRoute.EnglishMissingReason(localInPool: false, localWhisperTranslates: false, Turbo);
        Assert.StartsWith("No worker in the pool translates into English.", none);
        Assert.Equal(none, TranslationRoute.EnglishMissingReason(localInPool: true, localWhisperTranslates: true, LargeV3));
    }
}
