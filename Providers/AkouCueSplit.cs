using System;
using System.Collections.Generic;
using System.Linq;

namespace WhisperSubs.Providers
{
    /// <summary>
    /// Splits akou's long segments into cues a viewer can read. akou's <c>best</c> preset (Qwen3-ASR) gives
    /// no word times, so the plugin had one cue per segment: on real titles the median cue ran 3.4-11.7 s
    /// and the longest 24.7-27.7 s, against at most 5.5 s in human subtitles. A segment that already fits
    /// stays as it is. A longer one breaks at sentence ends, then into pieces of at most
    /// <see cref="MaxChars"/> characters (two 42-character lines), and any piece still over
    /// <see cref="MaxSeconds"/> splits again at word boundaries. The segment's time is shared among its
    /// pieces by character count, so they are contiguous and stay inside the segment. A stand-in until akou
    /// returns word times for Qwen; then the word path (<see cref="RemoteWhisperProvider.CutCues"/>) takes over.
    /// </summary>
    internal static class AkouCueSplit
    {
        /// <summary>Characters per cue: two lines of 42.</summary>
        internal const int MaxChars = 84;

        /// <summary>Seconds per cue.</summary>
        internal const double MaxSeconds = 7.0;

        /// <summary>The shortest piece a split may make; a segment too short for its pieces is kept whole.</summary>
        internal const double MinPieceSeconds = 0.2;

        /// <summary>
        /// The shortest cue a sentence break may leave, five sixths of a second (the floor common subtitle style
        /// guides set); a shorter sentence joins its neighbour.
        /// </summary>
        internal const double MinCueSeconds = 5.0 / 6.0;

        /// <summary>Splits every cue that is too long. Pure.</summary>
        internal static List<(double Start, double End, string Text)> Split(
            IReadOnlyList<(double Start, double End, string Text)> cues, int maxChars = MaxChars, double maxSeconds = MaxSeconds)
        {
            var result = new List<(double Start, double End, string Text)>();
            foreach (var cue in cues) result.AddRange(SplitOne(cue, maxChars, maxSeconds));
            return result;
        }

