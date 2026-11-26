"""Write the two files that sit beside a voice's clips in VOICE/_chatter/<voice>/:

    voice-bank.xml   one <line> per plain clip: situation, clip name, subtitle, tags from lines.csv
    callsigns.xml    one <composed> per clip recorded with a callsign in it ("Red Two, break off!")

write_casting.py turns voice-bank.xml into the bank.xml the plugin reads. Which clips go in is up to the caller:
build_voice.py lists every take it finds.
"""
import html
from pathlib import Path
from xml.sax.saxutils import escape

import script
from common import CHATTER_DIR, slug

TAG_COLUMNS = ("attacker", "threat", "arrival", "context")
# In the name of every clip recorded with a callsign, after "<Situation>-<variant>". The plugin tells such a clip
# from a plain take by this text, so it is fixed.
CALLSIGN_MARK = "-v4"


def line_tags():
    """{(situation, variant): {tag: value}} for the lines.csv rows that carry tags. They travel into voice-bank.xml
    so the casting files (and the plugin) see them."""
    tags = {}
    for row in script.load_lines():
        row_tags = {column: (row.get(column) or "").strip() for column in TAG_COLUMNS}
        if any(row_tags.values()):
            tags[(row["situation"], str(row["variant"]))] = row_tags
    return tags


def write_voice_bank(voice: str, rows, tags=None) -> Path:
    """rows: (situation, variant, take id, clip name without .ogg, subtitle text), one per clip already in the
    voice's folder. tags: as line_tags() returns them (the default)."""
    tags = line_tags() if tags is None else tags
    lines = []
    for situation, variant, _take, clip, text in sorted(rows):
        clip_tags = tags.get((situation, str(variant)), {})
        extra = "".join(f' {tag}="{html.escape(value)}"' for tag, value in clip_tags.items() if value)
        lines.append(f'<line situation="{situation}" audio="{clip}"{extra}>{html.escape(text, quote=False)}</line>')
    out = CHATTER_DIR / slug(voice) / "voice-bank.xml"
    out.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return out


def callsign_clip_name(situation: str, variant, kind: str, callsign: str) -> str:
    """File name of a line recorded with a callsign. The plugin plays it in place of a plain take of the same line,
    which it finds by the "<Situation>-<variant>-" start of the name."""
    return f"{situation}-{variant}{CALLSIGN_MARK}--{kind}--{slug(callsign)}.ogg"


def write_callsigns(voice_dir: Path, entries) -> Path:
    """entries: (kind, callsign, situation, clip file name, subtitle text), in the order to write them.
    kind: address (names the player's seat), selfid (the speaker's own), lost / reply / mate (another wingman's)."""
    quote = {'"': "&quot;"}     # the callsign is written inside an attribute
    rows = [f'  <composed kind="{kind}" callsign="{escape(callsign, quote)}" situation="{situation}" '
            f'audio="{name}">{escape(text)}</composed>' for kind, callsign, situation, name, text in entries]
    out = Path(voice_dir) / "callsigns.xml"
    out.write_text('<?xml version="1.0" encoding="utf-8"?>\n<callsigns version="1">\n'
                   + "\n".join(rows) + "\n</callsigns>\n", encoding="utf-8")
    return out
