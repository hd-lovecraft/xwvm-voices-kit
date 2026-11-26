using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GameMetadata;
using HarmonyLib;
using UnityEngine;
using Util;
using Xwvm.MissionClasses;
using Xwvm.MissionClasses.Entities;
using XMission = Xwvm.MissionClasses.Mission;

namespace XwvmChatter
{
    // Help for the mission's own story lines: trench-run moments the sidecar cannot trigger, a mayday that plays on the
    // explosion instead of up to 5 s later, and the officer reading a story line whose pilot is dead.
    internal static partial class GameHooks
    {
        // ------------------------------------------------------------------ voiced story messages

        /// <summary>A story message with audio (a sidecar &lt;message audio=...&gt;). Text-only messages don't count:
        /// chatter fills the gaps the voiced script leaves.</summary>
        private static bool IsVoiced(MissionMessageTrigger message) =>
            message.TextAndAudioAlternatives != null && message.TextAndAudioAlternatives.Any(alternative => !string.IsNullOrEmpty(alternative.audio));

        /// <summary>Does a voiced mission message (loaded by the game into Mission.MissionMessageTriggers) trigger on this
        /// flight group's arrival? Whatever the game would fire that message for, chatter stays quiet for. Repeat waves of
        /// a scripted group are held too.</summary>
        internal static bool ScriptVoicesArrival(XMission mission, FlightGroup flightGroup) =>
            ScriptVoices(mission, flightGroup, Common.TriggerCondition.Arrived, false);

