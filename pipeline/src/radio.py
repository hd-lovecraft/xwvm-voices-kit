"""The radio treatment: what makes a dry recording sound as if it came over a cockpit radio, before the static.

The default, `plain`, is a generic two-way radio: a narrow band, some grit, an even level. Its few settings are in
config/radio.yaml. It is a starting point, not a signature sound: bring your own by naming a function in
pipeline/.env,

    RADIO_TREATMENT=my_radio:treat

where my_radio.py is a module Python can import (put it in this folder, or in your own project's) and
treat(samples) takes one take as 48 kHz mono float samples and returns the treated take the same way.
"""
import importlib
import os

import numpy as np
import yaml
from scipy import signal

from common import CONFIG_DIR, load_env

SAMPLE_RATE = 48000
SETTINGS_YAML = CONFIG_DIR / "radio.yaml"
DEFAULTS = {
    "lowcut_hz": 350.0,      # nothing below this: no chest, no rumble
    "highcut_hz": 3400.0,    # nothing above this: the telephone band
    "drive": 4.0,            # how hard the voice is pushed into the soft clipper; 1 = clean
    "level_dbfs": -20.0,     # level the treated take leaves at, so the static sits the same under every voice
}


def settings() -> dict:
    """DEFAULTS, with whatever config/radio.yaml sets laid over them."""
    merged = dict(DEFAULTS)
    if SETTINGS_YAML.exists():
        merged.update(yaml.safe_load(SETTINGS_YAML.read_text(encoding="utf-8")) or {})
    return merged


def _band(samples: np.ndarray, lowcut_hz: float, highcut_hz: float) -> np.ndarray:
    if lowcut_hz > 0:
        samples = signal.sosfilt(signal.butter(4, lowcut_hz, "high", fs=SAMPLE_RATE, output="sos"), samples)
    if highcut_hz > 0:
        samples = signal.sosfilt(signal.butter(4, highcut_hz, "low", fs=SAMPLE_RATE, output="sos"), samples)
    return samples


def plain(samples: np.ndarray) -> np.ndarray:
    """A generic radio voice: band limits, soft clipping for grit, band limits again, then a fixed level."""
    chosen = settings()
    voice = _band(np.asarray(samples, dtype=np.float64), chosen["lowcut_hz"], chosen["highcut_hz"])
    peak = float(np.max(np.abs(voice))) or 1e-9
    voice = np.tanh(voice / peak * float(chosen["drive"]))
    voice = _band(voice, chosen["lowcut_hz"], chosen["highcut_hz"])     # clipping adds harmonics outside the band
    rms = float(np.sqrt(np.mean(voice ** 2))) or 1e-9
    return voice * (10.0 ** (float(chosen["level_dbfs"]) / 20.0) / rms)


def treatment():
    """The function build_voice.py runs every take through: RADIO_TREATMENT ("module:function") or `plain`."""
    load_env()
    named = os.environ.get("RADIO_TREATMENT", "").strip()
    if not named:
        return plain
    module_name, _, function_name = named.partition(":")
    return getattr(importlib.import_module(module_name), function_name or "treat")
