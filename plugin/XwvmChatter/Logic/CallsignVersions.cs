using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XwvmChatter
{
    /// <summary>
    /// Every callsign version in the mission's voices, findable by the script line it belongs to.
    ///
    /// A script line "UnderAttack-3" has plain takes ("UnderAttack-3-t1.ogg", "-t2", ...) and, in the same voice
    /// folder, whole recorded versions that say a callsign ("UnderAttack-3-v4--address--red-2.ogg"). A version
    /// belongs to the LINE, not to one take: it matches any take whose name starts with the same
    /// "&lt;Situation&gt;-&lt;variant&gt;-" prefix.
    /// </summary>
    public sealed class CallsignVersions
    {
        private static readonly List<CallsignVersion> None = new List<CallsignVersion>();

        private readonly List<CallsignVersion> _all = new List<CallsignVersion>();
        // voice folder -> its versions, in manifest order
        private readonly Dictionary<string, List<CallsignVersion>> _byFolder = new Dictionary<string, List<CallsignVersion>>(StringComparer.OrdinalIgnoreCase);
        // a line's clip path -> its versions (filled on first ask; emptied whenever a manifest is added)
        private readonly Dictionary<string, List<CallsignVersion>> _byLine = new Dictionary<string, List<CallsignVersion>>(StringComparer.OrdinalIgnoreCase);

        public int Count => _all.Count;
        public IReadOnlyList<CallsignVersion> All => _all;

        public void Add(VoiceManifest manifest)
        {
            foreach (var version in manifest.Versions)
            {
                _all.Add(version);
                var folder = Path.GetDirectoryName(version.AudioFile) ?? "";
                if (!_byFolder.TryGetValue(folder, out var inFolder)) _byFolder[folder] = inFolder = new List<CallsignVersion>();
                inFolder.Add(version);
            }
            _byLine.Clear();
        }

        /// <summary>The versions of this script line, whatever callsign or kind, in manifest order.</summary>
        public IReadOnlyList<CallsignVersion> Of(Line line) => line?.AudioFile == null ? None : Of(line.AudioFile);

        /// <summary>As <see cref="Of(Line)"/>, by the path of any of the line's clips.</summary>
        public IReadOnlyList<CallsignVersion> Of(string lineAudioFile)
        {
            if (_byLine.TryGetValue(lineAudioFile, out var versions)) return versions;
            versions = None;
            if (_byFolder.TryGetValue(Path.GetDirectoryName(lineAudioFile) ?? "", out var inFolder))
            {
                var prefix = LinePrefix(lineAudioFile);
                versions = inFolder.Where(v => Path.GetFileName(v.AudioFile).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            _byLine[lineAudioFile] = versions;
            return versions;
        }

        /// <summary>"…/UnderAttack-3-t2.ogg" -> "UnderAttack-3-": the clip name without its last dash-separated part.</summary>
        public static string LinePrefix(string audioFile)
        {
            var clipName = Path.GetFileNameWithoutExtension(audioFile);
            var lastDash = clipName.LastIndexOf('-');
            return (lastDash > 0 ? clipName.Substring(0, lastDash) : clipName) + "-";
        }
    }
}
