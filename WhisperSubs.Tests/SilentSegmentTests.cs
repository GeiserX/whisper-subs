using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// A worker that listened and heard no speech answers with an empty segment list. On one server the
/// forced pass sends short foreign-language stretches to a Qwen3-ASR server; 99 of them in one night came
/// back as <c>{"text":"","segments":[]}</c> and every one was logged as a failed segment. That answer is a
/// correct result: no cue, no error. A worker that did not answer properly must still fail.
/// </summary>
public class SilentSegmentTests
{
    // The exact body a CrispASR Qwen3-ASR server returns for a 3 s near-silent clip (measured 2026-10-08).
    private const string CrispAsrSilence = """{"task":"transcribe","language":"es","duration":3.000,"text":"","segments":[]}""";

    [Theory]
    [InlineData("crispasr-qwen3", 42, CrispAsrSilence)]
    [InlineData("openai", 0, """{"text":"","segments":[]}""")]
    [InlineData("openai", 0, """{"text":" ","segments":[{"start":0,"end":1.5,"text":"  "}]}""")]
    public async Task NoSpeechAnswer_IsAnEmptyTranscript(string dialect, int wordCueMaxChars, string body)
    {
        var wav = SilentWav();
        try
        {
            var provider = Provider(dialect, wordCueMaxChars, HttpStatusCode.OK, body);

            var srt = await provider.TranscribeAsync(wav, "en", CancellationToken.None);

            Assert.Equal(string.Empty, srt);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task ServerError_IsStillAFailure()
    {
        var wav = SilentWav();
        try
        {
            var provider = Provider("crispasr-qwen3", 42, HttpStatusCode.InternalServerError, "upstream is down");

            await Assert.ThrowsAnyAsync<HttpRequestException>(() => provider.TranscribeAsync(wav, "en", CancellationToken.None));
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Theory]
    [InlineData("")]                                   // a 200 with no body at all says nothing about the audio
    [InlineData("""{"text":"untimed"}""")]               // untimed JSON cannot be synchronised
    [InlineData("""{"segments":[{"start":2,"end":1,"text":"x"}]}""")]   // broken timestamps
    [InlineData("""{"text":"hola","segments":[]}""")]                // text with no timing is not silence
    [InlineData("""{"text":"","segments":[{"start":2,"end":1,"text":""}]}""")]   // unreadable timing is not silence either
    [InlineData("""{"text":"","segments":[{"text":""}]}""")]
    public async Task AnswerThatIsNotATranscript_IsStillAFailure(string body)
    {
        var wav = SilentWav();
        try
        {
            var provider = Provider("openai", 0, HttpStatusCode.OK, body);

            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranscribeAsync(wav, "en", CancellationToken.None));
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task EmptyAnswerForACanaryTarget_IsStillAFailure()
    {
        // A CrispASR server answers a target it cannot serve with no cues; that stays an error.
        var wav = SilentWav();
        try
        {
            var provider = Provider("crispasr", 0, HttpStatusCode.OK, """{"text":"","segments":[]}""");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.TranscribeAsync(wav, "en", CancellationToken.None, translate: true, targetLanguage: "nl"));
        }
        finally
        {
            File.Delete(wav);
        }
    }

    private static RemoteWhisperProvider Provider(string dialect, int wordCueMaxChars, HttpStatusCode status, string body)
        => new(NullLogger.Instance, "http://worker:9020", "qwen3",
            httpClient: new HttpClient(new FixedAnswerHandler(status, body)), dialect: dialect, wordCueMaxChars: wordCueMaxChars);

    // One second of 16 kHz mono 16-bit silence: the provider reads its length for the deadline.
    private static string SilentWav()
    {
        var path = Path.Combine(Path.GetTempPath(), $"silent_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, Controller.Workers.SyntheticAudio.SilentWav16kMono());
        return path;
    }

    private sealed class FixedAnswerHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}

public class ForcedPassEndTests
{
    [Theory]
    [InlineData(true, 0, "Subtitle")]
    [InlineData(true, 3, "Subtitle")]
    // Every foreign segment answered "no speech": the title has no foreign dialogue, so it is not retried every night.
    [InlineData(false, 0, "NoForeignSpeech")]
    // A segment failed and nothing was transcribed: a failure, so the next run tries again.
    [InlineData(false, 1, "Failed")]
    public void EndOfForcedPass(bool hasCues, int failedSegments, string expected)
        => Assert.Equal(expected, WhisperSubs.Controller.SubtitleManager.EndOfForcedPass(hasCues, failedSegments).ToString());
}
