"""Write what the plugin reads: each voice's bank.xml and the casting files.

    src/write_casting.py --all

A mission name (it must have a casting file) or --generic in place of --all runs the same full pass: the casting
is one file, so it is always written whole.

Reads
    config/missions/<MISSION>.yaml    who flies where in one mission:
                                          mission: TIEDOGFIGHT
                                          commander: null       # a voice, or null for the tour's officer
                                          cast:                 # seat -> voice, as named in config/casting.yaml
                                            Red 2: Iris Calder
                                          silent: [Red 4]       # optional: seats that never speak
                                          sandbox: true         # optional: a test mission
    config/tours.yaml                 each tour's officer and missions
    config/default_officer.yaml       the officer of every other mission
    config/ship_voices.yaml           the voice of each named friendly ship
    config/casting.yaml               `generic: true` marks the pool for uncast wingmen
    VOICE/_chatter/<voice>/           voice-bank.xml and callsigns.xml, as bank.py writes them
    the mission file (XWI) and the mission's own sidecar content/xwing/<MISSION>.xwvm, where they exist

Writes, under content/xwing/
    VOICE/_chatter/<voice>/bank.xml   the voice's lines, once
    _chatter/generic.xml              the generic pool and the default officer
    _chatter/casting.xml              one <mission> block per mission: seats, ships, the officer, calls to silence
    _chatter/sandbox.xml              the same for the missions marked `sandbox: true`

The casting files hold speakers only: the plugin gives each <speaker voice=...> the lines of that voice's bank.xml
(ChatterConfig.AbsorbBanks). A fighter seat that no mission casts takes a pool voice in flight. Chatter files of
the older format (_chatter/_chatter-*.xwvm) are removed: beside these, they would give a speaker its lines twice.
"""
import argparse
import html
import os
import re
import struct
import sys
from pathlib import Path

import yaml

import script
import ships
from bank import CALLSIGN_MARK
from common import CASTING_YAML, CHATTER_DIR, CHATTER_SIDECARS, CONFIG_DIR, CONTENT_XWING, slug, xwing_dir

NL = "\n"

# ---- reading X-Wing mission files (XWI) ----
XWI_DIRS = [xwing_dir() / "remastered" / "X-Wing Data" / "MISSION",     # the ones XWVM reads
            xwing_dir() / "classic" / "MISSION"]
if os.environ.get("APPDATA"):       # custom missions; without APPDATA the path would hang off the working folder
    XWI_DIRS.append(Path(os.environ["APPDATA"]) / "XWVM" / "CustomMissions" / "xwing")
XWI_FG_BASE, XWI_FG_SIZE = 206, 148          # first flight-group record (0xCE), record length (0x94)
XWI_CRAFT_TYPE, XWI_NUM_CRAFT = 50, 56       # offsets in a record, 16 bits each
# FlightGroupFileStruct +0x8E: an objective other than 0 makes a craft "mission critical" (the game's MCC radio).
XWI_OBJECTIVE = 142

# ---- scripted moments: the mission's own voice-over owns them ----
# The game's mission-critical-craft radio (attacked, shields down, hull critical, destroyed) fires for flight groups
# with an objective. Where the mission's own sidecar (content/xwing/<MISSION>.xwvm) already voices that moment - a
# <message> triggered by the matching condition on such a flight group, or by FailedPrimaryGoals for the loss - the
# chatter must not add a line; the plugin silences the stock message instead. This is per mission, not per flight
# group: the game hands the plugin a stand-in sender, not the craft, so one scripted craft silences the call for
# every craft of the mission.
MCC_CONDITIONS = {"Attacked": "MCCAttacked", "ZeroShields": "MCCShieldsDown", "FiftyHull": "MCCHullCritical",
                  "Destroyed": "MCCDestroyed",
                  "Docked": "DockingComplete"}      # a boarding; the game announces it as MCCBoardingComplete
DESTROY_OBJECTIVES = {1, 5, 9}   # all destroyed / special craft destroyed / 50 % destroyed

# On-screen subtitle label for a speaker without a callsign: the role, never the voice's name.
ROLE_LABEL = {"commander": "Commander"}

