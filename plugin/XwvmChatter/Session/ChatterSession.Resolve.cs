using System.Collections.Generic;
using System.Linq;
using Xwvm.MissionClasses.Entities;

namespace XwvmChatter
{
    // Resolution: from a situation (and maybe a sender) to one line and the speaker who says it.
    public sealed partial class ChatterSession
    {
        private readonly LineHistory _history = new LineHistory();

        /// <summary>The outcome of resolution: what will be said, by whom, and why (for the log).</summary>
        public struct Resolution
        {
            public Line Line;
            public Speaker Speaker;   // may be null for a sender the game names who is not in the cast (then Ship carries identity)
            public Ship Ship;         // physical speaker if any (null for commander/astromech)
            public string Reason;
        }

        /// <summary>
        /// Find the line for a situation and who says it.
        ///   sender != null  the game says who is talking (an order reply, a kill report, a help call): one of that
        ///                   speaker's own lines. Fighters of the player's side only; a named friendly ship gets its
        ///                   capital-ship lines. A seat with self-ID lines recorded uses those before the nameless ones.
        ///   sender == null  a warning or remark to the player: any wingman on the radio may call it. Preference: the
        ///                   player's own flight, then other flights, then the officer or astromech; where the line
        ///                   addresses the player, a voice that can say the player's callsign; a mission's own lines
        ///                   before the defaults; then the least recently heard; then at random.
        /// Tags (attacker class, threat, arrival) narrow the lines first; if no tagged line fits, all stay in.
        /// False (with res.Reason) when no line or no live speaker is found - the caller leaves it to the game.
        /// </summary>
        public bool TryResolve(string situationId, Ship sender, List<string> attackerClasses, string threat, string arrival, out Resolution res)
        {
            res = default;
            var candidates = Config.Lines.Where(l => l.IsFor(situationId)).ToList();
            if (candidates.Count == 0) { res.Reason = "no lines for situation"; return false; }
            string tagNote = null;        // what the tags narrowed, for the log
            List<Line> untagged = null;   // the candidates before the tags narrowed them; null when they did not
            if (candidates.Any(l => l.Tagged))
            {
                var fitting = candidates.Where(l => LineTags.Fit(l, attackerClasses, threat, arrival)).ToList();
                if (fitting.Count > 0) { untagged = candidates; candidates = fitting; }   // if nothing fits at all, keep everything rather than silence
                tagNote = $"classes={(attackerClasses != null ? string.Join("/", attackerClasses) : "?")} threat={threat ?? "?"} arrival={arrival ?? "-"} eligible={candidates.Count}";
            }

            if (sender != null) return TryResolveSender(situationId, sender, candidates, out res);

            var options = BuildWarningOptions(candidates);
            if (options.Count == 0 && untagged != null)
            {
                // The tag filter can strand us with no live owner (e.g. every wingman dead and the commander's lines
                // filtered out by attacker/threat tags). NOTHING GOES SILENT: relax the tags before touching stock.
                options = BuildWarningOptions(untagged);
                if (options.Count > 0) tagNote += " (tag-relaxed)";
            }
            if (options.Count == 0) { res.Reason = "no live speaker in any chain"; return false; }

            // Lowest rank wins. Most important first: a live wingman of the PLAYER'S OWN flight group (it is the flight
            // that is actually with you), then any live wingman, then the officer/astromech fallbacks; where the line
            // addresses the player, a speaker whose voice HAS a version for the player's seat (otherwise a voice
            // recorded for another seat can win the roll and say the line nameless); a mission's own lines before the
            // defaults. Among the best, the pick is uniformly random, so warnings rotate across the squadron.
            bool addressesPlayer = Situations.Addressed.Contains(situationId);
            string playerCallsign = BareDesignation(Player);
            int Rank(Resolution option) => (option.Ship != null ? (SameFlightGroupAsPlayer(option.Ship) ? 0 : 1) : 2) * 4
                                           + (addressesPlayer && !VoiceCoversAddress(option.Speaker, playerCallsign) ? 2 : 0)
                                           + (option.Line.FromMissionBlock ? 0 : 1);
            var bestRank = options.Min(Rank);
            var best = options.Where(option => Rank(option) == bestRank).ToList();
            var freshLines = new HashSet<Line>(_history.Fresh(best.Select(option => option.Line).Distinct().ToList(), situationId));
            best = best.Where(option => freshLines.Contains(option.Line)).ToList();
            res = best[UnityEngine.Random.Range(0, best.Count)];
            if (tagNote != null) res.Reason += "; " + tagNote;
            return true;
        }

