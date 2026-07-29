using WhisperSubs.Controller.Workers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Pre-flight upload gate (issue #138): refuse an upload we already know will be refused, and say why in
/// terms the admin can act on. The default cap of 0 must be byte-identical to the old behaviour.
/// </summary>
public class UploadPreflightTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ZeroOrNegativeCapMeansUnlimited(long cap)
    {
        // Existing installs (and every self-hosted worker) must never be blocked.
        Assert.True(UploadPreflight.IsAllowed(long.MaxValue / 2, cap));
        Assert.Equal(string.Empty, UploadPreflight.ExplainIfBlocked(76_800_000, 76_800_000, cap, "wav"));
    }

    [Fact]
    public void UploadAtExactlyTheCapIsAllowed()
    {
        Assert.True(UploadPreflight.IsAllowed(25_000_000, 25_000_000));
    }

    [Fact]
    public void UploadOverTheCapIsBlocked()
    {
        Assert.False(UploadPreflight.IsAllowed(25_000_001, 25_000_000));
    }

    [Fact]
    public void BlockedMessageStatesSizeCapAndDuration()
    {
        // The reporter's exact case: a 40-minute title against Groq's 25 MB.
        const long fortyMinutesPcm = 76_800_000;
        var message = UploadPreflight.ExplainIfBlocked(
            fortyMinutesPcm, fortyMinutesPcm, 25_000_000, "wav");

        Assert.Contains("40-minute", message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MB", message, System.StringComparison.Ordinal);
        Assert.Contains("Nothing was uploaded", message, System.StringComparison.OrdinalIgnoreCase);
        // It must tell them what to actually change.
        Assert.Contains("FLAC", message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Opus", message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdviceIsTailoredToTheCodecAlreadyInUse()
    {
        const long twoHoursPcm = 230_400_000;

        // Already on FLAC: suggest Opus, do not suggest FLAC again.
        var flacAdvice = UploadPreflight.ExplainIfBlocked(twoHoursPcm, 115_000_000, 25_000_000, "flac");
        Assert.Contains("Opus", flacAdvice, System.StringComparison.OrdinalIgnoreCase);

        // Already on Opus: nothing smaller to offer, so point at a self-hosted worker honestly.
        var opusAdvice = UploadPreflight.ExplainIfBlocked(twoHoursPcm, 20_000_000, 5_000_000, "opus");
        Assert.Contains("self-hosted", opusAdvice, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("smallest upload format", opusAdvice, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatBytesIsInvariantAndReadable()
    {
        // Decimal units, matching the README, the config page and how providers quote their caps.
        Assert.Equal("25.0 MB", RemoteErrorGuidance.FormatBytes(25_000_000));
        Assert.Equal("1.0 GB", RemoteErrorGuidance.FormatBytes(1_000_000_000));
    }

    [Theory]
    [InlineData(413, "Max upload size")]
    [InlineData(401, "API key")]
    [InlineData(404, "BASE url")]
    [InlineData(429, "Rate limited")]
    [InlineData(503, "own side")]
    public void GuidanceIsActionablePerStatus(int status, string expected)
    {
        var guidance = RemoteErrorGuidance.For((System.Net.HttpStatusCode)status);
        Assert.Contains(expected, guidance, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnmappedStatusAddsNoInventedAdvice()
    {
        Assert.Equal(string.Empty, RemoteErrorGuidance.For(System.Net.HttpStatusCode.OK));
    }
}