# ---- output files ----
# Each voice folder carries bank.xml (its lines, once) and the files in _chatter/ only cast. They are plain .xml:
# .xwvm is the game's sidecar extension, and the game never opens that folder. Inside, the shape is the game's
# sidecar shape (<sidecar><mission target=...><chatter>). banks="VOICE/_chatter" on a block tells the plugin where
# the bank.xml of each <speaker voice=...> is (ChatterConfig.AbsorbBanks).
BANKS_ATTR = "VOICE/_chatter"
BANK_FILE = "bank.xml"
CASTING_FILE = "casting.xml"
SANDBOX_FILE = "sandbox.xml"
BANK_GENERIC_FILE = "generic.xml"
XML_HEADER = '<?xml version="1.0" encoding="utf-8"?>'

# ---- who may say what ----
WARNINGS = {"UnderAttack", "IncomingMissile", "MCCAttacked", "MCCShieldsDown", "MCCHullCritical", "MCCDestroyed"}
# What a commander (a tour's flight officer) voices: the warnings and the calls below. The officer never answers a
# wingman's lament (WingmanLostReply).
COMMANDER_SITUATIONS = WARNINGS | {
    "DockingStart", "DockingComplete",              # the game's boarding calls (MCCBoardingStart / Complete)
    # Wingman lines with no particular sender. The plugin ranks the commander below every live wingman
    # (ChatterSession.TryResolve), so the officer says these only when no wingman can.
    "NiceShot", "EnemyArrival", "Engagement",
    "FlightLost",                                   # the officer takes over when the player's flight is gone
}
SHIP_SMALL_SITUATIONS = {"PreparingHyper", "DockingStart", "DockingComplete"}   # transports/shuttles: no kill calls
LINE_RE = re.compile(r'<line situation="(?P<sit>\w+)" audio="(?P<audio>[^"]+)"(?P<extra>[^>]*)>(?P<text>.*?)</line>')
# a callsigns.xml entry that names the speaker's own seat or the player's: (situation, variant, subtitle)
_OWN_CALLSIGN_RE = re.compile(r'<composed kind="(?:selfid|address)"[^>]*situation="(\w+)" audio="\w+-(\d+)'
                              + re.escape(CALLSIGN_MARK) + r'--[^"]*"[^>]*>(.*?)</composed>')


def subtitle_text(text: str) -> str:
    """The on-screen subtitle for a line: the spoken words without the voice-direction tags written for the recording
    ("Power is out [heavy gasps] I need help!" -> "Power is out I need help!"). The plugin shows the text as it is."""
    return re.sub(r"\s+", " ", re.sub(r"\[[^\]]*\]", " ", text)).strip()


# ---- mission files ----

def find_xwi(mission: str):
    """The mission's XWI file, found by name in XWI_DIRS, or None. Capitals do not matter, in the name or in the
    extension, on any system."""
    for folder in XWI_DIRS:
        for path in folder.glob("*"):
            if path.suffix.upper() == ".XWI" and path.stem.upper() == mission.upper():
                return path
    return None


def xwi_flight_groups(mission: str):
    """Yield (index, record bytes, name, craft type, craft count) for each flight group in the mission file;
    nothing if there is no file. The count in the file's header is not reliable (some files say 0), so the table is
    walked until a record stops looking like a flight group. A record cut short by the end of the file is left out."""
    xwi = find_xwi(mission)
    if not xwi:
        return
    data = xwi.read_bytes()
    index = 0
    while True:
        start = XWI_FG_BASE + index * XWI_FG_SIZE
        record = data[start:start + XWI_FG_SIZE]
        if len(record) < XWI_FG_SIZE:
            break
        name = record[:16].split(b"\0")[0].decode(errors="replace").strip()
        if not name or not all(32 <= char < 127 for char in record[:len(name)]):
            break
        craft_type = struct.unpack_from("<h", record, XWI_CRAFT_TYPE)[0]
        count = struct.unpack_from("<h", record, XWI_NUM_CRAFT)[0]
        if not (0 <= craft_type < 64 and 0 <= count < 64):
            break       # walked past the flight-group table into other data
        yield index, record, name, craft_type, count
        index += 1


