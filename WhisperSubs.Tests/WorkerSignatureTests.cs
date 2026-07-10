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

    [Fact]
    public void Signature_TwoEqualWorkerConfigs_ProduceEqualSignature_SoDoubleSaveIsNoOp()
    {
        // Double-save no-op: two separately-built but value-equal configs must yield the SAME signature, so a
        // repeated config save with nothing changed skips the provider-constructing rebuild in ReconcileWorkers.
        var a = new PluginConfiguration();
        a.Workers.Add(new WhisperWorker { Id = "a", ApiUrl = "http://x:8080", MaxConcurrency = 2, CostWeight = 1.5 });
        a.Workers.Add(new WhisperWorker { Id = "b", ApiUrl = "http://y:9090", MaxConcurrency = 1 });

        var b = new PluginConfiguration();
        b.Workers.Add(new WhisperWorker { Id = "a", ApiUrl = "http://x:8080", MaxConcurrency = 2, CostWeight = 1.5 });
        b.Workers.Add(new WhisperWorker { Id = "b", ApiUrl = "http://y:9090", MaxConcurrency = 1 });

        Assert.Equal(
            SubtitleQueueService.ComputeWorkersSignature(a),
            SubtitleQueueService.ComputeWorkersSignature(b));
    }

    [Fact]
    public void Signature_IsOrderInsensitive_RowReorderProducesSameSignature()
    {
        // L1: merely reordering worker rows must NOT change the signature (else a no-op save runs a needless,
        // HttpClient-churning rebuild). Same workers, different row order → identical signature.
        var ab = new PluginConfiguration();
        ab.Workers.Add(new WhisperWorker { Id = "a", ApiUrl = "http://x:8080", MaxConcurrency = 2 });
        ab.Workers.Add(new WhisperWorker { Id = "b", ApiUrl = "http://y:9090", MaxConcurrency = 1 });

        var ba = new PluginConfiguration();
        ba.Workers.Add(new WhisperWorker { Id = "b", ApiUrl = "http://y:9090", MaxConcurrency = 1 });
        ba.Workers.Add(new WhisperWorker { Id = "a", ApiUrl = "http://x:8080", MaxConcurrency = 2 });

        Assert.Equal(
            SubtitleQueueService.ComputeWorkersSignature(ab),
            SubtitleQueueService.ComputeWorkersSignature(ba));
    }

    [Fact]
    public void Signature_OrderInsensitive_StillChangesOnRealEdits()
    {
        // L1 guard: order-insensitivity must not swallow a REAL change — a url/concurrency edit or an added
        // worker still flips the signature so ReconcileWorkers rebuilds.
        var baseCfg = new PluginConfiguration();
        baseCfg.Workers.Add(new WhisperWorker { Id = "a", ApiUrl = "http://x:8080", MaxConcurrency = 2 });
        baseCfg.Workers.Add(new WhisperWorker { Id = "b", ApiUrl = "http://y:9090", MaxConcurrency = 1 });
        var baseSig = SubtitleQueueService.ComputeWorkersSignature(baseCfg);

        var urlChanged = new PluginConfiguration();
        urlChanged.Workers.Add(new WhisperWorker { Id = "a", ApiUrl = "http://x:8080", MaxConcurrency = 2 });
        urlChanged.Workers.Add(new WhisperWorker { Id = "b", ApiUrl = "http://CHANGED:9090", MaxConcurrency = 1 });
        Assert.NotEqual(baseSig, SubtitleQueueService.ComputeWorkersSignature(urlChanged));

        var concurrencyChanged = new PluginConfiguration();
        concurrencyChanged.Workers.Add(new WhisperWorker { Id = "a", ApiUrl = "http://x:8080", MaxConcurrency = 4 });
        concurrencyChanged.Workers.Add(new WhisperWorker { Id = "b", ApiUrl = "http://y:9090", MaxConcurrency = 1 });
        Assert.NotEqual(baseSig, SubtitleQueueService.ComputeWorkersSignature(concurrencyChanged));

        var added = new PluginConfiguration();
        added.Workers.Add(new WhisperWorker { Id = "a", ApiUrl = "http://x:8080", MaxConcurrency = 2 });
        added.Workers.Add(new WhisperWorker { Id = "b", ApiUrl = "http://y:9090", MaxConcurrency = 1 });
        added.Workers.Add(new WhisperWorker { Id = "c", ApiUrl = "http://z:7070", MaxConcurrency = 1 });
        Assert.NotEqual(baseSig, SubtitleQueueService.ComputeWorkersSignature(added));
    }
}
