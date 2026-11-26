using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using Xwvm.Ui.LoadingScreen;
using XMission = Xwvm.MissionClasses.Mission;

namespace XwvmChatter
{
    /// <summary>
    /// The game's own loading screen (LoadingScreenManager.LoadScene) preloads a mission's assets in blocking steps before
    /// the mission scene activates - ships, droids, icons, flight music (PreloadMissionDigitalMusic), pyrotechnics - and the
    /// screen stays up until each finishes. We add our clips as one more step of that same chain: a postfix on
    /// PreloadMissionDigitalMusic returns an enumerator that runs the game's music step and then ours, so the loading
    /// screen waits for both. The step gets the game's temporary Mission (built by Mission.FromMissionEntry with the
    /// sidecars visited), which names the flight groups and the player's seat before any ship exists.
    ///
    /// Never load in rendered gameplay frames: doing this during the opening jump turned it into a slideshow.
    ///
    /// What is loaded: every clip the mission can play (PlanClips). With Radio.DirectDecode on, only the files are read
    /// here and each clip is built when its line is first picked; off, every clip is built here by the game's loader.
    /// </summary>
    internal static class LoadScreenPreload
    {
        /// <summary>Radio.LoadsInFlight. Every load in flight finishes in the same loading-screen frame, so the number of
        /// clips per frame, not the size of a clip, sets how long the game's loader takes.</summary>
        private static int MaxLoadsInFlight => Math.Max(1, Plugin.Instance?.LoadsInFlight ?? 48);

