# File formats

Every file a voice-maker or tool-writer produces or can read. Situation ids, tags and callsign tokens are explained
in `situations.md`.

## Overview

Paths under `pipeline/` are input and working files. Paths under `content/xwing/` are the output; that folder is
laid out as the game's sidecar folder (`%APPDATA%\XWVM\GlobalSidecars\xwing`, or the plugin setting
`General.SidecarDir`).

| File | Written by | Read by |
|---|---|---|
| `pipeline/config/lines.csv` | you | `build_voice.py`, `bank.py`, `write_casting.py` |
| `pipeline/config/casting.yaml` | you | `write_casting.py` |
| `pipeline/config/missions/<MISSION>.yaml` | you | `write_casting.py` |
| `pipeline/config/tours.yaml` | you | `write_casting.py` |
| `pipeline/config/default_officer.yaml` | you | `write_casting.py` |
| `pipeline/config/ship_voices.yaml` | you | `write_casting.py` |
| `pipeline/raw/<voice>/*.wav` | you | `build_voice.py` |
| `content/xwing/VOICE/_chatter/<voice>/*.ogg` | `build_voice.py`; you, for callsign versions | plugin |
| `content/xwing/VOICE/_chatter/<voice>/voice-bank.xml` | `build_voice.py` (`bank.write_voice_bank`) | `write_casting.py` |
| `content/xwing/VOICE/_chatter/<voice>/callsigns.xml` | you (`bank.write_callsigns`) | plugin, `write_casting.py` |
| `content/xwing/VOICE/_chatter/<voice>/bank.xml` | `write_casting.py` | plugin |
| `content/xwing/_chatter/casting.xml` | `write_casting.py` | plugin |
| `content/xwing/_chatter/sandbox.xml` | `write_casting.py` | plugin |
| `content/xwing/_chatter/generic.xml` | `write_casting.py` | plugin |

The scripts are in `pipeline/src/`. The radio treatment (`radio.py`) and the static, levelling and encoding
(`bake.py`) have settings files of their own that are not covered here.

## Names

### Voice name to folder name (the slug)

A voice is known by its name ("Iris Calder") in the YAML files and in `voice="..."`. Its folder is the slug of that
name: lower case, every run of characters other than `a`-`z` and `0`-`9` becomes one dash, and dashes at either end
are dropped.

| Name | Folder |
|---|---|
| `Iris Calder` | `iris-calder` |
| `Jon Marek` | `jon-marek` |
| `  Mira   Kessel!  ` | `mira-kessel` |

The plugin (`ChatterConfig.Slug`) and the build (`common.slug`) apply the same rule. The same slug turns a callsign
into the seat part of a clip name (`Red 2` becomes `red-2`).

### How a mission is matched

The plugin takes the name of the mission file being flown, without folder or extension (`TIEDOGFIGHT`), and compares
it with the `target` attribute of each `<mission>` and `<chatter-defaults>` element.

| In `target` | Meaning |
|---|---|
| `*` | any run of characters, including none |
| `?` | exactly one character |
| a vertical bar between two patterns | alternatives: either pattern may match |
| anything else | itself; case does not matter |

A `<mission>` without `target` matches nothing. A `<chatter-defaults>` without `target` matches every mission. With
alternatives, only the first is tied to the start of the name and only the last to its end (`RED1|GOLD1` also
matches `RED1-LONGER` and `PREFIX-GOLD1`); this follows the game's `SidecarLoader.MatchesMissionName`. `write_casting.py` writes
one exact mission name per `<mission>` and `*` for the defaults.

### How a seat is matched

A `designation` in a casting file is compared with each friendly craft's designation in flight, without regard to
case and ignoring a pilot name in brackets after it. "Red 1", "Red Leader" and "Red" are the same seat.

## Input files

### `config/lines.csv`

The script: one row per line. Comma-separated, UTF-8, with a header row.

