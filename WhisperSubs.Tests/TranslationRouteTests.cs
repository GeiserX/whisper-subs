using System;
using WhisperSubs.Controller;
using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

public class TranslationRouteTests
{
    // English is always Whisper, whatever the source and whether or not Canary is installed.
    [Theory]
    [InlineData("es", true)]
    [InlineData("es", false)]
    [InlineData("en", true)]
    [InlineData("auto", false)]
    [InlineData(null, false)]
    public void EnglishTarget_IsAlwaysWhisper(string? source, bool canaryAvailable)
    {
        Assert.Equal(TranslationEngine.Whisper, TranslationRoute.Decide(source, "en", canaryAvailable).Engine);
        Assert.Equal(TranslationEngine.Whisper, TranslationRoute.Decide(source, " EN ", canaryAvailable).Engine);
    }

    [Fact]
    public void EnglishAudio_ToCanaryTarget_WithCanary_IsCanary()
    {
        var decision = TranslationRoute.Decide("en", "nl", canaryAvailable: true);
        Assert.Equal(TranslationEngine.Canary, decision.Engine);
        Assert.Contains("'nl'", decision.Reason);
    }

    // The model card excludes Latvian only from a benchmark comparison (competing models lack it);
    // English to Latvian is evaluated like every other pair, so lv is an ordinary Canary target.
    [Fact]
    public void Latvian_IsAnOrdinaryCanaryTarget()
    {
        Assert.Equal(TranslationEngine.Canary, TranslationRoute.Decide("en", "lv", canaryAvailable: true).Engine);
        Assert.Equal(TranslationEngine.EngineMissing, TranslationRoute.Decide("en", "lv", canaryAvailable: false).Engine);
    }

    [Fact]
    public void EveryCatalogTarget_RoutesToCanaryFromEnglish()
    {
        foreach (var target in CanaryCatalog.Targets)
        {
            Assert.Equal(TranslationEngine.Canary, TranslationRoute.Decide("EN", target.Code.ToUpperInvariant(), canaryAvailable: true).Engine);
        }
    }

    [Fact]
    public void EnglishAudio_CanaryNotInstalled_IsEngineMissingWithSetupHint()
    {
        var decision = TranslationRoute.Decide("en", "de", canaryAvailable: false);
        Assert.Equal(TranslationEngine.EngineMissing, decision.Engine);
        Assert.Contains("not installed", decision.Reason);
        Assert.Contains("CrispASR worker", decision.Reason);
        Assert.DoesNotContain("not a worker", decision.Reason);
    }

    // Installed, but "Also use this server as a worker" is off and no row lists the target.
    [Fact]
    public void EngineMissing_InstalledLocalWorkerOff_NamesTheOption()
    {
        var reason = TranslationRoute.Decide("en", "nl", canaryAvailable: false, LocalCanaryState.LocalWorkerOff).Reason;
        Assert.Contains("installed on this server", reason);
        Assert.Contains("\"Also use this server as a worker\" is off", reason);
        Assert.Contains("CrispASR server row that lists 'nl'", reason);
        Assert.DoesNotContain("not installed", reason);
    }

    // Installed, but a single legacy Remote API URL makes the pool remote-only.
    [Fact]
    public void EngineMissing_InstalledLegacyRemoteUrl_NamesTheRemoteUrl()
    {
        var reason = TranslationRoute.Decide("en", "nl", canaryAvailable: false, LocalCanaryState.LegacyRemoteOnly).Reason;
        Assert.Contains("installed on this server", reason);
        Assert.Contains("single Remote API URL", reason);
        Assert.DoesNotContain("is off", reason);
        Assert.DoesNotContain("not installed", reason);
    }

    // The live bug: this server WAS a worker, and the old message said it was not.
    [Fact]
    public void EngineMissing_NeverSaysNotAWorkerWhenThisServerIsOne()
    {
        foreach (var state in Enum.GetValues<LocalCanaryState>())
        {
            Assert.DoesNotContain("not a worker", TranslationRoute.Decide("en", "nl", false, state).Reason);
        }
        Assert.Contains("which is a worker in the pool", TranslationRoute.Decide("en", "nl", false, LocalCanaryState.InPool).Reason);
        // An available engine wins whatever the local state says.
        Assert.Equal(TranslationEngine.Canary, TranslationRoute.Decide("en", "nl", true, LocalCanaryState.LocalWorkerOff).Engine);
    }

