using System;
using System.Collections.Generic;
using System.Linq;

namespace WhisperSubs.Setup
{
    /// <summary>
    /// NVIDIA Canary-1B-v2 (CC-BY-4.0) GGUF weights for CrispASR, from the cstr/canary-1b-v2-GGUF
    /// Hugging Face repository, plus the fixed list of languages Canary can translate English audio
    /// into. Canary only translates between English and these 24 languages; there is no direct path
    /// between two non-English languages, which is why <see cref="Controller.TranslationRoute"/>
    /// admits only English audio for these targets.
    /// </summary>
    public static class CanaryCatalog
    {
        /// <summary>
        /// Pinned repository revision. The SHA-256 of each file is recorded too, so pinning the
        /// revision keeps a later upstream re-upload from turning into a checksum failure.
        /// </summary>
        public const string HuggingFaceRevision = "f4a12db73daa964aa56a188826682f6b11fdc960";

        public const string HuggingFaceBaseUrl =
            "https://huggingface.co/cstr/canary-1b-v2-GGUF/resolve/" + HuggingFaceRevision;

        public const string DefaultKey = "q8_0";

        public static readonly CanaryModelOption[] Models = new[]
        {
            new CanaryModelOption("q8_0", "canary-1b-v2-q8_0.gguf", "Q8_0 (recommended)", 1049, 1049050784,
                "81421a82cdcb23746f6c3e8c2859094f7e8c27dd8ba77d4b8f8440395fbc7c7a",
                "Near full-precision quality. Best default.", IsRecommended: true),
            new CanaryModelOption("q5_0", "canary-1b-v2-q5_0.gguf", "Q5_0 (smaller)", 720, 720322208,
                "e312694d877c0df7efe8ffc974cdf74bf06605c154552af214ee279741325703",
                "Smaller download and memory footprint, slightly lower quality.", IsRecommended: false),
        };

        /// <summary>
        /// Resolves a quantization key to its catalog entry. Unknown, empty or null keys fall back to
        /// <see cref="DefaultKey"/> so a stale config value never breaks setup.
        /// </summary>
        public static CanaryModelOption Resolve(string? key)
            => Array.Find(Models, m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))
               ?? Models.First(m => m.Key == DefaultKey);

        /// <summary>
        /// The 24 non-English languages Canary translates English speech into, as ISO 639-1 codes
        /// with English display names, in the model card's order. English itself is not listed: an
        /// English subtitle always comes from Whisper.
        /// </summary>
        public static readonly IReadOnlyList<CanaryTarget> Targets = new[]
        {
            new CanaryTarget("bg", "Bulgarian"),
            new CanaryTarget("hr", "Croatian"),
            new CanaryTarget("cs", "Czech"),
            new CanaryTarget("da", "Danish"),
            new CanaryTarget("nl", "Dutch"),
            new CanaryTarget("et", "Estonian"),
            new CanaryTarget("fi", "Finnish"),
            new CanaryTarget("fr", "French"),
            new CanaryTarget("de", "German"),
            new CanaryTarget("el", "Greek"),
            new CanaryTarget("hu", "Hungarian"),
            new CanaryTarget("it", "Italian"),
            new CanaryTarget("lv", "Latvian"),
            new CanaryTarget("lt", "Lithuanian"),
            new CanaryTarget("mt", "Maltese"),
            new CanaryTarget("pl", "Polish"),
            new CanaryTarget("pt", "Portuguese"),
            new CanaryTarget("ro", "Romanian"),
            new CanaryTarget("sk", "Slovak"),
            new CanaryTarget("sl", "Slovenian"),
            new CanaryTarget("es", "Spanish"),
            new CanaryTarget("sv", "Swedish"),
            new CanaryTarget("ru", "Russian"),
            new CanaryTarget("uk", "Ukrainian"),
        };

        /// <summary>True when <paramref name="code"/> is one of <see cref="Targets"/> (case-insensitive).</summary>
        public static bool IsTarget(string? code)
            => !string.IsNullOrWhiteSpace(code)
               && Targets.Any(t => string.Equals(t.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One language Canary can translate English speech into.</summary>
    public sealed record CanaryTarget(string Code, string DisplayName);

    /// <summary>
    /// A selectable Canary GGUF quantization: the stable <see cref="Key"/> stored in config, the
    /// upstream file name, its exact size and SHA-256, a UI label and whether it is the default.
    /// </summary>
    public sealed record CanaryModelOption(
        string Key,
        string FileName,
        string DisplayName,
        int SizeMB,
        long SizeBytes,
        string Sha256,
        string Description,
        bool IsRecommended);
}
