"""Actual primary passive discovery facade over real pinned RNS announcements.

The GUI method executes from its verified AST because Kivy is not loaded. A
synthetic advertised TCP endpoint and deterministic candidate search use actual
RNS advertisement/stamp APIs; no autoconnect, device or multiprocessing claim.
"""
import argparse
import ast
import hashlib
import inspect
from pathlib import Path
import subprocess
from types import SimpleNamespace

import RNS
import RNS.Discovery as discovery
from LXMF import LXStamper

from live_lxmf_node import Handler, command_lines, emit, write_config
from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
MAIN_HASH = "5b962d9ed2ccbc066bb19865d78c77da9ecf4008900f6f0e7e9dd1eb1c4767aa"


def select_stamp(material, stamp_cost, expand_rounds):
    block = LXStamper.stamp_workblock(material, expand_rounds=expand_rounds)
    candidate = 0
    while True:
        stamp = candidate.to_bytes(32, "big")
        if LXStamper.stamp_valid(stamp, stamp_cost, block):
            return stamp, LXStamper.stamp_value(block, stamp)
        candidate += 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, default=0)
    parser.add_argument("--source", type=Path, required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    root = args.source.resolve(strict=True)
    revision = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"]).decode().strip()
    blob = subprocess.check_output(["git", "-C", str(root), "show", f"{PIN}:sbapp/main.py"])
    path = root / "sbapp" / "main.py"
    if revision != PIN or hashlib.sha256(blob).hexdigest() != MAIN_HASH or path.read_bytes().replace(b"\r\n", b"\n") != blob:
        raise RuntimeError("Unexpected primary facade source")
    methods = [node for node in ast.walk(ast.parse(blob))
               if isinstance(node, ast.FunctionDef) and node.name == "list_discovered_interfaces"]
    if len(methods) != 1:
        raise RuntimeError("Missing or ambiguous actual primary facade")
    namespace = {"RNS": RNS}
    exec(compile(ast.fix_missing_locations(ast.Module(body=methods, type_ignores=[])), str(path), "exec"), namespace)
    facade = namespace["list_discovered_interfaces"]
    if Path(inspect.getsourcefile(facade)).resolve() != path:
        raise RuntimeError("Unexpected executed facade path")
    write_config(str(args.config), args.tcp_server_port, args.rnsd_port, enable_transport=True)
    runtime = RNS.Reticulum(configdir=str(args.config), loglevel=RNS.LOG_ERROR)
    listener = discovery.InterfaceDiscovery(required_value=8, discover_interfaces=True)
    RNS.Transport.register_announce_handler(Handler())
    announcer = RNS.Transport.interface_announcer or discovery.InterfaceAnnouncer(RNS.Transport)
    announcer.stamper = SimpleNamespace(generate_stamp=select_stamp)
    destination = announcer.discovery_destination
    app = SimpleNamespace(if_discovery=None)
    interface = type("TCPServerInterface", (), {})()
    interface.__dict__.update({
        "discovery_stamp_value": 8, "discovery_location": None,
        "discovery_name": "Actual Python discovery", "discovery_latitude": 0.0,
        "discovery_longitude": 0.0, "discovery_height": None,
        "discovery_lxmf_address": None, "reachable_on": "reference.example",
        "bind_port": 4242, "discovery_publish_ifac": True,
        "ifac_netname": "synthetic-public-network", "ifac_netkey": "synthetic-discovery-secret",
        "discovery_encrypt": False,
    })
    emit({"event": "ready", "dest": destination.hash.hex(), "identity": destination.identity.hash.hex(),
          "revision": PIN, "source_sha256": MAIN_HASH, "imported_discovery": discovery.__file__,
          "facade_path": str(path), "discover_interfaces": True, "autoconnect": 0})
    try:
        for line in command_lines():
            command = line.strip()
            if command == "quit":
                break
            if command == "announce":
                payload = announcer.get_interface_announce_data(interface)
                if payload is None:
                    raise RuntimeError("Actual Python advertisement generation failed")
                destination.announce(app_data=payload)
                emit({"event": "announced"})
            elif command == "list":
                rows = facade(app)
                public = []
                for row in rows:
                    public.append({
                        "hash": row["discovery_hash"].hex(), "name": row["name"],
                        "type": row["type"], "transport": row["transport"],
                        "network_id": row["network_id"], "transport_id": row["transport_id"],
                        "latitude": row.get("latitude"), "longitude": row.get("longitude"),
                        "status": row["status"], "hops": row["hops"],
                    })
                emit({"event": "listed", "interfaces": public,
                      "passive": not hasattr(app.if_discovery, "handler")})
            else:
                raise ValueError("Unsupported discovery operation")
    finally:
        del listener
        runtime.exit_handler()


if __name__ == "__main__":
    main()