def mcc_objectives(mission: str):
    """{fg index: objective code} for the mission's flight groups with an objective (XWI, +0x8E). Codes:
    1 all destroyed, 2 all survive, 3 captured, 4 docked, 5/6/7/8 the special craft, 9..12 50 %, 13..15 identified,
    16 arrive. Destroy-type codes mark ENEMY objectives: the game's MCC radio fires for those too when the player
    attacks them."""
    objectives = {}
    for index, record, *_ in xwi_flight_groups(mission):
        objective = struct.unpack_from("<h", record, XWI_OBJECTIVE)[0]
        if objective:
            objectives[index] = objective
    return objectives


# ---- scripted moments ----

def scripted_mcc(mission: str):
    """The MCC situations the mission's own sidecar already voices (see MCC_CONDITIONS). GlobalGroup triggers are
    resolved through the sidecar's own <flightGroup target="##N" globalGroup="G"/> map."""
    sidecar = CONTENT_XWING / f"{mission}.xwvm"
    if not sidecar.exists():
        return set()
    critical = set(mcc_objectives(mission))
    text = sidecar.read_text(encoding="utf-8", errors="replace")
    global_groups = {}
    for index, group in re.findall(r'<flightGroup target="##(\d+)" globalGroup="(\d+)"', text):
        global_groups.setdefault(group, set()).add(int(index))
    scripted = set()
    for message in re.findall(r"<message\b[^>]*>(.*?)</message>", text, re.S):
        for kind, variable, condition in re.findall(r'type="(\w+)" variable="(\w+)" condition="(\w+)"', message):
            if condition == "FailedPrimaryGoals":
                scripted.add("MCCDestroyed")
            elif condition not in MCC_CONDITIONS:
                continue
            elif kind == "FlightGroup" and variable.isdigit() and int(variable) in critical:
                scripted.add(MCC_CONDITIONS[condition])
            elif kind == "GlobalGroup" and global_groups.get(variable, set()) & critical:
                scripted.add(MCC_CONDITIONS[condition])
    return scripted


def silenced_mcc(mission: str):
    """What the plugin must silence for this mission: the scripted moments, or all four MCC calls when any
    mission-critical craft is a target to destroy (the officer's lines assume a craft to protect, and the game cannot
    tell the plugin which craft it means)."""
    if any(code in DESTROY_OBJECTIVES for code in mcc_objectives(mission).values()):
        # the boarding line stays: the game sends that one for friendly craft only
        return {situation for condition, situation in MCC_CONDITIONS.items() if condition != "Docked"}
    return scripted_mcc(mission)


def silence_attr(mission: str):
    """The silence="..." attribute for the mission's <chatter>, or "" when nothing is silenced."""
    silenced = sorted(silenced_mcc(mission))
    return f' silence="{",".join(silenced)}"' if silenced else ""


def situations_attr(sits):
    """The situations="..." attribute that limits a speaker to those situations."""
    return f' situations="{",".join(sorted(sits))}"'


# ---- voices, the pool, the tours ----

def _voice_dirs():
    """The voice folders, in name order; none before the first voice is built. A folder whose name starts with an
    underscore is not a voice."""
    if not CHATTER_DIR.is_dir():
        return []
    return sorted(path for path in CHATTER_DIR.iterdir() if path.is_dir() and not path.name.startswith("_"))


def _casting_voices():
    """The `voices:` map of casting.yaml as written (script.load_casting adds the aliases); {} if there is no file
    or it lists no voices. A file that cannot be read stops the run: carrying on would write the casting without
    its pool."""
    if not CASTING_YAML.exists():
        return {}
    try:
        casting = yaml.safe_load(CASTING_YAML.read_text(encoding="utf-8"))
    except (yaml.YAMLError, UnicodeDecodeError) as error:
        sys.exit(f"{CASTING_YAML} cannot be read: {error}")
    voices = casting.get("voices") if isinstance(casting, dict) else None
    if voices is not None and not isinstance(voices, dict):
        sys.exit(f"{CASTING_YAML}: `voices:` must be a map of voice names")
    return voices or {}


def generic_pool_voices():
    """casting.yaml voices flagged `generic: true` (display names), in casting order."""
    return [name for name, settings in _casting_voices().items() if (settings or {}).get("generic")]


def generic_pool_shipped():
    """Pool voices that actually have a shipped bank (VOICE/_chatter/<slug>/voice-bank.xml)."""
    return [voice for voice in generic_pool_voices() if (CHATTER_DIR / slug(voice) / "voice-bank.xml").exists()]


