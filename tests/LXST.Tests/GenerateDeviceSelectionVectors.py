"""Execute primary SoundCard lookup with fake lists and no audio initialization."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_LXST_SHA, verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--platform", choices=["linux", "windows"], default="linux")
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    path = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"]) / "LXST" / "Platforms" / args.platform / "soundcard.py"
    # Execute just the actual lookup function and its actual regex dependency,
    # excluding module initialization, which opens a global PulseAudio context.
    import ast
    import re
    module = ast.parse(path.read_text())
    name = "_match_soundcard" if args.platform == "linux" else "_match_device"
    method = next(item for item in module.body if isinstance(item, ast.FunctionDef) and item.name == name)
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
            if args.platform == "linux":
                item["id"] = scope[name](selector, devices, loopback)["id"]
            else:
                enumerated = [SimpleNamespace(**device) for device in devices
                              if loopback or not device["id"].endswith(".monitor")]
                item["id"] = scope[name](selector, enumerated).id
        except Exception as error:
            item["error"] = type(error).__name__
        cases.append(item)
    result = {
        "revision": PINNED_LXST_SHA,
        "source_sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "method": f"Compiled and executed unchanged AST of actual {name}; only device enumeration input is synthetic.",
        "devices": devices, "cases": cases,
    }
    if args.platform == "windows":
        from LXST.Sinks import WindowsBackend
        calls = []
        backend = WindowsBackend.__new__(WindowsBackend)
        backend.samplerate = 48000
        backend.device = SimpleNamespace(player=lambda **kwargs: calls.append(kwargs))
        backend.get_player(samples_per_frame=960, low_latency=True)
        result["low_latency"] = {
            "actual_player_arguments": calls,
            "runtime_method_defined": any(isinstance(item, ast.FunctionDef) and item.name == "enable_low_latency"
                                          for item in ast.walk(module)),
            "method": "Actual WindowsBackend.get_player with only device creation replaced; unchanged platform module AST checked for runtime method.",
        }
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
