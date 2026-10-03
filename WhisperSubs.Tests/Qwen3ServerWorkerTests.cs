using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// The crispasr-qwen3 worker dialect: a Qwen3-ASR server transcribes only, is asked for verbose_json
/// first, its cues are cut from word timestamps, and this server's Whisper detects languages for it.
/// </summary>
public class Qwen3ServerWorkerTests
{
    [Theory]
    [InlineData("crispasr-qwen3", "crispasr-qwen3")]
    [InlineData(" CRISPASR-QWEN3 ", "crispasr-qwen3")]
    [InlineData("crispasr", "crispasr")]
    [InlineData("openai", "openai")]
    [InlineData("", "openai")]
    [InlineData(null, "openai")]
    [InlineData("nope", "openai")]
    public void Dialect_NormalizesTheThirdValue(string? value, string expected)
    {
        Assert.Equal(expected, WorkerDialect.Normalize(value));
    }

    [Fact]
    public void Dialect_IsKnown_AcceptsAllThree()
    {
        Assert.True(WorkerDialect.IsKnown("crispasr-qwen3"));
        Assert.True(WorkerDialect.IsKnown("crispasr"));
        Assert.True(WorkerDialect.IsKnown("openai"));
        Assert.False(WorkerDialect.IsKnown("qwen3"));
    }

    [Fact]
    public void ForRow_Qwen3Server_TranslatesNothingWhateverTheRowSays()
    {
        var row = new WhisperWorker { Dialect = "crispasr-qwen3", CanTranslate = true, TranslateTargets = new List<string> { "en", "nl" } };
        Assert.Empty(WorkerTargets.ForRow(row));
        Assert.True(WorkerTargets.TranscribesItems("crispasr-qwen3", WorkerTargets.ForRow(row)));
    }

    [Fact]
    public void Validate_Qwen3Server_RefusesTargets_AcceptsNone()
    {
        var withTargets = new WhisperWorker { ApiUrl = "http://mini:9020", Dialect = "crispasr-qwen3", TranslateTargets = new List<string> { "en" } };
        var (okWith, errorWith) = WorkerConfigValidation.Validate(withTargets);
        Assert.False(okWith);
        Assert.Contains("cannot translate", errorWith);

        var plain = new WhisperWorker { ApiUrl = "http://mini:9020", Dialect = "crispasr-qwen3", CanTranslate = false };
        var (ok, error) = WorkerConfigValidation.Validate(plain);
        Assert.True(ok, error);
    }

    [Theory]
    [InlineData("crispasr-qwen3", "verbose_json")]
    [InlineData("crispasr", "srt")]
    [InlineData("openai", "srt")]
    [InlineData(null, "srt")]
    public void DefaultResponseFormat_JsonFirstOnlyForTheQwen3Server(string? dialect, string expected)
    {
        Assert.Equal(expected, RemoteWhisperProvider.DefaultResponseFormat(dialect));
    }

    [Theory]
    [InlineData(0, 42)]
    [InlineData(60, 60)]
    public void WordCueMaxChars_FallsBackToTheLocalQwen3Default(int configured, int expected)
    {
        Assert.Equal(expected, WorkerRegistry.WordCueMaxChars(configured));
    }

