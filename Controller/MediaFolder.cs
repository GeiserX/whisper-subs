using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace WhisperSubs.Controller
{
    /// <summary>
    /// <see cref="Directory.GetFiles(string, string)"/> for the folders that hold media, without following a
    /// symbolic link for every entry that is not a match. .NET's own enumeration asks the target of every
    /// symbolic link it meets whether it is a folder, before any name filter runs. A movie folder holding
    /// 27,883 entries, 1,949 of them links into a network share, took 3 to 7 minutes per listing that way
    /// (measured 2026-10-08), and the sweep lists the folder several times per title. On Linux this reads
    /// the names with libc's <c>readdir</c>, which never looks at a link's target, and checks only the names
    /// that match. Anywhere else, or if that fails, it is <see cref="Directory.GetFiles(string, string)"/>.
    /// </summary>
    internal static class MediaFolder
    {
        /// <summary>
        /// The files in <paramref name="directory"/> (top level only) whose names match
        /// <paramref name="searchPattern"/>, with <see cref="Directory.GetFiles(string, string)"/>'s rules:
        /// Win32 wildcards, case-sensitive on Linux, directories (and links to them) left out.
        /// </summary>
        public static string[] GetFiles(string directory, string searchPattern)
        {
            var names = ReadNames(directory);
            if (names == null) return Directory.GetFiles(directory, searchPattern);
            return Matching(directory, names, searchPattern, Directory.Exists).ToArray();
        }

        /// <summary>
        /// The full paths of the names that match <paramref name="searchPattern"/> as
        /// <see cref="Directory.GetFiles(string, string)"/> matches them, leaving out "." and ".." and every
        /// match <paramref name="isDirectory"/> says is a folder. Only matching names are checked, so a folder
        /// full of links costs nothing for the links that do not match. Pure.
        /// </summary>
        internal static List<string> Matching(string directory, IEnumerable<string> names, string searchPattern, Func<string, bool> isDirectory)
        {
            var expression = FileSystemName.TranslateWin32Expression(searchPattern);
            var files = new List<string>();
            foreach (var name in names)
            {
                if (name is "." or "..") continue;
                if (!FileSystemName.MatchesWin32Expression(expression, name, ignoreCase: false)) continue;
                var path = Path.Join(directory, name);
                if (isDirectory(path)) continue;
                files.Add(path);
            }
            return files;
        }

        /// <summary>
        /// Every entry name in <paramref name="directory"/>, read with libc's opendir/readdir on 64-bit Linux;
        /// null where that is not available or anything goes wrong, so the caller falls back to .NET.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "P/Invoke into libc; Matching and the equivalence with Directory.GetFiles are unit-tested")]
        internal static List<string>? ReadNames(string directory)
        {
            if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess) return null;
            try
            {
                var dir = opendir(directory);
                if (dir == IntPtr.Zero) return null;
                try
                {
                    var names = new List<string>();
                    while (true)
                    {
                        Marshal.SetLastSystemError(0);
                        var entry = readdir(dir);
                        if (entry == IntPtr.Zero)
                        {
                            // NULL with errno set is a read error, not the end of the folder: do not trust a partial list.
                            return Marshal.GetLastPInvokeError() == 0 ? names : null;
                        }
                        var name = Marshal.PtrToStringUTF8(entry + DirentNameOffset);
                        if (name != null) names.Add(name);
                    }
                }
                finally
                {
                    closedir(dir);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                return null;
            }
        }

        // struct dirent on 64-bit Linux, glibc and musl alike: d_ino (8), d_off (8), d_reclen (2), d_type (1), d_name.
        private const int DirentNameOffset = 19;

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr opendir([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr readdir(IntPtr dir);

        [DllImport("libc")]
        private static extern int closedir(IntPtr dir);
    }
}
