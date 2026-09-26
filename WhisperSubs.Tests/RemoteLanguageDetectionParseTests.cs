using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

// Issue #200: the remote probe used to report p = 0 for every answer, so no caller (all gate on
// p >= 0.3) ever trusted it. These pin the verbose_json parse that feeds DetectLanguageAsync.
public class RemoteLanguageDetectionParseTests
{
    [Fact]
    public void WhisperServerProbability_IsRead()
    {
        // Shape of whisper.cpp v1.8.4 whisper-server verbose_json (examples/server/server.cpp).
        const string json = """
            {"task":"transcribe","language":"russian","duration":10.0,"text":"...","segments":[],
             "detected_language":"russian","detected_language_probability":0.912,
             "language_probabilities":{"ru":0.912,"uk":0.05}}
            """;

        var (language, probability) = RemoteWhisperProvider.ParseDetectionResponse(json);

        Assert.Equal("ru", language);
        Assert.Equal(0.912f, probability, 3);
    }

    [Fact]
    public void LowProbability_IsKeptBelowTheForeignThreshold()
    {
        const string json = """{"language":"russian","detected_language":"russian","detected_language_probability":0.12}""";

        var (_, probability) = RemoteWhisperProvider.ParseDetectionResponse(json);

        Assert.Equal(0.12f, probability, 3);
    }

    [Fact]
    public void MissingProbability_FallsBackToTheLocalDefault()
    {
        // OpenAI-style verbose_json, or whisper-server run with --no-language-probabilities.
        const string json = """{"task":"transcribe","language":"english","text":"hi","segments":[]}""";

        var (language, probability) = RemoteWhisperProvider.ParseDetectionResponse(json);

        Assert.Equal("en", language);
        Assert.Equal(0.5f, probability);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"high\"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("-0.2")]
    [InlineData("1.5")]
    public void MalformedProbability_FallsBackToTheLocalDefault(string value)
    {
        var json = $$"""{"language":"english","detected_language":"english","detected_language_probability":{{value}}}""";

        var (language, probability) = RemoteWhisperProvider.ParseDetectionResponse(json);

        Assert.Equal("en", language);
        Assert.Equal(0.5f, probability);
    }

    [Fact]
    public void NumericStringProbability_IsRead()
    {
        const string json = """{"language":"en","detected_language_probability":"0.75"}""";

        var (_, probability) = RemoteWhisperProvider.ParseDetectionResponse(json);

        Assert.Equal(0.75f, probability, 3);
    }

    [Fact]
    public void ProbabilityOfADifferentLanguage_IsNotReused()
    {
        // The probability belongs to detected_language; pairing it with another language would
        // report a confidence nobody measured.
        const string json = """{"language":"english","detected_language":"russian","detected_language_probability":0.9}""";

        var (language, probability) = RemoteWhisperProvider.ParseDetectionResponse(json);

        Assert.Equal("en", language);
        Assert.Equal(0.5f, probability);
    }

    [Fact]
    public void MissingLanguage_StaysAuto()
    {
        var (language, probability) = RemoteWhisperProvider.ParseDetectionResponse("""{"text":""}""");

        Assert.Equal("auto", language);
        Assert.Equal(0.5f, probability);
    }
}
