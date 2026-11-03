"""Execute Sideband's JSON export statements, without importing its GUI.

The supplied configuration is test input; the export statements themselves
come from the pinned main.py AST, not a replacement implementation.
"""
import argparse
import ast
import hashlib
import json
from pathlib import Path
import subprocess
from types import SimpleNamespace

import RNS


PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
SOURCE_SHA256 = "5b962d9ed2ccbc066bb19865d78c77da9ecf4008900f6f0e7e9dd1eb1c4767aa"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    revision = subprocess.check_output(
        ["git", "-C", str(args.source), "rev-parse", "HEAD"], text=True).strip()
    if revision != PIN:
        raise RuntimeError(f"Expected {PIN}, got {revision}")
    source = subprocess.check_output(
        ["git", "-C", str(args.source), "show", f"{PIN}:sbapp/main.py"])
    if hashlib.sha256(source).hexdigest() != SOURCE_SHA256:
        raise RuntimeError("Unexpected main.py digest")
    tree = ast.parse(source)
    branch = next(node for node in tree.body
                  if isinstance(node, ast.If)
                  and ast.unparse(node.test) == "args.export_settings")
    export_assignment = next(node for node in branch.body
                             if isinstance(node, ast.Assign)
                             and any(isinstance(target, ast.Name)
                                     and target.id == "export"
                                     for target in node.targets))
    conversion_loop = next(node for node in branch.body if isinstance(node, ast.For))
    config = {
        "display_name": "Reference Peer",
        "dark_ui": False,
        "lxmf_propagation_node": bytes.fromhex("00112233445566778899aabbccddeeff"),
        "last_lxmf_propagation_node": None,
        "nn_home_node": bytes.fromhex("ffeeddccbbaa99887766554433221100"),
        "telemetry_collector": None,
        "propagation_by_default": True,
        "lxmf_try_propagation_on_fail": True,
        "plugin_settings": {"example": [False, 42, "not activated"]},
    }
    namespace = {"sideband": SimpleNamespace(config=config), "RNS": RNS}
    executed = ast.Module(body=[export_assignment, conversion_loop], type_ignores=[])
    exec(compile(executed, str(args.source / "sbapp" / "main.py"), "exec"), namespace)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(namespace["export"], indent=4) + "\n", encoding="utf-8")
    print(json.dumps({"revision": PIN, "source_sha256": SOURCE_SHA256,
                      "method": "main.py export assignment and bytes-to-hex loop",
                      "rns_path": RNS.__file__}))


if __name__ == "__main__":
    main()
