"""Execute Sideband's JSON export statements, without importing its GUI.

The supplied configuration is test input; the export statements themselves
come from the pinned main.py AST, not a replacement implementation.
"""
import argparse
import ast
import base64
import hashlib
import json
from pathlib import Path
import subprocess
from types import SimpleNamespace

import RNS
import LXMF
import RNS.vendor.umsgpack as msgpack


PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
SOURCE_SHA256 = "5b962d9ed2ccbc066bb19865d78c77da9ecf4008900f6f0e7e9dd1eb1c4767aa"
CORE_SHA256 = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"


def propagation_vectors(source_root, output):
    source = subprocess.check_output(
        ["git", "-C", str(source_root), "show", f"{PIN}:sbapp/sideband/core.py"])
    if hashlib.sha256(source).hexdigest() != CORE_SHA256:
        raise RuntimeError("Unexpected core.py digest")
    detector = next(node for node in ast.parse(source).body
                    if isinstance(node, ast.ClassDef) and node.name == "PropagationNodeDetector")
    now = 1_000_000
    old, candidate, manual = (bytes([value]) * 16 for value in (1, 2, 3))
    cases = []
    for name, configured, previous_hops, hops, emitted, enabled in [
        ("first", False, None, 2, now, True),
        ("closer", False, 3, 2, now, True),
        ("equal", False, 2, 2, now, True),
        ("farther", False, 1, 2, now, True),
        ("manual", True, 3, 1, now, True),
        ("future-boundary", False, None, 1, now + 300, True),
        ("future-rejected", False, None, 1, now + 301, True),
        ("old-accepted", False, None, 1, now - 86_400, True),
        ("disabled-flag-ignored", False, None, 1, now, False),
    ]:
        owner = SimpleNamespace(
            owner_app=None, config={"lxmf_propagation_node": manual if configured else None},
            active_propagation_node=old if previous_hops is not None else None,
            reticulum=SimpleNamespace(get_packet_rssi=lambda _: None,
                                     get_packet_snr=lambda _: None,
                                     get_packet_q=lambda _: None),
            log_announce=lambda *_, **__: None)
        owner.set_active_propagation_node = lambda value: setattr(owner, "active_propagation_node", value)
        oracle_rns = SimpleNamespace(
            Transport=SimpleNamespace(hops_to=lambda value: hops if value == candidate else previous_hops),
            sl=lambda _: False, LOG_EXTREME=0, LOG_ERROR=0, log=lambda *_, **__: None)
        namespace = {"RNS": oracle_rns, "time": SimpleNamespace(time=lambda: now),
                     "msgpack": msgpack, "pn_announce_data_is_valid": LXMF.pn_announce_data_is_valid}
        exec(compile(ast.Module(body=[detector], type_ignores=[]), "core.py", "exec"), namespace)
        data = msgpack.packb([0, emitted, enabled, 256, 1024, [16, 3, 18], {}])
        assert LXMF.pn_announce_data_is_valid(data)
        namespace["PropagationNodeDetector"](owner).received_announce(candidate, None, data, b"")
        cases.append({"name": name, "manual": configured, "previous_hops": previous_hops,
                      "hops": hops, "now": now, "announce": base64.b64encode(data).decode(),
                      "selected": owner.active_propagation_node == candidate})
    output.write_text(json.dumps({"revision": PIN, "source_sha256": CORE_SHA256,
                                 "method": "actual PropagationNodeDetector; injected clock/hop/stat dependencies",
                                 "cases": cases}, indent=4) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--propagation-output", type=Path)
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
    if args.propagation_output:
        propagation_vectors(args.source, args.propagation_output)


if __name__ == "__main__":
    main()
