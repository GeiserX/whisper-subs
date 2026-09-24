using WhisperSubs.Controller;
using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>Issue #112: request state-machine predicates + user-input language validation.</summary>
public class RequestPolicyTests
{
    [Theory]
    [InlineData(RequestState.Pending, true)]
    [InlineData(RequestState.Queued, true)]
    [InlineData(RequestState.Completed, false)]
    [InlineData(RequestState.Declined, false)]
    [InlineData(RequestState.Failed, false)]
    public void IsActive_TrueOnlyForPendingOrQueued(RequestState state, bool expected)
        => Assert.Equal(expected, RequestPolicy.IsActive(state));

    [Theory]
    [InlineData(RequestState.Pending, false)]
    [InlineData(RequestState.Queued, false)]
    [InlineData(RequestState.Completed, true)]
    [InlineData(RequestState.Declined, true)]
    [InlineData(RequestState.Failed, true)]
    public void IsTerminal_TrueOnlyForFinalStates(RequestState state, bool expected)
        => Assert.Equal(expected, RequestPolicy.IsTerminal(state));
}

public class RequestValidationTests
{
    [Theory]
    [InlineData("auto", "es", "auto")]
    [InlineData("AUTO", "es", "auto")]
    [InlineData("en", "es", "en")]
    [InlineData("ES", "en", "es")]     // normalised to lowercase
    [InlineData("  fr  ", "en", "fr")] // trimmed
    public void NormalizeLanguage_AcceptsAutoAndTwoLetterCodes(string input, string fallback, string expected)
        => Assert.Equal(expected, RequestValidation.NormalizeLanguage(input, fallback));

    [Fact]
    public void NormalizeLanguage_EmptyInput_FallsBackToConfigDefault()
    {
        Assert.Equal("auto", RequestValidation.NormalizeLanguage(null, "auto"));
        Assert.Equal("es", RequestValidation.NormalizeLanguage("", "es"));
        Assert.Equal("es", RequestValidation.NormalizeLanguage("   ", "es"));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("en/../..")]
    [InlineData("english")]  // 7 letters, not ISO 639-1
    [InlineData("e")]        // one letter
    [InlineData("e1")]       // not both letters
    [InlineData(".sh")]
    [InlineData("ññ")]       // 2 Unicode letters, but not ASCII a-z (char.IsLetter would have passed this)
    public void NormalizeLanguage_RejectsUnsupportedOrMalicious(string input)
        => Assert.Null(RequestValidation.NormalizeLanguage(input, "auto"));

    [Fact]
    public void NormalizeLanguage_EmptyFallback_WithEmptyInput_IsNull()
        => Assert.Null(RequestValidation.NormalizeLanguage(null, ""));
}

/// <summary>Per-title translation: the target allow-list that guards a value bound for a file name.</summary>
public class TranslationTargetValidationTests
{
    [Theory]
    [InlineData("es", "es")]
    [InlineData(" ES ", "es")]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("uk", "uk")]
    public void NormalizeTranslationTarget_AcceptsEnglishAndCanaryTargets(string input, string expected)
        => Assert.Equal(expected, RequestValidation.NormalizeTranslationTarget(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("eng")]
    [InlineData("zh")]
    [InlineData("xx")]
    [InlineData("../es")]
    [InlineData("es/")]
    [InlineData("e s")]
    public void NormalizeTranslationTarget_RejectsEverythingElse(string? input)
        => Assert.Null(RequestValidation.NormalizeTranslationTarget(input));

    [Fact]
    public void NormalizeTranslationTarget_ReturnsTheCatalogsOwnString()
    {
        var input = new string(new[] { 'E', 'S' });
        var result = RequestValidation.NormalizeTranslationTarget(input);
        Assert.Same(CanaryCatalog.Targets.Single(t => t.Code == "es").Code, result);
        Assert.NotSame(input, result);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("auto", true)]
    [InlineData("AUTO", true)]
    [InlineData("en", false)]
    [InlineData("es", false)]
    public void IsTranslationLanguageAllowed_OnlyAutoOrBlank(string? language, bool expected)
        => Assert.Equal(expected, RequestValidation.IsTranslationLanguageAllowed(language));

    [Fact]
    public void RequestableTargets_EnglishFirst_ThenTheCanaryList_EveryCodeAccepted()
    {
        var list = CanaryCatalog.RequestableTargets;
        Assert.Equal(25, list.Count);
        Assert.Equal("en", list[0].Code);
        Assert.Equal(CanaryCatalog.Targets.Select(t => t.Code), list.Skip(1).Select(t => t.Code));
        Assert.All(list, t => Assert.Equal(t.Code, RequestValidation.NormalizeTranslationTarget(t.Code)));
    }
}
