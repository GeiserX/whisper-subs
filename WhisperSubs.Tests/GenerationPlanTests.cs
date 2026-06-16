using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Locks the issue #83 gating truth table — which passes run for a given mode + toggles + force.
/// This is the pure core of <c>SubtitleManager.GenerateSubtitleAsync</c>, extracted precisely so
/// the decision can be tested without <c>Plugin.Instance</c>.
/// </summary>
public class GenerationPlanTests
{
    // ---- Original-language (full transcription) pass --------------------------------------------

    [Theory]
    [InlineData(SubtitleMode.Full)]
    [InlineData(SubtitleMode.FullAndForced)]
    public void Original_RunsInFullModes_WhenEnabled(SubtitleMode mode)
    {
        var plan = SubtitleManager.ResolveGenerationPlan(mode, generateOriginalLanguage: true, enableTranslation: false, force: false);
        Assert.True(plan.OriginalPassApplies);
    }

    [Theory]
    [InlineData(SubtitleMode.Full)]
    [InlineData(SubtitleMode.FullAndForced)]
    public void Original_Skipped_WhenDisabledAndNotForced(SubtitleMode mode)
    {
        var plan = SubtitleManager.ResolveGenerationPlan(mode, generateOriginalLanguage: false, enableTranslation: false, force: false);
        Assert.False(plan.OriginalPassApplies);
    }

    [Theory]
    [InlineData(SubtitleMode.Full)]
    [InlineData(SubtitleMode.FullAndForced)]
    public void Original_Force_OverridesDisabledToggle(SubtitleMode mode)
    {
        // The "manual single-item Generate always transcribes" contract.
        var plan = SubtitleManager.ResolveGenerationPlan(mode, generateOriginalLanguage: false, enableTranslation: false, force: true);
        Assert.True(plan.OriginalPassApplies);
    }

    [Theory]
    [InlineData(SubtitleMode.ForcedOnly)]
    [InlineData(SubtitleMode.TranslationOnly)]
    public void Original_NeverRunsOutsideFullModes_EvenForced(SubtitleMode mode)
    {
        // force makes the user "want" original, but ForcedOnly/TranslationOnly have no full pass.
        var plan = SubtitleManager.ResolveGenerationPlan(mode, generateOriginalLanguage: true, enableTranslation: true, force: true);
        Assert.False(plan.OriginalPassApplies);
    }

    // ---- Forced pass (governed by mode only) ----------------------------------------------------

    [Theory]
    [InlineData(SubtitleMode.ForcedOnly, true)]
    [InlineData(SubtitleMode.FullAndForced, true)]
    [InlineData(SubtitleMode.Full, false)]
    [InlineData(SubtitleMode.TranslationOnly, false)]
    public void Forced_RunsOnlyInForcedModes(SubtitleMode mode, bool expected)
    {
        // Toggles must not influence the forced pass — only the mode does.
        var plan = SubtitleManager.ResolveGenerationPlan(mode, generateOriginalLanguage: false, enableTranslation: false, force: false);
        Assert.Equal(expected, plan.ForcedPassApplies);
    }

    // ---- Translation pass -----------------------------------------------------------------------

    [Fact]
    public void Translation_TranslationOnly_AlwaysApplies_RegardlessOfEnableTranslation()
    {
        Assert.True(SubtitleManager.ResolveGenerationPlan(SubtitleMode.TranslationOnly, false, enableTranslation: false, force: false).TranslationApplies);
        Assert.True(SubtitleManager.ResolveGenerationPlan(SubtitleMode.TranslationOnly, false, enableTranslation: true, force: false).TranslationApplies);
    }

    [Theory]
    [InlineData(SubtitleMode.Full, true, true)]
    [InlineData(SubtitleMode.FullAndForced, true, true)]
    [InlineData(SubtitleMode.Full, false, false)]
    [InlineData(SubtitleMode.FullAndForced, false, false)]
    public void Translation_InFullModes_FollowsEnableTranslation(SubtitleMode mode, bool enableTranslation, bool expected)
    {
        var plan = SubtitleManager.ResolveGenerationPlan(mode, generateOriginalLanguage: true, enableTranslation, force: false);
        Assert.Equal(expected, plan.TranslationApplies);
    }

    [Fact]
    public void Translation_ForcedOnly_NeverApplies_EvenWithEnableTranslation()
    {
        // The subtle case: ForcedOnly has no full pass, so translation must not fire even when
        // EnableTranslation is on. (It's only excluded because fullPassApplies is false.)
        var plan = SubtitleManager.ResolveGenerationPlan(SubtitleMode.ForcedOnly, generateOriginalLanguage: true, enableTranslation: true, force: false);
        Assert.False(plan.TranslationApplies);
    }

    [Fact]
    public void Translation_Force_DoesNotForceTranslation()
    {
        // force is about transcription (manual Generate), not translation. In Full mode with
        // translation off, force must not switch translation on.
        var plan = SubtitleManager.ResolveGenerationPlan(SubtitleMode.Full, generateOriginalLanguage: true, enableTranslation: false, force: true);
        Assert.False(plan.TranslationApplies);
    }

    // ---- Default-config snapshot (existing users, untouched toggles) -----------------------------

    [Fact]
    public void DefaultConfig_Full_TranscribesOriginal_NoTranslation()
    {
        // Mirrors a fresh PluginConfiguration: original-language on, translation off, mode Full.
        var cfg = new PluginConfiguration();
        var plan = SubtitleManager.ResolveGenerationPlan(
            cfg.SubtitleMode,
            generateOriginalLanguage: cfg.GenerateOriginalLanguageSubtitles,
            enableTranslation: cfg.EnableTranslation,
            force: false);

        Assert.True(plan.OriginalPassApplies);   // existing behavior preserved
        Assert.False(plan.TranslationApplies);   // translation stays opt-in
        Assert.False(plan.ForcedPassApplies);
    }
}
