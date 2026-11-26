using System;
using System.Collections.Generic;
using System.Linq;

namespace XwvmChatter
{
    /// <summary>
    /// Keeps the radio from repeating itself: remembers which script lines played, per situation, and narrows a
    /// pool of candidate lines to the ones heard longest ago. No game equivalent (the game has one clip set per call).
    /// </summary>
    public sealed class LineHistory
    {
        // Lines remembered per situation: more than any situation has, so the whole order of a mission is kept.
        private const int Remembered = 64;

        // script lines played per situation, oldest first
        private readonly Dictionary<string, List<Line>> _played = new Dictionary<string, List<Line>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The part of <paramref name="pool"/> that may play now. Lines are compared by their words, since several takes
        /// of one script line share its text:
        ///   - a line not yet heard this mission always wins (the whole set plays before anything repeats);
        ///   - after that, only the half of the set heard longest ago is eligible;
        ///   - when every line has the same words, at least not the take that played last.
        /// Never returns an empty list for a non-empty pool.
        /// </summary>
        public List<Line> Fresh(List<Line> pool, string situationId)
        {
            if (pool.Count <= 1 || !_played.TryGetValue(situationId, out var played) || played.Count == 0) return pool;

            // position of the line in the played list (higher = more recent), -1 when never heard
            int LastHeard(Line line)
            {
                for (int i = played.Count - 1; i >= 0; i--)
                    if (SameLine(line, played[i])) return i;
                return -1;
            }

            var byWords = pool.GroupBy(l => l.Text ?? "", StringComparer.OrdinalIgnoreCase)
                              .Select(g => (lines: g.ToList(), heard: g.Max(LastHeard)))
                              .OrderBy(x => x.heard).ToList();
            if (byWords.Count > 1)
            {
                var neverHeard = byWords.Where(x => x.heard < 0).SelectMany(x => x.lines).ToList();
                if (neverHeard.Count > 0) return neverHeard;
                return byWords.Take(Math.Max(1, byWords.Count / 2)).SelectMany(x => x.lines).ToList();
            }
            var lastPlayed = played[played.Count - 1];
            var otherTakes = pool.Where(l => !string.Equals(l.AudioFile, lastPlayed.AudioFile, StringComparison.OrdinalIgnoreCase)).ToList();
            return otherTakes.Count > 0 ? otherTakes : pool;
        }

        /// <summary>Record that a line played. A callsign version counts as the script line it stands for
        /// (<see cref="Line.BaseLine"/>): its own text names a callsign and would never match the plain pool.</summary>
        public void Remember(string situationId, Line line)
        {
            var scriptLine = line.BaseLine ?? line;
            if (!_played.TryGetValue(situationId, out var played)) _played[situationId] = played = new List<Line>();
            played.RemoveAll(l => SameLine(l, scriptLine));
            played.Add(scriptLine);
            if (played.Count > Remembered) played.RemoveAt(0);
        }

        /// <summary>Same script line = same words.</summary>
        private static bool SameLine(Line a, Line b) =>
            ReferenceEquals(a, b) || string.Equals(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);
    }
}
