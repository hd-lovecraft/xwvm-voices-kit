"""The static half of the radio bake: lay static beds under a voice that has had its radio treatment
(radio.py), cut a death line short, add a distress alarm, set the level and encode OGG for the game.

Each clip gets one or two beds at a random offset with a small random level change, drawn from a seeded
generator: every clip has its own interference, and a rebuild gives the same clip. The beds are used at the level
they were made at; BED_TRIM_DB is the one master trim.

build_voice.py is the usual way in. Run on its own, this bakes WAVs that are already radio-processed:

    src/bake.py --in <WAV file or folder> --out <folder> [--seed 42] [--beds <folder>] [--wav]

Needs ffmpeg (decoding and OGG encoding); the mixing is numpy.
"""
import argparse
import random
import subprocess
import sys
import wave
import zlib
from pathlib import Path

import numpy as np

from common import PIPELINE_DIR, local_path

FFMPEG = str(local_path("FFMPEG", "ffmpeg"))                               # default: the one on PATH
DEFAULT_BEDS_DIR = local_path("RADIO_BEDS_DIR", PIPELINE_DIR / "beds")     # your own static beds (WAV)

# ---- how the static is laid under a voice ----
BEDS_PER_CLIP = (1, 2)      # how many beds to layer, inclusive range
BED_WANDER_DB = 3.0         # +/- dB random level change per bed
BED_TRIM_DB = -4.0          # master trim on the static layer under the voice
FADE_MS = 40                # bed fade in/out, so a slice does not click
LEAD_MS = 120               # the bed starts this long before the voice (the squelch opening)
TAIL_MS = 180               # ...and runs on this long after it
TARGET_RMS_DBFS = -19.5     # loudness target, chosen to sit level with hand-made mission voice-over
PEAK_CEILING_DBFS = -3.0    # never exceed this peak
SR = 48000


def read_wav(path: Path) -> np.ndarray:
    """Decode any audio file to 48 kHz mono float32 via ffmpeg (handles 16/24-bit, IEEE float, mp3, ogg...)."""
    cmd = [FFMPEG, "-v", "error", "-i", str(path), "-ac", "1", "-ar", str(SR), "-f", "f32le", "-acodec", "pcm_f32le",
           "pipe:1"]
    decoded = subprocess.run(cmd, capture_output=True, check=True)
    return np.frombuffer(decoded.stdout, dtype=np.float32).copy()


def db(x):
    """Decibels to a linear gain factor."""
    return 10 ** (x / 20.0)


def fade(a, ms):
    """Fade both ends of `a` over `ms` milliseconds, in place; returns `a`."""
    ramp_len = min(len(a), int(SR * ms / 1000))
    if ramp_len <= 0:
        return a
    ramp = np.linspace(0, 1, ramp_len, dtype=np.float32)
    a[:ramp_len] *= ramp
    a[-ramp_len:] *= ramp[::-1]
    return a


def load_beds(beds_dir: Path):
    """[(name, samples)] for every WAV in the folder, in name order."""
    files = sorted(beds_dir.glob("*.wav"))
    if not files:
        sys.exit(f"no beds found in {beds_dir}")
    return [(file.stem, read_wav(file)) for file in files]


def _rms(a: np.ndarray) -> float:
    """RMS level of `a`; a tiny number instead of zero, so it is safe to divide by."""
    return float(np.sqrt(np.mean(a.astype(np.float64) ** 2))) or 1e-9


def _random_window(sound: np.ndarray, length: int, rng: random.Random):
    """(a copy of `length` samples of `sound` from a random offset, that offset); a shorter sound is looped."""
    if len(sound) < length:
        sound = np.tile(sound, int(np.ceil(length / len(sound))) + 1)
    start = rng.randint(0, len(sound) - length)
    return sound[start:start + length].copy(), start


