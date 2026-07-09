using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// whisper-subs-9gq: the workers-signature guard that lets ReconcileWorkers skip a provider rebuild when the
/// configured worker set did not actually change — so an unrelated config save (e.g. toggling PauseOnPlayback)
/// does not needlessly news up RemoteWhisperProvider/HttpClient instances, while any real worker change (add,
/// concurrency, URL/model) flips the signature and triggers the reconcile.
/// </summary>
public class WorkerSignatureTests
{
    [Fact]
    public void Signature_UnrelatedConfigChange_IsStable()
    {
        var a = new PluginConfiguration();
        var b = new PluginConfiguration { PauseOnPlayback = true, WhisperThreadCount = 8, EnableTranslation = true };

        // No worker fields changed → identical signature → ReconcileWorkers no-ops (no rebuild).
        Assert.Equal(
            SubtitleQueueService.ComputeWorkersSignature(a),
            SubtitleQueueService.ComputeWorkersSignature(b));
    }

    [Fact]
    public void Signature_ChangesWhenAWorkerIsAdded()
    {
        var before = new PluginConfiguration();
        var after = new PluginConfiguration();
        after.Workers.Add(new WhisperWorker { Id = "mini", ApiUrl = "http://192.168.1.10:8080", MaxConcurrency = 2 });

        Assert.NotEqual(
            SubtitleQueueService.ComputeWorkersSignature(before),
            SubtitleQueueService.ComputeWorkersSignature(after));
    }

    [Fact]
    public void Signature_ChangesWhenWorkerConcurrencyChanges()
    {
        var one = new PluginConfiguration();
        one.Workers.Add(new WhisperWorker { Id = "mini", ApiUrl = "http://x:8080", MaxConcurrency = 1 });
        var four = new PluginConfiguration();
        four.Workers.Add(new WhisperWorker { Id = "mini", ApiUrl = "http://x:8080", MaxConcurrency = 4 });

        Assert.NotEqual(
            SubtitleQueueService.ComputeWorkersSignature(one),
            SubtitleQueueService.ComputeWorkersSignature(four));
    }

    [Fact]
    public void Signature_ChangesWhenEnableLocalWorkerToggles()
    {
        var withLocal = new PluginConfiguration { EnableLocalWorker = true };
        withLocal.Workers.Add(new WhisperWorker { Id = "mini", ApiUrl = "http://x:8080" });
        var withoutLocal = new PluginConfiguration { EnableLocalWorker = false };
        withoutLocal.Workers.Add(new WhisperWorker { Id = "mini", ApiUrl = "http://x:8080" });

        Assert.NotEqual(
            SubtitleQueueService.ComputeWorkersSignature(withLocal),
            SubtitleQueueService.ComputeWorkersSignature(withoutLocal));
    }

    [Fact]
    public void Signature_IgnoresDisabledAndBlankWorkerRows()
    {
        // A disabled or URL-less row contributes NO worker to the pool (BuildWorkers filters it), so it must
        // not alter the signature. Both configs have a non-empty Workers list (⇒ ExplicitList plan) but no
        // effective workers, so their signatures must match.
        var twoNoise = new PluginConfiguration { EnableLocalWorker = true };
        twoNoise.Workers.Add(new WhisperWorker { Id = "off", ApiUrl = "http://x:8080", Enabled = false });
        twoNoise.Workers.Add(new WhisperWorker { Id = "blank", ApiUrl = "", Enabled = true });

        var oneNoise = new PluginConfiguration { EnableLocalWorker = true };
        oneNoise.Workers.Add(new WhisperWorker { Id = "off2", ApiUrl = "http://y:9090", Enabled = false });

        Assert.Equal(
            SubtitleQueueService.ComputeWorkersSignature(twoNoise),
            SubtitleQueueService.ComputeWorkersSignature(oneNoise));
    }
}
