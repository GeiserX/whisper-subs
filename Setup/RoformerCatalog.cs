using System;
using System.Linq;

namespace WhisperSubs.Setup
{
    /// <summary>
    /// Binary variant catalog for BSRoformer.cpp (https://github.com/chenmozhijin/BSRoformer.cpp),
    /// the standalone vocal-separation CLI. Mirrors <see cref="BinaryCatalog"/>'s shape, but unlike
    /// whisper-cli (custom-built by this project's own CI), BSRoformer.cpp publishes prebuilt
    /// archives directly on its GitHub releases — so the download here targets THAT upstream release,
    /// pinned to <see cref="Version"/>, and extracts an archive instead of fetching a raw binary.
    /// </summary>
    public static class RoformerCatalog
    {
        /// <summary>Pinned upstream BSRoformer.cpp release tag. Bump deliberately, alongside a compatibility check.</summary>
        public const string Version = "v0.1.0";

        public const string ReleaseBaseUrl = "https://github.com/chenmozhijin/BSRoformer.cpp/releases/download/" + Version;

        public static readonly BinaryVariant[] Variants = new[]
        {
            new BinaryVariant("cpu", "CPU Only",
                "Works on any system. No GPU required.", true),
            new BinaryVariant("vulkan", "Vulkan (Intel / AMD / NVIDIA)",
                "Cross-vendor GPU acceleration via Vulkan. Works with Intel iGPU, AMD, and NVIDIA.", false),
            new BinaryVariant("cuda12", "NVIDIA CUDA 12",
                "Hardware-accelerated via NVIDIA GPU (CUDA 12.9). Requires NVIDIA drivers.", false),
        };

        /// <summary>
        /// Returns only the variants BSRoformer.cpp actually publishes prebuilt archives for on the
        /// given platform. Linux x64 (the common Jellyfin container target) and Windows x64 both
        /// get the full set of CPU/Vulkan/CUDA archives; other supported platforms only expose CPU.
        /// </summary>
        public static BinaryVariant[] GetAvailableVariants(string platform) => platform switch
        {
            "linux-x64" => Variants,
            "win-x64" => Variants,
            "linux-arm64" or "osx-arm64" or "osx-x64" => Variants.Where(v => v.Id == "cpu").ToArray(),
            _ => Array.Empty<BinaryVariant>()
        };

        /// <summary>
        /// Maps (platform, variant) to the exact upstream BSRoformer.cpp release asset file name.
        /// Unlike whisper-cli's own release, these are ARCHIVES (.tar.xz on Linux/macOS, .zip on
        /// Windows) — the caller must extract before use.
        /// </summary>
        public static string GetAssetName(string platform, string variant) => (platform, variant) switch
        {
            ("linux-x64", "cpu") => "BSRoformer-linux-x64-cpu.tar.xz",
            ("linux-x64", "vulkan") => "BSRoformer-linux-vulkan.tar.xz",
            ("linux-x64", "cuda12") => "BSRoformer-linux-cuda-12.9.1.tar.xz",
            ("linux-arm64", "cpu") => "BSRoformer-linux-arm64-cpu.tar.xz",
            ("osx-arm64", "cpu") => "BSRoformer-macos-arm64.tar.xz",
            ("osx-x64", "cpu") => "BSRoformer-macos-x64.tar.xz",
            ("win-x64", "cpu") => "BSRoformer-windows-x64-msvc.zip",
            ("win-x64", "vulkan") => "BSRoformer-windows-vulkan.zip",
            ("win-x64", "cuda12") => "BSRoformer-windows-cuda-12.9.1.zip",
            _ => throw new NotSupportedException($"No BSRoformer.cpp release asset for platform '{platform}' variant '{variant}'.")
        };

        /// <summary>The bs_roformer-cli executable name inside the extracted archive, per platform.</summary>
        public static string ExecutableFileName(string platform) =>
            platform.StartsWith("win", StringComparison.OrdinalIgnoreCase) ? "bs_roformer-cli.exe" : "bs_roformer-cli";

        /// <summary>
        /// Maps a failed GPU variant to the next more-compatible one (mirrors
        /// <see cref="WhisperSetupService.GetFallbackVariant"/>). BSRoformer.cpp has no "noavx"-style
        /// compatibility build, so "cpu" is the terminal sink.
        /// </summary>
        internal static string? GetFallbackVariant(string variant) => variant switch
        {
            "cuda12" or "vulkan" => "cpu",
            _ => null
        };
    }
}
