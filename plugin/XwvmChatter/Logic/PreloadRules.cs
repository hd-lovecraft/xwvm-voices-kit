using System;
using System.IO;

namespace XwvmChatter
{
    /// <summary>The plain rules the loading-screen plan is built from: which voice folder a clip sits in, the flight
    /// word of a designation, and the order situations are read in. No game equivalent: the game loads its radio
    /// clips at play time and plans nothing.</summary>
    public static class PreloadRules
    {
        // Situations in the order they are likely to be needed from the first second of a mission; the loading
        // screen reads files in this order.
        private static readonly string[] SituationOrder =
        {
            "EnemyArrival", "UnderAttack", "IncomingMissile", "AttackAck", "HelpAck", "Evade", "IgnoreTarget", "Proceeding",
            "AwaitingOrders", "GoingHome", "PreparingHyper", "WingmanHelpRequest", "WingmanDestroyed", "KillReport", "NiceShot", "SaveThanks",
        };

        /// <summary>Where a situation stands in the reading order; situations not listed come last.</summary>
        public static int SituationRank(string situation)
        {
            int rank = Array.IndexOf(SituationOrder, situation ?? "");
            return rank < 0 ? SituationOrder.Length : rank;
        }

        /// <summary>The voice folder a clip sits in ("…/VOICE/_chatter/jon-marek/x.ogg" -> "jon-marek").</summary>
        public static string VoiceDirOf(string path) =>
            Path.GetFileName(Path.GetDirectoryName(path) ?? "") ?? "";

        /// <summary>"Red 2" -> "Red".</summary>
        public static string FlightWord(string designation) =>
            string.IsNullOrEmpty(designation) ? "" : designation.Trim().Split(' ')[0];
    }
}
