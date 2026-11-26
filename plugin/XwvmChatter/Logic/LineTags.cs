using System.Collections.Generic;
using System.Linq;

namespace XwvmChatter
{
    /// <summary>
    /// Line tags: a line may say which moments it fits (attacker="tie interceptor", threat="multiple",
    /// arrival="launched"). This turns a craft into its tag words and tests a line against the moment.
    /// </summary>
    public static class LineTags
    {
        /// <summary>The tag words for a craft, from the game's X-Wing craft type id (XwFlightGroupTypeEnum).
        /// A TIE is both "tie" and its own class; a craft the list does not know is "other".</summary>
        /// <param name="craftTypeId">The game's craft type id.</param>
        /// <param name="isBigShip">The game's own "big ship" flag: adds "capital".</param>
        public static List<string> CraftClasses(int craftTypeId, bool isBigShip)
        {
            var tags = new List<string>();
            switch (craftTypeId)
            {
                case 4: tags.Add("tie"); tags.Add("fighter"); break;
                case 5: tags.Add("tie"); tags.Add("interceptor"); break;
                case 6: tags.Add("tie"); tags.Add("bomber"); break;
                case 17: tags.Add("tie"); tags.Add("advanced"); break;
                case 7: tags.Add("gunboat"); break;
                case 1: tags.Add("rebel"); tags.Add("xwing"); break;
                case 2: tags.Add("rebel"); tags.Add("ywing"); break;
                case 3: tags.Add("rebel"); tags.Add("awing"); break;
                case 18: tags.Add("rebel"); tags.Add("bwing"); break;
                // non-fighters: lines tagged with these name the craft, so an arriving transport is not called "fighters"
                case 8: tags.Add("transport"); break;
                case 9: tags.Add("shuttle"); break;
                case 12: tags.Add("freighter"); break;
                case 14: tags.Add("frigate"); break;
                case 15: tags.Add("corvette"); break;
                case 16: tags.Add("stardestroyer"); break;
            }
            if (isBigShip) tags.Add("capital");
            if (tags.Count == 0) tags.Add("other");
            return tags;
        }

        /// <summary>Does the line fit this moment? Every tag list the line carries must match; a line with no tags
        /// always fits, so general phrasings shuffle in with the specific ones.</summary>
        /// <param name="arrival">May hold several words ("launched launched-stardestroyer"); any one matching is enough.</param>
        public static bool Fit(Line line, List<string> attackerClasses, string threat, string arrival)
        {
            if (line.Attacker.Count > 0 && (attackerClasses == null || !line.Attacker.Any(tag => attackerClasses.Contains(tag)))) return false;
            if (line.Threat.Count > 0 && (threat == null || !line.Threat.Contains(threat))) return false;
            if (line.Arrival.Count > 0 && (arrival == null || !arrival.Split(' ').Any(word => line.Arrival.Contains(word)))) return false;
            return true;
        }
    }
}
