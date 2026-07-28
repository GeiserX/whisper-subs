using System.Net;
using WhisperSubs.Controller.Workers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// v4.1.1: the worker "Test connection" probe distinguishes a genuinely-unreachable endpoint from a
/// reachable-but-slow one. These cover every branch of the pure classifier that drives the {ok, warning, message}
/// the config page renders as red / yellow / green — the fix for the false-negative where a slow GPU (large-v3 on
/// an Intel iGPU) blew the transcribe timeout on the silent probe clip and a reachable worker was reported as
/// unreachable.
/// </summary>
public class WorkerProbeTests
{
    [Theory]
    [InlineData("169.254.169.254", true)]
    [InlineData("::ffff:169.254.169.254", true)]
    [InlineData("fe80::1", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("::1", false)]
    public void LinkLocalGuard_HandlesMappedIpv4AndLegitimateWorkerAddresses(
        string address, bool expected)
    {
        Assert.Equal(expected, WorkerProbe.IsBlockedLinkLocal(IPAddress.Parse(address)));
    }

    [Fact]
    public void NotReachable_IsHardFailure()
    {
        var (ok, warning, message) = WorkerProbe.ClassifyProbeOutcome(reachable: false, httpStatus: null, timedOut: false);
        Assert.False(ok);
        Assert.False(warning);
        Assert.Equal("Unreachable", message);   // controller appends ": <transport reason>"
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(299)]
    public void Reachable_2xx_IsVerifiedSuccess(int status)
    {
        var (ok, warning, message) = WorkerProbe.ClassifyProbeOutcome(reachable: true, httpStatus: status, timedOut: false);
        Assert.True(ok);
        Assert.False(warning);
        Assert.Equal("Reachable, auth OK, transcription verified", message);   // controller appends " (<latency> ms)."
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void Reachable_AuthFailure_IsHardFailure(int status)
    {
        var (ok, warning, message) = WorkerProbe.ClassifyProbeOutcome(reachable: true, httpStatus: status, timedOut: false);
        Assert.False(ok);
        Assert.False(warning);
        Assert.Equal("Reachable, but authentication failed — check the API key.", message);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void Reachable_OtherStatus_ReportsTheStatus(int status)
    {
        var (ok, warning, message) = WorkerProbe.ClassifyProbeOutcome(reachable: true, httpStatus: status, timedOut: false);
        Assert.False(ok);
        Assert.False(warning);
        Assert.Equal("Reachable, but the transcription endpoint returned HTTP " + status + ".", message);
    }

    [Fact]
    public void Reachable_ButTranscribeTimedOut_IsWarningNotFailure()
    {
        // The crux of the fix: reachable + slow transcribe must be OK-with-warning (yellow), never a red failure.
        var (ok, warning, message) = WorkerProbe.ClassifyProbeOutcome(reachable: true, httpStatus: null, timedOut: true);
        Assert.True(ok);
        Assert.True(warning);
        Assert.Equal(WorkerProbe.TranscribeTimeoutMessage, message);
        Assert.Contains("didn't finish within 30s", message);
        Assert.Contains("duration-scaled deadline", message);
    }

    [Fact]
    public void Timeout_TakesPrecedence_OverStatus_WhenReachable()
    {
        // Defensive precedence: a long-running decode that also carried a status is still a usable worker (warning).
        var (ok, warning, _) = WorkerProbe.ClassifyProbeOutcome(reachable: true, httpStatus: 200, timedOut: true);
        Assert.True(ok);
        Assert.True(warning);
    }

    [Fact]
    public void NotReachable_TakesPrecedence_OverEverything()
    {
        // Unreachable dominates even if a stale status/timeout is also passed.
        var (ok, warning, message) = WorkerProbe.ClassifyProbeOutcome(reachable: false, httpStatus: 200, timedOut: true);
        Assert.False(ok);
        Assert.False(warning);
        Assert.Equal("Unreachable", message);
    }

    [Fact]
    public void Reachable_NoStatusNoTimeout_IsTotalAndSafe()
    {
        // Not produced by the orchestration, but the classifier is total over its inputs rather than throwing.
        var (ok, warning, message) = WorkerProbe.ClassifyProbeOutcome(reachable: true, httpStatus: null, timedOut: false);
        Assert.False(ok);
        Assert.False(warning);
        Assert.Contains("no HTTP status", message);
    }
}