def sandbox_missions():
    """Test missions: a mission YAML with `sandbox: true`. They are written to sandbox.xml, apart from casting.xml."""
    sandbox = set()
    for mission_yaml in sorted((CONFIG_DIR / "missions").glob("*.yaml")):
        cfg = yaml.safe_load(mission_yaml.read_text(encoding="utf-8")) or {}
        if cfg.get("sandbox"):
            sandbox.add(cfg.get("mission") or mission_yaml.stem)
    return sandbox


def tour_officer(mission: str):
    """The mission's flight officer: the officer of its tour in config/tours.yaml, or None. A mission YAML normally
    leaves `commander:` null and takes this one."""
    tours_file = CONFIG_DIR / "tours.yaml"
    if not tours_file.exists():
        return None
    tours = yaml.safe_load(tours_file.read_text(encoding="utf-8"))
    listed = (tours.get("variants") or {}).get(mission, mission)
    for tour in tours.get("tours") or []:
        if listed in (tour.get("missions") or []):
            return tour.get("officer")
    return None


def tour_missions():
    """Every campaign mission tours.yaml knows (incl. variants)."""
    tours_file = CONFIG_DIR / "tours.yaml"
    if not tours_file.exists():
        return []
    tours = yaml.safe_load(tours_file.read_text(encoding="utf-8"))
    missions = []
    for tour in tours.get("tours") or []:
        missions += list(tour.get("missions") or [])
    missions += list((tours.get("variants") or {}).keys())
    return sorted(set(missions))


# ---- reading a voice's lines ----

def capital_lines():
    """{(situation, variant)} of the capital-ship lines (lines.csv role `capital`)."""
    return {(row["situation"], str(row["variant"])) for row in script.load_lines()
            if (row.get("role") or "") == "capital"}


def callsign_only_lines():
    """{(situation, variant)} of the lines that exist ONLY as callsign versions (lines.csv callsign_only = yes): no
    plain take is recorded; the plugin offers such a line only to a speaker whose bank has its version for the seat."""
    return {(row["situation"], str(row["variant"])) for row in script.load_lines()
            if (row.get("callsign_only") or "").strip().lower() == "yes"}


def _line_key(audio):
    """(situation, variant) from a clip name such as "KillReport-1-t2"; (None, None) for any other name."""
    match = re.match(r"(\w+?)-(\d+)(?:-|$)", audio or "")
    return (match.group(1), match.group(2)) if match else (None, None)


def load_bank(voice: str, ship: bool = False):
    """The voice's lines from VOICE/_chatter/<slug>/voice-bank.xml: [(situation, audio, subtitle, extra attributes)].
    ship=False (wingmen, officers, the pool) leaves the capital-ship lines out; ship=True gives ONLY those."""
    voice_dir = CHATTER_DIR / slug(voice)
    bank_file = voice_dir / "voice-bank.xml"
    if not bank_file.exists():
        return []
    capital = capital_lines()
    lines = []
    for match in LINE_RE.finditer(bank_file.read_text(encoding="utf-8")):
        if (_line_key(match.group("audio")) in capital) != ship:
            continue
        # a plain line never shows the {player}/{self} tokens that mark where a callsign goes
        text = subtitle_text(script.strip_tokens(html.unescape(match.group("text"))))
        lines.append((match.group("sit"), match.group("audio"), text, match.group("extra") or ""))
    if not ship:
        # Callsign-only lines: one entry per line this voice has a recorded callsign version of. The audio names the
        # line ("<Sit>-<var>" and CALLSIGN_MARK), not a file; the plugin swaps in the version for the speaker's seat or
        # skips the line.
        callsigns_file = voice_dir / "callsigns.xml"
        callsign_only = callsign_only_lines()
        if callsign_only and callsigns_file.exists():
            seen = set()
            for situation, variant, text in _OWN_CALLSIGN_RE.findall(callsigns_file.read_text(encoding="utf-8")):
                line = (situation, variant)
                if line in callsign_only and line not in seen:
                    seen.add(line)
                    lines.append((situation, f"{situation}-{variant}{CALLSIGN_MARK}",
                                  subtitle_text(html.unescape(text)), ' callsignOnly="true"'))
    return lines


# ---- XML pieces ----

def _xml_doc(root: str, body: str) -> str:
    """A whole XML file: the header and `body` inside one <root> element."""
    return XML_HEADER + NL + f"<{root}>" + NL + body + NL + f"</{root}>" + NL