        /// <summary>The game says who is talking: one of that speaker's own lines, or a line that names no speaker
        /// (it applies to any sender).</summary>
        private bool TryResolveSender(string situationId, Ship sender, List<Line> candidates, out Resolution res)
        {
            res = default;
            // Chatter is WINGMEN ONLY: mirror the stock rule and voice these situations only for craft of the player's
            // own kind (fighters). Transports/freighters/capitals never get wingman lines, whatever the casting binds.
            var playerKind = Player?.Kind;
            if (playerKind.HasValue && sender.Kind != playerKind.Value)
            {
                // A NAMED friendly ship cast with role="ship" speaks its own capital-ship lines - only lines bound to
                // its slot, never a wingman or pool line.
                var shipSpeaker = CastSpeakerFor(sender);
                if (shipSpeaker != null && shipSpeaker.IsShip && Situations.ShipSpoken.Contains(situationId) && (Player == null || sender.Iff == Player.Iff))
                {
                    var shipLines = candidates.Where(l => l.NamesSpeaker(shipSpeaker)
                                                          && (!l.CallsignOnly || HasSelfIdFor(l, sender, shipSpeaker))).ToList();
                    if (shipLines.Count == 0) { res.Reason = $"ship {SafeDesignation(sender)} has no {situationId} lines"; return false; }
                    res = new Resolution { Line = PickPreferringMission(shipLines, situationId), Speaker = shipSpeaker, Ship = sender, Reason = "ship (self)" };
                    return true;
                }
                res.Reason = $"sender {SafeDesignation(sender)} is {sender.Kind}, not a wingman"; return false;
            }
            // ...and on OUR side. The game raises these events for every craft; the stock handler filters by faction
            // AFTER our hook runs, so we must too - otherwise a bound hostile (bad casting) would get a Rebel voice.
            if (Player != null && sender.Iff != Player.Iff) { res.Reason = $"sender {SafeDesignation(sender)} is hostile/neutral (Iff {sender.Iff}); not ours"; return false; }

            var speaker = SpeakerFor(sender);
            if (speaker != null && speaker.IsSilent) { res.Reason = $"sender {SafeDesignation(sender)} is a silent story seat"; return false; }
            // A callsign-only line has no plain clip: it is a candidate only when this speaker's voice holds its
            // version for the sender's seat. Our own content rule; the game has no equivalent.
            var lines = candidates.Where(l => (l.SpeakerIds.Count == 0 || l.NamesSpeaker(speaker))
                                              && (!l.CallsignOnly || HasSelfIdFor(l, sender, speaker))).ToList();
            if (lines.Count == 0) { res.Reason = $"sender {SafeDesignation(sender)} not cast for {situationId}"; return false; }
            // When the voice has a self-ID version of some of these lines for the sender's seat, pick only among
            // those ("This is Blue Leader!" rather than a nameless call); the nameless lines remain the fallback for a
            // seat with no self-ID recordings.
            var selfIdLines = lines.Where(l => HasSelfIdFor(l, sender, speaker)).ToList();
            if (selfIdLines.Count > 0) lines = selfIdLines;
            res = new Resolution
            {
                Line = PickPreferringMission(lines, situationId), Speaker = speaker, Ship = sender,
                Reason = speaker != null && speaker.IsGeneric ? "implicit sender (generic pool)" : "implicit sender",
            };
            return true;
        }

