# Situations

A situation is a moment in flight that a line can be written for. Its id is what goes in the `situation` column of
`config/lines.csv` and in `<line situation="...">`. This page lists every id the plugin knows (41).

Ids are matched without regard to case. Write them as shown.

## Rules that apply to every situation

Unless a row says otherwise:

| Rule | Detail | Setting (`[Radio]` in `xwvm.voices.chatter.cfg`) |
|---|---|---|
| Once per 10 s | A situation id that has spoken does not speak again for 10 s of mission time. | none (fixed) |
| Quiet start | Nothing is said in the first second of mission time. | `StartupQuietSeconds` (1) |
| Story first | Nothing is said for 8 s after a voiced story line of the mission starts. | `NarrativeHoldSeconds` (8) |
| Mission decided | When the primary goals are complete or failed, everything is quiet for at least 12 s. | `EndStateHoldSeconds` (12) |
| Silenced | A situation listed in the mission's `silence` attribute is never spoken there. | none (see `file-formats.md`) |
| Stale | A line that waits more than 8 s for the radio is dropped; so is one whose speaker dies meanwhile. | none (fixed) |
| Tags fall back | If a situation has tagged lines but none fits the moment, all its lines stay eligible. | none |
| No repeats | Lines not yet heard this mission are picked first, then the half heard longest ago. | none |
| Nothing to say | If the speaker's voice has no line, the game's own stock line plays (where it has one). | none |

"Low priority" in the table means: dropped while the radio is talking, or within `RadioGapSeconds` (2) of it going
quiet.

## Who speaks

| Term in the table | Meaning |
|---|---|
| Sender | The game names the craft that speaks. A line plays only if that craft is a friendly fighter of the player's craft kind (a wingman) and its voice has a line. |
| Sender (wingman or named ship) | As above, and a friendly non-fighter cast as a named ship may say it too, from its capital-ship lines (`role` `capital` in `lines.csv`). |
| Chosen | The plugin picks the speaker among those whose voice has a line: a live wingman of the player's flight group first, then any other live wingman, then the flight officer. A wingman who is leaving for hyperspace is not chosen. |
| Flightmate | A live wingman of the player's own flight group. Never the officer. |
| Officer | The mission's flight officer (speaker role `commander`). |

A voice says a situation only if it has a line recorded for it. The casting files written by
`pipeline/src/write_casting.py` also limit an officer to these ids: `UnderAttack`, `IncomingMissile`, `MCCAttacked`,
`MCCShieldsDown`, `MCCHullCritical`, `MCCDestroyed`, `DockingStart`, `DockingComplete`, `NiceShot`, `EnemyArrival`,
`Engagement`, `FlightLost`. A named transport or shuttle is limited to `PreparingHyper`, `DockingStart`,
`DockingComplete`.

The setting `OwnFlightOnly` (default `PreparingHyper,KillReport,NiceShot`) lists sender situations that are voiced
only when the sender is in the player's flight group; a wingman of another group stays silent for them. `*` means
every sender situation; empty means none.

## The table

In the trigger column, a name in code style without quotes is a game situation record passed to
`XwGameplaySituationHandler.ReportSituation`; a name in quotes is a message type passed to
`GameManager.QueueCustomRadioMessage`. "Starter" names the roles that have a line in the starter `lines.csv`.

