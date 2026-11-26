using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XwvmChatter
{
    /// <summary>A voice slot that lines refer to: bound to a ship by designation, or to a role.</summary>
    public sealed class Speaker
    {
        public string Id;            // referenced by <line speaker="...">
        public string Designation;   // "Red 2", "Red Leader", "Gold 3" - matched against Ship.Designation
        public string Role;          // "commander" | "astromech" | "generic" | "silent" | "ship" | null
        public string Voice;         // voice profile name; a mission-cast voice is excluded from the generic pool in that mission
        public string DisplayName;   // subtitle prefix override; defaults to the ship's designation, "Commander" or "Astromech"
        public string Callsign;      // ship speakers: the name in their self-ID lines ("Qiluiy" for QILUIY 1-3)
        public bool FromMissionBlock;   // cast by the mission's own block (false: by a chatter-defaults block)

        /// <summary>Bank casting: which of the voice's bank lines this speaker takes - "capital" = only the
        /// capital-ship lines (bank role="capital"); anything else = every line that is NOT a capital-ship line.</summary>
        public string BankLines;
        /// <summary>Bank casting: when set, only these situations of the bank (a commander's calls; a transport's
        /// hyperspace and docking lines).</summary>
        public HashSet<string> BankSituations;
        /// <summary>Bank casting: ship speakers with the same group (the ships of one flight group) share one set
        /// of lines - any of them may speak a line, and it counts as the same line whoever does.</summary>
        public string BankGroup;

        public bool IsCommander => HasRole("commander");
        public bool IsAstromech => HasRole("astromech");
        /// <summary>Generic wingman pool voice: stands in for any friendly fighter that has no designation-bound speaker.</summary>
        public bool IsGeneric => HasRole("generic");
        /// <summary>A story pilot's seat whose story voice has no chatter bank yet. The seat is held (no generic pool
        /// voice is dealt to it) and its ship says nothing in chatter, so the wrong voice never speaks for a named
        /// pilot; its story lines still play.</summary>
        public bool IsSilent => HasRole("silent");
        /// <summary>A named friendly ship (freighter, cruiser, frigate, corvette; transports and shuttles for
        /// hyperspace/docking) bound by designation. It speaks only its own capital-ship lines - never a wingman
        /// line - and self-IDs with <see cref="Callsign"/> (its flight group's name, shared by every ship of the group).</summary>
        public bool IsShip => HasRole("ship");

        private bool HasRole(string role) => string.Equals(Role, role, StringComparison.OrdinalIgnoreCase);

        public override string ToString() => $"{Id}({Designation ?? Role}:{Voice})";
    }

    /// <summary>One playable line: a situation, the speakers who may say it, an audio file and subtitle text.</summary>
    public sealed class Line
    {
        public string Situation;             // e.g. "UnderAttack" (see Situations.cs)
        public List<string> SpeakerIds = new List<string>();  // speakers who may say it; empty = whoever the game names as sender
        public string AudioFile;             // full path to the clip (resolved)
        public string Text;                  // subtitle / log text; may contain {playername}
        public bool FromMissionBlock;        // true if from <mission><chatter>, false if from <chatter-defaults>
        public Line BaseLine;                // a callsign version swapped in: the script line it stands for (the key for not repeating lines)
        public bool CallsignOnly;            // the line exists only as callsign versions (no plain clip; AudioFile names the
                                             // line, not a file): offered only when the speaker's voice has the version for the seat
        public List<string> Attacker = new List<string>();   // eligible attacker classes (tie/fighter/interceptor/bomber/advanced/gunboat/capital/...); empty = any
        public List<string> Threat = new List<string>();     // eligible threat states (new/persistent/multiple); empty = any
        public List<string> Arrival = new List<string>();    // eligible arrival modes (hyper/launched); empty = any
        public List<string> Context = new List<string>();    // combat | quiet: the flight is / is not in a fight right now; empty = any

        /// <summary>Carries an attacker, threat or arrival tag (the tags <see cref="LineTags.Fit"/> tests; Context is
        /// applied separately, where a flightmate line is picked).</summary>
        public bool Tagged => Attacker.Count > 0 || Threat.Count > 0 || Arrival.Count > 0;

        public bool IsFor(string situationId) => string.Equals(Situation, situationId, StringComparison.OrdinalIgnoreCase);

        /// <summary>Does the line name this speaker? (False for a null speaker and for a line that names nobody.)</summary>
        public bool NamesSpeaker(Speaker speaker) =>
            speaker != null && SpeakerIds.Any(id => string.Equals(id, speaker.Id, StringComparison.OrdinalIgnoreCase));

        public override string ToString() => $"{Situation}[{string.Join(",", SpeakerIds)}] {Path.GetFileName(AudioFile)}";
    }

    /// <summary>Merged chatter configuration for a single mission.</summary>
    public sealed class ChatterConfig
    {
        /// <summary>Folder (under the sidecar dir) that holds the chatter sidecars.</summary>
        public const string ChatterFolder = "_chatter";
        /// <summary>File (next to a voice's clips) that lists the voice's lines: situation, clip, words, tags.</summary>
        public const string BankFile = "bank.xml";

        private static readonly char[] ListSeparators = { ',', ' ' };

        public string MissionName;
        public readonly Dictionary<string, Speaker> Speakers = new Dictionary<string, Speaker>(StringComparer.OrdinalIgnoreCase);
        public readonly List<Line> Lines = new List<Line>();
        public readonly List<string> SourceFiles = new List<string>();
        /// <summary>Situations this mission's hand-recorded narrative VO already voices (sidecar &lt;chatter silence="..."&gt;,
        /// written by write_casting.py from the narrative sidecar's triggers): the plugin says nothing for them and
        /// suppresses the stock line, so the scripted moment plays alone.</summary>
        public readonly HashSet<string> Silenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, XElement> _bankCache = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);   // bank.xml path -> its root, null when unreadable

        public bool IsEmpty => Lines.Count == 0;

        /// <summary>
        /// Loads all &lt;chatter&gt; blocks relevant to a mission from a sidecar directory.
        /// Sources, in priority order (later overrides earlier for speakers with the same id):
        ///   1. &lt;sidecar&gt;&lt;chatter-defaults target="pattern"&gt; (root child, invisible to vanilla)
        ///   2. &lt;sidecar&gt;&lt;mission target="pattern"&gt;&lt;chatter&gt; (unknown child, ignored by vanilla)
        /// A block's lines come from its own &lt;line&gt; elements (the older format: every take of every cast voice
        /// written out per mission) and from the voice banks: a block with banks="VOICE/_chatter" only CASTS -
        /// each &lt;speaker voice="..."&gt; takes its lines from &lt;banks&gt;/&lt;slug of the voice&gt;/bank.xml, the list
        /// that sits next to the voice's clips (see <see cref="AbsorbBanks"/>). Both may appear. The game has no
        /// equivalent of either (its sidecars carry scripted messages only).
        /// </summary>
        public static ChatterConfig Load(string sidecarDir, string missionName, Action<string> log)
        {
            var config = new ChatterConfig { MissionName = missionName };
            if (!Directory.Exists(sidecarDir))
            {
                log?.Invoke($"[chatter] sidecar dir not found: {sidecarDir}");
                return config;
            }

            var defaultBlocks = new List<(XElement block, string file)>();
            var missionBlocks = new List<(XElement block, string file)>();
            foreach (var file in ChatterFiles(sidecarDir))
            {
                XElement root;
                try { root = XElement.Load(file); }
                catch (Exception e) { log?.Invoke($"[chatter] skip {Path.GetFileName(file)}: {e.Message}"); continue; }

                foreach (var defaults in root.Elements("chatter-defaults"))
                {
                    if (MatchesMissionName(defaults.Attribute("target")?.Value ?? "*", missionName))
                        defaultBlocks.Add((defaults, file));
                }
                foreach (var mission in root.Elements("mission"))
                {
                    if (!MatchesMissionName(mission.Attribute("target")?.Value, missionName)) continue;
                    foreach (var chatter in mission.Elements("chatter"))
                        missionBlocks.Add((chatter, file));
                }
            }

            foreach (var (block, file) in defaultBlocks) config.Absorb(block, file, sidecarDir, fromMission: false, log);
            foreach (var (block, file) in missionBlocks) config.Absorb(block, file, sidecarDir, fromMission: true, log);
            config.KeepOnlyTheMissionsOwnOfficer(log);

            log?.Invoke($"[chatter] {missionName}: {config.Speakers.Count} speakers, {config.Lines.Count} lines from {config.SourceFiles.Count} file(s)");
            return config;
        }

        /// <summary>The files that may hold chatter blocks, in the order they are read (by path).
        /// Chatter sidecars live in &lt;sidecarDir&gt;\_chatter\, out of the game's way: its SidecarLoader reads only the
        /// top level (XwvmPath.GetFiles, TopDirectoryOnly), so it never parses them. They are plain .xml (.xwvm is the
        /// game's sidecar extension); .xwvm there is still read, and so is the top level, so a chatter block in an
        /// ordinary sidecar keeps working.</summary>
        private static IEnumerable<string> ChatterFiles(string sidecarDir)
        {
            var chatterDir = Path.Combine(sidecarDir, ChatterFolder);
            var hasChatterDir = Directory.Exists(chatterDir);
            return Directory.GetFiles(sidecarDir, "*.xwvm", SearchOption.TopDirectoryOnly)
                .Concat(hasChatterDir ? Directory.GetFiles(chatterDir, "*.xwvm", SearchOption.TopDirectoryOnly) : Array.Empty<string>())
                .Concat(hasChatterDir ? Directory.GetFiles(chatterDir, "*.xml", SearchOption.TopDirectoryOnly) : Array.Empty<string>())
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The chatter-defaults block may cast a commander for every mission. A mission that casts its own
        /// (its tour's officer) keeps only that one - the default and its lines go. No game equivalent: the game has
        /// no officer.</summary>
        private void KeepOnlyTheMissionsOwnOfficer(Action<string> log)
        {
            if (Speakers.Values.Any(sp => sp.IsCommander && sp.FromMissionBlock))
            {
                var defaultOfficerIds = new HashSet<string>(Speakers.Values.Where(sp => sp.IsCommander && !sp.FromMissionBlock).Select(sp => sp.Id), StringComparer.OrdinalIgnoreCase);
                if (defaultOfficerIds.Count > 0)
                {
                    foreach (var id in defaultOfficerIds) Speakers.Remove(id);
                    Lines.RemoveAll(l => !l.FromMissionBlock && l.SpeakerIds.Count > 0 && l.SpeakerIds.All(defaultOfficerIds.Contains));
                }
            }
            else if (Speakers.Values.FirstOrDefault(sp => sp.IsCommander) is Speaker defaultOfficer)
                log?.Invoke($"[chatter] {MissionName}: no officer of its own - default officer {defaultOfficer.Voice}");
        }

        /// <summary>Take one block's silenced situations, speakers and lines into this configuration.</summary>
        /// <param name="sidecarDir">audioPath is relative to the sidecar ROOT (GlobalSidecars\xwing), wherever the file sits,
        /// as the game resolves a narrative sidecar's audioPath against the folder it loads sidecars from.</param>
        private void Absorb(XElement block, string file, string sidecarDir, bool fromMission, Action<string> log)
        {
            if (!SourceFiles.Contains(file)) SourceFiles.Add(file);
            var audioPath = block.Attribute("audioPath")?.Value;
            var audioFormat = (block.Attribute("audioFormat")?.Value ?? "ogg").Trim().TrimStart('.').ToLowerInvariant();
            foreach (var situation in SplitList(block.Attribute("silence")?.Value))
                Silenced.Add(situation.Trim());

            var blockSpeakers = new List<Speaker>();
            foreach (var speakerElement in block.Elements("speaker"))
            {
                var speaker = ParseSpeaker(speakerElement, fromMission);
                if (string.IsNullOrEmpty(speaker.Id)) { log?.Invoke($"[chatter] speaker without id/designation/role in {Path.GetFileName(file)}"); continue; }
                Speakers[speaker.Id] = speaker;   // a later block overrides an earlier one: the mission's own over the defaults
                blockSpeakers.Add(speaker);
            }

            foreach (var lineElement in block.Elements("line"))
            {
                var line = ParseLine(lineElement, sidecarDir, lineElement.Attribute("audioPath")?.Value ?? audioPath, audioFormat, file, fromMission, log);
                if (line == null) continue;
                var speakerIds = lineElement.Attribute("speaker")?.Value;
                if (!string.IsNullOrWhiteSpace(speakerIds)) line.SpeakerIds.AddRange(SplitList(speakerIds));
                Lines.Add(line);
            }

            var banks = block.Attribute("banks")?.Value?.Trim();
            if (!string.IsNullOrEmpty(banks)) AbsorbBanks(blockSpeakers, banks, audioFormat, sidecarDir, fromMission, log);
        }

        /// <summary>One &lt;speaker&gt; element. Its id falls back to its designation, then its role; the caller
        /// rejects a speaker that has none of the three.</summary>
        private static Speaker ParseSpeaker(XElement element, bool fromMission)
        {
            var speaker = new Speaker
            {
                Id = element.Attribute("id")?.Value?.Trim(),
                Designation = element.Attribute("designation")?.Value?.Trim(),
                Role = element.Attribute("role")?.Value?.Trim(),
                Voice = element.Attribute("voice")?.Value?.Trim(),
                DisplayName = element.Attribute("name")?.Value?.Trim(),
                Callsign = element.Attribute("callsign")?.Value?.Trim(),
                BankLines = element.Attribute("lines")?.Value?.Trim(),
                BankGroup = element.Attribute("group")?.Value?.Trim(),
                FromMissionBlock = fromMission,
            };
            var situations = element.Attribute("situations")?.Value;
            if (!string.IsNullOrWhiteSpace(situations))
                speaker.BankSituations = new HashSet<string>(SplitList(situations).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(speaker.Id))
                speaker.Id = speaker.Designation ?? speaker.Role;
            return speaker;
        }

        /// <summary>Bank casting: the block names who sits where, and each speaker's lines are read from the voice's
        /// own bank.xml. A line belongs to one voice, so it is offered to that voice's speaker only. Order: every
        /// non-ship speaker in document order, then the named ships, where the ships of one flight group (speakers
        /// with the same group="...") share ONE set of lines.
        /// No game equivalent: the game's sidecars carry scripted messages only (SidecarLoader), there is no voice bank.</summary>
        private void AbsorbBanks(List<Speaker> speakers, string banks, string audioFormat, string sidecarDir, bool fromMission, Action<string> log)
        {
            foreach (var sharers in BankSharers(speakers))
            {
                var first = sharers[0];
                if (first.IsSilent || string.IsNullOrEmpty(first.Voice)) continue;
                // Joined with '/' (not Path.Combine), so the clip's path STRING has one fixed form for everything
                // downstream that keys on it (clip cache, a line's voice folder).
                var voiceFolder = banks.TrimEnd('/', '\\') + "/" + Slug(first.Voice);
                var bankPath = Path.Combine(sidecarDir, voiceFolder, BankFile);
                var bank = LoadBank(bankPath, first.Voice, log);
                if (bank == null) continue;
                bool wantsCapitalLines = string.Equals(first.BankLines, "capital", StringComparison.OrdinalIgnoreCase);
                var speakerIds = sharers.Select(sp => sp.Id).ToList();
                foreach (var lineElement in bank.Elements("line"))
                {
                    bool isCapitalLine = string.Equals(lineElement.Attribute("role")?.Value?.Trim(), "capital", StringComparison.OrdinalIgnoreCase);
                    if (isCapitalLine != wantsCapitalLines) continue;
                    if (first.BankSituations != null && !first.BankSituations.Contains(lineElement.Attribute("situation")?.Value?.Trim() ?? "")) continue;
                    var line = ParseLine(lineElement, sidecarDir, voiceFolder, audioFormat, bankPath, fromMission, log);
                    if (line == null) continue;
                    line.SpeakerIds.AddRange(speakerIds);
                    Lines.Add(line);
                }
            }
        }

        /// <summary>The speakers of a block, grouped by who shares a set of bank lines: each non-ship speaker alone,
        /// in document order, then the ships - those with the same group together, a ship without a group alone.</summary>
        private static List<List<Speaker>> BankSharers(List<Speaker> speakers)
        {
            var groups = new List<List<Speaker>>();
            foreach (var speaker in speakers.Where(sp => !sp.IsShip)) groups.Add(new List<Speaker> { speaker });
            var shipGroups = new Dictionary<string, List<Speaker>>(StringComparer.OrdinalIgnoreCase);
            foreach (var ship in speakers.Where(sp => sp.IsShip))
            {
                var key = string.IsNullOrEmpty(ship.BankGroup) ? "id:" + ship.Id : "group:" + ship.BankGroup;
                if (!shipGroups.TryGetValue(key, out var group)) { shipGroups[key] = group = new List<Speaker>(); groups.Add(group); }
                group.Add(ship);
            }
            return groups;
        }

        /// <summary>A voice's bank.xml, read once per configuration; null (logged the first time) when it is missing
        /// or unreadable.</summary>
        private XElement LoadBank(string bankPath, string voice, Action<string> log)
        {
            if (_bankCache.TryGetValue(bankPath, out var bank)) return bank;
            bank = null;
            if (!File.Exists(bankPath)) log?.Invoke($"[chatter] no {BankFile} for voice {voice}: {bankPath}");
            else
            {
                try { bank = XElement.Load(bankPath); }
                catch (Exception e) { log?.Invoke($"[chatter] skip {bankPath}: {e.Message}"); }
            }
            _bankCache[bankPath] = bank;
            return bank;
        }

        /// <summary>A voice's folder name: lower case, every run of other characters one dash (the pipeline's
        /// common.slug).</summary>
        public static string Slug(string name)
        {
            var slug = new StringBuilder();
            bool lastWasDash = false;
            foreach (var ch in (name ?? "").ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) { slug.Append(ch); lastWasDash = false; }
                else if (!lastWasDash && slug.Length > 0) { slug.Append('-'); lastWasDash = true; }
            }
            return slug.ToString().Trim('-');
        }

        /// <summary>One &lt;line&gt; element (of a sidecar block or of a voice's bank.xml) -> a Line without speakers; null when
        /// it has no situation or its clip is missing.</summary>
        /// <param name="audioPath">The clip's folder, relative to <paramref name="sidecarDir"/>; may be empty.</param>
        /// <param name="file">The file the element is in, for the log.</param>
        private static Line ParseLine(XElement element, string sidecarDir, string audioPath, string audioFormat, string file, bool fromMission, Action<string> log)
        {
            var situation = element.Attribute("situation")?.Value?.Trim();
            if (string.IsNullOrEmpty(situation)) { log?.Invoke($"[chatter] line without situation in {Path.GetFileName(file)}"); return null; }
            var audio = element.Attribute("audio")?.Value?.Trim();
            string audioFile = null;
            // a callsign-only line has no plain clip; its audio name only locates its callsign versions
            bool callsignOnly = string.Equals(element.Attribute("callsignOnly")?.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(audio))
            {
                var audioDir = string.IsNullOrEmpty(audioPath) ? sidecarDir : Path.Combine(sidecarDir, audioPath);
                audioFile = Path.Combine(audioDir, audio.Contains(".") ? audio : audio + "." + audioFormat);
                if (!callsignOnly && !File.Exists(audioFile))
                {
                    log?.Invoke($"[chatter] audio missing: {audioFile}");
                    return null;
                }
            }
            var line = new Line
            {
                Situation = situation,
                AudioFile = audioFile,
                Text = CollapseWhitespace(element.Value),
                FromMissionBlock = fromMission,
                CallsignOnly = callsignOnly && audioFile != null,
            };
            foreach (var (attribute, tags) in new[] { ("attacker", line.Attacker), ("threat", line.Threat), ("arrival", line.Arrival), ("context", line.Context) })
            {
                var value = element.Attribute(attribute)?.Value;
                if (!string.IsNullOrWhiteSpace(value))
                    tags.AddRange(SplitList(value).Select(tag => tag.Trim().ToLowerInvariant()));
            }
            return line;
        }

        /// <summary>The items of a comma- or space-separated attribute value; none for a missing one.</summary>
        private static string[] SplitList(string value) =>
            (value ?? "").Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>A line's words on one row: each row of the element's text trimmed, empty rows dropped, joined by spaces.</summary>
        private static string CollapseWhitespace(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var rows = text.Split('\n').Select(row => row.Trim()).Where(row => row.Length > 0);
            return string.Join(" ", rows);
        }

        /// <summary>Same semantics as the game's SidecarLoader.MatchesMissionName: '*' '?' wildcards, '|' alternatives.</summary>
        public static bool MatchesMissionName(string targetPattern, string missionName)
        {
            if (string.IsNullOrEmpty(targetPattern) || missionName == null) return false;
            var alternatives = targetPattern.Split('|')
                .Select(p => "(" + Regex.Escape(p.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + ")");
            var pattern = new Regex("^" + string.Join("|", alternatives) + "$", RegexOptions.IgnoreCase);
            return pattern.IsMatch(missionName);
        }
    }
}
