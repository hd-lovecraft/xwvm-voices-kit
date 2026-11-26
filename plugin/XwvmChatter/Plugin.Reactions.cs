using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Xwvm.MissionClasses.Entities;

namespace XwvmChatter
{
    // Reactions the game has no call for: "here they come", laments and replies, kill-boast teasing, the last-of-the-flight call.
    public sealed partial class Plugin
    {
        /// <summary>The last-of-the-flight call waits this long for a story line to finish, then is dropped.</summary>
        private const float FlightLostMaxWaitSeconds = 30f;

        // The "here they come" watch, per mission (ResetEngagementWatch).
        private int _engagementCalls;
        private readonly HashSet<object> _engagedGroups = new HashSet<object>();   // hostile groups already considered
        private float _nextEngagementScan;                                          // mission time

        private void ResetEngagementWatch()
        {
            _engagementCalls = 0;
            _engagedGroups.Clear();
            _nextEngagementScan = 0f;
        }

        /// <summary>"Here they come!": once a second, look for a hostile fighter group that has taken members of the
        /// player's flight as targets (the hostiles' own Ship.Target, mirrored in our ThreateningMe sets), at least
        /// EngagementMinCraft of them, the nearest inside EngagementRange and closing - while the flight has been quiet
        /// for EngagementQuietSeconds (nobody of ours fired or was fired at). Once per hostile group,
        /// EngagementMaxPerMission per mission.
        ///
        /// It must run inside the game's own mission step, so it is called from a postfix on
        /// Mission.ThinkMissionTriggers: the game's per-frame mission-condition pass, called by Mission.Update right
        /// after the AI's Think() (which reads the same ThreateningMe / positions) and after the parallel
        /// turret/rotation jobs have completed, before UpdateSimulation. Reading ship data (Simulation.GetEntityData,
        /// a NativeArray) from a free-running coroutine outside that step corrupted the native heap and crashed the
        /// game. Paced by mission time, so it also stops while the mission clock does.</summary>
        internal void EngagementTick(ChatterSession session)
        {
            if (!ReferenceEquals(Session, session) || !Enabled || !session.IsAwake || _engagementCalls >= EngagementMaxPerMission) return;
            if (session.MissionTime < _nextEngagementScan) return;
            _nextEngagementScan = session.MissionTime + 1f;
            object group = null; string groupName = "?"; float distance = 0f;
            try { group = session.ApproachingHostileGroup(EngagementRange, EngagementMinCraft, out groupName, out distance); }
            catch (Exception e) { LogVerbose($"[chatter] engagement watch: {e.Message}"); }
            if (group == null || _engagedGroups.Contains(group)) return;
            _engagedGroups.Add(group);
            if (session.FlightEngagedRecently(EngagementQuietSeconds, group)) { LogVerbose($"[chatter] engage:{groupName}@{distance:0}m: flight busy with another fight; no call"); return; }
            if (TryHandleTagged(Situations.Engagement, null, null, null, $"engage:{groupName}@{distance:0}m"))
                _engagementCalls++;
        }

        /// <summary>A flightmate died: after WingmanLostDelay a surviving flightmate laments them by name; by chance a
        /// lower-numbered live flightmate (the game's shipNumberInWave - Red 2 may steady Red 4) answers the lamenter by
        /// name once the lament has finished. Wingmen only, never the officer.</summary>
        internal void OnFlightmateLost(Ship victim)
        {
            var session = Session;
            if (!Enabled || session == null || !session.IsAwake) return;
            // the last of the flight: the officer takes over, once, after the death call has played
            if (!session.FlightLostSaid && session.FlightGone(victim) && !session.Config.Silenced.Contains(Situations.FlightLost))
            {
                session.FlightLostSaid = true;
                StartCoroutine(FlightLostLater(session, victim, UnityEngine.Random.Range(4.5f, 6.5f)));
            }
            if (session.Config.Silenced.Contains(Situations.WingmanLost)) return;
            if (WingmanLostChance <= 0f || UnityEngine.Random.value >= WingmanLostChance) { LogVerbose($"[chatter] lost {ChatterSession.SafeDesignation(victim)}: no lament (roll)"); return; }
            if (session.IsThrottled(Situations.WingmanLost)) { LogVerbose($"[chatter] lost {ChatterSession.SafeDesignation(victim)}: throttled"); return; }
            session.MarkSpoken(Situations.WingmanLost);
            var delay = WingmanLostDelay;
            StartCoroutine(LamentLater(session, victim, UnityEngine.Random.Range(delay.min, delay.max)));
        }

        /// <summary>The officer's call to the player once the last flightmate is gone.</summary>
        private IEnumerator FlightLostLater(ChatterSession session, Ship victim, float wait)
        {
            yield return new WaitForSeconds(wait);
            while (ReferenceEquals(Session, session) && session.IsMissionFrozen()) yield return null;
            if (!ReferenceEquals(Session, session)) yield break;
            // The story keeps the floor, but this call waits its turn rather than being lost: the death that empties the
            // flight can also be what triggers a story line.
            float waited = 0f;
            while (ReferenceEquals(Session, session) && NarrativeHoldSeconds > 0f && session.InNarrativeHold(NarrativeHoldSeconds) && waited < FlightLostMaxWaitSeconds)
            { waited += Time.deltaTime; yield return null; }
            if (!ReferenceEquals(Session, session)) yield break;
            if (waited >= FlightLostMaxWaitSeconds) { LogVerbose("[chatter] flight lost: story still talking after 30 s; dropped"); yield break; }
            if (waited > 0f) LogVerbose($"[chatter] flight lost: waited {waited:0.0} s for the story line");
            if (!session.TryResolveOfficerAddress(Situations.FlightLost, out var resolution)) { LogVerbose($"[chatter] flight lost: no line ({resolution.Reason})"); yield break; }
            session.TrySwapInCallsignVersion(ref resolution, out var deferred);
            // the skip is checked first, so "the officer takes over" is only logged when the officer has a line to say
            if (!deferred && resolution.Line.CallsignOnly) { LogVerbose("[chatter] flight lost: no version for the player's seat; skipped"); yield break; }
            Log.LogInfo($"[chatter] flight lost: {ChatterSession.SafeDesignation(victim)} was the last of the player's flight; the officer takes over");
            if (deferred) yield break;   // the callsign version plays when its clip has loaded
            session.Play(resolution, Situations.FlightLost);
        }

