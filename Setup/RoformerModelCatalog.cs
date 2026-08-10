using System;
using System.Linq;

namespace WhisperSubs.Setup
{
    /// <summary>
    /// GGUF model catalog for BSRoformer.cpp vocal separation. Uses the anvuew/BS-RoFormer model from
    /// the official chenmozhijin/BSRoformer-GGUF HuggingFace repo — a general-purpose vocal-isolation
    /// model well regarded in the music-source-separation community. Only the quantization varies;
    /// picking a different one trades size/speed for quality, mirroring how <see cref="ModelCatalog"/>
    /// offers whisper models by size.
    /// </summary>
    public static class RoformerModelCatalog
    {
        public const string HuggingFaceBaseUrl =
            "https://huggingface.co/chenmozhijin/BSRoformer-GGUF/resolve/main/anvuew/BS-RoFormer";

        public const string DefaultKey = "q8_0";

        public static readonly RoformerModelOption[] Models = new[]
        {
            new RoformerModelOption("q4_0", "BSRoformer-anvuew-Q4_0.gguf", "Q4_0 (smallest)", 31,
                "Lowest quality, smallest download. For very constrained storage/bandwidth.", isRecommended: false),
            new RoformerModelOption("q5_1", "BSRoformer-anvuew-Q5_1.gguf", "Q5_1 (small)", 40,
                "Good quality/size trade-off for constrained environments.", isRecommended: false),
            new RoformerModelOption("q8_0", "BSRoformer-anvuew-Q8_0.gguf", "Q8_0 (recommended)", 56,
                "Near-FP32 separation quality at a fraction of the size. Best default for most users.", isRecommended: true),
            new RoformerModelOption("fp16", "BSRoformer-anvuew-FP16.gguf", "FP16 (highest quality)", 102,
                "Maximum precision. Larger download, marginal quality gain over Q8_0.", isRecommended: false),
        };

        /// <summary>
        /// Resolves a quantization selection key to its catalog entry. Unknown, empty or null keys
        /// fall back to <see cref="DefaultKey"/> (Q8_0) so a stale/absent config value never breaks setup.
        /// </summary>
        public static RoformerModelOption Resolve(string? key)
            => Array.Find(Models, m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))
               ?? Models.First(m => m.Key == DefaultKey);
    }

    /// <summary>
    /// A selectable BSRoformer.cpp GGUF model quantization: the stable selection <see cref="Key"/>
    /// stored in config (e.g. "q8_0"), the upstream GGUF file name, its expected size, a UI label and
    /// description, and whether it's the recommended default.
    /// </summary>
    public sealed class RoformerModelOption
    {
        public string Key { get; }
        public string FileName { get; }
        public string DisplayName { get; }
        public int SizeMB { get; }
        public string Description { get; }
        public bool IsRecommended { get; }

        public RoformerModelOption(string key, string fileName, string displayName, int sizeMB, string description, bool isRecommended)
        {
            Key = key;
            FileName = fileName;
            DisplayName = displayName;
            SizeMB = sizeMB;
            Description = description;
            IsRecommended = isRecommended;
        }
    }
}
