using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace XwvmChatter
{
    /// <summary>Where a clip load's time goes: waiting for the file (off the main thread) against building the
    /// AudioClip (on the main thread). Logged after a loading-screen preload.</summary>
    internal static class LoadStats
    {
        internal static int Loads;
        internal static double WaitSecondsTotal, BuildMsTotal;
        internal static float BuildMsMax;
        /// <summary>Main-thread time of the most recent AudioClip.Create by our own decoder.</summary>
        internal static float LastBuildMs;

        internal static void Reset() { Loads = 0; WaitSecondsTotal = BuildMsTotal = 0; BuildMsMax = 0f; }

        internal static void Note(float waitSeconds, float buildMs)
        {
            Loads++; WaitSecondsTotal += waitSeconds; BuildMsTotal += buildMs;
            if (buildMs > BuildMsMax) BuildMsMax = buildMs;
        }

        internal static string Summary() => Loads == 0 ? "no loads" :
            $"{Loads} load(s): request {1000 * WaitSecondsTotal / Loads:0.0} ms avg (overlapped), GetContent {BuildMsTotal / Loads:0.00} ms avg / {BuildMsMax:0.0} ms max, {BuildMsTotal / 1000:0.0} s total on the main thread";
    }

    /// <summary>What the loading screen loaded, held across the scene change until the chatter session for that mission
    /// starts. Also the one place a clip is loaded from a file, for the loading screen and the session alike.</summary>
    internal static class ClipCache
    {
        /// <summary>Radio.DirectDecode on: the loading screen reads each clip's FILE into memory and builds nothing; a
        /// line's clip is built when it is first picked (a worker thread, then AudioClip.Create on the main thread) - the
        /// game's own practice of loading radio clips at play time (AudioClipManager.LoadAudioClipAsync), with the disk
        /// read already done. Keyed by file path; cleared when the session ends.</summary>
        internal static readonly ConcurrentDictionary<string, byte[]> Bytes =
            new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Radio.DirectDecode off: clips built on the loading screen, until the session adopts them. Anything
        /// not adopted (no session started, a different mission) is destroyed.</summary>
        internal static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The mission <see cref="Clips"/> were loaded for.</summary>
        internal static string Mission;

        internal static void Discard()
        {
            foreach (var clip in Clips.Values) { try { if (clip != null) { clip.UnloadAudioData(); UnityEngine.Object.Destroy(clip); } } catch { } }
            Clips.Clear(); Mission = null;
        }

        /// <summary>Hand every cached clip to a session for this mission; returns how many.</summary>
        internal static int AdoptInto(Dictionary<string, AudioClip> into, string mission)
        {
            if (Clips.Count == 0) return 0;
            if (!string.Equals(Mission, mission, StringComparison.OrdinalIgnoreCase)) { Discard(); return 0; }
            int adopted = 0;
            foreach (var cached in Clips) if (cached.Value != null && !into.ContainsKey(cached.Key)) { into[cached.Key] = cached.Value; adopted++; }
            Clips.Clear(); Mission = null;
            return adopted;
        }

        /// <summary>Load one clip and hand it to <paramref name="done"/> (null when it failed; the reason goes to
        /// <paramref name="log"/>). An .ogg goes through our own decoder when Radio.DirectDecode is on; anything else
        /// through the game's loader. Run the result as a coroutine.</summary>
        internal static IEnumerator LoadClip(string path, Action<AudioClip> done, Action<string> log)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".ogg" && (Plugin.Instance?.DirectDecode ?? true)
                ? LoadWithOwnDecoder(path, done, log)
                : LoadWithUnity(path, extension, done, log);
        }

        /// <summary>Our own decoder (OggDirect): the file and its header on a worker thread, AudioClip.Create here.</summary>
        private static IEnumerator LoadWithOwnDecoder(string path, Action<AudioClip> done, Action<string> log)
        {
            float started = Time.realtimeSinceStartup;
            var task = OggDirect.PrepareAsync(path);
            while (!task.IsCompleted) yield return null;
            float prepared = Time.realtimeSinceStartup;
            AudioClip clip = null;
            var preparedClip = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            if (preparedClip == null || preparedClip.Error != null) log?.Invoke($"[chatter] decode error {path}: {preparedClip?.Error ?? task.Exception?.GetBaseException().Message}");
            else
            {
                var buildTimer = Stopwatch.StartNew();
                try { clip = OggDirect.Build(Path.GetFileNameWithoutExtension(path), preparedClip); }
                catch (Exception e) { log?.Invoke($"[chatter] decode error {path}: {e.Message}"); }
                float buildMs = (float)buildTimer.Elapsed.TotalMilliseconds;
                LoadStats.Note(prepared - started, buildMs);
                LoadStats.LastBuildMs = buildMs;
            }
            done(clip);
        }

        /// <summary>The game's loader. Mirrors AudioClipHelper.LoadAudioClipFromFile (UnityWebRequestMultimedia over
        /// "file://" + path, format from the extension), with Radio.CompressedClips choosing whether the Vorbis data
        /// stays compressed in memory and Radio.StreamClips whether it is streamed.</summary>
        private static IEnumerator LoadWithUnity(string path, string extension, Action<AudioClip> done, Action<string> log)
        {
            var type = extension == ".ogg" ? AudioType.OGGVORBIS : extension == ".wav" ? AudioType.WAV : extension == ".mp3" ? AudioType.MPEG : AudioType.UNKNOWN;
            using (var request = UnityWebRequestMultimedia.GetAudioClip("file://" + path, type))
            {
                if (request.downloadHandler is DownloadHandlerAudioClip handler)
                {
                    handler.compressed = Plugin.Instance?.CompressedClips ?? true;
                    handler.streamAudio = Plugin.Instance?.StreamClips ?? false;
                }
                float started = Time.realtimeSinceStartup;
                yield return request.SendWebRequest();
                float received = Time.realtimeSinceStartup;
                AudioClip clip = null;
                if (request.result == UnityWebRequest.Result.Success)
                {
                    var buildTimer = Stopwatch.StartNew();
                    try { clip = DownloadHandlerAudioClip.GetContent(request); }
                    catch (Exception e) { log?.Invoke($"[chatter] decode error {path}: {e.Message}"); }
                    LoadStats.Note(received - started, (float)buildTimer.Elapsed.TotalMilliseconds);
                }
                else log?.Invoke($"[chatter] load error {path}: {request.error}");
                done(clip);
            }
        }
    }
}
