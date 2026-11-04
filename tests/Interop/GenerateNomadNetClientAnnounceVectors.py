"""Execute the primary client announce/job methods with disclosed clock/API observers."""
import hashlib
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys
from types import MethodType, SimpleNamespace

from reference_environment import verify_reference


def main():
    verify_reference(require_lxmf=True)
    source = Path(os.environ["DOTRETICULUM_NOMADNET_SOURCE"]).resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2":
        raise RuntimeError("Unexpected primary revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Modified primary source")
    sys.path.insert(0, str(source))
    module = importlib.import_module("nomadnet.NomadNetworkApp")
    if Path(module.__file__).resolve() != source / "nomadnet/NomadNetworkApp.py":
        raise RuntimeError("Wrong imported primary application")
    app_class = module.NomadNetworkApp
    observed = []
    original_time = module.time
    try:
        for name, last, fail in [("before", 98201.0, False), ("exact", 98200.0, False),
                                 ("due", 98199.0, False), ("never-announced", None, False),
                                 ("announce-failure", 0.0, True)]:
            calls, sleeps = [], []
            state = SimpleNamespace(should_run_jobs=True, defer_jobs=90, job_interval=5,
                                    peer_settings={"last_lxmf_sync": 100000.0, "last_announce": last,
                                                   "announce_interval": 1800, "display_name": "Generated Peer"},
                                    lxmf_sync_interval=21600, lxmf_sync_limit=8, required_stamp_cost=13,
                                    lxmf_destination=SimpleNamespace(hash=b"\x11" * 16, display_name="old"))

            def sleep(seconds):
                sleeps.append(seconds)
                if len(sleeps) > 1:
                    state.should_run_jobs = False

            def announce(destination):
                calls.append(["announce", destination.hex(), state.lxmf_destination.display_name])
                if fail:
                    raise ValueError("Disclosed failing announce observer")

            state.message_router = SimpleNamespace(
                set_inbound_stamp_cost=lambda destination, cost: calls.append(["cost", destination.hex(), cost]),
                announce=announce)
            state.request_lxmf_sync = lambda limit: calls.append(["sync", limit])
            state.save_peer_settings = lambda: calls.append(["saved", state.peer_settings["last_announce"]])
            state.announce_now = MethodType(app_class.announce_now, state)
            module.time = SimpleNamespace(time=lambda: 100000.0, sleep=sleep)
            error = None
            try:
                app_class._NomadNetworkApp__jobs(state)
            except Exception as exception:
                error = type(exception).__name__
            observed.append({"name": name, "lastBefore": last, "lastAfter": state.peer_settings["last_announce"],
                             "sleeps": sleeps, "calls": calls, "error": error})
    finally:
        module.time = original_time
    result = {
        "commit": revision,
        "sourceSha256": hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest(),
        "method": "Actual __jobs/announce_now method bodies. A disclosed module-local clock stops after one iteration; router/save callbacks observe calls instead of performing I/O. Not unmodified live application evidence.",
        "startupDelaySeconds": app_class.START_ANNOUNCE_DELAY,
        "observed": observed,
    }
    Path(sys.argv[1]).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