# ---- death FX (WingmanDestroyed): the transmission ends the way the ship does ----
# Applied at bake time from the clip's own random generator, so it is the same on every rebuild. Styles:
#   cut   - the voice stops dead inside its last word, a louder static burst fills the hole, the squelch closes.
#   chop  - 2-4 short dropouts (static in the gaps) stutter through the last third, then the cut and burst.
#   drown - static swells up over the voice for the last ~300 ms and takes it, then the cut.
DEATH_FX_SITUATIONS = {"WingmanDestroyed"}
DEATH_FX_STYLES = ("chop",)             # the styles a death line may draw; "cut" and "drown" can be added
DEATH_CUT_IN_LAST_WORD = (0.45, 0.80)   # the cut lands this far into the last word or syllable
DEATH_MIN_CUT_MS = 350                  # at least this much speech is lost; much less does not sound interrupted
DEATH_CUT_EARLY_MS = 70                 # then the cut is pulled this much earlier still
DEATH_MIN_KEEP = 0.55                   # never cut before this fraction of the speech (not the file) has played
DEATH_MAX_SPEECH_S = 4.0                # the signal dies no later than this into the speech, however long the take
DEATH_BURST_MS = (260, 520)             # static burst length after the cut
DEATH_BURST_DB = +6.0                   # burst level relative to the take's overall speech loudness
DEATH_BURST_FADE_MS = 4                 # burst end: as sharp as possible without a click
DEATH_CHOP_N = (2, 4)                   # dropouts
DEATH_CHOP_MS = (25, 70)                # dropout length
DEATH_DROWN_MS = (220, 380)             # static swell length before the cut


# ---- distress layer: a craft in trouble calls for help ----
# A distress line gets more static than other lines and a looping alarm under the voice, chosen by the kind of
# craft that is speaking: 'large' for corvettes and bigger, 'small' for shuttles, transports and freighters. The
# alarm goes through a reverb and the same radio rack as the voice. The levels were set by ear.
DISTRESS_ALARMS = {"large": local_path("DISTRESS_ALARM_LARGE", PIPELINE_DIR / "sfx" / "alarm-large.wav"),
                   "small": local_path("DISTRESS_ALARM_SMALL", PIPELINE_DIR / "sfx" / "alarm-small.wav")}
DISTRESS_STATIC_DB = 3.0            # extra static over BED_TRIM_DB on a distress line
DISTRESS_ALARM_DB = -11.0           # alarm RMS (after the radio rack) relative to the voice's RMS
DISTRESS_ALARM_SECONDS = 20.0       # how much of the loop to prepare (offsets are random within it)
DISTRESS_REVERB = {"rt60_s": 0.45, "predelay_ms": 12, "wet": 0.45}   # a small dry room, roughly
_ALARM_CACHE = {}


def _reverb(x: np.ndarray, rt60_s: float, predelay_ms: float, wet: float, seed: int = 7) -> np.ndarray:
    """Small-room reverb: convolve with exponentially decaying noise."""
    length = int(SR * rt60_s)
    seconds = np.arange(length) / SR
    impulse = np.random.default_rng(seed).standard_normal(length) * np.exp(-6.91 * seconds / rt60_s)  # -60 dB at rt60
    impulse = np.concatenate([np.zeros(int(SR * predelay_ms / 1000)), impulse])
    impulse /= np.sqrt(np.sum(impulse ** 2)) or 1.0
    reverberated = np.convolve(x, impulse)[:len(x)]
    return (1 - wet) * x + wet * reverberated


def distress_alarm(kind: str) -> np.ndarray:
    """The prepared alarm bed for 'large' / 'small': loop -> reverb -> the radio treatment, RMS-normalized to 1.0."""
    if kind not in _ALARM_CACHE:
        import radio            # imported here: a project's own treatment may import this module
        wanted = SR * DISTRESS_ALARM_SECONDS
        alarm = read_wav(DISTRESS_ALARMS[kind]).astype(np.float64)
        if len(alarm) > wanted:
            alarm = alarm[:int(wanted)]
        else:
            alarm = np.tile(alarm, int(np.ceil(wanted / len(alarm))))
        racked = radio.treatment()(_reverb(alarm, **DISTRESS_REVERB))
        racked = racked[int(SR * 0.5):]       # skip the filters' settling
        _ALARM_CACHE[kind] = (racked / (np.sqrt(np.mean(racked ** 2)) or 1e-9)).astype(np.float32)
    return _ALARM_CACHE[kind]


def _rms_envelope(voice: np.ndarray, win: int) -> np.ndarray:
    """Loudness over time: RMS in a sliding window of `win` samples."""
    return np.sqrt(np.convolve(voice.astype(np.float64) ** 2, np.ones(win) / win, mode="same"))


