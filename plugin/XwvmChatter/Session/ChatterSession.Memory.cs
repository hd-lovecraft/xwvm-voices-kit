using System;
using System.Collections.Generic;
using System.Linq;
using Common;
using UnityEngine;
using Xwvm.MissionClasses.Entities;

namespace XwvmChatter
{
    // What the session remembers between events: kills, who fired at whom, threats already warned about,
    // arrivals already announced, saves.
    public sealed partial class ChatterSession
    {
        private const float LongAgo = -9999f;   // "never", for a time that later times are compared against

        // ------------------------------------------------------------------ kills and boasts

        private readonly Dictionary<Ship, int> _kills = new Dictionary<Ship, int>();   // wingman -> kills this mission

        public void NoteWingmanKill(Ship killer) { _kills.TryGetValue(killer, out var kills); _kills[killer] = kills + 1; }

        /// <summary>A wingman's kill report becomes a count boast ("That's three for me.") on its 2nd..5th hostile fighter
        /// kill, by chance, when the script has lines for that count; otherwise the plain report.</summary>
        public string BoastOrReport(Ship shooter, float chance)
        {
            if (shooter == null || !_kills.TryGetValue(shooter, out var kills)) return Situations.KillReport;
            var boastId = Situations.KillStreak(kills);
            if (boastId == null || chance <= 0f || UnityEngine.Random.value >= chance) return Situations.KillReport;
            // Only if THIS shooter's voice has the boast (a line that names no speaker, or one naming its slot):
            // a boast without lines would leave the kill report to the stock voice.
            var speaker = SpeakerFor(shooter);
            bool hasBoast = Config.Lines.Any(l => l.IsFor(boastId) && (l.SpeakerIds.Count == 0 || l.NamesSpeaker(speaker)));
            return hasBoast ? boastId : Situations.KillReport;
        }

        // ------------------------------------------------------------------ dialogue pacing

        // When the last wingman line on the radio ends (the radio is a queue, so a line queued behind another starts
        // when that one ends). Death rattles are left out - one plays at every death, so it says nothing about pacing.
        // Real time (Time.time), not mission time.
        private float _wingBusyUntil = LongAgo;

        public void NoteWingLine(float seconds)
        {
            var now = Time.time;
            _wingBusyUntil = Math.Max(_wingBusyUntil, now) + Math.Max(0.5f, seconds);
        }

        /// <summary>Seconds since the last wingman line finished (negative while one is still playing).</summary>
        public float SecondsSinceWingLine => Time.time - _wingBusyUntil;

        // ------------------------------------------------------------------ is the flight in a fight?

        private float _lastOwnFire = LongAgo;
        private readonly Dictionary<object, float> _ownFireAtGroup = new Dictionary<object, float>();   // hostile FlightGroup -> last time we shot at it

        public void NoteOwnFire(Ship target = null)
        {
            _lastOwnFire = Mission.MissionTime;
            try
            {
                var targetGroup = target?.FlightGroup;
                if (targetGroup != null) _ownFireAtGroup[targetGroup] = _lastOwnFire;
            }
            catch { }
        }

        /// <summary>Has the player's flight fired or been fired at within <paramref name="seconds"/>? (our own
        /// WeaponFired events, hostile fire at the player, hostile fire at flightmates - all from the game's events.)</summary>
        public bool FlightEngagedRecently(float seconds) => FlightEngagedRecently(seconds, null);

        /// <summary>As above, but fire exchanged with <paramref name="exceptGroup"/> (the group that is arriving) does
        /// not count: "here they come" means the flight is not busy with ANOTHER dogfight, not that nobody has yet
        /// shot at the newcomers.</summary>
        public bool FlightEngagedRecently(float seconds, object exceptGroup)
        {
            var now = Mission.MissionTime;
            if (exceptGroup == null && now - _lastOwnFire <= seconds) return true;
            if (exceptGroup != null && _ownFireAtGroup.Any(fire => !ReferenceEquals(fire.Key, exceptGroup) && now - fire.Value <= seconds)) return true;
            if (_lastFireAtPlayer.Any(fire => now - fire.Value <= seconds && !InGroup(fire.Key, exceptGroup))) return true;
            if (_wingmanAggressor.Values.Any(attack => now - attack.time <= seconds && !InGroup(attack.aggressor, exceptGroup))) return true;
            return false;
        }

