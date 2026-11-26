using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Common;
using UnityEngine;
using Xwvm.MissionClasses.Entities;

namespace XwvmChatter
{
    // The radio as a shared resource: story messages come first, our lines wait their turn in the outbox,
    // low-priority calls are dropped while it is busy, and each situation speaks at most once per window.
    public sealed partial class ChatterSession
    {
        // ------------------------------------------------------------------ story messages come first

        private float _lastNarrative = LongAgo;   // mission time of the last scripted mission message

        public void NoteNarrativeMessage() { _lastNarrative = Mission.MissionTime; }

        /// <summary>True while a scripted mission message (narrative VO) recently played - chatter yields to it.</summary>
        public bool InNarrativeHold(float windowSeconds) => Mission.MissionTime - _lastNarrative < windowSeconds;

        // ------------------------------------------------------------------ end state
        // Once the game reports the primary goals complete or failed, the mission's closing narrative (sidecar goal
        // messages, delays in 5 s steps) owns the radio. Until it has played out, NOTHING of ours speaks - not even
        // the danger warnings the normal narrative hold lets through - and the objective calls (MCC*) stay off for
        // the rest of the mission (no "cover the objective" after "return to base").

        private float _endStateAt = -1f;            // mission time the mission was decided; negative until then
        public float EndStateHoldSeconds = 12f;     // minimum quiet after the decision (first goal message comes 5 s later)
        public float EndStateNarrativeWindow = 8f;  // and until this long after the last scripted message

        public bool MissionDecided => _endStateAt >= 0f;
        public void NoteEndState() { if (_endStateAt < 0f) _endStateAt = Mission.MissionTime; }
        public bool InEndStateHold() => _endStateAt >= 0f
            && (Mission.MissionTime - _endStateAt < EndStateHoldSeconds || InNarrativeHold(EndStateNarrativeWindow));

        // ------------------------------------------------------------------ radio spacing
        // The game spaces only repeats of the same clip (10 s, XwGameplaySituationHandler._radioSpamFilter); nothing
        // spaces DIFFERENT calls, so a help call, a kill report, a warning and an arrival could stack back to back on
        // the queue. OUR rule: low-priority calls are dropped while the radio is talking or within RadioGapSeconds of
        // it going quiet. Busy time is the sum of clips actually queued on the cockpit radio
        // (CockpitInstruments.PlayCustomRadioMessage - narrative and chatter alike).

        public float RadioGapSeconds = 2f;
        private float _radioBusyUntil = -1f;   // mission time the game's radio queue runs out

        private static readonly HashSet<string> LowPrioritySituations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Situations.EnemyArrival, Situations.KillReport, Situations.KillBoastReply, Situations.TorpedoHit, Situations.Engagement, Situations.NiceShot,
        };

        public void NoteRadioClip(float length)
        {
            float now = Mission.MissionTime;
            _radioBusyUntil = Math.Max(now, _radioBusyUntil) + Math.Max(0f, length);
        }

        public bool IsRadioBusy() => Mission.MissionTime < RadioQuietAt + RadioGapSeconds;
        public float RadioFreeIn => Math.Max(0f, RadioQuietAt + RadioGapSeconds - Mission.MissionTime);

        /// <summary>When the radio falls quiet: what the game's radio holds, plus our own lines still waiting.
        /// Idle radio, nothing waiting: the moment the last clip ended (in the past), so the gap counts from then -
        /// NOT "now", which would make the radio permanently busy and drop every low-priority line.</summary>
        private float RadioQuietAt => _outbox.Count == 0 ? _radioBusyUntil
            : Math.Max(_radioBusyUntil, Mission.MissionTime) + _outbox.Sum(waiting => waiting.Length);

