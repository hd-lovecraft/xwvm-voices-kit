using System;
using System.Collections.Generic;
using Xwvm.MissionClasses.Entities;
using Xwvm.MissionClasses.Simulation.Events;

namespace XwvmChatter
{
    /// <summary>
    /// The situations a line can be for. Maps the game's typed GameplaySituation records (and the named "custom
    /// radio message" types used by the XWVM-content code path) onto the stable situation ids that the data files
    /// write in &lt;line situation="..."&gt;.
    /// </summary>
    public static partial class Situations
    {
        // ---- Situation ids (as written in the data files) ----
        public const string UnderAttack       = "UnderAttack";        // player under laser fire - chosen speaker warns
        public const string IncomingMissile   = "IncomingMissile";    // missile at player - chosen speaker warns
        public const string AttackAck         = "AttackAck";          // sender acks "attack my target"
        public const string HelpAck           = "HelpAck";            // sender acks "cover me"
        public const string IgnoreTarget      = "IgnoreTarget";       // sender acks "ignore target"
        public const string Evade             = "Evade";              // sender acks "evade"
        public const string GoingHome         = "GoingHome";          // sender acks "go home"
        public const string PreparingHyper    = "PreparingHyper";     // sender about to jump out
        public const string AwaitingOrders    = "AwaitingOrders";
        public const string Proceeding        = "Proceeding";
        public const string KillReport        = "KillReport";         // sender destroyed something
        public const string NiceShot          = "NiceShot";           // sender congratulates player
        public const string DockingStart      = "DockingStart";
        public const string DockingComplete   = "DockingComplete";
        public const string Captured          = "Captured";           // sender captured a craft
        public const string Repaired          = "Repaired";           // sender was repaired
        public const string NotResponding     = "NotResponding";      // sender not responding to orders (text-only in stock)
        // From the mission-event listener (only produced by the game in "XWVM content" mode):
        public const string SaveThanks         = "SaveThanks";         // player killed the fighter that was shooting a wingman - THAT wingman thanks
        public const string EnemyArrival       = "EnemyArrival";       // hostile flight group arrives (hyper or carrier launch) - chosen speaker warns
        public const string WingmanHelpRequest = "WingmanHelpRequest"; // sender taking hits / shields down
        public const string WingmanDestroyed   = "WingmanDestroyed";   // sender dying
        public const string KillBoastReply     = "KillBoastReply";     // a flightmate teases the boaster (plain, chosen speaker)
        public const string TorpedoHit         = "TorpedoHit";         // a wingman's OWN warhead connects (implicit sender)
        public const string Engagement         = "Engagement";         // "here they come": a hostile fighter group closes on a quiet flight
        public const string WingmanLost        = "WingmanLost";        // a flightmate died - a survivor laments by name ({lost})
        public const string FlightLost         = "FlightLost";         // the player's last flightmate died - the officer takes over ({player})
        public const string WingmanLostReply   = "WingmanLostReply";   // a lower-numbered flightmate (else the officer) reassures the lamenter ({addressee})
        public const string MCCAttacked        = "MCCAttacked";        // mission-critical craft hit (speaker: any friendly)
        public const string MCCShieldsDown     = "MCCShieldsDown";
        public const string MCCHullCritical    = "MCCHullCritical";
        public const string MCCDestroyed       = "MCCDestroyed";
        // Player-side situations produced but never voiced by the X-Wing handler:
        public const string PrimaryComplete    = "PrimaryComplete";
        public const string PrimaryFailed      = "PrimaryFailed";
        public const string SecondaryComplete  = "SecondaryComplete";
        public const string BonusComplete      = "BonusComplete";
        public const string ReinforcementsOk   = "ReinforcementsOk";
        public const string ReinforcementsNone = "ReinforcementsNone";

        /// <summary>Count-specific kill boasts by the shooter ("That's three for me."): "KillStreak2".."KillStreak5" for
        /// the shooter's 2nd..5th hostile fighter this mission, null for any other count.</summary>
        public static string KillStreak(int kills) => kills >= 2 && kills <= 5 ? "KillStreak" + kills : null;
        public static bool IsKillStreak(string id) => id != null && id.StartsWith("KillStreak", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Result of classifying a situation. Sender == null means the game names no speaker (a warning or remark
        /// to the player; the resolver chooses who talks). Sender != null means the game says who is speaking.
        /// </summary>
        public struct Classified
        {
            public string Id;
            public Ship Sender;       // the speaker the game names, if any
            public Ship Attacker;     // for player warnings: who is shooting (UnderAttack) - drives attacker/threat line tags
            public bool IsValid => Id != null;
        }

        /// <summary>Custom radio message type names used by the game's XWVM-content code path -> situation ids.</summary>
        public static readonly Dictionary<string, string> CustomMessageTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "WingmanAttackTarget", AttackAck },
            { "WingmanHelpAck", HelpAck },
            { "WingmanIgnoreTarget", IgnoreTarget },
            { "WingmanEvade", Evade },
            { "WingmanHyperOut", GoingHome },
            { "WingmanPreparingHyperOut", PreparingHyper },
            { "WingmanHelpRequest", WingmanHelpRequest },
            { "WingmanDestroyed", WingmanDestroyed },
            { "MCCAttacked", MCCAttacked },
            { "MCCShieldsDown", MCCShieldsDown },
            { "MCCHullCritical", MCCHullCritical },
            { "MCCDestroyed", MCCDestroyed },
            { "MCCBoardingStart", DockingStart },
            { "MCCBoardingComplete", DockingComplete },
        };