def _envelope(voice: np.ndarray, win_ms=10.0):
    """Loudness over time at 48 kHz: RMS in a sliding window of `win_ms` milliseconds."""
    return _rms_envelope(voice, max(1, int(SR * win_ms / 1000)))


MAX_INTRA_SPEECH_GAP_S = 1.2   # a silence longer than this ends the speech; later blips are artifacts, not words


def _speech_bounds(envelope: np.ndarray, thresh_db: float, max_gap: int):
    """(first, last) sample index of the speech, or None if nothing rises above the threshold (dB under the
    peak). The speech is one chain from the first sound: a silence of up to `max_gap` samples is part of the
    performance ("No... No!"); whatever follows a longer one is a stray noise and is left out."""
    peak = float(envelope.max()) or 1e-9
    loud = np.nonzero(envelope > peak * db(thresh_db))[0]
    if len(loud) == 0:
        return None
    last = int(loud[0])
    for index in loud:
        if index - last > max_gap:
            break
        last = int(index)
    return int(loud[0]), last


def _speech_end(voice: np.ndarray, thresh_db=-40.0):
    """Index just past the last sample of real speech (see _speech_bounds); len(voice) for silence."""
    bounds = _speech_bounds(_envelope(voice), thresh_db, int(SR * MAX_INTRA_SPEECH_GAP_S))
    return len(voice) if bounds is None else bounds[1] + 1


def last_word_start(voice: np.ndarray, end: int, search_ms=700.0, prom_db=5.0, near_ms=220.0):
    """Start of the last word/syllable: the last envelope trough (>= prom_db below the peaks within near_ms on both
    sides) inside the final search_ms of speech. Falls back to end - 250 ms if speech is one unbroken run."""
    level_db = 20 * np.log10(np.maximum(_envelope(voice, 20.0), 1e-6))
    # a trough closer than this to the end is a final consonant or breath, not a word boundary
    min_last_ms = 130
    search_from = max(0, end - int(SR * search_ms / 1000))
    search_to = max(search_from + 1, end - int(SR * min_last_ms / 1000))
    near = int(SR * near_ms / 1000)
    step = int(SR * 0.005)
    for pos in range(search_to - step, search_from, -step):     # backwards: the first hit is the last trough
        here = level_db[pos]
        before = level_db[max(0, pos - near):pos].max()
        after = level_db[pos:min(len(level_db), pos + near)].max()
        if before - here >= prom_db and after - here >= prom_db:
            return pos
    return max(0, end - int(SR * 0.25))


def _bed_slice(beds, rng, n):
    """(`n` samples from a randomly chosen bed, that bed's name)."""
    name, bed = rng.choice(beds)
    window, _ = _random_window(bed, n, rng)
    return window, name


