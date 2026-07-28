using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// whisper-subs-9gq: hot-adding a worker to the LIVE pool without a restart. Pins the grow-only diff, the
/// capacity/semaphore growth (the uncapped SemaphoreSlim actually hands out the extra slots), that a waiter
/// blocked on a full pool is unblocked by the new permit and routed to the added worker, and — the safety
/// one — that a worker already running a job keeps its in-flight state across a Reconcile (no teardown).
/// </summary>
public class WorkerPoolReconcileTests
{
    private sealed class FakeProvider : ISubtitleProvider
    {
        public string Name => "fake";
        public bool RequiresSpeechAlignmentOptIn => false;
        public Task<string> TranscribeAsync(string audioPath, string language, CancellationToken ct, bool translate = false)
            => Task.FromResult(string.Empty);
        public Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken ct)
            => Task.FromResult(("en", 1f));
    }

    private static ITranscriptionWorker Worker(string id, int maxConcurrency = 1, double cost = 0, bool canTranslate = true)
        => new TranscriptionWorker(id, id, new FakeProvider(), new WorkerCapabilities
        {
            MaxConcurrency = maxConcurrency,
            CostWeight = cost,
            CanTranslate = canTranslate,
            IsLocal = cost == 0
        });

    private static readonly JobRequirements AnyJob = new(Translate: false, RequiredModel: null);

    [Fact]
    public void Reconcile_AddsNewWorker_GrowsCapacityAndAppearsInSnapshot()
    {
        var pool = new WorkerPool(new[] { Worker("local", 1) });
        Assert.Equal(1, pool.TotalCapacity);
        Assert.Equal(1, pool.WorkerCount);

        var count = pool.Reconcile(new[] { Worker("local", 1), Worker("mini", 2) });

        Assert.Equal(2, count);
        Assert.Equal(2, pool.WorkerCount);
        Assert.Equal(3, pool.TotalCapacity);   // 1 + 2

        var snap = pool.Snapshot();
        Assert.Equal(2, snap.Count);
        Assert.Contains(snap, s => s.Id == "mini" && s.MaxConcurrency == 2);
        Assert.Contains(snap, s => s.Id == "local");
    }

    [Fact]
    public async Task Reconcile_UncappedSemaphore_HandsOutFullGrownCapacity()
    {
        var pool = new WorkerPool(new[] { Worker("a", 2) });   // N = 2
        Assert.Equal(2, pool.TotalCapacity);

        var count = pool.Reconcile(new[] { Worker("a", 2), Worker("b", 3) });   // + M = 3
        Assert.Equal(2, count);
        Assert.Equal(5, pool.TotalCapacity);   // N + M — the semaphore max grew live

        // Prove the uncapped semaphore really grew: 5 concurrent acquires all succeed, the 6th blocks.
        var leases = new List<WorkerLease>();
        for (var i = 0; i < 5; i++) leases.Add(await pool.AcquireAsync(AnyJob, default));
        Assert.Equal(5, pool.ActiveJobs);

        var sixth = pool.AcquireAsync(AnyJob, default);
        Assert.False(sixth.IsCompleted);   // capacity reached ⇒ blocks

        foreach (var l in leases) pool.Release(l.Key);
        var s = await sixth;   // a slot freed ⇒ unblocks
        pool.Release(s.Key);
        Assert.Equal(0, pool.ActiveJobs);
    }

    [Fact]
    public async Task Reconcile_UnblocksAWaiter_MidDrain_RoutesToNewWorker()
    {
        var pool = new WorkerPool(new[] { Worker("a", 1) });   // capacity 1
        var a = await pool.AcquireAsync(AnyJob, default);
        Assert.Equal(1, pool.ActiveJobs);

        // The only slot is taken — a second acquire blocks, exactly like a saturated live pool mid-backlog.
        var blocked = pool.AcquireAsync(AnyJob, default);
        Assert.False(blocked.IsCompleted);

        // Hot-add a second worker: the new permit must wake the waiter, and PickLocked must see the new
        // worker (map-add precedes the Release) and route to it since "a" is still busy.
        pool.Reconcile(new[] { Worker("a", 1), Worker("b", 1) });

        var b = await blocked;
        Assert.Equal(2, pool.ActiveJobs);
        Assert.Equal("b", b.Worker.Id);

        pool.Release(a.Key);
        pool.Release(b.Key);
        Assert.Equal(0, pool.ActiveJobs);
    }

    [Fact]
    public void Reconcile_UnchangedWorkerSet_IsNoOp()
    {
        var pool = new WorkerPool(new[] { Worker("a", 1), Worker("b", 2) });
        Assert.Equal(3, pool.TotalCapacity);

        var count = pool.Reconcile(new[] { Worker("a", 1), Worker("b", 2) });   // same keys

        Assert.Equal(2, count);
        Assert.Equal(2, pool.WorkerCount);
        Assert.Equal(3, pool.TotalCapacity);   // unchanged — no permit added
    }

    [Fact]
    public async Task Reconcile_PreservesExistingWorkerInFlight_NoTeardown()
    {
        var pool = new WorkerPool(new[] { Worker("busy", 1) });
        var lease = await pool.AcquireAsync(AnyJob, default);
        pool.SetCurrent(lease.Key, "Film A");
        Assert.Equal(1, pool.ActiveJobs);

        // Add another worker while "busy" holds a job — its in-flight count and current item must survive.
        pool.Reconcile(new[] { Worker("busy", 1), Worker("fresh", 1) });

        var busy = pool.Snapshot().Single(s => s.Id == "busy");
        Assert.Equal(1, busy.InFlight);                 // untouched by the reconcile
        Assert.Contains("Film A", busy.CurrentItems);   // "what's running" preserved
        var fresh = pool.Snapshot().Single(s => s.Id == "fresh");
        Assert.Equal(0, fresh.InFlight);

        // The original lease is still valid and releasable — slot accounting stayed intact.
        pool.Release(lease.Key, "Film A");
        Assert.Equal(0, pool.ActiveJobs);
    }

    [Fact]
    public void DiffWorkers_IdentifiesAdds_PreservesExisting_IgnoresRemovals()
    {
        var (added, removed) = WorkerPool.DiffWorkers(
            new[] { "local" },
            new[] { Worker("local", 1), Worker("mini", 1) });

        Assert.Equal(new[] { "mini" }, added);   // local already present → not re-added
        Assert.Empty(removed);

        // A worker dropped from desired is reported as removed — but Reconcile ignores it (grow-only).
        var (added2, removed2) = WorkerPool.DiffWorkers(
            new[] { "local", "gone" },
            new[] { Worker("local", 1) });

        Assert.Empty(added2);
        Assert.Equal(new[] { "gone" }, removed2);
    }

    [Fact]
    public void DiffWorkers_DuplicateIds_GetCollisionSuffix_SoBothAreAddable()
    {
        // Two desired workers colliding on Id must diff to two distinct keys (the ctor's "{id}#{i}" scheme),
        // so both can be hot-added rather than one silently swallowing the other.
        var (added, _) = WorkerPool.DiffWorkers(
            System.Array.Empty<string>(),
            new[] { Worker("dup", 1), Worker("dup", 1) });

        Assert.Equal(2, added.Count);
        Assert.Contains("dup", added);
        Assert.Contains("dup#1", added);
    }

    [Fact]
    public void Reconcile_ShrinkingDesiredSet_LeavesExistingWorkersAndCapacityIntact()
    {
        // M2/M3 wiring (whisper-subs-9gq): Reconcile now diffs the desired set, but stays GROW-ONLY — a worker
        // dropped from 'desired' must NOT be removed or shrink capacity (it is reported for observability only;
        // it stays live until the next idle rebuild / restart). Proves the (now-wired) DiffWorkers 'removed'
        // path does not disturb existing workers or the concurrency ceiling.
        var pool = new WorkerPool(new[] { Worker("a", 1), Worker("b", 2) });
        Assert.Equal(2, pool.WorkerCount);
        Assert.Equal(3, pool.TotalCapacity);

        var count = pool.Reconcile(new[] { Worker("a", 1) });   // "b" dropped from desired

        Assert.Equal(2, count);
        Assert.Equal(2, pool.WorkerCount);                       // "b" still live (grow-only)
        Assert.Equal(3, pool.TotalCapacity);                     // capacity unchanged — no shrink
        Assert.Contains(pool.Snapshot(), s => s.Id == "b");
    }
}
