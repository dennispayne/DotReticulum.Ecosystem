"""Observe actual primary received/logged announce rows in an upstream-created schema."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import sqlite3
import subprocess
import tempfile
import threading
from types import MethodType, SimpleNamespace
from contextlib import closing

import LXMF
import RNS
from RNS.vendor import umsgpack as msgpack

from GenerateSidebandAnnounceVectors import Clock, CORE, PIN, methods
from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    if subprocess.check_output(["git", "-C", str(args.source), "rev-parse", "HEAD"], text=True).strip() != PIN:
        raise RuntimeError("Unexpected primary revision")
    if subprocess.check_output(["git", "-C", str(args.source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Modified primary source")
    source = subprocess.check_output(["git", "-C", str(args.source), "show", f"{PIN}:sbapp/sideband/core.py"])
    if hashlib.sha256(source).hexdigest() != CORE:
        raise RuntimeError("Unexpected core source")
    directory = Path(__file__).parent
    empty = json.loads((directory / "Fixtures" / "sideband-profile.json").read_text(encoding="utf-8"))
    prior = json.loads((directory / "Fixtures" / "sideband-announces.json").read_text(encoding="utf-8"))
    if empty["upstream"] != PIN or empty["coreSha256"] != CORE or prior["revision"] != PIN:
        raise RuntimeError("Prior executed-reference fixture pin mismatch")
    clock = Clock()
    names = {"received_announce", "log_announce", "list_announces", "_db_announces",
             "_db_save_announce", "delete_announce", "_db_delete_announce", "_db_upgradetables"}
    namespace = methods(source, "SidebandCore", names, {
        "RNS": RNS, "LXMF": LXMF, "msgpack": msgpack, "time": clock,
        "SidebandCore": SimpleNamespace(aspect_filter="lxmf.delivery")})
    costs = {}
    with tempfile.TemporaryDirectory(prefix="sideband-reference-announces-") as root:
        path = Path(root) / "sideband.db"
        path.write_bytes(base64.b64decode(empty["emptyDatabase"], validate=True))
        with closing(sqlite3.connect(path)) as database:
            owner = SimpleNamespace(
                MAX_ANNOUNCES=48, db_lock=threading.Lock(), __db_connect=lambda: database,
                message_router=SimpleNamespace(get_outbound_stamp_cost=lambda address: costs.get(address)),
                setstate=lambda name, value: None,
                reticulum=SimpleNamespace(get_packet_rssi=lambda _: -77.0,
                                          get_packet_snr=lambda _: 12.5, get_packet_q=lambda _: 94.0))
            for name in names:
                setattr(owner, name, MethodType(namespace[name], owner))
            owner._db_upgradetables()
            for operation in prior["operations"][:4]:
                address = bytes.fromhex(operation["address"])
                clock.now = operation["received"]
                data = bytes.fromhex(operation["app_data"])
                costs[address] = operation["cached_cost"]
                if operation["type"] == "lxmf.delivery":
                    owner.received_announce(address, None, data, bytes(32))
                else:
                    owner.log_announce(address, data, operation["type"])
            rows = [{
                "received": received, "source": address.hex(),
                "data": data.hex(), "type": kind, "extra": extra.hex() if extra is not None else None
            } for received, address, data, kind, extra in database.execute(
                "SELECT received,source,data,dest_type,extra FROM announce ORDER BY received")]
            listed = [{**item, "dest": item["dest"].hex()} for item in owner.list_announces()]
            columns = [{"name": row[1], "type": row[2]} for row in database.execute("PRAGMA table_info(announce)")]
            schema = database.execute("SELECT sql FROM sqlite_master WHERE type='table' AND name='announce'").fetchone()[0]
            if len(rows) != 4 or len(listed) != 4:
                raise RuntimeError("Actual persistence methods did not produce all four observations")
    result = {
        "revision": PIN, "core_sha256": CORE,
        "method": "Unchanged primary received_announce/log/list/SQLite bodies and _db_upgradetables in an upstream-created empty schema. Synthetic clock, cached-cost and PHY observers only; no network, hardware or GUI claim. Input LXMF providers are independently executed pinned fixtures; receiver actually normalizes delivery app data before persistence. Local database IDs excluded. A DB snapshot alone lacks the router cost cache, so offline import cannot reconstruct listed delivery costs.",
        "columns": columns, "schema": schema, "rows": rows, "listed_with_router_cache": listed,
    }
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps({"revision": PIN, "rows": len(rows), "sha256": hashlib.sha256(args.output.read_bytes()).hexdigest()}))


if __name__ == "__main__":
    main()
