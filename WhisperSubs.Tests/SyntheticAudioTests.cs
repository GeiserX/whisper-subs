using System;
using System.Text;
using WhisperSubs.Controller.Workers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// v4.0 worker "Test connection": the tiny silent WAV must be a byte-correct 16 kHz mono s16le file so any
/// OpenAI-compatible endpoint accepts it.
/// </summary>
public class SyntheticAudioTests
{
    [Fact]
    public void SilentWav_HasCanonicalHeader_AndCorrectLength()
    {
        var wav = SyntheticAudio.SilentWav16kMono(100);   // 100 ms @ 16 kHz mono 16-bit = 1600 samples
        Assert.Equal(44 + 3200, wav.Length);              // 44-byte header + 1600*2 data
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));   // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));   // mono
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));  // 16-bit
        Assert.Equal(3200, BitConverter.ToInt32(wav, 40));       // data chunk size
        Assert.Equal(36 + 3200, BitConverter.ToInt32(wav, 4));   // RIFF chunk size
    }

    [Theory]
    [InlineData(0, 10)]        // clamped up to 10 ms
    [InlineData(5000, 2000)]   // clamped down to 2000 ms
    public void SilentWav_ClampsDuration(int requestedMs, int effectiveMs)
    {
        var wav = SyntheticAudio.SilentWav16kMono(requestedMs);
        Assert.Equal(44 + 16000 * effectiveMs / 1000 * 2, wav.Length);
    }

    [Fact]
    public void SilentWav_DataIsAllSilence()
    {
        var wav = SyntheticAudio.SilentWav16kMono(50);
        for (var i = 44; i < wav.Length; i++) Assert.Equal(0, wav[i]);
    }
}
