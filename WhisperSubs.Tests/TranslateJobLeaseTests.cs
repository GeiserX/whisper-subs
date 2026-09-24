using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// How a translate job gets the worker that makes its target. The dispatcher chooses the job and its worker
/// together (<see cref="DispatchPlacement"/>, <see cref="DispatchScan"/>): a job leaves the queue only when a
/// free worker that can serve it is leased for it, so no job gives a slot back to wait for another worker
/// while the dispatcher keeps dequeuing. Real <see cref="WorkerPool"/>, fake providers.
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

    private static ITranscriptionWorker Worker(string id, bool isLocal, params string[] targets)
        => new TranscriptionWorker(id, id, new FakeProvider(), new WorkerCapabilities { IsLocal = isLocal, TranslateTargets = WorkerTargets.Set(targets) });

    private static readonly JobRequirements ItemRequirements = WorkerJob.Requirements(SubtitleMode.Full, enableTranslation: true);

    // A queued job: a generate job (Target null) or a translate job for one target.
    private sealed record Job(string Name, string? Target);

    private static JobRequirements? RequirementsOf(Job job, WorkerPool pool, bool poolServesItems = true, bool localWhisperTranslates = true)
        => DispatchPlacement.RequirementsOf(job.Target, ItemRequirements, poolServesItems, localWhisperTranslates, pool.HasCapableWorker);

    // The dispatcher's walk over a FIFO queue, as TryDequeuePlaceable does it over the priority lanes.
    private static (Job? Job, WorkerLease? Lease, DispatchScan Scan) PlaceNext(List<Job> queue, WorkerPool pool, bool poolServesItems = true)
    {
        var scan = new DispatchScan(pool);
        for (var i = 0; i < queue.Count; i++)
        {
            var visit = scan.Visit(RequirementsOf(queue[i], pool, poolServesItems));
            if (visit == LaneVisit.Stop) break;
            if (visit == LaneVisit.Take)
            {
                var job = queue[i];
                queue.RemoveAt(i);
                return (job, scan.Lease, scan);
            }
        }
        return (null, null, scan);
    }

    // ── The dispatcher's placement ─────────────────────────────────────────

    // A series-sized burst of translate jobs for a target only this server makes, generate jobs behind
    // them, and this server busy. Under the old shape every translate job leased the free worker, gave it
    // back and waited, so the whole burst left the queue as in-flight waiters holding no slot.
    [Fact]
    public void BurstOfTranslateJobs_ForABusyWorker_StaysQueuedWhileGenerateJobsRunElsewhere()
    {
        var pool = new WorkerPool(new[] { Worker("local", true, "en", "es"), Worker("gpu", false, "en") });
        var longJob = pool.TryAcquire(WorkerJob.ForTarget("es"))!.Value;   // this server is busy
        Assert.Equal("local", longJob.Worker.Id);

        var queue = Enumerable.Range(1, 6).Select(i => new Job($"Episode {i}", "es"))
            .Concat(Enumerable.Range(1, 3).Select(i => new Job($"Film {i}", null)))
            .ToList();
        var running = new List<(Job Job, WorkerLease Lease)>();
        var order = new List<string>();

        void AssertInvariants()
        {
            Assert.True(pool.ActiveJobs <= pool.TotalCapacity);
            // Every dispatched job holds exactly one slot, on a worker that can serve it: no waiter without a slot.
            var held = running.Count + (longJob.Key != null ? 1 : 0);
            Assert.Equal(held, pool.ActiveJobs);
            foreach (var (job, lease) in running)
            {
                var needs = RequirementsOf(job, pool)!.Value;
                Assert.True(WorkerScheduling.CanServe(new WorkerSlot(lease.Key, true, 0, lease.Worker.Capabilities), needs));
                if (job.Target != null) Assert.Contains(job.Target, lease.Worker.Capabilities.TranslateTargets);
            }
        }

        bool Dispatch()
        {
            var (job, lease, scan) = PlaceNext(queue, pool);
            if (job == null)
            {
                Assert.False(scan.BlockedOnlyByUnavailable);   // busy, not broken: the dispatcher waits for a release
                return false;
            }
            running.Add((job, lease!.Value));
            order.Add($"{job.Name}@{lease.Value.Worker.Id}");
            AssertInvariants();
            return true;
        }

        // This server is busy: the generate job at the back runs on the other worker; no translate job moves.
        Assert.True(Dispatch());
        Assert.Equal("Film 1@gpu", order[0]);
        Assert.False(Dispatch());
        Assert.Equal(8, queue.Count);
        Assert.All(queue.Take(6), j => Assert.Equal("es", j.Target));

        // This server frees: the first translate job takes it, in queue order.
        pool.Release(longJob.Key);
        longJob = default;
        Assert.True(Dispatch());
        Assert.Equal("Episode 1@local", order[^1]);

        // Finish jobs one at a time and keep dispatching until the queue is empty.
        while (queue.Count > 0 || running.Count > 0)
        {
            if (Dispatch()) continue;
            var done = running[0];
            running.RemoveAt(0);
            pool.Release(done.Lease.Key);
        }

        Assert.Equal(9, order.Count);
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"Episode {i}@local"), order.Where(o => o.StartsWith("Episode")));
        Assert.All(order.Where(o => o.StartsWith("Film")), o => Assert.EndsWith("@gpu", o));
        Assert.Equal(0, pool.ActiveJobs);
    }

    [Fact]
    public void NothingPlaceable_TargetWorkerOutOfRotation_PausesInsteadOfWaiting()
    {
        var pool = new WorkerPool(new[] { Worker("local", true, "en", "es"), Worker("gpu", false, "en") });
        Assert.True(pool.SetLocalAvailability("whisper-cli cannot start"));
        var queue = new List<Job> { new("Episode 1", "es"), new("Episode 2", "es") };

        var (job, _, scan) = PlaceNext(queue, pool);

        Assert.Null(job);
        Assert.True(scan.BlockedOnlyByUnavailable);
        Assert.Equal("whisper-cli cannot start", pool.NoneAvailableReason(scan.UnavailableRequirement!.Value));
        Assert.Equal(2, queue.Count);             // nothing dequeued: both keep their place and retries
        Assert.Equal(0, pool.ActiveJobs);
    }

    [Fact]
    public void NothingPlaceable_SomeJobWaitsForABusyWorker_WaitsInsteadOfPausing()
    {
        var pool = new WorkerPool(new[] { Worker("local", true, "en", "es"), Worker("gpu", false, "en") });
        Assert.True(pool.SetLocalAvailability("whisper-cli cannot start"));
        var gpu = pool.TryAcquire(ItemRequirements)!.Value;          // the only in-rotation worker is busy
        var queue = new List<Job> { new("Episode 1", "es"), new("Film 1", null) };

        var (job, _, scan) = PlaceNext(queue, pool);

        Assert.Null(job);
        Assert.False(scan.BlockedOnlyByUnavailable);   // the film waits for gpu, which will free
        pool.Release(gpu.Key);
        Assert.Equal("Film 1", PlaceNext(queue, pool).Job!.Name);
    }

    [Fact]
    public void GenerateJob_WhenThePoolTakesNoWholeItem_IsTakenWithoutALeaseToFail()
    {
        var crispEs = new TranscriptionWorker("crisp", "crisp", new FakeProvider(), new WorkerCapabilities
        {
            IsLocal = false, TranslateTargets = WorkerTargets.Set("es"), TranscribesItems = false,
        });
        var pool = new WorkerPool(new ITranscriptionWorker[] { crispEs });
        Assert.False(pool.HasCapableWorker(ItemRequirements));
        var queue = new List<Job> { new("Film 1", null), new("Episode 1", "es") };

        var first = PlaceNext(queue, pool, poolServesItems: false);
        Assert.Equal("Film 1", first.Job!.Name);
        Assert.Null(first.Lease);
        Assert.Equal(0, pool.ActiveJobs);

        var second = PlaceNext(queue, pool, poolServesItems: false);
        Assert.Equal("crisp", second.Lease!.Value.Worker.Id);   // a target-only worker takes its translate job
    }

    // ── What a job needs ───────────────────────────────────────────────────

    [Fact]
    public void RequirementsOf_TranslateJob_NeedsItsTarget_OrAnyWorkerWhenNobodyListsIt()
    {
        var pool = new WorkerPool(new[] { Worker("local", true, "en", "es") });
        Assert.Equal(WorkerJob.ForTarget("es"), RequirementsOf(new Job("E", "es"), pool));
        // No worker makes 'de': it still runs, so the pass fails it with the target's own final reason.
        Assert.Equal(WorkerJob.AnyWorker, RequirementsOf(new Job("E", "de"), pool));
        // English with a turbo model stays off this server's worker.
        var en = RequirementsOf(new Job("E", "en"), new WorkerPool(new[] { Worker("local", true, "en"), Worker("r", false, "en") }), localWhisperTranslates: false);
        Assert.True(en!.Value.RemoteOnly);
    }

    [Fact]
    public void RequirementsOf_GenerateJob_FollowsTheItemRequirementsOrCannotRun()
    {
        var pool = new WorkerPool(new[] { Worker("local", true, "en") });
        Assert.Equal(ItemRequirements, RequirementsOf(new Job("F", null), pool));
        Assert.Null(RequirementsOf(new Job("F", null), pool, poolServesItems: false));
    }

    // ── The release signal the dispatcher waits on ─────────────────────────

    [Fact]
    public void FreedSignal_FiresOnARealRelease_NotOnAFailedTryAcquire()
    {
        var pool = new WorkerPool(new[] { Worker("local", true, "en", "es"), Worker("gpu", false, "en") });
        var local = pool.TryAcquire(WorkerJob.ForTarget("es"))!.Value;

        var signal = pool.FreedSignal;
        Assert.Null(pool.TryAcquire(WorkerJob.ForTarget("es")));   // takes gpu's slot, finds no 'es' there, hands it back
        Assert.False(signal.IsCompleted);                           // waking on that would spin the dispatcher

        pool.Release(local.Key);
        Assert.True(signal.IsCompleted);
        Assert.False(pool.FreedSignal.IsCompleted);                 // re-armed for the next wait
    }

    [Fact]
    public void FreedSignal_FiresWhenTheLocalWorkerReturnsToRotation()
    {
        var pool = new WorkerPool(new[] { Worker("local", true, "en", "es") });
        pool.SetLocalAvailability("broken");
        var signal = pool.FreedSignal;
        pool.SetLocalAvailability(null);
        Assert.True(signal.IsCompleted);
    }

    // ── The routing rule inside a job ──────────────────────────────────────

    [Fact]
    public void Decide_OwnWorkerListsTheTarget_UsesItAndNeverWaits()
    {
        Assert.Equal(TargetLeasePolicy.UseOwnWorker, TargetLeaseRouting.Decide(WorkerTargets.Set("en", "es"), "es"));
        Assert.Equal(TargetLeasePolicy.TakeFreeAnotherOnly, TargetLeaseRouting.Decide(WorkerTargets.Set("en", "de"), "es"));
        Assert.Equal(TargetLeasePolicy.WaitForAnother, TargetLeaseRouting.Decide(WorkerTargets.Set("en"), "es"));
    }

    [Fact]
    public async Task PlacedTranslateJob_UsesItsOwnWorker()
    {
        var pool = new WorkerPool(new[] { Worker("gpu", false, "en"), Worker("local", true, "en", "es") });
        var queue = new List<Job> { new("Episode 1", "es") };
        var lease = PlaceNext(queue, pool).Lease!.Value;
        Assert.Equal("local", lease.Worker.Id);

        using var engine = await new PoolTargetEngines(pool, lease).AcquireAsync("es", "Episode 1", CancellationToken.None);
        Assert.Equal("local", engine.WorkerName);
        Assert.Equal(1, pool.ActiveJobs);   // no second slot
    }

    [Fact]
    public void WholeItemWorker_IsTheCheapestWorkerThatTranscribes()
    {
        var crispEs = new TranscriptionWorker("crisp", "crisp", new FakeProvider(), new WorkerCapabilities
        {
            IsLocal = false, TranslateTargets = WorkerTargets.Set("es"), TranscribesItems = false,
        });
        var paid = new TranscriptionWorker("paid", "paid", new FakeProvider(), new WorkerCapabilities { IsLocal = false, CostWeight = 2 });
        Assert.Equal("local", new WorkerPool(new ITranscriptionWorker[] { crispEs, paid, Worker("local", true, "en") }).WholeItemWorker()!.Id);
        Assert.Equal("paid", new WorkerPool(new ITranscriptionWorker[] { crispEs, paid }).WholeItemWorker()!.Id);
        Assert.Null(new WorkerPool(new ITranscriptionWorker[] { crispEs }).WholeItemWorker());
    }

    // ── How a job's failure reaches the dispatcher ─────────────────────────

    [Fact]
    public void TranslationJobFailure_NoAvailableWorkerAndFinalErrorsPassThrough_OthersAreWrapped()
    {
        var parked = new NoAvailableWorkerException("gone");
        Assert.Same(parked, SubtitleManager.TranslationJobFailure(SubtitleManager.GenerationOutcome.Failed, parked, "Film", "es"));
        var cause = new InvalidOperationException("boom");
        var wrapped = SubtitleManager.TranslationJobFailure(SubtitleManager.GenerationOutcome.Failed, cause, "Film", "es");
        Assert.IsType<InvalidOperationException>(wrapped);
        Assert.Same(cause, wrapped!.InnerException);
        var final = new TranslationNotPossibleException("never");
        Assert.Same(final, SubtitleManager.TranslationJobFailure(SubtitleManager.GenerationOutcome.Failed, final, "Film", "es"));
        Assert.Null(SubtitleManager.TranslationJobFailure(SubtitleManager.GenerationOutcome.Skipped, null, "Film", "es"));
    }
}
