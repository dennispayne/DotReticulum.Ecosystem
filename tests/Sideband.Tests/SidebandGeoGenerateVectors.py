"""Execute primary geo math and telemetry SQLite policies with synthetic, hardware-free inputs."""
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
CORE = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"
NOW = 1700000000


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference
    verify_reference(require_lxmf=True)
    root = args.source.resolve()
    revision = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
    if revision != PIN:
        raise RuntimeError("Unexpected primary revision")
    sources, hashes = {}, {}
    for name in ("geo", "core", "sense"):
        blob = subprocess.check_output(["git", "-C", str(root), "show", f"{PIN}:sbapp/sideband/{name}.py"])
        actual = (root / "sbapp" / "sideband" / f"{name}.py").read_bytes()
        if actual.replace(b"\r\n", b"\n") != blob.replace(b"\r\n", b"\n"):
            raise RuntimeError(f"Modified {name} source")
        sources[name], hashes[name] = blob, hashlib.sha256(blob).hexdigest()
    if hashes["core"] != CORE:
        raise RuntimeError("Unexpected core digest")
    sys.path.insert(0, str(root / "sbapp"))
    geo = importlib.import_module("sideband.geo")
    sense = importlib.import_module("sideband.sense")
    if Path(geo.__file__).resolve() != root / "sbapp" / "sideband" / "geo.py":
        raise RuntimeError("Unexpected geo import")
    import RNS
    from RNS.vendor import umsgpack as mp
    sense.time = SimpleNamespace(time=lambda: NOW)
    pairs = [
        ("london-paris", (51.5, -0.125, 10.0), (48.8566, 2.3522, 35.0)),
        ("sydney-tokyo", (-33.8688, 151.2093, 5.0), (35.6762, 139.6503, 100.0)),
        ("dateline", (10.0, 179.9, 0.0), (10.0, -179.9, 20.0)),
        ("equator", (0.0, 0.0, 0.0), (0.0, 1.0, 0.0)),
        ("same", (51.5, -0.125, 10.0), (51.5, -0.125, 10.0)),
        ("vertical", (0.0, 0.0, 0.0), (0.0, 0.0, 100.0)),
        ("poles", (90.0, 0.0, 0.0), (-90.0, 0.0, 0.0)),
        ("antipodal", (0.0, 0.0, 0.0), (0.0, 180.0, 0.0)),
        ("near-antipodal", (0.001, 0.0, 0.0), (-0.001, 179.9999, 0.0)),
        ("short", (51.5, -0.125, -10.0), (51.50001, -0.12499, -9.0)),
    ]
    geo_cases = []
    for name, first, second in pairs:
        for ellipsoid in (True, False):
            geo_cases.append({"name": name, "first": first, "second": second, "ellipsoid": ellipsoid,
                              "euclidian": geo.euclidian_distance(first, second, ellipsoid=ellipsoid),
                              "orthodromic": geo.orthodromic_distance(first, second, ellipsoid=ellipsoid),
                              "azalt": geo.azalt(first, second, ellipsoid=ellipsoid)})
    horizons = [{"height": h, "distance": geo.distance_to_horizon((0, 0, h)),
                 "angle": geo.angle_to_horizon((0, 0, h)), "radio": geo.radio_horizon(h)}
                for h in (0.0, 1.0, 10.0, 100.0, 1000.0, 10000.0)]
    shared = [{"first": a, "second": b, "result": geo.shared_radio_horizon(a, b)}
              for a, b in [((0, 0, 0), (0, 0, 0)), ((51.5, 0, 10), (51.501, 0, 10)),
                           ((51.5, 0, 1), (52.5, 0, 1))]]
    geo_errors = {}
    for name, call in [
        ("two_coordinate_chord", lambda: geo.euclidian_distance((0, 0), (1, 1))),
        ("ellipsoid_horizon", lambda: geo.distance_to_horizon((0, 0, 1), ellipsoid=True)),
        ("ellipsoid_radio", lambda: geo.radio_horizon(1, ellipsoid=True)),
        ("negative_horizon", lambda: geo.distance_to_horizon((0, 0, -1))),
    ]:
        try:
            geo_errors[name] = {"returned": call()}
        except Exception as error:
            geo_errors[name] = {"exception": type(error).__name__}

    core = next(n for n in ast.parse(sources["core"]).body if isinstance(n, ast.ClassDef) and n.name == "SidebandCore")
    method_names = ("_db_inittelemetry", "_db_save_telemetry", "_db_telemetry", "_db_clean_telemetry",
                    "_db_clear_telemetry", "create_telemetry_collector_response",
                    "should_send_telemetry", "allow_request_from", "requests_allowed_from", "is_trusted")
    methods = [n for n in core.body if isinstance(n, ast.FunctionDef) and n.name in method_names]
    constants = [n for n in core.body if isinstance(n, ast.Assign) and
                 any(isinstance(t, ast.Name) and t.id in ("TELEMETRY_KEEP", "TELEMETRY_FUTURE_GRACE", "TELEMETRY_CLEAN_INTERVAL")
                     for t in n.targets)]
    ns = {"RNS": RNS, "Telemeter": sense.Telemeter, "time": sense.time, "TB_DIFF_GRACE": sense.TB_DIFF_GRACE,
          "valid_telemetry_timebase": sense.valid_telemetry_timebase}
    exec(compile(ast.Module(body=constants + methods, type_ignores=[]), "core.py", "exec"), ns)
    db = sqlite3.connect(":memory:")
    owner = SimpleNamespace(__db_connect=lambda: db, db_lock=threading.Lock(),
                            TELEMETRY_KEEP=ns["TELEMETRY_KEEP"], TELEMETRY_FUTURE_GRACE=ns["TELEMETRY_FUTURE_GRACE"],
                            latest_telemetry=None, config={"telemetry_to_mqtt": False},
                            setstate=lambda *_: None, last_telemetry_clean=0)
    for name in method_names:
        setattr(owner, name, MethodType(ns[name], owner))
    owner._db_inittelemetry()
    first, second, excluded, recipient = (bytes([i]) * 16 for i in (1, 2, 3, 4))
    samples = [(first, NOW - 100, "first"), (second, NOW - 50, "second"),
               (first, NOW - 10, "newest"), (first, NOW - 10, "duplicate-different-payload"),
               (first, NOW + 240, "future-boundary"), (first, NOW + 241, "future-rejected"),
               (excluded, NOW - 20, "excluded"), (recipient, NOW - 30, "recipient")]
    append_cases = []
    for source, ts, contents in samples:
        wire = mp.packb({1: ts, 15: contents})
        accepted = owner._db_save_telemetry(source, wire) is not None
        append_cases.append({"source": source.hex(), "timestamp": ts, "wire_hex": wire.hex(), "accepted": accepted})
    queries = []
    for name, kwargs in [
        ("global-unfiltered-limit-bug", {"limit": 1}),
        ("global-after", {"after": NOW - 100, "limit": 3}),
        ("first-source", {"context_dest": first}),
        ("exclusive-window", {"context_dest": first, "after": NOW - 100, "before": NOW + 240}),
        ("empty", {"after": NOW + 240}),
    ]:
        result = owner._db_telemetry(**kwargs)
        queries.append({"name": name, "arguments_hex": mp.packb(kwargs).hex(), "result_hex": mp.packb(result).hex()})
    owner.list_telemetry = lambda **kwargs: owner._db_telemetry(**kwargs)
    owner.getstate = lambda _: False
    owner._db_get_appearance = lambda *_, **__: None
    owner.send_latest_telemetry = lambda **kwargs: kwargs
    owner.telemetry_response_excluded = [excluded]
    streams = []
    for latest in (True, False):
        owner.config["telemetry_requests_only_send_latest"] = latest
        result = owner.create_telemetry_collector_response(recipient, NOW - 100, is_collector_response=True)
        streams.append({"latest_only": latest, "result_hex": mp.packb(result).hex()})
    privacy = []
    for enabled in (False, True):
        for trust in (0, 1, 2):
            for per_conversation in (False, True):
                conversation = {"trust": trust, "data": {"telemetry": per_conversation, "allow_requests": per_conversation}}
                owner._db_conversation = lambda _, c=conversation: c
                owner.config.update(telemetry_enabled=enabled, telemetry_send_to_trusted=True,
                                    telemetry_allow_requests_from_anyone=False, telemetry_allow_requests_from_trusted=True)
                privacy.append({"enabled": enabled, "trust": trust, "per_conversation": per_conversation,
                                "send": owner.should_send_telemetry(first), "allow_request": owner.allow_request_from(first)})
    owner._db_clear_telemetry(first)
    cleared = mp.packb(owner._db_telemetry()).hex()
    db.execute("DELETE FROM telemetry")
    retention_inputs = [NOW - ns["TELEMETRY_KEEP"] - 1, NOW - ns["TELEMETRY_KEEP"],
                        NOW - 241, NOW - 240, NOW - 239, NOW, NOW + 240]
    for i, ts in enumerate(retention_inputs):
        db.execute("INSERT INTO telemetry (dest_context,ts,data) VALUES (?,?,?)",
                   (first, ts, mp.packb({1: ts, 15: f"retention-{i}"})))
    db.commit()
    owner._db_clean_telemetry()
    retained = [row[0] for row in db.execute("SELECT ts FROM telemetry ORDER BY ts")]
    db.close()
    defaults = {}
    for node in ast.walk(core):
        if isinstance(node, ast.Assign) and isinstance(node.value, ast.Constant):
            for target in node.targets:
                if isinstance(target, ast.Subscript) and isinstance(target.value, ast.Attribute) and target.value.attr == "config":
                    if isinstance(target.slice, ast.Constant) and str(target.slice.value).startswith("telemetry_"):
                        defaults.setdefault(target.slice.value, node.value.value)
    args.output.write_text(json.dumps({
        "revision": PIN, "source_sha256": hashes, "clock": NOW,
        "method": "Actual imported geo functions; AST-extracted primary core methods with real in-memory SQLite, clock/response/database/conversation dependencies. No full core, hardware, GUI, geoid data or network.",
        "geo_constants": {"equatorial_radius": geo.equatorial_radius, "polar_radius": geo.polar_radius,
                          "mean_earth_radius": geo.mean_earth_radius},
        "geo": geo_cases, "horizons": horizons, "shared_horizons": shared, "geo_errors": geo_errors,
        "append": append_cases, "queries": queries, "streams": streams, "privacy": privacy,
        "after_clear_hex": cleared, "retention": {"keep": ns["TELEMETRY_KEEP"], "future_grace": ns["TELEMETRY_FUTURE_GRACE"],
            "clean_interval": ns["TELEMETRY_CLEAN_INTERVAL"], "inputs": retention_inputs, "retained": retained,
            "defect": "Primary cleanup uses now - future_grace, NOT now + future_grace; recent valid telemetry is deleted."},
        "defaults": defaults,
        "native_design_differences": [
            "Original bounded atomic binary store, NOT a Sideband SQLite-compatible file. Uses source timestamp from packed sensor ID 1; received time is not substituted.",
            "Native query always applies newest-first ordering and a hard limit; primary global query without bounds ignores both.",
            "Privacy send/receive/display/exclusion policy and collector streams remain parent application integration; store never sends data.",
            "Native geo accepts typed finite geographic coordinates and clamps inverse-trig roundoff. No geoid/AAMSL assets or external service.",
            "Ellipsoid inverse retains upstream equator perturbation and a bounded 10000-iteration convergence limit; no replacement geodesic result is invented for nonconvergence."
        ]}, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(f"Generated {len(geo_cases)} geo cases, actual SQLite query/append/retention, collector and privacy observations; geo SHA256 {hashes['geo']}")


if __name__ == "__main__":
    main()