        /// <summary>Colour, not information about you: arrivals, kill reports, boasts and their replies, torpedo hits, "here
        /// they come", nice shot - and other flights' help calls. Warnings to the player, order replies, thanks, your own
        /// flight's hits/laments, objective calls and EVERY death rattle are never dropped for spacing. A death rattle
        /// queues behind whatever is on the radio.</summary>
        public bool IsLowPriority(string situationId, Ship sender)
        {
            if (situationId == null) return false;
            if (LowPrioritySituations.Contains(situationId) || Situations.IsKillStreak(situationId)) return true;
            if (string.Equals(situationId, Situations.WingmanHelpRequest, StringComparison.OrdinalIgnoreCase)
                && sender != null && !SameFlightGroupAsPlayer(sender) && !SameFlightByCallsign(sender))
                return true;
            return false;
        }

        // ------------------------------------------------------------------ outbox
        // The game's Radio plays its queue strictly in order (Xwvm.Cockpit.Radio.ManualUpdate: next clip only when the
        // AudioSource is idle) and never takes a clip back, and we leave its queue alone. If every line went onto it
        // the moment it was chosen, a death rattle would play seconds after the ship blew up, behind that pilot's own
        // earlier calls. So our lines wait HERE and go to the game's radio (CockpitInstruments.PlayCustomRadioMessage)
        // one at a time, only when it is free. A death rattle throws out the dying pilot's waiting lines and goes to
        // the front: it plays as soon as the current transmission ends. Story messages still go straight to the
        // game's radio and our lines wait for them.

        /// <summary>A line waiting for the radio.</summary>
        private sealed class OutItem
        {
            public string Key;          // the clip's key in the game's AudioClipManager
            public string Situation;
            public string Subtitle;     // null: none
            public float Length;        // seconds of the clip
            public float QueuedAt;      // mission time
            public Ship Ship;           // the speaker's craft; null for the officer and the astromech
            public Color? Colour;
            public bool R2;             // subtitle shown as the astromech's
            public bool IsDeath;        // a death rattle
            public Iff Iff;
        }

        private readonly List<OutItem> _outbox = new List<OutItem>();
        private bool _pumping;   // the Pump coroutine is running
        /// <summary>A line that waited this long is stale and is dropped (a death rattle never is).</summary>
        public float OutboxMaxWaitSeconds = 8f;

        /// <summary>Put a line in the outbox and make sure it is being emptied.</summary>
        /// <returns>How many transmissions are ahead of it (the one on the game's radio included).</returns>
        private int Enqueue(OutItem item)
        {
            if (item.IsDeath && item.Ship != null)
            {
                int removed = _outbox.RemoveAll(waiting => !waiting.IsDeath && ReferenceEquals(waiting.Ship, item.Ship));
                if (removed > 0) _log($"[chatter] {SafeDesignation(item.Ship)} died: {removed} of its waiting line(s) dropped");
                int afterEarlierRattles = _outbox.FindLastIndex(waiting => waiting.IsDeath) + 1;   // ahead of everything else
                _outbox.Insert(afterEarlierRattles, item);
            }
            else _outbox.Add(item);
            if (!_pumping) { _pumping = true; _runner.StartCoroutine(Pump()); }
            return _outbox.IndexOf(item) + (Mission.MissionTime < _radioBusyUntil ? 1 : 0);
        }

        /// <summary>Hands the outbox to the game's radio, one line each time the radio falls idle, dropping lines
        /// that have gone stale meanwhile. Runs while the outbox has anything in it.</summary>
        private IEnumerator Pump()
        {
            // Unity stops a coroutine that throws. Without the finally the flag would stay set and nothing would ever
            // start the pump again: no more queued lines for the rest of the mission.
            try
            {
                while (!_disposed && _outbox.Count > 0)
                {
                    if (Mission.MissionTime < _radioBusyUntil - 0.05f) { yield return null; continue; }   // game radio still talking
                    var next = _outbox[0]; _outbox.RemoveAt(0);
                    if (PutOnAir(next)) yield return null;
                }
            }
            finally { _pumping = false; }
        }