def apply_death_fx(out: np.ndarray, lead: int, voice: np.ndarray, beds, rng: random.Random, style: str):
    """Cut the transmission short. `out` is the mix so far (lead + voice + tail, beds mixed in) and is modified.
    Returns (the shortened mix, a description for the log, the sample index of the cut)."""
    # -30 dB: a take often ends in faint breath that -40 dB counts as speech, which would drag the cut late
    end = lead + _speech_end(voice, thresh_db=-30.0)
    speech_len = end - lead
    earliest_cut = lead + int(speech_len * DEATH_MIN_KEEP)
    # find the last word or syllable and cut partway into it
    word_start = last_word_start(voice, speech_len)
    cut = lead + word_start + int((speech_len - word_start) * rng.uniform(*DEATH_CUT_IN_LAST_WORD))
    cut = min(cut, end - int(SR * DEATH_MIN_CUT_MS / 1000))     # lose at least DEATH_MIN_CUT_MS of the last word...
    cut = max(cut, lead + word_start + int(SR * 0.03))          # ...but let it start (>= 30 ms in)
    cut -= int(SR * DEATH_CUT_EARLY_MS / 1000)
    cut = max(earliest_cut, min(cut, end - int(SR * 0.03)))
    cut = min(cut, lead + int(SR * DEATH_MAX_SPEECH_S))         # after the keep-floor: the cap always wins
    click = int(SR * 0.002)                                     # 2 ms de-click ramps
    burst_len = int(SR * rng.uniform(*DEATH_BURST_MS) / 1000)
    fade_len = int(SR * DEATH_BURST_FADE_MS / 1000)
    desc = [style, f"cut@{(cut - lead)/SR:.2f}s/{len(voice)/SR:.2f}s",
            f"lastword={word_start/SR:.2f}-{speech_len/SR:.2f}s"]
    # static is levelled against the take's overall speech loudness (a local window could land on a dropout)
    voice_rms = _rms(voice)

    if style == "chop":
        chops = rng.randint(*DEATH_CHOP_N)
        chop_from = lead + int(len(voice) * 0.60)
        for _ in range(chops):
            gap = int(SR * rng.uniform(*DEATH_CHOP_MS) / 1000)
            gap_start = rng.randint(chop_from, max(chop_from, cut - gap - click))
            gap_end = gap_start + gap
            hole, _ = _bed_slice(beds, rng, gap)
            out[gap_start - click:gap_start] *= np.linspace(1, 0, click, dtype=np.float32)
            # the dropouts at speech level; the final burst is 6 dB over them
            out[gap_start:gap_end] = fade(hole * (voice_rms / _rms(hole)) * db(DEATH_BURST_DB - 6), 3)
            out[gap_end:gap_end + click] *= np.linspace(0, 1, click, dtype=np.float32)
        desc.append(f"chops={chops}")
    elif style == "drown":
        swell_len = int(SR * rng.uniform(*DEATH_DROWN_MS) / 1000)
        swell_start = max(earliest_cut, cut - swell_len)
        swell, _ = _bed_slice(beds, rng, cut - swell_start)
        ramp = np.linspace(0, 1, cut - swell_start, dtype=np.float32) ** 1.5
        out[swell_start:cut] *= (1 - 0.85 * ramp)                     # the voice ducks under
        out[swell_start:cut] += swell * (voice_rms / _rms(swell)) * db(DEATH_BURST_DB) * ramp
        desc.append(f"swell={swell_len/SR:.2f}s")

    # the cut: everything after it is gone, voice and beds alike (the carrier drops); the burst takes its place
    out[cut - click:cut] *= np.linspace(1, 0, click, dtype=np.float32)
    result = np.zeros(cut + burst_len, dtype=np.float32)
    result[:cut] = out[:cut]
    burst, bed_name = _bed_slice(beds, rng, burst_len)
    burst *= (voice_rms / _rms(burst)) * db(DEATH_BURST_DB)
    # hard-limit the burst to the voice's own peak: it is noise, so clipping is inaudible, and otherwise its crest
    # would hit the peak ceiling when the clip is levelled and drag the speech under the loudness target
    voice_peak = float(np.max(np.abs(voice))) or 1.0
    np.clip(burst, -voice_peak, voice_peak, out=burst)
    burst[:click] *= np.linspace(0, 1, click, dtype=np.float32)
    if fade_len:        # [-0:] would be the whole burst
        burst[-fade_len:] *= np.linspace(1, 0, fade_len, dtype=np.float32)
    result[cut:] += burst
    desc.append(f"burst={burst_len/SR:.2f}s:{bed_name}")
    return result, " ".join(desc), cut


TRIM_LEAD_MS = 100    # keep this much natural room before speech
TRIM_TAIL_MS = 200    # ...and after (the bed's tail covers the rest)


def trim_silence(voice: np.ndarray, sr: int = None, thresh_db: float = -40.0):
    """Cut leading and trailing dead air, keeping a small natural pad. What counts as speech: _speech_bounds."""
    sr = sr or SR
    envelope = _envelope(voice) if sr == SR else _rms_envelope(voice, max(1, int(sr * 0.01)))
    bounds = _speech_bounds(envelope, thresh_db, int(sr * MAX_INTRA_SPEECH_GAP_S))
    if bounds is None:
        return voice
    first, last = bounds
    start = max(0, first - int(sr * TRIM_LEAD_MS / 1000))
    stop = min(len(voice), last + 1 + int(sr * TRIM_TAIL_MS / 1000))
    return voice[start:stop]


