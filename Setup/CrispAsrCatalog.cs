using System;
using System.Linq;

namespace WhisperSubs.Setup
{
    /// <summary>
    /// Binary variant catalog for CrispASR (https://github.com/CrispStrobe/CrispASR), the whisper.cpp
    /// fork that runs NVIDIA Canary for the non-English translation targets. Mirrors
    /// <see cref="RoformerCatalog"/>: CrispASR publishes its own release archives, so the download
    /// targets THAT upstream release, pinned to <see cref="Version"/>, and extracts an archive.
    /// whisper-cli stays the transcription engine; this binary only ever serves Canary routes.
    /// </summary>
    public static class CrispAsrCatalog
    {
        /// <summary>Pinned upstream CrispASR release tag. Bump only after re-running the compatibility spike.</summary>
        public const string Version = "v0.8.35";

        public const string ReleaseBaseUrl = "https://github.com/CrispStrobe/CrispASR/releases/download/" + Version;

        /// <summary>
        /// CPU is the default on purpose: Canary measured no speed-up on an integrated GPU over the
        /// CPU, and the CPU archive has no driver dependency. GPU variants are for discrete cards.
        /// </summary>
        public static readonly BinaryVariant[] LinuxX64Variants = new[]
        {
            new BinaryVariant("cpu", "CPU Only",
                "Works on any x86-64 system with AVX2. No GPU required. Recommended.", true),
            new BinaryVariant("cpu-legacy", "CPU (Compatibility)",
                "For older CPUs without AVX2. Slower than the standard CPU build.", false),
            new BinaryVariant("vulkan", "Vulkan (Intel / AMD / NVIDIA)",
                "Discrete-GPU acceleration through Vulkan. Needs a working Vulkan driver; it does not fall back to the CPU when the driver is missing.", false),
            new BinaryVariant("cuda12", "NVIDIA CUDA 12",
                "NVIDIA acceleration with CUDA 12. Falls back to the CPU when no usable GPU is found.", false),
            new BinaryVariant("cuda13", "NVIDIA CUDA 13",
                "NVIDIA acceleration with CUDA 13 (newer drivers). Falls back to the CPU when no usable GPU is found.", false),
            new BinaryVariant("hip", "AMD ROCm (HIP)",
                "AMD acceleration through ROCm. Needs the ROCm runtime; it does not fall back to the CPU when the driver is missing.", false),
        };

        private static readonly BinaryVariant[] WindowsVariants = new[]
        {
            new BinaryVariant("cpu", "CPU Only",
                "Works on any x86-64 system. No GPU required. Recommended.", true),
            new BinaryVariant("vulkan", "Vulkan (Intel / AMD / NVIDIA)",
                "Discrete-GPU acceleration through Vulkan. Needs a working Vulkan driver.", false),
        };

        private static readonly BinaryVariant[] CpuOnlyVariants = new[]
        {
            new BinaryVariant("cpu", "CPU Only",
                "Works on any system. No GPU required.", true),
        };

        private static readonly BinaryVariant[] MacVariants = new[]
        {
            new BinaryVariant("cpu", "Apple Metal / CPU",
                "Uses Apple Metal when available, with CPU fallback.", true),
        };

        /// <summary>
        /// Returns only the variants CrispASR publishes prebuilt archives for on the given platform.
        /// The macOS archive is arm64-only, so Intel Macs get no variants.
        /// </summary>
        public static BinaryVariant[] GetAvailableVariants(string platform) => platform switch
        {
            "linux-x64" => LinuxX64Variants,
            "win-x64" => WindowsVariants,
            "linux-arm64" => CpuOnlyVariants,
            "osx-arm64" => MacVariants,
            _ => Array.Empty<BinaryVariant>()
        };

        /// <summary>Maps (platform, variant) to the exact upstream release asset file name.</summary>
        public static string GetAssetName(string platform, string variant) => (platform, variant) switch
        {
            ("linux-x64", "cpu") => "crispasr-linux-x86_64.tar.gz",
            ("linux-x64", "cpu-legacy") => "crispasr-linux-x86_64-cpu-legacy.tar.gz",
            ("linux-x64", "vulkan") => "crispasr-linux-x86_64-vulkan.tar.gz",
            ("linux-x64", "cuda12") => "crispasr-linux-x86_64-cuda.tar.gz",
            ("linux-x64", "cuda13") => "crispasr-linux-x86_64-cuda13.tar.gz",
            ("linux-x64", "hip") => "crispasr-linux-x86_64-hip.tar.gz",
            ("linux-arm64", "cpu") => "crispasr-linux-arm64.tar.gz",
            ("osx-arm64", "cpu") => "crispasr-macos.tar.gz",
            ("win-x64", "cpu") => "crispasr-windows-x86_64-cpu.zip",
            ("win-x64", "vulkan") => "crispasr-windows-x86_64-vulkan.zip",
            _ => throw new NotSupportedException($"No CrispASR release asset for platform '{platform}' variant '{variant}'.")
        };