    [Fact]
    public async Task Qwen3ServerProvider_RefusesAnyTranslation()
    {
        var provider = new RemoteWhisperProvider(NullLogger.Instance, "http://mini:9020", "qwen3", dialect: "crispasr-qwen3", wordCueMaxChars: 42);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.TranscribeAsync("/nope/a.wav", "fr", CancellationToken.None, translate: true));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.TranscribeAsync("/nope/a.wav", "en", CancellationToken.None, targetLanguage: "nl"));
    }

    // The shape the mini's crispasr server returned on 2026-10-03 (segments with words), reduced.
    private const string ServerJson = """
    {"task":"transcribe","language":"en","duration":180.0,"text":"...","segments":[
      {"id":0,"start":50.64,"end":53.6,"text":"Anywhere with you. If I ever find you here again, I'm calling the cops.",
       "words":[{"word":"Anywhere","start":50.64,"end":50.96},{"word":"with","start":51.04,"end":51.12},{"word":"you.","start":51.2,"end":51.28},
                {"word":"If","start":51.76,"end":51.84},{"word":"I","start":51.9,"end":51.95},{"word":"ever","start":52.0,"end":52.2},{"word":"find","start":52.24,"end":52.4},
                {"word":"you","start":52.44,"end":52.6},{"word":"here","start":52.64,"end":52.8},{"word":"again,","start":52.84,"end":53.0},{"word":"I'm","start":53.04,"end":53.2},
                {"word":"calling","start":53.24,"end":53.4},{"word":"the","start":53.44,"end":53.5},{"word":"cops.","start":53.52,"end":53.6}]},
      {"id":1,"start":55.04,"end":70.56,"text":"Wait, please. This is everything that has ever meant anything to me.",
       "words":[{"word":"Wait,","start":55.04,"end":55.5},{"word":"please.","start":55.6,"end":56.16},
                {"word":"This","start":66.48,"end":66.7},{"word":"is","start":66.74,"end":66.8},{"word":"everything","start":66.84,"end":67.3},{"word":"that","start":67.34,"end":67.5},
                {"word":"has","start":67.54,"end":67.7},{"word":"ever","start":67.74,"end":68.0},{"word":"meant","start":68.04,"end":68.4},{"word":"anything","start":70.0,"end":70.3},
                {"word":"to","start":70.34,"end":70.4},{"word":"me.","start":70.44,"end":70.56}]}
    ]}
    """;

    [Fact]
    public void ConvertWithWordCues_CutsAtSentenceEndsGapsAndLength()
    {
        var srt = RemoteWhisperProvider.ConvertTranscriptionResponseToSrt(ServerJson, 180, 42);
        var cues = srt.Split("\n\n");
        // "Anywhere with you." | "If I ever find you here again, I'm calling" (42 chars) | "the cops." | "Wait, please." | "This is everything that has ever meant" (gap 1.6 s before "anything") | "anything to me."
        Assert.Equal(6, cues.Length);
        Assert.Contains("00:00:50,640 --> 00:00:51,280\nAnywhere with you.", cues[0]);
        Assert.Contains("00:00:51,760 --> 00:00:53,400\nIf I ever find you here again, I'm calling", cues[1]);
        Assert.Contains("00:00:53,440 --> 00:00:53,940\nthe cops.", cues[2]);          // half-second floor
        Assert.Contains("00:00:55,040 --> 00:00:56,160\nWait, please.", cues[3]);       // sentence end, then a ten-second pause
        Assert.Contains("00:01:06,480 --> 00:01:08,400\nThis is everything that has ever meant", cues[4]);
        Assert.Contains("00:01:10,000 --> 00:01:10,560\nanything to me.", cues[5]);
    }

    [Fact]
    public void ConvertWithoutWordCueLimit_KeepsTheSegmentPath()
    {
        var srt = RemoteWhisperProvider.ConvertTranscriptionResponseToSrt(ServerJson, 180, 0);
        Assert.Equal(2, srt.Split("\n\n").Length);
        Assert.Contains("00:00:50,640 --> 00:00:53,600", srt);
    }

    [Fact]
    public void ConvertWithWordCueLimit_SegmentsWithoutWords_FallBackToSegments()
    {
        const string json = """{"segments":[{"start":1.0,"end":2.5,"text":"Hello there."}]}""";
        var srt = RemoteWhisperProvider.ConvertTranscriptionResponseToSrt(json, 10, 42);
        Assert.Contains("00:00:01,000 --> 00:00:02,500\nHello there.", srt);
    }

    [Fact]
    public void CutCues_NeverLetsACueRunIntoTheNext()
    {
        var words = new List<RemoteWhisperProvider.TimedWord>
        {
            new("One.", 1.0, 1.05),      // floor would push its end to 1.5, past the next word
            new("Two", 1.2, 1.6),
        };
        var cues = RemoteWhisperProvider.CutCues(words, 42);
        Assert.Equal(2, cues.Count);
        Assert.Equal(1.2, cues[0].End);
        Assert.Equal(1.2, cues[1].Start);
    }

    [Theory]
    [InlineData("cops.", true)]
    [InlineData("really?", true)]
    [InlineData("no!", true)]
    [InlineData("\"Done.\"", true)]
    [InlineData("again,", false)]
    [InlineData("Dr.", true)]
    [InlineData("word", false)]
    [InlineData("", false)]
    public void EndsSentence_TerminalMarks(string word, bool expected)
    {
        Assert.Equal(expected, RemoteWhisperProvider.EndsSentence(word));
    }

    private static WhisperProvider Whisper()
        => new(NullLogger<WhisperProvider>.Instance, "/nope/whisper.bin", "/nope/whisper-cli", 0, "", "", "", null, 0);

    [Fact]
    public async Task LocalDetectionProvider_DetectsOnWhisper_TranscribesOnTheRemote()
    {
        var remote = new RemoteWhisperProvider(NullLogger.Instance, "http://mini:9020", "qwen3", dialect: "crispasr-qwen3", wordCueMaxChars: 42);
        var wrapped = new LocalDetectionProvider(remote, Whisper());
        Assert.Equal(remote.Name, wrapped.Name);
        Assert.Equal(remote.RequiresSpeechAlignmentOptIn, wrapped.RequiresSpeechAlignmentOptIn);

        var detect = await Assert.ThrowsAsync<System.IO.FileNotFoundException>(() => wrapped.DetectLanguageAsync("/nope/a.wav", CancellationToken.None));
        Assert.Contains("Whisper model", detect.Message);                   // went to the local Whisper

        var transcribe = await Assert.ThrowsAsync<System.IO.FileNotFoundException>(() => wrapped.TranscribeAsync("/nope/a.wav", "en", CancellationToken.None));
        Assert.Contains("Audio file", transcribe.Message);                  // went to the remote (its own check)
    }

    [Fact]
    public void DetectionWhisper_SeesThroughTheWrapper_AsWhisperDoesNot()
    {
        var whisper = Whisper();
        var remote = new RemoteWhisperProvider(NullLogger.Instance, "http://mini:9020", "qwen3", dialect: "crispasr-qwen3");
        var wrapped = new LocalDetectionProvider(remote, whisper);
        Assert.Same(whisper, SubtitleManager.DetectionWhisper(wrapped));
        Assert.Null(SubtitleManager.AsWhisper(wrapped));                    // the turbo warning stays local-only
        Assert.Null(SubtitleManager.DetectionWhisper(remote));
    }
}
