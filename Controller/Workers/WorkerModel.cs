using System.Collections.Generic;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// Static, slow-changing facts a transcription worker advertises so the scheduler can route a job to it
    /// without any backend-specific conditionals (v4.0 worker pool). A "worker" is the host's own local
    /// whisper-cli OR any remote OpenAI-compatible endpoint (CPU faster-whisper, NVIDIA/CUDA, AMD/ROCm, a
    /// NAS, a cloud API — the user's choice). This model is deliberately backend-agnostic so the pool serves
    /// any user's topology, not one reference setup.
    /// </summary>
    public sealed record WorkerCapabilities
    {
        /// <summary>Max jobs this worker runs at once. Default 1 — a single GPU saturates on one whisper stream.</summary>
        public int MaxConcurrency { get; init; } = 1;

        /// <summary>Whether the worker can translate to English (OpenAI /v1/audio/translations, or whisper-cli --translate).</summary>
        public bool CanTranslate { get; init; } = true;

        /// <summary>Models the worker can serve; empty = "any" (the common homelab case, and the local worker).</summary>
        public IReadOnlySet<string> Models { get; init; } = new HashSet<string>();

        /// <summary>A free local worker (the host's own CPU/GPU, or a LAN box) vs a paid/remote one.</summary>
        public bool IsLocal { get; init; } = true;

        /// <summary>Selection cost: 0 = free local (always preferred); &gt;0 = paid/cloud, used only to burst when locals are full.</summary>
        public double CostWeight { get; init; }

        /// <summary>Stable tiebreak among otherwise-equal workers. Lower = preferred.</summary>
        public int Priority { get; init; }
    }

    /// <summary>A live routing snapshot of one worker — a pure value (no Jellyfin/HTTP types), so selection is unit-testable.</summary>
    public readonly record struct WorkerSlot(string Id, bool Healthy, int InFlight, WorkerCapabilities Capabilities);

    /// <summary>
    /// A display snapshot of one worker for the admin queue/status panel (v4.0): identity + live load + the
    /// item(s) it is currently transcribing ("what's running where") + the static facts the UI shows.
    /// Distinct from <see cref="WorkerSlot"/> (routing) so the surfaced shape is stable and independent of
    /// the scheduler's internal value.
    /// </summary>
    public readonly record struct WorkerStatus(
        string Id, string Name, int InFlight, int MaxConcurrency, bool IsLocal, double CostWeight,
        IReadOnlyList<string> CurrentItems);

    /// <summary>What a specific job needs from a worker (drives the hard capability filter).</summary>
    public readonly record struct JobRequirements(bool Translate, string? RequiredModel);
}