```csv
situation,variant,voice,role,text,emotion,notes,direction,attacker,threat,tts,arrival,speed,context,callsign_only
UnderAttack,1,,wingman,{player}! Break off! He's on your tail!,urgent,,,,,,,,,
UnderAttack,40,,commander,"{player}, you've got a fighter on your tail!",urgent,,,,new,,,,,
WingmanLost,1,,wingman,We lost {lost}.,grave,,,,,We lost one.,,,quiet,
KillReport,5,,capital,{self} here. Target destroyed.,calm,,"[calm, clipped]",,,Target destroyed.,,,,
FlightLost,1,,commander,"{player}, the rest of your flight is gone.",steady,,,,,,,,,yes
```

| Column | Required | Read by the build | Meaning |
|---|---|---|---|
| `situation` | yes | yes | Situation id. Letters and digits, starting with a letter. |
| `variant` | yes | yes | A whole number. `situation` + `variant` identify the line and name its clips. |
| `text` | yes | yes | The words, with callsign tokens where a callsign is spoken. |
| `tts` | no | yes | The plain wording, when the text does not read right with its token removed. Used as the plain clip's subtitle, and it is what to record for the plain take. |
| `role` | no | yes, one value | `capital` marks a capital-ship line (said only by named ships). Any other value (`wingman`, `commander`, `any`) is a note: it tells you which voices should record the line. |
| `attacker` | no | yes | Tag, copied to the bank. |
| `threat` | no | yes | Tag, copied to the bank. |
| `arrival` | no | yes | Tag, copied to the bank. |
| `context` | no | yes | Tag, copied to the bank. |
| `callsign_only` | no | yes | `yes`: the line exists only as callsign versions; no plain take is recorded. |
| `voice`, `emotion`, `notes`, `direction`, `speed` | no | no | Notes for whoever, or whatever, records the line. |

How the subtitle of a plain clip is made (`script.plain_text`, then `write_casting.py`):

1. The `tts` column if it is filled; otherwise the `text` with "This is {self}" and its punctuation removed, every
   token removed (a token after a comma goes with the comma; any other token takes the punctuation after it), and
   each sentence starting with a capital. "Copy. {self} engaging your target." becomes "Copy. Engaging your target."
2. Anything in square brackets is removed, so recording directions never show.

### `config/casting.yaml`

The voices, by name.

```yaml
voices:
  Iris Calder: {}
  Jon Marek: {}
  Mira Kessel:
    generic: true
```

| Key | Required | Meaning |
|---|---|---|
| `voices` | yes | Map of voice name to its settings. Must be a map. |
| `voices.<name>.generic` | no | `true`: the voice is in the pool for wingmen no mission casts. |
| `aliases` | no | Map of `Other Name: Listed Name`. Read by `script.load_casting` only; see Open points. |

Any other key under a voice is ignored by the build, so a recording step may keep its own settings there.

### `config/missions/<MISSION>.yaml`

Who flies where in one mission. One file per mission you cast.

```yaml
mission: TIEDOGFIGHT
commander: null
cast:
  Red 2: Iris Calder
  Red 3: Jon Marek
silent: [Red 4]
sandbox: true
```

| Key | Required | Meaning |
|---|---|---|
| `mission` | yes | The mission file's name without extension. The file's own name is not used for this. |
| `cast` | no | Map of seat ("Red 2") to voice name. Every seat needs a voice name; an empty one stops the run. |
| `commander` | no | Voice name of the mission's flight officer. `null` or absent: the officer of the mission's tour. |
| `silent` | no | List of seats that are held but never speak: no pool voice is given to them and their craft says nothing, stock voice included. |
| `sandbox` | no | `true`: a test mission. Its block goes to `sandbox.xml` and not to `casting.xml`. |

A fighter seat that is not listed takes a voice from the generic pool in flight.

### `config/tours.yaml`

Each tour's flight officer and missions.

```yaml
tours:
- tour: Example tour
  officer: Mira Kessel
  missions:
  - TIEDOGFIGHT
variants: {}
```

| Key | Required | Meaning |
|---|---|---|
| `tours` | no | List of tours. |
| `tours[].officer` | no | Voice name of the tour's flight officer. |
| `tours[].missions` | no | Mission names of the tour. |
| `tours[].tour` | no | A label. Not read by the build. |
| `variants` | no | Map of `VARIANT: LISTED`: another file of a listed mission; it takes that mission's officer. |

