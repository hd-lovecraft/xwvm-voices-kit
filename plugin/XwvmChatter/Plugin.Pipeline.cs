using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Xwvm.MissionClasses.Entities;

namespace XwvmChatter
{
    // The decision pipeline: every situation, whoever reports it, passes the same checks in the same order before
    // a line plays.
    public sealed partial class Plugin
    {
        /// <summary>The origin (a label for the log) of a situation the game itself reported through
        /// XwGameplaySituationHandler.ReportSituation.</summary>
        internal const string OriginGameSituation = "situation";

        /// <summary>Situations that still play right after a story line: danger to the player, replies to the player's
        /// own orders (an order given must be answered) and death rattles (they wait in the outbox for the story line to
        /// finish, then play).</summary>
        private static readonly HashSet<string> NarrativeExempt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Situations.UnderAttack, Situations.IncomingMissile,
            Situations.AttackAck, Situations.HelpAck, Situations.IgnoreTarget, Situations.Evade, Situations.GoingHome,
            Situations.AwaitingOrders, Situations.Proceeding,
            Situations.WingmanDestroyed,
        };

        /// <summary>A situation with an implicit speaker (the game says who is talking), or none (a warning to the
        /// player: we choose the speaker).</summary>
        /// <returns>True when the plugin has dealt with it and the game's stock line must stay quiet - whether we
        /// spoke or chose to say nothing. False leaves it to the game.</returns>
        internal bool TryHandle(string situationId, Ship sender, string origin) =>
            Handle(situationId, sender, null, null, null, null, false, origin);

        /// <summary>As above, for "under attack": <paramref name="attacker"/> decides which lines fit (a TIE Interceptor,
        /// the same ship again, several at once).</summary>
        internal bool TryHandle(string situationId, Ship sender, Ship attacker, string origin) =>
            Handle(situationId, sender, attacker, null, null, null, false, origin);

        /// <summary>A situation the plugin raises itself, described by tags instead of an attacking ship: an arrival
        /// (craft classes, new/more, hyper/launched) or "here they come".</summary>
        internal bool TryHandleTagged(string situationId, List<string> classes, string threat, string arrival, string origin) =>
            Handle(situationId, null, null, classes, threat, arrival, true, origin);

