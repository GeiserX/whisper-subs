using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using WhisperSubs.Controller;
using WhisperSubs.ScheduledTasks;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// "Pause generation during playback" used to kill the running engine and retry, which threw away
/// every pass that cannot resume. It now stops the process and continues it.
/// </summary>
public class EngineProcessSuspenderTests
{
    private static (EngineProcessSuspender Suspender, List<(int Pid, int Signal)> Sent) Fake((int, int)? signals = null)
    {
        var sent = new List<(int, int)>();
        var suspender = new EngineProcessSuspender((pid, sig) => { sent.Add((pid, sig)); return 0; }, signals ?? (19, 18));
        return (suspender, sent);
    }

    [Fact]
    public void SuspendAndResume_SignalEveryProcessOfTheJob()
    {
        var (s, sent) = Fake();
        using var job = s.BeginScope();
        using var a = s.Track(101);
        using var b = s.Track(202);
        Assert.Empty(sent);

        job.Suspend();
        Assert.True(job.IsSuspended);
        Assert.Equal(new[] { (101, 19), (202, 19) }, sent.ToArray());

        sent.Clear();
        job.Resume();
        Assert.False(job.IsSuspended);
        Assert.Equal(new[] { (101, 18), (202, 18) }, sent.ToArray());
    }

    [Fact]
    public void SuspendAndResume_AreIdempotent()
    {
        var (s, sent) = Fake();
        using var job = s.BeginScope();
        using var a = s.Track(101);
        job.Suspend(); job.Suspend();
        job.Resume(); job.Resume();
        Assert.Equal(new[] { (101, 19), (101, 18) }, sent.ToArray());
    }

    // A job between two phases starts its next process during the pause: it must not run.
    [Fact]
    public void ProcessStartedWhileSuspended_IsStoppedAtOnce()
    {
        var (s, sent) = Fake();
        using var job = s.BeginScope();
        job.Suspend();
        using var late = s.Track(303);
        Assert.Equal(new[] { (303, 19) }, sent.ToArray());
        sent.Clear();
        job.Resume();
        Assert.Equal(new[] { (303, 18) }, sent.ToArray());
    }

    [Fact]
    public void UntrackedProcess_IsLeftAlone()
    {
        var (s, sent) = Fake();
        using var job = s.BeginScope();
        var handle = s.Track(101);
        handle.Dispose();
        handle.Dispose();   // twice is fine
        job.Suspend();
        job.Resume();
        Assert.Empty(sent);
    }

    // A job on a remote worker opens no scope: the FFmpeg it runs on this server is not tracked and
    // nothing can stop it.
    [Fact]
    public void WithoutAScope_NothingIsTracked()
    {
        var (s, sent) = Fake();
        Assert.Null(s.Current);
        using var handle = s.Track(101);
        handle.Dispose();
        Assert.Empty(sent);
    }

    // Two jobs on this server: one job's pause never touches the other's processes.
    [Fact]
    public async System.Threading.Tasks.Task Scopes_AreIndependentAcrossJobs()
    {
        var (s, sent) = Fake();
        EngineProcessSuspender.Scope? scopeA = null, scopeB = null;
        var aTracked = new System.Threading.Tasks.TaskCompletionSource();
        var release = new System.Threading.Tasks.TaskCompletionSource();

        async System.Threading.Tasks.Task JobA()
        {
            using var scope = s.BeginScope();
            scopeA = scope;
            await System.Threading.Tasks.Task.Yield();
            using var t = s.Track(111);            // lands in A's scope although awaited
            aTracked.SetResult();
            await release.Task;
        }
        async System.Threading.Tasks.Task JobB()
        {
            using var scope = s.BeginScope();
            scopeB = scope;
            await aTracked.Task;
            using var t = s.Track(222);
            scope.Suspend();
            Assert.Equal(new[] { (222, 19) }, sent.ToArray());   // only B's process
            Assert.False(scopeA!.IsSuspended);
            scope.Resume();
            release.SetResult();
        }
        await System.Threading.Tasks.Task.WhenAll(JobA(), JobB());
        Assert.NotSame(scopeA, scopeB);
        Assert.Null(s.Current);                     // neither job's scope leaks into the caller
    }

    [Fact]
    public void DisposingAScope_ContinuesWhatWasStopped()
    {
        var (s, sent) = Fake();
        var job = s.BeginScope();
        using var a = s.Track(101);
        job.Suspend();
        sent.Clear();
        job.Dispose();
        Assert.Equal(new[] { (101, 18) }, sent.ToArray());
        Assert.Null(s.Current);
        using var after = s.Track(404);             // the closed scope takes nothing more
        job.Suspend();
        Assert.Equal(new[] { (101, 18) }, sent.ToArray());
    }

    [Fact]
    public void UnsupportedPlatform_NeverSuspends()
    {
        var sent = new List<(int, int)>();
        var s = new EngineProcessSuspender((pid, sig) => { sent.Add((pid, sig)); return 0; }, signals: null);
        Assert.False(s.Supported);
        using var job = s.BeginScope();
        using var a = s.Track(101);
        job.Suspend();
        Assert.False(job.IsSuspended);
        job.Resume();
        Assert.Empty(sent);
    }

    [Fact]
    public void KillThatThrows_DoesNotBreakThePause()
    {
        var s = new EngineProcessSuspender((_, _) => throw new InvalidOperationException("gone"), (19, 18));
        using var job = s.BeginScope();
        using var a = s.Track(101);
        job.Suspend();
        Assert.True(job.IsSuspended);
        job.Resume();
        Assert.False(job.IsSuspended);
    }