A mission listed here that has no file in `config/missions/` still gets a `<mission>` block with its tour's officer,
its named ships and its `silence` list, provided the officer's voice has been built.

### `config/default_officer.yaml`

The officer of any mission that casts none of its own.

```yaml
officer: Mira Kessel
exclude_situations: [MCCAttacked, MCCShieldsDown, MCCHullCritical, MCCDestroyed]
```

| Key | Required | Meaning |
|---|---|---|
| `officer` | no | Voice name. Left out of the output if the voice has not been built. |
| `exclude_situations` | no | Situation ids this officer never voices. |

The default officer is written into `generic.xml`, and that file is written only when at least one `generic: true`
voice has been built.

### `config/ship_voices.yaml`

Named friendly ships that speak, one fixed voice per ship.

```yaml
ships:
  Liberty: {voice: Mira Kessel}
  Hope: {voice: Jon Marek, per_mission: {TIEDOGFIGHT: Iris Calder}}
```

| Key | Required | Meaning |
|---|---|---|
| `ships` | no | Map of ship name to its entry. |
| `ships.<name>` | | The flight group's name in the mission file, in display case: each word and each part between hyphens capitalised, Roman numerals kept in capitals (`LIBERTY` is `Liberty`, `SOR-SULL II` is `Sor-Sull II`). |
| `ships.<name>.voice` | yes | Voice name. |
| `ships.<name>.per_mission` | no | Map of mission name to a voice that replaces `voice` in that mission. |

A ship gets a speaker only if all of these hold: the mission file is found (see `XWING_GAME_DIR` in
`pipeline/.env.example`; custom missions are looked for under `%APPDATA%\XWVM\CustomMissions\xwing`), the flight
group is a freighter, cruiser, frigate, corvette, transport or shuttle on the friendly side, it is listed here, and
the voice has at least one capital-ship line built. A flight group of several ships gets one speaker per ship
("Liberty 1", "Liberty 2"), all sharing one set of lines and one callsign.

## Raw takes

`pipeline/raw/<voice slug>/<Situation>-<variant>[-suffix].wav`

```
raw/iris-calder/KillReport-1.wav
raw/iris-calder/KillReport-1-b.wav
raw/iris-calder/UnderAttack-1.wav
```

| Part | Rule |
|---|---|
| Folder | The slug of the voice name; or any folder given with `build_voice.py --raw`. |
| `<Situation>-<variant>` | Must be a row of `lines.csv`. A file that is not is skipped with a message. |
| `-suffix` | Optional, anything. Each file is one take of the line. |
| Audio | A dry recording of the plain wording, as a `.wav` file. Any sample rate and channel count that `ffmpeg` reads; it is converted to mono 48 kHz. |

You write them; `build_voice.py` reads them. Every take found is built, so delete the ones you do not want first.
Takes of a line are numbered `t1`, `t2`, ... in file-name order.

`build_voice.py` treats every raw file as a plain take. A recording that has a callsign in it must not be put in
this folder; see callsign versions below.

## A voice's folder

`content/xwing/VOICE/_chatter/<voice slug>/`

```
VOICE/_chatter/iris-calder/
  KillReport-1-t1.ogg
  KillReport-1-t2.ogg
  UnderAttack-1-t1.ogg
  UnderAttack-1-v4--address--red-2.ogg
  voice-bank.xml
  callsigns.xml
  bank.xml
```

A folder whose name starts with an underscore is not treated as a voice by `write_casting.py`.

### Clip names

| Clip | Name | Written by |
|---|---|---|
| Plain take | `<Situation>-<variant>-<take>.ogg`, take = `t1`, `t2`, ... | `build_voice.py` |
| Callsign version | `<Situation>-<variant>-v4--<kind>--<seat>.ogg` | you (`bank.callsign_clip_name` gives the name) |

- `<kind>` is `address`, `selfid`, `lost`, `reply` or `mate` (see `situations.md`).
- `<seat>` is the slug of the callsign: `red-2`, `gold-1`, `liberty`.
- The `-v4` and the two double dashes are fixed text.
- A callsign version belongs to a line, not to one take. The plugin finds the versions of a line by the start of the
  name, `<Situation>-<variant>-`, among the clips listed in the same folder's `callsigns.xml`.
