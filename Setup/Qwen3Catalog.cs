using System;
using System.Linq;

namespace WhisperSubs.Setup
{
    /// <summary>
    /// Qwen3-ASR-1.7B (Apache-2.0) GGUF weights for CrispASR, from the cstr/qwen3-asr-1.7b-GGUF
    /// Hugging Face repository. The optional transcription engine next to whisper-cli: one model
    /// for 30 languages and 22 Chinese dialects, with its own language identification. It runs
    /// through the same plugin-managed crispasr binary as Canary, so installing it is one more
    /// model download, not a second binary.
    /// </summary>
    public static class Qwen3Catalog
    {
        /// <summary>The config value of <c>TranscriptionEngine</c> that selects this engine.</summary>
        public const string EngineKey = "qwen3";

        /// <summary>The config value of <c>TranscriptionEngine</c> for whisper-cli, the default.</summary>
        public const string WhisperEngineKey = "whisper";

        /// <summary>
        /// Pinned repository revision. The SHA-256 of each file is recorded too, so pinning the
        /// revision keeps a later upstream re-upload from turning into a checksum failure.
        /// </summary>
        public const string HuggingFaceRevision = "674df5d44b50a63e7102a18895ed20e3f91de301";

        public const string HuggingFaceBaseUrl =
            "https://huggingface.co/cstr/qwen3-asr-1.7b-GGUF/resolve/" + HuggingFaceRevision;

        public const string DefaultKey = "q8_0";

        public static readonly Qwen3ModelOption[] Models = new[]
        {
            new Qwen3ModelOption("q8_0", "qwen3-asr-1.7b-q8_0.gguf", "Q8_0 (recommended)", 2507, 2506723200,
                "9851ab996591a2d0cb0efb216002764b509c86bd40c95e613d7b65b8e69c8a6e",
                "Near full-precision quality. Best default.", IsRecommended: true),
            new Qwen3ModelOption("q4_k", "qwen3-asr-1.7b-q4_k.gguf", "Q4_K (smaller)", 1491, 1490915200,
                "ec197cef7ccc589fdcae1becc3f4a3de119d0a41e790b898b519b1a048dad8d4",
                "Smaller download and memory footprint, slightly lower quality. The audio encoder stays at Q8_0.", IsRecommended: false),
        };

        /// <summary>
        /// Resolves a quantization key to its catalog entry. Unknown, empty or null keys fall back to
        /// <see cref="DefaultKey"/> so a stale config value never breaks setup.
        /// </summary>
        public static Qwen3ModelOption Resolve(string? key)
            => Array.Find(Models, m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))
               ?? Models.First(m => m.Key == DefaultKey);

        /// <summary>True when the configured engine key selects Qwen3-ASR. Pure.</summary>
        public static bool IsSelected(string? transcriptionEngine)
            => string.Equals((transcriptionEngine ?? "").Trim(), EngineKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The optional word aligner. Qwen3-ASR returns text with no timestamps; without this file
        /// crispasr times each cue by its VAD segment, split in proportion to text length, and a
        /// segment VAD keeps open under music becomes a cue that starts twenty seconds early. With
        /// it, crispasr aligns every word with NVIDIA's Canary CTC aligner (CC-BY-4.0, from
        /// cstr/canary-ctc-aligner-GGUF), the same file its own <c>-am auto</c> resolves to, and cue
        /// starts land on the spoken word. It costs about twice the run time. The aligner knows the
        /// 25 Canary languages only, so <see cref="AlignerSupports"/> gates it per title.
        /// </summary>
        public const string AlignerHuggingFaceRevision = "2b22dc9aff585bc8368a5228173c8afe22c155b7";

        public const string AlignerHuggingFaceBaseUrl =
            "https://huggingface.co/cstr/canary-ctc-aligner-GGUF/resolve/" + AlignerHuggingFaceRevision;

        public static readonly Qwen3ModelOption Aligner = new("aligner-q4_k", "canary-ctc-aligner-q4_k.gguf",
            "Word aligner Q4_K", 392, 392167040,
            "d16dbf18f9a66f59c0ceb61b204caca5dda21742d6e9dc304c9d0518c81ee38c",
            "Canary CTC aligner: word-accurate cue timing for the 25 European languages it knows, at about twice the run time.",
            IsRecommended: true);

        /// <summary>
        /// Whether the aligner can time <paramref name="language"/>: English and the 24 Canary
        /// targets. Any other language keeps VAD timing. Pure.
        /// </summary>
        public static bool AlignerSupports(string? language)
        {
            var code = (language ?? "").Trim().ToLowerInvariant();
            return code.Length > 0 && CanaryCatalog.RequestableTargets.Any(t => t.Code == code);
        }
    }

    /// <summary>
    /// A selectable Qwen3-ASR GGUF quantization: the stable <see cref="Key"/> stored in config, the
    /// upstream file name, its exact size and SHA-256, a UI label and whether it is the default.
    /// </summary>
    public sealed record Qwen3ModelOption(
        string Key,
        string FileName,
        string DisplayName,
        int SizeMB,
        long SizeBytes,
        string Sha256,
        string Description,
        bool IsRecommended);
}
