using WhisperSubs.Configuration;
using WhisperSubs.Controller.Workers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// v4.0 dispatcher: the pure config→job-requirements mapping. Pins that "which jobs need a translate-capable
/// worker" matches the scheduled task's own needsTranslation gate, so a translate pass never routes to a
/// transcribe-only worker, and that the common (translation-off) case requires nothing special.
/// </summary>
public class WorkerJobTests
{
    [Theory]
    [InlineData(SubtitleMode.TranslationOnly, false, true)]  // TranslationOnly always runs a translate pass
    [InlineData(SubtitleMode.TranslationOnly, true, true)]
    [InlineData(SubtitleMode.Full, true, true)]              // full pass + translation enabled
    [InlineData(SubtitleMode.FullAndForced, true, true)]
    [InlineData(SubtitleMode.Full, false, false)]            // translation off ⇒ no translate requirement
    [InlineData(SubtitleMode.FullAndForced, false, false)]
    [InlineData(SubtitleMode.ForcedOnly, true, false)]       // forced-only never runs a full/translate pass
    [InlineData(SubtitleMode.ForcedOnly, false, false)]
    public void TranslationPossible_MatchesTaskGate(SubtitleMode mode, bool enableTranslation, bool expected)
    {
        Assert.Equal(expected, WorkerJob.TranslationPossible(mode, enableTranslation));
    }

    [Fact]
    public void Requirements_TranslateFollowsTranslationPossible_ModelAlwaysNull()
    {
        var needsTranslate = WorkerJob.Requirements(SubtitleMode.TranslationOnly, false);
        Assert.True(needsTranslate.Translate);
        Assert.Null(needsTranslate.RequiredModel);

        var noTranslate = WorkerJob.Requirements(SubtitleMode.Full, false);
        Assert.False(noTranslate.Translate);
        Assert.Null(noTranslate.RequiredModel);
    }
}