        /// <summary>Every (speaker, line) pair that could make a player warning right now; see TryResolve.</summary>
        private List<Resolution> BuildWarningOptions(List<Line> candidates)
        {
            var options = new List<Resolution>();
            // who is alive does not change within one pick: each generic voice's live ships are found once, not per line
            var liveShipsByGeneric = new Dictionary<Speaker, List<Ship>>();
            List<Ship> LiveShipsFor(Speaker generic)
            {
                if (!liveShipsByGeneric.TryGetValue(generic, out var ships)) liveShipsByGeneric[generic] = ships = ShipsForGeneric(generic).ToList();
                return ships;
            }
            foreach (var line in candidates)
            {
                int optionsBefore = options.Count;
                if (line.SpeakerIds.Count == 0)
                {
                    // the line names nobody: the first cast speaker whose ship is on the radio, else the officer
                    var live = Config.Speakers.Values.Where(sp => !sp.IsSilent).Select(sp => (speaker: sp, ship: ShipFor(sp))).FirstOrDefault(cast => IsOnRadio(cast.ship));
                    var officer = Config.Speakers.Values.FirstOrDefault(sp => sp.IsCommander);
                    if (live.speaker != null) options.Add(new Resolution { Line = line, Speaker = live.speaker, Ship = live.ship, Reason = "any live cast" });
                    else if (officer != null) options.Add(new Resolution { Line = line, Speaker = officer, Reason = "commander fallback" });
                    continue;
                }
                // Warnings: any LIVE wingman the line names may call it (whoever "sees it"), chosen at random -
                // NOT the first one named, or one voice would monopolise warnings until it dies. Commander/astromech
                // are fallbacks used only when no wingman the line names is alive.
                foreach (var id in line.SpeakerIds)
                {
                    if (!Config.Speakers.TryGetValue(id, out var speaker)) continue;
                    if (speaker.IsCommander || speaker.IsAstromech) continue;   // fallbacks, below
                    if (speaker.IsGeneric)
                    {
                        // a generic voice is "alive" through every live uncast wingman it currently voices; each such ship is
                        // a candidate caller so the warning is attributed to a real callsign on the radio
                        foreach (var ship in LiveShipsFor(speaker))
                            options.Add(new Resolution { Line = line, Speaker = speaker, Ship = ship, Reason = "live generic wingman" });
                        continue;
                    }
                    var castShip = ShipFor(speaker);
                    if (IsOnRadio(castShip)) options.Add(new Resolution { Line = line, Speaker = speaker, Ship = castShip, Reason = "live wingman" });
                }
                if (options.Count == optionsBefore)   // no live caller for this line
                {
                    foreach (var id in line.SpeakerIds)
                    {
                        if (!Config.Speakers.TryGetValue(id, out var speaker)) continue;
                        if (speaker.IsCommander) { options.Add(new Resolution { Line = line, Speaker = speaker, Reason = "commander fallback" }); break; }
                        if (speaker.IsAstromech && PlayerHasAstromech) { options.Add(new Resolution { Line = line, Speaker = speaker, Reason = "astromech fallback" }); break; }
                    }
                }
            }
            // A callsign-only line stays only where this speaker has it recorded - addressing the player's seat, or
            // naming the speaker's own seat.
            return options.Where(option => !option.Line.CallsignOnly || HasCallsignVersion(option)).ToList();
        }

