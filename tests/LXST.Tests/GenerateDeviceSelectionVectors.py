"""Execute primary Linux SoundCard lookup with fake device lists; no PulseAudio server required."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    path = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"]) / "LXST" / "Platforms" / "linux" / "soundcard.py"
    # Execute just the actual lookup function and its actual regex dependency,
    # excluding module initialization, which opens a global PulseAudio context.
    import ast
    import re
    module = ast.parse(path.read_text())
    method = next(item for item in module.body if isinstance(item, ast.FunctionDef) and item.name == "_match_soundcard")
    scope = {"re": re}
    exec(compile(ast.Module(body=[method], type_ignores=[]), str(path), "exec"), scope)
    devices = [
        {"id": "out0", "name": "Primary Speakers"},
        {"id": "out1", "name": "Desk Soundbar"},
        {"id": "duplicate-first", "name": "Duplicate"},
        {"id": "duplicate-last", "name": "Duplicate"},
        {"id": "source.monitor", "name": "Monitor Input"},
    ]
    cases = []
    for selector, loopback in [("out1", False), ("Sound", False), ("DS", False), ("PS", False),
                               ("Duplicate", False), ("source.monitor", False), ("source.monitor", True),
                               ("", False), ("not-found", False), ("D[", False)]:
        item = {"selector": selector, "loopback": loopback}
        try:
            item["id"] = scope["_match_soundcard"](selector, devices, loopback)["id"]
        except Exception as error:
            item["error"] = type(error).__name__
        cases.append(item)
    Path(args.output).write_text(json.dumps({
        "revision": "3b596f48ddd252ddc9c315ef8f3020cfd3688223",
        "source_sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "method": "Compiled and executed unchanged AST of actual _match_soundcard; only device enumeration input is synthetic.",
        "devices": devices, "cases": cases,
    }, indent=2) + "\n")


if __name__ == "__main__":
    main()
