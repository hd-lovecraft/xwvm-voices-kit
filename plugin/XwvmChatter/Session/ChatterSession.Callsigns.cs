using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Xwvm.MissionClasses.Entities;

namespace XwvmChatter
{
    // Callsign versions: whole recorded lines that say a callsign ("Red Two, break off!"), listed in each voice's
    // callsigns.xml. When the speaker's voice has a version of the chosen line for the seat being named, it replaces
    // the plain line. The game has no equivalent - its stock calls never name anyone.
    //
    // A version's kind says whose callsign it carries:
    //   address  the PLAYER's ("Red Two, break off!")           selfid  the speaker's own ("This is Blue Leader...")
    //   lost     a dead flightmate's ("We lost Red Three.")     reply   the flightmate being answered
    //   mate     the flightmate being teased
    public sealed partial class ChatterSession
    {
        private const float VersionLoadTimeoutSeconds = 5f;   // the "not ready after 5 s" log line states it too

        private readonly CallsignVersions _versions = new CallsignVersions();
        private readonly Dictionary<string, VoiceManifest> _manifests = new Dictionary<string, VoiceManifest>(StringComparer.OrdinalIgnoreCase);   // voice folder -> its callsigns.xml
        private readonly Dictionary<string, bool> _coversAddress = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);   // answers of VoiceCoversAddress

        /// <summary>Read the callsigns.xml of every voice folder this mission's lines live in. The casting files never
        /// mention these; a voice's versions are found through the folder its lines are in.</summary>
        private void DiscoverCallsigns()
        {
            foreach (var dir in Config.Lines.Where(l => l.AudioFile != null)
                         .Select(l => Path.GetDirectoryName(l.AudioFile))
                         .Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var manifest = CallsignMap.LoadManifest(dir, _log);
                _manifests[dir] = manifest;
                _versions.Add(manifest);
            }
            if (_versions.Count > 0)
                _log($"[chatter] callsigns: {_versions.Count} whole-line variant(s) indexed");
        }

        /// <summary>The callsign a speaker says for itself: a named ship's own name (its flight group's, shared by every
        /// ship of the group), otherwise the craft's designation ("Red 2"). Null when there is no craft.</summary>
        private static string OwnCallsign(Speaker speaker, Ship ship) =>
            speaker != null && speaker.IsShip && !string.IsNullOrEmpty(speaker.Callsign) ? speaker.Callsign
            : ship != null ? BareDesignation(ship) : null;

        /// <summary>Swap the resolved line for its callsign version: one that addresses the player's seat, or one where
        /// the speaker names its own. A rule, not a roll - a line the script writes with a callsign always carries it
        /// when the speaker's voice has the version. Keeps the plain line when none matches.</summary>
        /// <param name="deferred">True when the version's clip is still loading: it plays by itself when ready, and the
        /// caller must not play anything.</param>
        /// <returns>True when the line was swapped now.</returns>
        public bool TrySwapInCallsignVersion(ref Resolution res, out bool deferred)
        {
            deferred = false;
            if (res.Line == null || res.Line.AudioFile == null || _manifests.Count == 0) return false;
            string playerCallsign = BareDesignation(Player);
            string ownCallsign = OwnCallsign(res.Speaker, res.Ship);
            // A craft that shares the player's callsign speaks, but never says a name - no address, no self-ID. (A
            // mission can have two flight groups with one name; otherwise a "Red 1" of the other group tells the
            // player's craft "Good shooting, Red One".)
            if (ownCallsign != null && CallsignMap.CallsignsEqual(ownCallsign, playerCallsign)) return false;
            if (_versions.Count == 0) return false;
            // only clips with a callsign-version name ("<line>-v4--<kind>--<seat>.ogg")
            var matches = _versions.Of(res.Line).Where(v =>
                Path.GetFileName(v.AudioFile).Contains("--") &&
                CallsignMap.CallsignsEqual(v.Callsign, v.Kind == CallsignVersion.Address ? playerCallsign : ownCallsign)).ToList();
            if (matches.Count == 0) return false;
            // The callsign version always plays, waiting for its clip if need be - never the plain line instead (when it
            // fell back to plain on first use, the player almost never heard their callsign).
            var version = matches[UnityEngine.Random.Range(0, matches.Count)];
            if (!Loaded(version.AudioFile)) MarkSpoken(res.Line.Situation);   // take the throttle slot now so a burst doesn't queue a burst
            deferred = !SwapNowOrWhenLoaded(ref res, version, res.Line.Situation);
            return !deferred;
        }