| Id | Who says it | Trigger | To whom | Notes for the writer |
|---|---|---|---|---|
| `UnderAttack` | Chosen | The game reports laser fire at the player: `IncomingLaserFireTowardsPlayer`. | The player | Not held by a story line. Tags: `attacker` (class of the shooter), `threat` (`new`, `persistent`, `multiple`). Token `{player}`. Starter: wingman, commander. |
| `IncomingMissile` | Chosen | The game reports a missile at the player: `IncomingMissileTowardsPlayer`. | The player | Not held by a story line. No tag data is supplied, so leave it untagged. Token `{player}`. Starter: wingman, commander. |
| `AttackAck` | Sender | A wingman accepts "attack my target": `WingmanAcknowledgesAttackTarget`, "WingmanAttackTarget". | The player | Reply to the player's order. Not held by a story line. An order to the whole flight gets one reply (the 10 s rule). Token `{self}`. Starter: wingman. |
| `HelpAck` | Sender | A wingman accepts "cover me": `WingmanAcknowledgesComingToHelp`, "WingmanHelpAck". | The player | As `AttackAck`. Token `{self}`. Starter: wingman. |
| `IgnoreTarget` | Sender | A wingman accepts "ignore target": `WingmanIgnoringTarget`, "WingmanIgnoreTarget". | The player | As `AttackAck`. Token `{self}`. Starter: wingman. |
| `Evade` | Sender | A wingman accepts "evade": `CraftAcknowledgesEvading`, "WingmanEvade". | The player | As `AttackAck`. Token `{self}`. Starter: wingman. |
| `GoingHome` | Sender | A wingman accepts "go home": `CraftAcknowledgesGoingHome`, "WingmanHyperOut". | The player | As `AttackAck`. Token `{self}`. Starter: wingman. |
| `AwaitingOrders` | Sender | `CraftAwaitingOrders`. | The player | Treated as an order reply: not held by a story line. Token `{self}`. Starter: wingman. |
| `Proceeding` | Sender | `CraftProceedingWithMission`. | The player | Treated as an order reply: not held by a story line. Token `{self}`. Starter: wingman. |
| `PreparingHyper` | Sender (wingman or named ship) | A craft is about to jump out: `CraftReportsGoingToHyperspace` (raised by the game's `HyperOutTask.OnStart`), "WingmanPreparingHyperOut". | The flight | Wingmen: player's flight group only by default (`OwnFlightOnly`). Token `{self}`; a named ship says its own name. Starter: wingman, capital. |
| `KillReport` | Sender (wingman or named ship) | A friendly craft destroyed something: `CraftReportsVictory`. | The flight | Low priority. Spoken 1.0-2.0 s after the kill (`Delays`); dropped if the speaker dies first. Wingmen: player's flight group only by default (`OwnFlightOnly`). May be replaced by a `KillStreak` line. Starter: wingman, capital (capital line carries `{self}`). |
| `KillStreak2` `KillStreak3` `KillStreak4` `KillStreak5` | Sender | Plugin: replaces the `KillReport` of a wingman of the player's flight on that wingman's 2nd, 3rd, 4th or 5th hostile fighter kill of the mission, by chance `KillBoastChance` (0.4), and only if the wingman's voice has a line for that count. | The flight | Low priority. Same delay as `KillReport`. The line states the count. Named ships never boast. If the boast cannot be said, the plain `KillReport` is. Starter: wingman. |
| `KillBoastReply` | Flightmate (not the boaster) | Plugin: after a `KillStreak` line has played out, by chance `KillBoastReplyChance` (0.5), 0.5-1.0 s after it ends. | The boaster | A reply to the boast. Low priority. Held by a story line. Token `{wingmate}` (the boaster). Starter: wingman. |
| `NiceShot` | Chosen | Plugin: the player destroys a hostile craft (not a mine, container or object), by chance `PlayerKillNiceShotChance` (0.5), and not within `PlayerKillNiceShotCooldownSeconds` (60) of the last one. Skipped when the kill earns a `SaveThanks`. The game's own congratulation (`CraftCongratulatesPlayerForKill`) is always silenced. | The player | Low priority. Spoken 0.8-1.8 s after the kill (`Delays`). Token `{player}`. Starter: wingman, commander. |
| `SaveThanks` | Sender (the wingman who was saved) | Plugin: the player destroys a hostile that fired at or hit a wingman in the last 10 s. With `SaveCountsTargeting` on, also a hostile that merely had the wingman targeted within the game's threat distance. | The player | Spoken 1.0-2.2 s after the kill (`Delays`). Any friendly fighter, not only the player's flight. Token `{player}`. Starter: wingman. |
| `TorpedoHit` | Sender (the shooter) | Plugin: a torpedo or missile fired by a wingman of the player's flight hits a hostile (a weapon-hit event from `Mission.RegisterMissionEvent`). | The flight | Low priority. Starter: wingman. |
| `WingmanHelpRequest` | Sender | A wingman is taking hits: "WingmanHelpRequest". | The flight | Low priority only when the sender is not in the player's flight group. Token `{self}`. Starter: wingman. |
| `WingmanDestroyed` | Sender (the dying wingman) | A friendly fighter is destroyed: "WingmanDestroyed", or the ship-destroyed event from `Mission.RegisterMissionEvent`, whichever comes first. | Nobody in particular | One per ship. Exempt from the 10 s rule and from the story hold; never low priority; goes ahead of every waiting line and removes the same pilot's waiting lines. `pipeline/src/bake.py` cuts the clip off inside its last word and ends it in static, so write a line that can lose its ending. Starter: wingman. |
| `WingmanLost` | Flightmate (not the dead one) | Plugin: a wingman of the player's flight group is destroyed. By chance `WingmanLostChance` (1.0), after `WingmanLostDelay` (2.5-4.5 s). | The flight | Not said during a story hold. Tag `context`: `quiet` if no wingman has spoken for `WingmanLostQuietSeconds` (3.5), else `combat`; untagged lines fit both. Token `{lost}` (the dead wingman). Starter: wingman, tagged `quiet`. |
| `WingmanLostReply` | Flightmate with a lower seat number than the lamenter | Plugin: after a `WingmanLost` line has played out, by chance `WingmanLostReplyChance` (0.6), 0.6-1.2 s after it ends. | The lamenter | A reply to the lament. Keeps the lament's `context`. Not subject to the 10 s rule or the story hold. If nobody above the lamenter is alive, there is no reply. Token `{addressee}` (the lamenter). Starter: role `any`. |
| `FlightLost` | Officer | Plugin: the last live wingman of the player's flight group is destroyed. Spoken 4.5-6.5 s later. | The player | Once per mission. Waits up to 30 s for a story line to finish, then is dropped. Not subject to the 10 s rule. Token `{player}`. Starter: commander, as a callsign-only line (no plain take). |
| `EnemyArrival` | Chosen | Plugin: a hostile flight group arrives (an arrival event from `Mission.RegisterMissionEvent`). Skipped when a voiced story line of the mission triggers on that group's arrival. | The flight | Low priority. Tags: `attacker` (class of the arriving craft), `threat` (`new` the first time that class arrives, `more` after), `arrival` (`hyper`, `launched`, `launched-<class>`, `launched-deathstar`). Starter: wingman, commander. |
| `Engagement` | Chosen | Plugin: at least `EngagementMinCraft` (3) fighters of one hostile group have targeted the player's flight, the nearest is within `EngagementRange` (1000 m) and closing, and the flight has not fired or been fired at for `EngagementQuietSeconds` (15). Checked once a second inside `Mission.ThinkMissionTriggers`. | The flight | Low priority. Once per hostile group, at most `EngagementMaxPerMission` (2) per mission. No tag data is supplied, so leave it untagged. Starter: wingman, commander. |
| `MCCAttacked` | Chosen | A mission-critical craft is hit: "MCCAttacked". | The flight | Off for the rest of the mission once it is decided. Often in a mission's `silence` list. Starter: commander. |
| `MCCShieldsDown` | Chosen | A mission-critical craft loses its shields: "MCCShieldsDown". | The flight | As `MCCAttacked`. Starter: commander. |
| `MCCHullCritical` | Chosen | A mission-critical craft's hull is critical: "MCCHullCritical". | The flight | As `MCCAttacked`. Starter: commander. |
| `MCCDestroyed` | Chosen | A mission-critical craft is destroyed: "MCCDestroyed". | The flight | As `MCCAttacked`. Starter: commander. |
| `DockingStart` | Chosen, or Sender (named ship) | A boarding or docking begins: "MCCBoardingStart" (chosen speaker; the game sends it for a friendly mission-critical craft), or `CraftCommencesDockingOperation` (the docking craft is the sender). | The flight | Token `{self}` in the named-ship line. Starter: commander, capital. |
| `DockingComplete` | Chosen, or Sender (named ship) | A boarding or docking ends: "MCCBoardingComplete", or `CraftCompletesDockingOperation`. | The flight | As `DockingStart`. Starter: commander, capital. |
| `Captured` | Sender | `CraftCapturesAnotherCraft`. | Not determined | No starter line. |
| `Repaired` | Sender | `CraftIsRepaired`. | Not determined | No starter line. |
| `NotResponding` | Sender | `CraftNotRespondingToOrders`. The game's stock version is text only. | The player | No starter line. |
| `PrimaryComplete` | Chosen | `PrimaryMissionCompleted`. | The player | Starts the mission-decided quiet. No starter line. See Open points. |
| `PrimaryFailed` | Chosen | `PrimaryMissionFailed`. | The player | Starts the mission-decided quiet. No starter line. See Open points. |
| `SecondaryComplete` | Chosen | `SecondaryMissionCompleted`. | The player | No starter line. See Open points. |
| `BonusComplete` | Chosen | `BonusMissionCompleted`. | The player | No starter line. See Open points. |
| `ReinforcementsOk` | Chosen | `PlayerRequestReinforcementsSuccess`. | The player | No starter line. See Open points. |
| `ReinforcementsNone` | Chosen | `PlayerRequestReinforcementsFailure`. | The player | No starter line. See Open points. |

## Tags

A tag narrows a line to some moments. A line with no tags fits every moment. Each tag column may hold several
words, separated by spaces or commas; the line fits if any one of them matches. Where a line has more than one tag
column filled, all of them must match.

| Tag | Used by | Values |
|---|---|---|
| `attacker` | `UnderAttack`, `EnemyArrival` | Craft class words, below. |
| `threat` | `UnderAttack` | `new` (first warning about this attacker), `persistent` (the player was warned about the same attacker in the last 20 s), `multiple` (two or more craft fired at the player in the last 8 s). |
| `threat` | `EnemyArrival` | `new`, `more`. |
| `arrival` | `EnemyArrival` | `hyper` (arrived by hyperspace), `launched` (has a mothership), `launched-<class>` (class of the mothership, for example `launched-stardestroyer`), `launched-deathstar` (the game's Death Star surface missions). A group that flies in from the edge of the sector has no arrival value. |
| `context` | `WingmanLost`, `WingmanLostReply` | `quiet`, `combat`. |

Craft class words:

| Craft | Words |
|---|---|
| TIE Fighter | `tie`, `fighter` |
| TIE Interceptor | `tie`, `interceptor` |
| TIE Bomber | `tie`, `bomber` |
| TIE Advanced | `tie`, `advanced` |
| Assault Gunboat | `gunboat` |
| X-wing, Y-wing, A-wing, B-wing | `rebel` and `xwing`, `ywing`, `awing` or `bwing` |
| Transport, shuttle, freighter | `transport`, `shuttle`, `freighter` |
| Frigate, corvette, Star Destroyer | `frigate`, `corvette`, `stardestroyer` |
| Any craft the game flags as a big ship | also `capital` |
| Anything else | `other` |

Because any one word is enough, `attacker` = `tie interceptor` fits every TIE. For interceptors only, write
`interceptor`.

## Callsign tokens

A token in the `text` column of `lines.csv` marks where a callsign is spoken.

| Token | Stands for | `kind` in `callsigns.xml` | Used in the starter script by |
|---|---|---|---|
| `{player}` | The player's callsign ("Red 2") | `address` | `UnderAttack`, `IncomingMissile`, `NiceShot`, `SaveThanks`, `FlightLost` |
| `{self}` | The speaker's own callsign; for a named ship, its flight group's name | `selfid` | `AttackAck`, `HelpAck`, `IgnoreTarget`, `Evade`, `GoingHome`, `AwaitingOrders`, `Proceeding`, `PreparingHyper`, `WingmanHelpRequest`, and the capital-ship lines of `KillReport`, `PreparingHyper`, `DockingStart`, `DockingComplete` |
| `{lost}` | The flightmate who just died | `lost` | `WingmanLost` |
| `{addressee}` | The flightmate being answered (the lamenter) | `reply` | `WingmanLostReply` |
| `{wingmate}` | The flightmate being teased (the boaster) | `mate` | `KillBoastReply` |

How a token becomes sound:

- Every line is recorded once without any callsign: the plain line. Its subtitle is the text with the token taken
  out, or the `tts` column when that is filled.
- A recording of the same line with a real callsign in it ("Red Two, break off!") is a callsign version of that
  line. There is one clip per line, kind and seat, listed in the voice's `callsigns.xml`.
- In flight the plugin picks a plain line first. If the speaker's voice has a version of that line for the seat
  being named, the version always plays in place of the plain take. If not, the plain take plays.
- A line marked `callsign_only` has no plain take. It is offered only to a speaker whose voice has its `address`
  version for the player's seat or its `selfid` version for the speaker's own seat; otherwise it is skipped.
  `WingmanLost`, `WingmanLostReply` and `KillBoastReply` never use callsign-only lines.

Where each kind is swapped in:

| Kind | Swapped in for | Callsign it must match |
|---|---|---|
| `address` | Any line that goes through the main pipeline (every situation except `WingmanLost`, `WingmanLostReply`, `KillBoastReply`), and `FlightLost` | The player's seat |
| `selfid` | The same lines, when the speaker is a craft (not the officer) | The speaker's own seat, or a named ship's `callsign` |
| `lost` | `WingmanLost` | The dead flightmate's seat |
| `reply` | `WingmanLostReply` | The lamenter's seat |
| `mate` | `KillBoastReply` | The boaster's seat |

Further rules the code applies:

- For `UnderAttack`, `IncomingMissile`, `NiceShot` and `SaveThanks`, a speaker whose voice has any `address` version
  for the player's seat is preferred over one of the same standing whose voice has none.
- When a sender's voice has `selfid` versions of some of a situation's lines for that seat, only those lines are
  picked.
- "Red 1", "Red Leader" and "Red" are the same seat. Case does not matter.
- A craft that shares the player's callsign never says a name, and no line says the player's callsign about another
  craft.

## Open points

- The kit holds no game code. For situations raised by the game, the exact conditions (how much damage raises
  "WingmanHelpRequest", when `CraftAwaitingOrders`, `CraftProceedingWithMission`, `CraftCapturesAnotherCraft`,
  `CraftIsRepaired` and `CraftNotRespondingToOrders` are raised, and to whom the game means them) could not be
  determined from this repository. The "To whom" entries for those rows are inferred from the starter lines or left
  as "Not determined".
- `GoingHome`, `PreparingHyper`, `DockingStart` and `DockingComplete` each arrive by two routes (a situation record
  and a named message). Which route the game uses in which mode could not be determined here.
- `PrimaryComplete` and `PrimaryFailed`: the pipeline lets them past the mission-decided check, but the play step
  checks the same hold again and it has just begun, so as the code reads a line for either would be dropped and the
  game's own handling would follow. Not verified in flight.
- `PrimaryComplete`, `PrimaryFailed`, `SecondaryComplete`, `BonusComplete`, `ReinforcementsOk`,
  `ReinforcementsNone`: the starter script has no lines, and the officer cast by `write_casting.py` is not given
  these ids, so only a wingman's voice could say them. Untested by anything in the repository.
- `Captured`, `Repaired`, `NotResponding`: mapped and resolvable, but the starter script has no lines and nothing
  in the repository exercises them.
- The plugin also knows a speaker role `astromech` (a last fallback for "Chosen" situations when the player's craft
  has an astromech). No build step writes such a speaker, and no starter line has that role.
- `KillBoastReply` is not checked against the mission's `silence` list; `WingmanLost`, `WingmanLostReply` and
  `FlightLost` are.
- A `KillStreak` line is checked against its own 10 s window, not against the `KillReport` window, so a boast can
  follow a kill report inside 10 s. It then marks `KillReport` as spoken.