        private static bool InGroup(Ship ship, object group)
        {
            try { return group != null && ship != null && ReferenceEquals(ship.FlightGroup, group); } catch { return false; }
        }

        /// <summary>A hostile fighter group with at least <paramref name="minCraft"/> members that have taken members of
        /// the player's flight as their Target (the game's ThreateningMe on our ships), whose nearest member is within
        /// <paramref name="maxDistance"/> of one of ours and closing on it. Returns the FlightGroup (as object, for a
        /// once-per-group memory) or null.</summary>
        public object ApproachingHostileGroup(float maxDistance, int minCraft, out string name, out float nearest)
        {
            name = "?"; nearest = 0f;
            var player = Player;
            if (player == null) return null;
            var ours = Mission.GetAllShipsOtherThanPlayer().Where(w => IsWingman(w) && IsAlive(w) && SameFlightGroupAsPlayer(w)).ToList();
            ours.Add(player);
            var threats = new HashSet<Ship>();
            foreach (var mate in ours)
            {
                var threateningMate = mate.ThreateningMe;
                if (threateningMate == null) continue;
                foreach (var hostile in threateningMate)
                    if (IsAlive(hostile) && hostile.Iff != player.Iff && hostile.Kind == EntityKind.Fighter) threats.Add(hostile);
            }
            foreach (var group in threats.GroupBy(hostile => hostile.FlightGroup))
            {
                if (group.Key == null || group.Count() < minCraft) continue;
                float nearestSqr = float.MaxValue; bool closing = false;
                foreach (var hostile in group)
                    foreach (var mate in ours)
                    {
                        float sqrDistance = hostile.GetSqrDistance(mate);
                        if (sqrDistance < nearestSqr) { nearestSqr = sqrDistance; closing = Vector3.Dot(hostile.Velocity, mate.Position - hostile.Position) > 0f; }
                    }
                float distance = (float)Math.Sqrt(nearestSqr);
                if (distance <= maxDistance && closing) { name = SafeDesignation(group.First()); nearest = distance; return group.Key; }
            }
            return null;
        }

        // ------------------------------------------------------------------ attacker / threat context (player warnings)

        public const float ThreatPersistSeconds = 20f;   // warned about this ship within this long -> "persistent"
        public const float ThreatMultipleSeconds = 8f;   // 2+ distinct ships firing on the player within this -> "multiple"
        private const float ForgetAttackerSeconds = 60f; // an attacker that has not fired for this long is forgotten

        private readonly Dictionary<Ship, float> _lastFireAtPlayer = new Dictionary<Ship, float>();   // attacker -> last time it fired on the player
        private readonly Dictionary<Ship, float> _lastWarnedAbout = new Dictionary<Ship, float>();    // attacker -> last time the player was warned about it

        /// <summary>X-Wing craft id -> attacker class tags. A TIE is both "tie" and its own class.</summary>
        public static List<string> AttackerClasses(Ship attacker)
        {
            if (attacker == null) return new List<string>();
            int craftTypeId = -1; try { craftTypeId = attacker.TypeId; } catch { }
            bool isBigShip = false; try { isBigShip = attacker.IsBigSize; } catch { }
            return LineTags.CraftClasses(craftTypeId, isBigShip);
        }