    // A pool built from these same settings: the state agrees with the rule the registry builds from.
    [Theory]
    [InlineData(false, 0, 0, false, true, LocalCanaryState.NotInstalled)]
    [InlineData(false, 2, 2, false, false, LocalCanaryState.NotInstalled)]   // missing files come first
    [InlineData(true, 0, 0, false, true, LocalCanaryState.InPool)]           // default single server
    [InlineData(true, 0, 0, false, false, LocalCanaryState.InPool)]          // the toggle only matters with rows
    [InlineData(true, 2, 2, false, true, LocalCanaryState.InPool)]
    [InlineData(true, 2, 2, false, false, LocalCanaryState.LocalWorkerOff)]
    [InlineData(true, 2, 0, false, false, LocalCanaryState.InPool)]          // every row disabled: the pool falls back to local
    [InlineData(true, 0, 0, true, true, LocalCanaryState.LegacyRemoteOnly)]
    [InlineData(true, 2, 2, true, true, LocalCanaryState.InPool)]            // rows win over the legacy URL
    public void LocalCanary_MatchesThePoolComposition(
        bool installed, int rows, int usableRows, bool legacyUrl, bool enableLocal, LocalCanaryState expected)
    {
        var builtWithLocal = Controller.Workers.WorkerPlan.HostsLocal(rows, usableRows, legacyUrl, enableLocal);
        Assert.Equal(expected, TranslationRoute.LocalCanary(installed, builtWithLocal, rows, usableRows, legacyUrl, enableLocal));
    }

    // Without a pool (null) the only engine is this server's install as read when the pass began. A
    // failure there means it was not installed then, even if the files have appeared since; the message
    // must not mention a pool worker, the local-worker option or a rebuild, none of which exist here.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void LocalCanary_NoPool_IsNotInstalledWhateverTheSettings(bool enableLocal, bool legacyUrl)
    {
        var state = TranslationRoute.LocalCanary(installed: true, localWorkerInPool: null, 2, 2, legacyUrl, enableLocal);
        Assert.Equal(LocalCanaryState.NotInstalled, state);
        var reason = TranslationRoute.Decide("en", "nl", false, state).Reason;
        Assert.DoesNotContain("pool", reason);
        Assert.DoesNotContain("is off", reason);
        Assert.DoesNotContain("Remote API URL", reason);
    }

    // The pool is fixed when built; the settings may have changed since. The running pool wins.
    [Fact]
    public void LocalCanary_RunningPoolWinsOverChangedSettings()
    {
        // Built with this server, the local-worker option since turned off: still in the pool.
        Assert.Equal(LocalCanaryState.InPool, TranslationRoute.LocalCanary(true, true, 2, 2, false, enableLocalWorker: false));

        // Built without it, the option since turned on: the pool predates the change. Not "is off".
        var pending = TranslationRoute.LocalCanary(true, false, 2, 2, false, enableLocalWorker: true);
        Assert.Equal(LocalCanaryState.PendingPoolRebuild, pending);
        var reason = TranslationRoute.Decide("en", "nl", false, pending).Reason;
        Assert.Contains("built before that change", reason);
        Assert.DoesNotContain("is off", reason);
        Assert.DoesNotContain("within a minute", reason);
    }

    // A non-English source to a non-English target is the pair the spike showed failing silently.
    // It is the normal case for most of a library, so it skips instead of failing the item.
    [Theory]
    [InlineData("fr", "es", true)]
    [InlineData("es", "fr", true)]
    [InlineData("es", "de", true)]
    [InlineData("es", "nl", false)]   // a missing engine does not matter when the audio is not English
    public void NonEnglishAudio_ToCanaryTarget_IsSkipped(string source, string target, bool canaryAvailable)
    {
        var decision = TranslationRoute.Decide(source, target, canaryAvailable);
        Assert.Equal(TranslationEngine.SkipSourceNotEnglish, decision.Engine);
        Assert.Contains($"The audio is '{source}'", decision.Reason);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownSource_ToCanaryTarget_IsSkipped(string? source)
    {
        var decision = TranslationRoute.Decide(source, "nl", canaryAvailable: true);
        Assert.Equal(TranslationEngine.SkipSourceNotEnglish, decision.Engine);
        Assert.Contains("could not be determined", decision.Reason);
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("zh")]
    [InlineData("xx")]
    public void TargetOutsideTheCatalog_IsUnsupported(string target)
    {
        // Config corruption stays an error whatever the audio language or install state.
        foreach (var (source, canary) in new[] { ("en", true), ("es", true), ("en", false) })
        {
            var decision = TranslationRoute.Decide(source, target, canary);
            Assert.Equal(TranslationEngine.Unsupported, decision.Engine);
            Assert.Contains("not a language", decision.Reason);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void MissingTarget_IsUnsupported(string? target)
    {
        Assert.Equal(TranslationEngine.Unsupported, TranslationRoute.Decide("en", target, canaryAvailable: true).Engine);
    }
}
