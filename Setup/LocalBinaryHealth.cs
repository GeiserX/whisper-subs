using System;

namespace WhisperSubs.Setup
{
    /// <summary>
    /// "Can the installed whisper-cli actually launch?", cached with a timestamp (issue #185). The binary
    /// used to be probed only at download time, so a container recreated without the NVIDIA runtime turned
    /// a working CUDA build into one that exits 127 on every run while the setup page still said "ready"
    /// (the file is still there) and the queue kept feeding it items.
    /// <para>
    /// The cache is what makes the check affordable in both places it is needed: <c>GET Setup/Status</c> is
    /// polled by the config page, and the dispatcher asks before every drain — neither may spawn a process
    /// each time. A launch failure observed during a real transcription is recorded here too, so the setup
    /// page reports it without waiting for the next probe window.
    /// </para>
    /// The clock and interval are injectable so the caching rule is unit-testable without waiting minutes.
    /// </summary>
    public sealed class LocalBinaryHealth
    {
        /// <summary>How long a verdict is trusted before the next caller re-probes.</summary>
        internal static readonly TimeSpan DefaultProbeInterval = TimeSpan.FromMinutes(5);

        /// <summary>The process-wide verdict shared by the status endpoint and the dispatcher.</summary>
        public static LocalBinaryHealth Instance { get; } = new LocalBinaryHealth();

        private readonly Func<DateTimeOffset> _clock;
        private readonly TimeSpan _interval;
        private readonly object _gate = new();
        private string? _error;
        private DateTimeOffset? _checkedAt;

        public LocalBinaryHealth(Func<DateTimeOffset>? clock = null, TimeSpan? probeInterval = null)
        {
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _interval = probeInterval ?? DefaultProbeInterval;
        }

        /// <summary>
        /// Whether a verdict taken at <paramref name="lastChecked"/> is old enough to re-probe. Pure, so the
        /// "at most once every few minutes" rule is pinned by a test instead of by a stopwatch.
        /// </summary>
        internal static bool IsStale(DateTimeOffset? lastChecked, DateTimeOffset now, TimeSpan interval)
            => lastChecked is null || now - lastChecked.Value >= interval;

        /// <summary>
        /// The last known launch error (null when the binary launches, or when nothing has probed yet).
        /// Never spawns a process — for callers that must not block, such as a status render.
        /// </summary>
        public string? LastError
        {
            get { lock (_gate) { return _error; } }
        }

        /// <summary>
        /// The current launch error, re-running <paramref name="probe"/> when the cached verdict has aged
        /// past the interval. <paramref name="probe"/> returns null when the binary starts fine.
        /// </summary>
        /// <remarks>
        /// The timestamp is stamped BEFORE the probe runs, and the probe runs OUTSIDE the lock: a probe can
        /// take seconds (a GPU build initialising drivers), and holding the lock would stall every config-page
        /// poll behind it. A caller arriving during that window gets the previous verdict, which is the point
        /// of a cache.
        /// </remarks>
        public string? Check(Func<string?> probe)
        {
            if (probe == null) throw new ArgumentNullException(nameof(probe));

            var now = _clock();
            lock (_gate)
            {
                if (!IsStale(_checkedAt, now, _interval)) return _error;
                _checkedAt = now;   // claim this probe window so concurrent callers don't pile on
            }

            var error = probe();
            lock (_gate)
            {
                _error = error;
                return _error;
            }
        }

        /// <summary>
        /// Records a launch failure seen during a real transcription. Takes effect immediately — the whole
        /// point is that the setup page stops claiming "ready" the moment a job proves otherwise.
        /// </summary>
        public void RecordLaunchFailure(string message)
        {
            lock (_gate)
            {
                _error = message;
                _checkedAt = _clock();
            }
        }

        /// <summary>
        /// Forgets the cached verdict so the next <see cref="Check"/> re-probes at once. Called after a new
        /// binary is installed: the verdict belongs to the file that was just replaced.
        /// </summary>
        public void Invalidate()
        {
            lock (_gate)
            {
                _error = null;
                _checkedAt = null;
            }
        }
    }
}
