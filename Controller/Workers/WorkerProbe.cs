namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// Pure classification for the worker "Test connection" probe (v4.1.1). The probe now runs in two stages —
    /// a cheap reachability GET of the base URL, then a transcribe POST of a tiny silent clip — and this helper
    /// maps that outcome to the {ok, warning, message} the config page renders (green / yellow / red). It is split
    /// out of the coverage-excluded controller so every branch is unit-tested.
    ///
    /// The distinction it encodes is the whole point of the fix: a transcribe that TIMES OUT after the host was
    /// already proven reachable is NOT a failure. A modest GPU running a large model (e.g. large-v3 on an Intel
    /// iGPU) can take far longer than the probe's transcribe deadline to decode even the silent test clip —
    /// whisper decodes silence as a worst-case, full-length hallucination — so a timeout there is a WARNING, not a
    /// failure: the worker is reachable, authenticated and accepted the audio, and real jobs get a longer,
    /// duration-scaled deadline. Only a genuine transport failure (refused / DNS / TLS / host-unreachable) of the
    /// reachability GET — or a connection that drops mid-transcribe — is reported as "Unreachable".
    /// </summary>
    internal static class WorkerProbe
    {
        /// <summary>
        /// The transcribe-timeout warning copy, kept as a const so the wording has a single source of truth.
        /// The "30s" here must stay in step with the transcribe probe's overall timeout in the controller.
        /// </summary>
        internal const string TranscribeTimeoutMessage =
            "Reachable and it accepted the audio, but the test transcription didn't finish within 30s. " +
            "That's normal for a large model on a modest GPU (this probe uses a silent clip — a worst case for " +
            "decode length). The worker will still be used; real jobs get a longer, duration-scaled deadline.";

        /// <summary>
        /// Maps a probe outcome to the config-page result.
        /// <para>
        /// <paramref name="reachable"/> is false only for a genuine transport failure of the reachability GET (or a
        /// connection that dropped mid-transcribe); the caller appends the transport reason to the returned message
        /// (e.g. "Unreachable: Connection refused"). When reachable, <paramref name="timedOut"/> true means the
        /// transcribe exceeded its deadline, otherwise <paramref name="httpStatus"/> is the status the transcribe
        /// answered with; for a 2xx the caller appends the measured latency to the returned stem.
        /// </para>
        /// The classifier is total over its inputs (never throws) and applies its rules in precedence order:
        /// unreachable dominates, then a timeout, then the HTTP status.
        /// </summary>
        internal static (bool ok, bool warning, string message) ClassifyProbeOutcome(bool reachable, int? httpStatus, bool timedOut)
        {
            if (!reachable)
            {
                // Genuine transport failure. Caller appends ": <transport reason>".
                return (false, false, "Unreachable");
            }

            if (timedOut)
            {
                // Reachable and it accepted the audio — the decode is just slow. A warning (worker stays usable),
                // never a red failure. This is the false-negative the fix exists to prevent.
                return (true, true, TranscribeTimeoutMessage);
            }

            if (httpStatus is int status)
            {
                if (status >= 200 && status < 300)
                {
                    // Caller appends the measured round-trip: "Reachable, auth OK, transcription verified (1234 ms)."
                    return (true, false, "Reachable, auth OK, transcription verified");
                }

                if (status == 401 || status == 403)
                {
                    return (false, false, "Reachable, but authentication failed — check the API key.");
                }

                return (false, false, "Reachable, but the transcription endpoint returned HTTP " + status + ".");
            }

            // Reachable, didn't time out, yet carried no HTTP status: not produced by the controller's orchestration,
            // but the classifier stays total over its input domain rather than throwing.
            return (false, false, "Reachable, but the transcription endpoint returned no HTTP status.");
        }
    }
}
