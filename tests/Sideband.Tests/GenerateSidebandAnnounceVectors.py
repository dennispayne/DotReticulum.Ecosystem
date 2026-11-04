"""Execute primary announcement SQLite methods and actual pinned LXMF providers."""
import argparse
import ast
import hashlib
import importlib
import inspect
import json
from pathlib import Path
import sqlite3
import subprocess
import threading
from types import MethodType, SimpleNamespace

import LXMF
import RNS
from RNS.vendor import umsgpack as msgpack

from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
CORE = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"


class Clock:
    now = 1700000000

    def time(self):
        return self.now

    def __str__(self):
        return "synthetic offline reference clock"


def methods(source, class_name, names, namespace):
    owner = next(node for node in ast.parse(source).body
                 if isinstance(node, ast.ClassDef) and node.name == class_name)
    selected = [node for node in owner.body
                if isinstance(node, ast.FunctionDef) and node.name in names]
    if len(selected) != len(names):
        raise RuntimeError("Pinned reference methods are unavailable")
    exec(compile(ast.Module(body=selected, type_ignores=[]), class_name, "exec"), namespace)
    return namespace


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    revision = subprocess.check_output(
        ["git", "-C", str(args.source), "rev-parse", "HEAD"], text=True).strip()
    dirty = subprocess.check_output(
        ["git", "-C", str(args.source), "status", "--porcelain"], text=True)
    if revision != PIN or dirty:
        raise RuntimeError("Unexpected or modified primary source")
    source = subprocess.check_output(
        ["git", "-C", str(args.source), "show", f"{PIN}:sbapp/sideband/core.py"])
    if hashlib.sha256(source).hexdigest() != CORE:
        raise RuntimeError("Unexpected core.py digest")
    clock = Clock()
    names = {"log_announce", "list_announces", "_db_announces",
             "_db_save_announce", "delete_announce", "_db_delete_announce"}
    namespace = methods(source, "SidebandCore", names, {
        "RNS": RNS, "LXMF": LXMF, "msgpack": msgpack, "time": clock})
    router_path = Path(inspect.getfile(LXMF.LXMRouter))
    router_globals = vars(importlib.import_module("LXMF.LXMRouter")).copy()
    router_globals["time"] = clock
    pn_namespace = methods(router_path.read_bytes(), "LXMRouter", {
        "get_propagation_node_app_data", "get_propagation_node_announce_metadata"},
        router_globals)
    signature = inspect.signature(LXMF.LXMRouter)
    defaults = lambda name: signature.parameters[name].default
    pn = SimpleNamespace(
        name="Synthetic propagation node", propagation_node=True, from_static_only=False,
        propagation_per_transfer_limit=defaults("propagation_limit"),
        propagation_per_sync_limit=defaults("sync_limit"),
        propagation_stamp_cost=defaults("propagation_cost"),
        propagation_stamp_cost_flexibility=defaults("propagation_cost_flexibility"),
        peering_cost=defaults("peering_cost"))
    pn.get_propagation_node_announce_metadata = MethodType(
        pn_namespace["get_propagation_node_announce_metadata"], pn)
    pn_data = pn_namespace["get_propagation_node_app_data"](pn)
    changes, snapshots, operations, costs = [], [], [], {}
    with sqlite3.connect(":memory:") as database:
        database.execute("CREATE TABLE announce (id BLOB,received REAL,source BLOB,"
                         "data BLOB,dest_type TEXT,extra BLOB)")
        owner = SimpleNamespace(
            MAX_ANNOUNCES=48, db_lock=threading.Lock(), __db_connect=lambda: database,
            message_router=SimpleNamespace(get_outbound_stamp_cost=lambda address: costs.get(address)),
            setstate=lambda name, value: changes.append([name, value]))
        for name in names:
            setattr(owner, name, MethodType(namespace[name], owner))

        def capture(name):
            snapshots.append({"name": name, "records": [
                {**entry, "dest": entry["dest"].hex()}
                for entry in owner.list_announces()]})

        def save(index, data, kind="lxmf.delivery", stamp=None, stats=None):
            address = index.to_bytes(16, "big")
            clock.now += 1
            costs[address] = stamp
            owner.log_announce(address, data, kind, stamp, stats)
            row = database.execute("SELECT data FROM announce WHERE source=?", (address,)).fetchone()
            operations.append({"address": address.hex(), "received": clock.now,
                               "app_data": row[0].hex(), "type": kind, "cached_cost": stamp})
            return address

        capture("empty")
        delivery = SimpleNamespace(display_name="Synthetic peer", stamp_cost=13)
        provider = SimpleNamespace(delivery_destinations={bytes(16): delivery})
        app_data = LXMF.LXMRouter.get_announce_app_data(provider, bytes(16))
        address = save(1, app_data, stamp=13, stats={"rssi": -77.0, "snr": 12.5, "q": 94.0})
        save(2, None)
        save(3, b"Legacy peer", stamp=7)
        save(4, pn_data, "lxmf.propagation")
        capture("delivery-empty-legacy-and-propagation")
        delivery.display_name = "Renamed peer"
        delivery.stamp_cost = 17
        save(1, LXMF.LXMRouter.get_announce_app_data(provider, bytes(16)), stamp=17)
        capture("same-source-replaces-and-moves")
        costs[address] = 23
        capture("current-router-cost-is-read-not-stored-cost")
        owner.delete_announce(address)
        owner.delete_announce(address)
        capture("delete-is-idempotent")
        database.execute("DELETE FROM announce")
        for index in range(1, 54):
            save(index, app_data, stamp=13)
        capture("primary-prunes-before-insert-retains-49")
        save(53, app_data, stamp=13)
        capture("duplicate-source-after-limit-retains-48")
    result = {
        "revision": PIN, "core_sha256": CORE,
        "lxmf_router_sha256": hashlib.sha256(router_path.read_bytes()).hexdigest(),
        "method": "Unmodified primary SQLite/log/list/delete methods; actual LXMF delivery and propagation providers. Offline SQLite, controlled clock, explicit cached-cost/state observers and synthetic router owner; no network, primary GUI or native lifecycle claim. Local database IDs are excluded, not wire identifiers.",
        "operations": operations, "snapshots": snapshots, "state_changes": changes,
        "pn_app_data": pn_data.hex(),
        "pn_metadata_keys": {"name": LXMF.PN_META_NAME, "implementation": LXMF.PN_META_IMPL_NAME,
                             "version": LXMF.PN_META_VERSION},
    }
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps({"revision": PIN, "snapshots": len(snapshots),
                      "primary_peak": len(snapshots[-2]["records"])}))


if __name__ == "__main__":
    main()
