namespace WhisperSubs.Setup
{
    public static class ModelCatalog
    {
        public static readonly ModelEntry[] Models = new[]
        {
            new ModelEntry("ggml-large-v3-turbo-q5_0.bin", "Large V3 Turbo (Q5)", 574, true,
                "Best quality/size ratio. Recommended for most users.", canTranslate: false),
            new ModelEntry("ggml-large-v3-turbo.bin", "Large V3 Turbo (F16)", 1620, false,
                "Full-precision turbo model. Slightly better quality, 3x larger.", canTranslate: false),
            new ModelEntry("ggml-large-v3-q5_0.bin", "Large V3 (Q5)", 1081, false,
                "Best choice for non-English audio and translation: near-F16 accuracy at ~1/3 the size and RAM, and unlike turbo it translates reliably.", canTranslate: true),
            new ModelEntry("ggml-large-v3.bin", "Large V3 (F16)", 3095, false,
                "Maximum accuracy, but ~3-4x slower than turbo (more on iGPU) and needs ~4 GB RAM. The Q5 version above is usually the better trade-off.", canTranslate: true),
            new ModelEntry("ggml-medium-q5_0.bin", "Medium (Q5)", 539, false,
                "Good quality, similar size to turbo-q5 but slower and less accurate.", canTranslate: true),
            new ModelEntry("ggml-medium.bin", "Medium (F16)", 1530, false,
                "Full-precision medium model.", canTranslate: true),
            new ModelEntry("ggml-small.bin", "Small", 488, false,
                "Faster inference, lower accuracy. Good for quick tests.", canTranslate: true),
            new ModelEntry("ggml-base.bin", "Base", 148, false,
                "Lightweight model. Fast but noticeably less accurate.", canTranslate: true),
            new ModelEntry("ggml-tiny.bin", "Tiny", 78, false,
                "Smallest model. Only for testing or very constrained environments.", canTranslate: true),
        };

        public const string HuggingFaceBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";

        // ── Silero VAD model (for whisper-cli --vad speech-onset alignment) ──
        // Tiny (~865 KB) Silero VAD ggml from the official ggml-org/whisper-vad repo.
        public const string VadModelFileName = "ggml-silero-v5.1.2.bin";
        public const string VadModelUrl = "https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v5.1.2.bin";
        public const long VadModelSizeBytes = 885098;

        // ── Dedicated language-detection model (forced-subtitle per-chunk --detect-language) ──
        // Forced mode runs --detect-language once per ~30s speech chunk (100+ per film). That only
        // needs the encoder's language-ID pass, where "base" (~148 MB) is accurate but far cheaper to
        // run and load than the multi-hundred-MB/GB transcription model — so detection finishes in
        // seconds even on a slow no-AVX2 CPU instead of timing out. Lives in its own subdir so it
        // never affects transcription-model selection. (Issue #95.)
        public const string DetectionModelFileName = "ggml-base.bin";
        public const string DetectionModelUrl = HuggingFaceBaseUrl + "/ggml-base.bin";
        public const long DetectionModelSizeBytes = 147951465;

        /// <summary>
        /// True if the given model (by file name or full path) is known to translate reliably.
        /// whisper.cpp's distilled "turbo" models were fine-tuned without the translate task and emit
        /// the source language instead of English, so they are flagged CanTranslate=false in the catalog.
        /// Returns true for unknown models (don't block/warn on models we don't recognize). (Issue #44.)
        /// </summary>
        public static bool IsTranslationCapable(string? modelFileNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(modelFileNameOrPath)) return true; // unknown → don't warn
            var name = Path.GetFileName(modelFileNameOrPath);
            var entry = Array.Find(Models, m => string.Equals(m.FileName, name, StringComparison.OrdinalIgnoreCase));
            return entry == null || entry.CanTranslate; // unknown model → assume capable (no false alarms)
        }
    }

    public class ModelEntry
    {
        public string FileName { get; }
        public string DisplayName { get; }
        public int SizeMB { get; }
        public bool IsRecommended { get; }
        public string Description { get; }
        public bool CanTranslate { get; }

        public ModelEntry(string fileName, string displayName, int sizeMB, bool isRecommended, string description, bool canTranslate)
        {
            FileName = fileName;
            DisplayName = displayName;
            SizeMB = sizeMB;
            IsRecommended = isRecommended;
            Description = description;
            CanTranslate = canTranslate;
        }
    }
}