        /// <summary>What a named friendly ship says itself. XwGameplaySituationHandler raises CraftReportsVictory for any
        /// friendly Ship (no kind check) and voices it with the stock clips; CraftReportsGoingToHyperspace is raised for every
        /// craft but voiced only for the player's kind (voicing it for ships is our deviation); the docking calls
        /// come from transports/shuttles/freighters. Damage calls stay with the mission's own VO / the officer.</summary>
        public static readonly HashSet<string> ShipSpoken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { KillReport, PreparingHyper, DockingStart, DockingComplete };

        /// <summary>Situations whose lines address the PLAYER by callsign ({player} in the script): the resolver prefers a
        /// speaker whose voice has the callsign version for the player's seat.</summary>
        public static readonly HashSet<string> Addressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            UnderAttack, IncomingMissile, NiceShot, SaveThanks,
        };

        /// <summary>Situations whose speaker the resolver chooses (the game names no sender).</summary>
        public static readonly HashSet<string> PlayerWarnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            UnderAttack, IncomingMissile, EnemyArrival, Engagement, MCCAttacked, MCCShieldsDown, MCCHullCritical, MCCDestroyed,
            // boarding: the game sends MCCBoardingStart/Complete only for a friendly mission-critical craft
            // (XwGameplaySituationHandler); the tour's flight officer answers, as for the MCC calls
            DockingStart, DockingComplete,
            PrimaryComplete, PrimaryFailed, SecondaryComplete, BonusComplete, ReinforcementsOk, ReinforcementsNone,
        };

        /// <summary>The situation id (and sender or attacker) for one of the game's situation records; an invalid
        /// result for a record the chatter has no lines for.</summary>
        public static Classified Classify(GameplaySituation situation)
        {
            switch (situation)
            {
                case GameplaySituation.IncomingLaserFireTowardsPlayer x:  return new Classified { Id = UnderAttack, Attacker = x.Attacker as Ship };
                case GameplaySituation.IncomingMissileTowardsPlayer _:    return new Classified { Id = IncomingMissile };
                case GameplaySituation.WingmanAcknowledgesAttackTarget x: return new Classified { Id = AttackAck, Sender = x.Wingman as Ship };
                case GameplaySituation.WingmanAcknowledgesComingToHelp x: return new Classified { Id = HelpAck, Sender = x.Wingman as Ship };
                case GameplaySituation.WingmanIgnoringTarget x:           return new Classified { Id = IgnoreTarget, Sender = x.Wingman as Ship };
                case GameplaySituation.CraftAcknowledgesEvading x:        return new Classified { Id = Evade, Sender = x.Craft as Ship };
                case GameplaySituation.CraftAcknowledgesGoingHome x:      return new Classified { Id = GoingHome, Sender = x.Craft as Ship };
                case GameplaySituation.CraftReportsGoingToHyperspace x:   return new Classified { Id = PreparingHyper, Sender = x.Craft as Ship };
                case GameplaySituation.CraftAwaitingOrders x:             return new Classified { Id = AwaitingOrders, Sender = x.Craft as Ship };
                case GameplaySituation.CraftProceedingWithMission x:      return new Classified { Id = Proceeding, Sender = x.Craft as Ship };
                case GameplaySituation.CraftReportsVictory x:             return new Classified { Id = KillReport, Sender = x.Craft as Ship };
                // The game names "the first friendly ship in the list" as the congratulator (Mission.ResolveWeaponHit), so it would
                // always be the same wingman. Treat it like a warning instead: no sender -> any live wingman, own flight preferred.
                // (Stock rule kept: the game only raises this for PLAYER kills at > 150 m hull distance.)
                case GameplaySituation.CraftCongratulatesPlayerForKill _: return new Classified { Id = NiceShot };
                case GameplaySituation.CraftCommencesDockingOperation x:  return new Classified { Id = DockingStart, Sender = x.Craft as Ship };
                case GameplaySituation.CraftCompletesDockingOperation x:  return new Classified { Id = DockingComplete, Sender = x.Craft as Ship };
                case GameplaySituation.CraftCapturesAnotherCraft x:       return new Classified { Id = Captured, Sender = x.Craft as Ship };
                case GameplaySituation.CraftIsRepaired x:                 return new Classified { Id = Repaired, Sender = x.Craft as Ship };
                case GameplaySituation.CraftNotRespondingToOrders x:      return new Classified { Id = NotResponding, Sender = x.Craft as Ship };
                case GameplaySituation.PrimaryMissionCompleted _:         return new Classified { Id = PrimaryComplete };
                case GameplaySituation.PrimaryMissionFailed _:            return new Classified { Id = PrimaryFailed };
                case GameplaySituation.SecondaryMissionCompleted _:       return new Classified { Id = SecondaryComplete };
                case GameplaySituation.BonusMissionCompleted _:           return new Classified { Id = BonusComplete };
                case GameplaySituation.PlayerRequestReinforcementsSuccess _: return new Classified { Id = ReinforcementsOk };
                case GameplaySituation.PlayerRequestReinforcementsFailure _: return new Classified { Id = ReinforcementsNone };
                default: return default;
            }
        }
    }
}
