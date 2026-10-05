using System;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Controller;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// crispasr writes its subtitle only when it finishes, so a Qwen3-ASR title cancelled by playback
/// used to restart from zero every time. The manager now walks such a title in windows and saves each
/// one; these are the pure rules behind that.
/// </summary>
public class Qwen3WindowTests
{
    private static WhisperProvider Whisper()
        => new(NullLogger<WhisperProvider>.Instance, "/nope/whisper.bin", "/nope/whisper-cli", 0, "", "", "", null, 0);

    private static Qwen3Provider Qwen3()
        => new(NullLogger.Instance, "/nope/crispasr", "/nope/qwen3.gguf", 0, "/nope/vad.bin", null, 0, "/nope/cache");

    [Fact]
    public void WindowSecondsFor_OnlyTheLocalQwen3Engine()
    {
        Assert.Equal(600, SubtitleManager.WindowSecondsFor(new EngineSwitchProvider(Whisper(), Qwen3)));
        Assert.Equal(0, SubtitleManager.WindowSecondsFor(new EngineSwitchProvider(Whisper(), () => null)));   // Whisper selected
        Assert.Equal(0, SubtitleManager.WindowSecondsFor(Whisper()));
        Assert.Equal(0, SubtitleManager.WindowSecondsFor(new RemoteWhisperProvider(NullLogger.Instance, "http://w:1", "m")));
        Assert.Equal(0, SubtitleManager.WindowSecondsFor(null));
    }

    [Theory]
    [InlineData(0, 600, 7174, false)]      // first window of a two-hour film
    [InlineData(6000, 600, 7174, false)]   // 6600 < 7144
    [InlineData(6600, 600, 7174, true)]    // 7200 covers the end
    [InlineData(6545, 600, 7174, true)]    // 7145 >= 7144: within the 30 s the completeness check allows
    [InlineData(0, 600, 500, true)]        // shorter than one window
    [InlineData(0, 0, 7174, true)]         // no windowing
    [InlineData(0, 600, 0, true)]          // unknown length
    public void WindowReachesEnd_Rule(double start, double window, double duration, bool expected)
    {
        Assert.Equal(expected, SubtitleManager.WindowReachesEnd(start, window, duration));
    }

    private const string ThreeCues =
        "1\n00:00:05,000 --> 00:00:07,000\nFirst line.\n\n2\n00:04:10,500 --> 00:04:12,000\nSecond line.\n\n3\n00:09:58,250 --> 00:10:00,000\nCut off mid";

    [Fact]
    public void SplitOffLastCue_ReturnsHeadStartAndCount()
    {
        var (head, lastStart, count) = SubtitleManager.SplitOffLastCue(ThreeCues);
        Assert.Equal(3, count);
        Assert.Equal(598.25, lastStart, 3);
        Assert.DoesNotContain("Cut off mid", head);
        Assert.Contains("Second line.", head);
        Assert.Equal(2, WhisperProvider.CountSrtEntries(head));
    }

    [Fact]
    public void SplitOffLastCue_FewerThanTwoCues_SplitsNothing()
    {
        const string one = "1\n00:00:05,000 --> 00:00:07,000\nOnly line.";
        var (head, lastStart, count) = SubtitleManager.SplitOffLastCue(one);
        Assert.Equal(one, head);
        Assert.Equal(-1, lastStart);
        Assert.Equal(1, count);

        var empty = SubtitleManager.SplitOffLastCue("");
        Assert.Equal(("", -1d, 0), empty);
        Assert.Equal(0, SubtitleManager.SplitOffLastCue(null).CueCount);
    }

    [Fact]
    public void SplitOffLastCue_HandlesWindowsLineEndings()
    {
        var (head, lastStart, count) = SubtitleManager.SplitOffLastCue(ThreeCues.Replace("\n", "\r\n"));
        Assert.Equal(3, count);
        Assert.Equal(598.25, lastStart, 3);
        Assert.DoesNotContain("Cut off mid", head);
    }

    // Two or more cues: hold the last one back and restart just before it, at a cue boundary.
    [Fact]
    public void PlanNextWindow_ContinuesJustBeforeTheHeldBackCue()
    {
        var (next, drop) = SubtitleManager.PlanNextWindow(1200, 600, 3, 598.25);
        Assert.True(drop);
        Assert.Equal(1200 + 598.25 - 0.2, next, 3);
    }

    // Silence, one cue, or a last cue at the very start: keep everything and move a full window on,
    // so a run can never stall on the same stretch.
    [Theory]
    [InlineData(0, -1.0)]
    [InlineData(1, -1.0)]
    [InlineData(2, 0.5)]
    [InlineData(2, 1.2)]
    public void PlanNextWindow_AlwaysAdvances(int cueCount, double lastCueStart)
    {
        var (next, drop) = SubtitleManager.PlanNextWindow(1200, 600, cueCount, lastCueStart);
        Assert.False(drop);
        Assert.Equal(1800, next);
    }

    [Fact]
    public void PlanNextWindow_NeverGoesBackwards()
    {
        for (var last = -1.0; last < 600; last += 37.3)
        {
            var (next, _) = SubtitleManager.PlanNextWindow(1200, 600, 5, last);
            Assert.True(next > 1201, $"last={last} next={next}");
        }
    }

    // crispasr exits 0 with no file on music or silence; that is an empty window, not a failure.
    [Fact]
    public void DescribeMissingOutput_NoSpeechGetsItsOwnMarker()
    {
        Assert.Equal(Qwen3Provider.NoSpeechMarker,
            Qwen3Provider.DescribeMissingOutput("crispasr: warning: no speech detected in '/tmp/a.wav'"));
        Assert.Contains("error: model",
            Qwen3Provider.DescribeMissingOutput("crispasr: error: model file is corrupt") ?? "");
        Assert.Null(Qwen3Provider.DescribeMissingOutput("some unrelated line"));
        Assert.Null(Qwen3Provider.DescribeMissingOutput(null));
    }

    // The continuation math end to end on the SRT helpers the manager uses: the head of window one plus
    // the offset cues of window two keep absolute times and running indices.
    [Fact]
    public void WindowAppend_KeepsAbsoluteTimesAndIndices()
    {
        var (head, lastStart, count) = SubtitleManager.SplitOffLastCue(ThreeCues);
        var (next, drop) = SubtitleManager.PlanNextWindow(0, 600, count, lastStart);
        Assert.True(drop);
        const string windowTwo = "1\n00:00:00,200 --> 00:00:03,000\nCut off mid sentence, now whole.";
        var appended = head.TrimEnd() + "\n\n" + WhisperProvider.OffsetSrt(windowTwo, next, WhisperProvider.CountSrtEntries(head) + 1);
        Assert.Contains("3\n00:09:58,250 --> 00:10:01,050\nCut off mid sentence, now whole.", appended.Replace("\r\n", "\n"));
        Assert.Equal(3, WhisperProvider.CountSrtEntries(appended));
    }
}
