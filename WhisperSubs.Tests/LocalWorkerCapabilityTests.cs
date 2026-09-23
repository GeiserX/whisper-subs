using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Configuration;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// The host's worker must offer a Canary target as soon as the engine is installed and the target is
/// ticked, without a pool rebuild: a long sweep keeps the pool busy, so a rebuild may be hours away.
/// </summary>
public class LocalWorkerCapabilityTests
{
    private sealed class FakeProvider : ISubtitleProvider
    {
        public string Name => "Fake";
        public bool RequiresSpeechAlignmentOptIn => false;
        public Task<string> TranscribeAsync(string audioPath, string language, CancellationToken ct, bool translate = false, string? targetLanguage = null)
            => Task.FromResult(string.Empty);
        public Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken ct)
            => Task.FromResult(("en", 1f));
    }

    private const string Binary = "/data/crispasr/bin/crispasr";
    private const string Model = "/data/crispasr/models/canary-1b-v2-q8_0.gguf";

    [Fact]
    public void CanaryTargets_FollowTheInstall_WithoutRebuildingThePool()
    {
        var files = new HashSet<string>();
        var now = DateTimeOffset.UnixEpoch;
        var check = new CanaryInstallCheck(files.Contains, () => now, TimeSpan.FromMinutes(1));
        var config = new PluginConfiguration();
        var worker = new LocalTranscriptionWorker(new FakeProvider(), () => config, check, _ => null);
        var pool = new WorkerPool(new ITranscriptionWorker[] { worker });

        // Built with nothing installed and no target: English only.
        Assert.False(pool.HasCapableWorker(WorkerJob.ForTarget("es")));
        Assert.True(worker.Capabilities.TranslateTargets.SetEquals(new[] { "en" }));

        // The admin installs the engine and ticks a target. Saving replaces the configuration object,
        // and the install writes new paths, so the very next question sees the engine.
        files.Add(Binary);
        files.Add(Model);
        config = new PluginConfiguration
        {
            CrispAsrBinaryPath = Binary,
            CanaryModelPath = Model,
            TranslationTargetLanguages = new List<string> { "es" },
        };
        Assert.True(pool.HasCapableWorker(WorkerJob.ForTarget("es")));
        Assert.Equal(TargetLeasePolicy.UseOwnWorker, TargetLeaseRouting.Decide(worker.Capabilities.TranslateTargets, "es"));
        Assert.False(pool.HasCapableWorker(WorkerJob.ForTarget("nl")));   // not ticked

        // The files go away: the cached verdict stands until the interval, then the target is gone.
        files.Clear();
        Assert.True(pool.HasCapableWorker(WorkerJob.ForTarget("es")));
        now += TimeSpan.FromMinutes(1);
        Assert.False(pool.HasCapableWorker(WorkerJob.ForTarget("es")));

        // Whole titles and English never depended on the engine.
        Assert.True(pool.HasCapableWorker(WorkerJob.Requirements(SubtitleMode.Full, enableTranslation: true)));
        Assert.Contains("en", worker.Capabilities.TranslateTargets);
    }

    [Fact]
    public void InstallCheck_CachesWithinTheInterval_AndRechecksANewPathAtOnce()
    {
        var calls = 0;
        var files = new HashSet<string> { Binary, Model };
        var now = DateTimeOffset.UnixEpoch;
        var check = new CanaryInstallCheck(p => { calls++; return files.Contains(p); }, () => now, TimeSpan.FromMinutes(1));

        Assert.True(check.IsInstalled(Binary, Model));
        var afterFirst = calls;
        Assert.True(check.IsInstalled(Binary, Model));
        Assert.Equal(afterFirst, calls);                  // cached: no file check per scheduling decision

        Assert.False(check.IsInstalled(Binary, "/elsewhere/model.gguf"));   // a new path is checked now
        Assert.False(check.IsInstalled("", ""));
    }
}