    [Fact]
    public void PlatformSignals_MatchThisOs()
    {
        var signals = EngineProcessSuspender.PlatformSignals();
        if (OperatingSystem.IsLinux()) Assert.Equal((19, 18), signals);
        else Assert.Null(signals);   // macOS included: a stopped child hangs .NET's child reaping there
    }

    // The real thing, on Linux: a stopped process reports state T and does not exit; continued, it
    // runs on and finishes, and the runtime keeps reaping children (a second process started during
    // the stop still exits and is seen). Without this the fake-signal tests above could pass while
    // nothing ever stops, or while the runtime hangs as it does on macOS.
    [Fact]
    public void RealProcess_StopsAndContinues()
    {
        var suspender = EngineProcessSuspender.Default;
        if (!suspender.Supported) return;

        using var job = suspender.BeginScope();
        using var process = Process.Start(new ProcessStartInfo("sleep", "2") { UseShellExecute = false })!;
        using var tracked = suspender.Track(process.Id);
        try
        {
            job.Suspend();
            Thread.Sleep(300);
            Assert.StartsWith("T", StateOf(process.Id));           // stopped
            Assert.False(process.WaitForExit(2500));                // a stopped "sleep 2" outlives its 2 seconds

            // Child reaping still works while one child is stopped.
            using (var other = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false })!)
            {
                Assert.True(other.WaitForExit(5000));
            }

            job.Resume();
            Assert.True(process.WaitForExit(5000));                 // continued, it finishes
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            job.Resume();
            try { if (!process.HasExited) process.Kill(); } catch { }
        }
    }

    private static string StateOf(int pid)
    {
        using var ps = Process.Start(new ProcessStartInfo("ps", $"-o state= -p {pid}") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = ps.StandardOutput.ReadToEnd().Trim();
        ps.WaitForExit();
        return output;
    }

    [Fact]
    public void PausableDeadline_DoesNotRunWhilePaused()
    {
        var paused = true;
        using var cts = new CancellationTokenSource();
        using var deadline = new PausableDeadline(TimeSpan.FromSeconds(10), cts, () => paused);
        Assert.False(deadline.Advance(TimeSpan.FromSeconds(60)));    // an hour of pause costs nothing
        paused = false;
        Assert.False(deadline.Advance(TimeSpan.FromSeconds(9)));
        Assert.True(deadline.Advance(TimeSpan.FromSeconds(2)));      // 11 s of running time: due
    }

    [Fact]
    public void PausableDeadline_FiresOnTheTarget_AndRestartResetsIt()
    {
        using var cts = new CancellationTokenSource();
        using var deadline = new PausableDeadline(TimeSpan.FromMilliseconds(400), cts, () => false);
        deadline.Restart();
        Assert.True(cts.Token.WaitHandle.WaitOne(5000));             // fires

        using var cts2 = new CancellationTokenSource();
        using var held = new PausableDeadline(TimeSpan.FromMilliseconds(400), cts2, () => true);
        held.Restart();
        Assert.False(cts2.Token.WaitHandle.WaitOne(1200));           // paused: never fires
    }

    [Theory]
    [InlineData(true, false, 0, SubtitleGenerationTask.PauseAction.Suspend)]
    [InlineData(false, false, 0, SubtitleGenerationTask.PauseAction.None)]
    [InlineData(true, true, 600, SubtitleGenerationTask.PauseAction.None)]
    [InlineData(false, true, 600, SubtitleGenerationTask.PauseAction.Resume)]
    [InlineData(true, true, 4 * 3600, SubtitleGenerationTask.PauseAction.Resume)]    // the budget ran out while stopped
    [InlineData(true, false, 4 * 3600, SubtitleGenerationTask.PauseAction.None)]     // and the job is never stopped again
    [InlineData(true, false, 4 * 3600 - 1, SubtitleGenerationTask.PauseAction.Suspend)]
    public void DecidePause_Rules(bool playing, bool suspended, int heldSeconds, SubtitleGenerationTask.PauseAction expected)
    {
        Assert.Equal(expected, SubtitleGenerationTask.DecidePause(playing, suspended, TimeSpan.FromSeconds(heldSeconds)));
    }

    // One budget for the producer's wait, the job's own wait and its suspension: three hours waited
    // leave one hour of suspension, not four more.
    [Fact]
    public void PlaybackHold_IsOneBudgetAcrossWaitAndSuspension()
    {
        var hold = new SubtitleGenerationTask.PlaybackHold();
        Assert.False(hold.Spent);
        hold.Add(TimeSpan.FromHours(3));                               // waited before the job started
        Assert.Equal(SubtitleGenerationTask.PauseAction.Suspend, SubtitleGenerationTask.DecidePause(true, false, hold.Held));
        hold.Add(TimeSpan.FromMinutes(59));
        Assert.Equal(SubtitleGenerationTask.PauseAction.None, SubtitleGenerationTask.DecidePause(true, true, hold.Held));
        hold.Add(TimeSpan.FromMinutes(1));
        Assert.True(hold.Spent);
        Assert.Equal(SubtitleGenerationTask.PauseAction.Resume, SubtitleGenerationTask.DecidePause(true, true, hold.Held));
        Assert.Equal(SubtitleGenerationTask.PauseAction.None, SubtitleGenerationTask.DecidePause(true, false, hold.Held));
    }

    [Theory]
    [InlineData(true, false, true)]    // single server: the producer waits, as before
    [InlineData(true, true, false)]    // a remote worker to keep busy: it does not
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void GateProducerOnPlayback_OnlyWithoutRemoteWorkers(bool pauseOnPlayback, bool hasRemote, bool expected)
    {
        Assert.Equal(expected, SubtitleGenerationTask.GateProducerOnPlayback(pauseOnPlayback, hasRemote));
    }
}
