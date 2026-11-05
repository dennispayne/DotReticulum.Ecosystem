"""Observe the pinned selected-message telemetry copy callback, not an implementation."""
import argparse
import ast
import hashlib
import importlib
import json
import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
HASHES = {
    "sbapp/ui/messages.py": "9b2580f76310356d2e1a30b038857fbad4ee346aad9e2a25ab237515fff39928",
    "sbapp/sideband/sense.py": "80418b1aa4de638f2c65f1bd64e5ea0466378a6f0bc682781672ef89b93a7dab",
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference
    verify_reference(require_lxmf=True)
    source = args.source.resolve()
    if subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip() != PIN:
        raise RuntimeError("Unexpected primary revision")
    blobs = {}
    for path, digest in HASHES.items():
        blob = subprocess.check_output(["git", "-C", str(source), "show", f"{PIN}:{path}"])
        if hashlib.sha256(blob).hexdigest() != digest:
            raise RuntimeError(f"Unexpected primary Git source: {path}")
        if (source / path).read_bytes().replace(b"\r\n", b"\n") != blob.replace(b"\r\n", b"\n"):
            raise RuntimeError(f"Modified primary imported source: {path}")
        blobs[path] = blob
    sys.path.insert(0, str(source / "sbapp"))
    sense = importlib.import_module("sideband.sense")
    if Path(sense.__file__).resolve() != source / "sbapp" / "sideband" / "sense.py":
        raise RuntimeError("Unexpected actual sensor module")
    from RNS.vendor import umsgpack as mp
    functions = [node for node in ast.walk(ast.parse(blobs["sbapp/ui/messages.py"]))
                 if isinstance(node, ast.FunctionDef) and node.name == "gen_copy_telemetry"]
    if len(functions) != 1:
        raise RuntimeError("Primary selected-message copy callback changed")
    corpus = json.loads((Path(__file__).parent / "Fixtures" / "sideband-telemetry.json").read_text(encoding="utf-8"))
    inputs = [(item["name"], item["wire_hex"], {}) for item in corpus["cases"]]
    inputs += [("all-sensors", corpus["all_wire_hex"], {}),
               ("link-zero", corpus["all_wire_hex"], {"rssi": 0.0, "snr": 0.0, "quality": 0.0}),
               ("link-replacement", corpus["all_wire_hex"], {"rssi": -91.5, "quality": 77.0}),
               ("missing-report", None, {})]
    raw_links = {"link-ui-rounding": {"q": 77.45, "rssi": -91.55, "snr": 3.25}}
    rounding = {node.targets[0].id: node for node in ast.walk(ast.parse(blobs["sbapp/ui/messages.py"]))
                if isinstance(node, ast.Assign) and len(node.targets) == 1
                and isinstance(node.targets[0], ast.Name) and node.targets[0].id in ("lq", "lr", "ls")}
    if set(rounding) != {"lq", "lr", "ls"}:
        raise RuntimeError("Primary link rounding assignments changed")
    namespace = {"phy_stats": raw_links["link-ui-rounding"]}
    for node in rounding.values():
        exec(compile(ast.Module(body=[node], type_ignores=[]), "messages.py", "exec"), namespace)
    inputs.append(("link-ui-rounding", corpus["all_wire_hex"],
                   {"quality": namespace["lq"], "rssi": namespace["lr"], "snr": namespace["ls"]}))
    for name, value in (
            ("float-boundaries", [0.0, -0.0, 1e-4, 9.999e-5, 1e16, 1e15, 1e17,
                                  5e-324, 1.7976931348623157e308, 2.2250738585072014e-308]),
            ("text-and-binary", ["'quoted'", '"quoted"', "both'\"", "\0\a\v\f\t\n\r",
                                 "\u00a0\u200b\u2028\u2603\U0001f600", bytes(range(256))])):
        custom = sense.Telemeter(from_packed=True)
        custom.synthesize("custom")
        custom.sensors["custom"].update_entry(value)
        wire = mp.packb({custom.sensors["custom"].sid: custom.sensors["custom"].pack()})
        inputs.append((name, wire.hex(), {}))
    cases = []
    for name, wire, extras in inputs:
        events = []
        copied = []
        errors = []
        telemeter = sense.Telemeter.from_packed(bytes.fromhex(wire)) if wire else None
        namespace = {"Clipboard": SimpleNamespace(copy=lambda text: (events.append("copy"), copied.append(text))),
                     "RNS": SimpleNamespace(LOG_ERROR=1, log=lambda text, _: errors.append(text))}
        exec(compile(ast.Module(body=functions, type_ignores=[]), "messages.py", "exec"), namespace)
        item = SimpleNamespace(dmenu=SimpleNamespace(dismiss=lambda: events.append("dismiss")))
        namespace["gen_copy_telemetry"](telemeter, extras, item)()
        readings = telemeter.read_all() if telemeter is not None else None
        cases.append({"name": name, "wire_hex": wire, "extra_link": extras, "copied": copied,
                      "raw_link": raw_links.get(name, {}),
                      "readings_hex": mp.packb(readings).hex(), "events": events, "error_count": len(errors)})
    result = {
        "revision": PIN, "source_sha256": HASHES,
        "method": "Actual AST-extracted gen_copy_telemetry and UI lq/lr/ls rounding assignments; imported Telemeter.from_packed/read_all and existing independently executed corpus; synthetic Clipboard/menu/logger only. No latest-source lookup, primary GUI, OS clipboard, device frames or network.",
        "cases": cases,
    }
    with args.output.open("w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(result, indent=4) + "\n")


if __name__ == "__main__":
    main()
