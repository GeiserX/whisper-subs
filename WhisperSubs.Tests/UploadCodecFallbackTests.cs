using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Regression locks for the 4.5.0.1 fixes.
/// <para>
/// The headline bug: when a FLAC/Opus re-encode falls back to the source WAV (ffmpeg missing, encode
/// failed, or the output was not smaller), the upload was still labelled <c>audio.flac</c>/<c>audio/flac</c>.
/// Providers sniff the format from the extension, so that sends RIFF/WAVE bytes under an OGG name and gets
/// rejected or mis-decoded — while the log says "uploading the original WAV instead", i.e. exactly the
/// undiagnosable state issue #138 existed to remove.
/// </para>
/// </summary>
public class UploadCodecFallbackTests
{
    [Fact]
    public async Task WavWorkerReportsWavAsTheEffectiveCodec()
    {
        var wav = Path.GetTempFileName();
        try
        {
            var (path, isTemporary, effectiveCodec) = await RemoteAudioEncoder.PrepareUploadAsync(
                wav, "wav", NullLogger.Instance, CancellationToken.None);

            Assert.Equal(wav, path);
            Assert.False(isTemporary);
            Assert.Equal(RemoteUploadFormat.Wav, effectiveCodec);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task FailedReencodeFallsBackToWavAndSaysSo()
    {
        // An empty file is not decodable audio, so ffmpeg exits non-zero (and if ffmpeg is absent entirely,
        // the exception path is taken) — both are fallbacks, and BOTH must report wav so the multipart part
        // is labelled to match the bytes actually being sent.
        var notAudio = Path.GetTempFileName();
        try
        {
            var (path, isTemporary, effectiveCodec) = await RemoteAudioEncoder.PrepareUploadAsync(
                notAudio, "opus", NullLogger.Instance, CancellationToken.None);

            Assert.Equal(notAudio, path);
            Assert.False(isTemporary);
            Assert.Equal(RemoteUploadFormat.Wav, effectiveCodec);

            // The label follows the EFFECTIVE codec, so the bytes and the name agree.
            Assert.Equal("audio.wav", RemoteUploadFormat.FileName(effectiveCodec));
            Assert.Equal("audio/wav", RemoteUploadFormat.ContentType(effectiveCodec));
        }
        finally
        {
            File.Delete(notAudio);
        }
    }

    [Fact]
    public async Task FallbackUploadIsSentAsWavNotAsTheConfiguredCodec()
    {
        // End-to-end: a worker configured for Opus whose re-encode cannot run must still POST audio.wav.
        var handler = new RecordingUploadHandler();
        using var client = new HttpClient(handler);
        var provider = new RemoteWhisperProvider(
            NullLogger<RemoteWhisperProvider>.Instance,
            "https://worker.example",
            "whisper-large-v3",
            httpClient: client,
            uploadCodec: "opus");

        // Not decodable audio => the encoder falls back to this file unchanged.
        var notAudio = Path.GetTempFileName();
        try
        {
            await Assert.ThrowsAnyAsync<System.Exception>(
                () => provider.TranscribeAsync(notAudio, "auto", CancellationToken.None));
        }
        finally
        {
            File.Delete(notAudio);
        }

        Assert.NotEmpty(handler.FileNames);
        Assert.All(handler.FileNames, name => Assert.Equal("audio.wav", name));
        Assert.All(handler.ContentTypes, type => Assert.Equal("audio/wav", type));
    }

    /// <summary>Captures the multipart file part's filename and media type from each request.</summary>
    private sealed class RecordingUploadHandler : HttpMessageHandler
    {
        public System.Collections.Generic.List<string> FileNames { get; } = [];

        public System.Collections.Generic.List<string> ContentTypes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                    if (!string.Equals(name, "file", System.StringComparison.Ordinal))
                    {
                        continue;
                    }

                    FileNames.Add(part.Headers.ContentDisposition?.FileName?.Trim('"') ?? "");
                    ContentTypes.Add(part.Headers.ContentType?.MediaType ?? "");
                }
            }

            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("upstream is down"),
            };
        }
    }

    [Fact]
    public async Task BlindFormatRetryIsSpentOnceAcrossJobsNotOncePerJob()
    {
        // The HIGH finding: the format cache fills only on SUCCESS, so a permanently-4xx worker (e.g. the
        // bare OpenRouter model slug the README warns about) used to re-upload the full audio on EVERY job.
        // Job 1 may spend the one blind retry (2 requests); job 2 must not (1 request).
        var handler = new CountingBadRequestHandler();
        using var client = new HttpClient(handler);
        var provider = new RemoteWhisperProvider(
            NullLogger<RemoteWhisperProvider>.Instance,
            "https://worker.example",
            "wrong-model",
            httpClient: client);

        var wav = Path.GetTempFileName();
        try
        {
            await Assert.ThrowsAnyAsync<System.Exception>(
                () => provider.TranscribeAsync(wav, "auto", CancellationToken.None));
            var afterFirstJob = handler.Count;

            await Assert.ThrowsAnyAsync<System.Exception>(
                () => provider.TranscribeAsync(wav, "auto", CancellationToken.None));
            var secondJobRequests = handler.Count - afterFirstJob;

            Assert.Equal(2, afterFirstJob);        // one blind retry, spent
            Assert.Equal(1, secondJobRequests);    // budget gone: no second upload
        }
        finally
        {
            File.Delete(wav);
        }
    }

    /// <summary>Always answers 400 with a body that names no format, and counts requests.</summary>
    private sealed class CountingBadRequestHandler : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"model does not exist"}"""),
            };
        }
    }
}
