using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XwvmChatter
{
    /// <summary>
    /// The generic pool: a handful of voices that stand in for every friendly wingman a mission did not cast.
    /// This decides which pool voice sits in which seat. It needs only the mission's name, its seats and the
    /// casting, so the loading screen (before any ship exists) and the flight reach the same answer.
    /// No game equivalent: the game has one stock voice set for all wingmen.
    /// </summary>
    public static class PoolDeal
    {
        /// <summary>The pool for a mission: role="generic" speakers, minus any voice the mission casts by designation
        /// (so a stand-in never sounds like a character cast here). Stable order by id.</summary>
        public static List<Speaker> PoolOf(ChatterConfig config)
        {
            var castVoices = new HashSet<string>(
                config.Speakers.Values.Where(sp => !string.IsNullOrEmpty(sp.Designation) && !string.IsNullOrEmpty(sp.Voice)).Select(sp => sp.Voice),
                StringComparer.OrdinalIgnoreCase);
            var pool = config.Speakers.Values.Where(sp => sp.IsGeneric && !(sp.Voice != null && castVoices.Contains(sp.Voice)))
                                             .OrderBy(sp => sp.Id, StringComparer.OrdinalIgnoreCase).ToList();
            if (pool.Count == 0)   // every pool voice is cast here (tiny pool, big cast): better a doubled voice than stock
                pool = config.Speakers.Values.Where(sp => sp.IsGeneric).OrderBy(sp => sp.Id, StringComparer.OrdinalIgnoreCase).ToList();
            return pool;
        }

        /// <summary>Seats no mission-cast speaker is bound to: the pool's roster, in natural callsign order.</summary>
        public static List<string> UncastRoster(List<string> seats, ChatterConfig config)
        {
            var roster = seats.Where(seat => !config.Speakers.Values.Any(sp => !string.IsNullOrEmpty(sp.Designation) && CallsignMap.CallsignsEqual(sp.Designation, seat))).ToList();
            roster.Sort(NaturalCompare);
            return roster;
        }

        /// <summary>
        /// Deal pool voices to the roster. A seat goes to a voice that has self-ID lines recorded for that callsign
        /// (<paramref name="covers"/>), the least used such voice first; a seat no voice covers takes the next voice in
        /// rotation, starting at an offset hashed from the mission name so different missions lead with different
        /// voices. The same seat gets the same voice every time the mission is flown.
        /// </summary>
        /// <param name="pool">The pool voices; must not be empty.</param>
        /// <param name="offset">Where the rotation starts; late arrivals continue it.</param>
        /// <param name="covered">How many seats went to a voice that has their self-ID lines.</param>
        public static Dictionary<string, Speaker> Deal(List<Speaker> pool, List<string> roster, string missionName,
                                                       Func<Speaker, string, bool> covers, out int offset, out int covered)
        {
            offset = (int)(Fnv1a(missionName ?? "") % (uint)pool.Count);
            var voiceBySeat = new Dictionary<string, Speaker>(StringComparer.OrdinalIgnoreCase);
            var timesDealt = new Dictionary<Speaker, int>();
            foreach (var voice in pool) timesDealt[voice] = 0;
            covered = 0;
            for (int i = 0; i < roster.Count; i++)
            {
                var seat = roster[i];
                var covering = pool.Where(voice => covers(voice, seat)).OrderBy(voice => timesDealt[voice]).ThenBy(voice => pool.IndexOf(voice)).ToList();
                var dealt = covering.Count > 0 ? covering[0] : pool[(offset + i) % pool.Count];
                if (covering.Count > 0) covered++;
                voiceBySeat[seat] = dealt;
                timesDealt[dealt]++;
            }
            return voiceBySeat;
        }

        /// <summary>Does this voice have self-ID lines ("This is Blue Leader...") recorded for the callsign?
        /// <paramref name="manifests"/> is keyed by voice folder path; the voice is found by its folder name.</summary>
        public static bool CoversSelfId(IDictionary<string, VoiceManifest> manifests, Speaker speaker, string callsign)
        {
            if (string.IsNullOrEmpty(speaker?.Voice)) return false;
            var voiceFolderName = ChatterConfig.Slug(speaker.Voice);
            foreach (var manifest in manifests)
            {
                var folderName = Path.GetFileName(manifest.Key.TrimEnd(Path.DirectorySeparatorChar, '/'));
                if (!string.Equals(folderName, voiceFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                return manifest.Value.Versions.Any(v => v.Kind == CallsignVersion.SelfId && CallsignMap.CallsignsEqual(v.Callsign, callsign));
            }
            return false;
        }

        /// <summary>"Blue 1" &lt; "Red 2" &lt; "Red 10": alphabetical on the word part, numeric on the trailing number.</summary>
        public static int NaturalCompare(string a, string b)
        {
            SplitCallsign(a, out var wordA, out var numberA);
            SplitCallsign(b, out var wordB, out var numberB);
            var byWord = string.Compare(wordA, wordB, StringComparison.OrdinalIgnoreCase);
            return byWord != 0 ? byWord : numberA.CompareTo(numberB);
        }

        /// <summary>"Red 10" -> "Red", 10. A callsign without a trailing number is all word, number 0.</summary>
        private static void SplitCallsign(string callsign, out string word, out int number)
        {
            var lastSpace = callsign.LastIndexOf(' ');
            if (lastSpace > 0 && int.TryParse(callsign.Substring(lastSpace + 1), out number)) { word = callsign.Substring(0, lastSpace); return; }
            word = callsign; number = 0;
        }

        /// <summary>FNV-1a, 32 bit: a hash that is the same on every machine and run (string.GetHashCode is not).</summary>
        public static uint Fnv1a(string text)
        {
            uint hash = 2166136261;
            foreach (var ch in text) { hash ^= ch; hash *= 16777619; }
            return hash;
        }
    }
}
