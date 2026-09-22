using System;

namespace WhisperSubs.Providers
{
    /// <summary>
    /// whisper-cli exited without ever transcribing anything: a missing shared library (exit 127) or a
    /// fatal signal on launch (132 SIGILL / 134 SIGABRT / 135 SIGBUS). A distinct type because nothing
    /// about the ITEM caused it — the same binary fails identically for every item, so the dispatcher
    /// takes the local worker out of rotation instead of burning each queued item's retry budget against
    /// a process that never started. (Issue #185: a CUDA build in a container recreated without the
    /// NVIDIA runtime lost libcuda.so.1 and failed 315 queued items the same way.)
    /// </summary>
    public sealed class WhisperLaunchException : InvalidOperationException
    {
        public WhisperLaunchException(int exitCode, string message) : base(message) => ExitCode = exitCode;

        /// <summary>The whisper-cli exit code that identified this as a launch failure.</summary>
        public int ExitCode { get; }

        /// <summary>
        /// The launch failure anywhere in <paramref name="ex"/>'s inner-exception chain, or null when
        /// there is none. Needed because the failure is re-wrapped on its way out:
        /// <c>SubtitleManager.GenerateSubtitleAsync</c> reports "all N attempt(s) failed" with the first
        /// per-pass error as the inner exception, so the dispatcher never sees the launch failure at the
        /// top level. The walk is depth-bounded so a self-referencing chain cannot hang dispatch.
        /// </summary>
        public static WhisperLaunchException? Find(Exception? ex)
        {
            var depth = 0;
            for (var e = ex; e != null && depth < 16; e = e.InnerException, depth++)
            {
                if (e is WhisperLaunchException launch) return launch;
            }
            return null;
        }
    }
}