- Clips are Ogg Vorbis. `build_voice.py` writes mono, 48 kHz.

`build_voice.py` makes plain clips only. Making a callsign version (radio treatment, static, encoding, naming, and
listing it in `callsigns.xml`) is your own step, using the functions in `bank.py` and `bake.py`.

### `voice-bank.xml`

The list of plain clips `build_voice.py` built. An intermediate file: `write_casting.py` reads it to write
`bank.xml`. The plugin never reads it.

```xml
<line situation="KillReport" audio="KillReport-1-t1">Got 'em. Splash one!</line>
<line situation="UnderAttack" audio="UnderAttack-40-t1" threat="new">You've got a fighter on your tail!</line>
<line situation="WingmanLost" audio="WingmanLost-1-t1" context="quiet">We lost one.</line>
```

There is no XML header and no root element: the file is one `<line>` per row.

| Attribute | Required | Meaning |
|---|---|---|
| `situation` | yes | Situation id. |
| `audio` | yes | Clip name without `.ogg`. |
| `attacker`, `threat`, `arrival`, `context` | no | The line's tags from `lines.csv`; a tag with no value is left out. |
| element text | yes | The subtitle, XML-escaped. |

`write_casting.py` reads this file with a pattern, not an XML parser. A tool that writes it must keep each element
on one row, use double quotes, and put `situation` first and `audio` second.

### `bank.xml`

The voice's lines as the plugin reads them. Written by `write_casting.py` for every voice folder that has a
`voice-bank.xml`.

```xml
<?xml version="1.0" encoding="utf-8"?>
<bank>
  <line situation="KillReport" audio="KillReport-1-t1">Got 'em. Splash one!</line>
  <line situation="UnderAttack" audio="UnderAttack-40-t1" threat="new">You've got a fighter on your tail!</line>
  <line situation="WingmanLost" audio="WingmanLost-1-t1" context="quiet">We lost one.</line>
  <line situation="FlightLost" audio="FlightLost-1-v4" callsignOnly="true">Red Two, your flight is gone.</line>
  <line situation="KillReport" audio="KillReport-5-t1" role="capital">Target destroyed.</line>
</bank>
```

The plugin does not check the root element's name; it reads the `<line>` children.

| Attribute of `<line>` | Required | Plugin reads | Build writes | Meaning |
|---|---|---|---|---|
| `situation` | yes | yes | yes | Situation id. A line without one is skipped and logged. |
| `audio` | no | yes | yes | Clip name in the voice's folder. Without a dot in it, `.ogg` is added. A line whose clip file is missing is skipped and logged. Without `audio` the line is words only: its subtitle is shown and nothing plays. |
| `callsignOnly` | no | yes | yes | `true`: the line has no plain clip. `audio` then names the line (`<Situation>-<variant>-v4`), not a file, and the line is offered only where a callsign version fits. |
| `role` | no | yes | yes | `capital`: a capital-ship line, given only to speakers with `lines="capital"`. Every other line is given only to the other speakers. |
| `attacker`, `threat`, `arrival`, `context` | no | yes | yes | Tags. Several values separated by spaces or commas; read in lower case. |
| element text | no | yes | yes | The subtitle. Rows are trimmed and joined with single spaces. |

Order written: plain lines, then one entry per callsign-only line the voice has a version of, then capital-ship
lines. A line is a capital-ship line when its `lines.csv` row has `role` `capital`; a callsign-only entry is added
when the row has `callsign_only` `yes` and `callsigns.xml` lists an `address` or `selfid` version of it.

### `callsigns.xml`

The voice's callsign versions. You write it (`bank.write_callsigns`); the plugin reads it, finding it through the
folder the voice's clips are in.

