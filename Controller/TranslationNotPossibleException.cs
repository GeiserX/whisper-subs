using System;

namespace WhisperSubs.Controller
{
    /// <summary>
    /// A translate job that can never succeed as asked: the audio is not English for a Canary target, no
    /// engine serves the target, the target is not supported, or the item is not a video with a file. The
    /// answer is the same on every attempt, so the dispatcher records it and does not retry.
    /// </summary>
    public sealed class TranslationNotPossibleException : InvalidOperationException
    {
        public TranslationNotPossibleException(string message) : base(message) { }
    }
}
