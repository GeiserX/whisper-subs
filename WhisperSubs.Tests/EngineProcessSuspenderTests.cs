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
    public void SuspendAndResume_SignalEveryTrackedProcess()
    {
        var (s, sent) = Fake();
        using var a = s.Track(101);
        using var b = s.Track(202);
        Assert.Empty(sent);

        s.Suspend();
        Assert.True(s.IsSuspended);
        Assert.Equal(new[] { (101, 19), (202, 19) }, sent.ToArray());

        sent.Clear();
        s.Resume();
        Assert.False(s.IsSuspended);
        Assert.Equal(new[] { (101, 18), (202, 18) }, sent.ToArray());
    }

    [Fact]
    public void SuspendAndResume_AreIdempotent()
    {
        var (s, sent) = Fake();
        using var a = s.Track(101);
        s.Suspend(); s.Suspend();
        s.Resume(); s.Resume();
        Assert.Equal(new[] { (101, 19), (101, 18) }, sent.ToArray());
    }

    // A job between two phases starts its next process during the pause: it must not run.
    [Fact]
    public void ProcessStartedWhileSuspended_IsStoppedAtOnce()
    {
        var (s, sent) = Fake();
        s.Suspend();
        using var late = s.Track(303);
        Assert.Equal(new[] { (303, 19) }, sent.ToArray());
        sent.Clear();
        s.Resume();
        Assert.Equal(new[] { (303, 18) }, sent.ToArray());
    }

    [Fact]
    public void UntrackedProcess_IsLeftAlone()
    {
        var (s, sent) = Fake();
        var handle = s.Track(101);
        handle.Dispose();
        handle.Dispose();   // twice is fine
        s.Suspend();
        s.Resume();
        Assert.Empty(sent);
    }

    [Fact]
    public void UnsupportedPlatform_NeverSuspends()
    {
        var sent = new List<(int, int)>();
        var s = new EngineProcessSuspender((pid, sig) => { sent.Add((pid, sig)); return 0; }, signals: null);
        Assert.False(s.Supported);
        using var a = s.Track(101);
        s.Suspend();
        Assert.False(s.IsSuspended);
        s.Resume();
        Assert.Empty(sent);
    }

    [Fact]
    public void KillThatThrows_DoesNotBreakThePause()
    {
        var s = new EngineProcessSuspender((_, _) => throw new InvalidOperationException("gone"), (19, 18));
        using var a = s.Track(101);
        s.Suspend();
        Assert.True(s.IsSuspended);
        s.Resume();
        Assert.False(s.IsSuspended);
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

        using var process = Process.Start(new ProcessStartInfo("sleep", "2") { UseShellExecute = false })!;
        using var tracked = suspender.Track(process.Id);
        try
        {
            suspender.Suspend();
            Thread.Sleep(300);
            Assert.StartsWith("T", StateOf(process.Id));           // stopped
            Assert.False(process.WaitForExit(2500));                // a stopped "sleep 2" outlives its 2 seconds

            // Child reaping still works while one child is stopped.
            using (var other = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false })!)
            {
                Assert.True(other.WaitForExit(5000));
            }

            suspender.Resume();
            Assert.True(process.WaitForExit(5000));                 // continued, it finishes
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            suspender.Resume();
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
    [InlineData(true, false, 0, false, SubtitleGenerationTask.PauseAction.Suspend)]
    [InlineData(false, false, 0, false, SubtitleGenerationTask.PauseAction.None)]
    [InlineData(true, true, 600, false, SubtitleGenerationTask.PauseAction.None)]
    [InlineData(false, true, 600, false, SubtitleGenerationTask.PauseAction.Resume)]
    [InlineData(true, true, 4 * 3600, false, SubtitleGenerationTask.PauseAction.Resume)]   // the 4-hour guard
    [InlineData(true, false, 0, true, SubtitleGenerationTask.PauseAction.None)]            // guard spent: do not stop this job again
    public void DecidePause_Rules(bool playing, bool suspended, int heldSeconds, bool guardSpent, SubtitleGenerationTask.PauseAction expected)
    {
        Assert.Equal(expected, SubtitleGenerationTask.DecidePause(playing, suspended, TimeSpan.FromSeconds(heldSeconds), guardSpent));
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
