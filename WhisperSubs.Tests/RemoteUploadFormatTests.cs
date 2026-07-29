using WhisperSubs.Controller.Workers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Upload-format policy for remote workers (issue #138). The default MUST stay wav: whisper.cpp's
/// whisper-server decodes WAV only and this project's own worker image ships without ffmpeg, so a
/// compressed default would break every self-hosted worker.
/// </summary>
public class RemoteUploadFormatTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mp3")]        // unsupported
    [InlineData("nonsense")]
    public void UnknownOrMissingCodecFallsBackToWav(string? codec)
    {
        Assert.Equal(RemoteUploadFormat.Wav, RemoteUploadFormat.Normalize(codec));
        Assert.False(RemoteUploadFormat.RequiresReencode(codec));
    }

    [Theory]
    [InlineData("flac", "flac")]
    [InlineData("FLAC", "flac")]
    [InlineData(" Opus ", "opus")]
    public void SupportedCodecsNormalize(string configured, string expected)
    {
        Assert.Equal(expected, RemoteUploadFormat.Normalize(configured));
        Assert.True(RemoteUploadFormat.RequiresReencode(configured));
    }

    [Theory]
    [InlineData("wav", "audio.wav", "audio/wav")]
    [InlineData("flac", "audio.flac", "audio/flac")]
    [InlineData("opus", "audio.ogg", "audio/ogg")]
    public void FileNameAndContentTypeMatchTheBytes(string codec, string fileName, string contentType)
    {
        // Providers sniff by extension; a mismatched name is rejected or mis-decoded.
        Assert.Equal(fileName, RemoteUploadFormat.FileName(codec));
        Assert.Equal(contentType, RemoteUploadFormat.ContentType(codec));
    }

    [Fact]
    public void FlacArgumentsPinSixteenBitSamples()
    {
        // THE footgun: without -sample_fmt s16 ffmpeg's FLAC encoder defaults to 24-bit and the output is
        // LARGER than the 16-bit PCM input (measured: 19,713,918 vs 19,200,102 bytes). The vendor-published
        // command omits this flag, so this assertion is the guard against copying it verbatim.
        var args = RemoteUploadFormat.BuildFfmpegArguments("/tmp/in.wav", "/tmp/out.flac", "flac");
        Assert.Contains("-sample_fmt s16", args, System.StringComparison.Ordinal);
        Assert.Contains("-c:a flac", args, System.StringComparison.Ordinal);
        Assert.Contains("\"/tmp/in.wav\"", args, System.StringComparison.Ordinal);
        Assert.Contains("\"/tmp/out.flac\"", args, System.StringComparison.Ordinal);
    }

    [Fact]
    public void OpusArgumentsUseLibopusAtTheConfiguredBitrate()
    {
        var args = RemoteUploadFormat.BuildFfmpegArguments("/tmp/in.wav", "/tmp/out.ogg", "opus");
        Assert.Contains("-c:a libopus", args, System.StringComparison.Ordinal);
        Assert.Contains($"-b:a {RemoteUploadFormat.OpusBitrateKbps}k", args, System.StringComparison.Ordinal);
    }

    [Fact]
    public void BuildingArgumentsForWavIsRejected()
    {
        // wav means "no re-encode"; asking for its arguments is a caller bug.
        Assert.Throws<System.ArgumentException>(
            () => RemoteUploadFormat.BuildFfmpegArguments("/tmp/in.wav", "/tmp/out.wav", "wav"));
    }

    [Fact]
    public void EstimatesReflectMeasuredRatios()
    {
        // A 40-minute title: 2400s x 32000 = 76.8 MB of PCM.
        const long fortyMinutes = 76_800_000;

        Assert.Equal(fortyMinutes, RemoteUploadFormat.EstimateUploadBytes(fortyMinutes, "wav"));

        var flac = RemoteUploadFormat.EstimateUploadBytes(fortyMinutes, "flac");
        var opus = RemoteUploadFormat.EstimateUploadBytes(fortyMinutes, "opus");

        // FLAC roughly halves it - still over a 25 MB cap, which is why it alone did not fix the report.
        Assert.InRange(flac, 38_000_000, 42_000_000);
        Assert.True(flac > 25L * 1024 * 1024);

        // Opus fits comfortably under 25 MB - this is what makes the reported 40-minute title work.
        Assert.InRange(opus, 5_000_000, 8_000_000);
        Assert.True(opus < 25L * 1024 * 1024);
    }
}
