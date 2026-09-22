using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using WhisperSubs.Controller;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Issue #185 — a local whisper-cli that cannot LAUNCH. The reporter's CUDA build outlived the container
/// it was downloaded in: without the NVIDIA runtime it lost libcuda.so.1 and exited 127 before doing any
/// work, yet the setup page still said "ready" (the file is on disk) and the dispatcher fed it 315 items,
/// spending every one's retry budget on a process that never started.
/// <para>
/// Pins the three parts of the fix that are decidable without a live Jellyfin: the cached launch verdict
/// (<see cref="LocalBinaryHealth"/>), the setup-status rules and wording, and the pool taking the local
/// worker out of rotation so queued items wait instead of burning retries.
/// </para>
/// </summary>
public class LocalBinaryLaunchTests
{
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    // ── LocalBinaryHealth: the probe cache ───────────────────────────────

    [Theory]
    [InlineData(0, true)]     // never probed is always stale
    [InlineData(1, false)]
    [InlineData(299, false)]
    [InlineData(300, true)]   // exactly the interval re-probes
    [InlineData(301, true)]
    public void IsStale_ReprobesOnlyOnceTheIntervalHasPassed(int secondsSince, bool expected)
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset? last = secondsSince == 0 ? null : now.AddSeconds(-secondsSince);
        Assert.Equal(expected, LocalBinaryHealth.IsStale(last, now, FiveMinutes));
    }

    [Fact]
    public void Check_WithinTheInterval_ServesTheCachedVerdict_WithoutProbingAgain()
    {
        // The config page polls Setup/Status; spawning whisper-cli on every poll is the thing the cache exists
        // to prevent. The clock is frozen, so every call after the first is inside the window.
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var health = new LocalBinaryHealth(() => now, FiveMinutes);
        var probes = 0;

        Assert.Equal("boom", health.Check(() => { probes++; return "boom"; }));
        Assert.Equal("boom", health.Check(() => { probes++; return "different"; }));
        Assert.Equal("boom", health.Check(() => { probes++; return "different"; }));
        Assert.Equal(1, probes);
    }

    [Fact]
    public void Check_AfterTheInterval_ProbesAgain_AndAWorkingBinaryClearsTheError()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var health = new LocalBinaryHealth(() => now, FiveMinutes);

        Assert.Equal("boom", health.Check(() => "boom"));

        now = now.AddMinutes(4);                          // still inside the window
        Assert.Equal("boom", health.Check(() => null));

        now = now.AddMinutes(2);                          // past it — the admin fixed the container
        Assert.Null(health.Check(() => null));
        Assert.Null(health.LastError);
    }

    [Fact]
    public void Invalidate_ForcesTheNextCheckToReprobe_SoANewDownloadIsJudgedOnItsOwn()
    {
        // A fresh binary must never inherit the replaced one's verdict.
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var health = new LocalBinaryHealth(() => now, FiveMinutes);

        Assert.Equal("boom", health.Check(() => "boom"));
        health.Invalidate();
        Assert.Null(health.LastError);

        Assert.Null(health.Check(() => null));            // re-probed at the same instant
    }

    [Fact]
    public void RecordLaunchFailure_TakesEffectImmediately_AndHoldsForTheInterval()
    {
        // A real job proving the binary is broken must not wait for the next probe window to be believed.
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var health = new LocalBinaryHealth(() => now, FiveMinutes);

        Assert.Null(health.LastError);
        health.RecordLaunchFailure("exit 127");
        Assert.Equal("exit 127", health.LastError);

        // …and it is a fresh verdict, so a status poll a moment later does not re-probe over it.
        Assert.Equal("exit 127", health.Check(() => throw new InvalidOperationException("must not probe")));
    }

    [Fact]
    public void Instance_IsTheOneSharedVerdict_ReprobedAtMostEveryFewMinutes()
    {
        // The status endpoint and the dispatcher must agree, and neither may spawn a process per call.
        Assert.Same(LocalBinaryHealth.Instance, LocalBinaryHealth.Instance);
        Assert.Equal(TimeSpan.FromMinutes(5), LocalBinaryHealth.DefaultProbeInterval);
    }

    // ── Resuming a parked queue on its own ───────────────────────────────

    [Theory]
    [InlineData(0, 0)]        // never probed → no wait, re-probe now
    [InlineData(1, 299)]
    [InlineData(299, 1)]
    [InlineData(300, 0)]      // exactly stale
    [InlineData(600, 0)]      // long past — never negative
    public void TimeUntilStale_IsTheWaitBeforeTheNextProbe(int secondsSince, int expectedSeconds)
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset? last = secondsSince == 0 ? null : now.AddSeconds(-secondsSince);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
            LocalBinaryHealth.TimeUntilStale(last, now, FiveMinutes));
    }

    [Fact]
    public void TimeUntilStale_AfterAProbe_IsNeverZero_SoTheResumeRetryCannotSpin()
    {
        // The dispatcher uses this as a delay. A zero here is a hot loop against a binary that cannot start.
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var health = new LocalBinaryHealth(() => now, FiveMinutes);

        Assert.Equal(TimeSpan.Zero, health.TimeUntilStale());      // nothing probed yet
        health.Check(() => "missing libcuda.so.1");
        Assert.Equal(FiveMinutes, health.TimeUntilStale());

        now = now.AddMinutes(2);
        Assert.Equal(TimeSpan.FromMinutes(3), health.TimeUntilStale());
    }

    [Theory]
    // started, paused, queueEmpty, cancelled → what the loop does next
    [InlineData(true, false, false, false, SubtitleQueueService.DrainFollowUp.ReDispatchNow)]
    [InlineData(true, true, false, false, SubtitleQueueService.DrainFollowUp.ReDispatchAfterProbeWindow)]
    [InlineData(true, true, true, false, SubtitleQueueService.DrainFollowUp.Stop)]    // nothing left to resume
    [InlineData(true, true, false, true, SubtitleQueueService.DrainFollowUp.Stop)]    // shutting down
    [InlineData(false, false, false, false, SubtitleQueueService.DrainFollowUp.Stop)] // pool build threw
    [InlineData(true, false, true, false, SubtitleQueueService.DrainFollowUp.Stop)]
    public void DecideFollowUp_PausedQueueRetriesOnADelay_NeverImmediatelyAndNeverNotAtAll(
        bool started, bool paused, bool queueEmpty, bool cancelled, SubtitleQueueService.DrainFollowUp expected)
        => Assert.Equal(expected, SubtitleQueueService.DecideFollowUp(started, paused, queueEmpty, cancelled));

    [Fact]
    public async Task AParkedQueueComesBackOnceTheProbeWindowPassesAndTheBinaryWorks()
    {
        // End to end over the real pieces the timed retry drives, with the clock and the probe faked: the
        // launch failure parks the local worker and no item can be leased; once the window passes and the
        // probe succeeds, the same call chain the retry makes leases the worker again — no enqueue, no
        // scheduled task, no restart.
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var health = new LocalBinaryHealth(() => now, FiveMinutes);
        var pool = new WorkerPool(new[] { Worker("local", isLocal: true) });
        var binaryBroken = true;

        string? RunDrainPreflight()
        {
            var error = health.Check(() => binaryBroken ? "missing libcuda.so.1" : null);
            pool.SetLocalAvailability(error);
            return error;
        }

        Assert.NotNull(RunDrainPreflight());
        await Assert.ThrowsAsync<NoAvailableWorkerException>(() => pool.AcquireAsync(AnyJob, default));

        // The admin fixes the container. Inside the window nothing changes — that is the cache doing its job,
        // and why the retry waits for TimeUntilStale rather than firing straight away.
        binaryBroken = false;
        now = now.Add(health.TimeUntilStale() - TimeSpan.FromSeconds(1));
        Assert.NotNull(RunDrainPreflight());
        await Assert.ThrowsAsync<NoAvailableWorkerException>(() => pool.AcquireAsync(AnyJob, default));

        // Past the window — exactly where ScheduleResumeAfterProbeWindow puts the retry.
        now = now.Add(health.TimeUntilStale() + TimeSpan.FromSeconds(1));
        Assert.Null(RunDrainPreflight());

        var lease = await pool.AcquireAsync(AnyJob, default);
        Assert.Equal("local", lease.Worker.Id);
        pool.Release(lease.Key);
    }

    [Fact]
    public async Task AParkedQueueStaysParkedWhileTheBinaryIsStillBroken()
    {
        // The negative control for the test above: passing the window is not on its own enough.
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var health = new LocalBinaryHealth(() => now, FiveMinutes);
        var pool = new WorkerPool(new[] { Worker("local", isLocal: true) });

        pool.SetLocalAvailability(health.Check(() => "missing libcuda.so.1"));
        now = now.AddMinutes(10);
        pool.SetLocalAvailability(health.Check(() => "missing libcuda.so.1"));

        await Assert.ThrowsAsync<NoAvailableWorkerException>(() => pool.AcquireAsync(AnyJob, default));
        Assert.NotNull(pool.UnavailableReason("local"));
    }

    // ── Setup status: present is not the same as working ─────────────────

    [Theory]
    [InlineData(true, true, null, true)]
    [InlineData(true, true, "missing libcuda.so.1", false)]   // the issue #185 case
    [InlineData(true, false, null, false)]
    [InlineData(false, true, null, false)]
    [InlineData(true, true, "", true)]                        // empty means no error, same as null
    public void IsSetupComplete_RequiresABinaryThatActuallyStarts(
        bool binaryOk, bool modelOk, string? launchError, bool expected)
        => Assert.Equal(expected, WhisperSetupService.IsSetupComplete(binaryOk, modelOk, launchError));

    [Fact]
    public void SetupStatus_LaunchErrorDefaultsToNull_SoAHealthyInstallReportsNothingExtra()
        => Assert.Null(new SetupStatus().BinaryLaunchError);

    [Fact]
    public void DescribeLaunchFailure_MissingCudaLibrary_NamesItAndTheTwoWaysOut()
    {
        // Verbatim shape of the reporter's stderr.
        var message = WhisperSetupService.DescribeLaunchFailure(
            127,
            "/config/plugins/whisper/whisper-cli: error while loading shared libraries: libcuda.so.1: " +
            "cannot open shared object file: No such file or directory");

        Assert.NotNull(message);
        Assert.Contains("libcuda.so.1", message);
        // A CUDA userspace library is bind-mounted by the container runtime, not apt-installed, so the
        // message must say that rather than repeat the generic install hint.
        Assert.Contains("NVIDIA runtime", message);
        Assert.Contains("CPU variant", message);
    }

    [Fact]
    public void DescribeLaunchFailure_OtherMissingLibrary_CarriesTheInstallHint()
    {
        var message = WhisperSetupService.DescribeLaunchFailure(
            127, "whisper-cli: error while loading shared libraries: libgomp.so.1: cannot open shared object file");

        Assert.NotNull(message);
        Assert.Contains("libgomp.so.1", message);
        Assert.Contains(WhisperSetupService.GetInstallHint("libgomp.so.1"), message);
    }

    [Fact]
    public void DescribeLaunchFailure_UnparseableStderr_StillReportsTheFailure()
    {
        var message = WhisperSetupService.DescribeLaunchFailure(127, "");
        Assert.NotNull(message);
        Assert.Contains("a shared library", message);
    }

    [Theory]
    [InlineData(132)]
    [InlineData(134)]
    [InlineData(135)]
    public void DescribeLaunchFailure_FatalSignal_PointsAtTheNoavxBuild(int exitCode)
    {
        var message = WhisperSetupService.DescribeLaunchFailure(exitCode, "");
        Assert.NotNull(message);
        Assert.Contains("noavx", message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    public void DescribeLaunchFailure_AnyOtherExitCode_IsNotALaunchFailure(int exitCode)
    {
        // Some whisper-cli builds exit non-zero on --help. Treating that as broken would park a
        // perfectly good local worker.
        Assert.Null(WhisperSetupService.DescribeLaunchFailure(exitCode, "usage: whisper-cli [options]"));
        Assert.Null(WhisperSetupService.DescribeLaunchFailure(exitCode, null));
    }

    // ── WhisperLaunchException: recognised through the wrapping ──────────

    [Fact]
    public void Find_LocatesTheLaunchFailureThroughSubtitleManagersWrapping()
    {
        // The exact shape the dispatcher sees: GenerateSubtitleAsync reports "all N attempt(s) failed" with
        // the first per-pass error inside, and the forced pass adds its own layer on top of that.
        var launch = new WhisperLaunchException(127, "whisper-cli could not start: missing libcuda.so.1.");
        var forcedAbort = new InvalidOperationException("Aborted forced-subtitle detection for Film.", launch);
        var wrapped = new InvalidOperationException("Subtitle generation failed for \"Film\".", forcedAbort);

        var found = WhisperLaunchException.Find(wrapped);
        Assert.Same(launch, found);
        Assert.Equal(127, found!.ExitCode);
    }

    [Fact]
    public void Find_OnAnOrdinaryFailure_ReturnsNull_SoNormalErrorsStillConsumeRetries()
    {
        var ordinary = new InvalidOperationException("Subtitle file not found at expected location");
        Assert.Null(WhisperLaunchException.Find(ordinary));
        Assert.Null(WhisperLaunchException.Find(null));
    }

    [Fact]
    public void Find_WalksTheChainButIsDepthBounded_SoADiagnosticCannotHangDispatch()
    {
        // Real chains are two or three deep (see the test above). The bound is there so no chain, however
        // it was built, can spin the dispatcher; this pins both halves of that.
        Exception shallow = new WhisperLaunchException(127, "cannot start");
        for (var i = 0; i < 10; i++) shallow = new InvalidOperationException($"layer {i}", shallow);
        Assert.NotNull(WhisperLaunchException.Find(shallow));

        Exception deep = new WhisperLaunchException(127, "cannot start");
        for (var i = 0; i < 40; i++) deep = new InvalidOperationException($"layer {i}", deep);
        Assert.Null(WhisperLaunchException.Find(deep));   // past the bound — it stops looking, it does not spin
    }

    // ── WorkerPool: the broken local worker leaves rotation ──────────────

    private sealed class FakeProvider : ISubtitleProvider
    {
        public string Name => "fake";
        public bool RequiresSpeechAlignmentOptIn => false;
        public Task<string> TranscribeAsync(string audioPath, string language, CancellationToken ct, bool translate = false)
            => Task.FromResult(string.Empty);
        public Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken ct)
            => Task.FromResult(("en", 1f));
    }

    private static ITranscriptionWorker Worker(string id, bool isLocal, double cost = 0)
        => new TranscriptionWorker(id, id, new FakeProvider(), new WorkerCapabilities
        {
            MaxConcurrency = 1,
            CostWeight = cost,
            CanTranslate = true,
            IsLocal = isLocal
        });

    private static readonly JobRequirements AnyJob = new(Translate: false, RequiredModel: null);

    [Fact]
    public async Task LocalOnlyPool_WithABrokenBinary_RefusesToHandOutASlot()
    {
        // The whole point: nothing is dequeued, so no item is charged an attempt.
        var pool = new WorkerPool(new[] { Worker("local", isLocal: true) });
        Assert.True(pool.SetLocalAvailability("missing libcuda.so.1"));

        var ex = await Assert.ThrowsAsync<NoAvailableWorkerException>(
            () => pool.AcquireAsync(AnyJob, default));
        Assert.Contains("libcuda.so.1", ex.Message);

        // The CONFIG is still able to serve the job — only the worker is faulted. The dispatcher's
        // fail-fast-and-drop path keys off HasCapableWorker, so it must stay true or the queue is dropped.
        Assert.True(pool.HasCapableWorker(AnyJob));
        Assert.False(pool.HasAvailableWorker(AnyJob));
    }

    [Fact]
    public async Task AGoodProbe_PutsTheLocalWorkerBackInRotation()
    {
        var pool = new WorkerPool(new[] { Worker("local", isLocal: true) });
        pool.SetLocalAvailability("missing libcuda.so.1");
        await Assert.ThrowsAsync<NoAvailableWorkerException>(() => pool.AcquireAsync(AnyJob, default));

        Assert.True(pool.SetLocalAvailability(null));     // the container was fixed
        var lease = await pool.AcquireAsync(AnyJob, default);
        Assert.Equal("local", lease.Worker.Id);
        Assert.Null(pool.UnavailableReason("local"));
        pool.Release(lease.Key);
    }

    [Fact]
    public async Task WithARemoteWorkerConfigured_ABrokenLocalBinaryRoutesTheWorkToTheRemoteOne()
    {
        // The reporter's remote workers kept working; only the local one was broken.
        var pool = new WorkerPool(new[] { Worker("local", isLocal: true), Worker("remote", isLocal: false, cost: 5) });
        pool.SetLocalAvailability("missing libcuda.so.1");

        var lease = await pool.AcquireAsync(AnyJob, default);
        Assert.Equal("remote", lease.Worker.Id);          // normally local wins on cost — not while parked
        pool.Release(lease.Key);
    }

    [Fact]
    public async Task AJobAlreadyWaitingOnTheLocalWorker_StopsWhenThatWorkerIsParkedMidWait()
    {
        // The binary can break while a job is queued behind the one slot. Without this, the waiter would
        // wake, find nothing pickable, and spin on the 100 ms retry forever.
        var pool = new WorkerPool(new[] { Worker("local", isLocal: true) });
        var held = await pool.AcquireAsync(AnyJob, default);

        var waiter = pool.AcquireAsync(AnyJob, default);
        Assert.False(waiter.IsCompleted);

        pool.SetLocalAvailability("missing libcuda.so.1");
        pool.Release(held.Key);                          // wakes the waiter with the worker parked

        var ex = await Assert.ThrowsAsync<NoAvailableWorkerException>(() => waiter);
        Assert.Contains("libcuda.so.1", ex.Message);
    }

    [Fact]
    public void MarkUnavailable_ReportsOnlyTheFirstTime_SoTheReasonIsLoggedOnce()
    {
        // Hundreds of identical log lines was half of what the reporter saw.
        var pool = new WorkerPool(new[] { Worker("local", isLocal: true) });

        Assert.True(pool.MarkUnavailable("local", "missing libcuda.so.1"));
        Assert.False(pool.MarkUnavailable("local", "missing libcuda.so.1"));
        Assert.False(pool.SetLocalAvailability("missing libcuda.so.1"));   // already parked — no second log
        Assert.Equal("missing libcuda.so.1", pool.UnavailableReason("local"));

        Assert.True(pool.MarkAvailable("local"));
        Assert.False(pool.MarkAvailable("local"));
    }

    [Fact]
    public void MarkUnavailable_ForAnUnknownKey_IsIgnored()
        => Assert.False(new WorkerPool(new[] { Worker("local", isLocal: true) })
            .MarkUnavailable("nobody", "why"));

    [Fact]
    public void SetLocalAvailability_LeavesRemoteWorkersAlone()
    {
        var pool = new WorkerPool(new[] { Worker("local", isLocal: true), Worker("remote", isLocal: false, cost: 5) });
        pool.SetLocalAvailability("missing libcuda.so.1");

        Assert.NotNull(pool.UnavailableReason("local"));
        Assert.Null(pool.UnavailableReason("remote"));
    }

    // ── Queue: a broken worker must not spend the items' retries ─────────

    private static Video NewItem(string name = "Film") => new Video { Id = Guid.NewGuid(), Name = name };

    [Fact]
    public void RequeueWithoutRetry_ReturnsTheItemAtTheSameRetryCount()
    {
        // 315 items × a binary that cannot start would otherwise empty the whole queue in one pass.
        var queue = new SubtitleQueueService();
        var item = NewItem();

        Assert.True(queue.Enqueue(item, "en", PriorityTier.High));
        Assert.True(queue.TryDequeuePriority(out var dispatched));
        Assert.Equal(0, dispatched!.RetryCount);

        queue.RequeueWithoutRetry(dispatched);
        Assert.Equal(1, queue.PriorityCount);

        Assert.True(queue.TryDequeuePriority(out var again));
        Assert.Same(item, again!.Item);
        Assert.Equal(PriorityTier.High, again.Tier);
        Assert.Equal(0, again.RetryCount);                // unchanged — this is the whole point
    }

    [Fact]
    public void RequeueWithoutRetry_NeverGivesUp_EvenPastTheRetryCap()
    {
        // An item that already spent its budget on real failures still must not be dropped because the
        // binary stopped launching: that failure says nothing about the item.
        var queue = new SubtitleQueueService();
        var item = NewItem();

        Assert.True(queue.Enqueue(item, "en", PriorityTier.Low));
        Assert.True(queue.TryDequeuePriority(out var first));
        Assert.True(queue.RetryOrRelease(first!, maxRetries: 3));
        Assert.True(queue.TryDequeuePriority(out var second));
        Assert.Equal(1, second!.RetryCount);

        queue.RequeueWithoutRetry(second);
        Assert.True(queue.TryDequeuePriority(out var third));
        Assert.Equal(1, third!.RetryCount);               // still 1, not 2
        Assert.Equal(PriorityTier.Low, third.Tier);
    }

    [Fact]
    public void RequeueWithoutRetry_PreservesForce_AndKeepsTheIdentityQueuedXorInFlight()
    {
        var queue = new SubtitleQueueService();
        var item = NewItem();

        Assert.True(queue.Enqueue(item, "es", PriorityTier.Critical, force: true));
        Assert.True(queue.TryDequeuePriority(out var dispatched));
        Assert.Equal(0, queue.PriorityCount);             // in-flight, not queued

        queue.RequeueWithoutRetry(dispatched!);
        Assert.Equal(1, queue.PriorityCount);             // queued, not in-flight

        // Still reserved-free, so a fresh request for the same identity merges rather than double-queueing.
        Assert.False(queue.Enqueue(item, "es", PriorityTier.Critical));
        Assert.Equal(1, queue.PriorityCount);

        Assert.True(queue.TryDequeuePriority(out var again));
        Assert.True(again!.Force);
        Assert.Equal("es", again.Language);
    }
}
