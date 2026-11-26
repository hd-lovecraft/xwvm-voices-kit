using System;
using System.Collections.Generic;
using System.Linq;
using XMission = Xwvm.MissionClasses.Mission;
using Xwvm.MissionClasses.Entities;

namespace XwvmChatter
{
    // Casting at run time: which speaker a ship is, and the generic pool that voices wingmen nobody cast.
    public sealed partial class ChatterSession
    {
        // The generic pool's deal (PoolDeal), made once per session.
        private Dictionary<string, Speaker> _genericBySeat;   // bare callsign -> pool speaker; null until dealt
        private List<Speaker> _genericPool;
        private int _genericRotationStart;                    // where the rotation began; late arrivals continue it

        /// <summary>Find the live ship a speaker slot is bound to (null for roles / absent ships).</summary>
        public Ship ShipFor(Speaker speaker)
        {
            if (speaker == null || string.IsNullOrEmpty(speaker.Designation)) return null;
            return FindShipByDesignation(speaker.Designation);
        }

        public Ship FindShipByDesignation(string designation)
        {
            if (string.IsNullOrWhiteSpace(designation)) return null;
            var wanted = designation.Trim();
            foreach (var ship in FriendlyShips())
            {
                if (DesignationMatches(ship, wanted)) return ship;
            }
            return null;
        }

