using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// v4.0 dispatcher: the live worker pool that replaced the global TranscriptionLock. Pins the concurrency
/// gate (ΣMaxConcurrency, one-slot serialisation at the default), cost-weighted local-first routing, the
/// fail-fast capability check, and — the subtle one — that releasing by the lease key keeps accounting
/// correct even when two workers collide on Id.
/// </summary>
public class WorkerPoolTests
{
    private sealed class FakeProvider : ISubtitleProvider
    {
        public string Name => "fake";
        public bool UsesVad => false;
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
    public void TotalCapacity_IsSumOfMaxConcurrency_AtLeastOne()
    {
        var pool = new WorkerPool(new[] { Worker("a", 1), Worker("b", 3) });
        Assert.Equal(4, pool.TotalCapacity);
        Assert.Equal(2, pool.WorkerCount);

        // A worker with a nonsense concurrency still contributes at least one slot.
        var floored = new WorkerPool(new[] { Worker("z", 0) });
        Assert.Equal(1, floored.TotalCapacity);
    }

    [Fact]
    public async Task SingleWorkerSingleSlot_SerialisesLikeTheOldLock()
    {
        var pool = new WorkerPool(new[] { Worker("local", 1) });

        var first = await pool.AcquireAsync(AnyJob, default);
        Assert.Equal(1, pool.ActiveJobs);

        // The only slot is taken — a second acquire must block (not complete).
        var second = pool.AcquireAsync(AnyJob, default);
        Assert.False(second.IsCompleted);

        pool.Release(first.Key);                    // frees the slot
        var unblocked = await second;
        Assert.Equal(1, pool.ActiveJobs);

        pool.Release(unblocked.Key);
        Assert.Equal(0, pool.ActiveJobs);
    }

    [Fact]
    public async Task ConcurrentAcquires_UpToCapacity_ThenBlockUntilRelease()
    {
        var pool = new WorkerPool(new[] { Worker("a", 1), Worker("b", 1) }); // capacity 2
        var a = await pool.AcquireAsync(AnyJob, default);
        var b = await pool.AcquireAsync(AnyJob, default);
        Assert.Equal(2, pool.ActiveJobs);

        var third = pool.AcquireAsync(AnyJob, default);
        Assert.False(third.IsCompleted);            // capacity reached ⇒ blocks

        pool.Release(a.Key);
        var c = await third;                         // a freed ⇒ unblocks
        Assert.Equal(2, pool.ActiveJobs);

        pool.Release(b.Key);
        pool.Release(c.Key);
        Assert.Equal(0, pool.ActiveJobs);
    }

    [Fact]
    public async Task Acquire_PrefersLocalOverPaid_ByCostWeight()
    {
        var pool = new WorkerPool(new[] { Worker("cloud", 1, cost: 5), Worker("local", 1, cost: 0) });
        var lease = await pool.AcquireAsync(AnyJob, default);
        Assert.Equal("local", lease.Worker.Id);      // cost 0 strictly beats cost 5
        pool.Release(lease.Key);
    }

    [Fact]
    public void HasCapableWorker_FalseWhenTranslateNeededButNoneTranslate()
    {
        var pool = new WorkerPool(new[] { Worker("t", 1, canTranslate: false) });
        Assert.False(pool.HasCapableWorker(new JobRequirements(Translate: true, RequiredModel: null)));
        Assert.True(pool.HasCapableWorker(new JobRequirements(Translate: false, RequiredModel: null)));
    }

    [Fact]
    public async Task AcquireAsync_ThrowsWhenNoCapableWorker_NeverSpins()
    {
        // Defense-in-depth (CodeRabbit): a transcribe-only pool asked for a translate job must fail fast
        // with InvalidOperationException, not loop forever acquiring/releasing the slot.
        var pool = new WorkerPool(new[] { Worker("t", 1, canTranslate: false) });
        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => pool.AcquireAsync(new JobRequirements(Translate: true, RequiredModel: null), default));
    }

    [Fact]
    public async Task Snapshot_ReflectsIdentityAndLiveLoad()
    {
        var pool = new WorkerPool(new[] { Worker("local", 2, cost: 0), Worker("cloud", 1, cost: 5) });

        var idle = pool.Snapshot();
        Assert.Equal(2, idle.Count);
        var local = idle.Single(s => s.Id == "local");
        Assert.Equal(2, local.MaxConcurrency);
        Assert.True(local.IsLocal);
        Assert.Equal(0, local.InFlight);
        var cloud = idle.Single(s => s.Id == "cloud");
        Assert.Equal(5, cloud.CostWeight);
        Assert.False(cloud.IsLocal);

        // Acquire one slot → the snapshot's InFlight reflects it (local is cheapest, so it's taken first).
        var lease = await pool.AcquireAsync(AnyJob, default);
        pool.SetCurrent(lease.Key, "The Movie S01E12");
        var busy = pool.Snapshot().Single(s => s.Id == "local");
        Assert.Equal(1, busy.InFlight);
        Assert.Contains("The Movie S01E12", busy.CurrentItems);   // "what's running where"

        pool.Release(lease.Key, "The Movie S01E12");
        var freed = pool.Snapshot().Single(s => s.Id == "local");
        Assert.Equal(0, freed.InFlight);
        Assert.Empty(freed.CurrentItems);                          // cleared on release
    }

    [Fact]
    public async Task DuplicateWorkerIds_DoNotCollide_BothSlotsUsableAndReleasable()
    {
        // Two workers misconfigured with the SAME Id — the lease key must keep per-slot accounting distinct.
        var pool = new WorkerPool(new[] { Worker("dup", 1), Worker("dup", 1) });
        Assert.Equal(2, pool.TotalCapacity);

        var a = await pool.AcquireAsync(AnyJob, default);
        var b = await pool.AcquireAsync(AnyJob, default);
        Assert.Equal(2, pool.ActiveJobs);            // both slots acquired despite the Id clash
        Assert.NotEqual(a.Key, b.Key);               // distinct routing keys

        pool.Release(a.Key);
        pool.Release(b.Key);
        Assert.Equal(0, pool.ActiveJobs);            // both released cleanly (would be 1 if released by Id)
    }

    [Fact]
    public async Task Ctor_DuplicateIds_KeepsBothWorkers_DistinctRoutingKeys_AndSummedCapacity()
    {
        // Characterization (whisper-subs-9gq): two workers colliding on Id must NOT collapse — the ctor's
        // collision-safe scheme keys them "dup" and "dup#1", and capacity is the SUM of their capped
        // concurrencies. Pins the routing-key behavior so a refactor can't silently merge them (which would
        // drop a worker / halve capacity). Snapshot still surfaces both (each under its own Id "dup").
        var pool = new WorkerPool(new[] { Worker("dup", 2), Worker("dup", 3) });

        Assert.Equal(2, pool.WorkerCount);
        Assert.Equal(5, pool.TotalCapacity);        // 2 + 3
        Assert.Equal(2, pool.Snapshot().Count);     // both present

        // The internal routing keys are observable via the lease keys: filling all 5 slots must touch BOTH
        // "dup" and "dup#1", proving the collision suffix keeps the reverse key→worker lookup unambiguous.
        var leases = new System.Collections.Generic.List<WorkerLease>();
        for (var i = 0; i < 5; i++) leases.Add(await pool.AcquireAsync(AnyJob, default));
        var keys = leases.Select(l => l.Key).Distinct().OrderBy(k => k, System.StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "dup", "dup#1" }, keys);

        foreach (var l in leases) pool.Release(l.Key);
        Assert.Equal(0, pool.ActiveJobs);
    }
}
