using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WhisperSubs.Providers
{
    /// <summary>One GPU render node this server can open, as the settings page shows it.</summary>
    public sealed record GpuNode(string Node, string PciAddress, bool VirtualFunction, string Driver);

    /// <summary>
    /// The GPU render nodes this server sees, and whether any is an SR-IOV virtual function. On an Intel iGPU
    /// split with SR-IOV, Vulkan compute on a virtual function hung it (GPU HANG, chip reset every few
    /// seconds, 2026-10-09), and a container given all of /dev/dri hands the engines those functions too.
    /// </summary>
    internal static class GpuInventory
    {
        /// <summary>
        /// The render nodes under <paramref name="devDri"/>, each matched to its PCI function through
        /// <paramref name="sysClassDrm"/>. A function with a <c>physfn</c> link is a virtual function.
        /// Never throws; an unreadable entry reads as unknown.
        /// </summary>
        internal static IReadOnlyList<GpuNode> Scan(string devDri = "/dev/dri", string sysClassDrm = "/sys/class/drm")
        {
            try
            {
                if (!Directory.Exists(devDri)) return Array.Empty<GpuNode>();
                return Directory.EnumerateFileSystemEntries(devDri, "renderD*")
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .Select(n => Describe(n, Path.Combine(sysClassDrm, n, "device")))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<GpuNode>();
            }
        }

        private static GpuNode Describe(string node, string device)
            => new(node, LinkName(device), Exists(Path.Combine(device, "physfn")), LinkName(Path.Combine(device, "driver")));

        private static string LinkName(string path)
        {
            try
            {
                var target = new DirectoryInfo(path).LinkTarget;
                return target == null ? "" : Path.GetFileName(target.TrimEnd('/'));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return "";
            }
        }

        private static bool Exists(string path)
        {
            try { return Directory.Exists(path) || File.Exists(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        /// <summary>What the settings page warns about the GPUs it sees, or null when there is nothing to say. Pure.</summary>
        internal static string? Warning(IReadOnlyList<GpuNode> nodes)
        {
            var virtualFunctions = nodes.Where(n => n.VirtualFunction).Select(n => n.Node).ToList();
            if (virtualFunctions.Count > 0)
            {
                var physical = nodes.Where(n => !n.VirtualFunction).Select(n => n.Node).ToList();
                return $"This server sees {nodes.Count} GPU render nodes, and {virtualFunctions.Count} of them are SR-IOV virtual functions ({string.Join(", ", virtualFunctions)}). "
                    + "Vulkan compute on a virtual function can hang it. Pass only the physical function"
                    + (physical.Count > 0 ? $" ({string.Join(", ", physical)} and its card node)" : "")
                    + " into the Jellyfin container instead of all of /dev/dri.";
            }
            return nodes.Count > 1
                ? $"This server sees {nodes.Count} GPU render nodes ({string.Join(", ", nodes.Select(n => n.Node))}). The engines use the first Vulkan device unless you set one below."
                : null;
        }
    }
}
