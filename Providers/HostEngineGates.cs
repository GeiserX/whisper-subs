using System.Threading;

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
    /// memory together.
    /// </summary>
    internal static class HostEngineGates
    {
        /// <summary>
        /// One process with a full model at a time: Whisper transcription or translation, Qwen3-ASR,
        /// Canary, and language detection that has to fall back to the transcription model.
        /// </summary>
        internal static readonly SemaphoreSlim Model = new(1, 1);

        /// <summary>One language-detection run on the small detection model at a time.</summary>
        internal static readonly SemaphoreSlim Detection = new(1, 1);
    }
}
