using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace XwvmChatter
{
    /// <summary>One callsign version of a line: a whole recorded line that names a callsign.</summary>
    public sealed class CallsignVersion
    {
        /// <summary>Kind: the player's callsign leads a warning ("Red Two, break off!"). The default kind.</summary>
        public const string Address = "address";
        /// <summary>Kind: the speaker names itself ("This is Blue Leader...").</summary>
        public const string SelfId = "selfid";

        public string Kind;        // whose callsign the line carries: Address, SelfId, or "lost" / "reply" / "mate"; lower case
        public string Callsign;    // in-game callsign, e.g. "Red 1"
        public string Situation;
        public string AudioFile;   // absolute, resolved next to the manifest
        public string Text;

        public override string ToString() => $"{Kind}:{Callsign}:{Situation}:{Path.GetFileName(AudioFile)}";
    }

    /// <summary>A voice's callsigns.xml: the callsign versions recorded for that voice.</summary>
    public sealed class VoiceManifest
    {
        public readonly List<CallsignVersion> Versions = new List<CallsignVersion>();
    }

    /// <summary>
    /// Loads the callsigns.xml manifest that sits next to a voice's clips, and compares callsigns. The casting files
    /// never reference a manifest; the plugin finds it through the folder a line's clip is in. Only the
    /// &lt;composed&gt; elements (the callsign versions) are read; anything else in the file is ignored.
    /// </summary>
    public static class CallsignMap
    {
        private const string ManifestFile = "callsigns.xml";

        /// <summary>The manifest of the voice folder <paramref name="dir"/>; empty when the folder has none or it
        /// cannot be read. A version whose clip is missing is left out.</summary>
        public static VoiceManifest LoadManifest(string dir, Action<string> log)
        {
            var manifest = new VoiceManifest();
            var path = Path.Combine(dir, ManifestFile);
            if (!File.Exists(path)) return manifest;
            try
            {
                var root = XElement.Load(path);
                foreach (var element in root.Elements("composed"))
                {
                    var audioFile = ResolveAudio(dir, element, log);
                    if (audioFile == null) continue;
                    manifest.Versions.Add(new CallsignVersion
                    {
                        Kind = AttributeOrNull(element, "kind")?.ToLowerInvariant() ?? CallsignVersion.Address,
                        Callsign = AttributeOrNull(element, "callsign"),
                        Situation = AttributeOrNull(element, "situation"),
                        AudioFile = audioFile,
                        Text = (element.Value ?? "").Trim(),
                    });
                }
            }
            catch (Exception e) { log?.Invoke($"[chatter] callsigns.xml parse error in {dir}: {e.Message}"); }
            return manifest;
        }

        /// <summary>The trimmed attribute value; null when it is absent or empty.</summary>
        private static string AttributeOrNull(XElement element, string name)
        {
            var value = element.Attribute(name)?.Value?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>The full path of the element's clip; null (and a log line) when the file is not there.</summary>
        private static string ResolveAudio(string dir, XElement element, Action<string> log)
        {
            var audio = element.Attribute("audio")?.Value?.Trim();
            if (string.IsNullOrEmpty(audio)) return null;
            var fullPath = Path.GetFullPath(Path.Combine(dir, audio));
            if (!File.Exists(fullPath)) { log?.Invoke($"[chatter] callsigns.xml references missing {audio} in {dir}"); return null; }
            return fullPath;
        }

        /// <summary>"Red 1" == "Red Leader" == "Red"; otherwise plain case-insensitive equality.</summary>
        public static bool CallsignsEqual(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(WithoutLeaderSuffix(a), WithoutLeaderSuffix(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>"Red Leader" -> "Red", "Red 1" -> "Red"; any other callsign unchanged (trimmed).</summary>
        private static string WithoutLeaderSuffix(string callsign)
        {
            const string leader = " Leader", one = " 1";
            callsign = (callsign ?? "").Trim();
            if (callsign.EndsWith(leader, StringComparison.OrdinalIgnoreCase)) return callsign.Substring(0, callsign.Length - leader.Length);
            if (callsign.EndsWith(one, StringComparison.OrdinalIgnoreCase)) return callsign.Substring(0, callsign.Length - one.Length);
            return callsign;
        }
    }
}