        /// <summary>Swap in the version of <paramref name="kind"/> that names <paramref name="callsign"/> (a lament names
        /// the dead flightmate, a reply the pilot being answered). Keeps the plain line when no version exists.</summary>
        /// <param name="pendingClip">Set when the version is still loading.</param>
        /// <returns>True when the version is still loading and will play by itself; the caller must not play anything.
        /// False otherwise - swapped or not, the caller plays <paramref name="res"/>.</returns>
        public bool TrySwapInVersionNaming(ref Resolution res, string kind, string callsign, string situationId, out string pendingClip)
        {
            pendingClip = null;
            if (res.Line == null || res.Line.AudioFile == null || _versions.Count == 0 || string.IsNullOrEmpty(callsign)) return false;
            // never say the player's own callsign about another craft that shares it - plain line instead
            if (CallsignMap.CallsignsEqual(callsign, BareDesignation(Player))) return false;
            var matches = _versions.Of(res.Line).Where(v =>
                string.Equals(v.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                CallsignMap.CallsignsEqual(v.Callsign, callsign)).ToList();
            if (matches.Count == 0) return false;
            var version = matches[UnityEngine.Random.Range(0, matches.Count)];
            if (SwapNowOrWhenLoaded(ref res, version, situationId)) return false;
            pendingClip = version.AudioFile;
            return true;
        }

        /// <summary>Swap the version in now when its clip is in memory. Otherwise start loading it and leave the swap
        /// and the playing to <see cref="SwapWhenReady"/>.</summary>
        /// <returns>True when the line was swapped now; false when the version will play by itself.</returns>
        private bool SwapNowOrWhenLoaded(ref Resolution res, CallsignVersion version, string situationId)
        {
            if (Loaded(version.AudioFile))
            {
                ApplyVersion(ref res, version);
                return true;
            }
            RequestLoad(version.AudioFile);
            _runner.StartCoroutine(SwapWhenReady(res, version, situationId));
            _log($"[chatter] variant {Path.GetFileName(version.AudioFile)}: loading, will play when ready");
            return false;
        }

        /// <summary>Does this speaker's voice have any line that addresses the given callsign (the player's seat)?
        /// Asked for every candidate of every warning, so the answer is kept: scanning ~6,000 versions each time cost
        /// 80-120 ms frames on every "under attack" pick.</summary>
        public bool VoiceCoversAddress(Speaker speaker, string callsign)
        {
            if (speaker == null || string.IsNullOrEmpty(speaker.Voice) || string.IsNullOrEmpty(callsign)) return false;
            var voiceDir = ChatterConfig.Slug(speaker.Voice);
            var key = voiceDir + "\n" + callsign;
            if (_coversAddress.TryGetValue(key, out var covers)) return covers;
            covers = _versions.All.Any(v => v.Kind == CallsignVersion.Address && CallsignMap.CallsignsEqual(v.Callsign, callsign)
                                            && string.Equals(PreloadRules.VoiceDirOf(v.AudioFile), voiceDir, StringComparison.OrdinalIgnoreCase));
            _coversAddress[key] = covers;
            return covers;
        }

        /// <summary>A callsign-only line has no plain clip. Does the resolved speaker's voice hold a version of it for
        /// this moment - addressing the player's seat, or naming the speaker's own?</summary>
        private bool HasCallsignVersion(Resolution res)
        {
            if (res.Line?.AudioFile == null) return false;
            string playerCallsign = BareDesignation(Player);
            string ownCallsign = OwnCallsign(res.Speaker, res.Ship);
            return _versions.Of(res.Line).Any(v =>
                v.Kind == CallsignVersion.Address ? CallsignMap.CallsignsEqual(v.Callsign, playerCallsign)
                                                  : ownCallsign != null && !CallsignMap.CallsignsEqual(ownCallsign, playerCallsign) && CallsignMap.CallsignsEqual(v.Callsign, ownCallsign));
        }

        /// <summary>Does the speaker's voice hold a self-ID version of this line for the sender's seat? A craft sharing
        /// the player's callsign never says a name, so it has none.</summary>
        private bool HasSelfIdFor(Line line, Ship sender, Speaker speaker)
        {
            if (line?.AudioFile == null || _versions.Count == 0 || sender == null) return false;
            string ownCallsign = OwnCallsign(speaker, sender);
            if (CallsignMap.CallsignsEqual(ownCallsign, BareDesignation(Player))) return false;
            return _versions.Of(line).Any(v => v.Kind == CallsignVersion.SelfId && CallsignMap.CallsignsEqual(v.Callsign, ownCallsign));
        }

        /// <summary>Replace the resolved line with a callsign version, remembering the script line it stands for.</summary>
        private void ApplyVersion(ref Resolution res, CallsignVersion version)
        {
            res.Line = new Line
            {
                Situation = res.Line.Situation,
                AudioFile = version.AudioFile,
                Text = version.Text,
                SpeakerIds = res.Line.SpeakerIds,
                FromMissionBlock = res.Line.FromMissionBlock,
                BaseLine = res.Line.BaseLine ?? res.Line,
            };
            res.Reason += $"; {version.Kind} {version.Callsign}";
        }

        /// <summary>Wait for a version's clip, then swap the version in and play it. Dropped when the clip takes too
        /// long or the speaker dies meanwhile.</summary>
        private IEnumerator SwapWhenReady(Resolution res, CallsignVersion version, string situationId)
        {
            float waited = 0f;
            while (!Loaded(version.AudioFile))
            {
                if (BuildFailed(version.AudioFile))
                {
                    _history.Remember(situationId, res.Line);   // as PlayWhenReady: move on to another line, keep the slot
                    _log($"[chatter] variant {Path.GetFileName(version.AudioFile)}: could not be built; dropped");
                    yield break;
                }
                if (waited > VersionLoadTimeoutSeconds)
                {
                    _lastSpoken.Remove(situationId);   // nothing was said: give the slot back so the next report can speak
                    _log($"[chatter] variant {Path.GetFileName(version.AudioFile)}: not ready after 5 s; dropped");
                    yield break;
                }
                waited += Time.deltaTime; yield return null;
            }
            if (SpeakerGone(res.Ship, situationId)) { _log($"[chatter] variant {Path.GetFileName(version.AudioFile)}: speaker died before it was ready; dropped"); yield break; }
            ApplyVersion(ref res, version);
            _lastSpoken.Remove(situationId);   // release the slot we reserved so Play's own MarkSpoken is the real one
            Play(res, situationId);
        }
    }
}
