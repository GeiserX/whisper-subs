using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// v4.0 resilience: the per-call deadline policy that closes the "70-hour stuck task" class — an
/// unreachable endpoint used to block for the fixed 30-minute HTTP timeout on every one of hundreds of
/// per-chunk calls. The deadline scales with the audio length and is clamped both ways.
/// </summary>
public class TranscriptionTimeoutTests
{
    // The plugin extracts 16 kHz mono s16le WAV = 32000 bytes per audio-second.
    private static long Bytes(double audioSeconds) => (long)(audioSeconds * 32000);

    [Fact]
    public void Compute_ScalesWithAudioLength()
    {
        // 10 min audio × factor 6 = 3600s, inside [60s, 12h].
        Assert.Equal(3600, TranscriptionTimeout.Compute(Bytes(600), 6.0, 60, 12).TotalSeconds, 0);
    }

    [Fact]
    public void Compute_ClampsToFloor_ForTinyAudio()
    {
        // A ~5s detection chunk × 6 = 30s → floored to the 60s minimum.
        Assert.Equal(60, TranscriptionTimeout.Compute(Bytes(5), 6.0, 60, 12).TotalSeconds, 0);
    }

    [Fact]
    public void Compute_ClampsToCap_ForVeryLongAudio()
    {
        // A 2h15m film × 6 = 48600s → capped at 12h = 43200s (bounded, but never guillotines a real run).
        Assert.Equal(43200, TranscriptionTimeout.Compute(Bytes(8100), 6.0, 60, 12).TotalSeconds, 0);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-100000L)]
    public void Compute_NonPositiveBytes_FallToFloor(long bytes)
    {
        Assert.Equal(60, TranscriptionTimeout.Compute(bytes, 6.0, 60, 12).TotalSeconds, 0);
    }

    [Fact]
    public void Compute_NonPositiveSettings_UseSafeDefaults()
    {
        // factor<=0 → 6, min<=0 → 60, maxHours<=0 → 12.
        Assert.Equal(3600, TranscriptionTimeout.Compute(Bytes(600), 0, 0, 0).TotalSeconds, 0);
        Assert.Equal(60, TranscriptionTimeout.Compute(Bytes(1), -1, -1, -1).TotalSeconds, 0);
    }

    [Fact]
    public void Compute_NeverExceedsCancelAfterCeiling()
    {
        // An absurd maxHours must not yield a deadline above CancellationTokenSource.CancelAfter's
        // int.MaxValue-ms limit (~596h) — that would throw on every remote call. Huge audio × huge factor
        // × a 1,000,000-hour cap still clamps to the safe ceiling.
        var d = TranscriptionTimeout.Compute(long.MaxValue / 2, 1e9, 60, 1_000_000);
        Assert.True(d.TotalMilliseconds <= int.MaxValue, $"deadline {d} exceeds the CancelAfter ceiling");
    }

    [Fact]
    public void Compute_HonorsCustomFactorAndBounds()
    {
        // factor 2, floor 30s, cap 1h.
        Assert.Equal(200, TranscriptionTimeout.Compute(Bytes(100), 2.0, 30, 1).TotalSeconds, 0);   // 100s × 2
        Assert.Equal(1200, TranscriptionTimeout.Compute(Bytes(600), 2.0, 30, 1).TotalSeconds, 0);  // 10 min × 2
        Assert.Equal(3600, TranscriptionTimeout.Compute(Bytes(2400), 2.0, 30, 1).TotalSeconds, 0); // 40 min × 2 → 1h cap
    }
}
