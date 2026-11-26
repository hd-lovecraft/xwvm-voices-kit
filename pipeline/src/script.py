"""The script and the cast, as the build steps read them:

    config/lines.csv      one row per line: situation, variant, role, text, and tags
    config/casting.yaml   your voices by name (`generic: true` puts one in the pool for uncast wingmen)

Nothing here knows how a line gets recorded; that is your own step.
"""
import csv
import re
import sys

import yaml

from common import CASTING_YAML, LINES_CSV


def load_casting():
    """casting.yaml as a dict. A top-level `aliases:` map (`Other Name: Listed Name`) lets a voice be found under
    a second name as well as the one it is listed under in `voices:`."""
    if not CASTING_YAML.exists():
        sys.exit(f"{CASTING_YAML} missing.")
    with open(CASTING_YAML, encoding="utf-8") as file:
        casting = yaml.safe_load(file)
    voices = casting.get("voices") or {}
    for alias, listed in (casting.get("aliases") or {}).items():
        if listed in voices and alias not in voices:
            voices[alias] = voices[listed]
    return casting


def load_lines():
    """The rows of lines.csv, one dict per line."""
    with open(LINES_CSV, encoding="utf-8", newline="") as file:
        return list(csv.DictReader(file))


# Callsign tokens in the lines.csv text mark where a callsign is spoken: {player} addresses the player, {self} is
# the speaker's own, {lost}, {addressee} and {wingmate} name another wingman. The plain take of a line is recorded
# without the callsign, so its subtitle drops the token.
TOKEN_RE = re.compile(r"(?:,\s*)?\{(?:player|self|lost|addressee|wingmate)\}\s*")
# A token after a comma goes with the comma; the sentence keeps its own punctuation ("Nice shot, {player}!").
_TOK_ATTACHED = re.compile(r",\s*\{(?:player|self|lost|addressee|wingmate)\}")
# Any other token takes the punctuation that follows it ("Got 'em! {self}. Splash one!", "{self}, copy.").
_TOK_STANDALONE = re.compile(r"\{(?:player|self|lost|addressee|wingmate)\}[!.?,]*\s*")


def strip_tokens(text: str) -> str:
    """The line as the plain take says it: callsign tokens removed, spaces tidied, first letter capitalised."""
    plain = _TOK_ATTACHED.sub("", text or "")
    plain = " ".join(_TOK_STANDALONE.sub("", plain).split())
    return plain[:1].upper() + plain[1:] if plain else plain


_SELF_INTRO = re.compile(r"\bThis is \{self\}[!.?,]*\s*")    # "This is {self}! I'm hit!" -> "I'm hit!"
_SENTENCE_START = re.compile(r"([.!?]\s+)([a-z])")


def plain_text(line: dict) -> str:
    """What the plain clip of a line says, and so its subtitle: the wording without any callsign.

    The `tts` column when it is filled: write it for a line that does not read right once its token is gone
    ("We lost {lost}." -> "We lost one."). Otherwise the text with its tokens removed, a self-introduction dropped
    whole, and each sentence starting with a capital ("Copy. {self} engaging." -> "Copy. Engaging.")."""
    written = (line.get("tts") or "").strip()
    if written:
        return written
    plain = strip_tokens(_SELF_INTRO.sub("", line.get("text") or ""))
    return _SENTENCE_START.sub(lambda m: m.group(1) + m.group(2).upper(), plain)
