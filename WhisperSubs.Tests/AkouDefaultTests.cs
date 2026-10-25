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
using System.Xml.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// akou as the default engine: an install with an akou row and nothing else installed must transcribe,
/// name untagged languages through akou, keep cues readable, and skip what it cannot do with a clear
/// reason instead of failing. Every rule here keeps the old behaviour when the local engine is installed.
/// </summary>
public class AkouDefaultTests
{
    // ---- Cue split ----

    private static (double Start, double End, string Text) Cue(double start, double end, string text) => (start, end, text);

    [Fact]
    public void Split_LeavesACueThatFits()
    {
        var cue = Cue(1, 4, "A short line.");
        Assert.Equal(new[] { cue }, AkouCueSplit.Split(new[] { cue }));
    }

    [Fact]
    public void Split_ALongSegment_GivesReadableContiguousCues()
    {
        // 26 s, 3 sentences, ~300 characters: the shape akou's best preset returns on real titles.
        var text = "I told you already that we cannot stay here tonight because the river is rising and the road is closed. "
                 + "Nobody listens to me in this house, not you, not your brother, and certainly not your father. "
                 + "So pack what you need and meet me at the car in ten minutes or I leave without you.";
        var pieces = AkouCueSplit.Split(new[] { Cue(10, 36, text) });

        Assert.True(pieces.Count >= 4);
        Assert.All(pieces, p => Assert.True(p.Text.Length <= AkouCueSplit.MaxChars, p.Text));
        Assert.All(pieces, p => Assert.True(p.End - p.Start <= AkouCueSplit.MaxSeconds + 1e-9, $"{p.End - p.Start:F2}s"));
        Assert.Equal(10, pieces[0].Start);
        Assert.Equal(36, pieces[^1].End);
        for (var i = 1; i < pieces.Count; i++) Assert.Equal(pieces[i - 1].End, pieces[i].Start, 9);
        Assert.Equal(Words(text), string.Join(' ', pieces.Select(p => p.Text)).Split(' '));
    }

    [Fact]
    public void Split_BreaksAtSentenceEnds()
    {
        var pieces = AkouCueSplit.Split(new[] { Cue(0, 14, "Where are you going? Home. I am tired of all of this, honestly.") });
        Assert.Equal("Where are you going?", pieces[0].Text);
        Assert.Equal("Home.", pieces[1].Text);
    }

    [Fact]
    public void Split_AShortSentence_JoinsTheShorterNeighbour()
    {
        // At 9 s "Home." would get 0.7 s, under the floor: it joins the shorter sentence before it.
        var pieces = AkouCueSplit.Split(new[] { Cue(0, 9, "Where are you going? Home. I am tired of all of this, honestly.") });
        Assert.Equal("Where are you going? Home.", pieces[0].Text);
        Assert.Equal("I am tired of all of this, honestly.", pieces[1].Text);
    }

    [Fact]
    public void Split_ALongSentenceWithinTheCharLimit_SplitsOnTime()
    {
        // 60 characters over 20 s: fits two lines, but no viewer holds one cue for 20 s.
        var pieces = AkouCueSplit.Split(new[] { Cue(0, 20, "and then we walked along the shore until the light was gone") });
        Assert.True(pieces.Count >= 3);
        Assert.All(pieces, p => Assert.True(p.End - p.Start <= AkouCueSplit.MaxSeconds + 1e-9));
    }

    [Theory]
    [InlineData("Supercalifragilisticexpialidocious", 12.0)]   // one word: nothing to split at
    [InlineData("", 12.0)]
    public void Split_KeepsWhatCannotSplit(string text, double end)
    {
        var cue = Cue(0, end, text);
        Assert.Equal(new[] { cue }, AkouCueSplit.Split(new[] { cue }));
    }

