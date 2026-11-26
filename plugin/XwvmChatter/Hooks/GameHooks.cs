using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using Xwvm.MissionClasses.Entities;
using Xwvm.MissionClasses.GameplaySituationHandling;
using Xwvm.MissionClasses.Simulation.Events;
using Ev = Xwvm.MissionClasses.Events;
using XMission = Xwvm.MissionClasses.Mission;

namespace XwvmChatter
{
    /// <summary>
    /// Every place the plugin attaches to the game (Harmony patches), and why. Each patch only gathers what
    /// happened and passes it on; the decisions are in Plugin.Pipeline / Plugin.Reactions / ChatterSession.
    /// Postfixes unless marked.
    ///
    ///   LIFECYCLE
    ///   XwGameplaySituationHandler.Initialize                a mission's chatter starts with the game's own handler
    ///   XwGameplaySituationHandler.Dispose                   and ends with it
    ///
    ///   WHAT HAPPENED
    ///   XwGameplaySituationHandler.ReportSituation (prefix)  the game's typed radio situations; we voice them or let the stock line play
    ///   GameManager.QueueCustomRadioMessage (prefix)         the named ones (WingmanDestroyed, MCCAttacked...), same treatment
    ///   Mission.RegisterMissionEvent                         weapon fired / weapon hit / arrival / ship destroyed: events the game
    ///                                                        raises no radio situation for
    ///   Ship.NotifyFiredLaser                                which hostile is shooting at which wingman
    ///   Mission.ThinkMissionTriggers                         a once-a-second look at the flight, run inside the game's mission step
    ///
    ///   THE RADIO
    ///   CockpitInstruments.PlayCustomRadioMessage            every clip that reaches the cockpit radio, and how long it is
    ///   GameManager.QueueRadioMessageFromAssetRelativeToMission   a story line is playing (chatter yields)
    ///   GameManager.QueueRadioMessageFromAssetRelativeToMission (prefix, in GameHooks.StoryTriggers.cs)
    ///                                                        the officer's reading of a story line whose pilot is dead
    ///   GameManager.QueueRadioMessage                        a stock wingman line played (dialogue pacing)
    ///   GameManager.QueueCustomRadioMessage                  the same for the named stock calls
    ///
    ///   LOADING (Audio/LoadScreenPreload.cs)
    ///   LoadingScreenManager.PreloadMissionDigitalMusic      our clip files are read as one more step of the loading screen
    ///
    /// That is 13 patches.
    /// </summary>
    internal static partial class GameHooks
    {
        // Dialogue pacing counts a stock wingman line as the wing speaking, but its length is not known when the game
        // queues it: about this long per clip of a typed call, and for a named call.
        private const float StockClipSeconds = 1.2f;
        private const float StockNamedCallSeconds = 2.0f;

        // Extension points. Each is a no-op unless extra source files implement it.
        static partial void TraceShipDestroyed(ChatterSession session, Ship player, Ship victim, Ship killer, bool playerDied);
        static partial void TraceTrench(XMission mission, Vector3 position, bool inTrench, float exhaustDistance);

        // ------------------------------------------------------------------ lifecycle

        [HarmonyPatch(typeof(XwGameplaySituationHandler), nameof(XwGameplaySituationHandler.Initialize))]
        [HarmonyPostfix]
        private static void Initialize_Postfix(XwGameplaySituationHandler __instance)
        {
            try
            {
                var mission = Traverse.Create(__instance).Field("_mission").GetValue<XMission>() ?? GeneralManager.FlightManager?.Mission;
                if (mission == null) { Plugin.Log.LogWarning("[chatter] handler initialised but no mission"); return; }
                Plugin.Instance.BeginSession(mission);
            }
            catch (Exception e) { Plugin.Log.LogError($"[chatter] BeginSession failed: {e}"); }
        }

        [HarmonyPatch(typeof(XwGameplaySituationHandler), nameof(XwGameplaySituationHandler.Dispose))]
        [HarmonyPostfix]
        private static void Dispose_Postfix() => Plugin.Instance.EndSession();

        // ------------------------------------------------------------------ the game's own radio situations

