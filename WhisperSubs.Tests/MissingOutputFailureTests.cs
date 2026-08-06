using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Tests for <see cref="WhisperProvider.DescribeMissingOutputFailure"/> (issue #153).
///
/// whisper.cpp's argument parser prints <c>error: unknown argument: X</c>, dumps the usage text, and
/// then calls <c>exit(0)</c> — a SUCCESS code. So a fatal config mistake never reaches the
/// non-zero-exit branch: the captured stderr was thrown away and the user saw only "Subtitle file not
/// found at expected location", which names the symptom and hides the cause. The reporter burned all
/// four attempts on it without ever learning which flag was wrong.
/// </summary>
public class MissingOutputFailureTests
{
    // whisper-cli v1.8.4's real output for the flag in the report, captured from the binary.
    private const string UnknownArgumentStderr =
        "error: unknown argument: --word_timestamps\n" +
        "\n" +
        "usage: /config/whisper/whisper-cli [options] file0 file1 ...\n" +
        "supported audio formats: flac, mp3, ogg, wav\n";

    [Fact]
    public void UnknownArgument_NamesTheOffendingFlag()
    {
        var message = WhisperProvider.DescribeMissingOutputFailure(UnknownArgumentStderr);

        Assert.NotNull(message);
        // The whole point: the user must learn WHICH flag killed the run.
        Assert.Contains("--word_timestamps", message);
    }

    [Fact]
    public void UnknownArgument_PointsAtTheSettingThatCarriesIt()
    {
        // A correct diagnosis the user cannot act on is still a bad error. Custom Whisper Arguments is
        // the only place a stray flag can come from, so the message must name it.
        var message = WhisperProvider.DescribeMissingOutputFailure(UnknownArgumentStderr);

        Assert.Contains("Custom Whisper Arguments", message!);
    }

    [Theory]
    [InlineData("--word_timestamps")]
    [InlineData("--beam_size")]
    [InlineData("-XYZ")]
    public void UnknownArgument_ExtractsWhicheverFlagWasRejected(string flag)
    {
        var message = WhisperProvider.DescribeMissingOutputFailure($"error: unknown argument: {flag}\n");

        Assert.NotNull(message);
        Assert.Contains(flag, message);
    }

    [Fact]
    public void OtherErrorLine_IsSurfacedVerbatim()
    {
        // For an error we have not characterised, repeat whisper-cli's own words rather than inventing
        // a diagnosis — a wrong-but-confident message is worse than a raw one.
        var message = WhisperProvider.DescribeMissingOutputFailure(
            "whisper_init_from_file_with_params_no_state: loading model\n" +
            "error: failed to initialize whisper context\n");

        Assert.NotNull(message);
        Assert.Contains("failed to initialize whisper context", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoStderr_ReturnsNull_SoTheCallerKeepsItsOwnMessage(string? stderr)
    {
        // Null means "I have nothing to add" and the caller falls back to the path-not-found error.
        Assert.Null(WhisperProvider.DescribeMissingOutputFailure(stderr));
    }

    [Fact]
    public void HealthyChatterWithoutAnErrorLine_ReturnsNull()
    {
        // whisper-cli writes ALL its normal progress to stderr, so a run that merely produced no file
        // for some other reason must NOT be dressed up as an error that whisper never reported.
        var chatter =
            "whisper_init_from_file_with_params_no_state: loading model from 'ggml-large-v3-turbo.bin'\n" +
            "whisper_model_load: model size = 1620.00 MB\n" +
            "system_info: n_threads = 4 | AVX = 1 | AVX2 = 1\n" +
            "whisper_print_progress_callback: progress =  100%\n";

        Assert.Null(WhisperProvider.DescribeMissingOutputFailure(chatter));
    }

    [Fact]
    public void ErrorMatchIsAnchoredToItsOwnLine_NotMatchedMidSentence()
    {
        // "error:" appearing inside a normal log line must not be mistaken for a reported failure.
        var chatter = "whisper_model_load: no error: model loaded cleanly\n";

        Assert.Null(WhisperProvider.DescribeMissingOutputFailure(chatter));
    }
}
