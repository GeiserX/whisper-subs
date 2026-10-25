using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Controller;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// The akou-first install as a new user meets it: the documented akou start must actually start, a title an
/// akou row cannot take must go to another worker before it is skipped, and akou cues must stay readable.
/// </summary>
public class AkouReviewFixesTests
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

    private static ITranscriptionWorker Akou(string id = "akou", params string[] targets) => new TranscriptionWorker(id, id, new FakeProvider(),
        new WorkerCapabilities { IsLocal = false, HostAssisted = true, TranslateTargets = WorkerTargets.Set(targets) });

    private static ITranscriptionWorker Whisper(string id = "whisper", double cost = 5) => new TranscriptionWorker(id, id, new FakeProvider(),
        new WorkerCapabilities { IsLocal = false, CostWeight = cost, TranslateTargets = WorkerTargets.EnglishOnly });

    // ---- Documented akou start ----

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WhisperSubs.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("Web/configPage.html")]
    public void EveryDocumentedAkouDockerRun_SaysItIsBehindAProxy(string file)
    {
        // akou 1.2.4 binds 0.0.0.0 in its image and refuses to start there (exit 78) unless
        // server.behind_proxy is true, which AKOU_BEHIND_PROXY=true sets.
        var text = RepoFile(file).Replace("\\\n", " ");
        var runs = Regex.Matches(text, @"docker run[^\n`]*drumsergio/akou:[^\s`<]+").Select(m => m.Value).ToList();
        Assert.NotEmpty(runs);
        Assert.All(runs, run => Assert.Contains("-e AKOU_BEHIND_PROXY=true", run));
        // No port published on every interface: Jellyfin reaches akou over the shared network.
        Assert.All(runs, run => Assert.DoesNotContain("-p 8476:8476", run));
        Assert.All(runs, run => Assert.Contains("--network", run));
    }

    [Fact]
    public void TheComposeAkouService_SaysItIsBehindAProxy()
    {
        var text = RepoFile("docs/install-topologies.md");
        var service = Regex.Match(text, @"services:\s*\n\s*akou:[\s\S]*?\n```").Value;
        Assert.Contains("image: drumsergio/akou:", service);
        Assert.Contains("- AKOU_BEHIND_PROXY=true", service);
    }

    [Fact]
    public void BadHost_PointsAtTheProxySetting()
    {
        var result = AkouServerCheck.Evaluate(403, """{"error":"bad_host"}""", null, null);
        Assert.False(result.Ok);
        Assert.Contains("AKOU_BEHIND_PROXY=true", result.Message);
    }

    // ---- A language an akou row cannot take ----

    [Fact]
    public void AnyLanguage_SkipsHostAssistedRows_Only()
    {
        Assert.False(WorkerScheduling.CanServe(new WorkerSlot("akou", true, 0, Akou().Capabilities), WorkerJob.AnyLanguage));
        Assert.True(WorkerScheduling.CanServe(new WorkerSlot("whisper", true, 0, Whisper().Capabilities), WorkerJob.AnyLanguage));
        // A plain item job still goes to either (positive control).
        Assert.True(WorkerScheduling.CanServe(new WorkerSlot("akou", true, 0, Akou().Capabilities), new JobRequirements(null, null)));
    }

    [Fact]
    public void ThePool_SaysWhetherAnyWorkerTakesEveryLanguage()
    {
        Assert.False(new WorkerPool(new[] { Akou("a1"), Akou("a2") }).HasCapableWorker(WorkerJob.AnyLanguage));
        Assert.True(new WorkerPool(new[] { Akou(), Whisper() }).HasCapableWorker(WorkerJob.AnyLanguage));
    }

    [Fact]
    public async Task AnItemOnAnAkouRow_BorrowsAWorkerThatTakesTheLanguage()
    {
        var pool = new WorkerPool(new[] { Akou(), Whisper() });
        var lease = pool.TryAcquire(new JobRequirements(null, null))!.Value;
        Assert.Equal("akou", lease.Worker.Id);   // the cheaper worker took the title
        var engines = new PoolTargetEngines(pool, lease);

        var other = await engines.AcquireAnyLanguageAsync("Film", CancellationToken.None);

        Assert.NotNull(other);
        Assert.Equal("whisper", other!.WorkerName);
        Assert.Null(pool.TryAcquire(WorkerJob.AnyLanguage));   // held until the pass ends
        other.Dispose();
        var again = pool.TryAcquire(WorkerJob.AnyLanguage);
        Assert.NotNull(again);
        pool.Release(again!.Value.Key);
        pool.Release(lease.Key);
    }

    [Fact]
    public async Task AnItemOnAnAkouRow_WithNoOtherWorker_GetsNone_SoTheTitleIsSkipped()
    {
        var pool = new WorkerPool(new[] { Akou("a1"), Akou("a2") });
        var lease = pool.TryAcquire(new JobRequirements(null, null))!.Value;
        Assert.Null(await new PoolTargetEngines(pool, lease).AcquireAnyLanguageAsync("Film", CancellationToken.None));
        pool.Release(lease.Key);
    }

    [Fact]
    public async Task AnItemOnARowWithCanaryTargets_TakesOnlyAFreeWorker()
    {
        var pool = new WorkerPool(new[] { Akou("akou", "nl"), Whisper() });
        var lease = pool.TryAcquire(new JobRequirements(null, null))!.Value;
        var busy = pool.TryAcquire(WorkerJob.AnyLanguage)!.Value;   // another title holds the Whisper row

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new PoolTargetEngines(pool, lease).AcquireAnyLanguageAsync("Film", CancellationToken.None));
        Assert.Contains("next run tries again", ex.Message);
        pool.Release(busy.Key);
        pool.Release(lease.Key);
    }

    [Fact]
    public void MayWait_OnlyWithoutCanaryTargets()
    {
        Assert.True(TargetLeaseRouting.MayWait(WorkerTargets.Set()));
        Assert.True(TargetLeaseRouting.MayWait(WorkerTargets.Set("en")));
        Assert.False(TargetLeaseRouting.MayWait(WorkerTargets.Set("en", "nl")));
    }

    [Fact]
    public void TheDefaultEngines_HaveNoOtherWorker()
    {
        ITranslationTargetEngines engines = new NoPoolEngines();
        Assert.Null(engines.AcquireAnyLanguageAsync("Film", CancellationToken.None).Result);
    }

    private sealed class NoPoolEngines : ITranslationTargetEngines
    {
        public bool CanServe(string target) => false;
        public bool SkipUnservedTargets => false;
        public bool? LocalWorkerInPool => null;
        public Task<TargetEngineLease> AcquireAsync(string target, string itemName, CancellationToken cancellationToken)
            => throw new InvalidOperationException();
    }

    // ---- Cue split ----

    [Fact]
    public void Split_JoinsAShortSentence_ToItsNeighbour()
    {
        // "No." then one long sentence of about 155 characters, 10 s in all: the sentence break alone gave
        // "No." 0.19 s.
        var sentence = string.Join(' ', Enumerable.Repeat("words", 25)) + " plus more.";
        var cue = (0.0, 10.0, "No. " + sentence);

        var cues = AkouCueSplit.Split(new[] { cue });

        Assert.True(cues.Count >= 2);
        Assert.All(cues, c => Assert.True(c.End - c.Start >= AkouCueSplit.MinCueSeconds, $"{c.Text}: {c.End - c.Start:F2}s"));
        Assert.StartsWith("No. words", cues[0].Text);
        Assert.Equal("No. " + sentence, string.Join(' ', cues.Select(c => c.Text)));
        Assert.Equal(0.0, cues[0].Start);
        Assert.Equal(10.0, cues[^1].End);
    }

    [Fact]
    public void Split_KeepsSentencesThatAreLongEnough()
    {
        // Positive control: two sentences of a few seconds each stay two cues at the sentence end.
        var cue = (0.0, 12.0, "This is the first sentence and it has a fair length to it. " +
                              "And this is the second sentence, which is about as long.");
        var cues = AkouCueSplit.Split(new[] { cue });
        Assert.Equal(2, cues.Count);
        Assert.EndsWith("to it.", cues[0].Text);
    }
}
