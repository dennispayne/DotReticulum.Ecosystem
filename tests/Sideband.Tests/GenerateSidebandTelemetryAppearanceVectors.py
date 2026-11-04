"""Observe actual primary appearance byte conversion without a GUI, devices or network."""
import argparse
import ast
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys
from types import MethodType, SimpleNamespace

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
DIGEST = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference
    verify_reference(require_lxmf=True)
    import RNS
    import LXMF
    if subprocess.check_output(["git", "-C", str(args.source), "rev-parse", "HEAD"], text=True).strip() != PIN:
        raise RuntimeError("Unexpected primary revision")
    source = subprocess.check_output(["git", "-C", str(args.source), "show", f"{PIN}:sbapp/sideband/core.py"])
    if hashlib.sha256(source).hexdigest() != DIGEST:
        raise RuntimeError("Unexpected primary source")
    core = next(n for n in ast.parse(source).body if isinstance(n, ast.ClassDef) and n.name == "SidebandCore")
    method = next(n for n in core.body if isinstance(n, ast.FunctionDef) and n.name == "get_message_fields")
    namespace = {"RNS": RNS, "LXMF": LXMF, "struct": struct}
    exec(compile(ast.Module(body=[method], type_ignores=[]), "core.py:appearance", "exec"), namespace)
    owner = SimpleNamespace(should_send_telemetry=lambda _: False, latest_packed_telemetry=None)
    observe = MethodType(namespace["get_message_fields"], owner)
    vectors = []
    for channel in [n / 255 for n in range(256)] + [0.1, 0.3, 0.5, 0.75, 0.499999999]:
        rgba = [channel, channel, channel, 1]
        owner.config = {"telemetry_send_appearance": True, "telemetry_icon": "account",
                        "telemetry_fg": rgba, "telemetry_bg": [1, 1, 1, 1]}
        appearance = observe(bytes(16))[LXMF.FIELD_ICON_APPEARANCE]
        vectors.append({"rgba": rgba, "foreground": appearance[1].hex(), "background": appearance[2].hex()})
    args.output.write_text(json.dumps({"revision": PIN, "source_sha256": DIGEST,
        "method": "Actual AST-extracted SidebandCore.get_message_fields; synthetic config and conversation, actual pinned RNS/LXMF constants",
        "vectors": vectors}, indent=2) + "\n", encoding="utf-8")
    print(f"Executed {len(vectors)} actual primary appearance observations")


if __name__ == "__main__":
    main()
