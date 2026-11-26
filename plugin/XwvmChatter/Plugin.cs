using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameMetadata;
using HarmonyLib;
using Util;
using XMission = Xwvm.MissionClasses.Mission;

namespace XwvmChatter
{
    // The entry point: settings, and the start and end of a mission's chatter session. The other parts of the class:
    //   Plugin.Pipeline.cs    the checks every situation passes before a line plays
    //   Plugin.Reactions.cs   calls the game has no situation for (laments, "here they come", teasing)
    [BepInPlugin(Guid, Name, Version)]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        /// <summary>Also the name of the config file players see (BepInEx\config\xwvm.voices.chatter.cfg).</summary>
        public const string Guid = "xwvm.voices.chatter";
        public const string Name = "XWVM Dynamic Wingman Chatter";
        /// <summary>The release number. Written once, in XwvmChatter.csproj &lt;Version&gt;; BuildInfo is generated from it
        /// at build time.</summary>
        public const string Version = BuildInfo.Version;

        internal static Plugin Instance;
        internal static ManualLogSource Log;
        /// <summary>The mission being flown; null outside flight and in a mission without chatter data.</summary>
        internal static ChatterSession Session;

        private Harmony _harmony;

        // Extension points. Each is a no-op unless extra source files implement it.
        partial void OnAwakeBeforeLog(ref bool announced);        // end of Awake, before the "loaded" line
        partial void OnSessionStarted(ChatterSession session);    // a mission's session has just begun
        partial void OnPickStarting();                            // before a line is looked for
        partial void OnPickFinished(string situationId, string reason);

        // ------------------------------------------------------------------ settings
        // Bound in Awake, which holds the defaults and descriptions. Awake binds every entry before it applies the first
        // patch, so the "?? fallback" values in the accessors below only guard against a read before that.

        // [General]
        private ConfigEntry<bool> _enabled;
        private ConfigEntry<string> _sidecarDir;
        private ConfigEntry<bool> _verbose;

        // [Radio] when a line may play
        private ConfigEntry<float> _startupQuiet;
        private ConfigEntry<float> _narrativeHold;
        private ConfigEntry<float> _endStateHold;
        private ConfigEntry<float> _radioGap;
        private ConfigEntry<string> _delays;
        private ConfigEntry<string> _ownFlightOnly;

        // [Radio] the reactions
        private ConfigEntry<float> _playerKillNiceShotChance;
        private ConfigEntry<float> _niceShotCooldown;
        private ConfigEntry<bool> _saveCountsTargeting;
        private ConfigEntry<float> _killBoastChance;
        private ConfigEntry<float> _killBoastReplyChance;
        private ConfigEntry<float> _engagementQuiet;
        private ConfigEntry<float> _engagementRange;
        private ConfigEntry<int> _engagementMinCraft;
        private ConfigEntry<int> _engagementMaxPerMission;
        private ConfigEntry<float> _wingmanLostQuiet;
        private ConfigEntry<float> _wingmanLostChance;
        private ConfigEntry<float> _wingmanLostReplyChance;
        private ConfigEntry<string> _wingmanLostDelay;

        // [Radio] loading the clips
        private ConfigEntry<bool> _preloadOnLoadingScreen;
        private ConfigEntry<int> _loadsInFlight;
        private ConfigEntry<bool> _directDecode;
        private ConfigEntry<bool> _streamClips;
        private ConfigEntry<bool> _compressedClips;

        internal bool Enabled => _enabled.Value;
        internal bool Verbose => _verbose.Value;

