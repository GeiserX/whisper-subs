using System;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// How a translate job gets the worker that makes its target. The dispatcher leases a worker before it
/// knows the job, so the job may land on one that does not list its target. It must then wait for one
/// that does (a busy worker is not a failure), and when every such worker is out of rotation it must
/// reach the dispatcher as <see cref="NoAvailableWorkerException"/>, which keeps the job and its retries.
/// Real <see cref="WorkerPool"/>, fake providers.
/// </summary>
public class TranslateJobLeaseTests
{
    private sealed class FakeProvider : ISubtitleProvider
    {
        public string Name => "Fake";
        public bool RequiresSpeechAlignmentOptIn => false;
        public Task<string> TranscribeAsync(string audioPath, string language, CancellationToken ct, bool translate = false, string? targetLanguage = null)
            => Task.FromResult(string.Empty);
        public Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken ct)
            => Task.FromResult(("en", 1f));
    }

    private static ITranscriptionWorker Worker(string id, params string[] targets)
        => new TranscriptionWorker(id, id, new FakeProvider(), new WorkerCapabilities { TranslateTargets = WorkerTargets.Set(targets) });

    // ── The routing rule ───────────────────────────────────────────────────

    [Fact]
    public void Decide_AJobThatCanReleaseItsSlot_AlwaysWaitsHoldingNothing()
    {
        // The own worker lists other Canary targets: an item job must not wait, a translate job may.
        Assert.Equal(TargetLeasePolicy.TakeFreeAnotherOnly, TargetLeaseRouting.Decide(WorkerTargets.Set("en", "de"), "es"));
        Assert.Equal(TargetLeasePolicy.ReleaseOwnAndWait, TargetLeaseRouting.Decide(WorkerTargets.Set("en", "de"), "es", canReleaseOwn: true));
        Assert.Equal(TargetLeasePolicy.ReleaseOwnAndWait, TargetLeaseRouting.Decide(WorkerTargets.None, "en", canReleaseOwn: true));
        // Its own worker still wins when it lists the target: no second slot, nothing released.
        Assert.Equal(TargetLeasePolicy.UseOwnWorker, TargetLeaseRouting.Decide(WorkerTargets.Set("en", "es"), "es", canReleaseOwn: true));
    }

    // ── Case (a): the target's worker is busy ──────────────────────────────

    [Fact]
    public async Task TranslateJob_TargetWorkerBusy_WaitsForItInsteadOfFailing()
    {
        var pool = new WorkerPool(new[] { Worker("local", "en", "es", "de"), Worker("crisp", "en", "de") });
        var busyLocal = pool.TryAcquire(WorkerJob.ForTarget("es"))!.Value;         // a long generate job
        Assert.Equal("local", busyLocal.Worker.Id);
        var own = pool.TryAcquire(new JobRequirements(null, null))!.Value;         // the job's first lease
        Assert.Equal("crisp", own.Worker.Id);

        var released = 0;
        var engines = new PoolTargetEngines(pool, own, releaseOwnLease: () => { released++; pool.Release(own.Key); });
        var acquire = engines.AcquireAsync("es", "Film", CancellationToken.None);

        // It gave its own slot back and is waiting, not failed.
        await Task.Delay(50);
        Assert.Equal(1, released);
        Assert.False(acquire.IsCompleted);
        Assert.Equal(1, pool.ActiveJobs);

        pool.Release(busyLocal.Key);
        using var engine = await acquire.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("local", engine.WorkerName);
        Assert.Equal(1, released);
    }

    [Fact]
    public async Task ItemJob_TargetWorkerBusy_StillNeverWaits()
    {
        // Unchanged for a whole-item job (no release callback): waiting while holding a slot could deadlock.
        var pool = new WorkerPool(new[] { Worker("local", "en", "es", "de"), Worker("crisp", "en", "de") });
        Assert.Equal("local", pool.TryAcquire(WorkerJob.ForTarget("es"))!.Value.Worker.Id);
        var own = pool.TryAcquire(new JobRequirements(null, null))!.Value;
        Assert.Equal("crisp", own.Worker.Id);
        var engines = new PoolTargetEngines(pool, own);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engines.AcquireAsync("es", "Film", CancellationToken.None));
    }

    // ── Case (b): the target's only worker is out of rotation ──────────────

    [Fact]
    public async Task TranslateJob_TargetWorkerOutOfRotation_ReachesTheDispatcherAsNoAvailableWorker()
    {
        var openAi = new TranscriptionWorker("openai", "openai", new FakeProvider(), new WorkerCapabilities { IsLocal = false, TranslateTargets = WorkerTargets.None });
        var pool = new WorkerPool(new[] { Worker("local", "en", "es"), openAi });
        Assert.True(pool.SetLocalAvailability("whisper-cli cannot start"));
        var own = pool.TryAcquire(new JobRequirements(null, null))!.Value;
        Assert.Equal("openai", own.Worker.Id);

        var engines = new PoolTargetEngines(pool, own, releaseOwnLease: () => pool.Release(own.Key));
        var ex = await Assert.ThrowsAsync<NoAvailableWorkerException>(() => engines.AcquireAsync("es", "Film", CancellationToken.None));

        // The Canary pass records it as the target's failure; the job must hand it on as it is, so the
        // dispatcher keeps the job instead of spending a retry.
        Assert.Same(ex, SubtitleManager.TranslationJobFailure(SubtitleManager.GenerationOutcome.Failed, ex, "Film", "es"));
        Assert.Equal(0, pool.ActiveJobs);
    }

    [Fact]
    public void TranslationJobFailure_OtherErrorsAreStillWrapped()
    {
        var cause = new InvalidOperationException("boom");
        var wrapped = SubtitleManager.TranslationJobFailure(SubtitleManager.GenerationOutcome.Failed, cause, "Film", "es");
        Assert.IsType<InvalidOperationException>(wrapped);
        Assert.Same(cause, wrapped!.InnerException);
        var final = new TranslationNotPossibleException("never");
        Assert.Same(final, SubtitleManager.TranslationJobFailure(SubtitleManager.GenerationOutcome.Failed, final, "Film", "es"));
        Assert.Null(SubtitleManager.TranslationJobFailure(SubtitleManager.GenerationOutcome.Skipped, null, "Film", "es"));
    }

    // ── The dispatcher's first lease ───────────────────────────────────────

    [Fact]
    public void DispatchLease_FollowsTheItemRequirementsWhileThePoolServesItems()
    {
        var items = WorkerJob.Requirements(SubtitleMode.Full, enableTranslation: true);
        Assert.Equal(items, WorkerJob.DispatchLease(items, poolServesItems: true));
        Assert.Equal(WorkerJob.AnyWorker, WorkerJob.DispatchLease(items, poolServesItems: false));
    }

    // Nothing in the pool takes a whole item with EnableTranslation on, yet the 'es' row makes the
    // target: the translate job must still get a first lease, whatever the generation settings say.
    [Fact]
    public void AnyWorker_LeasesAWorkerThatTakesNoWholeItems()
    {
        var crispEs = new TranscriptionWorker("crisp", "crisp", new FakeProvider(), new WorkerCapabilities
        {
            IsLocal = false,
            TranslateTargets = WorkerTargets.Set("es"),
            TranscribesItems = false,
        });
        var openAi = new TranscriptionWorker("openai", "openai", new FakeProvider(), new WorkerCapabilities { IsLocal = false, TranslateTargets = WorkerTargets.None });
        var pool = new WorkerPool(new ITranscriptionWorker[] { crispEs, openAi });

        var items = WorkerJob.Requirements(SubtitleMode.Full, enableTranslation: true);
        Assert.False(pool.HasCapableWorker(items));
        Assert.True(pool.HasCapableWorker(WorkerJob.DispatchLease(items, pool.HasCapableWorker(items))));
        Assert.True(pool.HasCapableWorker(WorkerJob.ForTarget("es")));
    }
}