```xml
<?xml version="1.0" encoding="utf-8"?>
<callsigns version="1">
  <composed kind="address" callsign="Red 2" situation="UnderAttack" audio="UnderAttack-1-v4--address--red-2.ogg">Red Two! Break off!</composed>
  <composed kind="selfid" callsign="Red 3" situation="AttackAck" audio="AttackAck-1-v4--selfid--red-3.ogg">Copy. Red Three engaging.</composed>
  <composed kind="lost" callsign="Red 3" situation="WingmanLost" audio="WingmanLost-1-v4--lost--red-3.ogg">We lost Red Three.</composed>
</callsigns>
```

The plugin does not check the root element's name and reads only the `<composed>` children.

| Attribute of `<composed>` | Required | Plugin reads | Build writes | Meaning |
|---|---|---|---|---|
| `audio` | yes | yes | yes | Clip file name with its extension, relative to the voice's folder. An entry whose file is missing is left out and logged. |
| `callsign` | yes | yes | yes | The callsign said, as the game writes it ("Red 2"). "Red 1", "Red Leader" and "Red" are equal. |
| `kind` | no | yes | yes | `address` (default), `selfid`, `lost`, `reply`, `mate`. Read in lower case. |
| `situation` | no for the plugin | stored, not used | yes | Situation id of the line. `write_casting.py` needs it to list callsign-only lines. |
| element text | no | yes | yes | The subtitle of this version, with the callsign in it. |

| Attribute of `<callsigns>` | Plugin reads | Build writes |
|---|---|---|
| `version` | no | yes (`1`) |

`write_casting.py` also reads this file with patterns. A tool that writes it must keep each element on one row, use
double quotes, put `kind` first, and put `situation` directly before `audio`, as in the example.

A callsign version plays only in place of a line the plugin has already chosen. A line that has versions but no
plain take in `voice-bank.xml`, and is not `callsign_only`, never plays; `write_casting.py` prints a warning naming
such lines.

## The casting files

`content/xwing/_chatter/casting.xml`, `sandbox.xml`, `generic.xml`

All three have the same shape. They only cast: each `<speaker voice="...">` takes its lines from the `bank.xml` of
that voice. The game itself never opens this folder.

| File | Holds | Written when |
|---|---|---|
| `casting.xml` | One `<mission>` block per mission: those with a file in `config/missions/` and those listed in `config/tours.yaml`. | At least one such mission is not a sandbox mission; otherwise the file is removed. |
| `sandbox.xml` | The same for missions marked `sandbox: true`. | At least one mission is marked; otherwise the file is removed. |
| `generic.xml` | One `<chatter-defaults target="*">` block: the pool voices and the default officer. | At least one `generic: true` voice has been built; otherwise the file is removed. |

The plugin reads every `*.xml` and `*.xwvm` file in `_chatter/`, and every `*.xwvm` file directly in the sidecar
folder, in path order. File names carry no meaning to it. `write_casting.py` deletes every file named
`_chatter/_chatter-*.xwvm` on each run, so do not give a file of your own such a name.

### Example: `casting.xml`

```xml
<?xml version="1.0" encoding="utf-8"?>
<sidecar>
  <mission target="TIEDOGFIGHT">
    <chatter audioFormat="OGG" banks="VOICE/_chatter" silence="MCCAttacked,MCCDestroyed">
      <speaker id="q0" designation="Red 4" role="silent"/>
      <speaker id="v0" designation="Liberty" role="ship" callsign="Liberty" voice="Mira Kessel"
               name="Liberty" group="v0" lines="capital"/>
      <speaker id="s0" designation="Red 2" voice="Iris Calder"/>
      <speaker id="s1" designation="Red 3" voice="Jon Marek"/>
      <speaker id="cmd" role="commander" voice="Mira Kessel" name="Commander"
               situations="DockingComplete,DockingStart,FlightLost,MCCAttacked,NiceShot,UnderAttack"/>
    </chatter>
  </mission>
</sidecar>
```

The `situations` list of a real officer is longer; see below. `sandbox.xml` looks the same.

### Example: `generic.xml`

```xml
<?xml version="1.0" encoding="utf-8"?>
<sidecar>
  <chatter-defaults target="*" audioFormat="OGG" banks="VOICE/_chatter">
    <speaker id="g0" role="generic" voice="Mira Kessel"/>
    <speaker id="cmd0" role="commander" voice="Jon Marek" name="Commander"
             situations="DockingComplete,DockingStart,EnemyArrival,Engagement,FlightLost,NiceShot"/>
  </chatter-defaults>
</sidecar>
```