        /// <summary>
        /// The checks, in order. Each one that stops the line says why in the log.
        ///   1. bookkeeping that must happen even if nothing is said (one death rattle per ship; the mission is decided)
        ///   2. not awake yet                      -> the game's line
        ///   3. the mission's story script says it -> silence
        ///   4. mission decided                    -> silence (the closing story lines play alone)
        ///   5. radio busy and the call is colour  -> silence
        ///   6. the speaker is not ours to voice   -> silence (other flight group, a silent story seat)
        ///   7. a story line just played           -> silence, except warnings, order replies and death rattles
        ///   8. this situation spoke within 10 s   -> silence
        ///   9. no line or no live speaker         -> the game's line
        ///  10. swap in the callsign version, wait a beat if the situation has a delay, play
        /// </summary>
        private bool Handle(string situationId, Ship sender, Ship attacker, List<string> classes, string threat, string arrival, bool tagged, string origin)
        {
            var session = Session;
            if (!Enabled || session == null || situationId == null) return false;
            string senderNote = tagged ? "" : $" sender={ChatterSession.SafeDesignation(sender)}";   // for the log

            // 1. bookkeeping
            if (situationId == Situations.WingmanDestroyed && sender != null && !session.ClaimDeathRattle(sender))
            { LogVerbose($"[chatter] {origin} WingmanDestroyed: {ChatterSession.SafeDesignation(sender)} already has its death rattle"); return true; }
            bool decidesMission = situationId == Situations.PrimaryComplete || situationId == Situations.PrimaryFailed;
            if (decidesMission)
            {
                if (!session.MissionDecided) Log.LogInfo($"[chatter] {situationId}: mission decided at {session.MissionTime:0.0} s - end-state hold, objective calls off");
                session.NoteEndState();
            }

            // 2. not awake
            if (!session.IsAwake) { LogVerbose($"[chatter] {origin} {situationId}: before the chatter wakes up ({session.MissionTime:0.0} s); left to the game"); return false; }

            // 3. the story script voices this moment (<chatter silence="...">)
            if (session.Config.Silenced.Contains(situationId)) { Log.LogInfo($"[chatter] {origin} {situationId}: scripted in this mission's narrative VO; silenced"); return true; }

            // 4. End state: the closing narrative owns the radio, no exemptions; objective calls are moot once decided
            // ("cover the objective" after "return to base").
            if (session.MissionDecided && situationId.StartsWith("MCC", StringComparison.Ordinal))
            { LogVerbose($"[chatter] {origin} {situationId}: mission decided; objective call dropped"); return true; }
            if (session.InEndStateHold() && !decidesMission)
            { LogVerbose($"[chatter] {origin} {situationId}: end-state hold"); return true; }

            // 5. radio spacing
            if (session.IsLowPriority(situationId, sender) && session.IsRadioBusy())
            { LogVerbose($"[chatter] {origin} {situationId}{senderNote}: radio busy (free in {session.RadioFreeIn:0.0} s); low priority, dropped"); return true; }

            // One reaction per player kill. Our kill hook (GameHooks.OnPlayerKill, which runs before the game's
            // congratulation) decides every player kill once: thanks if a wingman was saved, else the
            // PlayerKillNiceShotChance roll. The game's own congratulation (Mission: CraftCongratulatesPlayerForKill,
            // kills from over 150 m) is folded into that roll, so it is silenced here, stock line included. A deliberate
            // deviation from the game, which congratulates every long kill (it felt like every single kill).
            if (situationId == Situations.NiceShot && origin == OriginGameSituation)
            { LogVerbose($"[chatter] {origin} NiceShot: the game's congratulation is folded into the kill's single roll; silenced"); return true; }

            // 6. Own-flight-only situations (hyperspace departures, kill reports...): a wingman of ANOTHER flight group
            // says nothing. Same craft kind and side only; anything else (transports, hostiles) keeps its normal path.
            var ownFlightOnly = OwnFlightOnly;
            if (sender != null && (ownFlightOnly.Contains("*") || ownFlightOnly.Contains(situationId)) && session.IsWingman(sender) && !session.SameFlightGroupAsPlayer(sender))
            {
                LogVerbose($"[chatter] {origin} {situationId} sender={ChatterSession.SafeDesignation(sender)}: other flight group, silenced");
                return true;
            }
            // a story pilot whose voice has no chatter lines yet says nothing - neither chatter nor the stock voice
            if (sender != null && session.IsSilentSeat(sender)) { LogVerbose($"[chatter] {origin} {situationId} sender={ChatterSession.SafeDesignation(sender)}: silent story seat"); return true; }

            // 7. Narrative priority: scripted mission VO owns the radio for a beat.
            if (NarrativeHoldSeconds > 0f && !NarrativeExempt.Contains(situationId) && session.InNarrativeHold(NarrativeHoldSeconds))
            { LogVerbose($"[chatter] {origin} {situationId}: narrative hold"); return true; }

            // A wingman's 2nd..5th fighter kill may be reported as a count boast instead ("That's three for me.").
            bool boast = false;
            if (situationId == Situations.KillReport && sender != null && session.IsWingman(sender))   // named ships never boast
            {
                var boastOrReport = session.BoastOrReport(sender, KillBoastChance);
                if (boastOrReport != situationId) { situationId = boastOrReport; boast = true; LogVerbose($"[chatter] {origin}: {ChatterSession.SafeDesignation(sender)} boasts ({boastOrReport})"); }
            }

            // Who is shooting, and have we warned about them already? Fed by every event, spoken or not.
            if (attacker != null) { threat = session.NoteAttack(attacker); classes = ChatterSession.AttackerClasses(attacker); }

            // 8. Throttled situations stop here, BEFORE a line is looked for: the game raises "under attack" about four
            // times a second through a bomber run, and looking for a line each time cost most of that flight's long
            // frames. A boast keeps its own check below.
            if (!boast && session.IsThrottled(situationId)) { LogVerbose($"[chatter] {origin} {situationId}: throttled"); return true; }

            // 9. the line and its speaker
            OnPickStarting();
            bool resolved = session.TryResolve(situationId, sender, classes, threat, arrival, out var resolution);
            OnPickFinished(situationId, resolution.Reason);
            if (!resolved)
            {
                // a boast that cannot resolve becomes the plain kill report, never a fall-through to the stock voice
                if (boast && session.TryResolve(Situations.KillReport, sender, classes, threat, arrival, out resolution)) { situationId = Situations.KillReport; boast = false; }
                else
                {
                    LogVerbose($"[chatter] {origin} {situationId}{senderNote}: fall through ({resolution.Reason})");
                    return false;
                }
            }
            // The 10 s window also collapses a whole-wing order into one acknowledgement: "attack my target" to the
            // flight raises an ack per ship; one of them answers and the rest are throttled.
            if (session.IsThrottled(situationId)) { LogVerbose($"[chatter] {origin} {situationId}: throttled"); return true; }

            // 10. callsign version, delay, play
            session.TrySwapInCallsignVersion(ref resolution, out var deferred);
            if (deferred) return true;   // the callsign version plays when its clip has loaded
            if (resolution.Line.CallsignOnly) { LogVerbose($"[chatter] {origin} {situationId}: callsign-only line without a version for this seat; skipped"); return true; }   // never the stock voice
            var delay = DelayFor(situationId);
            if (boast) session.MarkSpoken(Situations.KillReport);
            if (delay.HasValue && delay.Value.max > 0f)
            {
                // Take the throttle slot NOW (so a burst of kills doesn't queue a burst of reports), speak after a beat.
                session.MarkSpoken(situationId);
                var wait = UnityEngine.Random.Range(delay.Value.min, delay.Value.max);
                LogVerbose($"[chatter] {origin} {situationId}: in {wait:0.0}s");
                // the tease is timed from the end of the delay window, whenever in it the boast starts
                StartCoroutine(PlayLater(session, resolution, situationId, wait, boast ? sender : null, delay.Value.max - wait));
                if (attacker != null) session.MarkWarned(attacker);
                return true;
            }
            var spoke = session.Play(resolution, situationId);
            if (spoke && boast) StartCoroutine(BoastTeaseAfter(session, resolution.Line.AudioFile, sender, 0f));
            if (spoke && attacker != null) session.MarkWarned(attacker);
            return spoke;
        }

        /// <param name="boaster">Set when the line is a kill boast: the tease follows only if the boast really played
        /// (a tease after a boast that was dropped would answer a line nobody heard).</param>
        private IEnumerator PlayLater(ChatterSession session, ChatterSession.Resolution resolution, string situationId, float wait,
                                      Ship boaster = null, float teaseDelay = 0f)
        {
            yield return new WaitForSeconds(wait);
            while (ReferenceEquals(Session, session) && session.IsMissionFrozen()) yield return null;   // a jump began meanwhile
            if (!ReferenceEquals(Session, session)) yield break;                                      // mission ended meanwhile
            if (ChatterSession.SpeakerGone(resolution.Ship, situationId)) { LogVerbose($"[chatter] {situationId}: speaker {ChatterSession.SafeDesignation(resolution.Ship)} died before speaking; dropped"); yield break; }
            bool spoke = session.Play(resolution, situationId);
            if (spoke && boaster != null) StartCoroutine(BoastTeaseAfter(session, resolution.Line.AudioFile, boaster, Math.Max(0f, teaseDelay)));
        }
    }
}
