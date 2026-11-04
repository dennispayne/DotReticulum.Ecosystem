"""Actual primary message_notification policy, with explicit router/DB observers."""
import argparse
import hashlib
import inspect
import json
import os
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

import LXMF

from GenerateSidebandRemoteVectors import CORE, PIN
from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    source = args.source.resolve()
    path = source / "sbapp" / "sideband" / "core.py"
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    dirty = subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True)
    if revision != PIN or dirty or hashlib.sha256(path.read_bytes()).hexdigest() != CORE:
        raise RuntimeError("Unexpected actual primary")
    sys.path.insert(0, str(source))
    if os.environ.get("DOTRETICULUM_LXST_UPSTREAM"):
        sys.path.insert(0, os.environ["DOTRETICULUM_LXST_UPSTREAM"])
    from sbapp.sideband.core import SidebandCore
    if Path(inspect.getfile(SidebandCore)).resolve() != path:
        raise RuntimeError("Unexpected actual primary import")

    def context(state, eligible, stamp_failure):
        queued, history, mutations = [], [], []
        owner = SimpleNamespace(
            message_router=SimpleNamespace(handle_outbound=lambda message: queued.append(message.desired_method)),
            _db_message_set_method=lambda *values: mutations.append(["method", values[1]]),
            _db_message_set_state=lambda *values: mutations.append(["state", values[1]]),
            lxm_ingest=lambda *values, **keywords: history.append(bool(keywords.get("originator"))))
        message = SimpleNamespace(state=state, try_propagation_on_fail=eligible,
            stamp_generation_failed=stamp_failure, desired_method=LXMF.LXMessage.PROPAGATED,
            fields={}, hash=bytes(32), delivery_attempts=4, next_delivery_attempt=12345, packed=b"packed")
        return owner, message, queued, history, mutations

    cases = []
    for state in (LXMF.LXMessage.FAILED, LXMF.LXMessage.REJECTED, LXMF.LXMessage.CANCELLED):
        for eligible in (False, True):
            for stamp_failure in (False, True):
                for no_display in (False, True):
                    owner, message, queued, history, mutations = context(state, eligible, stamp_failure)
                    SidebandCore.message_notification(owner, message, no_display=no_display)
                    cases.append(dict(state=state, eligible=eligible, stamp_failure=stamp_failure,
                        no_display=no_display, queued=queued, history=history, mutations=mutations,
                        eligible_after=bool(message.try_propagation_on_fail),
                        attempts_after=message.delivery_attempts,
                        next_attempt_present=hasattr(message, "next_delivery_attempt"),
                        packed_cleared=message.packed is None))
    owner, message, queued, history, mutations = context(LXMF.LXMessage.FAILED, True, False)
    SidebandCore.message_notification(owner, message)
    SidebandCore.message_notification(owner, message)
    args.output.write_text(json.dumps({
        "revision": PIN, "core_sha256": CORE,
        "method": "Actual imported SidebandCore.message_notification; explicit offline router/DB/history observers and synthetic message state. No live-failure or stamp-generation claim.",
        "cases": cases,
        "one_shot": dict(queued=queued, history=history, mutations=mutations,
                         eligible_after=bool(message.try_propagation_on_fail)),
    }, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps({"imported_core": str(path), "cases": len(cases), "one_shot": 1,
                      "revision": revision, "core_sha256": CORE}))


if __name__ == "__main__":
    main()
