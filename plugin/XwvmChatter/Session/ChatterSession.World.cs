using System;
using System.Collections.Generic;
using System.Linq;
using Common;
using Xwvm.MissionClasses.Ai;
using Xwvm.MissionClasses.Entities;

namespace XwvmChatter
{
    // Questions about the game world: who is alive, who is a wingman, whose flight group, what a ship is called.
    public sealed partial class ChatterSession
    {
        private const string LeaderSuffix = " Leader";

        public bool FlightLostSaid;   // the "your flight is gone" call is made once per mission

        private Ship Player => Mission?.PlayerShip;
        internal Ship PlayerShipOrNull => Player;
        private bool PlayerHasAstromech => Player?.Stats?.HasAstromech ?? false;

        private GameManager GameManagerOrNull() => Mission?.GameManager as GameManager;

        /// <summary>Is the player's flight gone once <paramref name="victim"/> is? No live wingman of the player's
        /// flight group left on the radio. Our own rule - the game has no equivalent call.</summary>
        public bool FlightGone(Ship victim)
        {
            try
            {
                return !Mission.GetAllShipsOtherThanPlayer().Any(w => !ReferenceEquals(w, victim) && IsWingman(w) && SameFlightGroupAsPlayer(w) && IsOnRadio(w));
            }
            catch { return false; }
        }

        /// <summary>Is seat <paramref name="number"/> of flight group <paramref name="group"/> ("red", 1) a live craft
        /// right now? (The player counts.)</summary>
        public bool SeatAlive(string group, int number)
        {
            try
            {
                bool IsLiveSeat(Ship ship) => ship != null && string.Equals(ship.FlightGroup?.Designation?.Trim(), group, StringComparison.OrdinalIgnoreCase)
                                              && ship.shipNumberInWave == number && IsAlive(ship);
                return IsLiveSeat(Player) || Mission.GetAllShipsOtherThanPlayer().Any(IsLiveSeat);
            }
            catch { return true; }   // unsure: leave the story line as written
        }

        /// <summary>The game's own weapon classes: is this projectile a torpedo or a missile?</summary>
        public static bool IsWarhead(Projectile projectile)
        {
            try
            {
                var type = projectile?.WeaponStats?.Type;
                return type.HasValue && (WeaponTypeExtension.torpedoTypes.Contains(type.Value) || WeaponTypeExtension.missileTypes.Contains(type.Value));
            }
            catch { return false; }
        }

        /// <summary>Every craft on the player's side other than the player - any kind of ship, but no containers,
        /// mines or objects.</summary>
        public IEnumerable<Ship> FriendlyShips()
        {
            var player = Player;
            if (player == null) yield break;
            foreach (var ship in Mission.GetAllShipsOtherThanPlayer())
            {
                if (ship == null || ship.Iff != player.Iff) continue;
                var kind = ship.Kind;
                if (kind == EntityKind.Container || kind == EntityKind.Mine || kind == EntityKind.Object) continue;
                yield return ship;
            }
        }

        public static bool IsAlive(Ship ship) => ship != null && ship.IsPresent && !ship.IsDead;

        private static bool IsDeathRattle(string situationId) =>
            string.Equals(situationId, Situations.WingmanDestroyed, StringComparison.OrdinalIgnoreCase);

        /// <summary>The speaker died before the line could play (clip load, delay, callsign swap): drop it - EXCEPT a death
        /// rattle, whose speaker is dead by definition.</summary>
        public static bool SpeakerGone(Ship ship, string situationId) =>
            ship != null && !IsAlive(ship) && !IsDeathRattle(situationId);

        /// <summary>A wingman on the way out: the game's own ShipFlag.Leaving / HyperingOut, applied when a fighter enters
        /// FlightPlanEnum.GoToHyperspace (FlightPlanXw: DefaultTask HyperOut, ApplyFlagsOnEnter Leaving) - the same moment
        /// HyperOutTask.OnStart reports CraftReportsGoingToHyperspace, i.e. the "hyperdrive spooling up" line.</summary>
        public static bool IsLeaving(Ship ship) => ship != null && (ship.Flags.Has(ShipFlag.Leaving) || ship.Flags.Has(ShipFlag.HyperingOut));

