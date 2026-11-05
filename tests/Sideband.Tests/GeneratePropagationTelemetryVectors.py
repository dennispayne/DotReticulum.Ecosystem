"""Execute primary propagation sensor/status APIs; no production Python dependency."""
import argparse
import hashlib
import importlib
import json
import os
from pathlib import Path
import runpy
import subprocess
import sys
from types import SimpleNamespace
from unittest.mock import patch

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    args = parser.parse_args()
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference, PINNED_LXMF_SHA, PINNED_RNS_SHA
    verify_reference(require_lxmf=True)
    source = args.source.resolve()
    if subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip() != PIN:
        raise RuntimeError("Unexpected Sideband revision")
    hashes = {}
    for path in ("sbapp/sideband/sense.py", "docs/example_plugins/lxmd_telemetry.py"):
        blob = subprocess.check_output(["git", "-C", str(source), "show", f"{PIN}:{path}"])
        if (source / path).read_bytes().replace(b"\r\n", b"\n") != blob:
            raise RuntimeError(f"Modified reference {path}")
        hashes[path] = hashlib.sha256(blob).hexdigest()
    sys.path.insert(0, str(source / "sbapp"))
    import RNS
    import LXMF
    from RNS.vendor import umsgpack as mp
    from LXMF.Utilities import lxmd
    from LXMF.LXMPeer import LXMPeer
    sense = importlib.import_module("sideband.sense")
    if Path(sense.__file__).resolve() != source / "sbapp" / "sideband" / "sense.py":
        raise RuntimeError("Unexpected sensor import")
    args.work.mkdir(parents=True, exist_ok=True)
    config = args.work / "rns"
    config.mkdir(exist_ok=True)
    (config / "config").write_text("[reticulum]\n share_instance = No\n enable_transport = No\n[interfaces]\n", encoding="utf-8")
    RNS.Reticulum(configdir=str(config), loglevel=RNS.LOG_ERROR)
    identity = RNS.Identity()
    router = LXMF.LXMRouter(identity=identity, storagepath=str(args.work / "store"), autopeer=False)
    router.enable_propagation()
    status = router.compile_stats()
    # Status hashes are public, ephemeral fixture identities. No private key is exported.
    cases = []
    now = [1000.0]
    queries = []
    responses = [status, LXMPeer.ERROR_NO_ACCESS, None, LXMPeer.ERROR_NO_IDENTITY,
                 LXMPeer.ERROR_TIMEOUT, status]

    def query_status(**kwargs):
        queries.append({"time": now[0], "explicit_same_identity": kwargs == {"identity": identity}})
        return responses.pop(0)

    sensor = sense.LXMFPropagation()
    sensor.lxmd = SimpleNamespace(query_status=query_status)
    sensor.ERROR_NO_IDENTITY = LXMPeer.ERROR_NO_IDENTITY
    sensor.ERROR_NO_ACCESS = LXMPeer.ERROR_NO_ACCESS
    sensor.ERROR_TIMEOUT = LXMPeer.ERROR_TIMEOUT
    errors = []
    with patch.object(sense, "time", SimpleNamespace(time=lambda: now[0])), \
         patch.object(sensor, "_start_update_job", lambda: None), \
         patch.object(RNS, "log", lambda text, *a, **k: errors.append(str(text))):
        sensor._update_data()
        cases.append({"name": "no-identity", "time": now[0], "queries": len(queries),
                      "last_attempt": sensor._last_update, "wire_hex": mp.packb({sensor.sid: sensor.pack()}).hex()})
        sensor.identity = identity
        for name, instant in (("initial", 1000), ("fresh", 1299), ("denied", 1300),
                              ("denied-fresh", 1599), ("empty", 1600), ("no-identification", 1900),
                              ("timeout", 2200), ("recovery", 2500)):
            now[0] = instant
            sensor._update_data()
            cases.append({"name": name, "time": instant, "queries": len(queries),
                          "last_attempt": sensor._last_update, "wire_hex": mp.packb({sensor.sid: sensor.pack()}).hex()})
        now[0] = 2800
        sensor.synthesized = True
        sensor._update_data()
        cases.append({"name": "synthesized", "time": now[0], "queries": len(queries),
                      "last_attempt": sensor._last_update, "wire_hex": mp.packb({sensor.sid: sensor.pack()}).hex()})
        sensor.teardown_sensor()
        teardown = {"running": sensor._running, "identity_cleared": sensor.identity is None,
                    "wire_hex": mp.packb({sensor.sid: sensor.pack()}).hex()}
    # Execute the actual example with inert plugin base/telemeter so no default identity file is opened.
    class Base:
        pass
    plugin = runpy.run_path(str(source / "docs" / "example_plugins" / "lxmd_telemetry.py"),
                            init_globals={"SidebandTelemetryPlugin": Base})["plugin_class"]()
    plugin_calls = []
    telemeter = SimpleNamespace(sensors={})
    def enable(name):
        plugin_calls.append(["enable", name])
        telemeter.sensors[name] = SimpleNamespace(set_identity=lambda path: plugin_calls.append(["identity", path]))
    telemeter.enable = enable
    plugin.update_telemetry(None)
    plugin.update_telemetry(telemeter)
    plugin.update_telemetry(telemeter)
    module_paths = {name: str(Path(module.__file__).resolve()) for name, module in
                    (("RNS", RNS), ("LXMF", LXMF), ("sense", sense), ("lxmd", lxmd))}
    for module in (lxmd, importlib.import_module("LXMF.LXMRouter"), importlib.import_module("LXMF.LXMPeer")):
        hashes[str(Path(module.__file__).resolve())] = hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest()
    result = {"revision": PIN, "lxmf_revision": PINNED_LXMF_SHA, "rns_revision": PINNED_RNS_SHA,
              "source_sha256": hashes, "module_paths": module_paths,
              "method": "Actual imported LXMFPropagation._update_data/pack/teardown; actual LXMRouter.compile_stats from isolated no-interface node; synthetic clock/status responses and suppressed worker launch disclose cadence/error seam, not live query or scheduled-thread interoperability. Actual example plugin with inert plugin base/telemeter; no identity-file read.",
              "stale_seconds": sensor.STALE_TIME, "wake_seconds": sensor._update_interval,
              "query_timeout_seconds": lxmd.query_status.__defaults__[1],
              "error_codes": {"no_identity": LXMPeer.ERROR_NO_IDENTITY, "no_access": LXMPeer.ERROR_NO_ACCESS,
                              "timeout": LXMPeer.ERROR_TIMEOUT},
              "status_hex": mp.packb(status).hex(), "cases": cases, "queries": queries,
              "errors": errors, "plugin_calls": plugin_calls, "teardown": teardown}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(result, indent=2) + "\n")
    router.exit_handler()


if __name__ == "__main__":
    main()