The `situations` list is shortened here too.

### Structure

| Element | Parent | Meaning |
|---|---|---|
| root (`<sidecar>`) | | The plugin does not check its name. |
| `<mission target="...">` | root | Applies to the missions `target` matches. Only its `<chatter>` children are read. |
| `<chatter>` | `<mission>` | A casting block for those missions. |
| `<chatter-defaults target="...">` | root | A casting block for every mission `target` matches (every mission when `target` is absent). |
| `<speaker>` | a block | One voice slot. |
| `<line>` | a block | A line written in the casting file itself. Read by the plugin, never written by the build. |

Blocks from `<chatter-defaults>` are taken first, then blocks from `<mission>`. A speaker with the same `id` as an
earlier one replaces it, so a mission's own speaker replaces a default.

### Attributes of a block (`<chatter>`, `<chatter-defaults>`)

| Attribute | Required | Plugin reads | Build writes | Meaning |
|---|---|---|---|---|
| `target` | see above | yes | yes | Mission pattern. On `<chatter-defaults>` and on `<mission>`, not on `<chatter>`. |
| `banks` | no | yes | yes (`VOICE/_chatter`) | Folder, relative to the sidecar folder, that holds one folder per voice. With it, each speaker of the block takes the lines of `<banks>/<voice slug>/bank.xml`. Without it, speakers get no bank lines. |
| `audioFormat` | no | yes | yes (`OGG`) | Extension added to an `audio` name that has no dot. Default `ogg`. |
| `silence` | no | yes | yes, on `<chatter>` | Situation ids, separated by commas or spaces, that are never spoken in the mission; the game's stock line is suppressed too. |
| `audioPath` | no | yes | no | Folder, relative to the sidecar folder, for the clips of the block's own `<line>` elements. |

What the build puts in `silence`: if any flight group in the mission file has a destroy-type objective, all four
`MCC` ids. Otherwise the ids among `MCCAttacked`, `MCCShieldsDown`, `MCCHullCritical`, `MCCDestroyed` and
`DockingComplete` whose moment the mission's own story sidecar (`content/xwing/<MISSION>.xwvm`) already voices for a
flight group that has an objective, and `MCCDestroyed` when a story message triggers on the primary goals failing.
Nothing when there is no story sidecar.

### Attributes of `<speaker>`

| Attribute | Required | Plugin reads | Build writes | Meaning |
|---|---|---|---|---|
| `id` | no | yes | yes | Name of the slot. Without it the `designation` is used, then the `role`; a speaker with none of the three is skipped and logged. |
| `designation` | no | yes | yes | The seat this speaker is bound to ("Red 2", "Liberty"). |
| `role` | no | yes | yes | See roles below. Absent: a cast wingman. |
| `voice` | no | yes | yes | Voice name; its slug is the voice folder. A speaker without one gets no bank lines. |
| `name` | no | yes | yes | Name shown before the subtitle. Default: the craft's designation, or "Commander" for an officer. Ignored for pool voices, which are shown as the craft they fly. |
| `callsign` | no | yes | yes, ships | Ships: the name the ship calls itself, matched against `selfid` versions. |
| `lines` | no | yes | yes, ships | `capital`: take only the bank's capital-ship lines. Anything else or absent: take every line that is not one. |
| `situations` | no | yes | yes, officers and small ships | Situation ids, separated by commas or spaces. The speaker takes only these from the bank. Absent: all. |
| `group` | no | yes | yes, ships | Ship speakers with the same value share one set of lines; any of them may say a line. |

Roles:

