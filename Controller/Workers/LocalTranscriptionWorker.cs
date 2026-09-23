using System;
using WhisperSubs.Configuration;
using WhisperSubs.Providers;
using WhisperSubs.Setup;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// "Are crispasr and the Canary model installed?", cached. The pool asks on every scheduling decision,
    /// so the files are checked at most once per interval. A different binary or model path is re-checked
    /// at once: an install writes new paths, so the first Generate after it sees the engine immediately.
    /// The clock, interval and file check are injectable so the rule is unit-testable.
    /// </summary>
    public sealed class CanaryInstallCheck
    {
        internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

        private readonly Func<string, bool> _fileExists;
        private readonly Func<DateTimeOffset> _clock;
        private readonly TimeSpan _interval;
        private readonly object _gate = new();
        private string? _key;
        private DateTimeOffset? _checkedAt;
        private bool _installed;

        public CanaryInstallCheck(Func<string, bool>? fileExists = null, Func<DateTimeOffset>? clock = null, TimeSpan? interval = null)
        {
            _fileExists = fileExists ?? System.IO.File.Exists;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _interval = interval ?? DefaultInterval;
        }

        public bool IsInstalled(string? binaryPath, string? modelPath)
        {
            var key = (binaryPath ?? "") + "\n" + (modelPath ?? "");
            var now = _clock();
            lock (_gate)
            {
                if (key == _key && !LocalBinaryHealth.IsStale(_checkedAt, now, _interval)) return _installed;
                _installed = SubtitleProviderFactory.IsCanaryInstalled(binaryPath, modelPath, _fileExists);
                _key = key;
                _checkedAt = now;
                return _installed;
            }
        }
    }

    /// <summary>
    /// The host's own worker. Whole titles go to whisper-cli, fixed at build time like every worker. Its
    /// Canary targets are live: <see cref="Capabilities"/> is computed when the pool asks, from the current
    /// configuration and whether crispasr and the Canary model are installed now. The pool is only rebuilt
    /// at a session start with nothing in flight, which a long sweep may not reach for hours, so a
    /// capability fixed at build time would refuse the engine the admin just installed.
    /// </summary>
    public sealed class LocalTranscriptionWorker : ITranscriptionWorker
    {
        private readonly Func<PluginConfiguration> _config;
        private readonly CanaryInstallCheck _installCheck;
        private readonly Func<PluginConfiguration, ISubtitleProvider?> _createCanary;

        public LocalTranscriptionWorker(
            ISubtitleProvider provider,
            Func<PluginConfiguration> config,
            CanaryInstallCheck installCheck,
            Func<PluginConfiguration, ISubtitleProvider?> createCanary)
        {
            Provider = provider;
            _config = config;
            _installCheck = installCheck;
            _createCanary = createCanary;
        }

        public string Id => "local";

        public string Name => "Local (this server)";

        public ISubtitleProvider Provider { get; }

        /// <summary>English through whisper-cli, plus the configured Canary targets while the engine is installed.</summary>
        public WorkerCapabilities Capabilities
        {
            get
            {
                var config = _config();
                return new WorkerCapabilities
                {
                    IsLocal = true,
                    CostWeight = 0,
                    MaxConcurrency = 1,
                    TranslateTargets = WorkerTargets.ForLocal(
                        config.TranslationTargetLanguages,
                        _installCheck.IsInstalled(config.CrispAsrBinaryPath, config.CanaryModelPath)),
                };
            }
        }

        /// <summary>A Canary provider built from the current configuration, or null while the engine is missing.</summary>
        public ISubtitleProvider? TargetProvider => _createCanary(_config());
    }
}