        private IEnumerator LamentLater(ChatterSession session, Ship victim, float wait)
        {
            yield return new WaitForSeconds(wait);
            while (ReferenceEquals(Session, session) && session.IsMissionFrozen()) yield return null;
            if (!ReferenceEquals(Session, session)) yield break;
            if (NarrativeHoldSeconds > 0f && session.InNarrativeHold(NarrativeHoldSeconds)) { LogVerbose($"[chatter] lost {ChatterSession.SafeDesignation(victim)}: narrative hold"); yield break; }
            string lostCallsign = ChatterSession.BareDesignation(victim);
            // dialogue pacing: the quiet line after a lull on the radio, the intense one in the middle of a fight
            float sinceWingLine = session.SecondsSinceWingLine;
            string register = sinceWingLine >= WingmanLostQuietSeconds ? "quiet" : "combat";
            if (!session.TryResolveFlightmate(Situations.WingmanLost, victim, null, out var resolution, register))
            { LogVerbose($"[chatter] lost {lostCallsign}: no lament ({resolution.Reason})"); yield break; }
            var lamenter = resolution.Ship;
            bool deferred = session.TrySwapInVersionNaming(ref resolution, "lost", lostCallsign, Situations.WingmanLost, out var pendingClip);
            Log.LogInfo($"[chatter] lost {lostCallsign}: {ChatterSession.SafeDesignation(lamenter)} laments ({resolution.Reason}; last wingman line {(sinceWingLine > 9000f ? "never" : sinceWingLine.ToString("0.0") + " s ago")})");
            if (deferred) { StartCoroutine(LamentReplyAfter(session, pendingClip, lamenter, register)); yield break; }
            if (session.Play(resolution, Situations.WingmanLost)) StartCoroutine(LamentReplyAfter(session, resolution.Line.AudioFile, lamenter, register));
        }

        /// <summary>After a kill boast has played out, another live flightmate teases the boaster (plain line, by chance).</summary>
        private IEnumerator BoastTeaseAfter(ChatterSession session, string boastClip, Ship boaster, float boastDelay)
        {
            if (KillBoastReplyChance <= 0f || UnityEngine.Random.value >= KillBoastReplyChance) yield break;
            float boastSeconds = session.ClipLength(boastClip);
            yield return new WaitForSeconds(boastDelay + (boastSeconds > 0f ? boastSeconds : 2.0f) + UnityEngine.Random.Range(0.5f, 1.0f));
            if (!ReferenceEquals(Session, session) || session.IsMissionFrozen()) yield break;
            if (NarrativeHoldSeconds > 0f && session.InNarrativeHold(NarrativeHoldSeconds)) yield break;
            if (!session.TryResolveFlightmate(Situations.KillBoastReply, boaster, null, out var resolution)) { LogVerbose($"[chatter] tease: none ({resolution.Reason})"); yield break; }
            session.MarkSpoken(Situations.KillBoastReply);
            // the tease names the boaster (callsign kind "mate"); plain when no version exists for that seat
            if (session.TrySwapInVersionNaming(ref resolution, "mate", ChatterSession.BareDesignation(boaster), Situations.KillBoastReply, out _)) yield break;
            Log.LogInfo($"[chatter] tease: {ChatterSession.SafeDesignation(resolution.Ship)} needles {ChatterSession.SafeDesignation(boaster)} ({resolution.Reason})");
            session.Play(resolution, Situations.KillBoastReply);
        }

        /// <summary>Once the lament clip has played out, a superior answers the lamenter by name (by chance).</summary>
        private IEnumerator LamentReplyAfter(ChatterSession session, string lamentClip, Ship lamenter, string register)
        {
            if (WingmanLostReplyChance <= 0f || UnityEngine.Random.value >= WingmanLostReplyChance) yield break;
            float lamentSeconds = session.ClipLength(lamentClip);
            yield return new WaitForSeconds((lamentSeconds > 0f ? lamentSeconds : 2.5f) + UnityEngine.Random.Range(0.6f, 1.2f));
            if (!ReferenceEquals(Session, session) || lamenter == null || !ChatterSession.IsOnRadio(lamenter)) yield break;
            if (session.Config.Silenced.Contains(Situations.WingmanLostReply)) yield break;
            string lamenterCallsign = ChatterSession.BareDesignation(lamenter);
            if (!session.TryResolveFlightmate(Situations.WingmanLostReply, null, lamenter, out var resolution, register))
            { LogVerbose($"[chatter] reply to {lamenterCallsign}: none ({resolution.Reason})"); yield break; }
            bool deferred = session.TrySwapInVersionNaming(ref resolution, "reply", lamenterCallsign, Situations.WingmanLostReply, out _);
            if (deferred) yield break;
            Log.LogInfo($"[chatter] reply to {lamenterCallsign}: {ChatterSession.SafeDesignation(resolution.Ship)} ({resolution.Reason})");
            session.MarkSpoken(Situations.WingmanLostReply);
            session.Play(resolution, Situations.WingmanLostReply);
        }
    }
}
