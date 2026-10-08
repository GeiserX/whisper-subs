using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace WhisperSubs.Controller
{
    /// <summary>
    /// Suspends and continues the engine processes this server runs (whisper-cli, crispasr, FFmpeg
    /// extraction, vocal separation) with SIGSTOP / SIGCONT, for "Pause generation during playback".
    /// A stopped process keeps everything it has done, so a pause costs no work in any pass, where
    /// killing it threw the pass away and a pass that could not resume never finished on a busy
    /// server. Linux only; elsewhere <see cref="Supported"/> is false and the caller keeps
    /// the cancel-and-retry behaviour.
    /// </summary>
    internal sealed class EngineProcessSuspender
    {
        private readonly Func<int, int, int> _kill;
        private readonly (int Stop, int Cont)? _signals;
        private readonly System.Threading.AsyncLocal<Scope?> _current = new();

        /// <summary>The process-wide instance, wired to libc's <c>kill</c>.</summary>
        public static EngineProcessSuspender Default { get; } = new(NativeKill, PlatformSignals());

        internal EngineProcessSuspender(Func<int, int, int> kill, (int Stop, int Cont)? signals)
        {
            _kill = kill;
            _signals = signals;
        }

        /// <summary>
        /// SIGSTOP / SIGCONT numbers: 19 / 18 on Linux, none elsewhere. macOS is left out on purpose:
        /// a stopped child makes .NET's SIGCHLD handler spin there (waitid reports the stopped child as
        /// if it had exited, the reap finds it alive, and the loop never ends), after which every wait
        /// on a child process hangs. Measured with a stopped <c>sleep</c> under .NET 10 on macOS 27.
        /// </summary>
        internal static (int Stop, int Cont)? PlatformSignals()
            => OperatingSystem.IsLinux() ? (19, 18) : null;

        public bool Supported => _signals != null;

        /// <summary>
        /// The scope of the job this call belongs to, or null. A scope follows the async flow that
        /// opened it, so the processes one job starts land in that job's scope and in no other: a job
        /// is never stopped by another job's pause.
        /// </summary>
        public Scope? Current => _current.Value;

        /// <summary>
        /// Opens the scope for one job on this server. Call it from the method that then awaits the
        /// job, so the job's own calls see it. Disposing continues anything still stopped.
        /// </summary>
        public Scope BeginScope()
        {
            var scope = new Scope(this);
            _current.Value = scope;
            return scope;
        }

        /// <summary>
        /// Registers a running engine process with the current job's scope until the returned handle is
        /// disposed. Outside a scope this does nothing.
        /// </summary>
        public IDisposable Track(int pid) => Current?.Track(pid) ?? NoHandle.Instance;

        private void Send(int pid, bool stop)
        {
            if (_signals is not { } signals) return;
            try { _kill(pid, stop ? signals.Stop : signals.Cont); }
            catch { /* the process is gone, or libc is not there: nothing to stop */ }
        }

        /// <summary>One job's engine processes and whether they are stopped.</summary>
        internal sealed class Scope : IDisposable
        {
            private readonly EngineProcessSuspender _owner;
            private readonly HashSet<int> _pids = new();
            private readonly object _gate = new();
            private bool _suspended;
            private bool _disposed;

            internal Scope(EngineProcessSuspender owner) { _owner = owner; }

            public bool IsSuspended { get { lock (_gate) { return _suspended; } } }

            /// <summary>
            /// Registers a process. One that starts while the job is suspended is stopped at once, so a
            /// job between two phases does not slip a new process past the pause.
            /// </summary>
            public IDisposable Track(int pid)
            {
                lock (_gate)
                {
                    if (_disposed) return NoHandle.Instance;
                    _pids.Add(pid);
                    if (_suspended) _owner.Send(pid, stop: true);
                }
                return new Handle(this, pid);
            }

            /// <summary>Stops every process of this job. Idempotent.</summary>
            public void Suspend()
            {
                lock (_gate)
                {
                    if (_suspended || _disposed || !_owner.Supported) return;
                    _suspended = true;
                    foreach (var pid in _pids) _owner.Send(pid, stop: true);
                }
            }

            /// <summary>Continues every process of this job. Idempotent.</summary>
            public void Resume()
            {
                lock (_gate)
                {
                    if (!_suspended) return;
                    _suspended = false;
                    foreach (var pid in _pids) _owner.Send(pid, stop: false);
                }
            }

            private void Untrack(int pid)
            {
                lock (_gate) { _pids.Remove(pid); }
            }

            /// <summary>Continues anything still stopped and closes the scope.</summary>
            public void Dispose()
            {
                Resume();
                lock (_gate) { _disposed = true; _pids.Clear(); }
                if (ReferenceEquals(_owner._current.Value, this)) _owner._current.Value = null;
            }

            private sealed class Handle : IDisposable
            {
                private readonly Scope _scope;
                private readonly int _pid;
                private bool _released;

                public Handle(Scope scope, int pid) { _scope = scope; _pid = pid; }

                public void Dispose()
                {
                    if (_released) return;
                    _released = true;
                    _scope.Untrack(_pid);
                }
            }
        }

        private sealed class NoHandle : IDisposable
        {
            public static readonly NoHandle Instance = new();
            public void Dispose() { }
        }

        [ExcludeFromCodeCoverage(Justification = "P/Invoke into libc")]
        private static int NativeKill(int pid, int signal) => kill(pid, signal);

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);
    }

    /// <summary>
    /// A deadline that does not run while its job is suspended. The per-file detection timeout and
    /// the vocal-separation deadline used <c>CancelAfter</c>, which keeps counting through a pause and
    /// would kill a stopped process for being slow.
    /// </summary>
    internal sealed class PausableDeadline : IDisposable
    {
        private readonly TimeSpan _timeout;
        private readonly System.Threading.CancellationTokenSource _target;
        private readonly Func<bool> _isPaused;
        private readonly System.Threading.CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private TimeSpan _remaining;
        private bool _started;

        public PausableDeadline(TimeSpan timeout, System.Threading.CancellationTokenSource target, Func<bool> isPaused)
        {
            _timeout = timeout;
            _target = target;
            _isPaused = isPaused;
            _remaining = timeout;
        }

        /// <summary>Starts the clock, or restarts it with the full timeout (a new file in a batch).</summary>
        public void Restart()
        {
            lock (_gate)
            {
                _remaining = _timeout;
                if (_started) return;
                _started = true;
            }
            _ = RunAsync();
        }

        /// <summary>
        /// Accounts for <paramref name="elapsed"/>: nothing while paused, otherwise it comes off what
        /// remains. Returns true when the deadline has passed. Pure apart from its own state.
        /// </summary>
        internal bool Advance(TimeSpan elapsed)
        {
            lock (_gate)
            {
                if (_isPaused()) return false;
                _remaining -= elapsed;
                return _remaining <= TimeSpan.Zero;
            }
        }

        [ExcludeFromCodeCoverage(Justification = "A timer loop over the unit-tested Advance")]
        private async System.Threading.Tasks.Task RunAsync()
        {
            var tick = TimeSpan.FromMilliseconds(250);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var last = clock.Elapsed;
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await System.Threading.Tasks.Task.Delay(tick, _stop.Token).ConfigureAwait(false);
                    var now = clock.Elapsed;
                    var due = Advance(now - last);
                    last = now;
                    if (due)
                    {
                        try { _target.Cancel(); } catch (ObjectDisposedException) { }
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
            _stop.Dispose();
        }
    }
}
