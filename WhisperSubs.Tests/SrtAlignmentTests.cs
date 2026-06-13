using System.Text.RegularExpressions;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Tests for <see cref="WhisperProvider.AlignSrtToSpeech"/>, the pure SRT-to-speech
/// start-time alignment helper. whisper.cpp emits gapless segments, so a subtitle for
/// upcoming speech can appear during the preceding silence. AlignSrtToSpeech snaps each
/// subtitle START forward to the next speech onset when the start lands in a silence gap,
/// never moving backward, never past (end - 0.5s), and skipping sub-50ms adjustments.
/// End times, text, and entry numbers are preserved.
/// </summary>
public class SrtAlignmentTests
{
    // Matches an SRT timing line and captures the start and end timestamps.
    private static readonly Regex TimingRegex =
        new(@"(\d{2}:\d{2}:\d{2},\d{3}) --> (\d{2}:\d{2}:\d{2},\d{3})");

    /// <summary>Returns the (start, end) timestamp strings from every timing line in order.</summary>
    private static List<(string Start, string End)> Timings(string srt)
    {
        var list = new List<(string, string)>();
        foreach (Match m in TimingRegex.Matches(srt))
            list.Add((m.Groups[1].Value, m.Groups[2].Value));
        return list;
    }

    // ── 1. Start in a silence gap is snapped forward to the speech onset ──

