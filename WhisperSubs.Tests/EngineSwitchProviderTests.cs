using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Which engine each call lands on. Whisper is a real provider pointed at missing files, so a call
/// that reaches it fails with Whisper's own "model not found"; Qwen3-ASR likewise with its own.
/// </summary>
public class EngineSwitchProviderTests
{
    private static WhisperProvider Whisper()
        => new(NullLogger<WhisperProvider>.Instance, "/nope/whisper.bin", "/nope/whisper-cli", 0, "", "", "", null, 0);

    private static Qwen3Provider Qwen3()
        => new(NullLogger.Instance, "/nope/crispasr", "/nope/qwen3.gguf", 0, "/nope/vad.bin", null, 0, "/nope/cache");

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, "", true)]
    [InlineData(true, null, false)]
    [InlineData(false, "en", false)]
    [InlineData(true, "nl", false)]
    public void IsTranscription_OnlyAPlainTranscribe(bool translate, string? target, bool expected)
    {
        Assert.Equal(expected, EngineSwitchProvider.IsTranscription(translate, target));
    }

    [Fact]
    public void Current_FollowsTheLiveSelection()
    {
        Qwen3Provider? selected = null;
        var provider = new EngineSwitchProvider(Whisper(), () => selected);
        Assert.Equal("Whisper", provider.Name);
        Assert.IsType<WhisperProvider>(provider.Current);

        selected = Qwen3();
        Assert.Equal("Qwen3-ASR", provider.Name);
        Assert.True(provider.RequiresSpeechAlignmentOptIn);
        Assert.IsType<Qwen3Provider>(provider.Current);
    }

    [Fact]
    public async Task Transcribe_WithQwen3Selected_GoesToQwen3()
    {
        var provider = new EngineSwitchProvider(Whisper(), Qwen3);
        var ex = await Assert.ThrowsAsync<System.IO.FileNotFoundException>(
            () => provider.TranscribeAsync("/nope/a.wav", "en", CancellationToken.None));
        Assert.Contains("crispasr", ex.Message);
    }

    [Fact]
    public async Task Transcribe_WithoutQwen3_StaysOnWhisper()
    {
        var provider = new EngineSwitchProvider(Whisper(), () => null);
        var ex = await Assert.ThrowsAsync<System.IO.FileNotFoundException>(
            () => provider.TranscribeAsync("/nope/a.wav", "en", CancellationToken.None));
        Assert.Contains("Whisper model", ex.Message);
    }

    [Fact]
    public async Task Translate_WithQwen3Selected_StillGoesToWhisper()
    {
        var provider = new EngineSwitchProvider(Whisper(), Qwen3);
        var ex = await Assert.ThrowsAsync<System.IO.FileNotFoundException>(
            () => provider.TranscribeAsync("/nope/a.wav", "fr", CancellationToken.None, translate: true));
        Assert.Contains("Whisper model", ex.Message);

        var en = await Assert.ThrowsAsync<System.IO.FileNotFoundException>(
            () => provider.TranscribeAsync("/nope/a.wav", "fr", CancellationToken.None, translate: true, targetLanguage: "en"));
        Assert.Contains("Whisper model", en.Message);
    }

    [Fact]
    public async Task NonEnglishTarget_IsRefusedByWhisper_NotSentToQwen3()
    {
        var provider = new EngineSwitchProvider(Whisper(), Qwen3);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.TranscribeAsync("/nope/a.wav", "en", CancellationToken.None, translate: true, targetLanguage: "nl"));
    }

    [Fact]
    public async Task ForcedChunk_WithQwen3Selected_GoesToQwen3WithoutVad()
    {
        // No VAD model at all: the applyVad:false path must not demand one.
        var qwen3 = new Qwen3Provider(NullLogger.Instance, "/nope/crispasr", "/nope/qwen3.gguf", 0, "", null, 0, "/nope/cache");
        var provider = new EngineSwitchProvider(Whisper(), () => qwen3);
        var ex = await Assert.ThrowsAsync<System.IO.FileNotFoundException>(
            () => provider.TranscribeAsync("/nope/chunk.wav", "fr", CancellationToken.None, translate: false, applyVad: false));
        Assert.Contains("crispasr", ex.Message);
    }

    // "auto" never reaches crispasr: the Whisper detection model names the language first (here it
    // fails on its missing files, which is how we know the detection ran before Qwen3-ASR).
    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    public async Task AutoLanguage_WithQwen3Selected_DetectsOnWhisperFirst(string language)
    {
        var provider = new EngineSwitchProvider(Whisper(), Qwen3);
        var ex = await Assert.ThrowsAsync<System.IO.FileNotFoundException>(
            () => provider.TranscribeAsync("/nope/a.wav", language, CancellationToken.None));
        Assert.Contains("Whisper model", ex.Message);
    }

    // A language outside the model's 30 stays on Whisper, which knows it, instead of failing or
    // coming back in the wrong language.
    [Theory]
    [InlineData("sw")]
    [InlineData("cy")]
    [InlineData("und")]
    public async Task UnsupportedLanguage_WithQwen3Selected_StaysOnWhisper(string language)
    {
        var provider = new EngineSwitchProvider(Whisper(), Qwen3);
        var ex = await Assert.ThrowsAsync<System.IO.FileNotFoundException>(
            () => provider.TranscribeAsync("/nope/a.wav", language, CancellationToken.None));
        Assert.Contains("Whisper model", ex.Message);
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("yue", true)]
    [InlineData("fil", true)]
    [InlineData("mk", true)]
    [InlineData("sw", false)]
    [InlineData("auto", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Supports_IsTheModelCardList(string? code, bool expected)
    {
        Assert.Equal(expected, WhisperSubs.Setup.Qwen3Catalog.Supports(code));
        Assert.Equal(30, WhisperSubs.Setup.Qwen3Catalog.Languages.Count);
    }

    // The alignment rule follows the engine that ran: Whisper here has no VAD model, so it says
    // false, while Qwen3-ASR always says true.
    [Fact]
    public async Task RequiresSpeechAlignmentOptIn_FollowsTheEngineThatRan()
    {
        var provider = new EngineSwitchProvider(Whisper(), Qwen3);
        Assert.True(provider.RequiresSpeechAlignmentOptIn);          // before any job: Qwen3-ASR is current

        await Assert.ThrowsAnyAsync<Exception>(() => provider.TranscribeAsync("/nope/a.wav", "sw", CancellationToken.None));
        Assert.False(provider.RequiresSpeechAlignmentOptIn);         // Swahili ran on Whisper
        Assert.IsType<WhisperProvider>(provider.LastUsed);

        await Assert.ThrowsAnyAsync<Exception>(() => provider.TranscribeAsync("/nope/a.wav", "en", CancellationToken.None));
        Assert.True(provider.RequiresSpeechAlignmentOptIn);          // English ran on Qwen3-ASR
        Assert.IsType<Qwen3Provider>(provider.LastUsed);

        await Assert.ThrowsAnyAsync<Exception>(() => provider.TranscribeAsync("/nope/a.wav", "fr", CancellationToken.None, translate: true));
        Assert.False(provider.RequiresSpeechAlignmentOptIn);         // a translation ran on Whisper
    }

    // The detection paths must see the Whisper provider through the wrapper, or forced-subtitle
    // chunk detection loses its batching.
    [Fact]
    public void AsWhisper_SeesThroughTheWrapper()
    {
        var whisper = Whisper();
        Assert.Same(whisper, WhisperSubs.Controller.SubtitleManager.AsWhisper(whisper));
        Assert.Same(whisper, WhisperSubs.Controller.SubtitleManager.AsWhisper(new EngineSwitchProvider(whisper, Qwen3)));
        Assert.Null(WhisperSubs.Controller.SubtitleManager.AsWhisper(Qwen3()));
        Assert.Null(WhisperSubs.Controller.SubtitleManager.AsWhisper(null));
    }

    [Fact]
    public async Task DetectLanguage_AlwaysGoesToWhisper()
    {
        var provider = new EngineSwitchProvider(Whisper(), Qwen3);
        var ex = await Assert.ThrowsAsync<System.IO.FileNotFoundException>(
            () => provider.DetectLanguageAsync("/nope/a.wav", CancellationToken.None));
        Assert.Contains("Whisper model", ex.Message);
    }
}