    [Fact]
    public void Split_KeepsASegmentTooShortForItsPieces()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 60));   // 299 characters in 0.3 s
        var cue = Cue(5, 5.3, text);
        Assert.Equal(new[] { cue }, AkouCueSplit.Split(new[] { cue }));
    }

    [Fact]
    public void AkouTranscript_SplitsSegmentsWithoutWords()
    {
        const string body = """{"text":"x","segments":[{"s":0,"e":24,"text":"First sentence is here and it is long enough to matter. Second one follows it closely. And a third sentence ends this long segment."}]}""";
        var srt = RemoteWhisperProvider.AkouTranscript(body, 30);
        var cues = Regex.Matches(srt, @"^\d+$", RegexOptions.Multiline).Count;
        Assert.True(cues >= 4, srt);
        Assert.DoesNotContain(srt.Split('\n'), l => l.Trim().Length > AkouCueSplit.MaxChars);
    }

    private static string[] Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    // ---- WAV slice ----

    [Fact]
    public void WavSlice_CopiesTheAskedStretch_WithACanonicalHeader()
    {
        var wav = LongWav(70_000);   // 70 s at 32,000 bytes a second
        using var source = new MemoryStream(wav);
        using var slice = new MemoryStream();

        var bytes = WavSlice.Copy(source, slice, 30, 30);

        Assert.Equal(30 * 32_000, bytes);
        Assert.Equal(WavSlice.HeaderBytes + bytes, slice.Length);
        var header = slice.ToArray();
        Assert.Equal("RIFF", Encoding.ASCII.GetString(header, 0, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(header, 36, 4));
        Assert.Equal((uint)bytes, BitConverter.ToUInt32(header, 40));
        Assert.Equal((uint)(36 + bytes), BitConverter.ToUInt32(header, 4));
    }

    [Fact]
    public void WavSlice_PastTheEnd_CopiesWhatIsLeft()
    {
        using var source = new MemoryStream(LongWav(40_000));
        using var tail = new MemoryStream();
        Assert.Equal(10 * 32_000, WavSlice.Copy(source, tail, 30, 30));
        using var none = new MemoryStream();
        Assert.Equal(0, WavSlice.Copy(source, none, 60, 30));
    }

    [Fact]
    public void WavSlice_KeepsTheBytesInOrder()
    {
        var wav = LongWav(2_000);
        var dataStart = wav.Length - 64_000;
        for (var i = dataStart; i < wav.Length; i++) wav[i] = (byte)(i % 251);
        using var source = new MemoryStream(wav);
        using var slice = new MemoryStream();
        WavSlice.Copy(source, slice, 1, 0.5);
        var expected = wav.Skip(dataStart + 32_000).Take(16_000).ToArray();
        Assert.Equal(expected, slice.ToArray().Skip(WavSlice.HeaderBytes).ToArray());
    }

    [Fact]
    public void WavSlice_RejectsWhatIsNotAWav()
    {
        using var source = new MemoryStream(Encoding.ASCII.GetBytes("OggS not a wave file at all"));
        Assert.Throws<InvalidDataException>(() => WavSlice.Copy(source, new MemoryStream(), 0, 30));
    }

    // ---- Language detection through akou ----

    [Fact]
    public async Task Detection_SendsA30sSlice_WithAnExplicitLanguageList_AndDeletesTheJob()
    {
        var akou = new MultiJobAkou("""{"language":"en","text":"Hello there.","segments":[]}""");
        var wav = Wav(70_000);
        try
        {
            var (language, probability) = await Provider(akou).DetectAkouLanguageAsync(wav, CancellationToken.None);

            Assert.Equal("en", language);
            Assert.Equal(1.0f, probability);
            // Two slices that agree settle it; the third is never sent.
            Assert.Equal(2, akou.Jobs.Count);
            var job = akou.Jobs[0];
            Assert.Equal(WavSlice.HeaderBytes + 30 * 32_000, job.UploadBytes);
            Assert.Equal("best", job.Field("preset"));
            Assert.Equal("-5", job.Field("priority"));
            Assert.Null(job.Field("language"));
            Assert.Equal(Setup.Qwen3Catalog.LanguageCodes(), job.All("languages[]"));
            Assert.Contains("GET /v1/jobs/job_1/result", akou.Calls);
            Assert.Equal(new[] { "DELETE /v1/jobs/job_1", "DELETE /v1/jobs/job_2" }, akou.Calls.Where(c => c.StartsWith("DELETE", StringComparison.Ordinal)));
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task Detection_UsesTheRowsLanguageList()
    {
        var akou = new MultiJobAkou("""{"language":"es","segments":[]}""");
        var wav = Wav(5_000);
        try
        {
            var provider = new RemoteWhisperProvider(NullLogger.Instance, "http://akou:8476", "best", "key",
                httpClient: new HttpClient(akou), dialect: "akou", akou: new AkouJobOptions(-3, 600, new[] { "es", "en" }));
            await provider.DetectAkouLanguageAsync(wav, CancellationToken.None);
            Assert.Equal(new[] { "es", "en" }, akou.Jobs[0].All("languages[]"));
            Assert.Equal("-3", akou.Jobs[0].Field("priority"));
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task Detection_MovesPastASilentSlice()
    {
        var akou = new MultiJobAkou("""{"note":"no_speech","text":"","segments":[]}""", """{"language":"fr","segments":[]}""");
        var wav = Wav(70_000);
        try
        {
            var (language, _) = await Provider(akou).DetectAkouLanguageAsync(wav, CancellationToken.None);
            Assert.Equal("fr", language);
            Assert.Equal(3, akou.Jobs.Count);
            Assert.Equal(new[] { "DELETE /v1/jobs/job_1", "DELETE /v1/jobs/job_2", "DELETE /v1/jobs/job_3" }, akou.Calls.Where(c => c.StartsWith("DELETE", StringComparison.Ordinal)));
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task Detection_GivesUp_WhenEverySliceIsSilent()
    {
        var silent = """{"note":"no_speech","text":"","segments":[]}""";
        var akou = new MultiJobAkou(silent, silent, silent, silent);
        var wav = Wav(200_000);
        try
        {
            // A skip the sweep remembers, not a failure retried every night.
            var ex = await Assert.ThrowsAsync<LanguageNotCoveredException>(() => Provider(akou).DetectAkouLanguageAsync(wav, CancellationToken.None));
            Assert.Equal(RemoteWhisperProvider.AkouUndetectedMessage, ex.Message);
            Assert.Equal("", ex.Language);
            Assert.Equal(RemoteWhisperProvider.AkouDetectionSlices, akou.Jobs.Count);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task Detection_StopsWhenTheAudioEnds()
    {
        var akou = new MultiJobAkou("""{"note":"no_speech","segments":[]}""");
        var wav = Wav(10_000);
        try
        {
            await Assert.ThrowsAsync<LanguageNotCoveredException>(() => Provider(akou).DetectAkouLanguageAsync(wav, CancellationToken.None));
            Assert.Single(akou.Jobs);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task Detection_AnOpeningInAnotherLanguage_DoesNotDecideTheTitle()
    {
        // An English song over the titles, then Spanish dialogue: the first slice alone would name every
        // window English, and Qwen would translate the dialogue into it.
        var akou = new MultiJobAkou("""{"language":"en","segments":[]}""", """{"language":"es","segments":[]}""", """{"language":"es","segments":[]}""");
        var wav = Wav(600_000);
        try
        {
            var (language, _) = await Provider(akou).DetectAkouLanguageAsync(wav, CancellationToken.None);
            Assert.Equal("es", language);
            Assert.Equal(3, akou.Jobs.Count);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task Detection_SlicesThatAllDisagree_SettleNothing()
    {
        var akou = new MultiJobAkou("""{"language":"en","segments":[]}""", """{"language":"es","segments":[]}""",
            """{"language":"fr","segments":[]}""", """{"language":"de","segments":[]}""");
        var wav = Wav(200_000);
        try
        {
            var ex = await Assert.ThrowsAsync<LanguageNotCoveredException>(() => Provider(akou).DetectAkouLanguageAsync(wav, CancellationToken.None));
            Assert.Contains("disagreed: en, es, fr, de", ex.Message);
            Assert.Equal(4, akou.Jobs.Count);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Theory]
    [InlineData(0, new[] { 0.0 })]
    [InlineData(20, new[] { 0.0 })]
    [InlineData(30, new[] { 0.0 })]
    [InlineData(70, new[] { 0.0, 20, 40 })]
    [InlineData(600, new[] { 0.0, 190, 380, 570 })]
    [InlineData(7200, new[] { 0.0, 2390, 4780, 7170 })]
    public void DetectionStarts_SpreadOverTheAudio(double duration, double[] expected)
        => Assert.Equal(expected, RemoteWhisperProvider.AkouDetectionStarts(duration));

    [Fact]
    public void DetectionVerdict_NeedsTwoSlicesOrTheOnlyOne()
    {
        Assert.Null(RemoteWhisperProvider.AkouDetectionVerdict(new[] { "en" }, final: false));
        Assert.Equal("en", RemoteWhisperProvider.AkouDetectionVerdict(new[] { "en" }, final: true));
        Assert.Equal("es", RemoteWhisperProvider.AkouDetectionVerdict(new[] { "en", "es", "es" }, final: false));
        Assert.Null(RemoteWhisperProvider.AkouDetectionVerdict(new[] { "en", "es" }, final: true));
        Assert.Null(RemoteWhisperProvider.AkouDetectionVerdict(Array.Empty<string>(), final: true));
    }

    [Fact]
    public void WavSlice_ReadsTheDuration()
    {
        using var source = new MemoryStream(LongWav(70_000));
        Assert.Equal(70.0, WavSlice.DurationSeconds(source), 3);
    }

    [Fact]
    public async Task Detection_IsOnlyForAkouRows()
    {
        var provider = new RemoteWhisperProvider(NullLogger.Instance, "http://mini:9020", "qwen3", dialect: "crispasr-qwen3");
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DetectAkouLanguageAsync("/nope.wav", CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"language":"de"}""", "de", false)]
    [InlineData("""{"language":"","note":"no_speech"}""", null, true)]
    [InlineData("""{"text":"x"}""", null, false)]
    public void ParseAkouDetection_ReadsLanguageAndNote(string body, string? language, bool noSpeech)
        => Assert.Equal((language, noSpeech), RemoteWhisperProvider.ParseAkouDetection(body));

    [Fact]
    public void ParseAkouDetection_RejectsMalformedJson()
        => Assert.Throws<InvalidOperationException>(() => RemoteWhisperProvider.ParseAkouDetection("not json"));

    [Fact]
    public async Task HostAssisted_AkouRowWithoutWhisper_DetectsThroughAkou_ThenTranscribesInThatLanguage()
    {
        var akou = new MultiJobAkou(
            """{"language":"en","segments":[]}""",
            """{"language":"en","duration_s":1,"text":"Hi.","segments":[{"s":0,"e":0.8,"text":"Hi.","speaker":null}],"words":[{"w":"Hi.","s":null,"e":null,"c":null}]}""");
        var wav = Wav(3_000);
        try
        {
            var wrapped = new HostAssistedProvider(Provider(akou), Whisper(), () => false);
            var srt = await wrapped.TranscribeAsync(wav, "auto", CancellationToken.None);

            Assert.Contains("Hi.", srt);
            Assert.Equal(2, akou.Jobs.Count);
            Assert.Equal("en", akou.Jobs[1].Field("language"));
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task HostAssisted_AkouRowWithoutWhisper_UncoveredLanguage_NeverReachesAkou()
    {
        var akou = new MultiJobAkou();
        var wrapped = new HostAssistedProvider(Provider(akou), Whisper(), () => false);
        var ex = await Assert.ThrowsAsync<LanguageNotCoveredException>(() => wrapped.TranscribeAsync("/nope.wav", "sw", CancellationToken.None));
        Assert.Equal("akou's Qwen3-ASR does not cover sw; install the local Whisper engine to transcribe it.", ex.Message);
        Assert.Empty(akou.Jobs);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]    // this server's Whisper detects, as before
    [InlineData(false, false, false)]  // a crispasr Qwen3-ASR row has no detection of its own
    public void DetectsThroughAkou_OnlyForAnAkouRowWithoutWhisper(bool hostWhisper, bool akouRow, bool expected)
        => Assert.Equal(expected, HostAssistedProvider.DetectsThroughAkou(hostWhisper, akouRow));

    [Fact]
    public void DetectionFields_NeverCarryALanguage()
    {
        var fields = RemoteWhisperProvider.BuildAkouDetectionFields(-5, null);
        Assert.DoesNotContain(fields, f => f.Name == "language");
        Assert.Equal(30, fields.Count(f => f.Name == "languages[]"));
        Assert.Equal(30, Setup.Qwen3Catalog.LanguageCodes().Distinct().Count());
        Assert.All(Setup.Qwen3Catalog.LanguageCodes(), c => Assert.True(Setup.Qwen3Catalog.Supports(c)));
    }

    // ---- Server check (the Test button) ----

    // GET /v1/server as akou 1.2.4 answers it, trimmed to the fields the check reads.
    private const string Server124 = """
        {"name":"akou","version":"1.2.4","presets":[{"name":"fast","available":true},{"name":"best","available":true},{"name":"fusion","available":false}],
         "capabilities":{"jobs":true,"bazarr":true,"wyoming":false},"bound_languages":["es","en"],"queue":{"depth":2,"running":1}}
        """;

    [Fact]
    public void Check_AGoodServerAndKey_IsOk()
    {
        var result = AkouServerCheck.Evaluate(200, Server124, 200, """{"name":"whisper-subs","scopes":["jobs"]}""");
        Assert.True(result.Ok, result.Message);
        Assert.Equal("1.2.4", result.Version);
        Assert.Equal(new[] { "fast", "best" }, result.Presets);
        Assert.True(result.BestAvailable);
        Assert.Equal(new[] { "es", "en" }, result.BoundLanguages);
        Assert.Equal(2, result.QueueDepth);
        Assert.Equal("akou 1.2.4: takes jobs, best is installed, 2 job(s) queued.", result.Message);
    }

    [Fact]
    public void Check_AnAdminKey_IsOk()
        => Assert.True(AkouServerCheck.Evaluate(200, Server124, 200, """{"scopes":["admin"]}""").Ok);

    [Theory]
    [InlineData(null, null, "No key")]
    [InlineData(401, """{"error":{"code":"unauthorized"}}""", "refused the key (401)")]
    [InlineData(200, """{"scopes":["bazarr"]}""", "lacks the jobs scope")]
    [InlineData(403, """{"error":"bad_host"}""", "server.public_host")]
    [InlineData(500, "", "answered HTTP 500")]
    public void Check_KeyProblems_SayWhatToDo(int? keyStatus, string? keyBody, string expected)
    {
        var result = AkouServerCheck.Evaluate(200, Server124, keyStatus, keyBody);
        Assert.False(result.Ok);
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public void Check_MissingBestAndNoJobs_AreBothReported()
    {
        const string body = """{"name":"akou","version":"1.2.4","presets":[{"name":"best","available":false}],"capabilities":{"jobs":false}}""";
        var result = AkouServerCheck.Evaluate(200, body, 200, """{"scopes":["jobs"]}""");
        Assert.False(result.Ok);
        Assert.Equal(2, result.Problems.Count);
        Assert.Contains("akou models pull best", result.Message);
        Assert.Contains("capabilities.jobs", result.Message);
    }

    [Theory]
    [InlineData(403, """{"error":"bad_host"}""", "server.public_host")]
    [InlineData(404, "", "HTTP 404")]
    [InlineData(200, """{"name":"whisper.cpp"}""", "not an akou server")]
    [InlineData(200, "<html>", "not an akou server")]
    [InlineData(200, "[]", "not an akou server")]
    public void Check_NotAnAkouServer(int status, string body, string expected)
    {
        var result = AkouServerCheck.Evaluate(status, body, 200, """{"scopes":["jobs"]}""");
        Assert.False(result.Ok);
        Assert.Contains(expected, result.Message);
    }

    [Theory]
    [InlineData("""{"error":"bad_host"}""", "bad_host")]
    [InlineData("""{"error":{"code":"forbidden"}}""", "forbidden")]
    [InlineData("""{"error":3}""", null)]
    [InlineData("nope", null)]
    [InlineData(null, null)]
    public void ErrorCode_ReadsBothShapes(string? body, string? expected)
        => Assert.Equal(expected, AkouServerCheck.ErrorCode(body));

    [Theory]
    [InlineData("""{"scopes":["jobs","bazarr"]}""", true)]
    [InlineData("""{"scopes":"jobs"}""", false)]
    [InlineData("""{}""", false)]
    [InlineData("x", false)]
    public void HasJobsScope_ReadsTheScopes(string body, bool expected)
        => Assert.Equal(expected, AkouServerCheck.HasJobsScope(body));

    // ---- What an akou-only install builds and downloads ----

    [Theory]
    [InlineData(false, false, false, false)]   // akou alone: neither model is fetched
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, false)]     // crispasr (Qwen3-ASR, Canary) uses the VAD model too
    [InlineData(true, true, true, true)]
    public void LocalDownloads_FollowTheBinaries(bool whisper, bool crisp, bool vad, bool detection)
        => Assert.Equal((vad, detection), SubtitleProviderFactory.LocalDownloads(whisper, crisp));

    [Fact]
    public void BinaryOnDisk_LooksWhereWhisperIsFound()
    {
        var sep = Path.PathSeparator;
        Assert.True(WhisperProvider.BinaryOnDisk("/opt/w", null, null, p => p == "/opt/w"));
        // A configured path that is missing is not rescued by PATH, as FindWhisperExecutable does.
        Assert.False(WhisperProvider.BinaryOnDisk("/opt/w", null, "/usr/bin", p => p == Path.Combine("/usr/bin", "whisper-cli")));
        Assert.True(WhisperProvider.BinaryOnDisk(null, "/data", null, p => p == Path.Combine("/data", "whisper", "whisper-cli")));
        Assert.True(WhisperProvider.BinaryOnDisk("", null, $"/a{sep}/b", p => p == Path.Combine("/b", "whisper")));
        Assert.False(WhisperProvider.BinaryOnDisk(null, "/data", $"/a{sep}/b", _ => false));
    }

    [Fact]
    public void LocalEngines_AnAkouOnlyInstall_HasNoEngine()
    {
        var engines = LocalEngines.From(new PluginConfiguration(), "/data", "/usr/bin", _ => false);
        Assert.False(engines.Whisper);
        Assert.False(engines.HasEngine);
        Assert.False(engines.WhisperTranslates);
    }

    [Fact]
    public void LocalEngines_WhisperWithAModel_DetectsAndTranslates()
    {
        var config = new PluginConfiguration { WhisperModelPath = "/models/ggml-medium.bin", WhisperBinaryPath = "/bin/whisper-cli" };
        var engines = LocalEngines.From(config, null, null, p => p is "/models/ggml-medium.bin" or "/bin/whisper-cli");
        Assert.True(engines.Whisper);
        Assert.True(engines.WhisperTranslates);
        Assert.True(engines.HasEngine);
    }

    [Fact]
    public void LocalEngines_ATurboModel_DoesNotTranslate()
    {
        var config = new PluginConfiguration { WhisperModelPath = "/models/ggml-large-v3-turbo.bin", WhisperBinaryPath = "/bin/whisper-cli" };
        var engines = LocalEngines.From(config, null, null, _ => true);
        Assert.True(engines.Whisper);
        Assert.False(engines.WhisperTranslates);
    }

    [Fact]
    public void LocalEngines_AModelWithoutTheBinary_IsNoEngine()
    {
        var config = new PluginConfiguration { WhisperModelPath = "/models/ggml-medium.bin" };
        Assert.False(LocalEngines.From(config, "/data", "", p => p == "/models/ggml-medium.bin").HasEngine);
    }

    [Theory]
    [InlineData(false, 1, false)]   // nothing installed beside an akou row: the host stays out
    [InlineData(false, 0, true)]    // alone it stays, so its own "download a model" error shows
    [InlineData(true, 1, true)]
    public void LocalUsable_KeepsAnEmptyHostOutOfAPool(bool hasEngine, int remotes, bool expected)
        => Assert.Equal(expected, WorkerPlan.LocalUsable(hasEngine, remotes));

    [Fact]
    public void HostsLocal_WithAnEngine_IsUnchanged()
    {
        Assert.True(WorkerPlan.HostsLocal(1, 1, false, true));
        Assert.True(WorkerPlan.HostsLocal(1, 1, false, true, localHasEngine: true));
        Assert.False(WorkerPlan.HostsLocal(1, 1, false, true, localHasEngine: false));
        Assert.True(WorkerPlan.HostsLocal(0, 0, false, true, localHasEngine: false));
    }

    [Fact]
    public void HasTranscriber_CountsAnAkouRowWithNoModelPath()
    {
        Assert.False(WorkerPlan.HasTranscriber(new PluginConfiguration()));
        Assert.True(WorkerPlan.HasTranscriber(new PluginConfiguration { WhisperModelPath = "/m.bin" }));
        Assert.True(WorkerPlan.HasTranscriber(new PluginConfiguration { RemoteWhisperApiUrl = "http://x" }));
        Assert.True(WorkerPlan.HasTranscriber(Akou()));
        var disabled = Akou();
        disabled.Workers[0].Enabled = false;
        Assert.False(WorkerPlan.HasTranscriber(disabled));
        var noUrl = Akou();
        noUrl.Workers[0].ApiUrl = " ";
        Assert.False(WorkerPlan.HasTranscriber(noUrl));
    }

    // ---- English and forced when no worker can do them ----

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]    // the host's own worker serves English itself
    [InlineData(false, false, false)]
    public void HostAssistedEnglish_OnlyWithATranslatingWhisperAndNoLocalWorker(bool translates, bool localInPool, bool expected)
        => Assert.Equal(expected, WorkerTargets.HostAssistedEnglish(translates, localInPool));

    [Fact]
    public void ForRow_AkouAdvertisesEnglishOnlyWhenTheHostTranslates()
    {
        var row = Akou().Workers[0];
        Assert.Empty(WorkerTargets.ForRow(row));
        Assert.Equal(new[] { "en" }, WorkerTargets.ForRow(row, hostAssistedEnglish: true));
        Assert.True(WorkerTargets.ServedByConfig("en", false, false, new[] { row }, hostAssistedEnglish: true));
        Assert.False(WorkerTargets.ServedByConfig("en", false, false, new[] { row }));
        Assert.False(WorkerTargets.ServedByConfig("fr", false, false, new[] { row }, hostAssistedEnglish: true));
    }

    [Fact]
    public void Requirements_WithoutATranslatingWorker_DropEnglish()
    {
        Assert.Equal("en", WorkerJob.Requirements(SubtitleMode.Full, true).TranslateTarget);
        Assert.Equal("en", WorkerJob.Requirements(SubtitleMode.Full, true, poolTranslates: true).TranslateTarget);
        Assert.Null(WorkerJob.Requirements(SubtitleMode.Full, true, poolTranslates: false).TranslateTarget);
        Assert.False(WorkerJob.TranslationOn(SubtitleMode.Full, true, false));
        Assert.True(WorkerJob.TranslationOn(SubtitleMode.Full, true, true));
    }

    [Fact]
    public void TranslationSkippedWarning_OnlyWhenAskedAndImpossible()
    {
        Assert.Null(WorkerJob.TranslationSkippedWarning(SubtitleMode.Full, true, true));
        Assert.Null(WorkerJob.TranslationSkippedWarning(SubtitleMode.Full, false, false));
        Assert.StartsWith("English translation skipped:", WorkerJob.TranslationSkippedWarning(SubtitleMode.Full, true, false));
        Assert.Contains("Translation Only mode makes nothing", WorkerJob.TranslationSkippedWarning(SubtitleMode.TranslationOnly, false, false) ?? "");
    }

    [Theory]
    [InlineData(SubtitleMode.FullAndForced, false, true, SubtitleMode.Full)]
    [InlineData(SubtitleMode.FullAndForced, true, true, SubtitleMode.FullAndForced)]
    [InlineData(SubtitleMode.ForcedOnly, false, true, null)]
    [InlineData(SubtitleMode.TranslationOnly, true, false, null)]
    [InlineData(SubtitleMode.Full, false, false, SubtitleMode.Full)]
    public void EffectiveMode_DropsWhatThePoolCannotDo(SubtitleMode mode, bool forced, bool translates, SubtitleMode? expected)
        => Assert.Equal(expected, SubtitleManager.EffectiveMode(mode, forced, translates));

    [Fact]
    public void ForcedSkippedWarning_NamesTheMode()
    {
        Assert.Null(SubtitleManager.ForcedSkippedWarning(SubtitleMode.FullAndForced, true));
        Assert.Null(SubtitleManager.ForcedSkippedWarning(SubtitleMode.Full, false));
        Assert.Contains("runs as Full", SubtitleManager.ForcedSkippedWarning(SubtitleMode.FullAndForced, false));
        Assert.Contains("makes nothing", SubtitleManager.ForcedSkippedWarning(SubtitleMode.ForcedOnly, false));
    }

    [Fact]
    public void DetectsForced_OnlyWithAWhisperHere()
    {
        var row = Provider(new MultiJobAkou());
        Assert.False(SubtitleManager.DetectsForced(new HostAssistedProvider(row, Whisper(), () => false)));
        Assert.True(SubtitleManager.DetectsForced(new HostAssistedProvider(row, Whisper(), () => true)));
        Assert.True(SubtitleManager.DetectsForced(row));
        Assert.True(SubtitleManager.DetectsForced(null));
    }

    [Fact]
    public void NotCoveredOnly_WhenEveryPassSkipped()
    {
        var uncovered = new LanguageNotCoveredException("sw");
        var S = SubtitleManager.GenerationOutcome.Skipped;
        Assert.Same(uncovered, SubtitleManager.NotCoveredOnly(new[] { (S, (Exception?)null), (S, (Exception?)uncovered) }));
        Assert.Null(SubtitleManager.NotCoveredOnly(new[] { (S, (Exception?)uncovered), (SubtitleManager.GenerationOutcome.Succeeded, (Exception?)null) }));
        Assert.Null(SubtitleManager.NotCoveredOnly(new[] { (S, (Exception?)null) }));
        Assert.Null(SubtitleManager.NotCoveredOnly(Array.Empty<(SubtitleManager.GenerationOutcome, Exception?)>()));
    }

    [Theory]
    [InlineData("auto", 600, null, true)]     // first window of an untagged title: detect once
    [InlineData("", 600, null, true)]
    [InlineData("auto", 600, "en", false)]    // later windows reuse the code
    [InlineData("es", 600, null, false)]      // tagged: nothing to detect
    [InlineData("auto", 0, null, false)]      // one pass: the provider detects as before
    public void DetectOnceForWindows_OnlyTheFirstWindowOfAnUntaggedTitle(string language, double window, string? resolved, bool expected)
        => Assert.Equal(expected, SubtitleManager.DetectOnceForWindows(language, window, resolved));

    [Fact]
    public void Degraded_ChangesTheSignatureOnlyWhenSomethingIsMissing()
    {
        var config = new PluginConfiguration();
        var plain = SubtitleSkipCache.ComputeSignature(config);
        Assert.Equal(plain, SubtitleSkipCache.ComputeSignature(config, null, SubtitleSkipCache.Degraded(false, false, false)));
        Assert.Equal(new[] { "forced", "en", "whisper" }, SubtitleSkipCache.Degraded(true, true, true));
        var degraded = SubtitleSkipCache.ComputeSignature(config, null, SubtitleSkipCache.Degraded(true, false, true));
        Assert.Equal(plain + "|degraded=forced,whisper", degraded);
    }

    // ---- Upgrade: a 4.16.1 config keeps working as it did ----

    [Fact]
    public void Upgrade_A4161ConfigWithLocalWhisperAndAnAkouRow_BehavesAsBefore()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <WhisperModelPath>/config/plugins/configurations/WhisperSubs/models/ggml-medium.bin</WhisperModelPath>
              <SubtitleMode>FullAndForced</SubtitleMode>
              <EnableTranslation>true</EnableTranslation>
              <EnableLocalWorker>true</EnableLocalWorker>
              <Workers>
                <WhisperWorker>
                  <Id>a1</Id>
                  <Name>akou</Name>
                  <Enabled>true</Enabled>
                  <ApiUrl>https://akou.example</ApiUrl>
                  <Model>best</Model>
                  <MaxConcurrency>1</MaxConcurrency>
                  <Dialect>akou</Dialect>
                  <Priority>-5</Priority>
                </WhisperWorker>
              </Workers>
            </PluginConfiguration>
            """;
        var config = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(new StringReader(xml))!;
        var engines = LocalEngines.From(config, "/config/plugins/configurations/WhisperSubs", "/usr/bin", _ => true);

        Assert.True(engines.HasEngine);
        Assert.True(WorkerPlan.HostsLocal(config.Workers.Count, 1, false, config.EnableLocalWorker, engines.HasEngine));
        Assert.False(WorkerTargets.HostAssistedEnglish(engines.WhisperTranslates, localWorkerInPool: true));
        Assert.Empty(WorkerTargets.ForRow(config.Workers[0], hostAssistedEnglish: false));
        Assert.Equal(SubtitleMode.FullAndForced, SubtitleManager.EffectiveMode(config.SubtitleMode, true, true));
        Assert.Equal("en", WorkerJob.Requirements(config.SubtitleMode, config.EnableTranslation, true).TranslateTarget);
        Assert.Equal(SubtitleSkipCache.ComputeSignature(config),
            SubtitleSkipCache.ComputeSignature(config, null, SubtitleSkipCache.Degraded(false, false, false)));
    }

    // ---- Helpers ----

    private static PluginConfiguration Akou() => new()
    {
        Workers = new List<WhisperWorker> { new() { Name = "akou", Dialect = "akou", ApiUrl = "https://akou.example", Model = "best", Enabled = true } },
    };

    private static WhisperProvider Whisper()
        => new(NullLogger<WhisperProvider>.Instance, "/nope/whisper.bin", "/nope/whisper-cli", 0, "", "", "", null, 0);

    /// <summary>A 16 kHz mono 16-bit WAV of silence of any length (SyntheticAudio caps at 2 s).</summary>
    private static byte[] LongWav(int milliseconds)
    {
        var data = 32 * milliseconds;
        var wav = new byte[WavSlice.HeaderBytes + data];
        var header = SyntheticAudio.SilentWav16kMono(10);
        Array.Copy(header, wav, WavSlice.HeaderBytes);
        BitConverter.GetBytes(36 + data).CopyTo(wav, 4);
        BitConverter.GetBytes(data).CopyTo(wav, 40);
        return wav;
    }

    private static RemoteWhisperProvider Provider(MultiJobAkou akou)
        => new(NullLogger.Instance, "http://akou:8476", "best", "key", httpClient: new HttpClient(akou), dialect: "akou");

    private static string Wav(int milliseconds)
    {
        var path = Path.Combine(Path.GetTempPath(), $"akoudet_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, LongWav(milliseconds));
        return path;
    }

    /// <summary>One submitted job: its form fields in order and the size of its upload.</summary>
    private sealed record SubmittedJob(List<(string Name, string Value)> Fields, long UploadBytes)
    {
        public string? Field(string name) => Fields.Where(f => f.Name == name).Select(f => f.Value).FirstOrDefault();
        public IReadOnlyList<string> All(string name) => Fields.Where(f => f.Name == name).Select(f => f.Value).ToList();
    }

    /// <summary>An akou server that takes several jobs, each done at once with the next queued result.</summary>
    private sealed class MultiJobAkou(params string[] results) : HttpMessageHandler
    {
        private int _next;
        public ConcurrentQueue<string> CallLog { get; } = new();
        public List<string> Calls => CallLog.ToList();
        public List<SubmittedJob> Jobs { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            CallLog.Enqueue($"{request.Method} {path}");
            if (request.Method == HttpMethod.Post && path == "/v1/jobs")
            {
                var fields = new List<(string, string)>();
                long upload = 0;
                foreach (var part in (MultipartFormDataContent)request.Content!)
                {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"') ?? "";
                    if (name == "file") upload = (await part.ReadAsByteArrayAsync(cancellationToken)).LongLength;
                    else fields.Add((name, await part.ReadAsStringAsync(cancellationToken)));
                }
                lock (Jobs) Jobs.Add(new SubmittedJob(fields, upload));
                return Json(HttpStatusCode.Accepted, $"{{\"id\":\"job_{Jobs.Count}\",\"status\":\"queued\"}}");
            }
            var match = Regex.Match(path, @"^/v1/jobs/(job_\d+)(/result)?(\?.*)?$");
            if (match.Success && request.Method == HttpMethod.Get && !match.Groups[2].Success)
            {
                return Json(HttpStatusCode.OK, $"{{\"id\":\"{match.Groups[1].Value}\",\"status\":\"done\"}}");
            }
            if (match.Success && request.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK, results[Math.Min(Interlocked.Increment(ref _next) - 1, results.Length - 1)]);
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
