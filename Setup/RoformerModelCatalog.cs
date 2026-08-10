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
        public const string HuggingFaceRevision = "df802a6773d25ba6ef785ff619daa3e510503168";

        public const string HuggingFaceBaseUrl =
            "https://huggingface.co/chenmozhijin/BSRoformer-GGUF/resolve/" + HuggingFaceRevision + "/anvuew/BS-RoFormer";

        public const string DefaultKey = "q8_0";

        public static readonly RoformerModelOption[] Models = new[]
        {
            new RoformerModelOption("q4_0", "BSRoformer-anvuew-Q4_0.gguf", "Q4_0 (smallest)", 31, 30370912,
                "7b7a7e62ea021170621fe7373fc8f0086ce8b33681c115b1e93f905b4a15eac9",
                "Lowest quality, smallest download. For very constrained storage/bandwidth.", isRecommended: false),
            new RoformerModelOption("q5_1", "BSRoformer-anvuew-Q5_1.gguf", "Q5_1 (small)", 40, 39855712,
                "6fd0b2a9fc881649a67983f8dc0764f218ec3f8db1a1db2a034da25de764f5c2",
                "Good quality/size trade-off for constrained environments.", isRecommended: false),
            new RoformerModelOption("q8_0", "BSRoformer-anvuew-Q8_0.gguf", "Q8_0 (recommended)", 56, 55663712,
                "f0b0093b29ec92aaf6a866973996953a6687df02e0cad68a3833672060c177af",
                "Near-FP32 separation quality at a fraction of the size. Best default for most users.", isRecommended: true),
            new RoformerModelOption("fp16", "BSRoformer-anvuew-FP16.gguf", "FP16 (highest quality)", 102, 102430304,
                "bb25b9fded9780ca19bbc86db17669fb975b142b5e280d850806959130bcd19f",
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
        public long SizeBytes { get; }
        public string Sha256 { get; }
        public string Description { get; }
        public bool IsRecommended { get; }

        public RoformerModelOption(
            string key,
            string fileName,
            string displayName,
            int sizeMB,
            long sizeBytes,
            string sha256,
            string description,
            bool isRecommended)
        {
            Key = key;
            FileName = fileName;
            DisplayName = displayName;
            SizeMB = sizeMB;
            SizeBytes = sizeBytes;
            Sha256 = sha256;
            Description = description;
            IsRecommended = isRecommended;
        }
    }
}