def bake_one(voice: np.ndarray, beds, rng: random.Random, situation: str = None, death_style: str = None,
             distress: str = None, death: bool = False):
    """Lay static under one radio-processed take and level it. Returns (the clip, what went into it, for the log).
    distress = 'large' / 'small' adds the distress layer (more static and that craft's alarm); death=True forces
    the death FX whatever the situation (a mayday in a mission's story, say)."""
    voice = trim_silence(voice)
    lead = int(SR * LEAD_MS / 1000)
    tail = int(SR * TAIL_MS / 1000)
    total = lead + len(voice) + tail
    out = np.zeros(total, dtype=np.float32)
    out[lead:lead + len(voice)] += voice
    bed_trim = BED_TRIM_DB + (DISTRESS_STATIC_DB if distress else 0.0)
    if distress:
        alarm, _ = _random_window(distress_alarm(distress), total, rng)
        out += fade(alarm * (_rms(voice) * db(DISTRESS_ALARM_DB)), FADE_MS)

    bed_count = rng.randint(*BEDS_PER_CLIP)
    used = []
    for name, bed in rng.sample(beds, k=min(bed_count, len(beds))):
        layer, start = _random_window(bed, total, rng)
        gain = db(bed_trim + rng.uniform(-BED_WANDER_DB, BED_WANDER_DB))
        out += fade(layer * gain, FADE_MS)
        used.append(f"{name}@{start/SR:.2f}s{20*np.log10(gain):+.1f}dB")

    speech_part = None
    if distress:
        used.append(f"distress:{distress}")
    if (death or situation in DEATH_FX_SITUATIONS) and DEATH_FX_STYLES:
        style = death_style or rng.choice(DEATH_FX_STYLES)
        out, fx_desc, cut = apply_death_fx(out, lead, voice, beds, rng, style)
        used.append("deathfx:" + fx_desc)
        speech_part = out[:cut]     # level on the speech, not the deliberately hot burst, so voices match other clips

    # level on RMS (loudness as heard), capped by the peak ceiling
    loudness_ref = speech_part if speech_part is not None and len(speech_part) else out
    gain = db(TARGET_RMS_DBFS) / _rms(loudness_ref)
    peak = float(np.max(np.abs(out))) or 1e-9
    gain = min(gain, db(PEAK_CEILING_DBFS) / peak)
    out *= gain
    return out, used


def write_ogg(a: np.ndarray, out_path: Path, quality: int = 6):
    """Encode mono 48 kHz samples as OGG Vorbis."""
    out_path.parent.mkdir(parents=True, exist_ok=True)
    pcm = (np.clip(a, -1, 1) * 32767).astype(np.int16).tobytes()
    cmd = [FFMPEG, "-v", "error", "-y", "-f", "s16le", "-ar", str(SR), "-ac", "1", "-i", "pipe:0",
           "-c:a", "libvorbis", "-q:a", str(quality), str(out_path)]
    subprocess.run(cmd, input=pcm, check=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--in", dest="inp", required=True, help="processed-voice WAV file or directory")
    parser.add_argument("--out", required=True, help="output directory for OGGs")
    parser.add_argument("--beds", default=str(DEFAULT_BEDS_DIR), help="directory of pre-rendered bed WAVs")
    parser.add_argument("--seed", type=int, default=None,
                        help="deterministic randomization (per-file seeds derive from this + filename)")
    parser.add_argument("--wav", action="store_true",
                        help="also write a 48k WAV next to each OGG (for comparing by ear)")
    args = parser.parse_args()

    inp = Path(args.inp)
    files = [inp] if inp.is_file() else sorted(inp.glob("*.wav"))
    if not files:
        sys.exit(f"no input WAVs at {inp}")
    beds = load_beds(Path(args.beds))
    print(f"beds: {', '.join(name for name, _ in beds)}")

    out_dir = Path(args.out)
    for file in files:
        # seeded from the file name, so a clip comes out the same on every run
        seed = (args.seed if args.seed is not None else 0) ^ zlib.crc32(file.stem.encode())
        mixed, used = bake_one(read_wav(file), beds, random.Random(seed))
        out = out_dir / (file.stem + ".ogg")
        write_ogg(mixed, out)
        if args.wav:
            with wave.open(str(out_dir / (file.stem + ".wav")), "wb") as wav_file:
                wav_file.setnchannels(1)
                wav_file.setsampwidth(2)
                wav_file.setframerate(SR)
                wav_file.writeframes((np.clip(mixed, -1, 1) * 32767).astype(np.int16).tobytes())
        print(f"  {file.name:32} -> {out.name:32} len={len(mixed)/SR:.2f}s beds=[{' '.join(used)}]")
    print(f"done: {len(files)} clip(s) -> {out_dir}")


if __name__ == "__main__":
    main()