        /// <summary>
        /// SHA-256 published by GitHub for each pinned v0.8.35 release asset. Keeping the digest next
        /// to the asset mapping prevents a mutable release download from being executed unchecked.
        /// </summary>
        public static string GetAssetSha256(string platform, string variant) => (platform, variant) switch
        {
            ("linux-x64", "cpu") => "2a9982f69c8ee714cab81d8697ef8fb76878ee76eeb878cf65ddce1e443f9ff9",
            ("linux-x64", "cpu-legacy") => "37e5a1adf91b06c400a9da391cfa05aeec9e2aa7e393677c2227b1043a619c8d",
            ("linux-x64", "vulkan") => "c94ebfa24da74b3a5c8a6ea13be16e99e7c885b75721e3e9db3883f9406ced21",
            ("linux-x64", "cuda12") => "7413c6100cd419d39be5c76c7d0a41c0bad36cb82e9c96e4b701765ed8a5027c",
            ("linux-x64", "cuda13") => "f6d116bb75c740aac94ce7e0366d1583d08a7f6c0b07ec05a6b396a70521b30e",
            ("linux-x64", "hip") => "bb0ccb4b8cbf813873f1d9faed77a9fd4ab371aa0f32732ec74dcffe519337bb",
            ("linux-arm64", "cpu") => "5b1ae7df881d09bed899ad67e8cabb7241f5664c891a64071a952fb742747238",
            ("osx-arm64", "cpu") => "0fa1aca0f102c2ea428a357eef143843a19bc122020bfa71cc3b4f984d8ecdf8",
            ("win-x64", "cpu") => "fb812ef200ffcf7de55e9be900a39a440c83a3facb09d7df9020d5a3b56ff280",
            ("win-x64", "vulkan") => "8c4f6483d3dad9ab4066f3853b48306e16e5b4010d1bf42054c81bf439166b3f",
            _ => throw new NotSupportedException($"No CrispASR release digest for platform '{platform}' variant '{variant}'.")
        };

        /// <summary>Exact byte sizes published for the pinned v0.8.35 assets.</summary>
        public static long GetAssetSizeBytes(string platform, string variant) => (platform, variant) switch
        {
            ("linux-x64", "cpu") => 39924568,
            ("linux-x64", "cpu-legacy") => 39718726,
            ("linux-x64", "vulkan") => 73850252,
            ("linux-x64", "cuda12") => 165318168,
            ("linux-x64", "cuda13") => 128658689,
            ("linux-x64", "hip") => 107165881,
            ("linux-arm64", "cpu") => 33301701,
            ("osx-arm64", "cpu") => 16830142,
            ("win-x64", "cpu") => 8462806,
            ("win-x64", "vulkan") => 36826421,
            _ => throw new NotSupportedException($"No CrispASR release size for platform '{platform}' variant '{variant}'.")
        };

        /// <summary>The crispasr executable name inside the extracted archive, per platform.</summary>
        public static string ExecutableFileName(string platform) =>
            platform.StartsWith("win", StringComparison.OrdinalIgnoreCase) ? "crispasr.exe" : "crispasr";

        /// <summary>
        /// Maps a variant that failed validation to the next more-compatible one on the same platform:
        /// every GPU build falls back to "cpu", and on Linux x64 "cpu" falls back to "cpu-legacy" for
        /// CPUs without AVX2. Returns null at the end of the chain or when the fallback is not
        /// published for <paramref name="platform"/>.
        /// </summary>
        internal static string? GetFallbackVariant(string platform, string variant)
        {
            var next = variant switch
            {
                "vulkan" or "cuda12" or "cuda13" or "hip" => "cpu",
                "cpu" => "cpu-legacy",
                _ => null
            };
            return next != null && GetAvailableVariants(platform).Any(v => v.Id == next) ? next : null;
        }
    }
}
