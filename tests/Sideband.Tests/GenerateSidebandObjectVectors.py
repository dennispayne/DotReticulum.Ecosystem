"""Execute pinned primary object classification, persistence and list selection."""
import argparse
import ast
import hashlib
import json
import math
import os
from pathlib import Path
import sqlite3
import subprocess
import threading
import tempfile
from types import MethodType, SimpleNamespace

import RNS
from RNS.vendor import umsgpack as msgpack

from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
DIGEST = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    revision = subprocess.check_output(
        ["git", "-C", str(args.source), "rev-parse", "HEAD"], text=True).strip()
    if revision != PIN:
        raise RuntimeError(f"Expected {PIN}, got {revision}")
    source = subprocess.check_output(
        ["git", "-C", str(args.source), "show", f"{PIN}:sbapp/sideband/core.py"])
    if hashlib.sha256(source).hexdigest() != DIGEST:
        raise RuntimeError("Unexpected core.py digest")
    core = next(node for node in ast.parse(source).body
                if isinstance(node, ast.ClassDef) and node.name == "SidebandCore")
    names = {"is_object", "_db_conversation_set_object", "_db_conversations",
             "__load_fixed_locations", "fixed_location"}
    methods = [node for node in core.body
               if isinstance(node, ast.FunctionDef) and node.name in names]
    if len(methods) != len(names):
        raise RuntimeError("Actual primary object methods are unavailable")
    namespace = {"RNS": RNS, "msgpack": msgpack, "os": os}
    exec(compile(ast.Module(body=methods, type_ignores=[]), "core.py", "exec"), namespace)
    observations = []
    with sqlite3.connect(":memory:") as database:
        database.execute(
            "CREATE TABLE conv (dest_context BLOB PRIMARY KEY,last_tx REAL,last_rx REAL,"
            "unread INTEGER,type INTEGER,trust INTEGER,name BLOB,data BLOB)")
        peer = bytes.fromhex("00112233445566778899aabbccddeeff")
        other = bytes.fromhex("ffeeddccbbaa99887766554433221100")

        def conversation(address):
            row = database.execute("SELECT data FROM conv WHERE dest_context=?", (address,)).fetchone()
            return None if row is None else {"data": msgpack.unpackb(row[0])}

        owner = SimpleNamespace(
            db_lock=threading.Lock(), _db_conversation=conversation,
            __db_connect=lambda: database)
        for name in names:
            setattr(owner, name, MethodType(namespace[name], owner))
        observations.append({"name": "missing", "is_object": owner.is_object(peer)})
        database.execute("INSERT INTO conv VALUES (?,?,?,?,?,?,?,?)",
                         (peer, 10, 20, 1, 1, 1, b"synthetic object",
                          msgpack.packb({"telemetry": True, "allow_requests": False})))
        database.execute("INSERT INTO conv VALUES (?,?,?,?,?,?,?,?)",
                         (other, 30, 0, 0, 1, 0, b"ordinary", msgpack.packb({})))
        for index, value in enumerate((True, False, True)):
            owner._db_conversation_set_object(peer, value)
            data = conversation(peer)["data"]
            observations.append({
                "name": f"set-{index}", "is_object": owner.is_object(peer),
                "data_hex": msgpack.packb(data).hex(),
                "telemetry": data["telemetry"], "allow_requests": data["allow_requests"],
                "conversations": [row["dest"].hex() for row in owner._db_conversations()],
                "objects": [row["dest"].hex() for row in owner._db_conversations(False, True)],
                "all": [row["dest"].hex() for row in owner._db_conversations(True, True)],
            })
        owner._db_conversation_set_object(peer, False)
        observations.append({
            "name": "unclassified-map", "is_object": owner.is_object(
                peer, {"data": {"telemetry": True}})})
    fixed = []
    with tempfile.TemporaryDirectory(prefix="sideband-fixed-reference-") as directory:
        location_file = Path(directory) / "fixed_locations"
        for name, text in (
                ("normal", f"{peer.hex()},51.5,-1.25\n"),
                ("zero", f"{peer.hex()},0,0\n"),
                ("boundary", f"{peer.hex()},-90,180\n"),
                ("spaces-uppercase", f" {peer.hex().upper()} , 12.5 , -3.25 \n"),
                ("duplicate", f"{peer.hex()},1,2\n{peer.hex()},3,4\n"),
                ("comment-and-blank", f"#synthetic locations\n\n{peer.hex()},1,2\n"),
                ("wrong-columns", "not,a,location,record\n"),
                ("bad-hash", "invalid,1,2\n"),
                ("bad-number", f"{peer.hex()},text,2\n"),
                ("outside-range", f"{peer.hex()},91,2\n"),
                ("nonfinite", f"{peer.hex()},nan,nan\n")):
            location_file.write_bytes(text.encode("utf-8"))
            owner = SimpleNamespace(config={"command_plugins_path": directory}, fixed_locations={})
            namespace["__load_fixed_locations"](owner)
            locations = []
            for address in sorted(owner.fixed_locations):
                result = namespace["fixed_location"](owner, address)
                locations.append({
                    "address": address.hex(),
                    "latitude": result["latitude"] if math.isfinite(result["latitude"]) else str(result["latitude"]),
                    "longitude": result["longitude"] if math.isfinite(result["longitude"]) else str(result["longitude"])})
            fixed.append({"name": name, "text": text, "locations": locations})
    args.output.write_bytes((json.dumps({
        "revision": PIN, "source_sha256": DIGEST,
        "method": "Actual is_object, _db_conversation_set_object and _db_conversations; "
                  "real in-memory SQLite, synthetic connection/row lookup and identity records; "
                  "actual fixed-location loader/getter with temporary synthetic input; "
                  "no daemon, GUI or network claim.",
        "cases": observations, "fixed_locations": fixed}, indent=4, allow_nan=False) + "\n").encode("utf-8"))


if __name__ == "__main__":
    main()
