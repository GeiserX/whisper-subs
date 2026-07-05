namespace WhisperSubs.Controller
{
    /// <summary>
    /// Named subtitle-queue priority tiers. The <b>integer backing value drives dequeue order</b>:
    /// a LOWER number is HIGHER priority, so <see cref="Critical"/> (0) is served before
    /// <see cref="Background"/> (4). These values are PERSISTED to queue.json — never renumber or
    /// reorder them; only add new tiers with new distinct values. (Issue #112.)
    /// </summary>
    public enum PriorityTier
    {
        /// <summary>Highest priority — served before everything else.</summary>
        Critical = 0,
        /// <summary>Default tier for an admin Generate / Generate-all request.</summary>
        High = 1,
        /// <summary>Default tier for a user request.</summary>
        Medium = 2,
        /// <summary>Low priority — above the background sweep, below normal requests.</summary>
        Low = 3,
        /// <summary>Lowest priority — the background "fill every gap" sweep.</summary>
        Background = 4
    }

    /// <summary>
    /// Who asked for a job. The server maps this to a <see cref="PriorityTier"/> using the admin's
    /// configured mapping — the tier is ALWAYS derived from this server-known role, never sent by or
    /// trusted from the client. (Issue #112.)
    /// </summary>
    public enum RequesterKind
    {
        /// <summary>An admin used Generate / Generate-all in the plugin UI.</summary>
        Admin,
        /// <summary>A non-admin user requested subtitles (the opt-in request feature).</summary>
        User,
        /// <summary>The scheduled background "fill every gap" sweep.</summary>
        BackgroundSweep
    }
}
