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
                "Experimental cross-vendor GPU acceleration. Requires a compatible Vulkan runtime and driver.", false),
            new BinaryVariant("cuda12", "NVIDIA CUDA 12",
                "Experimental NVIDIA acceleration (CUDA 12.9). Requires a compatible NVIDIA driver and GPU.", false),
        };

        private static readonly BinaryVariant[] MacVariants = new[]
        {
            new BinaryVariant("cpu", "Apple Metal / CPU",
                "Uses Apple Metal when available, with CPU fallback.", true),
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
            "linux-arm64" => Variants.Where(v => v.Id == "cpu").ToArray(),
            "osx-arm64" or "osx-x64" => MacVariants,
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

        /// <summary>
        /// SHA-256 published by GitHub for each pinned v0.1.0 release asset. Keeping the digest next
        /// to the asset mapping prevents a mutable release download from being executed unchecked.
        /// </summary>
        public static string GetAssetSha256(string platform, string variant) => (platform, variant) switch
        {
            ("linux-x64", "cpu") => "bc0f20237b9ed263582ebd0844dfc7dbb61309a67c31f4a8d7ba156e21292c77",
            ("linux-x64", "vulkan") => "bee2e9b5dd322b8d4fe1081583ce41709c2c1341550bbb557073c1216fef3e85",
            ("linux-x64", "cuda12") => "448289d5162062ebd2fb7af3c1a1d88297438aef15b0e8369f6b101e82b4983c",
            ("linux-arm64", "cpu") => "f77537a1b990d1d48036bc2c8ce8c354530ba2476903ec6177ed0d1fd60079ea",
            ("osx-arm64", "cpu") => "d0fc45181d31dcceea99c3828378f4a7376e52e7fdbc43e29a07e806785b4c9a",
            ("osx-x64", "cpu") => "9672c9c10128df822395fb7ef2a1a52f5ed7becbae2af349351a38a1af333379",
            ("win-x64", "cpu") => "e002811d56605bce6a51c275cf8f9ba447a3707771289ea6fbcca7f4d3e9ba1f",
            ("win-x64", "vulkan") => "a47653911eb17f68e65bce15aed6c4f1bd277a5fbd48293c4e54f0b005b869a7",
            ("win-x64", "cuda12") => "7b8b93bc180e16f15124e4b739b0a9e9d038669e21fdf975f63823c02127bd70",
            _ => throw new NotSupportedException($"No BSRoformer.cpp release digest for platform '{platform}' variant '{variant}'.")
        };

        /// <summary>Exact byte sizes published for the pinned v0.1.0 assets.</summary>
        public static long GetAssetSizeBytes(string platform, string variant) => (platform, variant) switch
        {
            ("linux-x64", "cpu") => 640204,
            ("linux-x64", "vulkan") => 5900692,
            ("linux-x64", "cuda12") => 237738556,
            ("linux-arm64", "cpu") => 583476,
            ("osx-arm64", "cpu") => 636996,
            ("osx-x64", "cpu") => 780976,
            ("win-x64", "cpu") => 671031,
            ("win-x64", "vulkan") => 22779026,
            ("win-x64", "cuda12") => 140056366,
            _ => throw new NotSupportedException($"No BSRoformer.cpp release size for platform '{platform}' variant '{variant}'.")
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
