using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

public class RemoteTranscriptionResponseTests
{
    private sealed record RecordedRequest(
        string Path,
        IReadOnlyDictionary<string, string> Fields,
        string? Authorization);

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                    if (string.IsNullOrEmpty(name) || string.Equals(name, "file", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    fields[name] = await part.ReadAsStringAsync(cancellationToken);
                }
            }

            Requests.Add(new RecordedRequest(
                request.RequestUri!.AbsolutePath,
                fields,
                request.Headers.Authorization?.ToString()));
            return _responses.Count > 0
                ? _responses.Dequeue()
                : throw new InvalidOperationException("No fake response queued");
        }
    }

    private static string CreateOneSecondWav()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[32044]);
        return path;
    }

    [Fact]
    public async Task Transcription_NegotiatesAndCachesTimestampedJson()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"response_format srt is unsupported"}""")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"segments":[{"start":0,"end":1,"text":"first"}]}""",
                    Encoding.UTF8,
                    "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"segments":[{"start":0,"end":1,"text":"second"}]}""",
                    Encoding.UTF8,
                    "application/json")
            });
        using var client = new HttpClient(handler);
        var provider = new RemoteWhisperProvider(
            NullLogger<RemoteWhisperProvider>.Instance,
            "https://worker.example",
            "whisper-large-v3",
            "secret",
            httpClient: client);
        var wav = CreateOneSecondWav();
        try
        {
            Assert.Contains("first", await provider.TranscribeAsync(wav, "es", CancellationToken.None));
            Assert.Contains("second", await provider.TranscribeAsync(wav, "es", CancellationToken.None));
        }
        finally
        {
            File.Delete(wav);
        }

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("srt", handler.Requests[0].Fields["response_format"]);
        Assert.Equal("verbose_json", handler.Requests[1].Fields["response_format"]);
        // timestamp_granularities[] is deliberately NOT sent: segment is already the default for
        // verbose_json, and OpenRouter 400s on it unless the model routes to an OpenAI-compatible
        // backend (issue #138).
        Assert.DoesNotContain("timestamp_granularities[]", handler.Requests[1].Fields.Keys);
        Assert.Equal("verbose_json", handler.Requests[2].Fields["response_format"]);
        Assert.Equal("es", handler.Requests[0].Fields["language"]);
        Assert.Equal("whisper-large-v3", handler.Requests[0].Fields["model"]);
        Assert.Equal("Bearer secret", handler.Requests[0].Authorization);
        Assert.All(handler.Requests, request =>
            Assert.Equal("/v1/audio/transcriptions", request.Path));
    }

    [Fact]
    public async Task Translation_UsesTranslationsPathAndOmitsSourceLanguage()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("1\n00:00:00,000 --> 00:00:01,000\nHello\n")
            });
        using var client = new HttpClient(handler);
        var provider = new RemoteWhisperProvider(
            NullLogger<RemoteWhisperProvider>.Instance,
            "https://worker.example",
            "whisper-1",
            httpClient: client);
        var wav = CreateOneSecondWav();
        try
        {
            await provider.TranscribeAsync(wav, "es", CancellationToken.None, translate: true);
        }
        finally
        {
            File.Delete(wav);
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/v1/audio/translations", request.Path);
        Assert.Equal("srt", request.Fields["response_format"]);
        Assert.False(request.Fields.ContainsKey("language"));
    }

    [Fact]
    public async Task TranscriptionAndTranslation_CacheFormatsIndependentlyOnSameProvider()
    {
        var timedJson = """{"segments":[{"start":0,"end":1,"text":"timed"}]}""";
        var srt = "1\n00:00:00,000 --> 00:00:01,000\ntranslated\n";
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"response_format srt unsupported"}""")
            },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(timedJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(srt) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(timedJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(srt) });
        using var client = new HttpClient(handler);
        var provider = new RemoteWhisperProvider(
            NullLogger<RemoteWhisperProvider>.Instance,
            "https://worker.example",
            "whisper-1",
            httpClient: client);
        var wav = CreateOneSecondWav();
        try
        {
            await provider.TranscribeAsync(wav, "es", CancellationToken.None);
            await provider.TranscribeAsync(wav, "es", CancellationToken.None, translate: true);
            await provider.TranscribeAsync(wav, "es", CancellationToken.None);
            await provider.TranscribeAsync(wav, "es", CancellationToken.None, translate: true);
        }
        finally
        {
            File.Delete(wav);
        }

        Assert.Equal(
            ["srt", "verbose_json", "srt", "verbose_json", "srt"],
            handler.Requests.Select(request => request.Fields["response_format"]));
        Assert.Equal(
            [
                "/v1/audio/transcriptions",
                "/v1/audio/transcriptions",
                "/v1/audio/translations",
                "/v1/audio/transcriptions",
                "/v1/audio/translations"
            ],
            handler.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task FailedConversion_DoesNotPoisonFormatCache()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>bad</html>") },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>bad again</html>") });
        using var client = new HttpClient(handler);
        var provider = new RemoteWhisperProvider(
            NullLogger<RemoteWhisperProvider>.Instance,
            "https://worker.example",
            "whisper-1",
            httpClient: client);
        var wav = CreateOneSecondWav();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.TranscribeAsync(wav, "auto", CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.TranscribeAsync(wav, "auto", CancellationToken.None));
        }
        finally
        {
            File.Delete(wav);
        }

        Assert.Equal(
            ["srt", "srt"],
            handler.Requests.Select(request => request.Fields["response_format"]));
    }

    [Fact]
    public async Task NonFormatBadRequest_RetriesFormatOnceThenSurfacesTheProvidersOwnError()
    {
        // Two deliberate behavior changes (issue #138):
        //  1. While a worker's format is still un-negotiated, ANY format-candidate 4xx earns ONE alternate
        //     format retry — OpenRouter rejects response_format=srt with a bare 400 that names no parameter,
        //     and without this its endpoint never works at all.
        //  2. The provider's own explanation is now surfaced (sanitized) instead of discarded, so an admin
        //     can see WHY without enabling debug logging.
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"model does not exist"}""")
            },
            // The one bounded retry also fails: a bad model name is not a format problem.
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"model does not exist"}""")
            });
        using var client = new HttpClient(handler);
        var provider = new RemoteWhisperProvider(
            NullLogger<RemoteWhisperProvider>.Instance,
            "https://worker.example",
            "missing-model",
            httpClient: client);
        var wav = CreateOneSecondWav();
        try
        {
            var exception = await Assert.ThrowsAnyAsync<HttpRequestException>(
                () => provider.TranscribeAsync(wav, "auto", CancellationToken.None));
            Assert.Contains("HTTP 400", exception.Message);
            // The upstream explanation is now visible — this is the whole point of the fix.
            Assert.Contains("model does not exist", exception.Message);
        }
        finally
        {
            File.Delete(wav);
        }

        // BOUNDED: exactly one retry, never a retry storm. A 77 MB upload must not be repeated
        // indefinitely because a model name is wrong.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(["srt", "verbose_json"],
            handler.Requests.Select(request => request.Fields["response_format"]));
    }

    [Fact]
    public async Task UntimedJsonSuccess_RetriesTimestampedJsonOnce()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"untimed"}""")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"segments":[{"start":0,"end":1,"text":"timed"}]}""")
            });
        using var client = new HttpClient(handler);
        var provider = new RemoteWhisperProvider(
            NullLogger<RemoteWhisperProvider>.Instance,
            "https://worker.example",
            "whisper-1",
            httpClient: client);
        var wav = CreateOneSecondWav();
        try
        {
            Assert.Contains("timed", await provider.TranscribeAsync(wav, "auto", CancellationToken.None));
        }
        finally
        {
            File.Delete(wav);
        }

        Assert.Equal(["srt", "verbose_json"],
            handler.Requests.Select(request => request.Fields["response_format"]));
    }

    [Fact]
    public async Task CachedVerboseJson_RecoversFromLaterUntimedCoercion()
    {
        var timedJson = """{"segments":[{"start":0,"end":1,"text":"timed"}]}""";
        var srt = "1\n00:00:00,000 --> 00:00:01,000\nrecovered\n";
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"response_format srt unsupported"}""")
            },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(timedJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":"untimed"}""") },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(srt) });
        using var client = new HttpClient(handler);
        var provider = new RemoteWhisperProvider(
            NullLogger<RemoteWhisperProvider>.Instance,
            "https://worker.example",
            "whisper-1",
            httpClient: client);
        var wav = CreateOneSecondWav();
        try
        {
            await provider.TranscribeAsync(wav, "auto", CancellationToken.None);
            Assert.Contains(
                "recovered",
                await provider.TranscribeAsync(wav, "auto", CancellationToken.None));
        }
        finally
        {
            File.Delete(wav);
        }

        Assert.Equal(
            ["srt", "verbose_json", "verbose_json", "srt"],
            handler.Requests.Select(request => request.Fields["response_format"]));
    }

    [Fact]
    public async Task RejectionRetryReturningUntimedJson_DoesNotFlipBackAgain()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"response_format srt unsupported"}""")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"still untimed"}""")
            });
        using var client = new HttpClient(handler);
        var provider = new RemoteWhisperProvider(
            NullLogger<RemoteWhisperProvider>.Instance,
            "https://worker.example",
            "whisper-1",
            httpClient: client);
        var wav = CreateOneSecondWav();
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.TranscribeAsync(wav, "auto", CancellationToken.None));
            Assert.Contains("without timestamped segments", exception.Message);
        }
        finally
        {
            File.Delete(wav);
        }

        Assert.Equal(
            ["srt", "verbose_json"],
            handler.Requests.Select(request => request.Fields["response_format"]));
    }

    [Fact]
    public void VerboseJsonSegments_AreConvertedToSrt()
    {
        const string json =
            """
            {
              "text": "Hello world",
              "segments": [
                { "start": 0.125, "end": 2.5, "text": " Hello" },
                { "start": 65.0, "end": 67.3456, "text": "world\nagain " }
              ]
            }
            """;

        var result = RemoteWhisperProvider.ConvertTranscriptionResponseToSrt(json);

        Assert.Equal(
            "1\n00:00:00,125 --> 00:00:02,500\nHello\n\n" +
            "2\n00:01:05,000 --> 00:01:07,346\nworld\nagain",
            result);
    }

    [Fact]
    public void NumericStringTimestamps_AreAcceptedForProviderCompatibility()
    {
        const string json =
            """
            {
              "segments": [
                { "start": "65.001", "end": "66.25", "text": "Provider string timestamps" }
              ]
            }
            """;

        var result = RemoteWhisperProvider.ConvertTranscriptionResponseToSrt(json);

        Assert.Contains("00:01:05,001 --> 00:01:06,250", result);
    }

    [Fact]
    public void CueText_RemovesBlankLinesAndUnsafeControlCharacters()
    {
        const string json =
            """
            {
              "segments": [
                { "start": 0, "end": 2, "text": " First\r\n\r\n Second\u0001 " }
              ]
            }
            """;

        var result = RemoteWhisperProvider.ConvertTranscriptionResponseToSrt(json);

        Assert.EndsWith("First\nSecond", result);
    }

    [Fact]
    public void RawSrtFromLegacyProvider_IsValidatedAndNormalized()
    {
        const string srt = "1\r\n00:00:00,000 --> 00:00:01,000\r\nHello\r\n";

        Assert.Equal(
            "1\n00:00:00,000 --> 00:00:01,000\nHello",
            RemoteWhisperProvider.ConvertTranscriptionResponseToSrt(srt));
    }

    [Theory]
    [InlineData("plain transcript")]
    [InlineData("<html>upstream error</html>")]
    [InlineData("\"json string\"")]
    [InlineData("1\n00:99:00,000 --> 01:00:00,000\nbad")]
    [InlineData("1\n00:00:02,000 --> 00:00:01,000\nbackwards")]
    [InlineData("garbage\n\n1\n00:00:00,000 --> 00:00:01,000\nlooks valid")]
    [InlineData("1\n00:00:00,000 --> 00:00:01,000\nlooks valid\n\ntrailing garbage")]
    public void NonJsonResponseMustContainAValidSrtTimingCue(string response)
    {
        Assert.Throws<InvalidOperationException>(
            () => RemoteWhisperProvider.ConvertTranscriptionResponseToSrt(response));
    }

    [Fact]
    public void RawSrtMustFitUploadedAudioDuration()
    {
        const string srt = "1\n00:00:00,000 --> 00:00:20,000\nToo long\n";

        Assert.Throws<InvalidOperationException>(
            () => RemoteWhisperProvider.ConvertTranscriptionResponseToSrt(
                srt, maxDurationSeconds: 10));
    }

    [Fact]
    public void Utf8Bom_IsAcceptedForJsonAndSrt()
    {
        var json = "\uFEFF{\"segments\":[{\"start\":0,\"end\":1,\"text\":\"x\"}]}";
        var srt = "\uFEFF1\n00:00:00,000 --> 00:00:01,000\nx\n";

        Assert.Contains("00:00:00,000 --> 00:00:01,000",
            RemoteWhisperProvider.ConvertTranscriptionResponseToSrt(json));
        Assert.StartsWith("1\n", RemoteWhisperProvider.ConvertTranscriptionResponseToSrt(srt));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t")]
    public void EmptyResponse_IsReturnedForCallerValidation(string response)
    {
        Assert.Same(response, RemoteWhisperProvider.ConvertTranscriptionResponseToSrt(response));
    }

    [Fact]
    public void PlainJsonWithoutSegments_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RemoteWhisperProvider.ConvertTranscriptionResponseToSrt("""{"text":"No timestamps"}"""));

        Assert.Contains("without timestamped segments", ex.Message);
        Assert.Contains("plain json text cannot be synchronized", ex.Message);
    }

    [Theory]
    [InlineData("""{"segments":[]}""", true)]
    [InlineData("""﻿{"segments":[]}""", true)]
    [InlineData("""{"segments":[{"start":2,"end":1,"text":"bad"}]}""", false)]
    [InlineData("""{"text":"untimed"}""", false)]
    [InlineData("""not json""", false)]
    public void ProbeShape_RequiresTimestampedSegmentsArray(string response, bool expected)
    {
        Assert.Equal(expected, RemoteWhisperProvider.HasTimestampedSegmentsArray(response));
    }

    [Theory]
    [InlineData("""{"segments":[]}""", true)]
    [InlineData("""{"segments":[{"start":0,"end":1,"text":"x"}]}""", false)]
    [InlineData("""{"text":"untimed"}""", false)]
    public void EmptyProbeShape_IsDetectedForWarning(string response, bool expected)
    {
        Assert.Equal(expected, RemoteWhisperProvider.HasEmptyTimestampedSegmentsArray(response));
    }

    [Theory]
    [InlineData("""{"text":"untimed"}""", true)]
    [InlineData("""{"text":"timed","segments":[]}""", false)]
    [InlineData("""not json""", false)]
    public void UntimedJsonDetection_IsNarrow(string response, bool expected)
    {
        Assert.Equal(expected, RemoteWhisperProvider.IsUntimedJsonResponse(response));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "response_format srt unsupported", true)]
    [InlineData(HttpStatusCode.BadRequest, "model does not exist", false)]
    [InlineData(HttpStatusCode.OK, """{"text":"untimed"}""", true)]
    [InlineData(HttpStatusCode.OK, """{"segments":[]}""", false)]
    [InlineData(HttpStatusCode.InternalServerError, "response_format", false)]
    public void ProbeNegotiation_MatchesRuntimePolicy(
        HttpStatusCode statusCode, string body, bool expected)
    {
        Assert.Equal(
            expected,
            RemoteWhisperProvider.ShouldRetryProbeAsVerbose(statusCode, body));
    }

    [Theory]
    [InlineData("""{"segments":[{"start":-1,"end":2,"text":"x"}]}""")]
    [InlineData("""{"segments":[{"start":2,"end":2,"text":"x"}]}""")]
    [InlineData("""{"segments":[{"start":3,"end":2,"text":"x"}]}""")]
    [InlineData("""{"segments":[{"start":0,"end":"not-a-number","text":"x"}]}""")]
    [InlineData("""{"segments":[{"end":2,"text":"x"}]}""")]
    [InlineData("""{"segments":[{"start":false,"end":2,"text":"x"}]}""")]
    [InlineData("""{"segments":[{"start":0,"end":2}]}""")]
    [InlineData("""{"segments":[{"start":0,"end":2,"text":false}]}""")]
    [InlineData("""{"segments":[1]}""")]
    [InlineData("""{"segments":[{"start":2,"end":3,"text":"a"},{"start":1,"end":2,"text":"b"}]}""")]
    [InlineData("""{"segments":[{"start":0,"end":0.0004,"text":"x"}]}""")]
    [InlineData("""{"segments":[{"start":0,"end":1e307,"text":"x"}]}""")]
    public void InvalidSegments_AreRejectedInsteadOfProducingPartialSubtitles(string json)
    {
        Assert.Throws<InvalidOperationException>(
            () => RemoteWhisperProvider.ConvertTranscriptionResponseToSrt(json));
    }

    [Fact]
    public void SegmentMustFitUploadedAudioDuration()
    {
        const string json = """{"segments":[{"start":0,"end":20,"text":"x"}]}""";

        Assert.Throws<InvalidOperationException>(
            () => RemoteWhisperProvider.ConvertTranscriptionResponseToSrt(
                json, maxDurationSeconds: 10));
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"segments":[]}""")]
    [InlineData("""{"segments":[{"start":0,"end":1,"text":"  "}]}""")]
    public void ResponseWithoutUsableSegments_IsRejected(string json)
    {
        Assert.Throws<InvalidOperationException>(
            () => RemoteWhisperProvider.ConvertTranscriptionResponseToSrt(json));
    }

    [Fact]
    public void MalformedJson_IsRejectedWithAStableError()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RemoteWhisperProvider.ConvertTranscriptionResponseToSrt("{not-json"));

        Assert.Contains("malformed JSON", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.NotAcceptable, true)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, true)]
    [InlineData(HttpStatusCode.UnprocessableEntity, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(null, false)]
    public void ResponseFormatFallback_IsLimitedToFormatRelatedStatuses(
        HttpStatusCode? statusCode, bool expected)
    {
        Assert.Equal(expected, RemoteWhisperProvider.IsFormatRejectionCandidateStatus(statusCode));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "response_format must be json", true)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "SRT is not supported", true)]
    [InlineData(HttpStatusCode.NotAcceptable, "not acceptable", true)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, "unsupported", false)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, "response_format is unsupported", true)]
    [InlineData(HttpStatusCode.BadRequest, "model does not exist", false)]
    [InlineData(HttpStatusCode.Unauthorized, "response_format is invalid", false)]
    public void RuntimeFallback_RequiresAnExplicitFormatError(
        HttpStatusCode statusCode, string message, bool expected)
    {
        var exception = new HttpRequestException(message, null, statusCode);

        Assert.Equal(expected, RemoteWhisperProvider.IsResponseFormatRejection(exception));
    }

    [Theory]
    [InlineData("srt", "verbose_json")]
    [InlineData("verbose_json", "srt")]
    [InlineData("unknown", "srt")]
    public void AlternateResponseFormat_NegotiatesBetweenTimedFormats(string current, string expected)
    {
        Assert.Equal(expected, RemoteWhisperProvider.AlternateResponseFormat(current));
    }

    [Fact]
    public async Task BoundedReader_AcceptsUtf8WithinLimit()
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("áudio"));

        var result = await RemoteWhisperProvider.ReadUtf8BoundedAsync(
            content, maxBytes: 16, CancellationToken.None);

        Assert.Equal("áudio", result);
    }

    [Fact]
    public async Task BoundedReader_RejectsDeclaredOrStreamedOversizeBodies()
    {
        using var declared = new ByteArrayContent(new byte[10]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RemoteWhisperProvider.ReadUtf8BoundedAsync(declared, 4, CancellationToken.None));

        using var streamed = new StreamContent(new MemoryStream(new byte[10]));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RemoteWhisperProvider.ReadUtf8BoundedAsync(streamed, 4, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => RemoteWhisperProvider.ReadUtf8BoundedAsync(streamed, 0, CancellationToken.None));
    }

    [Fact]
    public void RemoteProviderTiming_IsOptInLikeVadOutput()
    {
        var provider = new RemoteWhisperProvider(
            NullLogger<RemoteWhisperProvider>.Instance,
            "https://example.invalid",
            "whisper-1");

        Assert.True(provider.RequiresSpeechAlignmentOptIn);
        Assert.False(Controller.SubtitleManager.ShouldAlignToSpeech(
            alignEnabled: true, requiresOptIn: provider.RequiresSpeechAlignmentOptIn, alignWithVad: false));
        Assert.True(Controller.SubtitleManager.ShouldAlignToSpeech(
            alignEnabled: true, requiresOptIn: provider.RequiresSpeechAlignmentOptIn, alignWithVad: true));
    }

    [Fact]
    public async Task RequestNeverSendsTimestampGranularities()
    {
        // OpenRouter accepts timestamp_granularities[] only when the requested model happens to route to an
        // OpenAI-compatible backend, and returns 400 otherwise — which made every OpenRouter transcription
        // fail (issue #138). The field is redundant for us anyway: verbose_json returns segment timestamps
        // by default and we only ever read "segments", never "words".
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"srt is not supported"}""")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"segments":[{"start":0.0,"end":1.0,"text":"hola"}]}""")
            });
        using var client = new HttpClient(handler);
        var provider = new RemoteWhisperProvider(
            NullLogger<RemoteWhisperProvider>.Instance,
            "https://worker.example",
            "openai/whisper-large-v3",
            httpClient: client);

        var wav = CreateOneSecondWav();
        try
        {
            await provider.TranscribeAsync(wav, "auto", CancellationToken.None);
        }
        finally
        {
            File.Delete(wav);
        }

        // Both the initial srt attempt and the negotiated verbose_json retry must be free of it.
        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, r =>
            Assert.DoesNotContain("timestamp_granularities[]", r.Fields.Keys));
        Assert.Contains("verbose_json", handler.Requests.Select(r => r.Fields["response_format"]));
    }
}
