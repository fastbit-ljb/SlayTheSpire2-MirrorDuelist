"""Build MirrorDuelist.pck - localization only (no textures needed: the
monster reuses vanilla character visuals)."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from pck_tool import write_pck  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
MOD_ID = "MirrorDuelist"
OUT = ROOT / "build" / "MirrorDuelist.pck"


def localization_entries() -> dict:
    entries = {}
    for lang in ("eng", "zhs"):
        for file in sorted((ROOT / "localization" / lang).glob("*.json")):
            entries[f"{MOD_ID}/localization/{lang}/{file.name}"] = file.read_bytes()
    return entries


def build() -> Path:
    files = localization_entries()
    OUT.parent.mkdir(parents=True, exist_ok=True)
    write_pck(OUT, files, engine=(4, 5, 1))
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes, {len(files)} files)")
    return OUT


if __name__ == "__main__":
    build()
