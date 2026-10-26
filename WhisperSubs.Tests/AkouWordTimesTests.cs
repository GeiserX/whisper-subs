using System;
using System.Linq;
using System.Text.RegularExpressions;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// akou's native job result (<c>format=json</c>): words as <c>{w, s, e, c}</c>, with <c>s</c> and <c>e</c> null
/// where akou has no word times (an older akou, or a piece its aligner could not time). Cues follow the word
/// times wherever akou gives them, and fall back to the segment split only where it does not.
/// </summary>
public class AkouWordTimesTests
{
    private static string W(string w, double? s, double? e)
        => $"{{\"w\":\"{w}\",\"s\":{(s?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")},\"e\":{(e?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")},\"c\":null}}";

    private static string Body(string segments, params string[] words)
        => $"{{\"status\":\"done\",\"language\":\"en\",\"text\":\"x\",\"segments\":[{segments}],\"words\":[{string.Join(",", words)}]}}";

    private static string Seg(double s, double e, string text)
        => $"{{\"s\":{s.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"e\":{e.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"text\":\"{text}\",\"speaker\":null}}";

    private static (string Start, string End, string Text)[] Cues(string srt)
        => Regex.Matches(srt, @"^\d+\r?\n(\S+) --> (\S+)\r?\n(.+?)\r?$", RegexOptions.Multiline)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)).ToArray();

    [Fact]
    public void TimedWords_CutCuesAtTheWordTimes()
    {
        // One 6 s segment; the speech starts at 0.5 s and pauses for 1.8 s after the first sentence.
        var body = Body(Seg(0, 6, "Hello there. How are you today?"),
            W("Hello", 0.5, 0.8), W("there.", 0.9, 1.2), W("How", 3.0, 3.2), W("are", 3.3, 3.4), W("you", 3.5, 3.6), W("today?", 3.7, 4.4));

        var cues = Cues(RemoteWhisperProvider.AkouTranscript(body, 10));

        Assert.Equal(new[]
        {
            ("00:00:00,500", "00:00:01,200", "Hello there."),
            ("00:00:03,000", "00:00:04,400", "How are you today?"),
        }, cues);
    }

    [Fact]
    public void TimedWords_KeepCuesWithinOneLine()
    {
        var words = Enumerable.Range(0, 30).Select(i => W("word" + i, i * 0.4, i * 0.4 + 0.3)).ToArray();
        var text = string.Join(" ", Enumerable.Range(0, 30).Select(i => "word" + i));
        var srt = RemoteWhisperProvider.AkouTranscript(Body(Seg(0, 12, text), words), 20);

        var cues = Cues(srt);
        Assert.True(cues.Length >= 5, srt);
        Assert.All(cues, c => Assert.True(c.Text.Length <= Qwen3Provider.DefaultMaxLineLength, c.Text));
        Assert.Equal("00:00:00,000", cues[0].Start);
    }

    [Fact]
    public void APieceWithoutWordTimes_IsSplitBySegment_TheTimedPieceKeepsItsWordTimes()
    {
        // The aligner timed the first piece and failed on the second.
        var body = Body(
            Seg(0, 2, "Hello there.") + "," + Seg(10, 12, "Nobody timed these words."),
            W("Hello", 0.5, 0.8), W("there.", 0.9, 1.2),
            W("Nobody", null, null), W("timed", null, null), W("these", null, null), W("words.", null, null));

        var cues = Cues(RemoteWhisperProvider.AkouTranscript(body, 20));

        Assert.Equal(new[]
        {
            ("00:00:00,500", "00:00:01,200", "Hello there."),
            ("00:00:10,000", "00:00:12,000", "Nobody timed these words."),
        }, cues);
    }

    [Fact]
    public void NoWordTimes_SplitsSegmentsAsBefore()
    {
        const string text = "First sentence is here and it is long enough to matter. Second one follows it closely. And a third sentence ends this long segment.";
        var words = text.Split(' ').Select(w => W(w, null, null)).ToArray();
        var srt = RemoteWhisperProvider.AkouTranscript(Body(Seg(0, 24, text), words), 30);

        var expected = AkouCueSplit.Split(new[] { (0.0, 24.0, text) });
        var cues = Cues(srt);
        Assert.True(expected.Count >= 4);
        Assert.Equal(expected.Select(c => c.Text), cues.Select(c => c.Text));
        Assert.DoesNotContain(srt.Split('\n'), l => l.Trim().Length > AkouCueSplit.MaxChars);
    }

    [Fact]
    public void WordsThatDoNotSpellTheSegments_FallBackToTheSegmentSplit()
    {
        var body = Body(
            Seg(0, 2, "Hello there.") + "," + Seg(10, 12, "Something else."),
            W("Hello", 0.5, 0.8), W("there.", 0.9, 1.2), W("Other", null, null), W("words.", null, null));

        var cues = Cues(RemoteWhisperProvider.AkouTranscript(body, 20));

        Assert.Equal(new[]
        {
            ("00:00:00,000", "00:00:02,000", "Hello there."),
            ("00:00:10,000", "00:00:12,000", "Something else."),
        }, cues);
    }

    [Fact]
    public void NoSpeech_IsAnEmptySubtitle()
        => Assert.Equal("", RemoteWhisperProvider.AkouTranscript("""{"status":"done","text":"","note":"no_speech","segments":[],"words":[]}""", 10));

    [Fact]
    public void ASegmentPastTheAudio_IsRefused()
        => Assert.Throws<InvalidOperationException>(() => RemoteWhisperProvider.AkouTranscript(Body(Seg(0, 60, "Too long."), W("Too", null, null), W("long.", null, null)), 10));

    [Fact]
    public void ATranslation_HasSegmentTimesOnly()
    {
        // akou's translate task: language en, segments with times, words without.
        const string body = """{"status":"done","language":"en","source_language":"fr","task":"translate","text":"Good morning.","segments":[{"s":1,"e":2.5,"text":"Good morning.","speaker":null}],"words":[{"w":"Good","s":null,"e":null,"c":null},{"w":"morning.","s":null,"e":null,"c":null}]}""";
        Assert.Equal(new[] { ("00:00:01,000", "00:00:02,500", "Good morning.") }, Cues(RemoteWhisperProvider.AkouTranscript(body, 5)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"text":"x"}""")]
    [InlineData("""{"segments":[{"s":5,"e":6,"text":"b"},{"s":1,"e":2,"text":"a"}]}""")]
    public void AMalformedResult_IsRefused(string body)
        => Assert.Throws<InvalidOperationException>(() => RemoteWhisperProvider.AkouTranscript(body, 10));

    [Fact]
    public void AWordTimePastTheAudio_IsRefused()
        => Assert.Throws<InvalidOperationException>(() => RemoteWhisperProvider.AkouTranscript(Body(Seg(0, 2, "Late."), W("Late.", 40, 41)), 10));

    [Fact]
    public void ATimedPieceRunningIntoTheNext_EndsWhereTheNextStarts()
    {
        // The aligner put the last word of the first piece past the start of the untimed second piece.
        var body = Body(
            Seg(0, 2, "Hello there.") + "," + Seg(1.5, 3, "Untimed."),
            W("Hello", 0.5, 0.8), W("there.", 0.9, 1.9), W("Untimed.", null, null));

        var cues = Cues(RemoteWhisperProvider.AkouTranscript(body, 10));

        Assert.Equal(("00:00:00,500", "00:00:01,500", "Hello there."), cues[0]);
        Assert.Equal(("00:00:01,500", "00:00:03,000", "Untimed."), cues[1]);
    }

    [Theory]
    [InlineData("""{"warnings":["word times unavailable on 1 of 3 pieces: the aligner timed out"]}""", 1)]
    [InlineData("""{"warnings":[]}""", 0)]
    [InlineData("""{"warnings":"x"}""", 0)]
    [InlineData("""[]""", 0)]
    [InlineData("not json", 0)]
    public void Warnings_AreRead(string body, int count)
        => Assert.Equal(count, RemoteWhisperProvider.AkouWarnings(body).Count);

    [Fact]
    public void ALongWarning_IsCut()
        => Assert.Equal(300, RemoteWhisperProvider.AkouWarnings($"{{\"warnings\":[\"{new string('a', 1000)}\"]}}").Single().Length);

    [Theory]
    [InlineData("akou", true, true, null, true)]
    [InlineData("akou", true, false, "en", true)]
    [InlineData("akou", true, true, "de", false)]
    [InlineData("akou", false, true, null, false)]
    [InlineData("akou", true, false, null, false)]
    [InlineData("crispasr-qwen3", true, true, null, false)]
    public void AkouTranslatesRequest_OnlyEnglishOnAnAkouThatOffersIt(string dialect, bool serverTranslates, bool translate, string? target, bool expected)
        => Assert.Equal(expected, RemoteWhisperProvider.AkouTranslatesRequest(dialect, serverTranslates, translate, target));
}