    [Fact]
    public void EarlyStartInSilence_SnappedForwardToSpeechOnset()
    {
        // Audio is silent 0-2s; speech runs 2.0-5.0s. The subtitle starts at 0.0 (in silence).
        var srt = "1\n00:00:00,000 --> 00:00:05,000\nHello world\n";
        var segments = new List<(double Start, double End)> { (2.0, 5.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        var timings = Timings(result);
        Assert.Single(timings);
        Assert.Equal("00:00:02,000", timings[0].Start); // snapped to onset
        Assert.Equal("00:00:05,000", timings[0].End);    // end preserved
    }

    // ── 2. Start already inside speech is left unchanged ──

    [Fact]
    public void StartInsideSpeech_Unchanged()
    {
        // Subtitle starts at 3.0s, which is inside the 2.0-6.0s speech segment.
        var srt = "1\n00:00:03,000 --> 00:00:06,000\nInside speech\n";
        var segments = new List<(double Start, double End)> { (2.0, 6.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        var timings = Timings(result);
        Assert.Single(timings);
        Assert.Equal("00:00:03,000", timings[0].Start); // unchanged
        Assert.Equal("00:00:06,000", timings[0].End);
    }

    // ── 3. Never moves the start past (end - 0.5s) ──

    [Fact]
    public void NeverMovesPastHalfSecondBeforeEnd()
    {
        // Short entry 0.0 -> 2.2s. Onset at 2.0 would leave only 0.2s on screen, so the start
        // is capped at end - 0.5 = 1.7s. Bound is Math.Max(origStart, origEnd - 0.5) = max(0.0, 1.7) = 1.7.
        var srt = "1\n00:00:00,000 --> 00:00:02,200\nShort line\n";
        var segments = new List<(double Start, double End)> { (2.0, 8.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        var timings = Timings(result);
        Assert.Single(timings);
        Assert.Equal("00:00:01,700", timings[0].Start); // capped at end - 0.5, not snapped to 2.0
        Assert.Equal("00:00:02,200", timings[0].End);

        // Cross-check the exact bound against the spec formula.
        const double origStart = 0.0;
        const double origEnd = 2.2;
        var expectedMax = Math.Max(origStart, origEnd - 0.5); // 1.7
        Assert.Equal(1.7, expectedMax, precision: 3);
    }

    // ── 4. Start after all speech is left unchanged ──

    [Fact]
    public void StartAfterAllSpeech_Unchanged()
    {
        // Subtitle starts at 10.0s; the only speech is 1.0-5.0s. No later onset exists.
        var srt = "1\n00:00:10,000 --> 00:00:12,000\nLate line\n";
        var segments = new List<(double Start, double End)> { (1.0, 5.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        var timings = Timings(result);
        Assert.Single(timings);
        Assert.Equal("00:00:10,000", timings[0].Start); // unchanged
        Assert.Equal("00:00:12,000", timings[0].End);
    }

    // ── 5. Sub-50ms adjustments are skipped ──

    [Fact]
    public void SubFiftyMillisecondAdjustment_Skipped()
    {
        // Start at 5.000s lands in the silence gap between (0,1) and (5.030, 10). The next onset
        // is 5.030s — only 30ms later, below the 50ms churn threshold — so the start is kept.
        var srt = "1\n00:00:05,000 --> 00:00:09,000\nTiny nudge\n";
        var segments = new List<(double Start, double End)> { (0.0, 1.0), (5.030, 10.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        var timings = Timings(result);
        Assert.Single(timings);
        Assert.Equal("00:00:05,000", timings[0].Start); // unchanged (adjustment < 50ms)
        Assert.Equal("00:00:09,000", timings[0].End);
    }

    // ── 6. End times and multi-line text are preserved across a snap ──

    [Fact]
    public void EndTimesAndTextPreserved()
    {
        // Two-line entry whose start (0.0) is in silence and gets snapped to the 3.0s onset.
        var srt = "1\n00:00:00,000 --> 00:00:07,000\nFirst line of dialogue\nSecond line of dialogue\n";
        var segments = new List<(double Start, double End)> { (3.0, 7.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        var timings = Timings(result);
        Assert.Single(timings);
        Assert.Equal("00:00:03,000", timings[0].Start); // snapped
        Assert.Equal("00:00:07,000", timings[0].End);    // end preserved

        // Both text lines present verbatim.
        Assert.Contains("First line of dialogue", result);
        Assert.Contains("Second line of dialogue", result);
    }

    // ── 7. Entry numbers are preserved (no renumbering) ──

    [Fact]
    public void EntryNumbersPreserved()
    {
        var srt = """
            1
            00:00:00,000 --> 00:00:02,000
            One

            2
            00:00:03,000 --> 00:00:05,000
            Two

            3
            00:00:06,000 --> 00:00:08,000
            Three
            """;
        var segments = new List<(double Start, double End)> { (0.0, 8.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        // Numbers 1, 2, 3 appear in order, each on its own line.
        var numberLines = Regex.Matches(result, @"(?m)^(\d+)$").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(new[] { 1, 2, 3 }, numberLines);
    }

    // ── 8. Guard clauses: empty/null segments and whitespace SRT return input unchanged ──

    [Fact]
    public void EmptySpeechSegments_ReturnsUnchanged()
    {
        var srt = "1\n00:00:00,000 --> 00:00:05,000\nHello\n";
        var result = WhisperProvider.AlignSrtToSpeech(srt, new List<(double Start, double End)>());
        Assert.Equal(srt, result);
    }

    [Fact]
    public void NullSegments_ReturnsUnchanged()
    {
        var srt = "1\n00:00:00,000 --> 00:00:05,000\nHello\n";
        var result = WhisperProvider.AlignSrtToSpeech(srt, null!);
        Assert.Equal(srt, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n  \t\n")]
    public void WhitespaceSrt_ReturnsUnchanged(string srt)
    {
        var segments = new List<(double Start, double End)> { (2.0, 5.0) };
        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);
        Assert.Equal(srt, result); // returned verbatim
    }

    // ── 9. Mixed entries: only silence-gap starts move; in-speech starts stay ──

    [Fact]
    public void MultipleEntries_OnlyGapStartsAdjusted()
    {
        // Speech: 2.0-4.0s and 8.0-12.0s. Silence gaps: 0-2s, 4-8s, and after 12s.
        //  Entry 1 starts at 2.5s (inside first speech)   -> unchanged.
        //  Entry 2 starts at 5.0s (silence gap, onset 8.0) -> snapped to 8.0.
        //  Entry 3 starts at 9.0s (inside second speech)  -> unchanged.
        var srt = """
            1
            00:00:02,500 --> 00:00:04,000
            In speech one

            2
            00:00:05,000 --> 00:00:12,000
            In silence gap

            3
            00:00:09,000 --> 00:00:12,000
            In speech two
            """;
        var segments = new List<(double Start, double End)> { (2.0, 4.0), (8.0, 12.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        var timings = Timings(result);
        Assert.Equal(3, timings.Count);

        // Entry 1: in-speech start unchanged.
        Assert.Equal("00:00:02,500", timings[0].Start);
        Assert.Equal("00:00:04,000", timings[0].End);

        // Entry 2: silence-gap start snapped forward to the 8.0s onset (capped well below end - 0.5 = 11.5).
        Assert.Equal("00:00:08,000", timings[1].Start);
        Assert.Equal("00:00:12,000", timings[1].End);

        // Entry 3: in-speech start unchanged.
        Assert.Equal("00:00:09,000", timings[2].Start);
        Assert.Equal("00:00:12,000", timings[2].End);
    }

    // ── 10. Malformed entry (no timing line) passes through; valid entry still processed ──

    [Fact]
    public void MalformedEntry_PassedThrough()
    {
        // A junk block with no "-->" timing line, followed by one valid entry whose start (0.0)
        // is in silence and should be snapped to the 2.0s onset.
        var srt = """
            1
            this is not a timing line
            junk junk junk

            2
            00:00:00,000 --> 00:00:05,000
            Real subtitle
            """;
        var segments = new List<(double Start, double End)> { (2.0, 5.0) };

        // Must not throw.
        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        // Malformed content survives.
        Assert.Contains("this is not a timing line", result);
        Assert.Contains("junk junk junk", result);

        // The valid entry is still processed and snapped.
        var timings = Timings(result);
        Assert.Single(timings);
        Assert.Equal("00:00:02,000", timings[0].Start);
        Assert.Equal("00:00:05,000", timings[0].End);
        Assert.Contains("Real subtitle", result);
    }

    // ── 11. Unsorted segments are ordered internally before onset lookup ──

    [Fact]
    public void UnsortedSegments_StillSnapsToCorrectOnset()
    {
        // Start at 5.0s sits in the silence gap (4-8s). Segments are passed OUT OF ORDER;
        // the internal OrderBy(s => s.Start) must sort them so the onset scan finds 8.0, not 2.0.
        var srt = "1\n00:00:05,000 --> 00:00:12,000\nUnsorted input\n";
        var segments = new List<(double Start, double End)> { (8.0, 12.0), (2.0, 4.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        var timings = Timings(result);
        Assert.Single(timings);
        Assert.Equal("00:00:08,000", timings[0].Start); // snapped to the 8.0s onset, proving the sort
        Assert.Equal("00:00:12,000", timings[0].End);
    }

    // ── 12. Leading silence before the first segment snaps to the first onset ──

    [Fact]
    public void LeadingSilenceBeforeFirstSegment_SnapsToFirstOnset()
    {
        // Audio opens with silence 0-2s; first speech is 2.0-5.0s. The subtitle starts at 0.0.
        var srt = "1\n00:00:00,000 --> 00:00:10,000\nOpening line\n";
        var segments = new List<(double Start, double End)> { (2.0, 5.0), (7.0, 9.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        var timings = Timings(result);
        Assert.Single(timings);
        Assert.Equal("00:00:02,000", timings[0].Start); // snapped to the first onset
        Assert.Equal("00:00:10,000", timings[0].End);
    }

    // ── 13. Two consecutive silence entries snap to their own respective onsets ──

    [Fact]
    public void TwoConsecutiveSilenceEntries_SnapToTheirRespectiveOnsets()
    {
        // Speech: 2.0-4.0s and 8.0-12.0s. Both entries start in silence.
        //  Entry 1 starts at 0.0s (gap before 2.0) -> snaps to 2.0.
        //  Entry 2 starts at 5.0s (gap 4-8s)        -> snaps to 8.0.
        // The onset lookup must restart per entry; no cursor state may leak across entries.
        var srt = """
            1
            00:00:00,000 --> 00:00:10,000
            First gap entry

            2
            00:00:05,000 --> 00:00:12,000
            Second gap entry
            """;
        var segments = new List<(double Start, double End)> { (2.0, 4.0), (8.0, 12.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        var timings = Timings(result);
        Assert.Equal(2, timings.Count);
        Assert.Equal("00:00:02,000", timings[0].Start); // entry 1 -> first onset
        Assert.Equal("00:00:08,000", timings[1].Start); // entry 2 -> second onset (no leak)
    }

    // ── 14. Start exactly at a segment's lower boundary is in-speech (unchanged) ──

    [Fact]
    public void StartExactlyAtSegmentStart_Unchanged()
    {
        // Start 2.0s equals the segment's Start. The in-speech test (Start - 0.001 <= origStart)
        // holds at the lower boundary, so the start is left unchanged.
        var srt = "1\n00:00:02,000 --> 00:00:09,000\nAt onset\n";
        var segments = new List<(double Start, double End)> { (2.0, 9.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        var timings = Timings(result);
        Assert.Single(timings);
        Assert.Equal("00:00:02,000", timings[0].Start); // unchanged (inSpeech at lower boundary)
        Assert.Equal("00:00:09,000", timings[0].End);
    }

    // ── 15. Start exactly at a segment's end is in-speech within tolerance (unchanged) ──

    [Fact]
    public void StartExactlyAtSegmentEnd_Unchanged()
    {
        // Start 6.0s equals the segment's End. The in-speech test (origStart <= End + 0.001)
        // holds at the upper boundary, so the start is left unchanged.
        var srt = "1\n00:00:06,000 --> 00:00:10,000\nAt segment end\n";
        var segments = new List<(double Start, double End)> { (2.0, 6.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        var timings = Timings(result);
        Assert.Single(timings);
        Assert.Equal("00:00:06,000", timings[0].Start); // unchanged (within +0.001 of End)
        Assert.Equal("00:00:10,000", timings[0].End);
    }

    // ── 16. Start just past the last segment's end, with no later onset, is unchanged ──

    [Fact]
    public void StartJustPastLastSegmentEnd_Unchanged()
    {
        // Start 6.002s is just outside the segment end (6.0 + 0.001 = 6.001), so not in-speech.
        // No segment starts after 6.002s, so there is no onset to snap to -> unchanged.
        var srt = "1\n00:00:06,002 --> 00:00:10,000\nJust past the end\n";
        var segments = new List<(double Start, double End)> { (2.0, 6.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        var timings = Timings(result);
        Assert.Single(timings);
        Assert.Equal("00:00:06,002", timings[0].Start); // unchanged (not inSpeech, no later onset)
        Assert.Equal("00:00:10,000", timings[0].End);
    }

    // ── 17. A trailing bare number at EOF does not throw (i >= lines.Length break) ──

    [Fact]
    public void TrailingBareNumberAtEof_DoesNotThrow()
    {
        // Truncated SRT: a valid entry, then a stray bare number "2" with no timing line after it.
        // After reading the number the loop must break on i >= lines.Length without throwing.
        var srt = "1\n00:00:00,000 --> 00:00:02,000\nText\n\n2";
        var segments = new List<(double Start, double End)> { (0.5, 2.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        // Entry 1 survived; no exception was thrown reaching this assertion.
        var timings = Timings(result);
        Assert.Single(timings);
        Assert.Equal("00:00:02,000", timings[0].End);
        Assert.Contains("Text", result);
    }

    // ── 18. CRLF input snaps correctly and strips carriage returns ──

    [Fact]
    public void CrlfInput_SnapsCorrectlyAndStripsCarriageReturns()
    {
        // whisper.cpp SRT can use Windows CRLF line endings. Start at 0.0 is in silence and snaps
        // to the 2.0s onset; carriage returns must not leak into the canonicalized LF output.
        var srt = "1\r\n00:00:00,000 --> 00:00:05,000\r\nHello world\r\n";
        var segments = new List<(double Start, double End)> { (2.0, 6.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        Assert.Contains("00:00:02,000 --> 00:00:05,000", result); // snapped, end preserved
        Assert.Contains("Hello world", result);                   // text preserved
        Assert.DoesNotContain("\r", result);                      // no carriage returns leak
    }

    // ── 19. CRLF multi-entry: numbers preserved; gap snaps, in-speech stays ──

    [Fact]
    public void CrlfMultiEntry_PreservesNumbersAndSnaps()
    {
        // Two CRLF entries. Speech: 2.0-4.0s and 8.0-12.0s.
        //  Entry 1 starts at 2.5s (inside first speech) -> unchanged.
        //  Entry 2 starts at 5.0s (silence gap)         -> snapped to 8.0.
        var srt = "1\r\n00:00:02,500 --> 00:00:04,000\r\nIn speech\r\n\r\n"
                + "2\r\n00:00:05,000 --> 00:00:12,000\r\nIn gap\r\n";
        var segments = new List<(double Start, double End)> { (2.0, 4.0), (8.0, 12.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        // Both original numbers survive (each on its own line).
        var numberLines = Regex.Matches(result, @"(?m)^(\d+)$").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(new[] { 1, 2 }, numberLines);

        var timings = Timings(result);
        Assert.Equal(2, timings.Count);
        Assert.Equal("00:00:02,500", timings[0].Start); // in-speech entry unchanged
        Assert.Equal("00:00:08,000", timings[1].Start); // gap entry snapped to onset
        Assert.DoesNotContain("\r", result);
    }

    // ── 20. Every entry already inside speech: all timings untouched (round-trip) ──

    [Fact]
    public void FullyInSpeechMultiEntry_TimingsUnchanged()
    {
        // Speech: 2.0-4.0s and 8.0-12.0s. All three starts already sit inside a segment, so the
        // pass must touch nothing: each original timing line should appear verbatim in the output.
        var srt = """
            1
            00:00:02,500 --> 00:00:04,000
            Already aligned one

            2
            00:00:09,000 --> 00:00:12,000
            Already aligned two

            3
            00:00:10,000 --> 00:00:12,000
            Already aligned three
            """;
        var segments = new List<(double Start, double End)> { (2.0, 4.0), (8.0, 12.0) };

        var result = WhisperProvider.AlignSrtToSpeech(srt, segments);

        // Original timing lines appear verbatim (nothing snapped).
        Assert.Contains("00:00:02,500 --> 00:00:04,000", result);
        Assert.Contains("00:00:09,000 --> 00:00:12,000", result);
        Assert.Contains("00:00:10,000 --> 00:00:12,000", result);

        // Numbers 1, 2, 3 preserved in order.
        var numberLines = Regex.Matches(result, @"(?m)^(\d+)$").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(new[] { 1, 2, 3 }, numberLines);
    }
}