        /// <summary>Where the chatter data is: the folder the game reads its sidecars from, unless overridden.</summary>
        internal string SidecarDir
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_sidecarDir.Value)) return _sidecarDir.Value;
                var game = GeneralManager.CurrentGame.ToString();
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XWVM", "GlobalSidecars", game);
            }
        }

        internal float StartupQuietSeconds => _startupQuiet?.Value ?? 1f;
        internal float NarrativeHoldSeconds => _narrativeHold?.Value ?? 0f;

        /// <summary>Delay window (min,max seconds) for a situation, or null.</summary>
        internal (float min, float max)? DelayFor(string situationId)
        {
            if (Situations.IsKillStreak(situationId)) situationId = Situations.KillReport;   // a boast is a kill report with a number
            return SettingText.DelayFor(_delays?.Value, situationId);
        }

        /// <summary>The situations voiced only for the player's own flight group ("*" = all of them). Read from the
        /// setting on every call, so an edit takes effect in flight.</summary>
        internal HashSet<string> OwnFlightOnly => new HashSet<string>(
            (_ownFlightOnly?.Value ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        internal float PlayerKillNiceShotChance => _playerKillNiceShotChance?.Value ?? 0f;
        internal float NiceShotCooldown => _niceShotCooldown?.Value ?? 60f;
        internal float KillBoastChance => _killBoastChance?.Value ?? 0.4f;
        internal float KillBoastReplyChance => _killBoastReplyChance?.Value ?? 0.5f;
        internal float EngagementQuietSeconds => _engagementQuiet?.Value ?? 15f;
        internal float EngagementRange => _engagementRange?.Value ?? 1000f;
        internal int EngagementMinCraft => _engagementMinCraft?.Value ?? 3;
        internal int EngagementMaxPerMission => _engagementMaxPerMission?.Value ?? 2;
        internal float WingmanLostQuietSeconds => _wingmanLostQuiet?.Value ?? 3.5f;
        internal float WingmanLostChance => _wingmanLostChance?.Value ?? 1f;
        internal float WingmanLostReplyChance => _wingmanLostReplyChance?.Value ?? 0.6f;
        internal (float min, float max) WingmanLostDelay =>
            SettingText.TryRange(_wingmanLostDelay?.Value ?? "2.5-4.5", out var min, out var max) ? (min, max) : (2.5f, 4.5f);

        internal bool PreloadOnLoadingScreen => _preloadOnLoadingScreen?.Value ?? true;
        internal int LoadsInFlight => _loadsInFlight?.Value ?? 48;
        internal bool DirectDecode => _directDecode?.Value ?? true;
        internal bool StreamClips => _streamClips?.Value ?? false;
        internal bool CompressedClips => _compressedClips?.Value ?? true;

        // ------------------------------------------------------------------ startup

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            OggDirect.InstallResolver();   // the Vorbis decoder is embedded in this DLL
            _enabled = Config.Bind("General", "Enabled", true, "Master switch. When false the plugin never intercepts anything.");
            _sidecarDir = Config.Bind("General", "SidecarDir", "", "Override for the sidecar directory. Empty = %APPDATA%\\XWVM\\GlobalSidecars\\<game> (same folder the game reads).");
            _verbose = Config.Bind("General", "Verbose", true, "Log every intercepted situation, including the ones left to the game's stock line.");
            _playerKillNiceShotChance = Config.Bind("Radio", "PlayerKillNiceShotChance", 0.5f,
                "Chance (0..1) that a wingman says a NiceShot line when the PLAYER destroys a hostile craft. The base game only " +
                "congratulates kills from over 150 m; this rolls on every kill instead, limited by PlayerKillNiceShotCooldownSeconds. 0 = off.");
            _niceShotCooldown = Config.Bind("Radio", "PlayerKillNiceShotCooldownSeconds", 60f,
                "After a wingman praises one of your kills, no praise for this many seconds of mission time.");
            _delays = Config.Bind("Radio", "Delays", "KillReport=1.0-2.0,NiceShot=0.8-1.8,SaveThanks=1.0-2.2",
                "Per-situation delay before the line plays, seconds (min-max, random): a pilot takes a beat before reporting a kill. " +
                "Format: Situation=min-max,... The speaker is re-checked when the line plays (a dead pilot doesn't report).");
            _startupQuiet = Config.Bind("Radio", "StartupQuietSeconds", 1f,
                "Seconds of mission time before the chatter wakes up: until then nothing is said and no clips are loaded, so the " +
                "game's own mission start (the opening hyperspace) runs exactly as it does without the mod. Mission time stands " +
                "still during the opening jump, so this counts from drop-out. Default 1 s = the game's own announcer gate " +
                "(XwGameplaySituationHandler ignores mission events while MissionTime < 1).");
            _killBoastChance = Config.Bind("Radio", "KillBoastChance", 0.4f,
                "When a wingman of the player's flight scores its 2nd..5th fighter kill of the mission, the chance its kill report is a " +
                "count boast (\"That's three for me.\") instead of the plain report.");
            _killBoastReplyChance = Config.Bind("Radio", "KillBoastReplyChance", 0.5f,
                "After a boast, the chance another live flightmate teases the boaster (\"Save some for the rest of us!\").");
            _engagementQuiet = Config.Bind("Radio", "EngagementQuietSeconds", 15f,
                "\"Here they come!\" only when the player's flight has neither fired nor been fired at for this long.");
            _engagementRange = Config.Bind("Radio", "EngagementRange", 1000f,
                "\"Here they come!\" fires when the nearest of the approaching hostiles is within this many metres of one of ours and " +
                "closing - right before they open fire (about 1 km).");
            _engagementMinCraft = Config.Bind("Radio", "EngagementMinCraft", 3,
                "\"Here they come!\" needs at least this many fighters of one hostile group to have members of the player's flight targeted.");
            _engagementMaxPerMission = Config.Bind("Radio", "EngagementMaxPerMission", 2,
                "At most this many \"here they come\" calls per mission (one per hostile group).");
            _wingmanLostQuiet = Config.Bind("Radio", "WingmanLostQuietSeconds", 3.5f,
                "Dialogue pacing for the lament: if no wingman has spoken for this long when it plays (death rattles not counted), " +
                "the quiet line (\"We lost Red Two.\"); otherwise the intense one (\"Red Two! No!\"). The reply keeps the lament's register.");
            _wingmanLostChance = Config.Bind("Radio", "WingmanLostChance", 1f,
                "When a wingman of the player's flight dies, the chance a surviving flightmate laments them by name (\"We lost Red Three\").");
            _wingmanLostReplyChance = Config.Bind("Radio", "WingmanLostReplyChance", 0.6f,
                "After a lament, the chance a live flightmate above the lamenter in the flight (lower seat number: Red 2 to Red 4) " +
                "answers them by name (\"Keep it together, Red Four\"). Wingmen only; if nobody above is left, the lament stands alone.");
            _wingmanLostDelay = Config.Bind("Radio", "WingmanLostDelay", "2.5-4.5",
                "Seconds after the death (and its death rattle) before the lament, min-max.");
            _preloadOnLoadingScreen = Config.Bind("Radio", "PreloadOwnFlight", true,
                "Preload every chatter clip this mission can play - every voice in it (cast, generic pool, flight officer, " +
                "astromech), every line, every callsign variant - on the game's own loading screen, as one more step after its " +
                "flight-music preload, so nothing loads in flight. Off: each line loads the first time it is wanted (the game's " +
                "pattern for its radio messages), which can stutter. Despite the name, this is not limited to the player's flight.");
            _narrativeHold = Config.Bind("Radio", "NarrativeHoldSeconds", 8f,
                "After a scripted narrative radio message starts, dynamic chatter stays quiet for this long so it never steps on " +
                "the story. Warnings of danger to the player (UnderAttack / IncomingMissile), replies to the player's orders and " +
                "death calls are exempt. 0 = off.");
            _saveCountsTargeting = Config.Bind("Radio", "SaveCountsTargeting", false,
                "Off (default): a kill counts as saving a wingman only if that hostile fired at or hit the wingman in the last few " +
                "seconds. On: also when the hostile merely had the wingman targeted within the game's 800 m threat distance - true " +
                "for almost every enemy fighter in a dogfight, so nearly every kill becomes a 'save'.");
            _loadsInFlight = Config.Bind("Radio", "LoadsInFlight", 48,
                "With DirectDecode off: how many chatter clips the loading screen loads at once. Each loading-screen frame finishes all of them together.");
            _directDecode = Config.Bind("Radio", "DirectDecode", true,
                "On: the loading screen only reads the clip files into memory (under a second) and each clip is built by the plugin's " +
                "own decoder when its line is first picked, off the main thread. Off: the game's own loader (UnityWebRequest) builds " +
                "every clip on the loading screen (up to a minute for a large mission) and Unity unpacks each clip as it starts " +
                "playing, which costs longer frames.");
            _streamClips = Config.Bind("Radio", "StreamClips", false,
                "Experimental, with DirectDecode off: load clips as streamed (DownloadHandlerAudioClip.streamAudio) - Unity reads the " +
                "file when the clip plays instead of building the whole clip at load time. Off: the game's own behaviour (whole clip at load).");
            _compressedClips = Config.Bind("Radio", "CompressedClips", true,
                "With DirectDecode off: keep chatter clips compressed in memory and decode them on the audio thread as they play (no " +
                "main-thread decode when a clip finishes loading; about a tenth of the memory). Off: the game's own behaviour - " +
                "decode the whole clip when it loads.");
            _radioGap = Config.Bind("Radio", "RadioGapSeconds", 2f,
                "Low-priority calls (arrivals, kill reports, boasts and replies, torpedo hits, 'here they come', nice shot, other " +
                "flights' help calls) are dropped while the cockpit radio is talking or within this many seconds of it going quiet. " +
                "Warnings to you, order replies, thanks, your own flight's calls and every death call always play. 0 = only while talking.");
            _endStateHold = Config.Bind("Radio", "EndStateHoldSeconds", 12f,
                "When the game reports the primary goals complete or failed, ALL chatter (warnings included) stays quiet for at " +
                "least this long and until NarrativeHoldSeconds after the last scripted message, so the mission's closing narrative " +
                "plays on its own. Objective (MCC) calls stay off for the rest of the mission.");
            _ownFlightOnly = Config.Bind("Radio", "OwnFlightOnly", "PreparingHyper,KillReport,NiceShot",
                "Situations spoken by a wingman that are voiced ONLY when the sender is in the player's flight group; a wingman from " +
                "another flight group is silenced for them (not stock). Default: the chatty ones (hyperspace departures, kill " +
                "reports, nice-shot) - hits, help calls and deaths are still heard from any friendly fighter. '*' = every " +
                "wingman-spoken situation; empty = hear everyone. Player warnings are unaffected (own flight preferred, any live " +
                "wingman otherwise).");
            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(GameHooks));
            _harmony.PatchAll(typeof(LoadScreenPreload));
bool announced = false;
            OnAwakeBeforeLog(ref announced);
            if (!announced) Log.LogInfo($"{Name} {BuildInfo.LoggedVersion} loaded. Enabled={_enabled.Value}");
        }

        /// <summary>The mission being loaded or flown, as the bare file name the sidecars are matched by; null when the
        /// game has none.</summary>
        internal static string CurrentMissionName()
        {
            try
            {
                var entry = SceneHelper.GetParameter<MissionEntry>("ActiveMission");
                if (!string.IsNullOrEmpty(entry.file)) return Path.GetFileNameWithoutExtension(entry.file);
            }
            catch { }
            return null;
        }

        /// <summary>Logs only when General.Verbose is on: the decisions that say why a line did not play.</summary>
        internal void LogVerbose(string message) { if (Verbose) Log.LogInfo(message); }

        // ------------------------------------------------------------------ session lifecycle

        internal void BeginSession(XMission mission)
        {
            EndSession();
            var name = CurrentMissionName() ?? "?";
            var config = ChatterConfig.Load(SidecarDir, name, message => Log.LogInfo(message));
            if (config.IsEmpty) { Log.LogInfo($"[chatter] no chatter config for mission {name}; stock behaviour"); return; }
            Session = new ChatterSession(mission, config, this, message => Log.LogInfo(message));
            Session.EndStateHoldSeconds = _endStateHold?.Value ?? 12f;
            Session.RadioGapSeconds = Math.Max(0f, _radioGap?.Value ?? 2f);
            Session.SaveCountsTargeting = _saveCountsTargeting?.Value ?? false;
            Session.EndStateNarrativeWindow = Math.Max(NarrativeHoldSeconds, 8f);
            Log.LogInfo($"[chatter] session started for {name}: speakers=[{string.Join(", ", config.Speakers.Values)}]");
            ResetEngagementWatch();
            OnSessionStarted(Session);
        }

        internal void EndSession()
        {
            if (Session == null)
            {
                // clips preloaded for a mission that never started its session
                if (ClipCache.Clips.Count > 0 && CurrentMissionName() != ClipCache.Mission) ClipCache.Discard();
                return;
            }
            try { Session.Dispose(); } catch (Exception e) { Log.LogWarning($"[chatter] dispose: {e.Message}"); }
            Session = null;
            Log.LogInfo("[chatter] session ended");
        }
    }
}
