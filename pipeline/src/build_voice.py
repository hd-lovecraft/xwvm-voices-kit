"""Turn a voice's raw takes into its in-game bank: radio treatment, static, clips and voice-bank.xml. No judging
step: every take in the folder ships, so listen to the raw takes first and delete the ones you do not want.

    (your own step)                                raw takes -> raw/iris-calder/<Situation>-<variant>.wav
    src/make_beds.py                               once: a plain set of static beds, if you have none of your own
    src/build_voice.py --voice "Iris Calder"       -> content/xwing/VOICE/_chatter/iris-calder/
    src/write_casting.py --all                     -> bank.xml per voice and the casting files

Raw takes are WAV files named <Situation>-<variant>[-anything].wav; the subtitle is that line's plain wording
(script.plain_text). The takes of a line are numbered in file-name order (t1, t2, ...), so a rebuild after deleting
a raw take can leave a clip of the earlier build behind. The build names such clips and never deletes them.
Needs ffmpeg and a folder of static beds (RADIO_BEDS_DIR, default pipeline/beds).
"""
import argparse
import random
import re
import sys
import zlib
from pathlib import Path

import bake
import bank
import radio
import script
from common import CHATTER_DIR, RAW_DIR, slug


def radio_clip(raw: Path, situation: str, beds, treat):
    """One raw take as the clip the game plays: the radio treatment, then static."""
    voice = treat(bake.read_wav(raw).astype("float64")).astype("float32")
    # seeded from the file name, so a take gets the same static on every build
    mixed, _ = bake.bake_one(voice, beds, random.Random(zlib.crc32(raw.stem.encode())), situation=situation)
    return mixed


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--voice", required=True, help="voice name, as in config/casting.yaml")
    parser.add_argument("--raw", help="folder of raw takes (default: raw/<voice>)")
    parser.add_argument("--beds", default=str(bake.DEFAULT_BEDS_DIR), help="folder of static beds (WAV)")
    args = parser.parse_args()

    raw_dir = Path(args.raw) if args.raw else RAW_DIR / slug(args.voice)
    takes = sorted(raw_dir.glob("*.wav"))
    if not takes:
        sys.exit(f"no raw takes in {raw_dir}")
    beds_dir = Path(args.beds)
    if not any(beds_dir.glob("*.wav")):
        sys.exit(f"no static beds in {beds_dir}: run src/make_beds.py for a plain set, or set RADIO_BEDS_DIR to your own")
    texts = {(row["situation"], str(row["variant"])): script.plain_text(row) for row in script.load_lines()}
    beds = bake.load_beds(beds_dir)
    treat = radio.treatment()
    out_dir = CHATTER_DIR / slug(args.voice)
    out_dir.mkdir(parents=True, exist_ok=True)

    rows = []
    takes_per_line = {}
    for raw in takes:
        match = re.match(r"([A-Za-z][A-Za-z0-9]*)-(\d+)", raw.stem)   # situation ids may end in a digit (KillStreak2)
        if not match or match.groups() not in texts:
            print(f"  skip {raw.name}: not a <Situation>-<variant> in lines.csv")
            continue
        line = match.groups()
        situation, variant = line
        takes_per_line[line] = takes_per_line.get(line, 0) + 1
        take = f"t{takes_per_line[line]}"
        clip = f"{situation}-{variant}-{take}"
        bake.write_ogg(radio_clip(raw, situation, beds, treat), out_dir / f"{clip}.ogg")
        rows.append((situation, variant, take, clip, texts[line]))
    if not rows:
        sys.exit("nothing built")
    bank.write_voice_bank(args.voice, rows)
    # A clip this build did not write is in no bank. Only names of this script's own pattern are reported.
    built = {f"{clip}.ogg" for _, _, _, clip, _ in rows}
    stale = sorted(path.name for path in out_dir.glob("*.ogg")
                   if re.fullmatch(r"[A-Za-z][A-Za-z0-9]*-\d+-t\d+\.ogg", path.name) and path.name not in built)
    if stale:
        print(f"  note: {len(stale)} clip(s) left from an earlier build are not in voice-bank.xml; delete them: "
              + ", ".join(stale))
    print(f"{args.voice}: {len(rows)} clip(s) -> {out_dir}  (+ voice-bank.xml)")
    print("next: src/write_casting.py --all")


if __name__ == "__main__":
    main()
