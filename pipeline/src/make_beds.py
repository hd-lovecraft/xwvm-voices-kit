"""Write a plain set of radio static beds, so the example builds without any audio of your own.

    src/make_beds.py            -> pipeline/beds/static1.wav ... static4.wav  (or RADIO_BEDS_DIR)

A bed is a stretch of static the build lays under a voice; each clip gets a random slice of one or two. These are
band-limited noise with a slow flutter and a few crackles: serviceable, not characterful. Record or design your own
and put them in the same folder (any WAV, mono, at the level you want them heard under a voice at about -20 dBFS).
Nothing is overwritten: the script stops if the folder already holds WAV files.
"""
import argparse
import sys
import wave

import numpy as np
from scipy import signal

from common import PIPELINE_DIR, local_path

SAMPLE_RATE = 48000
BED_SECONDS = 20
BED_COUNT = 4
BED_LEVEL_DBFS = -37.5      # about 22 dB under a treated voice once the build's own trim is applied


def bed(seed: int) -> np.ndarray:
    """One bed: the same samples for the same seed."""
    rng = np.random.default_rng(seed)
    length = SAMPLE_RATE * BED_SECONDS
    hiss = signal.sosfilt(signal.butter(2, [400, 3800], "band", fs=SAMPLE_RATE, output="sos"),
                          rng.standard_normal(length))
    # the level drifts a little, a few times a second, as a weak signal does
    flutter = signal.sosfilt(signal.butter(2, 3.0, "low", fs=SAMPLE_RATE, output="sos"), rng.standard_normal(length))
    flutter = 0.8 + 0.2 * flutter / (np.max(np.abs(flutter)) or 1.0)
    crackle = np.zeros(length)
    pops = rng.integers(0, length, size=BED_SECONDS * 3)
    crackle[pops] = rng.uniform(-1.0, 1.0, size=len(pops)) * 8.0
    crackle = signal.sosfilt(signal.butter(2, [1200, 3600], "band", fs=SAMPLE_RATE, output="sos"), crackle)
    static = hiss * flutter + crackle
    return static * (10.0 ** (BED_LEVEL_DBFS / 20.0) / np.sqrt(np.mean(static ** 2)))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.parse_args()
    folder = local_path("RADIO_BEDS_DIR", PIPELINE_DIR / "beds")
    if folder.exists() and any(folder.glob("*.wav")):
        sys.exit(f"{folder} already holds WAV files; nothing written")
    folder.mkdir(parents=True, exist_ok=True)
    for number in range(1, BED_COUNT + 1):
        samples = np.clip(bed(number), -1.0, 1.0)
        with wave.open(str(folder / f"static{number}.wav"), "wb") as out:
            out.setnchannels(1)
            out.setsampwidth(2)
            out.setframerate(SAMPLE_RATE)
            out.writeframes((samples * 32767).astype("<i2").tobytes())
    print(f"wrote {BED_COUNT} beds of {BED_SECONDS} s to {folder}")


if __name__ == "__main__":
    main()
