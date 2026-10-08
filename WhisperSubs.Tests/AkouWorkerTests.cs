using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using WhisperSubs.ScheduledTasks;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// The akou dialect: a low-priority file job per request, deleted once read or once anything goes wrong,
/// so no audio stays on the server; and the request gate that lets a worker take more titles than it
/// serves requests at once.
/// </summary>
public class AkouWorkerTests
{
    private const string Done = """{"language":"es","duration":1,"text":"Hola.","segments":[{"start":0.0,"end":0.9,"text":"Hola."}]}""";

    [Fact]
    public async Task Job_IsSubmittedLowPriority_ReadAsVerboseJson_AndDeleted()
    {
        var akou = new FakeAkou(statuses: new[] { "running", "done" }, result: Done);
        var wav = Wav();
        try
        {
            var srt = await Provider(akou).TranscribeAsync(wav, "es", CancellationToken.None);

            Assert.Contains("Hola.", srt);
            Assert.Equal("-5", akou.SubmittedFields["priority"]);
            Assert.Equal("es", akou.SubmittedFields["language"]);
            Assert.Equal("best", akou.SubmittedFields["model"]);
            Assert.Equal("whisper-subs", akou.SubmittedFields["title"]);
            Assert.Contains("GET /v1/jobs/job_1/result?format=verbose_json", akou.Calls);
            Assert.Equal("DELETE /v1/jobs/job_1", akou.Calls.Last());
            Assert.All(akou.Calls.Where(c => c.StartsWith("GET /v1/jobs/job_1?", StringComparison.Ordinal)), c => Assert.EndsWith("?wait=60", c));
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task FailedJob_Throws_AndIsStillDeleted()
    {
        var akou = new FakeAkou(statuses: new[] { "failed" }, result: Done, error: "decode_failed");
        var wav = Wav();
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(akou).TranscribeAsync(wav, "es", CancellationToken.None));

            Assert.Contains("decode_failed", ex.Message);
            Assert.Equal("DELETE /v1/jobs/job_1", akou.Calls.Last());
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task JobPastItsDeadline_TimesOut_AndIsStillDeleted()
    {
        // The job never leaves the queue (akou busy with its other clients); the 1 s deadline ends the wait.
        var akou = new FakeAkou(statuses: Enumerable.Repeat("queued", 1000).ToArray(), result: Done, pollDelay: TimeSpan.FromMilliseconds(400));
        var wav = Wav();
        try
        {
            var provider = new RemoteWhisperProvider(NullLogger.Instance, "http://akou:8476", "best", "key",
                realtimeFactor: 0.0001, minTimeoutSeconds: 1, httpClient: new HttpClient(akou), dialect: "akou");

            await Assert.ThrowsAsync<TimeoutException>(() => provider.TranscribeAsync(wav, "es", CancellationToken.None));

            Assert.Equal("DELETE /v1/jobs/job_1", akou.Calls.Last());
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task CancelledJob_IsStillDeleted()
    {
        var akou = new FakeAkou(statuses: Enumerable.Repeat("running", 1000).ToArray(), result: Done, pollDelay: TimeSpan.FromMilliseconds(200));
        var wav = Wav();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(akou).TranscribeAsync(wav, "es", cts.Token));

            Assert.Equal("DELETE /v1/jobs/job_1", akou.Calls.Last());
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task SilenceFromAkou_IsAnEmptyTranscript()
    {
        var akou = new FakeAkou(statuses: new[] { "done" }, result: """{"language":"es","duration":3,"text":"","segments":[]}""");
        var wav = Wav();
        try
        {
            Assert.Equal(string.Empty, await Provider(akou).TranscribeAsync(wav, "es", CancellationToken.None));
            Assert.Equal("DELETE /v1/jobs/job_1", akou.Calls.Last());
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task AkouRow_NeverGetsATranslation()
    {
        var akou = new FakeAkou(statuses: new[] { "done" }, result: Done);
        var wav = Wav();
        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => Provider(akou).TranscribeAsync(wav, "es", CancellationToken.None, translate: true));
            Assert.Empty(akou.Calls);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Theory]
    [InlineData("""{"id":"job_01M4DGZ1Y3VEE9EYX0J8WC3KNW","status":"queued"}""", "job_01M4DGZ1Y3VEE9EYX0J8WC3KNW", "queued", null)]
    [InlineData("""{"id":"job_1","status":"failed","error":{"code":"decode_failed","message":"bad audio"}}""", "job_1", "failed", "bad audio")]
    public void ParseAkouJob_ReadsIdStatusAndError(string json, string id, string status, string? error)
        => Assert.Equal((id, status, error), RemoteWhisperProvider.ParseAkouJob(json));

    [Theory]
    [InlineData("""{"id":"../keys","status":"done"}""")]     // the id goes into URLs
    [InlineData("""{"id":"job_1"}""")]
    [InlineData("""not json""")]
    public void ParseAkouJob_RefusesAnythingElse(string json)
        => Assert.Throws<InvalidOperationException>(() => RemoteWhisperProvider.ParseAkouJob(json));

    [Theory]
    [InlineData("es", true)]
    [InlineData("auto", false)]
    [InlineData("", false)]
    public void AkouJobFields(string language, bool sendsLanguage)
    {
        var fields = RemoteWhisperProvider.BuildAkouJobFields("best", language);

        Assert.Contains(("priority", "-5"), fields);
        Assert.Equal(sendsLanguage, fields.Any(f => f.Name == "language"));
    }

    [Fact]
    public async Task RequestGate_LetsOneRequestThroughAtATime()
    {
        Assert.Equal(1, await PeakConcurrentRequests(maxConcurrentRequests: 1));
        // Positive control: without the gate both requests reach the worker together.
        Assert.Equal(2, await PeakConcurrentRequests(maxConcurrentRequests: 0));
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 2, 2)]
    [InlineData(2, 2, 4)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 99, 4)]
    [InlineData(0, 2, 2)]
    public void TitlesPerWorker(int requests, int titlesPerSlot, int expected)
        => Assert.Equal(expected, WorkerRegistry.TitlesPerWorker(requests, titlesPerSlot));

    [Fact]
    public void TitlesPerSlot_ChangesTheWorkerSignatureOnlyWhenSet()
    {
        var config = new PluginConfiguration();
        config.Workers.Add(new WhisperWorker { Id = "a", ApiUrl = "http://a:9020", Dialect = "akou" });
        var atDefault = SubtitleQueueService.ComputeWorkersSignature(config);
        Assert.DoesNotContain("titles=", atDefault);

        config.RemoteTitlesPerRequestSlot = 2;
        Assert.NotEqual(atDefault, SubtitleQueueService.ComputeWorkersSignature(config));
    }

    [Fact]
    public void Dialect_IsKnown_HostAssisted_AndTranslatesNothing()
    {
        Assert.Equal(WorkerDialect.Akou, WorkerDialect.Normalize(" AKOU "));
        Assert.True(WorkerDialect.IsKnown("akou"));
        Assert.True(WorkerDialect.IsHostAssisted("akou"));
        Assert.True(WorkerDialect.IsHostAssisted("crispasr-qwen3"));
        Assert.False(WorkerDialect.IsHostAssisted("crispasr"));
        Assert.Empty(WorkerTargets.ForRow(new WhisperWorker { Dialect = "akou", CanTranslate = true }));
        var (ok, message) = WorkerConfigValidation.Validate(new WhisperWorker { ApiUrl = "http://a:8476", Dialect = "akou", TranslateTargets = new List<string> { "en" } });
        Assert.False(ok);
        Assert.Contains("cannot translate", message);
    }

    [Fact]
    public void AkouTitles_GoInWindows_QwenServerTitlesDoNot()
    {
        var whisper = new WhisperProvider(NullLogger<WhisperProvider>.Instance, "/nope/whisper.bin", "/nope/whisper-cli", 0, "", "", "", null, 0);
        var akouRow = new HostAssistedProvider(new RemoteWhisperProvider(NullLogger.Instance, "http://a:8476", "best", dialect: "akou"), whisper);
        var qwenRow = new HostAssistedProvider(new RemoteWhisperProvider(NullLogger.Instance, "http://m:9020", "qwen3", dialect: "crispasr-qwen3"), whisper);

        Assert.Equal(SubtitleManager.EngineWindowSeconds, SubtitleManager.WindowSecondsFor(akouRow));
        Assert.Equal(0, SubtitleManager.WindowSecondsFor(qwenRow));
    }

    [Theory]
    [InlineData(true, true, "WaitThenSuspend")]
    [InlineData(true, false, "CancelAndRetry")]
    // A remote job starts at once, but what it runs on this server is suspended during playback.
    [InlineData(false, true, "Suspend")]
    [InlineData(false, false, "RunThrough")]
    public void PlaybackPlan(bool isLocal, bool suspendSupported, string expected)
        => Assert.Equal(expected, SubtitleGenerationTask.PlanForPlayback(isLocal, suspendSupported).ToString());

    private static async Task<int> PeakConcurrentRequests(int maxConcurrentRequests)
    {
        var handler = new SlowWorker();
        var provider = new RemoteWhisperProvider(NullLogger.Instance, "http://w:9020", "qwen3",
            httpClient: new HttpClient(handler), dialect: "crispasr-qwen3", maxConcurrentRequests: maxConcurrentRequests);
        var a = Wav();
        var b = Wav();
        try
        {
            await Task.WhenAll(provider.TranscribeAsync(a, "es", CancellationToken.None), provider.TranscribeAsync(b, "es", CancellationToken.None));
            return handler.Peak;
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    private static RemoteWhisperProvider Provider(FakeAkou akou)
        => new(NullLogger.Instance, "http://akou:8476", "best", "key", httpClient: new HttpClient(akou), dialect: "akou");

    private static string Wav()
    {
        var path = Path.Combine(Path.GetTempPath(), $"akou_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, SyntheticAudio.SilentWav16kMono(1000));
        return path;
    }

    /// <summary>Answers like an akou server and records every call.</summary>
    private sealed class FakeAkou(string[] statuses, string result, string? error = null, TimeSpan? pollDelay = null) : HttpMessageHandler
    {
        private int _poll;
        public ConcurrentQueue<string> CallLog { get; } = new();
        public List<string> Calls => CallLog.ToList();
        public Dictionary<string, string> SubmittedFields { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            CallLog.Enqueue($"{request.Method} {path}");
            if (request.Method == HttpMethod.Post && path == "/v1/jobs")
            {
                foreach (var part in (MultipartFormDataContent)request.Content!)
                {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                    if (name != null && name != "file") SubmittedFields[name] = await part.ReadAsStringAsync(cancellationToken);
                }
                return Json(HttpStatusCode.Accepted, """{"id":"job_1","status":"queued"}""");
            }
            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/jobs/job_1?", StringComparison.Ordinal))
            {
                if (pollDelay is { } delay) await Task.Delay(delay, cancellationToken);
                var status = statuses[Math.Min(Interlocked.Increment(ref _poll) - 1, statuses.Length - 1)];
                var err = error == null ? "" : $",\"error\":{{\"code\":\"{error}\",\"message\":\"{error}\"}}";
                return Json(HttpStatusCode.OK, $"{{\"id\":\"job_1\",\"status\":\"{status}\"{err}}}");
            }
            if (request.Method == HttpMethod.Get && path == "/v1/jobs/job_1/result?format=verbose_json")
            {
                return Json(HttpStatusCode.OK, result);
            }
            if (request.Method == HttpMethod.Delete && path == "/v1/jobs/job_1")
            {
                Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
                return Json(HttpStatusCode.OK, """{"id":"job_1","deleted":true}""");
            }
            return Json(HttpStatusCode.NotFound, """{"error":{"code":"not_found"}}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>A worker that takes 300 ms per request and counts how many it holds at once.</summary>
    private sealed class SlowWorker : HttpMessageHandler
    {
        private int _now;
        private int _peak;
        public int Peak => _peak;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _now);
            int seen;
            while ((seen = _peak) < now && Interlocked.CompareExchange(ref _peak, now, seen) != seen) { }
            await Task.Delay(300, cancellationToken);
            Interlocked.Decrement(ref _now);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"Hola.","segments":[{"start":0,"end":0.9,"text":"Hola.","words":[]}]}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