        /// <summary>Record a laser-fire event from <paramref name="attacker"/> and classify the threat: new /
        /// persistent / multiple.</summary>
        public string NoteAttack(Ship attacker)
        {
            if (attacker == null) return null;
            var now = Mission.MissionTime;
            _lastFireAtPlayer[attacker] = now;
            foreach (var forgotten in _lastFireAtPlayer.Where(fire => now - fire.Value > ForgetAttackerSeconds).Select(fire => fire.Key).ToList())
            {
                _lastFireAtPlayer.Remove(forgotten);
                _lastWarnedAbout.Remove(forgotten);
            }
            var firingNow = _lastFireAtPlayer.Count(fire => now - fire.Value <= ThreatMultipleSeconds && IsAlive(fire.Key));
            if (firingNow >= 2) return "multiple";
            if (_lastWarnedAbout.TryGetValue(attacker, out var warnedAt) && now - warnedAt <= ThreatPersistSeconds) return "persistent";
            return "new";
        }

        public void MarkWarned(Ship attacker) { if (attacker != null) _lastWarnedAbout[attacker] = Mission.MissionTime; }

        // ------------------------------------------------------------------ arrivals

        private readonly HashSet<string> _seenArrivalClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>"new" the first time a craft class arrives this mission, "more" afterwards (drives arrival line tags).</summary>
        public string NoteArrival(List<string> classes)
        {
            bool seen = classes != null && classes.Any(c => _seenArrivalClasses.Contains(c));
            if (classes != null) foreach (var c in classes) _seenArrivalClasses.Add(c);
            return seen ? "more" : "new";
        }

        // ------------------------------------------------------------------ save-thanks (who is shooting which wingman)

        public const float SaveWindowSeconds = 10f;
        private readonly Dictionary<Ship, (Ship aggressor, float time)> _wingmanAggressor = new Dictionary<Ship, (Ship, float)>();   // wingman -> who last fired at or hit it, and when

        public bool SaveCountsTargeting = false;   // the Radio.SaveCountsTargeting setting; see WingmanSavedFrom, gate 2
        /// <summary>Why the last WingmanSavedFrom answer was yes (for the log).</summary>
        public string LastSaveReason = "";

        public void NoteWingmanAggressor(Ship wingman, Ship aggressor)
        {
            _wingmanAggressor[wingman] = (aggressor, Mission.MissionTime);
        }

        /// <summary>The live wingman the player just saved by killing <paramref name="deadHostile"/>, else null. The
        /// gates are the game's own threat data:
        ///   1. the hostile hit or fired at the wingman within the last SaveWindowSeconds (WeaponHit events and the
        ///      game's Ship.NotifyFiredLaser, which the AI calls on its target whenever it shoots);
        ///   2. only when SaveCountsTargeting is on: the wingman is in the hostile's sights right now -
        ///      wingman.ThreateningMe contains the hostile (set when the hostile took it as Target) and they are within
        ///      the game's Ship.ThreateningDistance (800 m). Off by default: in a furball the AI pairs every hostile
        ///      with a wingman inside that distance, so nearly every kill would count as a save.</summary>
        public Ship WingmanSavedFrom(Ship deadHostile)
        {
            LastSaveReason = "";
            if (deadHostile == null) return null;
            var now = Mission.MissionTime;
            foreach (var attack in _wingmanAggressor)
            {
                var wingman = attack.Key;
                if (!ReferenceEquals(attack.Value.aggressor, deadHostile)) continue;
                if (now - attack.Value.time > SaveWindowSeconds) continue;
                if (IsOnRadio(wingman)) { LastSaveReason = $"fired at / hit {SafeDesignation(wingman)} {now - attack.Value.time:0.0} s ago"; return wingman; }
            }
            if (!SaveCountsTargeting) return null;
            try
            {
                foreach (var wingman in Mission.GetAllShipsOtherThanPlayer())
                {
                    if (!IsWingman(wingman) || !IsOnRadio(wingman)) continue;
                    var threats = wingman.ThreateningMe;
                    if (threats == null || !threats.Contains(deadHostile)) continue;
                    float threateningDistance = wingman.ThreateningDistance;
                    if (deadHostile.GetSqrDistance(wingman) <= threateningDistance * threateningDistance)
                    { LastSaveReason = $"had {SafeDesignation(wingman)} targeted within {threateningDistance:0} m"; return wingman; }
                }
            }
            catch (Exception e) { _log($"[chatter] save check: {e.Message}"); }
            return null;
        }
    }
}
