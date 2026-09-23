using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WhisperSubs.Configuration;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using WhisperSubs.Setup;

namespace WhisperSubs.Controller
{
    /// <summary>A provider for one Canary target, released when disposed.</summary>
    public sealed class TargetEngineLease : IDisposable
    {
        private Action? _release;

        public TargetEngineLease(ISubtitleProvider provider, string workerName, Action? release)
        {
            Provider = provider;
            WorkerName = workerName;
            _release = release;
        }

        public ISubtitleProvider Provider { get; }

        public string WorkerName { get; }

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    /// <summary>
    /// Where the translation pass gets an engine for one Canary target. The pool implementation routes
    /// the target like any other job; the fallback serves only a local crispasr install.
    /// </summary>
    public interface ITranslationTargetEngines
    {
        /// <summary>True when some engine can ever serve <paramref name="target"/> (it may be busy).</summary>
        bool CanServe(string target);

        /// <summary>
        /// True for the scheduled sweep: a target no engine serves is skipped, because the sweep warns about
        /// it once per run. False for an explicit request, which fails that target with the reason.
        /// </summary>
        bool SkipUnservedTargets { get; }

        /// <summary>
        /// Whether the engines in use include this server's own worker, as actually built. Only picks the
        /// wording of a missing-engine error, which must describe the running pool, not the settings.
        /// </summary>
        bool LocalWorkerInPool { get; }

        /// <summary>An engine for <paramref name="target"/>. Throws when none can be had for this item.</summary>
        Task<TargetEngineLease> AcquireAsync(string target, string itemName, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Routes Canary targets through the worker pool from inside an item that already holds a lease. The
    /// item's own worker is used when it lists the target; otherwise the pool is asked for a worker whose
    /// targets contain it, following <see cref="TargetLeaseRouting"/> so two items never wait on each other.
    /// </summary>
    [ExcludeFromCodeCoverage(Justification = "Orchestration over the live pool; TargetLeaseRouting, WorkerScheduling and TryAcquire are unit-tested")]
    internal sealed class PoolTargetEngines : ITranslationTargetEngines
    {
        private readonly WorkerPool _pool;
        private readonly WorkerLease _lease;

        public PoolTargetEngines(WorkerPool pool, WorkerLease lease, bool skipUnservedTargets = false)
        {
            _pool = pool;
            _lease = lease;
            SkipUnservedTargets = skipUnservedTargets;
        }

        public bool SkipUnservedTargets { get; }

        public bool LocalWorkerInPool => _pool.HasLocalWorker;

        public bool CanServe(string target) => _pool.HasCapableWorker(WorkerJob.ForTarget(target));

        public async Task<TargetEngineLease> AcquireAsync(string target, string itemName, CancellationToken cancellationToken)
        {
            var job = WorkerJob.ForTarget(target);
            var own = _lease.Worker;
            WorkerLease sub;
            switch (TargetLeaseRouting.Decide(own.Capabilities.TranslateTargets, job.TranslateTarget!))
            {
                case TargetLeasePolicy.UseOwnWorker:
                    return new TargetEngineLease(own.TargetProvider ?? own.Provider, own.Name, null);
                case TargetLeasePolicy.TakeFreeAnotherOnly:
                    sub = _pool.TryAcquire(job) ?? throw new InvalidOperationException(
                        $"No worker that translates into '{job.TranslateTarget}' was free while this item ran on {own.Name}, which does not list it. The next run tries again.");
                    break;
                default:
                    sub = await _pool.AcquireAsync(job, cancellationToken).ConfigureAwait(false);
                    break;
            }

            var label = $"{itemName} ({job.TranslateTarget})";
            _pool.SetCurrent(sub.Key, label);
            var worker = sub.Worker;
            return new TargetEngineLease(worker.TargetProvider ?? worker.Provider, worker.Name, () => _pool.Release(sub.Key, label));
        }
    }

    /// <summary>
    /// Fallback when the manager runs without a pool lease: only a local crispasr install serves targets,
    /// which is how the translation pass behaved before targets were routed through the pool.
    /// </summary>
    [ExcludeFromCodeCoverage(Justification = "Orchestration: builds CanaryProvider from config and the filesystem")]
    internal sealed class LocalTargetEngines : ITranslationTargetEngines
    {
        private readonly CanaryProvider? _canary;

        public LocalTargetEngines(PluginConfiguration config, ILogger logger)
        {
            _canary = SubtitleProviderFactory.CreateCanary(config, logger);
        }

        public bool CanServe(string target) => _canary != null && CanaryCatalog.IsTarget(target);

        public bool SkipUnservedTargets => false;

        /// <summary>Without a pool only this server's install serves targets.</summary>
        public bool LocalWorkerInPool => true;

        public Task<TargetEngineLease> AcquireAsync(string target, string itemName, CancellationToken cancellationToken)
            => _canary != null
                ? Task.FromResult(new TargetEngineLease(_canary, "Local (this server)", null))
                : throw new InvalidOperationException("The Canary engine is not installed.");
    }
}
