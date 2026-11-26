using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using XMission = Xwvm.MissionClasses.Mission;

namespace XwvmChatter
{
    /// <summary>
    /// One mission's chatter: the casting and lines that apply to it, and everything remembered while it is flown.
    /// Created when the game's X-Wing situation handler initialises, discarded when the game disposes it.
    ///
    /// The class is split over several files by concern:
    ///   World      questions about the game's ships (alive? a wingman? whose flight?)
    ///   Casting    which speaker a ship is; the generic pool for uncast wingmen
    ///   Resolve    situation -> the line and the speaker who says it
    ///   Callsigns  swapping in the recorded version that names a callsign
    ///   Memory     kills, fire, threats, arrivals - what later decisions depend on
    ///   Radio      story lines first, our outbox, spacing, the once-per-window throttle
    ///   Clips      building a clip the first time its line is picked
    ///   Play       handing a line to the game's radio with its subtitle
    /// </summary>
    public sealed partial class ChatterSession
    {
        public readonly XMission Mission;
        public readonly ChatterConfig Config;
        private readonly MonoBehaviour _runner;   // runs our coroutines (the plugin)
        private readonly Action<string> _log;
        private bool _disposed;

        // Extension points. Each is a no-op unless extra source files implement it.
        partial void NoteAction(string action);                 // the session started a load or a play
        partial void OnClipBuilt(string path, AudioClip clip);  // a clip build completed
        partial void OnPlayStarting();                          // around Play
        partial void OnPlayFinished();
        partial void OnHandedToRadio();                         // a clip was handed to the radio

        public ChatterSession(XMission mission, ChatterConfig config, MonoBehaviour runner, Action<string> log)
        {
            Mission = mission; Config = config; _runner = runner; _log = log;
            int adopted = ClipCache.AdoptInto(_clips, config.MissionName);
            if (adopted > 0) _log($"[chatter] took {adopted} clip(s) preloaded on the loading screen");
            DiscoverCallsigns();
            _runner.StartCoroutine(DealPoolWhenAwake());
        }

        /// <summary>Mission time; stands still while the game has the mission frozen (the opening hyperspace).</summary>
        internal float MissionTime { get { try { return Mission?.MissionTime ?? 0f; } catch { return 0f; } } }

        /// <summary>The chatter wakes up Radio.StartupQuietSeconds into the mission, leaving the game's own mission
        /// start alone. Before that every situation is left to the game and nothing is loaded.</summary>
        internal bool IsAwake => MissionTime >= (Plugin.Instance?.StartupQuietSeconds ?? 1f);

        /// <summary>Deal the generic pool once the chatter is awake: the session is created before the ships are, and
        /// by now the opening jump is over and the player ship and wingmen exist.</summary>
        private IEnumerator DealPoolWhenAwake()
        {
            while (!_disposed && !IsAwake) yield return null;
            if (_disposed) yield break;
            _log($"[chatter] awake at mission time {MissionTime:0.0} s");
            try { if (Mission.GetAllShipsOtherThanPlayer().Any()) EnsureGenericPoolDealt(); }
            catch (Exception e) { _log($"[chatter] pool deal skipped ({e.Message})"); }
        }

        public void Dispose()
        {
            _disposed = true;
            ClipCache.Bytes.Clear();   // the clip files read on the loading screen
            _outbox.Clear();
            foreach (var clip in _clips.Values)
            {
                if (clip == null) continue;
                try { clip.UnloadAudioData(); UnityEngine.Object.Destroy(clip); } catch { }
            }
            _clips.Clear();
            _registeredKeys.Clear();
        }
    }
}
