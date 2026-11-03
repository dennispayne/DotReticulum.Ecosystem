"""Observe pinned Sideband persistent scheduling methods without changing their source.

Unused resource/telemetry/plugin/platform imports are inert stubs. The scheduler
uses a fixed clock, no-op sleep/delivery-limit update, and a recording router
boundary; a config accessor stops it before unrelated telemetry work. These are
disclosed harness shims, not GUI, network or background-service interoperability.
SQLite, MessagePack, getpersistent/setpersistent, _periodic_jobs and
request_lxmf_sync execute their actual upstream implementations.
"""
import argparse
import base64
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import threading
import types


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sideband", required=True, type=Path)
    parser.add_argument("--rns", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--public-fixture", type=Path)
    args = parser.parse_args()
    source = args.sideband.resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != "d069122af14880e43607aed0b71add4c7806fbe0":
        raise RuntimeError("Unexpected primary Sideband revision")
    core_path = source / "sbapp" / "sideband" / "core.py"
    fingerprint = hashlib.sha256(core_path.read_bytes()).hexdigest()
    if fingerprint != "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25":
        raise RuntimeError("Unexpected core.py fingerprint")
    if subprocess.check_output(
            ["git", "-C", str(source), "status", "--porcelain", "--untracked-files=no", "--", "sbapp"],
            text=True).strip():
        raise RuntimeError("Sideband reference source has local modifications")
    sys.path.insert(0, str(args.rns.resolve()))
    import RNS
    import LXMF
    from reference_environment import verify_reference, PINNED_RNS_SHA, PINNED_LXMF_SHA
    os.environ["DOTRETICULUM_UPSTREAM"] = str(args.rns.resolve())
    verify_reference(require_lxmf=True)
    for package in ["sbapp", "sbapp.sideband"]:
        module = types.ModuleType(package)
        module.__path__ = []
        sys.modules[package] = module
    stubs = {
        "sbapp.sideband.res": {"sideband_fb_data": b""},
        "sbapp.sideband.sense": {"Telemeter": object, "Commands": object,
                                "valid_telemetry_timebase": lambda _: True, "TB_DIFF_GRACE": 0},
        "sbapp.sideband.plugins": {"SidebandCommandPlugin": object, "SidebandServicePlugin": object,
                                  "SidebandTelemetryPlugin": object},
        "sbapp.sideband.mqtt": {"MQTT": object},
        "sbapp.plyer": {},
    }
    for name, members in stubs.items():
        module = types.ModuleType(name)
        module.__dict__.update(members)
        sys.modules[name] = module
    spec = importlib.util.spec_from_file_location("sbapp.sideband.core", core_path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False, mode=0o700)

    def create_core(name):
        core = module.SidebandCore.__new__(module.SidebandCore)
        core.db = None
        core.db_lock = threading.Lock()
        core.db_path = str(output / f"{name}.db")
        core._SidebandCore__db_init()
        return core

    class EndSchedule(Exception):
        pass

    class ScheduleConfig(dict):
        def __getitem__(self, key):
            if key == "telemetry_enabled":
                raise EndSchedule()
            return super().__getitem__(key)

    class RecordingRouter:
        def __init__(self, accepted):
            self.propagation_transfer_state = LXMF.LXMRouter.PR_IDLE if accepted else LXMF.LXMRouter.PR_RECEIVING
            self.requests = 0

        def request_messages_from_propagation_node(self, identity, max_messages=None):
            self.requests += 1

        def get_outbound_propagation_node(self):
            return bytes.fromhex("00112233445566778899aabbccddeeff")

    def rows(core):
        return [{"property": bytes(key).hex(), "value": base64.b64encode(value).decode()}
                for key, value in core.db.execute("SELECT property, value FROM persistent ORDER BY property")]

    cases = []
    specifications = [
        ("empty", {}, None, True, False),
        ("integer_timestamp", {"lxmf.lastsync": 1760000000, "lxmf.syncretrying": False}, None, True, False),
        ("fractional_timestamp", {"lxmf.lastsync": 1760000000.25, "lxmf.syncretrying": True}, None, True, False),
        ("negative_timestamp", {"lxmf.lastsync": -100.25, "lxmf.syncretrying": False}, None, True, False),
        ("large_finite_timestamp", {"lxmf.lastsync": 1e300}, None, True, False),
        ("unsigned_timestamp", {"lxmf.lastsync": 18446744073709551615}, None, True, False),
        ("isolated_retry", {"lxmf.syncretrying": True}, None, True, False),
        ("null_values", {"lxmf.lastsync": None, "lxmf.syncretrying": None}, None, True, False),
        ("initial_schedule", {}, 1760002000.5, True, False),
        ("disabled_preserves", {"lxmf.lastsync": 1760000000, "lxmf.syncretrying": True}, 1760002000.5, False, False),
        ("exact_boundary", {"lxmf.lastsync": 1760000000, "lxmf.syncretrying": False}, 1760001800, True, False),
        ("first_rejection", {"lxmf.lastsync": 1760000000, "lxmf.syncretrying": False}, 1760002000.5, True, False),
        ("second_rejection", {"lxmf.lastsync": 1760000000, "lxmf.syncretrying": True}, 1760002000.5, True, False),
        ("accepted_request", {"lxmf.lastsync": 1760000000, "lxmf.syncretrying": True}, 1760002000.5, True, True),
        ("future_timestamp", {"lxmf.lastsync": 1761000000, "lxmf.syncretrying": True}, 1760002000.5, True, False),
        ("unknown_state", {"fixture.unsupported": {"binary": b"\x00\x01"}}, None, True, False),
    ]
    for name, initial, now, enabled, accepted in specifications:
        core = create_core(name)
        if not cases:
            empty_database = base64.b64encode(Path(core.db_path).read_bytes()).decode()
        for key, value in initial.items():
            core.setpersistent(key, value)
        queued = 0
        if now is not None:
            core.is_service = True
            core.owner_service = None
            core._update_delivery_limits = lambda: None
            core.config = ScheduleConfig(lxmf_periodic_sync=enabled, lxmf_sync_interval=1800)
            core.identity = RNS.Identity()
            core.message_router = RecordingRouter(accepted)
            module.time = types.SimpleNamespace(time=lambda: now, sleep=lambda _: None)
            try:
                core._periodic_jobs()
            except EndSchedule:
                pass
            queued = core.message_router.requests
        snapshot_rows = rows(core)
        cases.append({
            "name": name, "rows": snapshot_rows,
            "initialLastSynchronization": initial.get("lxmf.lastsync"),
            "initialSynchronizationRetrying": initial.get("lxmf.syncretrying"),
            "now": now, "enabled": enabled, "requestAccepted": accepted,
            "lastSynchronization": core.getpersistent("lxmf.lastsync"),
            "synchronizationRetrying": core.getpersistent("lxmf.syncretrying"),
            "queuedRequests": queued,
        })
        core.db.close()

    core = create_core("wal")
    core.setpersistent("lxmf.lastsync", 1760000000)
    core.setpersistent("lxmf.syncretrying", False)
    core.db.execute("PRAGMA journal_mode=WAL")
    core.db.execute("PRAGMA wal_autocheckpoint=0")
    core.db.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    core.setpersistent("lxmf.lastsync", 1760000360.25)
    core.setpersistent("lxmf.syncretrying", True)
    wal_directory = output / "wal-snapshot"
    wal_directory.mkdir(mode=0o700)
    wal_files = {}
    for suffix in ["", "-wal", "-shm"]:
        original = Path(core.db_path + suffix)
        shutil.copyfile(original, wal_directory / ("sideband.db" + suffix))
        wal_files["sideband.db" + suffix] = base64.b64encode(original.read_bytes()).decode()
    wal_observation = {
        "lastSynchronization": core.getpersistent("lxmf.lastsync"),
        "synchronizationRetrying": core.getpersistent("lxmf.syncretrying"),
    }
    core.db.close()
    fixture = {
        "sideband": revision, "coreSha256": fingerprint, "rns": PINNED_RNS_SHA, "lxmf": PINNED_LXMF_SHA,
        "rnsModule": str(Path(RNS.__file__).resolve()), "lxmfModule": str(Path(LXMF.__file__).resolve()),
        "methods": "Actual SidebandCore.__db_init, setpersistent, getpersistent, _periodic_jobs and request_lxmf_sync",
        "shims": ["Unused subsystem import stubs", "Fixed core clock and no-op sleep",
                  "No-op delivery-limit update", "Recording router boundary", "Config accessor stops before telemetry"],
        "emptyDatabase": empty_database, "cases": cases, "walFiles": wal_files, "walObservation": wal_observation,
    }
    (output / "observations.json").write_text(json.dumps(fixture, indent=2) + "\n", encoding="utf-8")
    if args.public_fixture:
        public = {key: value for key, value in fixture.items() if key not in ["rnsModule", "lxmfModule"]}
        args.public_fixture.write_text(json.dumps(public, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: fixture[key] for key in ["sideband", "rns", "lxmf", "methods", "shims"]}))


if __name__ == "__main__":
    main()
