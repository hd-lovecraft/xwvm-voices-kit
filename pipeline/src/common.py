"""Paths and settings shared by the build steps."""
import os
import re
from pathlib import Path

# The project this run works on: its config/, its .env, its working folders, and content/ beside it.
# Default: this repo's own pipeline/ folder. A project that keeps this kit in a subfolder sets CHATTER_PIPELINE_DIR.
PIPELINE_DIR = Path(os.environ.get("CHATTER_PIPELINE_DIR") or Path(__file__).resolve().parent.parent).resolve()
REPO_DIR = PIPELINE_DIR.parent
CONFIG_DIR = PIPELINE_DIR / "config"
RAW_DIR = PIPELINE_DIR / "raw"
BUILD_DIR = PIPELINE_DIR / "build"
CONTENT_XWING = REPO_DIR / "content" / "xwing"
CHATTER_DIR = CONTENT_XWING / "VOICE" / "_chatter"      # one folder of clips per voice
CHATTER_SIDECARS = CONTENT_XWING / "_chatter"           # the casting files; only the plugin reads this folder
CASTING_YAML = CONFIG_DIR / "casting.yaml"
LINES_CSV = CONFIG_DIR / "lines.csv"


def load_env():
    """Read NAME=value settings into the environment without overriding what is already set: pipeline/.env
    first, then pipeline/config/paths.env (a project may keep its folder settings there)."""
    lines = []
    for env_file in (PIPELINE_DIR / ".env", CONFIG_DIR / "paths.env"):
        if env_file.exists():
            lines += env_file.read_text(encoding="utf-8").splitlines()
    for line in lines:
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        os.environ.setdefault(key.strip(), value.strip().strip('"').strip("'"))


def slug(name: str) -> str:
    """Folder and file form of a name: 'Iris Calder' -> 'iris-calder'."""
    return re.sub(r"[^a-z0-9]+", "-", (name or "").lower()).strip("-")


def local_path(name: str, default) -> Path:
    """A folder or file on this machine: NAME=path in the environment or pipeline/.env, else `default`."""
    load_env()
    value = os.environ.get(name, "").strip()
    return Path(value) if value else Path(default)


def xwing_dir() -> Path:
    """The original X-Wing install (its mission files are read, never changed): XWING_GAME_DIR in pipeline/.env."""
    return local_path("XWING_GAME_DIR", r"C:\Program Files (x86)\Steam\steamapps\common\STAR WARS X-Wing")