        /// <summary>Alive and not leaving: may be CHOSEN to speak (warnings, praise, laments, replies, thanks). OUR rule, not
        /// the game's - XwGameplaySituationHandler voices any sender. Without it a leader who has said goodbye keeps
        /// calling targets all the way to the jump point. Lines about the ship's own events (the game names it as
        /// sender: the jump call, being hit, its death) still play.</summary>
        public static bool IsOnRadio(Ship ship) => IsAlive(ship) && !IsLeaving(ship);

        /// <summary>Is this ship a wingman of the player: same craft kind and same side? (transports/capitals/hostiles: no)</summary>
        public bool IsWingman(Ship ship)
        {
            var player = Player;
            if (ship == null || player == null || ReferenceEquals(ship, player)) return false;
            try { return ship.Kind == player.Kind && ship.Iff == player.Iff; } catch { return false; }
        }

        /// <summary>The game's own state for "nothing is happening": GameManager.freezeMission, which is set for the
        /// whole of the player's hyper-in and hyper-out sequences (tunnel, streaks, S-foils), the start-up frame and
        /// restarts. The simulation ticks with dt 0 meanwhile, so mission time stands still - which is also why the
        /// game's own announcer never talks over a jump.</summary>
        public bool IsMissionFrozen()
        {
            try { return GameManagerOrNull()?.freezeMission ?? false; } catch { return false; }
        }

        /// <summary>Same flight group as the player ("Red 3" when you fly Red 1)?</summary>
        public bool SameFlightGroupAsPlayer(Ship ship)
        {
            var player = Player;
            if (ship == null || player == null) return false;
            try
            {
                var shipGroup = ship.FlightGroup;
                var playerGroup = player.FlightGroup;
                if (shipGroup != null && playerGroup != null && ReferenceEquals(shipGroup, playerGroup)) return true;
                var shipGroupName = shipGroup?.Designation ?? "";
                var playerGroupName = playerGroup?.Designation ?? "";
                return shipGroupName != "" && string.Equals(shipGroupName, playerGroupName, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>Own flight by callsign ("RED 2" -> "RED" vs the player's flight group): a destroyed ship's FlightGroup
        /// can no longer be relied on when its death call arrives (a wingman's own death rattle was once classed as
        /// "other flight" and dropped for radio spacing).</summary>
        public bool SameFlightByCallsign(Ship ship)
        {
            try
            {
                var callsign = BareDesignation(ship);
                var player = Player;
                if (player == null || string.IsNullOrEmpty(callsign)) return false;
                var lastSpace = callsign.LastIndexOf(' ');
                var flight = lastSpace > 0 && int.TryParse(callsign.Substring(lastSpace + 1), out _) ? callsign.Substring(0, lastSpace) : callsign;
                if (flight.EndsWith(LeaderSuffix, StringComparison.OrdinalIgnoreCase)) flight = flight.Substring(0, flight.Length - LeaderSuffix.Length);
                var playerFlight = player.FlightGroup?.Designation ?? "";
                return playerFlight != "" && string.Equals(flight.Trim(), playerFlight.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>"Red 2 (Pilot Name)" -> "Red 2".</summary>
        public static string BareDesignation(Ship ship)
        {
            var designation = SafeDesignation(ship);
            var pilotName = designation.IndexOf(" (", StringComparison.Ordinal);
            return (pilotName > 0 ? designation.Substring(0, pilotName) : designation).Trim();
        }

        /// <summary>The ship's designation, "?" when there is no ship or the game cannot give one.</summary>
        public static string SafeDesignation(Ship ship)
        {
            try { return ship?.Designation ?? "?"; } catch { return "?"; }
        }
    }
}