| `role` | Meaning | Build writes |
|---|---|---|
| absent | A wingman cast in the seat `designation`. | yes, from `cast` |
| `generic` | A pool voice. In flight each friendly fighter that no speaker is bound to gets one pool voice and keeps it for the mission; the same seat gets the same voice each time the mission is flown. A voice that a mission casts by designation leaves the pool in that mission, unless that would empty the pool. | yes, in `generic.xml` |
| `commander` | The flight officer. Has no craft. A mission block's officer replaces the default one and its lines. | yes |
| `ship` | A named friendly ship bound by `designation`. Says only its own lines, for `KillReport`, `PreparingHyper`, `DockingStart`, `DockingComplete`. | yes, from `ship_voices.yaml` |
| `silent` | A seat that is held: no pool voice is dealt to it and its craft says nothing. | yes, from `silent` |
| `astromech` | A fallback speaker for warnings, used when the player's craft has an astromech. Subtitle shown as the astromech's. | no |

What the build writes in `situations`:

| Speaker | Ids |
|---|---|
| Officer of a mission | `UnderAttack`, `IncomingMissile`, `MCCAttacked`, `MCCShieldsDown`, `MCCHullCritical`, `MCCDestroyed`, `DockingStart`, `DockingComplete`, `NiceShot`, `EnemyArrival`, `Engagement`, `FlightLost` |
| Default officer | The same, less `exclude_situations` |
| Transport or shuttle | `PreparingHyper`, `DockingStart`, `DockingComplete` |
| Freighter, cruiser, frigate, corvette | attribute not written |

Speaker ids the build uses: `s0`, `s1`, ... for cast seats, `cmd` for a mission's officer, `q0`, ... for silent
seats, `v0`, ... for ships, `g0`, ... for pool voices, `cmd0` for the default officer.

### Attributes of a `<line>` inside a block

Read by the plugin; the build never writes these.

| Attribute | Required | Meaning |
|---|---|---|
| `situation` | yes | Situation id. |
| `speaker` | no | Speaker ids, separated by commas or spaces, who may say it. Absent: whoever the game names as sender. |
| `audio` | no | Clip name, found under `audioPath`. Absent: words only. |
| `audioPath` | no | Overrides the block's `audioPath` for this line. |
| `callsignOnly`, `attacker`, `threat`, `arrival`, `context` | no | As in `bank.xml`. |
| element text | no | The subtitle. |

`role` is not read on these lines; it is read only in `bank.xml`.

## Read but never written, written but never read

Plugin reads, build never writes:

- `<line>` elements inside a casting block, with their `speaker` and `audioPath` attributes
- `audioPath` on a block
- speaker role `astromech`
- a speaker without `id`
- a line without `audio` (words only)
- an `audio` name with its own extension, and an `audioFormat` other than `OGG`
- chatter blocks in `.xwvm` files, in `_chatter/` or directly in the sidecar folder
- `target` patterns with `?` or `|`
- a `<composed>` without `kind`

Build writes, plugin never reads:

- `voice-bank.xml` (the whole file)
- `version` on `<callsigns>`
- `situation` on `<composed>` (the plugin stores it and never uses it; `write_casting.py` does)
- the XML header and the root element names `sidecar`, `bank`, `callsigns`

## Open points

- `aliases` in `casting.yaml` is handled by `script.load_casting`, but no build step in this repository calls that
  function; `write_casting.py` reads the `voices` map directly. Whether an alias has any effect on the build could
  not be confirmed.
- `role` in `lines.csv`: only the value `capital` changes what the build does. Nothing stops a wingman's voice
  from recording a `commander` line or the reverse; the line then reaches whichever speakers that voice is cast as
  (an officer only for the ids in its `situations` list).
- Subtitle text is passed through the game's own `MissionMessage.FormatText` before it is shown. What that function
  replaces (a code comment mentions `{playername}`) is not in this repository.
- The plugin loads clips other than `.ogg` through a different path (`.wav` and `.mp3` are named in the code). Only
  `.ogg` is written by the build and covered by tests.
- The test suite uses a clip named `KillReport-c1-t1`, so the plugin accepts a variant that is not a number. The
  build does not: `build_voice.py` and `write_casting.py` both expect digits.
- `write_casting.py` is the new name of the casting script. Its behaviour is described here from the code as it
  stood while the rename was in progress; a later change to that script is not reflected.
- Mission files are found by name under the original game's install and the custom missions folder. If a mission
  file is not found, the build writes no ship speakers for it and its `silence` list can only hold `MCCDestroyed`
  (from a story message on the primary goals failing), without an error.