        /// <summary>Play one queued line unless it has gone stale. False when it was dropped. An error costs that
        /// one line, not the queue.</summary>
        private bool PutOnAir(OutItem next)
        {
            try
            {
                if (InEndStateHold()) { _log($"[chatter] {next.Situation}: end-state hold while waiting; dropped"); return false; }
                if (!next.IsDeath && next.Ship != null && !IsAlive(next.Ship)) { _log($"[chatter] {next.Situation} from {SafeDesignation(next.Ship)}: speaker died while waiting; dropped"); return false; }
                float waited = Mission.MissionTime - next.QueuedAt;
                if (!next.IsDeath && waited > OutboxMaxWaitSeconds) { _log($"[chatter] {next.Situation}: waited {waited:0.0} s for the radio; stale, dropped"); return false; }
                var gameManager = GameManagerOrNull();
                var instruments = gameManager?.CockpitInstruments;
                if (instruments == null) return false;
                instruments.PlayCustomRadioMessage(next.Key);             // NoteRadioClip runs in our postfix on this call
                if (next.Ship != null && !next.IsDeath) NoteWingLine(next.Length);
                if (next.Subtitle != null) ShowSubtitle(gameManager, next.Subtitle, next.Colour, next.R2, next.Iff);
                if (waited > 0.5f) _log($"[chatter] {next.Situation}: on air after waiting {waited:0.0} s");
                return true;
            }
            catch (Exception e)
            {
                _log($"[chatter] {next.Situation}: error putting the line on air; dropped ({e.Message})");
                return false;
            }
        }

        /// <summary>Show a line's words as the game shows a radio message: a subtitle and an entry in the message log.</summary>
        private void ShowSubtitle(GameManager gameManager, string subtitle, Color? colour, bool asAstromech, Iff iff)
        {
            try { gameManager.SubtitleManager?.addNewSubtitle(asAstromech ? SubtitleManager.SubtitleType.R2 : SubtitleManager.SubtitleType.dialogue, subtitle, colour, 5); }
            catch (Exception e) { _log($"[chatter] subtitle error: {e.Message}"); }
            try { gameManager.ShowMessageInLog(subtitle, iff); } catch (Exception e) { _log($"[chatter] log error: {e.Message}"); }
        }

        // ------------------------------------------------------------------ once per window

        /// <summary>A situation speaks at most once in this many seconds of mission time - the game's own window for
        /// repeating a radio call (XwGameplaySituationHandler._radioSpamFilter).</summary>
        public const float SpamWindowSeconds = 10f;

        private readonly Dictionary<string, float> _lastSpoken = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);   // situation -> mission time
        private readonly HashSet<Common.EntityId> _deathRattled = new HashSet<Common.EntityId>();   // ships whose death rattle is claimed

        public bool IsThrottled(string situationId)
        {
            // death rattles are never held to one per window: each dying pilot gets theirs
            if (IsDeathRattle(situationId)) return false;
            return _lastSpoken.TryGetValue(situationId, out var spokenAt) && spokenAt > Mission.MissionTime - SpamWindowSeconds;
        }

        public void MarkSpoken(string situationId) => _lastSpoken[situationId] = Mission.MissionTime;
        /// <summary>Mission seconds since this situation last spoke (or reserved its slot); +inf if never.</summary>
        public float SinceSpoken(string situationId) => _lastSpoken.TryGetValue(situationId, out var spokenAt) ? Mission.MissionTime - spokenAt : float.PositiveInfinity;
        public void ClearThrottle(string situationId) => _lastSpoken.Remove(situationId);

        /// <summary>One death rattle per ship: true the first time this ship's death is claimed (kill event or the
        /// game's own radio call, whichever comes first), false after.</summary>
        public bool ClaimDeathRattle(Ship ship)
        {
            if (ship == null) return true;
            try { return _deathRattled.Add(ship.EntityId); } catch { return true; }
        }
    }
}