        /// <summary>Does a voiced mission message wait on <paramref name="condition"/> for this flight group? Matching is
        /// the game's own rule, Trigger.EvaluateSingleTriggerAgainstFlightGroup (FlightGroup = missionfileIndex,
        /// GlobalGroup, CraftType, Iff...).</summary>
        /// <param name="untriggeredOnly">Only messages the game has not fired yet.</param>
        private static bool ScriptVoices(XMission mission, FlightGroup flightGroup, Common.TriggerCondition condition, bool untriggeredOnly)
        {
            try
            {
                var messages = mission?.MissionMessageTriggers;
                if (messages == null || flightGroup == null) return false;
                foreach (var message in messages)
                {
                    var triggers = message?.Trigger;
                    if (triggers == null || (untriggeredOnly && message.Triggered)) continue;
                    if (!IsVoiced(message)) continue;
                    foreach (var trigger in new[] { triggers.Trigger1, triggers.Trigger2 })
                        if (trigger != null && trigger.Condition == condition && Trigger.EvaluateSingleTriggerAgainstFlightGroup(flightGroup, trigger.VariableType, trigger.Variable))
                            return true;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[chatter] scripted-message check ({condition}): {e.Message}"); }
            return false;
        }

        // ------------------------------------------------------------------ the trench run

        // The moments of the trench run that fire story lines, and (TrenchLines) which line belongs to which.
        private const string TrenchEnter = "enter";
        private const string TrenchLeft = "left";
        private const string TrenchExhaust10 = "exhaust10";
        private const string TrenchExhaust5 = "exhaust5";

        /// <summary>Story line (by the stem of its audio file) -> the trench moment that fires it.</summary>
        private static readonly Dictionary<string, string> TrenchLines = new Dictionary<string, string>
        {
            ["stay-low-and-adjust-your-deflectors"] = TrenchEnter,
            ["blast-or-avoid-any-obstacles-and-watch-out-for-laser-batteries"] = TrenchEnter,
            ["some-may-be-angled-to-point-to-your-rear-after-you-pass"] = TrenchEnter,
            ["stay-on-target"] = TrenchExhaust10,
            ["stay-on-target-closer"] = TrenchExhaust5,
            ["youve-left-the-trench-whats-wrong"] = TrenchLeft,
        };

        private static XMission _trenchMission;          // the mission the flags below belong to
        private static bool _wasInTrench, _enterFired, _leftFired, _exhaust10Fired, _exhaust5Fired;
        private static float _nextTrenchCheck;           // mission time

        /// <summary>The trench run's story lines the sidecar cannot trigger: entering the trench, closing on the exhaust
        /// port, leaving the trench. The sidecar carries them with the game's own never-firing condition
        /// (TriggerCondition.None -> Failed|NeverPossible, Trigger.EvaluateSingleTrigger) and FireTrench sends each
        /// through the game's own message path. The moments use the game's own rules:
        ///   in the trench    = Mission.ApplyTrenchCMD's test: exhaust-port mission (MissionEndsByHittingExhaust), Death
        ///                      Star mission, player below the surface (Position.y &lt; 0) - when the game switches the CMD
        ///                      to its trench page
        ///   exhaust distance = from the port at the trench grid's origin (UpdateDeathStarGroups: 1600-unit tiles; its own
        ///                      TellPlayerToStayOnTarget fires on the tile row before it - a moment the X-Wing handler
        ///                      leaves silent), in the same units as the script's "10.00k" / "5.00k"
        /// A message is matched by its audio stem (TrenchLines); a mission without those stems does nothing.
        /// Called from the ThinkMissionTriggers postfix; checks four times a second of mission time.</summary>
        internal static void TrenchTick(XMission mission)
        {
            try
            {
                if (!ReferenceEquals(mission, _trenchMission))
                {
                    _trenchMission = mission;
                    _wasInTrench = _enterFired = _leftFired = _exhaust10Fired = _exhaust5Fired = false;
                    _nextTrenchCheck = 0f;
                }
                if (!mission.IsDeathStarMission || !mission.MissionEndsByHittingExhaust || mission.MissionTime < _nextTrenchCheck) return;
                _nextTrenchCheck = mission.MissionTime + 0.25f;
                var player = mission.PlayerShip;
                if (player == null) return;
                var position = player.Position;
                bool inTrench = position.y < 0f;                                              // Mission.ApplyTrenchCMD
                float exhaustDistance = new Vector2(position.x, position.z).magnitude;        // the port sits at the trench grid's origin
                TraceTrench(mission, position, inTrench, exhaustDistance);
                if (inTrench && !_wasInTrench && !_enterFired) { _enterFired = true; FireTrench(mission, TrenchEnter); }
                if (!inTrench && _wasInTrench && _enterFired && !_leftFired && !mission.DSExhaustPortHit) { _leftFired = true; FireTrench(mission, TrenchLeft); }
                if (inTrench && !_exhaust10Fired && exhaustDistance <= 10000f) { _exhaust10Fired = true; FireTrench(mission, TrenchExhaust10); }
                if (inTrench && !_exhaust5Fired && exhaustDistance <= 5000f) { _exhaust5Fired = true; FireTrench(mission, TrenchExhaust5); }
                _wasInTrench = inTrench;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[chatter] trench tick: {e.Message}"); _nextTrenchCheck = float.MaxValue; }
        }

        /// <summary>Fire every untriggered story line of this trench moment the way the game fires a message: Triggered
        /// plus Countdown = DisplayDelay, which Mission.CalculateMissionMessageTriggers counts down in its 5 s ticks and
        /// then registers; with no delay, RegisterMissionEvent(MissionMessageMissionEvent) now, which is what that method
        /// does for an undelayed message.</summary>
        private static void FireTrench(XMission mission, string moment)
        {
            int fired = 0;
            foreach (var message in mission.MissionMessageTriggers)
            {
                if (message == null || message.Triggered || message.TextAndAudioAlternatives == null) continue;
                var audio = message.TextAndAudioAlternatives.Select(alternative => alternative.audio).FirstOrDefault(name => !string.IsNullOrEmpty(name));
                if (audio == null) continue;
                var stem = Path.GetFileNameWithoutExtension(audio).ToLowerInvariant();
                if (!TrenchLines.TryGetValue(stem, out var lineMoment) || lineMoment != moment) continue;
                message.Triggered = true;
                if (message.DisplayDelay > 0) message.Countdown = message.DisplayDelay;       // the game counts it down and plays it
                else mission.RegisterMissionEvent(new Xwvm.MissionClasses.Events.MissionMessageMissionEvent(mission, message));
                fired++;
            }
            Plugin.Instance?.LogVerbose($"[chatter] trench: {moment} at mission {mission.MissionTime:0.0} s - {fired} story line(s) fired through the game's message path");
        }

        // ------------------------------------------------------------------ a mayday on time

        // Flight groups that just lost a craft and have a voiced, untriggered "destroyed" story line watching them.
        private static readonly List<FlightGroup> _pendingDeathGroups = new List<FlightGroup>();
        private static float _pendingDeathUntil;          // mission time
        private static XMission _pendingDeathMission;

        /// <summary>A mayday on time. The game checks story-message triggers only every 5 s (Mission.Update:
        /// nextMessageThinkTime = missionTime + 5 -> CalculateMissionMessageTriggers), so a line triggered by a death can
        /// land up to 5 s after the explosion. When a craft dies, note its flight group here; on the following frames
        /// ScriptedDeathTick asks the game whether that death completed a trigger.</summary>
        internal static void WatchForScriptedDeathLine(XMission mission, Ship victim)
        {
            try
            {
                var flightGroup = victim?.FlightGroup;
                if (mission == null || flightGroup == null || !ScriptVoices(mission, flightGroup, Common.TriggerCondition.Destroyed, true)) return;
                if (!ReferenceEquals(mission, _pendingDeathMission)) { _pendingDeathGroups.Clear(); _pendingDeathMission = mission; }   // never carry a group into the next mission
                if (!_pendingDeathGroups.Contains(flightGroup)) _pendingDeathGroups.Add(flightGroup);
                _pendingDeathUntil = mission.MissionTime + 1f;                  // the kill's counts settle within a frame or two
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[chatter] scripted death line: {e.Message}"); }
        }

        /// <summary>Per frame (ThinkMissionTriggers postfix), while a death is pending: ask the game's own static
        /// Trigger.EvaluateSingleTrigger whether a voiced, untriggered message's Destroyed trigger on that group now
        /// passes. Only then is the game's next message check brought forward to now, and the game plays the line by its
        /// own rules. Only the death that completes a trigger hurries the check: a line watching "50% of a group" would
        /// otherwise restart the game's 5 s rhythm on every death in that group. Side effect, once per such death:
        /// messages counting down a delay tick early.</summary>
        internal static void ScriptedDeathTick(XMission mission)
        {
            if (_pendingDeathGroups.Count == 0) return;
            if (!ReferenceEquals(mission, _pendingDeathMission)) { _pendingDeathGroups.Clear(); return; }
            try
            {
                foreach (var message in mission.MissionMessageTriggers)
                {
                    var triggers = message?.Trigger;
                    if (triggers == null || message.Triggered) continue;
                    if (!IsVoiced(message)) continue;
                    foreach (var trigger in new[] { triggers.Trigger1, triggers.Trigger2 })
                    {
                        if (trigger == null || trigger.Condition != Common.TriggerCondition.Destroyed) continue;
                        if (!_pendingDeathGroups.Any(group => Trigger.EvaluateSingleTriggerAgainstFlightGroup(group, trigger.VariableType, trigger.Variable))) continue;
                        var result = Trigger.EvaluateSingleTrigger(mission, trigger.Condition, trigger.VariableType, trigger.Variable, trigger.Amount, trigger.CheckWaveSpawning);
                        if ((result & Common.TriggerResult.Passed) == 0) continue;
                        _pendingDeathGroups.Clear();
                        if (mission.nextMessageThinkTime <= mission.MissionTime) return;      // the game checks this frame anyway
                        Plugin.Instance?.LogVerbose($"[chatter] a death completed a voiced story trigger ({trigger.Amount} {trigger.VariableType} {trigger.Variable} destroyed); game's message check brought forward {mission.nextMessageThinkTime - mission.MissionTime:0.0} s");
                        mission.nextMessageThinkTime = mission.MissionTime;
                        return;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[chatter] scripted death tick: {e.Message}"); _pendingDeathGroups.Clear(); return; }
            if (mission.MissionTime > _pendingDeathUntil) _pendingDeathGroups.Clear();
        }

        // ------------------------------------------------------------------ a story line whose pilot is dead

        /// <summary>A story clip named after its speaker: "red-leader-...", "red-2-...".</summary>
        private static readonly Regex SpeakerStem = new Regex(@"^([a-z]+)-(leader|\d+)-", RegexOptions.IgnoreCase);

        /// <summary>When the seat a story clip is named after has no live craft, play the alternative recorded next to
        /// it: the officer's reading ("&lt;stem&gt;--officer") or, for pilot talk only that pilot could say, a near-empty
        /// clip ("&lt;stem&gt;--silent": the text still shows). A line with neither (a death call, a line ABOUT the seat)
        /// plays as written. The game's messages have no speaker; this is our own rule, driven by which files exist.</summary>
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.QueueRadioMessageFromAssetRelativeToMission))]
        [HarmonyPrefix]
        private static void QueueRadioMessageFromAssetRelativeToMission_Prefix(ref string audioName)
        {
            try
            {
                var session = Plugin.Session;
                if (session == null || string.IsNullOrEmpty(audioName)) return;
                var directory = Path.GetDirectoryName(audioName) ?? "";
                var stem = Path.GetFileNameWithoutExtension(audioName);
                var extension = Path.GetExtension(audioName);
                var speaker = SpeakerStem.Match(stem);
                if (!speaker.Success || stem.Contains("--")) return;
                string group = speaker.Groups[1].Value;
                int seat = speaker.Groups[2].Value.ToLowerInvariant() == "leader" ? 1 : int.Parse(speaker.Groups[2].Value);
                if (session.SeatAlive(group, seat)) return;
                var mission = SceneHelper.GetParameter<MissionEntry>("ActiveMission");
                foreach (var kind in new[] { "officer", "silent" })
                {
                    var alternative = (directory.Length > 0 ? directory + "/" : "") + stem + "--" + kind + extension;
                    if (mission.LocateMissionRelatedAsset(alternative) == null) continue;   // (MissionEntry is a struct)
                    Plugin.Log.LogInfo($"[narrative] {stem}: {group} {seat} is gone; " + (kind == "officer" ? "the officer's version plays" : "text only"));
                    audioName = alternative;
                    return;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[narrative] officer fallback check failed: {e.Message}"); }
        }
    }
}