        /// <summary>The tour officer's line of <paramref name="situationId"/>, addressing the player. Callsign-only
        /// lines are offered only when the officer's voice has the version for the player's seat (it is swapped in by
        /// TrySwapInCallsignVersion).</summary>
        public bool TryResolveOfficerAddress(string situationId, out Resolution res)
        {
            res = default;
            var officer = Config.Speakers.Values.FirstOrDefault(sp => sp.IsCommander);
            if (officer == null) { res.Reason = "no officer cast"; return false; }
            string playerCallsign = BareDesignation(Player);
            var lines = Config.Lines.Where(l => l.IsFor(situationId) && l.NamesSpeaker(officer)
                                                && (!l.CallsignOnly || VoiceCoversAddress(officer, playerCallsign))).ToList();
            if (lines.Count == 0) { res.Reason = $"officer {officer.Voice} has no {situationId} line for {playerCallsign}"; return false; }
            res = new Resolution { Line = PickPreferringMission(lines, situationId), Speaker = officer, Reason = "officer" };
            return true;
        }

        /// <summary>Resolve a line of <paramref name="situationId"/> said by one of the player's flightmates:
        ///   lament  (exclude = the dead wingman, above = null): any LIVE wingman of the player's flight, not the dead one
        ///   reply   (exclude = null, above = the lamenter): a LIVE wingman of the player's flight with a LOWER seat number
        ///           than the lamenter (the game's shipNumberInWave). Wingmen only - never the officer;
        ///           when nobody above the lamenter is left alive, the lament stands alone.
        /// The player never speaks. Lines come from the speaker's own bank (its slot id on the line).</summary>
        /// <param name="context">The register, chosen by the caller from dialogue pacing: "quiet" when no wingman has
        /// spoken for a few seconds, "combat" otherwise. Lines tagged for the other register are skipped (unless no
        /// line is left); untagged lines fit both. Null: no register.</param>
        public bool TryResolveFlightmate(string situationId, Ship exclude, Ship above, out Resolution res, string context = null)
        {
            res = default;
            var candidates = Config.Lines.Where(l => l.IsFor(situationId) && !l.CallsignOnly).ToList();
            if (candidates.Count == 0) { res.Reason = "no lines for situation"; return false; }
            if (context != null)
            {
                var fitting = candidates.Where(l => l.Context.Count == 0 || l.Context.Contains(context)).ToList();
                if (fitting.Count > 0) candidates = fitting;
            }
            var options = new List<Resolution>();
            foreach (var wingman in Mission.GetAllShipsOtherThanPlayer())
            {
                if (!IsWingman(wingman) || !IsOnRadio(wingman) || !SameFlightGroupAsPlayer(wingman)) continue;
                if (exclude != null && ReferenceEquals(wingman, exclude)) continue;
                if (above != null && (ReferenceEquals(wingman, above) || wingman.shipNumberInWave >= above.shipNumberInWave)) continue;
                var speaker = SpeakerFor(wingman);
                if (speaker == null) continue;
                foreach (var line in candidates)
                    if (line.NamesSpeaker(speaker))
                        options.Add(new Resolution { Line = line, Speaker = speaker, Ship = wingman, Reason = above != null ? $"seat {wingman.shipNumberInWave} above {above.shipNumberInWave}" : "surviving flightmate" });
            }
            if (options.Count == 0) { res.Reason = above != null ? "nobody above the lamenter is alive and voiced (wingmen only)" : "no surviving voiced flightmate"; return false; }
            var freshLines = new HashSet<Line>(_history.Fresh(options.Select(option => option.Line).Distinct().ToList(), situationId));
            var fresh = options.Where(option => freshLines.Contains(option.Line)).ToList();
            if (fresh.Count == 0) fresh = options;
            res = fresh[UnityEngine.Random.Range(0, fresh.Count)];
            if (context != null) res.Reason += "; " + context;
            return true;
        }

        /// <summary>One line at random from the least recently heard of <paramref name="lines"/> - of the mission's
        /// own lines when it has any, otherwise of them all.</summary>
        private Line PickPreferringMission(List<Line> lines, string situationId)
        {
            var missionLines = lines.Where(l => l.FromMissionBlock).ToList();
            var fresh = _history.Fresh(missionLines.Count > 0 ? missionLines : lines, situationId);
            return fresh[UnityEngine.Random.Range(0, fresh.Count)];
        }
    }
}
