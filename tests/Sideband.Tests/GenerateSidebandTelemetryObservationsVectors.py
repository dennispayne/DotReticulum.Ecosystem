"""Observe original primary receive enrichment with actual Telemeter and in-memory SQLite."""
import argparse
import ast
import hashlib
import importlib
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import threading
from types import MethodType, SimpleNamespace

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
HASHES = {
    "core": "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25",
    "sense": "80418b1aa4de638f2c65f1bd64e5ea0466378a6f0bc682781672ef89b93a7dab",
    "geo": "af416ae7a1c087cc0c636e994ff58d5c6f7edf9f85379765d7fda9d130e780c7",
}
NOW = 1700000000
RECEIVER, SENDER, CLAIMED_SOURCE, ORIGINAL_RECEIVER, OLD_RELAY = (bytes([i]) * 16 for i in (17, 34, 51, 68, 85))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference, PINNED_RNS_SHA, PINNED_LXMF_VERSION
    verify_reference(require_lxmf=True)
    import RNS
    import LXMF
    from RNS.vendor import umsgpack as mp

    root = args.source.resolve()
    if subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip() != PIN:
        raise RuntimeError("Unexpected primary checkout")
    sources = {}
    for name, digest in HASHES.items():
        blob = subprocess.check_output(["git", "-C", str(root), "show", f"{PIN}:sbapp/sideband/{name}.py"])
        if hashlib.sha256(blob).hexdigest() != digest:
            raise RuntimeError(f"Unexpected primary {name} digest")
        if (root / "sbapp" / "sideband" / f"{name}.py").read_bytes().replace(b"\r\n", b"\n") != blob.replace(b"\r\n", b"\n"):
            raise RuntimeError(f"Modified primary {name}")
        sources[name] = blob
    sys.path.insert(0, str(root / "sbapp"))
    sense = importlib.import_module("sideband.sense")
    geo = importlib.import_module("sideband.geo")
    for name, module in (("sense", sense), ("geo", geo)):
        if Path(module.__file__).resolve() != (root / "sbapp" / "sideband" / f"{name}.py").resolve():
            raise RuntimeError(f"Unexpected actual {name} import")
    clock = {"now": NOW}
    sense.time = SimpleNamespace(time=lambda: clock["now"])
    core = next(n for n in ast.parse(sources["core"]).body if isinstance(n, ast.ClassDef) and n.name == "SidebandCore")
    methods = [n for n in core.body if isinstance(n, ast.FunctionDef) and n.name in ("_db_inittelemetry", "_db_save_telemetry")]
    ns = {"RNS": RNS, "LXMF": LXMF, "Telemeter": sense.Telemeter, "time": sense.time,
          "valid_telemetry_timebase": sense.valid_telemetry_timebase}
    exec(compile(ast.Module(body=methods, type_ignores=[]), "core.py", "exec"), ns)
    save_lxm = next(n for n in core.body if isinstance(n, ast.FunctionDef) and n.name == "_db_save_lxm")
    prefix = []
    for node in save_lxm.body:
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "attach_msg" for t in node.targets):
            break
        prefix.append(node)
    receipt_code = compile(ast.Module(body=prefix, type_ignores=[]), "core.py:receive-prefix", "exec")
    db = sqlite3.connect(":memory:")
    owner = SimpleNamespace(__db_connect=lambda: db, db_lock=threading.Lock(),
                            lxmf_destination=SimpleNamespace(hash=RECEIVER), latest_telemetry=None,
                            config={"telemetry_to_mqtt": False, "telemetry_receive_trusted_only": False},
                            setstate=lambda *_: None, setpersistent=lambda *_: None,
                            _telemetry_timebase=lambda _: 0, _db_update_appearance=lambda *_, **__: None)
    for method in methods:
        setattr(owner, method.name, MethodType(ns[method.name], owner))
    owner._db_inittelemetry()
    save_telemetry = owner._db_save_telemetry
    invocations = []

    def observe_save(context, packed, **kwargs):
        result = save_telemetry(context, packed, **kwargs)
        invocations.append({"context": context.hex(), "kwargs": {
            key: value.hex() if isinstance(value, bytes) else value for key, value in kwargs.items()
        }, "result_hex": result.hex() if result is not None else None})
        return result

    owner._db_save_telemetry = observe_save

    def packed_report(case):
        t = sense.Telemeter(from_packed=True)
        t.synthesize("time")
        t.sensors["time"].data = {"utc": case.get("timestamp", NOW - 1)}
        t.synthesize("information")
        t.sensors["information"].data = {"contents": "Synthetic observation reference"}
        if case.get("remote") is not None:
            lat, lon, alt = case["remote"]
            t.synthesize("location")
            t.sensors["location"].data = dict(latitude=lat, longitude=lon, altitude=alt, speed=0.0,
                                             bearing=0.0, accuracy=1.0, last_update=NOW - 100.125)
        if case.get("received") == "present":
            t.synthesize("received")
            received = t.sensors["received"]
            received.by, received.via = ORIGINAL_RECEIVER, OLD_RELAY
            received.geodesic_distance, received.euclidian_distance = 12345.6789, 9876.54321
            received.update_data()
        if case.get("existing_link"):
            t.synthesize("physical_link")
            link = t.sensors["physical_link"]
            link.rssi, link.snr, link.q = -101.5, -2.5, 18.0
            link.update_data()
        data = mp.unpackb(t.packed())
        if case.get("received") == "nil":
            data[sense.Sensor.SID_RECEIVED] = None
        if case.get("received") == "malformed":
            data[sense.Sensor.SID_RECEIVED] = [ORIGINAL_RECEIVER]
        if case.get("nil_location"):
            data[sense.Sensor.SID_LOCATION] = None
        if case.get("unknown"):
            data[0x80] = {"synthetic": [42, None, b"safe"]}
        return mp.packb(data)

    pairs = [
        ("london-paris", [51.5, -0.125, 10.0], [48.8566, 2.3522, 35.0]),
        ("short", [51.5, -0.125, -10.0], [51.50001, -0.12499, -9.0]),
        ("equator", [0.0, 0.0, 0.0], [0.0, 1.0, 0.0]),
        ("same", [51.5, -0.125, 10.0], [51.5, -0.125, 10.0]),
        ("vertical", [0.0, 0.0, 0.0], [0.0, 0.0, 100.0]),
        ("antipodal", [0.0, 0.0, 0.0], [0.0, 180.0, 0.0]),
        ("dateline", [10.0, 179.9, 0.0], [10.0, -179.9, 20.0]),
    ]
    cases = []
    for forwarded in (False, True):
        for received in ("absent", "present", "nil", "malformed"):
            for physical in ((None, None, None), (0, 0, 0), (-90.0, 0.0, 0.0), (None, 10.0, None), (None, None, 50.0)):
                cases.append(dict(name="metadata", forwarded=forwarded, received=received, physical=physical, existing_link=True))
        for name, own, remote in pairs:
            cases.append(dict(name=name, forwarded=forwarded, own=own, remote=remote, received="present", physical=(-80, 5, 75)))
        cases.extend([
            dict(name="no-received", forwarded=forwarded, physical=(None, None, None)),
            dict(name="nil-location", forwarded=forwarded, own=pairs[0][1], nil_location=True),
            dict(name="no-own-location", forwarded=forwarded, remote=pairs[0][2], received="present"),
            dict(name="large-integer-time", forwarded=forwarded, timestamp=9007199254740993, now=9007199254740994),
            dict(name="fractional-time", forwarded=forwarded, timestamp=NOW - 1.125),
            dict(name="unknown-safe-sensor", forwarded=forwarded, unknown=True),
        ])
    observations = []
    for case in cases:
        db.execute("DELETE FROM telemetry")
        db.commit()
        invocations.clear()
        clock["now"] = case.get("now", NOW)
        own = case.get("own")
        owner.latest_telemetry = None if own is None else {"location": dict(zip(("latitude", "longitude", "altitude"), own))}
        packed = packed_report(case)
        physical = case.get("physical", (None, None, None))
        forwarded = case["forwarded"]
        fields = {LXMF.FIELD_TELEMETRY_STREAM: [[CLAIMED_SOURCE, clock["now"], packed, None]]} if forwarded else {LXMF.FIELD_TELEMETRY: packed}
        ns.update(self=owner, lxm=SimpleNamespace(state=0, fields=fields, rssi=physical[0], snr=physical[1], q=physical[2]),
                  context_dest=SENDER, originator=False)
        exec(receipt_code, ns)
        if len(invocations) != 1 or invocations[0]["result_hex"] is None:
            raise RuntimeError(f"Reference unexpectedly rejected {case}")
        row = db.execute("SELECT dest_context,ts,data FROM telemetry").fetchone()
        result = bytes(row[2])
        if result.hex() != invocations[0]["result_hex"]:
            raise RuntimeError("Reference return/database mismatch")
        observations.append({"input": dict(case, receiver=RECEIVER.hex(), sender=SENDER.hex(), physical=physical),
                             "input_hex": packed.hex(), "output_hex": result.hex(),
                             "save_call": invocations[0], "stored_context": bytes(row[0]).hex(),
                             "stored_timestamp": row[1]})
    db.close()
    args.output.write_text(json.dumps({
        "revision": PIN, "source_sha256": HASHES, "clock": NOW,
        "reference": {"rns_revision": PINNED_RNS_SHA, "rns_version": RNS.__version__, "lxmf_version": PINNED_LXMF_VERSION},
        "method": "Actual _db_save_lxm telemetry prefix invoking actual AST-extracted _db_save_telemetry/_db_inittelemetry, actual imported Telemeter/sensors/geo and real in-memory SQLite. Declared synthetic clock, locations, public hashes; injected database/state/appearance APIs. No full core, signature validation, hardware, GUI, network, MQTT or geoid assets.",
        "observations": observations,
        "contracts": [
            "Direct data sets received.by to local delivery destination, via to LXMF sender; distances use local/remote transmitted coordinates when available, otherwise replace previous distances with nil.",
            "Forwarded data retains claimed by and existing distances, replaces via with relay. Missing/nil/malformed Received is synthesized as nil by/distances. This is claimed provenance, not authentication of the original source.",
            "Only direct outer messages contribute local physical-link observations. An all-zero/nil physical tuple does not overwrite existing physical metadata. A nonzero tuple replaces the entire previous physical sensor, retaining explicitly supplied zero/nil components.",
            "Direct _db_save_telemetry with a nonempty all-zero physical_link dictionary WOULD overwrite physical data; the helper models the actual _db_save_lxm caller's omission rule instead.",
            "Time and location update values are not replaced with the receiver clock, including integer timestamps above double's exact range.",
            "Native enrichment preserves other wire values and unknown safe sensors. The primary Telemeter drops unknown sensors during repacking; the unknown-safe-sensor observations explicitly expose that difference.",
            "Caller must validate signature/sender context and source timestamp, enforce receive/trust/privacy/stream limits and distinguish an asserted stream source from the authenticated relay. Helper performs no such authentication, policy enforcement or storage."
        ]}, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(f"Generated {len(observations)} actual SQLite/Telemeter receive-enrichment observations")


if __name__ == "__main__":
    main()
