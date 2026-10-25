using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// akou's translate task: an akou server whose <c>GET /v1/server</c> says <c>capabilities.translate</c> makes the
/// English subtitle itself, so an install with no Whisper of its own still gets one. An older akou says nothing
/// about it and its row behaves as before: English goes to this server's Whisper, or is skipped.
/// </summary>
public class AkouTranslateTests
{
    private const string ServerTranslates = """
        {"name":"akou","version":"1.3.0","presets":[{"name":"best","available":true}],
         "capabilities":{"jobs":true,"translate":true},"translate_languages":["fr","es"],"queue":{"depth":0}}
        """;

    // akou 1.2.4 has no translate task and no capabilities.translate.
    private const string Server124 = """
        {"name":"akou","version":"1.2.4","presets":[{"name":"best","available":true}],"capabilities":{"jobs":true,"bazarr":true}}
        """;

    private const string Translated = """
        {"status":"done","language":"en","source_language":"fr","task":"translate","text":"Good morning.",
         "segments":[{"s":0.1,"e":0.9,"text":"Good morning.","speaker":null}],
         "words":[{"w":"Good","s":null,"e":null,"c":null},{"w":"morning.","s":null,"e":null,"c":null}]}
        """;

    [Theory]
    [InlineData(ServerTranslates, true)]
    [InlineData(Server124, false)]
    [InlineData("""{"name":"akou","capabilities":{"translate":"yes"}}""", false)]
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void Translates_ReadsCapabilitiesTranslate(string? body, bool expected)
        => Assert.Equal(expected, AkouServerCheck.Translates(body));

    [Fact]
    public void Check_SaysWhenTheServerTranslates()
    {
        var result = AkouServerCheck.Evaluate(200, ServerTranslates, 200, """{"scopes":["jobs"]}""");
        Assert.True(result.Ok, result.Message);
        Assert.True(result.Translates);
        Assert.Contains("translates into English", result.Message);
        Assert.False(AkouServerCheck.Evaluate(200, Server124, 200, """{"scopes":["jobs"]}""").Translates);
    }

    [Fact]
    public async Task Refresh_ReadsTheServer_AndKeepsTheLastAnswerWhenItCannot()
    {
        var akou = new TranslateAkou(ServerTranslates);
        var provider = Provider(akou);
        Assert.False(provider.AkouTranslates);

        await provider.RefreshAkouServerAsync(CancellationToken.None);
        Assert.True(provider.AkouTranslates);
        Assert.Contains("GET /v1/server", akou.Calls);

        akou.ServerBody = null;   // the server is down: 503
        await provider.RefreshAkouServerAsync(CancellationToken.None);
        Assert.True(provider.AkouTranslates);

        akou.ServerBody = Server124;
        await provider.RefreshAkouServerAsync(CancellationToken.None);
        Assert.False(provider.AkouTranslates);
    }

