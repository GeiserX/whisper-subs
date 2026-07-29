using WhisperSubs.Controller;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Locks the invariant that duration (and therefore the per-call deadline and the segment-timestamp
/// sanity bound) is derived from the SOURCE uncompressed WAV, never from the uploaded body. A compressed
/// upload has identical duration but a fraction of the bytes; deriving duration from it would collapse the
/// deadline and make <c>ConvertTranscriptionResponseToSrt</c> reject every legitimate late segment as
/// "out-of-order" — a hard exception on essentially every remote file (issue #138 follow-up).
/// </summary>
public class SourceAudioDurationTests
{
    // 16 kHz mono s16le PCM = 32,000 bytes per audio-second.
    private const double BytesPerSecond = TranscriptionTimeout.BytesPerAudioSecond;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(32_000, 1)]              // 1 second
    [InlineData(1_920_000, 60)]          // 1 minute
    [InlineData(76_800_000, 2400)]       // the reporter's 40-minute title
    public void DurationIsDerivedFromSourceBytes(long sourceBytes, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, RemoteWhisperProvider.SourceAudioDurationSeconds(sourceBytes), 3);
    }

    [Fact]
    public void NegativeBytesClampToZero()
    {
        // Defensive: a bogus length must never yield a negative duration (which would make the
        // "end > maxDuration" guard reject everything).
        Assert.Equal(0, RemoteWhisperProvider.SourceAudioDurationSeconds(-1));
    }

    [Fact]
    public void CompressedUploadSizeWouldUnderstateDuration_WhichIsWhyWePassSourceBytes()
    {
        // A 40-minute title: 76.8 MB as PCM, ~6.7 MB as 24 kbps Opus (measured). Feeding the COMPRESSED
        // size in would claim the audio is ~3.5 minutes long, and every segment past that would be
        // rejected. This test documents the trap the signature now prevents.
        const long sourcePcmBytes = 76_800_000;
        const long compressedUploadBytes = 6_700_000;

        var correct = RemoteWhisperProvider.SourceAudioDurationSeconds(sourcePcmBytes);
        var wrong = RemoteWhisperProvider.SourceAudioDurationSeconds(compressedUploadBytes);

        Assert.Equal(2400, correct, 3);            // 40 minutes
        Assert.True(wrong < 250);                  // ~3.5 minutes — nonsense for the same audio
        Assert.True(correct > wrong * 9);          // an order of magnitude apart
    }

    [Fact]
    public void DeadlineScalesWithSourceNotUpload()
    {
        // The same guarantee at the deadline layer: a 2h film must get a 2h-sized deadline even when the
        // uploaded body is a tenth the size.
        const long twoHoursPcm = 230_400_000;      // 7200s x 32000
        const long compressed = 20_000_000;        // ~Opus 24k for the same 2h

        var fromSource = TranscriptionTimeout.Compute(twoHoursPcm, 6.0, 60, 12);
        var fromUpload = TranscriptionTimeout.Compute(compressed, 6.0, 60, 12);

        Assert.True(fromSource > fromUpload,
            "deriving the deadline from compressed bytes would guillotine a healthy long transcription");
    }
}