        private static IEnumerable<(double Start, double End, string Text)> SplitOne(
            (double Start, double End, string Text) cue, int maxChars, double maxSeconds)
        {
            var duration = cue.End - cue.Start;
            var words = (cue.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var flatLength = words.Sum(w => w.Length) + Math.Max(0, words.Length - 1);
            if ((flatLength <= maxChars && duration <= maxSeconds) || words.Length < 2 || duration <= 0)
            {
                return new[] { cue };
            }

            var pieces = MergeShort(SentencePieces(words, maxChars), duration, maxChars);
            if (duration < pieces.Count * MinPieceSeconds)
            {
                // Too short to give every sentence its own cue: pack by length alone.
                pieces = PackedPieces(words, maxChars);
                if (duration < pieces.Count * MinPieceSeconds) return new[] { cue };
            }

            // A piece whose share of the time is still too long splits at word boundaries, until none is
            // (a one-word piece stays, however long).
            for (var round = 0; round < 16; round++)
            {
                var weights = pieces.Select(Length).ToList();
                var total = (double)weights.Sum();
                var next = new List<List<string>>();
                var changed = false;
                for (var i = 0; i < pieces.Count; i++)
                {
                    var pieceSeconds = duration * weights[i] / total;
                    var parts = (int)Math.Ceiling(pieceSeconds / maxSeconds);
                    if (parts > 1 && pieces[i].Count > 1
                        && duration >= (pieces.Count - 1 + Math.Min(parts, pieces[i].Count)) * MinPieceSeconds)
                    {
                        next.AddRange(Balanced(pieces[i], Math.Min(parts, pieces[i].Count)));
                        changed = true;
                    }
                    else
                    {
                        next.Add(pieces[i]);
                    }
                }
                pieces = next;
                if (!changed) break;
            }

            return Timed(cue.Start, cue.End, pieces);
        }

        /// <summary>Breaks at sentence ends, and inside a sentence wherever the next word would pass the limit.</summary>
        private static List<List<string>> SentencePieces(string[] words, int maxChars)
        {
            var pieces = new List<List<string>>();
            var current = new List<string>();
            var length = 0;
            foreach (var word in words)
            {
                var grown = current.Count == 0 ? word.Length : length + 1 + word.Length;
                if (current.Count > 0 && grown > maxChars)
                {
                    pieces.Add(current);
                    current = new List<string>();
                    grown = word.Length;
                }
                current.Add(word);
                length = grown;
                if (RemoteWhisperProvider.EndsSentence(word))
                {
                    pieces.Add(current);
                    current = new List<string>();
                    length = 0;
                }
            }
            if (current.Count > 0) pieces.Add(current);
            return pieces;
        }

        /// <summary>
        /// Joins a piece whose share of the time is under <see cref="MinCueSeconds"/> to a neighbour: a short
        /// sentence ("No.") inside a long segment would otherwise be a cue too brief to read. The neighbour is the
        /// one the pair fits <paramref name="maxChars"/> with, the shorter when both or neither do; a pair over the
        /// limit by a short piece's few characters reads better than a cue of a fifth of a second.
        /// </summary>
        private static List<List<string>> MergeShort(List<List<string>> pieces, double duration, int maxChars)
        {
            while (pieces.Count > 1)
            {
                var total = (double)pieces.Sum(Length);
                var shortest = -1;
                for (var i = 0; i < pieces.Count; i++)
                {
                    if (duration * Length(pieces[i]) / total >= MinCueSeconds) continue;
                    if (shortest < 0 || Length(pieces[i]) < Length(pieces[shortest])) shortest = i;
                }
                if (shortest < 0) break;

                var left = shortest > 0 ? shortest - 1 : -1;
                var right = shortest < pieces.Count - 1 ? shortest + 1 : -1;
                int Joined(int other) => Length(pieces[shortest]) + 1 + Length(pieces[other]);
                int into;
                if (left < 0) into = right;
                else if (right < 0) into = left;
                else
                {
                    var leftFits = Joined(left) <= maxChars;
                    var rightFits = Joined(right) <= maxChars;
                    into = leftFits != rightFits
                        ? (leftFits ? left : right)
                        : (Length(pieces[left]) <= Length(pieces[right]) ? left : right);
                }

                var merged = into < shortest
                    ? pieces[into].Concat(pieces[shortest]).ToList()
                    : pieces[shortest].Concat(pieces[into]).ToList();
                var first = Math.Min(into, shortest);
                pieces[first] = merged;
                pieces.RemoveAt(first + 1);
            }
            return pieces;
        }

        /// <summary>Packs words into as few pieces of at most <paramref name="maxChars"/> as fit.</summary>
        private static List<List<string>> PackedPieces(string[] words, int maxChars)
        {
            var pieces = new List<List<string>>();
            var current = new List<string>();
            var length = 0;
            foreach (var word in words)
            {
                var grown = current.Count == 0 ? word.Length : length + 1 + word.Length;
                if (current.Count > 0 && grown > maxChars)
                {
                    pieces.Add(current);
                    current = new List<string>();
                    grown = word.Length;
                }
                current.Add(word);
                length = grown;
            }
            if (current.Count > 0) pieces.Add(current);
            return pieces;
        }

        /// <summary>Splits a word list into <paramref name="parts"/> runs of about equal length, none empty.</summary>
        private static IEnumerable<List<string>> Balanced(List<string> words, int parts)
        {
            var total = (double)Length(words);
            var result = new List<List<string>>();
            var current = new List<string>();
            var done = 0;
            for (var i = 0; i < words.Count; i++)
            {
                current.Add(words[i]);
                done += words[i].Length + (current.Count > 1 ? 1 : 0);
                var wordsLeft = words.Count - i - 1;
                var groupsLeft = parts - result.Count - 1;
                if (groupsLeft > 0 && (done >= total * (result.Count + 1) / parts || wordsLeft == groupsLeft))
                {
                    result.Add(current);
                    current = new List<string>();
                }
            }
            if (current.Count > 0) result.Add(current);
            return result;
        }

        private static int Length(List<string> words) => words.Sum(w => w.Length) + Math.Max(0, words.Count - 1);

        /// <summary>Shares start-to-end among the pieces by their length: contiguous, the last ending at <paramref name="end"/>.</summary>
        private static IEnumerable<(double Start, double End, string Text)> Timed(double start, double end, List<List<string>> pieces)
        {
            var weights = pieces.Select(Length).ToList();
            var total = (double)weights.Sum();
            var duration = end - start;
            var done = 0.0;
            var result = new List<(double, double, string)>();
            for (var i = 0; i < pieces.Count; i++)
            {
                var from = start + duration * done / total;
                done += weights[i];
                var to = i == pieces.Count - 1 ? end : start + duration * done / total;
                result.Add((from, to, string.Join(' ', pieces[i])));
            }
            return result;
        }
    }
}