    [Theory]
    [InlineData("fr", "fr")]
    [InlineData("auto", "auto")]
    [InlineData("", "auto")]
    public async Task Translation_IsAnAkouTranslateJob_WithoutAModel(string language, string sent)
    {
        var akou = new TranslateAkou(ServerTranslates, Translated);
        var provider = Provider(akou);
        await provider.RefreshAkouServerAsync(CancellationToken.None);
        var wav = Wav();
        try
        {
            var srt = await provider.TranscribeAsync(wav, language, CancellationToken.None, translate: true);

            Assert.Contains("Good morning.", srt);
            var job = Assert.Single(akou.Jobs);
            Assert.Equal("translate", job["task"]);
            Assert.Equal(sent, job["language"]);
            Assert.Equal("-5", job["priority"]);
            Assert.Equal("whisper-subs", job["title"]);
            // akou runs Canary or Whisper by the language; `best` would be refused.
            Assert.False(job.ContainsKey("model"));
            Assert.False(job.ContainsKey("languages[]"));
            Assert.Contains("GET /v1/jobs/job_1/result?format=json", akou.Calls);
            Assert.Equal("DELETE /v1/jobs/job_1", akou.Calls.Last());
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task Transcription_DoesNotSendATask()
    {
        var akou = new TranslateAkou(ServerTranslates, Translated);
        var provider = Provider(akou);
        await provider.RefreshAkouServerAsync(CancellationToken.None);
        var wav = Wav();
        try
        {
            await provider.TranscribeAsync(wav, "fr", CancellationToken.None);
            var job = Assert.Single(akou.Jobs);
            Assert.False(job.ContainsKey("task"));
            Assert.Equal("best", job["model"]);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task AnOlderAkou_IsNeverGivenATranslation()
    {
        var akou = new TranslateAkou(Server124, Translated);
        var provider = Provider(akou);
        await provider.RefreshAkouServerAsync(CancellationToken.None);
        var wav = Wav();
        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => provider.TranscribeAsync(wav, "fr", CancellationToken.None, translate: true));
            Assert.Empty(akou.Jobs);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task ACanaryTarget_IsNeverSentToAkou()
    {
        var akou = new TranslateAkou(ServerTranslates, Translated);
        var provider = Provider(akou);
        await provider.RefreshAkouServerAsync(CancellationToken.None);
        var wav = Wav();
        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => provider.TranscribeAsync(wav, "en", CancellationToken.None, targetLanguage: "de"));
            Assert.Empty(akou.Jobs);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task HostAssisted_EnglishGoesToAkou_WhenAkouTranslates()
    {
        var akou = new TranslateAkou(ServerTranslates, Translated);
        var remote = Provider(akou);
        await remote.RefreshAkouServerAsync(CancellationToken.None);
        // This server's Whisper does not exist: reaching it would throw.
        var wrapped = new HostAssistedProvider(remote, Whisper(), () => false);
        var wav = Wav();
        try
        {
            var srt = await wrapped.TranscribeAsync(wav, "fr", CancellationToken.None, translate: true);
            Assert.Contains("Good morning.", srt);
            Assert.Equal("translate", Assert.Single(akou.Jobs)["task"]);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task HostAssisted_AnOlderAkou_LeavesEnglishToThisServersWhisper()
    {
        var akou = new TranslateAkou(Server124, Translated);
        var remote = Provider(akou);
        await remote.RefreshAkouServerAsync(CancellationToken.None);
        var wrapped = new HostAssistedProvider(remote, Whisper(), () => false);
        var wav = Wav();
        try
        {
            // As in 4.17: the host's Whisper takes it, and here it is missing.
            await Assert.ThrowsAnyAsync<Exception>(() => wrapped.TranscribeAsync(wav, "fr", CancellationToken.None, translate: true));
            Assert.Empty(akou.Jobs);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]    // this server's Whisper serves it, as before
    public async Task AkouWorker_AdvertisesEnglish_WhenAkouOrThisServersWhisperMakesIt(bool hostEnglish, bool akouTranslates, bool expected)
    {
        var akou = new TranslateAkou(akouTranslates ? ServerTranslates : Server124);
        var remote = Provider(akou);
        var worker = new AkouTranscriptionWorker("a", "akou", new HostAssistedProvider(remote, Whisper(), () => false), new WorkerCapabilities
        {
            IsLocal = false,
            TranslateTargets = hostEnglish ? WorkerTargets.EnglishOnly : WorkerTargets.None,
            HostAssisted = true,
        }, remote);
        Assert.Equal(hostEnglish, worker.Capabilities.TranslateTargets.Contains("en"));

        await worker.RefreshAsync(CancellationToken.None);

        Assert.Equal(expected, worker.Capabilities.TranslateTargets.Contains("en"));
        Assert.True(worker.Capabilities.HostAssisted);
    }

    [Fact]
    public async Task Pool_Refresh_TurnsEnglishOnForTheRun()
    {
        var akou = new TranslateAkou(ServerTranslates);
        var remote = Provider(akou);
        var worker = new AkouTranscriptionWorker("a", "akou", new HostAssistedProvider(remote, Whisper(), () => false),
            new WorkerCapabilities { IsLocal = false, TranslateTargets = WorkerTargets.None, HostAssisted = true }, remote);
        // A worker with nothing to read refreshes as a no-op.
        var other = new TranscriptionWorker("w", "whisper", Whisper(), new WorkerCapabilities { IsLocal = false, TranslateTargets = WorkerTargets.None });
        var pool = new WorkerPool(new ITranscriptionWorker[] { worker, other });
        Assert.False(pool.HasCapableWorker(WorkerJob.EnglishItems));

        await pool.RefreshAsync(CancellationToken.None);

        Assert.True(pool.HasCapableWorker(WorkerJob.EnglishItems));
    }

    private static RemoteWhisperProvider Provider(TranslateAkou akou)
        => new(NullLogger.Instance, "http://akou:8476", "best", "key", httpClient: new HttpClient(akou), dialect: "akou");

    private static WhisperProvider Whisper()
        => new(NullLogger<WhisperProvider>.Instance, "/nope/whisper.bin", "/nope/whisper-cli", 0, "", "", "", null, 0);

    private static string Wav()
    {
        var path = Path.Combine(Path.GetTempPath(), $"akoutr_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, SyntheticAudio.SilentWav16kMono(1000));
        return path;
    }

    /// <summary>An akou server that answers <c>/v1/server</c> and takes jobs that are done at once.</summary>
    private sealed class TranslateAkou(string? serverBody, string result = Translated) : HttpMessageHandler
    {
        public string? ServerBody { get; set; } = serverBody;
        private readonly ConcurrentQueue<string> _calls = new();
        public List<string> Calls => _calls.ToList();
        public List<Dictionary<string, string>> Jobs { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            _calls.Enqueue($"{request.Method} {path}");
            if (request.Method == HttpMethod.Get && path == "/v1/server")
            {
                return ServerBody == null
                    ? Json(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"down"}}""")
                    : Json(HttpStatusCode.OK, ServerBody);
            }
            if (request.Method == HttpMethod.Post && path == "/v1/jobs")
            {
                var fields = new Dictionary<string, string>();
                foreach (var part in (MultipartFormDataContent)request.Content!)
                {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"') ?? "";
                    if (name != "file") fields[name] = await part.ReadAsStringAsync(cancellationToken);
                }
                lock (Jobs) Jobs.Add(fields);
                return Json(HttpStatusCode.Accepted, $"{{\"id\":\"job_{Jobs.Count}\",\"status\":\"queued\"}}");
            }
            var match = Regex.Match(path, @"^/v1/jobs/(job_\d+)(/result)?(\?.*)?$");
            if (match.Success && request.Method == HttpMethod.Get && !match.Groups[2].Success)
            {
                return Json(HttpStatusCode.OK, $"{{\"id\":\"{match.Groups[1].Value}\",\"status\":\"done\"}}");
            }
            if (match.Success && request.Method == HttpMethod.Get && match.Groups[3].Value == "?format=json")
            {
                return Json(HttpStatusCode.OK, result);
            }
            if (match.Success && request.Method == HttpMethod.Delete)
            {
                return Json(HttpStatusCode.OK, $"{{\"id\":\"{match.Groups[1].Value}\",\"deleted\":true}}");
            }
            return Json(HttpStatusCode.NotFound, """{"error":{"code":"not_found"}}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
