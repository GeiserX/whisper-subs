using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WhisperSubs.Configuration;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Tests for the <c>SubtitleMaxLineLength</c> → <c>--max-len</c>/<c>--split-on-word</c> mapping.
/// whisper.cpp applies NO character cap of its own (<c>--max-len</c> defaults to 0 = unlimited), so a
/// stretch the model fails to punctuate lands in one enormous run-on cue — the complaint in issue #151.
/// The setting is opt-in, so the load-bearing property is that the DEFAULT emits nothing at all and
/// existing installs keep their exact command line.
/// </summary>
public class MaxLineLengthArgsTests
{
    private static IReadOnlyList<string> Build(int maxLineLength)
        => WhisperProvider.BuildTranscribeArguments(
            "/m/model.bin", "/tmp/a.wav", "es", threadCount: 0, translate: false,
            vadModelPath: null, outputPrefix: "/tmp/out", langPrompt: null, tuning: null,
            maxLineLength: maxLineLength);

    [Fact]
    public void Default_EmitsNeitherFlag_SoExistingInstallsAreUnchanged()
    {
        // The whole back-compat guarantee of this feature. If this fails, every existing user's
        // subtitles get silently re-segmented and re-timed by an upgrade.
        var args = Build(0);

        Assert.DoesNotContain("--max-len", args);
        Assert.DoesNotContain("--split-on-word", args);
    }

    [Fact]
    public void Positive_EmitsMaxLenWithItsValue()
    {
        var args = Build(47);

        var idx = IndexOf(args, "--max-len");
        Assert.True(idx >= 0, $"--max-len missing from: {string.Join(" ", args)}");
        Assert.Equal("47", args[idx + 1]);
    }

    [Fact]
    public void Positive_AlsoEmitsSplitOnWord()
    {
        // Never a bare --max-len: whisper.cpp splits on TOKEN boundaries by default, so a cap without
        // --split-on-word can cut a word in half. The two must always travel together.
        Assert.Contains("--split-on-word", Build(47));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-47)]
    public void NonPositive_IsTreatedAsUnset(int value)
    {
        // Mirrors the VadTuning sentinel convention: a non-positive value means "leave whisper's own
        // default alone" rather than emitting a nonsense --max-len -47 that whisper-cli would reject.
        var args = Build(value);

        Assert.DoesNotContain("--max-len", args);
        Assert.DoesNotContain("--split-on-word", args);
    }

    [Fact]
    public void SplitOnWord_IsNeverEmittedWithoutMaxLen()
    {
        // A bare --split-on-word is inert (it only modifies where --max-len cuts), so emitting one
        // alone would be dead noise on the command line and a sign the pairing had drifted apart.
        foreach (var value in new[] { 0, -1, 1, 47, 500 })
        {
            var args = Build(value);
            if (args.Contains("--split-on-word"))
            {
                Assert.Contains("--max-len", args);
            }
        }
    }

    [Fact]
    public void Value_UsesInvariantFormatting()
    {
        // Guards against a locale that would render 1000 as "1,000" or "1 000" — whisper-cli parses
        // this with std::stoi and would silently truncate at the separator.
        var args = Build(1000);
        var idx = IndexOf(args, "--max-len");

        Assert.Equal(1000.ToString(CultureInfo.InvariantCulture), args[idx + 1]);
        Assert.DoesNotContain(",", args[idx + 1]);
    }

    [Fact]
    public void MaxLen_PrecedesOutputFlags_SoCustomArgsCanStillSupersedeIt()
    {
        // Custom args are appended after this whole vector, and whisper-cli takes the LAST value for a
        // repeated flag — that is what lets a power user override the setting from Custom Whisper
        // Arguments, exactly as the VAD tuning flags behave. Structured flags must therefore all sit
        // inside the built vector rather than being tacked on at the very end.
        var args = Build(47);
        var maxLenIdx = IndexOf(args, "--max-len");
        var outputIdx = IndexOf(args, "-osrt");

        // Assert presence FIRST: a bare `maxLenIdx < outputIdx` is trivially true when --max-len is
        // absent (-1 < anything), which would let this test pass against a build that emits nothing.
        Assert.True(maxLenIdx >= 0, $"--max-len missing from: {string.Join(" ", args)}");
        Assert.True(outputIdx >= 0, $"-osrt missing from: {string.Join(" ", args)}");
        Assert.True(maxLenIdx < outputIdx,
            $"--max-len must be part of the structured vector: {string.Join(" ", args)}");
    }

    [Fact]
    public void ConfigDefault_IsOff()
    {
        Assert.Equal(0, new PluginConfiguration().SubtitleMaxLineLength);
    }

    private static int IndexOf(IReadOnlyList<string> args, string value)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}
