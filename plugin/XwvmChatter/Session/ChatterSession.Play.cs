using System;
using System.IO;
using Common;
using UnityEngine;
using Xwvm.MissionClasses;

namespace XwvmChatter
{
    // Playback: hand a resolved line to the game's radio with its subtitle and log entry.
    public sealed partial class ChatterSession
    {
        /// <summary>Play a resolved line through the game's radio queue with our own subtitle and log line. Goes
        /// through the last checks again (end state, radio busy), since a line may reach here after a delay.</summary>
        /// <returns>True when the line was queued, shown, or will play once its clip is built; false when it was dropped.</returns>
        public bool Play(Resolution res, string situationId)
        {
            OnPlayStarting();
            try { return PlayTimed(res, situationId); }
            finally { OnPlayFinished(); }
        }

        private bool PlayTimed(Resolution res, string situationId)
        {
            NoteAction("play " + situationId + " " + Path.GetFileName(res.Line?.AudioFile ?? ""));
            if (InEndStateHold()) { _log($"[chatter] {situationId}: end-state hold (mission decided; closing narrative plays); dropped"); return false; }
            if (IsLowPriority(situationId, res.Ship) && IsRadioBusy()) { _log($"[chatter] {situationId} sender={SafeDesignation(res.Ship)}: radio busy at play time (free in {RadioFreeIn:0.0} s); low priority, dropped"); return false; }
            var gameManager = GameManagerOrNull();
            if (gameManager == null) { _log("[chatter] no GameManager"); return false; }
            var line = res.Line;
            AudioClip clip = null;
            if (line.AudioFile != null && !_clips.TryGetValue(line.AudioFile, out clip))
            {
                // never hand this to the stock voice - build the clip and speak when it lands
                RequestLoad(line.AudioFile);
                MarkSpoken(situationId);
                _runner.StartCoroutine(PlayWhenReady(res, situationId));
                _log($"[chatter] {situationId}: clip loading, will play when ready ({Path.GetFileName(line.AudioFile)})");
                return true;
            }

            var speakerName = DisplayNameFor(res);
            var iff = res.Ship?.Iff ?? Player?.Iff ?? Iff.Rebel;

            // The subtitle is built as GameManager.QueueRadioMessage builds one for a stock call: the faction's highlight
            // colour (addNewSubtitle takes a Color?), the astromech's own subtitle type.
            string subtitle = null; Color? subtitleColour = null; bool asAstromech = false;
            if (!string.IsNullOrEmpty(line.Text))
            {
                subtitle = $"<b>{speakerName}:</b> {MissionMessage.FormatText(line.Text)}";
                subtitleColour = gameManager.GetFactionColorHighlight(iff);
                asAstromech = res.Speaker?.IsAstromech ?? false;
            }

            int ahead = 0;
            if (clip != null)
            {
                OnHandedToRadio();
                var key = "chatter:" + line.AudioFile.ToLowerInvariant();
                if (_registeredKeys.Add(key)) gameManager.AudioClipManager.AddCustomClip(key, clip);
                if (gameManager.CockpitInstruments == null) { _log("[chatter] no CockpitInstruments"); return false; }
                ahead = Enqueue(new OutItem
                {
                    Key = key, Length = clip.length, Ship = res.Ship, Situation = situationId, Subtitle = subtitle,
                    Colour = subtitleColour, R2 = asAstromech, Iff = iff, QueuedAt = MissionTime,
                    IsDeath = IsDeathRattle(situationId),
                });
            }
            else if (subtitle != null)
            {
                // a line without a clip: its words only, shown at once
                ShowSubtitle(gameManager, subtitle, subtitleColour, asAstromech, iff);
            }

            MarkSpoken(situationId);
            _history.Remember(situationId, line);
            _log($"[chatter] {situationId} → {speakerName} ({res.Reason}) \"{line.Text}\" [{ClipLabel(line.AudioFile)}]" + (ahead > 0 ? $" (waits behind {ahead})" : ""));
            return true;
        }

        /// <summary>Log label for a line's audio.</summary>
        private static string ClipLabel(string audioFile)
        {
            if (string.IsNullOrEmpty(audioFile)) return "-";
            try { return Path.GetFileName(audioFile); } catch (ArgumentException) { return audioFile; }
        }

        /// <summary>The name a line is attributed to in its subtitle and in the log.</summary>
        public string DisplayNameFor(Resolution res)
        {
            // a generic-pool voice is always attributed to the ship it stands in for, never to the voice's own name
            if (res.Ship != null && (res.Speaker?.IsGeneric ?? false)) return SafeDesignation(res.Ship);
            if (res.Speaker != null && !string.IsNullOrEmpty(res.Speaker.DisplayName)) return res.Speaker.DisplayName;
            if (res.Ship != null) return SafeDesignation(res.Ship);
            if (res.Speaker != null)
            {
                if (res.Speaker.IsCommander) return "Commander";
                if (res.Speaker.IsAstromech) return "Astromech";
                return res.Speaker.Designation ?? res.Speaker.Id;
            }
            return "Radio";
        }
    }
}
