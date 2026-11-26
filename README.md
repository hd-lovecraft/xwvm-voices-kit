# XWVM Voices kit

The code behind dynamic wingman chatter for XWVM, without any voices. Bring your own. Also includes plugin source for dynamic voices.

| Folder | What it is |
|---|---|
| `plugin/` | The BepInEx plugin (`XwvmChatter`) and its unit tests. |
| `pipeline/src/` | The build steps: add the radio sound to your recordings, write the files the plugin reads. |
| `pipeline/config/` | Example input: placeholder voice names, a starter script of one line per situation, one cast mission. |
| `docs/` | Reference: [every situation](docs/situations.md) a line can be written for, and [every file format](docs/file-formats.md). |

Nothing here contains game code or audio. The plugin compiles against the game's own assemblies in your install.
How you choose which takes to keep is your call.

* Game: XWVM alpha (Unity 6, Mono). Loader: BepInEx 5.4.23.5 x64; the plugin patches with Harmony.
* What a player installs is one DLL, `XwvmChatter.dll`, in the game's `BepInEx\plugins`. Everything else is data in
  `%APPDATA%\XWVM\GlobalSidecars\xwing\`: plain XML and clips, which the game itself never opens.

## How a line gets said

1. **The game reports a situation.** Mostly through its own `XwGameplaySituationHandler.ReportSituation`; a few
   come from mission events the game raises no radio call for (a kill, a hit, an arrival).
2. **The pipeline decides whether to speak** (`Plugin.Pipeline.cs`): not before the mission has started, not over a
   story line, not once the mission is decided, not while the radio is busy if the call is only colour, and each
   situation at most once in 10 seconds.
3. **A speaker is found** (`ChatterSession.Resolve.cs`). Either the game names the sender (the wingman who took the
   order or got the kill), or it is a remark to the player and any wingman on the radio may make it: the player's
   own flight first, then other flights, then the tour's flight officer.
4. **A line is picked** from that speaker's voice: the lines for the situation, narrowed by tags (what is
   attacking, what arrived), least recently heard first.
5. **A callsign version is swapped in** when the voice has one for the seat being named
   ("Red Two, break off!").
6. **It plays through the game's radio**, queued behind whatever is already on it, with a subtitle.

If any step finds nothing to say, the game's stock line plays as it would without the mod.

## What the plugin reads

| File | What it holds |
|---|---|
| `_chatter/casting.xml` | Per mission: which seat has which voice (`<speaker designation="Red 2" voice="...">`). |
| `_chatter/generic.xml` | For every mission: the pool of voices for wingmen nobody cast, and a default officer. |
| `VOICE/_chatter/<voice>/bank.xml` | That voice's lines: situation, clip name, subtitle, optional tags. |
| `VOICE/_chatter/<voice>/callsigns.xml` | That voice's lines recorded with a callsign in them. |
| `VOICE/_chatter/<voice>/*.ogg` | The clips. |

The build below writes all of it. Every element and attribute is in [docs/file-formats.md](docs/file-formats.md).

## The plugin's code

In `plugin/XwvmChatter/`:

| Folder | What is in it | Touches the game? |
|---|---|---|
| `Data/` | Reading the XML (`ChatterConfig`, `CallsignMap`) and the list of situations (`Situations`). | Only `Situations` |
| `Logic/` | The rules as plain functions: dealing pool voices (`PoolDeal`), not repeating lines (`LineHistory`), tags (`LineTags`), finding callsign versions (`CallsignVersions`), reading the delay settings (`SettingText`), what the loading-screen plan is built from (`PreloadRules`). | No |
| `Session/` | `ChatterSession`, one per mission, split by concern: `World`, `Casting`, `Resolve`, `Callsigns`, `Memory`, `Radio`, `Clips`, `Play`. | Yes |
| `Hooks/` | `GameHooks.cs`: the Harmony patches, with a list of all of them at the top of the file. `GameHooks.StoryTriggers.cs` (the same class): help for the mission's own story lines. | Yes |
| `Audio/` | Reading clip files on the loading screen (`LoadScreenPreload`), holding them until the mission starts (`ClipCache`) and decoding them (`OggDirect`). | Yes |
| `Plugin*.cs` | Entry point and settings (`Plugin.cs`); the pipeline (`Plugin.Pipeline.cs`); the reactions the game has no call for (`Plugin.Reactions.cs`). | Yes |

Where the plugin attaches to the game is listed at the top of `Hooks/GameHooks.cs`: 13 patches.

Reading the comments: one that names a game method (`Mission.ThinkMissionTriggers`,
`SidecarLoader.MatchesMissionName`) says which game behaviour the code follows. The rule is to reuse the game's own
practice, to name the game method in the comment, and to say so where the plugin deviates.

## Build and test the plugin

```
dotnet test                                        in plugin\XwvmChatter.Tests   unit tests (no game needed)
dotnet build -c Release -p:GamePath="D:\XWVM"      in plugin\XwvmChatter         the DLL to ship; never copied anywhere
dotnet build -c Debug                              in plugin\XwvmChatter         the same plugin; copies itself into the game's BepInEx\plugins
dotnet msbuild -t:Compile                          in plugin\XwvmChatter         compile check only; nothing is copied
```

Building needs the game installed, for its assemblies; pass `GamePath` only if it is not in the default folder. The
tests do not need it: they compile the plugin's `Logic/` and `Data/` source files directly (all but `Situations`,
which uses the game's types).

* **Release build**: the DLL is `bin\Release\netstandard2.1\XwvmChatter.dll`. This is what ships.
* **Debug build**: the same plugin. It logs its version with `-dev` and copies itself into the game's
  `BepInEx\plugins`. Setting the `ChatterExtraSources` property to a folder compiles the source files in it into a
  Debug build, never into a Release build.
* The version is written once, in `XwvmChatter.csproj` `<Version>`.
* Settings: `BepInEx\config\xwvm.voices.chatter.cfg`, created on first run. Each entry explains itself.

What the tests do not cover: anything in `Session/`, `Hooks/`, `Audio/` and `Plugin*.cs` needs the game's types and
is checked by flying a mission and reading `BepInEx\LogOutput.log`. Every decision is logged with its reason, and
tools read that wording, so treat a log message as an interface.

## Build a voice

Set up once, from `pipeline/`:

```
python -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt
copy .env.example .env
```

Then fill in `.env`: the folders on your machine. You also need `ffmpeg`. Run the commands below from `pipeline/`
with `.venv\Scripts\python`.

| Step | Command | Action |
|---|---|---|
| 1 | edit `config/casting.yaml` | Your voices, by name. |
| 2 | edit `config/lines.csv` | The script: one row per line, with its situation, role and tags. |
| 3 | your own step | Record or synthesize each line, dry, as `raw/<voice>/<Situation>-<variant>.wav`. |
| 4 | `src/make_beds.py` | Once: a plain set of radio static beds, unless you have your own. |
| 5 | `src/build_voice.py --voice "<name>"` | Radio treatment and static over every raw take; clips and `voice-bank.xml` into the voice's folder. |
| 6 | `src/write_casting.py --all` | `bank.xml` per voice, and who flies where (`config/missions/*.yaml`) into the casting XML. |

Output lands in `content/xwing/`, laid out as the game's `%APPDATA%\XWVM\GlobalSidecars\xwing` folder.

A raw take is, for example, `raw/iris-calder/KillReport-1.wav`; more takes of the line add a suffix
(`KillReport-1-b.wav`). Step 5 keeps every take it finds, so listen to the raw takes first and delete the ones you do
not want. A rebuild numbers the takes that are left afresh; if that leaves a clip of the earlier build in the voice's
folder, step 5 names it for you to delete.

The radio sound is deliberately plain: a narrow band, some grit, an even level (`src/radio.py`, four settings in
`config/radio.yaml`), over generated static. Make it yours: put your own beds in the beds folder, and name your own
treatment in `.env` (`RADIO_TREATMENT=my_radio:treat`, a function that takes and returns one take as samples).

In `lines.csv` the text may carry a callsign token (`{player}`, `{self}`, `{lost}`, `{addressee}`, `{wingmate}`); the
plain clip is recorded without it, and its subtitle is the text with the token taken out ("Copy. {self} engaging
your target." shows as "Copy. Engaging your target."). Where that does not read right, write the plain wording in the
`tts` column ("We lost {lost}." with `tts` "We lost one."): it is used as the subtitle, and it is what to record.

The build reads `situation`, `variant`, `role`, `text`, `tts`, the tags `attacker`, `threat`, `arrival` and
`context`, and `callsign_only`; the other columns are notes for whoever, or whatever, records the line.

`build_voice.py` builds the plain clips. A line recorded with a callsign in it ("Red Two, break off!"), which is the
only form a `callsign_only` line has, is your own step: `bank.callsign_clip_name` names the clip and
`bank.write_callsigns` lists it for the plugin. The same goes for a distress line with an alarm under it
(`bake.bake_one(..., distress=...)`, which needs the two alarm sounds named in `.env`).


## Credits

The plugin embeds [NVorbis](https://github.com/NVorbis/NVorbis) (MIT licence) to decode Ogg Vorbis clips.

## Licence

MIT; see `LICENSE`. It covers the code in this repository, not any voices or recordings you build with it.