        /// <summary>Typed situations. Returning false skips the game's handler, so its stock line stays quiet.</summary>
        [HarmonyPatch(typeof(XwGameplaySituationHandler), nameof(XwGameplaySituationHandler.ReportSituation))]
        [HarmonyPrefix]
        private static bool ReportSituation_Prefix(GameplaySituation situation)
        {
            try
            {
                var classified = Situations.Classify(situation);
                if (!classified.IsValid) return true;
                return !Plugin.Instance.TryHandle(classified.Id, classified.Sender, classified.Attacker, Plugin.OriginGameSituation);
            }
            catch (Exception e) { Plugin.Log.LogError($"[chatter] ReportSituation hook: {e}"); return true; }
        }

        /// <summary>Named custom radio messages (WingmanDestroyed, MCCHullCritical, ... - the game's XWVM-content path).</summary>
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.QueueCustomRadioMessage))]
        [HarmonyPrefix]
        private static bool QueueCustomRadioMessage_Prefix(string customMessageType, FlightSimEntity sender)
        {
            try
            {
                if (!Situations.CustomMessageTypes.TryGetValue(customMessageType ?? "", out var situationId)) return true;
                var ship = sender as Ship;
                // MCC* / warnings: the game picked "first friendly" as a stand-in sender; we choose our own speaker.
                if (Situations.PlayerWarnings.Contains(situationId)) ship = null;
                return !Plugin.Instance.TryHandle(situationId, ship, "custom:" + customMessageType);
            }
            catch (Exception e) { Plugin.Log.LogError($"[chatter] QueueCustomRadioMessage hook: {e}"); return true; }
        }

        // ------------------------------------------------------------------ the once-a-second look

        /// <summary>Runs inside the game's own mission step: Mission.ThinkMissionTriggers is the game's per-frame
        /// mission-condition pass, called by Mission.Update after the AI's Think() and after the parallel turret/rotation
        /// jobs have completed. Reading ship data from a free-running coroutine outside that step crashed the game with
        /// native heap corruption (see Plugin.EngagementTick).</summary>
        [HarmonyPatch(typeof(XMission), nameof(XMission.ThinkMissionTriggers))]
        [HarmonyPostfix]
        private static void ThinkMissionTriggers_Postfix(XMission __instance)
        {
            try
            {
                var session = Plugin.Session;
                if (session == null || Plugin.Instance == null || !ReferenceEquals(session.Mission, __instance)) return;
                Plugin.Instance.EngagementTick(session);
                ScriptedDeathTick(__instance);
                TrenchTick(__instance);
            }
            catch (Exception e) { Plugin.Log.LogError($"[chatter] engagement tick: {e}"); }
        }

        // ------------------------------------------------------------------ mission events

        /// <summary>Events the game raises no radio situation for. Each kind goes to its own method below.</summary>
        [HarmonyPatch(typeof(XMission), nameof(XMission.RegisterMissionEvent))]
        [HarmonyPostfix]
        private static void RegisterMissionEvent_Postfix(XMission __instance, Ev.MissionEvent missionEvent)
        {
            try
            {
                var plugin = Plugin.Instance; var session = Plugin.Session;
                if (session == null || !plugin.Enabled) return;
                var player = __instance.PlayerShip;
                if (!session.IsAwake) return;                                             // Radio.StartupQuietSeconds
                if (missionEvent.type == Ev.MissionEvent.EventType.WeaponFired) OnWeaponFired(session, player, missionEvent);
                else if (missionEvent.type == Ev.MissionEvent.EventType.WeaponHit) OnWeaponHit(plugin, session, player, missionEvent);
                else if (missionEvent is Ev.ArrivedMissionEvent arrived) OnArrival(plugin, session, __instance, player, arrived.flightGroup);
                else if (missionEvent is Ev.ShipDestroyedMissionEvent destroyed) OnShipDestroyed(plugin, session, __instance, player, destroyed.subject, destroyed.agent);
            }
            catch (Exception e) { Plugin.Log.LogError($"[chatter] RegisterMissionEvent hook: {e}"); }
        }

        /// <summary>Our flight firing counts as "engaged": the "here they come" call needs a flight that is not.</summary>
        private static void OnWeaponFired(ChatterSession session, Ship player, Ev.MissionEvent fired)
        {
            var shooter = fired.agent;
            if (shooter != null && player != null && (ReferenceEquals(shooter, player) || IsFlightmate(session, shooter)))
                session.NoteOwnFire((fired as Ev.WeaponFiredEvent)?.projectile?.targetEntity as Ship);
        }

        private static void OnWeaponHit(Plugin plugin, ChatterSession session, Ship player, Ev.MissionEvent hitEvent)
        {
            var target = hitEvent.subject; var shooter = hitEvent.agent;
            if (target == null || shooter == null) return;
            // for the save-thanks: remember which hostile last landed a hit on which wingman
            if (session.IsWingman(target) && shooter.Iff != player?.Iff)
                session.NoteWingmanAggressor(target, shooter);
            // torpedo hit: a flightmate's own warhead connects with a hostile (the game's projectile knows its weapon type)
            if (player != null && !ReferenceEquals(shooter, player) && IsFlightmate(session, shooter)
                && target.Iff != player.Iff && hitEvent is Ev.WeaponHitEvent weaponHit && ChatterSession.IsWarhead(weaponHit.projectile))
                plugin.TryHandle(Situations.TorpedoHit, shooter, null, "torpedo:" + ChatterSession.SafeDesignation(target));
        }

        /// <summary>A hostile flight group enters the sector (hyperspace or a carrier launch): a wingman calls it.</summary>
        private static void OnArrival(Plugin plugin, ChatterSession session, XMission mission, Ship player, FlightGroup flightGroup)
        {
            if (flightGroup == null || player == null || flightGroup.Iff == player.Iff) return;
            // The story script owns this moment: a VOICED mission message triggers on this group's arrival ("Another
            // freighter just arrived..."), so a wingman calling it too would announce it twice.
            if (ScriptVoicesArrival(mission, flightGroup)) { plugin.LogVerbose($"[chatter] arrival:{SafeFlightGroupName(flightGroup)} EnemyArrival: the story script voices this arrival; held"); return; }
            var classes = CraftClassesOf(flightGroup);
            string arrival = ArrivalTags(mission, flightGroup);
            var newOrMore = session.NoteArrival(classes);   // "new" the first time this class shows up, "more" after
            plugin.TryHandleTagged(Situations.EnemyArrival, classes, newOrMore, arrival, $"arrival:{SafeFlightGroupName(flightGroup)}");
        }

        /// <summary>How the group arrived, as line tags: "hyper", "launched" (it has a mothership), or null (it flies in
        /// from the sector edge; no arrival-tagged lines). A launch also carries what launched it: "launched-&lt;class&gt;"
        /// per class of the mothership's flight group, and "launched-deathstar" on the game's own Death Star surface
        /// missions (Mission.IsDeathStarMission - the surface hangar ports there are not a Star Destroyer). Lines tagged
        /// plain "launched" fit every launch; "launched-stardestroyer" only a Star Destroyer's.</summary>
        private static string ArrivalTags(XMission mission, FlightGroup flightGroup)
        {
            string arrival = flightGroup.arriveByHyperspace ? "hyper" : null;
            try
            {
                if (!flightGroup.arriveByHyperspace && flightGroup.MothershipFg != null)
                {
                    arrival = "launched";
                    if (mission.IsDeathStarMission) arrival += " launched-deathstar";
                    else foreach (var mothershipClass in CraftClassesOf(flightGroup.MothershipFg)) arrival += " launched-" + mothershipClass;
                }
            }
            catch { }
            return arrival;
        }

        /// <summary>The craft classes lines are tagged with, for a flight group's craft type.</summary>
        private static List<string> CraftClassesOf(FlightGroup flightGroup)
        {
            int craftTypeId = -1; bool isCapitalShip = false;
            try { craftTypeId = flightGroup.FlightGroupType.xwId; } catch { }
            try { isCapitalShip = flightGroup.IsCapitalShip; } catch { }
            return LineTags.CraftClasses(craftTypeId, isCapitalShip);
        }

        private static void OnShipDestroyed(Plugin plugin, ChatterSession session, XMission mission, Ship player, Ship victim, Ship killer)
        {
            WatchForScriptedDeathLine(mission, victim);                 // a scripted mayday plays on the explosion, not 5 s later
            if (victim == null || player == null) return;
            bool playerDied = ReferenceEquals(victim, player);
            TraceShipDestroyed(session, player, victim, killer, playerDied);
            // Every death gets its rattle. The game announces a wingman's death through its own radio call
            // (XwGameplaySituationHandler.HandleFellowCraftPainUpdates -> ShipSendCustomRadioMessage "WingmanDestroyed"),
            // but its spam filter (IsClipPlayedTooRecently, 10 s per call type) swallows every further death in that
            // window: four wingmen dying within 10 s got one call. A deliberate deviation from the game: we voice the
            // death from its kill event too. One rattle per ship (ClaimDeathRattle), whichever reports first; same side,
            // same craft kind as the player (the game's own condition for these calls).
            if (!playerDied && session.IsWingman(victim))
                plugin.TryHandle(Situations.WingmanDestroyed, victim, "kill:" + ChatterSession.SafeDesignation(victim));
            // kill count for boasts: a flightmate's hostile fighter kills this mission
            if (killer != null && !ReferenceEquals(killer, player) && IsFlightmate(session, killer)
                && victim.Iff != player.Iff && victim.Kind == Common.EntityKind.Fighter)
                session.NoteWingmanKill(killer);
            // "We lost Red Three": a wingman of the player's flight died (any killer) - a survivor laments a beat later
            if (!playerDied && IsFlightmate(session, victim))
                plugin.OnFlightmateLost(victim);
            if (!playerDied && ReferenceEquals(killer, player))
                OnPlayerKill(plugin, session, player, victim);
        }

        /// <summary>The player destroyed something: at most ONE reaction. Thanks from a wingman the kill saved, else a
        /// roll for "nice shot" with a cooldown. The game raises no radio situation for close-range player kills
        /// (CraftCongratulatesPlayerForKill needs more than 150 m), which is why this listens to the kill event.</summary>
        private static void OnPlayerKill(Plugin plugin, ChatterSession session, Ship player, Ship victim)
        {
            if (victim.Iff == player.Iff) return;                                   // no cheers for friendly fire
            var kind = victim.Kind;
            if (kind == Common.EntityKind.Mine || kind == Common.EntityKind.Object || kind == Common.EntityKind.Container) return;
            // if the victim was shooting a wingman moments ago, that wingman thanks the player - the better line for
            // the moment, so it takes precedence over the nice-shot roll
            var saved = session.WingmanSavedFrom(victim);
            if (saved != null)
            {
                plugin.LogVerbose($"[chatter] playersave: {ChatterSession.SafeDesignation(victim)} was on {ChatterSession.SafeDesignation(saved)} ({session.LastSaveReason})");
                if (plugin.TryHandle(Situations.SaveThanks, saved, null, "playersave:" + ChatterSession.SafeDesignation(victim))) return;
            }
            var sincePraise = session.SinceSpoken(Situations.NiceShot);
            if (sincePraise < plugin.NiceShotCooldown) { plugin.LogVerbose($"[chatter] playerkill {ChatterSession.SafeDesignation(victim)}: no cheer (praised {sincePraise:0} s ago; cooldown {plugin.NiceShotCooldown:0} s)"); return; }
            var chance = plugin.PlayerKillNiceShotChance;
            var roll = UnityEngine.Random.value;
            if (roll >= chance) { plugin.LogVerbose($"[chatter] playerkill {ChatterSession.SafeDesignation(victim)}: no cheer (roll {roll:0.00} >= {chance:0.00})"); return; }
            // no implicit sender -> any live wingman, own flight preferred
            plugin.TryHandle(Situations.NiceShot, null, null, "playerkill:" + ChatterSession.SafeDesignation(victim));
        }

        /// <summary>The game tells a craft when an AI fires at it (FighterShoot/TriggerWeapons -> target.NotifyFiredLaser(us))
        /// and keeps that for 1 s (the cockpit threat light). We remember hostile -> wingman for the save window.</summary>
        [HarmonyPatch(typeof(Ship), nameof(Ship.NotifyFiredLaser))]
        [HarmonyPostfix]
        private static void NotifyFiredLaser_Postfix(Ship __instance, FlightSimEntity other)
        {
            try
            {
                var session = Plugin.Session;
                if (session == null || !(other is Ship shooter) || __instance == null) return;
                var player = session.PlayerShipOrNull;
                if (player == null || shooter.Iff == player.Iff || !session.IsWingman(__instance)) return;
                session.NoteWingmanAggressor(__instance, shooter);
            }
            catch { }
        }

        // ------------------------------------------------------------------ the radio

        /// <summary>Story lines (sidecar messages) play through this; record it so chatter yields. __result false = the
        /// game could not locate the file (GameResourceLibrary.GetRelatedAssetPath), which nothing else logs.</summary>
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.QueueRadioMessageFromAssetRelativeToMission))]
        [HarmonyPostfix]
        private static void QueueRadioMessageFromAssetRelativeToMission_Postfix(string audioName, bool __result)
        {
            try { if (__result) Plugin.Session?.NoteNarrativeMessage(); } catch { }
            try { Plugin.Log.LogInfo($"[narrative] message audio {audioName}: {(__result ? "found, loading" : "NOT FOUND - text and beep only")}"); } catch { }
        }

        /// <summary>Every clip handed to the cockpit radio, story and chatter alike: how long the radio will be busy.
        /// (Radio.EnqueueCustomClip returns the queued clip, null when the radio has no clip under that name.)</summary>
        [HarmonyPatch(typeof(Cockpit.CockpitInstruments), nameof(Cockpit.CockpitInstruments.PlayCustomRadioMessage))]
        [HarmonyPostfix]
        private static void PlayCustomRadioMessage_Postfix(string name, AudioClip __result)
        {
            try
            {
                if (__result != null) Plugin.Session?.NoteRadioClip(__result.length);
                Plugin.Log.LogInfo(__result != null
                    ? $"[radio] queued {Path.GetFileName(name)} ({__result.length:0.0} s)"
                    : $"[radio] NOT QUEUED {name}: no clip under that name");
            }
            catch { }
        }

        /// <summary>Dialogue pacing: stock wingman lines the game itself voices (our prefix let them through) count as the
        /// wing speaking.</summary>
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.QueueRadioMessage))]
        [HarmonyPostfix]
        private static void QueueRadioMessage_Postfix(string[] clipSet, FlightSimEntity sender)
        {
            try
            {
                var session = Plugin.Session;
                if (session != null && sender is Ship ship && session.IsWingman(ship)) session.NoteWingLine(StockClipSeconds * Math.Max(1, clipSet?.Length ?? 1));
            }
            catch { }
        }

        /// <summary>As above for the named stock calls; death rattles are left out of the pacing. __runOriginal is
        /// false when our prefix took the call, so only lines the game really queued count.</summary>
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.QueueCustomRadioMessage))]
        [HarmonyPostfix]
        private static void QueueCustomRadioMessage_Postfix(string customMessageType, FlightSimEntity sender, bool __runOriginal)
        {
            try
            {
                var session = Plugin.Session;
                if (!__runOriginal || session == null || string.Equals(customMessageType, "WingmanDestroyed", StringComparison.OrdinalIgnoreCase)) return;
                if (sender is Ship ship && session.IsWingman(ship)) session.NoteWingLine(StockNamedCallSeconds);
            }
            catch { }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>A wingman in the player's own flight group.</summary>
        private static bool IsFlightmate(ChatterSession session, Ship ship) =>
            session.IsWingman(ship) && session.SameFlightGroupAsPlayer(ship);

        private static string SafeFlightGroupName(FlightGroup flightGroup)
        {
            try { return flightGroup?.Designation ?? "?"; } catch { return "?"; }
        }
    }
}
