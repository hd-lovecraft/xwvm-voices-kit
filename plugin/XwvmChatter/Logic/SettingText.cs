using System;
using System.Globalization;

namespace XwvmChatter
{
    /// <summary>Parsing for the settings that are written as text: "2.5-4.5" and "KillReport=1.0-2.0,NiceShot=0.8-1.8".
    /// Numbers always use a dot, whatever the player's regional settings.</summary>
    public static class SettingText
    {
        /// <summary>"min-max" (or a single number, meaning both). False when it cannot be read.</summary>
        public static bool TryRange(string text, out float min, out float max)
        {
            min = max = 0f;
            var ends = (text ?? "").Split('-');
            return TryNumber(ends[0], out min)
                && TryNumber(ends.Length > 1 ? ends[1] : ends[0], out max);
        }

        /// <summary>The delay window for one situation out of "Situation=min-max,Situation=min-max"; null when the
        /// situation is not listed. The two numbers may come in either order; an unreadable max reads as 0.</summary>
        public static (float min, float max)? DelayFor(string delays, string situationId)
        {
            foreach (var entry in (delays ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var nameAndRange = entry.Split('=');
                if (nameAndRange.Length != 2 || !string.Equals(nameAndRange[0].Trim(), situationId, StringComparison.OrdinalIgnoreCase)) continue;
                var ends = nameAndRange[1].Split('-');
                if (TryNumber(ends[0], out var first))
                {
                    float second = first;
                    if (ends.Length > 1) TryNumber(ends[1], out second);
                    return (Math.Min(first, second), Math.Max(first, second));
                }
            }
            return null;
        }

        private static bool TryNumber(string text, out float number) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
}
