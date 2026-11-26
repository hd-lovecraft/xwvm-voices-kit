using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace XwvmChatter
{
    // Clips: which are in memory, building one the first time its line is picked, and playing a line once its clip
    // has landed. The files themselves were read on the loading screen (LoadScreenPreload), so a build here is a
    // header parse on a worker thread plus AudioClip.Create - the game's own practice of loading a radio clip at
    // play time (AudioClipManager.LoadAudioClipAsync), with the disk read already done.
    public sealed partial class ChatterSession
    {
        private const float ClipLoadTimeoutSeconds = 4f;   // the "not ready after 4 s" log line states it too

        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);   // clip path -> built clip
        private readonly HashSet<string> _loading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // clip paths a build was started for
        private readonly HashSet<string> _failedClips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // clip paths whose build failed: never ready
        private readonly HashSet<string> _registeredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // clips handed to the game's AudioClipManager
        private int _clipBuildsInFlight;   // builds started and not yet finished

        public bool ClipsReady => _clipBuildsInFlight == 0;
        public int ClipCount => _clips.Count;
        internal int ClipBuildsInFlight => _clipBuildsInFlight;

        private bool Loaded(string path) => _clips.ContainsKey(path);

        /// <summary>The clip's build failed, so waiting for it is pointless.</summary>
        private bool BuildFailed(string path) => _failedClips.Contains(path);

        /// <summary>Seconds of a loaded clip (0 when unknown).</summary>
        public float ClipLength(string path)
        {
            try { return path != null && _clips.TryGetValue(path, out var clip) && clip != null ? clip.length : 0f; } catch { return 0f; }
        }

        /// <summary>Start building one clip. Does nothing when it is already in memory or on its way.</summary>
        private void RequestLoad(string path)
        {
            if (string.IsNullOrEmpty(path) || _clips.ContainsKey(path) || !_loading.Add(path)) return;
            NoteAction("load " + Path.GetFileName(path));
            _clipBuildsInFlight++;
            try
            {
                _runner.StartCoroutine(ClipCache.LoadClip(path, clip =>
                {
                    _clipBuildsInFlight--;
                    if (_disposed) { try { if (clip != null) UnityEngine.Object.Destroy(clip); } catch { } return; }
                    if (clip == null) { _failedClips.Add(path); _log($"[chatter] failed to decode {path}"); return; }
                    clip.name = "chatter:" + Path.GetFileNameWithoutExtension(path);
                    _clips[path] = clip;
                    OnClipBuilt(path, clip);
                }, _log));
            }
            catch (Exception e) { _clipBuildsInFlight--; _failedClips.Add(path); _log($"[chatter] load error {path}: {e.Message}"); }
        }

        /// <summary>A line whose clip is not in memory yet: play it when the clip lands (a few hundred ms at most)
        /// instead of handing the radio to the stock voice.</summary>
        private IEnumerator PlayWhenReady(Resolution res, string situationId)
        {
            while (IsMissionFrozen() && !_disposed) yield return null;             // never start a line over a jump
            float waited = 0f;
            while (!Loaded(res.Line.AudioFile) && !_disposed)
            {
                if (BuildFailed(res.Line.AudioFile))
                {
                    // Counted as heard so the next pick is another line. The throttle slot stays taken: a situation
                    // whose only line is broken is then tried once per window, not on every report.
                    _history.Remember(situationId, res.Line);
                    _log($"[chatter] {situationId}: clip could not be built; dropped ({Path.GetFileName(res.Line.AudioFile)})");
                    yield break;
                }
                if (waited > ClipLoadTimeoutSeconds)
                {
                    _lastSpoken.Remove(situationId);   // nothing was said: give the slot back so the next report can speak
                    _log($"[chatter] {situationId}: clip not ready after 4 s; dropped ({Path.GetFileName(res.Line.AudioFile)})");
                    yield break;
                }
                waited += Time.deltaTime; yield return null;
            }
            if (_disposed) yield break;
            if (SpeakerGone(res.Ship, situationId)) { _log($"[chatter] {situationId}: speaker died while its clip loaded; dropped"); yield break; }
            _lastSpoken.Remove(situationId);   // release the slot we reserved so Play's own MarkSpoken is the real one
            Play(res, situationId);
        }
    }
}