def _bank_line(line, role_attr: str = "") -> str:
    """A line (as load_bank gives it) as bank.xml lists it."""
    situation, audio, text, extra = line
    return (f'  <line situation="{situation}" audio="{audio}"{extra}{role_attr}>'
            f'{html.escape(text, quote=False)}</line>')


def _commander_speaker(speaker_id: str, voice: str, situations, indent: str = "      ") -> str:
    """The <speaker> of a flight officer, who voices only `situations`."""
    return (f'{indent}<speaker id="{speaker_id}" role="commander" voice="{html.escape(voice)}" '
            f'name="{ROLE_LABEL["commander"]}"{situations_attr(situations)}/>')


def _mission_block(mission: str, silence: str, speakers) -> str:
    """The <mission> block of one mission around its <speaker> lines."""
    return (f'  <mission target="{mission}">' + NL
            + f'    <chatter audioFormat="OGG" banks="{BANKS_ATTR}"{silence}>' + NL
            + NL.join(speakers) + NL + '    </chatter>' + NL + '  </mission>')


# ---- writers ----

def write_banks():
    """VOICE/_chatter/<slug>/bank.xml for every voice with a voice-bank.xml: the plain lines (subtitle text, tags),
    the callsign-only lines, then the capital-ship lines marked role="capital"."""
    folders = line_count = 0
    for voice_dir in _voice_dirs():
        if not (voice_dir / "voice-bank.xml").exists():
            continue
        rows = [_bank_line(line) for line in load_bank(voice_dir.name)]
        rows += [_bank_line(line, ' role="capital"') for line in load_bank(voice_dir.name, ship=True)]
        (voice_dir / BANK_FILE).write_text(_xml_doc("bank", NL.join(rows)), encoding="utf-8")
        folders += 1
        line_count += len(rows)
    print(f"  {BANK_FILE}: {folders} voice folders, {line_count} lines")


def write_casting(blocks):
    """blocks: [(mission, <mission> block xml)] -> the casting file and the sandbox one. A file with no missions is
    removed, and so are the per-mission sidecars of the older format."""
    CHATTER_SIDECARS.mkdir(parents=True, exist_ok=True)
    sandbox = sandbox_missions()
    for name, for_sandbox in ((CASTING_FILE, False), (SANDBOX_FILE, True)):
        mine = [block for mission, block in sorted(blocks) if (mission in sandbox) == for_sandbox]
        out = CHATTER_SIDECARS / name
        if not mine:
            if out.exists():
                out.unlink()
            continue
        doc = _xml_doc("sidecar", NL.join(mine))
        out.write_text(doc, encoding="utf-8")
        print(f"  {name}: {len(mine)} missions, {doc.count('<speaker ')} speakers")
    old = list(CHATTER_SIDECARS.glob("_chatter-*.xwvm"))
    for path in old:
        path.unlink()
    if old:
        print(f"  removed {len(old)} .xwvm chatter sidecar(s) of the older formats")


def _default_officer_speaker():
    """[the default officer's <speaker>] for the pool file, or [] (config/default_officer.yaml): the commander of
    any mission whose own casting has none. The plugin drops it wherever a mission block casts a commander
    (ChatterConfig.Load)."""
    officer_file = CONFIG_DIR / "default_officer.yaml"
    if not officer_file.exists():
        return []
    cfg = yaml.safe_load(officer_file.read_text(encoding="utf-8")) or {}
    officer = cfg.get("officer")
    if officer and load_bank(officer):
        situations = COMMANDER_SITUATIONS - set(cfg.get("exclude_situations") or [])
        print(f"  default officer: {officer} ({len(situations)} situations)")
        return [_commander_speaker("cmd0", officer, situations, indent="    ")]
    if officer:
        print(f"  ! default officer {officer} has no bank - not written")
    return []