        [HarmonyPatch(typeof(LoadingScreenManager), "PreloadMissionDigitalMusic")]
        [HarmonyPostfix]
        private static void PreloadMissionDigitalMusic_Postfix(LoadingScreenManager __instance, ref IEnumerator __result, XMission mission)
        {
            try
            {
                var plugin = Plugin.Instance;
                if (plugin == null || !plugin.Enabled || !plugin.PreloadOnLoadingScreen) return;
                __result = GameStepThenChatterClips(__result, mission, __instance);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[chatter] loading-screen preload not hooked: {e.Message}"); }
        }

        private static IEnumerator GameStepThenChatterClips(IEnumerator gameStep, XMission mission, LoadingScreenManager screen)
        {
            yield return gameStep;                       // the game's own flight-music preload, unchanged
            List<string> plan = null; string missionName = null;
            try { plan = PlanClips(mission, out missionName); }
            catch (Exception e) { Plugin.Log.LogWarning($"[chatter] loading-screen preload: plan failed ({e.Message}); lines load on demand"); }
            if (plan == null || plan.Count == 0) yield break;

            ClipCache.Discard(); ClipCache.Bytes.Clear();
            ClipCache.Mission = missionName;
            float start = Time.realtimeSinceStartup;
            int frames = 0;
            LoadStats.Reset(); OggDirect.CreateProbe.Reset(); OggDirect.ResetWorkerStats();

            // Progress on the game's own loading-screen text box (LoadingScreenManager.continueBox, the box its
            // ShowPressToContinue uses once loading is done); hidden again before the game's next step.
            float nextProgressAt = 0f;
            bool boxShown = false;
            bool progressFailed = false;   // stop updating after an error, but keep the screen so the box can still be hidden
            void ShowProgress(int done)
            {
                if (progressFailed) return;
                try
                {
                    var box = screen != null ? screen.continueBox : null;
                    if (box == null) return;
                    // AnimatedBox.SetText also starts AnimatedTipText.CycleTextTo, an endless fade loop that re-writes the
                    // string it was given every cycle, so calling it per update stacks loops that keep putting old
                    // percentages back. The tip text's immediate path (AnimatedTipText.SetText(text, immediate: true))
                    // just sets the text; the box is opened once.
                    string text = $"Loading radio chatter... {done:N0} / {plan.Count:N0} ({100 * done / Math.Max(1, plan.Count)}%)";
                    if (box.tipText != null) box.tipText.SetText(text, true); else box.SetText(text);
                    if (!boxShown) { box.ShowBox(true); boxShown = true; }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[chatter] loading-screen progress: {e.Message}"); progressFailed = true; }
            }

            if (Plugin.Instance?.DirectDecode ?? true)
            {
                // Files only (see ClipCache.Bytes): the reads run on worker threads, the main thread only shows progress.
                long bytesRead = 0;
                int readFailures = 0, readers = Math.Max(1, Math.Min(8, Environment.ProcessorCount));
                var unread = new ConcurrentQueue<string>(plan);
                var reads = new List<Task>();
                for (int reader = 0; reader < readers; reader++)
                    reads.Add(Task.Run(() =>
                    {
                        while (unread.TryDequeue(out var path))
                        {
                            try { var bytes = File.ReadAllBytes(path); ClipCache.Bytes[path] = bytes; Interlocked.Add(ref bytesRead, bytes.Length); }
                            catch { Interlocked.Increment(ref readFailures); }
                        }
                    }));
                var allRead = Task.WhenAll(reads);
                while (!allRead.IsCompleted)
                {
                    if (Time.realtimeSinceStartup >= nextProgressAt) { ShowProgress(ClipCache.Bytes.Count + readFailures); nextProgressAt = Time.realtimeSinceStartup + 0.25f; }
                    frames++;
                    yield return null;
                }
                if (boxShown) { try { screen.continueBox.ShowBox(false); } catch { } }
                Plugin.Log.LogInfo($"[chatter] loading screen: read {ClipCache.Bytes.Count} clip file(s) ({bytesRead / 1048576.0:0} MB) for {missionName} in " +
                                   $"{Time.realtimeSinceStartup - start:0.0} s over {frames} frame(s) ({readFailures} failed); clips are built when first picked (Radio.DirectDecode)");
                yield break;
            }

            // Radio.DirectDecode off: build every clip here with the game's loader, MaxLoadsInFlight at a time.
            int inFlight = 0, next = 0, failed = 0, mostFinishedInOneFrame = 0, finishedBefore = 0;
            Action<string> log = message => Plugin.Log.LogInfo(message);
            ShowProgress(ClipCache.Clips.Count + failed);
            while (next < plan.Count || inFlight > 0)
            {
                if (Time.realtimeSinceStartup >= nextProgressAt) { ShowProgress(ClipCache.Clips.Count + failed); nextProgressAt = Time.realtimeSinceStartup + 0.5f; }
                frames++;
                int finished = ClipCache.Clips.Count + failed;
                if (finished - finishedBefore > mostFinishedInOneFrame) mostFinishedInOneFrame = finished - finishedBefore;
                finishedBefore = finished;
                while (inFlight < MaxLoadsInFlight && next < plan.Count)
                {
                    var path = plan[next++];
                    inFlight++;
                    Plugin.Instance.StartCoroutine(ClipCache.LoadClip(path, clip =>
                    {
                        inFlight--;
                        if (clip == null) { failed++; return; }
                        clip.name = "chatter:" + Path.GetFileNameWithoutExtension(path);
                        ClipCache.Clips[path] = clip;
                    }, log));
                }
                yield return null;
            }
            try
            {
                var box = screen != null ? screen.continueBox : null;
                if (box != null) box.ShowBox(false);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[chatter] loading-screen progress: {e.Message}"); }
            float elapsed = Time.realtimeSinceStartup - start;
            Plugin.Log.LogInfo($"[load] {frames} frame(s), {1000f * elapsed / Math.Max(1, frames):0.0} ms/frame, up to {mostFinishedInOneFrame} clip(s) finished in one frame, " +
                               $"{MaxLoadsInFlight} in flight, direct={Plugin.Instance?.DirectDecode ?? true}, stream={Plugin.Instance?.StreamClips ?? false}, compressed={Plugin.Instance?.CompressedClips ?? true}; {LoadStats.Summary()}{OggDirect.CreateProbe.Summary()}{OggDirect.WorkerSummary()}");
            Plugin.Log.LogInfo($"[chatter] loading screen: preloaded {ClipCache.Clips.Count} clip(s) for {missionName} in " +
                               $"{Time.realtimeSinceStartup - start:0.0} s ({failed} failed); the loading screen waited for this");
        }

        /// <summary>Every clip the mission CAN play, and nothing it can't, in the order the first seconds of a mission are
        /// likely to need them. The flight groups name every friendly wingman seat before a ship exists
        /// (ChatterSession.FriendlySeats) and the session deals the generic pool from that same list (PoolDeal.Deal), so
        /// the voices here are exactly the voices that will speak: cast seats present, the dealt pool voices, the
        /// officer, the astromech, named friendly ships. Callsign versions only for the player's seat, the seats present
        /// and the named ships. A deliberate deviation from the game, which loads its radio clips at play time.</summary>
        /// <returns>Clip file paths, or null when there is nothing to preload.</returns>
        private static List<string> PlanClips(XMission mission, out string missionName)
        {
            missionName = Plugin.CurrentMissionName();
            if (missionName == null || mission == null) return null;
            var config = ChatterConfig.Load(Plugin.Instance.SidecarDir, missionName, _ => { });
            if (config.IsEmpty) return null;
            var playerFlightGroup = mission.PlayerFlightGroup;
            if (playerFlightGroup == null) { Plugin.Log.LogInfo($"[chatter] loading screen: {missionName} has no player flight group; nothing preloaded"); return null; }
            string playerFlightName = (playerFlightGroup.Designation ?? "").Trim();
            string playerCallsign = playerFlightGroup.waveSize > 1 ? $"{playerFlightName} {playerFlightGroup.playerPositionInFg}" : playerFlightName;
            if (PreloadRules.FlightWord(playerFlightName) == "") return null;

            var seats = ChatterSession.FriendlySeats(mission, out var playerSeat);
            if (!string.IsNullOrEmpty(playerSeat)) playerCallsign = playerSeat;
            var lines = config.Lines.Where(line => line.AudioFile != null && !line.CallsignOnly).ToList();   // a callsign-only line has no plain clip
            var manifests = new Dictionary<string, VoiceManifest>(StringComparer.OrdinalIgnoreCase);        // voice folder -> its callsign versions
            foreach (var voiceDir in lines.Select(line => Path.GetDirectoryName(line.AudioFile)).Where(dir => !string.IsNullOrEmpty(dir))
                                          .Distinct(StringComparer.OrdinalIgnoreCase))
                manifests[voiceDir] = CallsignMap.LoadManifest(voiceDir, _ => { });

            // the voices that will speak
            var voices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var speaker in config.Speakers.Values)
            {
                if (string.IsNullOrEmpty(speaker.Voice)) continue;
                if (speaker.IsCommander || speaker.IsAstromech) voices.Add(ChatterConfig.Slug(speaker.Voice));
                else if (!speaker.IsGeneric && !string.IsNullOrEmpty(speaker.Designation) && seats.Any(seat => CallsignMap.CallsignsEqual(seat, speaker.Designation)))
                    voices.Add(ChatterConfig.Slug(speaker.Voice));
            }
            var uncastSeats = PoolDeal.UncastRoster(seats, config);
            var pool = PoolDeal.PoolOf(config);
            if (uncastSeats.Count > 0 && pool.Count > 0)
                foreach (var dealt in PoolDeal.Deal(pool, uncastSeats, config.MissionName, (poolVoice, callsign) => PoolDeal.CoversSelfId(manifests, poolVoice, callsign), out _, out _).Values)
                    voices.Add(ChatterConfig.Slug(dealt.Voice));

            // the callsigns a line may name
            var namedCallsigns = new List<string>(seats);
            if (!string.IsNullOrEmpty(playerCallsign)) namedCallsigns.Add(playerCallsign);
            // Named friendly ships (role="ship"): their voices and the name they identify themselves by. A ship may not be
            // present the whole mission, but its lines are few.
            foreach (var ship in config.Speakers.Values.Where(speaker => speaker.IsShip && !string.IsNullOrEmpty(speaker.Voice)))
            {
                voices.Add(ChatterConfig.Slug(ship.Voice));
                if (!string.IsNullOrEmpty(ship.Callsign)) namedCallsigns.Add(ship.Callsign);
            }

            var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plan = new List<string>();
            foreach (var line in lines.Where(spoken => voices.Contains(PreloadRules.VoiceDirOf(spoken.AudioFile))).OrderBy(spoken => PreloadRules.SituationRank(spoken.Situation)))
                if (planned.Add(line.AudioFile)) plan.Add(line.AudioFile);
            int lineCount = plan.Count, versionCount = 0;
            foreach (var manifest in manifests)
            {
                if (!voices.Contains(Path.GetFileName(manifest.Key))) continue;
                foreach (var version in manifest.Value.Versions)
                    if (namedCallsigns.Any(callsign => CallsignMap.CallsignsEqual(version.Callsign, callsign)) && planned.Add(version.AudioFile)) { plan.Add(version.AudioFile); versionCount++; }
            }

            Plugin.Log.LogInfo($"[chatter] loading screen: {missionName}, player {playerCallsign}; {seats.Count} wingman seat(s) [{string.Join(", ", seats)}]; " +
                               $"preloading {plan.Count} clip(s) - {lineCount} line(s) + {versionCount} callsign variant(s) for {string.Join(", ", voices)}");
            return plan;
        }
    }
}
