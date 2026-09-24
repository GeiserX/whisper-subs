using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
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
        /// Whether the running pool holds this server's own worker, as actually built; null when there is no
        /// pool at all. Only picks the wording of a missing-engine error, which must describe what really
        /// serves targets, not the settings.
        /// </summary>
        bool? LocalWorkerInPool { get; }

        /// <summary>An engine for <paramref name="target"/>. Throws when none can be had for this item.</summary>
        Task<TargetEngineLease> AcquireAsync(string target, string itemName, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Routes Canary targets through the worker pool from inside an item that already holds a lease. The
    /// item's own worker is used when it lists the target; otherwise the pool is asked for a worker whose
    /// targets contain it, following <see cref="TargetLeaseRouting"/> so two items never wait on each other.
    /// A translate job is placed on a worker that lists its target (<see cref="DispatchPlacement"/>), so it
    /// uses its own worker and never waits here.
    /// </summary>
    [ExcludeFromCodeCoverage(Justification = "Orchestration over the live pool; TargetLeaseRouting, WorkerScheduling and TryAcquire are unit-tested")]
    internal sealed class PoolTargetEngines : ITranslationTargetEngines
    {
        private readonly WorkerPool _pool;
        private readonly WorkerLease _lease;
        private readonly bool _localWhisperTranslates;
        private readonly Action? _beforeTargetEngine;

        /// <param name="localWhisperTranslates">False keeps English off this server's own worker: its
        /// Whisper model is a turbo model, which cannot translate. Only a translate job asks for English here.</param>
        /// <param name="beforeTargetEngine">Runs before the first target engine is handed out: the dispatcher
        /// releases the language-probe lease there, since the probe is over by then.</param>
        public PoolTargetEngines(WorkerPool pool, WorkerLease lease, bool skipUnservedTargets = false,
            bool localWhisperTranslates = true, Action? beforeTargetEngine = null)
        {
            _pool = pool;
            _lease = lease;
            SkipUnservedTargets = skipUnservedTargets;
            _localWhisperTranslates = localWhisperTranslates;
            _beforeTargetEngine = beforeTargetEngine;
        }

        public bool SkipUnservedTargets { get; }

        public bool? LocalWorkerInPool => _pool.HasLocalWorker;

        public bool CanServe(string target) => _pool.HasCapableWorker(WorkerJob.ForTarget(target, _localWhisperTranslates));

        public async Task<TargetEngineLease> AcquireAsync(string target, string itemName, CancellationToken cancellationToken)
        {
            _beforeTargetEngine?.Invoke();
            var job = WorkerJob.ForTarget(target, _localWhisperTranslates);
            var own = _lease.Worker;
            WorkerLease sub;
            // A job kept off this server's worker must not use it as its own either.
            var ownTargets = job.RemoteOnly && own.Capabilities.IsLocal
                ? WorkerTargets.Set(own.Capabilities.TranslateTargets.Where(t => !string.Equals(t, job.TranslateTarget, StringComparison.OrdinalIgnoreCase)).ToArray())
                : own.Capabilities.TranslateTargets;
            switch (TargetLeaseRouting.Decide(ownTargets, job.TranslateTarget!))
            {
                case TargetLeasePolicy.UseOwnWorker:
                    return new TargetEngineLease(TargetProviders.For(own, job.TranslateTarget!), own.Name, null);
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
            return new TargetEngineLease(TargetProviders.For(worker, job.TranslateTarget!), worker.Name, () => _pool.Release(sub.Key, label));
        }
    }

    /// <summary>Which of a worker's providers makes one target. Pure.</summary>
    internal static class TargetProviders
    {
        /// <summary>
        /// "en" is Whisper's translate task, so it always uses the worker's own <see cref="ITranscriptionWorker.Provider"/>;
        /// on this server's worker, <see cref="ITranscriptionWorker.TargetProvider"/> is Canary, which must never
        /// make the English one. Any other target uses the target provider when the worker has one.
        /// </summary>
        public static ISubtitleProvider For(ITranscriptionWorker worker, string target)
            => string.Equals(target, "en", StringComparison.OrdinalIgnoreCase)
                ? worker.Provider
                : worker.TargetProvider ?? worker.Provider;
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

        /// <summary>
        /// No pool here: this server's install, as read when this object was built, is the only engine.
        /// Claiming a pool worker would make a missing-engine error talk about a pool that does not exist.
        /// </summary>
        public bool? LocalWorkerInPool => null;

        public Task<TargetEngineLease> AcquireAsync(string target, string itemName, CancellationToken cancellationToken)
            => _canary != null
                ? Task.FromResult(new TargetEngineLease(_canary, "Local (this server)", null))
                : throw new InvalidOperationException("The Canary engine is not installed.");
    }
}