def emit_generic():
    """Write the pool file (BANK_GENERIC_FILE): a role="generic" speaker for each pool voice that has a bank, and the
    default officer, inside <chatter-defaults target="*"> so that it applies to every mission. Returns the file, or
    None when no pool voice has a bank (a pool file from an earlier run is then removed)."""
    voices = generic_pool_shipped()
    CHATTER_SIDECARS.mkdir(parents=True, exist_ok=True)
    out = CHATTER_SIDECARS / BANK_GENERIC_FILE
    if not voices:
        if out.exists():
            out.unlink()
            print(f"  {BANK_GENERIC_FILE}: no shipped generic-pool voice; removed")
        else:
            print(f"  {BANK_GENERIC_FILE}: no shipped generic-pool voice yet "
                  "(flag voices `generic: true` in casting.yaml, then generate/emit)")
        return None
    # no name= attribute: a pool voice is subtitled as the ship it flies, never under a name of its own
    speakers_xml = [f'    <speaker id="g{number}" role="generic" voice="{html.escape(voice)}"/>'
                    for number, voice in enumerate(voices)]
    speakers_xml += _default_officer_speaker()
    doc = _xml_doc("sidecar", f'  <chatter-defaults target="*" audioFormat="OGG" banks="{BANKS_ATTR}">' + NL
                   + NL.join(speakers_xml) + NL + '  </chatter-defaults>')
    out.write_text(doc, encoding="utf-8")
    # no lines in the pool file: they are in each voice's bank.xml
    print(f"  {BANK_GENERIC_FILE}: {len(voices)} generic speakers, 0 lines")
    return out


def ship_block(mission):
    """The <speaker> lines of the mission's named friendly ships that config/ship_voices.yaml gives a voice. One
    role="ship" speaker per ship; its callsign is the flight group's name, which the ship's lines say. A ship speaks
    only the capital-ship lines, in its fixed voice (`per_mission:` overrides it for one mission); a transport or
    shuttle only those of SHIP_SMALL_SITUATIONS."""
    voices_file = CONFIG_DIR / "ship_voices.yaml"
    if not voices_file.exists():
        return []
    ship_voices = (yaml.safe_load(voices_file.read_text(encoding="utf-8")) or {}).get("ships") or {}
    speakers_xml = []
    seat_number = 0
    for group, craft_type, count in ships.friendly_ships(mission):
        name = ships.group_name(group)
        entry = ship_voices.get(name)
        if not entry:
            continue
        voice = (entry.get("per_mission") or {}).get(mission) or entry["voice"]
        if not load_bank(voice, ship=True):
            continue
        only = "" if craft_type in ships.CAPITAL else situations_attr(SHIP_SMALL_SITUATIONS)
        seats = [name] if count <= 1 else [f"{name} {number}" for number in range(1, count + 1)]
        # the ships of one flight group share one set of lines: `group` is the id of the first of them
        group_id = f"v{seat_number}"
        for seat in seats:
            speakers_xml.append(
                f'      <speaker id="v{seat_number}" designation="{html.escape(seat)}" role="ship" '
                f'callsign="{html.escape(name)}" voice="{html.escape(voice)}" name="{html.escape(name)}"'
                f' group="{group_id}" lines="capital"{only}/>')
            seat_number += 1
    return speakers_xml


def emit(mission_yaml: Path):
    """Cast one mission from its YAML: the seats of `cast:`, the `silent:` seats, the named ships and the flight
    officer. Returns (mission, its <mission> block) for write_casting()."""
    cfg = yaml.safe_load(mission_yaml.read_text(encoding="utf-8"))
    mission = cfg["mission"]
    cast = dict(cfg.get("cast", {}) or {})
    unvoiced = [str(seat) for seat, voice in cast.items() if not isinstance(voice, str) or not voice.strip()]
    if unvoiced:
        sys.exit(f"{mission_yaml.name}: no voice named for {', '.join(unvoiced)}")
    commander = cfg.get("commander") or tour_officer(mission)   # the YAML's own, else the tour's

    voices = list(cast.values()) + ([commander] if commander else [])
    missing = [voice for voice in dict.fromkeys(voices) if not load_bank(voice)]
    if missing:
        print(f"  ! no shipped bank for: {missing} (build its bank first: build_voice.py)")

    # `silent:` seats are held but say nothing, so that no pool voice is given to them
    silent = cfg.get("silent") or []
    speakers_xml = [f'      <speaker id="q{number}" designation="{html.escape(seat)}" role="silent"/>'
                    for number, seat in enumerate(silent)]
    ship_speakers = ship_block(mission)
    speakers_xml += ship_speakers
    speakers_xml += [f'      <speaker id="s{number}" designation="{html.escape(seat)}" voice="{html.escape(voice)}"/>'
                     for number, (seat, voice) in enumerate(cast.items())]
    if commander:
        speakers_xml.append(_commander_speaker("cmd", commander, COMMANDER_SITUATIONS))

    silence = silence_attr(mission)
    counts = (f"{len(voices)} speakers" + (f" + {len(silent)} silent" if silent else "")
              + (f" + {len(ship_speakers)} ship" if ship_speakers else ""))
    scripted = f"; scripted: {silence.strip()}" if silence else ""
    print(f"  {mission}: {counts}{scripted}")
    return mission, _mission_block(mission, silence, speakers_xml)