        /// <summary>Accepts "Red 2", "Red 2 (Name)", "Red Leader" (FG leader of "Red"), "Red" (leader), "Red 1".</summary>
        public static bool DesignationMatches(Ship ship, string wanted)
        {
            string designation;
            try { designation = ship.Designation ?? ""; } catch { return false; }
            var flightGroupName = ship.FlightGroup?.Designation ?? "";
            if (string.Equals(designation, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            // without the "(pilot name)" suffix
            var pilotName = designation.IndexOf(" (", StringComparison.Ordinal);
            var bare = pilotName > 0 ? designation.Substring(0, pilotName) : designation;
            if (pilotName > 0 && string.Equals(bare, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            // A single-ship flight group NAMED "Gold 1" is Gold Leader too - the same equivalence the casting and the
            // loading screen use (CallsignMap.CallsignsEqual: "X Leader" = "X 1" = "X"); without it the seat falls to
            // the pool and the cast Gold Leader never speaks in flight.
            if (CallsignMap.CallsignsEqual(bare, wanted)) return true;
            // "Red Leader" / "Red" / "Red 1" -> leader of FG "Red"
            if (wanted.EndsWith(LeaderSuffix, StringComparison.OrdinalIgnoreCase))
                wanted = wanted.Substring(0, wanted.Length - LeaderSuffix.Length);
            if (wanted.EndsWith(" 1", StringComparison.OrdinalIgnoreCase))
                wanted = wanted.Substring(0, wanted.Length - 2);
            if (string.Equals(flightGroupName, wanted, StringComparison.OrdinalIgnoreCase) && ship.IsFgLeader) return true;
            return false;
        }

        /// <summary>Which speaker slot (if any) does a given ship correspond to? Designation-bound (cast) first;
        /// otherwise, for a wingman (own kind, own side), its deterministic generic-pool voice.</summary>
        public Speaker SpeakerFor(Ship ship)
        {
            var cast = CastSpeakerFor(ship);
            if (cast != null) return cast;
            return GenericSpeakerFor(ship);
        }

        /// <summary>Is the ship a story pilot's seat that is held silent (<see cref="Speaker.IsSilent"/>)?</summary>
        public bool IsSilentSeat(Ship ship) => CastSpeakerFor(ship)?.IsSilent ?? false;

        /// <summary>Designation-bound speaker only (mission casting / stand-ins); null if the ship is uncast.</summary>
        public Speaker CastSpeakerFor(Ship ship)
        {
            if (ship == null) return null;
            foreach (var speaker in Config.Speakers.Values)
                if (!string.IsNullOrEmpty(speaker.Designation) && DesignationMatches(ship, speaker.Designation)) return speaker;
            return null;
        }

        /// <summary>The friendly wingman seats a mission will field, from its flight groups - the same rule as
        /// IsWingman (same craft kind and IFF as the player), seat names as BareDesignation gives them ("RED 2", or the
        /// bare name for a single-ship group). Known before any ship exists, so the loading screen preloads exactly the
        /// voices the deal will give, and the session deals from the same list. The player's own seat is left out.</summary>
        internal static List<string> FriendlySeats(XMission mission, out string playerSeat)
        {
            playerSeat = null;
            var seats = new List<string>();
            var playerGroup = mission?.PlayerFlightGroup;
            if (playerGroup == null || mission.flightGroups == null) return seats;
            string SeatName(FlightGroup group, int number) => group.waveSize > 1 ? $"{(group.Designation ?? "").Trim()} {number}" : (group.Designation ?? "").Trim();
            playerSeat = SeatName(playerGroup, playerGroup.playerPositionInFg);
            var playerKind = playerGroup.Stats.Kind;
            var playerIff = playerGroup.Iff;
            foreach (var group in mission.flightGroups)
            {
                if (group == null || group.Iff != playerIff) continue;
                try { if (group.Stats == null || group.Stats.Kind != playerKind) continue; } catch { continue; }
                for (int number = 1; number <= Math.Max(1, group.waveSize); number++)
                {
                    var seat = SeatName(group, number);
                    if (seat == "" || CallsignMap.CallsignsEqual(seat, playerSeat)) continue;
                    if (!seats.Any(known => CallsignMap.CallsignsEqual(known, seat))) seats.Add(seat);
                }
            }
            return seats;
        }

        /// <summary>
        /// The generic-pool speaker for an uncast wingman, as <see cref="PoolDeal.Deal"/> dealt it over the mission's
        /// whole uncast roster (present or not yet arrived): the same callsign keeps its voice all mission and across
        /// replays. A callsign that was not on the roster takes the next voice in the rotation.
        /// Null for non-wingmen or when no pool is shipped.
        /// </summary>
        public Speaker GenericSpeakerFor(Ship ship)
        {
            if (!IsWingman(ship)) return null;
            var callsign = BareDesignation(ship);
            if (string.IsNullOrEmpty(callsign)) return null;
            EnsureGenericPoolDealt();
            if (_genericBySeat == null) return null;
            if (_genericBySeat.TryGetValue(callsign, out var speaker)) return speaker;
            // late arrival not on the initial roster: next slot in the rotation
            speaker = _genericPool[(_genericRotationStart + _genericBySeat.Count) % _genericPool.Count];
            _genericBySeat[callsign] = speaker;
            _log($"[chatter] generic pool: late callsign {callsign} -> {speaker.Voice}");
            return speaker;
        }

        /// <summary>Does this pool voice have self-ID lines recorded for the callsign, so the pool deal can prefer it
        /// for the seat?</summary>
        private bool VoiceCoversSelfId(Speaker speaker, string callsign) => PoolDeal.CoversSelfId(_manifests, speaker, callsign);

        /// <summary>Deal the generic pool to the uncast seats, the first time it is asked for. Stays undealt (and is
        /// tried again next time) while the mission has no pool.</summary>
        private void EnsureGenericPoolDealt()
        {
            if (_genericBySeat != null) return;
            var pool = PoolDeal.PoolOf(Config);
            if (pool.Count == 0) return;
            var roster = UncastRoster();
            _genericPool = pool;
            _genericBySeat = PoolDeal.Deal(pool, roster, Config.MissionName, VoiceCoversSelfId, out _genericRotationStart, out var covered);
            _log($"[chatter] generic pool ({string.Join(", ", pool.Select(p => p.Voice))}), roster {roster.Count} " +
                 $"({covered} callsign(s) with a self-ID stem): " +
                 string.Join(", ", roster.Select(seat => seat + "=" + _genericBySeat[seat].Voice + (VoiceCoversSelfId(_genericBySeat[seat], seat) ? "" : "*plain"))));
        }

        /// <summary>The seats the pool is dealt to. From the mission's flight groups (FriendlySeats), the list the
        /// loading screen preloaded for, so the deal is the one it predicted; from the ships present only if that
        /// gives nothing.</summary>
        private List<string> UncastRoster()
        {
            List<string> roster = null;
            try { roster = PoolDeal.UncastRoster(FriendlySeats(Mission, out _), Config); }
            catch (Exception e) { _log($"[chatter] generic roster from flight groups failed ({e.Message}); from ships"); }
            if (roster != null && roster.Count > 0) return roster;

            roster = new List<string>();
            try
            {
                foreach (var ship in Mission.GetAllShipsOtherThanPlayer())
                {
                    if (!IsWingman(ship) || CastSpeakerFor(ship) != null) continue;
                    var callsign = BareDesignation(ship);
                    if (!string.IsNullOrEmpty(callsign) && !roster.Contains(callsign)) roster.Add(callsign);
                }
            }
            catch (Exception e) { _log($"[chatter] generic roster error: {e.Message}"); }
            roster.Sort(PoolDeal.NaturalCompare);
            return roster;
        }

        /// <summary>Live uncast wingmen currently voiced by a given generic speaker (for warnings: "whoever saw it").</summary>
        public IEnumerable<Ship> ShipsForGeneric(Speaker speaker)
        {
            if (speaker == null || !speaker.IsGeneric) yield break;
            foreach (var ship in FriendlyShips())
            {
                if (!IsOnRadio(ship) || !IsWingman(ship) || CastSpeakerFor(ship) != null) continue;
                if (ReferenceEquals(GenericSpeakerFor(ship), speaker)) yield return ship;
            }
        }
    }
}
