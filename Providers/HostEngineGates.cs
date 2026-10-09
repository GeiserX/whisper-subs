using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Configuration;

namespace WhisperSubs.Providers
{
    /// <summary>
    /// How many engine processes run on this server at once, however many titles are in flight and however
    /// many worker rows ask. Every pool row builds its own providers, so the limits are process-wide.
    /// Measured in the Jellyfin container on an Intel UHD 770 (2026-10-09): whisper-cli with large-v3 on
    /// Vulkan peaks at about 6.2 GB, 4.1 GB of it GPU buffers charged to the container; crispasr with
    /// Qwen3-ASR and the aligner at about 5.6 GB; a whisper-cli language-detection batch on the small
    /// detection model at about 0.6 GB. Five titles in flight once ran five large-v3 processes against a
    /// 16 GB limit, and Jellyfin was OOM-killed 21 times in one night.
    /// A process suspended for playback keeps its turn, so a stopped process and a new one never hold
    /// memory together. The limits come from the settings (<see cref="Apply"/>); the defaults are 1 and 1.
    /// </summary>
    internal static class HostEngineGates
    {
        /// <summary>
        /// Processes with a full model: Whisper transcription or translation, Qwen3-ASR, Canary, and language
        /// detection on the transcription model. <see cref="PluginConfiguration.MaxFullModelEngines"/> at once.
        /// </summary>
        internal static readonly EngineGate Model = new(1);

        /// <summary>One language-detection run on the small detection model at a time.</summary>
        internal static readonly EngineGate Detection = new(1);

        /// <summary>
        /// Whether small-model detection takes its own gate beside the full-model engines (default), or
        /// queues with them.
        /// </summary>
        internal static bool DetectionBesideFullModel { get; private set; } = true;

        /// <summary>The Vulkan devices engines on this server may use (GGML_VK_VISIBLE_DEVICES); "" = the engine's choice.</summary>
        internal static string VulkanDevice { get; private set; } = "";

        /// <summary>Takes the limits and the Vulkan device from the settings. Safe while engines run.</summary>
        internal static void Apply(PluginConfiguration config)
        {
            Model.Limit = EngineOptions.MaxFullModelEngines(config.MaxFullModelEngines);
            DetectionBesideFullModel = config.AllowDetectionBesideFullModel;
            VulkanDevice = EngineOptions.VulkanDevice(config.VulkanDevice);
        }

        /// <summary>Restores the defaults (tests).</summary>
        internal static void Reset()
        {
            Model.Limit = 1;
            DetectionBesideFullModel = true;
            VulkanDevice = "";
        }

        /// <summary>Points an engine process at the configured Vulkan device, when one is set.</summary>
        internal static void ApplyVulkanDevice(ProcessStartInfo startInfo)
        {
            var device = VulkanDevice;
            if (device.Length > 0) startInfo.Environment["GGML_VK_VISIBLE_DEVICES"] = device;
        }
    }

    /// <summary>
    /// A counting gate whose limit can change while it is held: raising it lets waiters in at once, lowering
    /// it lets the holders finish and admits nobody new until the count is under the new limit.
    /// </summary>
    internal sealed class EngineGate
    {
        private readonly object _lock = new();
        private readonly LinkedList<TaskCompletionSource<bool>> _waiters = new();
        private int _limit;
        private int _held;

        internal EngineGate(int limit) => _limit = Math.Max(1, limit);

        internal int Limit
        {
            get { lock (_lock) return _limit; }
            set
            {
                List<TaskCompletionSource<bool>> wake;
                lock (_lock)
                {
                    _limit = Math.Max(1, value);
                    wake = TakeAdmittedLocked();
                }
                foreach (var w in wake) w.TrySetResult(true);
            }
        }

        /// <summary>Holders right now (tests and the status panel).</summary>
        internal int Held { get { lock (_lock) return _held; } }

        internal async Task WaitAsync(CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<bool> waiter;
            LinkedListNode<TaskCompletionSource<bool>> node;
            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_held < _limit && _waiters.Count == 0)
                {
                    _held++;
                    return;
                }
                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                node = _waiters.AddLast(waiter);
            }

            using (cancellationToken.Register(() =>
            {
                bool removed;
                lock (_lock)
                {
                    removed = node.List != null;
                    if (removed) _waiters.Remove(node);
                }
                if (removed) waiter.TrySetCanceled(cancellationToken);
            }))
            {
                await waiter.Task.ConfigureAwait(false);
            }
        }

        /// <summary>Waits up to <paramref name="timeout"/>; true when the turn was taken.</summary>
        internal async Task<bool> WaitAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await WaitAsync(cts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        internal void Release()
        {
            List<TaskCompletionSource<bool>> wake;
            lock (_lock)
            {
                if (_held == 0) throw new SemaphoreFullException();
                _held--;
                wake = TakeAdmittedLocked();
            }
            foreach (var w in wake) w.TrySetResult(true);
        }

        // Admits waiters in order while there is room; the turn is counted before the waiter wakes.
        private List<TaskCompletionSource<bool>> TakeAdmittedLocked()
        {
            var admitted = new List<TaskCompletionSource<bool>>();
            while (_held < _limit && _waiters.First is { } first)
            {
                _waiters.RemoveFirst();
                _held++;
                admitted.Add(first.Value);
            }
            return admitted;
        }
    }
}