def emit_commander_only(mission: str):
    """A mission with no casting YAML (its wingmen come from the generic pool) still gets its tour's flight officer,
    its named ships and what to silence. Returns as emit() does, or None if the tour has no officer with a bank."""
    commander = tour_officer(mission)
    if not commander or not load_bank(commander):
        return None
    speakers_xml = [_commander_speaker("cmd", commander, COMMANDER_SITUATIONS)] + ship_block(mission)
    return mission, _mission_block(mission, silence_attr(mission), speakers_xml)


def warn_unanchored_variants():
    """Warn about callsign versions that can never play. The plugin swaps a callsign version in for a plain line it
    has already chosen (ChatterSession.TryAddress), so a line that has callsign versions but no plain take in
    voice-bank.xml is never heard, unless lines.csv marks it callsign_only. A warning, not a failure: the rest of
    the bank is fine. Returns {voice folder: [line names]}."""
    unanchored = {}
    callsign_only = {f"{situation}-{variant}" for situation, variant in callsign_only_lines()}
    for voice_dir in _voice_dirs():
        callsigns_file, bank_file = voice_dir / "callsigns.xml", voice_dir / "voice-bank.xml"
        if not callsigns_file.exists() or not bank_file.exists():
            continue
        with_callsign = set(re.findall(r'audio="([A-Za-z0-9]+-\d+)' + re.escape(CALLSIGN_MARK) + "--",
                                       callsigns_file.read_text(encoding="utf-8")))
        plain = set(re.findall(r'audio="([A-Za-z0-9]+-\d+)-', bank_file.read_text(encoding="utf-8")))
        never_played = with_callsign - plain - callsign_only
        if never_played:
            unanchored[voice_dir.name] = sorted(never_played)
    for voice, lines in unanchored.items():
        print(f"  ! {voice}: {len(lines)} callsign line(s) with NO plain take - they never play: {', '.join(lines[:8])}"
              + (" ..." if len(lines) > 8 else ""))
    return unanchored


def _emit_all(missions_dir: Path):
    """The full pass: banks, the pool file, every cast mission, then the tour missions nobody cast."""
    write_banks()
    emit_generic()
    blocks = []
    cast_missions = set()
    for mission_yaml in sorted(missions_dir.glob("*.yaml")):
        mission, block = emit(mission_yaml)
        blocks.append((mission, block))
        cast_missions.add(mission)      # the name in the YAML, which need not be the file's
    officer_only = 0
    for mission in tour_missions():
        if mission in cast_missions:
            continue
        block = emit_commander_only(mission)
        if block:
            officer_only += 1
            blocks.append(block)
    print(f"  commander-only casting for {officer_only} pool-only mission(s) (tour officer + scripted-moment policy)")
    write_casting(blocks)
    warn_unanchored_variants()


def main():
    parser = argparse.ArgumentParser(
        description="Write bank.xml for every voice and the casting files. The three forms do the same full pass.")
    parser.add_argument("mission", nargs="?", help="a mission that has a casting file in config/missions")
    parser.add_argument("--all", action="store_true", help="write everything")
    parser.add_argument("--generic", action="store_true", help="write everything (the pool file is part of it)")
    args = parser.parse_args()
    missions_dir = CONFIG_DIR / "missions"
    if args.mission and not (missions_dir / f"{args.mission}.yaml").exists():
        sys.exit(f"no casting file: {missions_dir / (args.mission + '.yaml')}")
    _casting_voices()       # a casting.yaml that cannot be read stops the run before anything is written
    if not (args.mission or args.all or args.generic):
        sys.exit("give a mission name, --all, or --generic")
    _emit_all(missions_dir)


if __name__ == "__main__":
    main()
