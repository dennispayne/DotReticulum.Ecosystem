"""Execute the pinned Sideband message-notification policy without its GUI.

The actual method runs with message input objects and injected database,
router-admission, ingestion and logging dependencies. No reference is replaced.
"""
import argparse
import ast
import hashlib
import json
from pathlib import Path
import subprocess
from types import SimpleNamespace

import LXMF

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
DIGEST = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
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
    method = next(node for node in core.body
                  if isinstance(node, ast.FunctionDef) and node.name == "message_notification")
    namespace = {"LXMF": LXMF, "RNS": SimpleNamespace(
        log=lambda *_, **__: None, LOG_ERROR=0, LOG_VERBOSE=0, LOG_DEBUG=0)}
    exec(compile(ast.Module(body=[method], type_ignores=[]), "core.py", "exec"), namespace)
    cases = []
    for name, state, flag, stamp_failed in [
        ("failed-eligible", LXMF.LXMessage.FAILED, True, False),
        ("failed-disabled", LXMF.LXMessage.FAILED, False, False),
        ("failed-no-flag", LXMF.LXMessage.FAILED, None, False),
        ("failed-stamp-generation", LXMF.LXMessage.FAILED, True, True),
        ("delivered", LXMF.LXMessage.DELIVERED, True, False),
        ("rejected", LXMF.LXMessage.REJECTED, True, False),
    ]:
        message = SimpleNamespace(
            state=state, stamp_generation_failed=stamp_failed, fields={},
            delivery_attempts=3, next_delivery_attempt=123,
            packed=b"already packed", desired_method=LXMF.LXMessage.DIRECT,
            hash=bytes([1]) * 32)
        if flag is not None:
            message.try_propagation_on_fail = flag
        queued, ingested, mutations = [], [], []
        owner = SimpleNamespace(
            message_router=SimpleNamespace(handle_outbound=queued.append),
            lxm_ingest=lambda value, **_: ingested.append(value),
            _db_message_set_method=lambda *value: mutations.append(("method", *value)),
            _db_message_set_state=lambda *value: mutations.append(("state", *value)))
        namespace["message_notification"](owner, message)
        first_queued = len(queued)
        namespace["message_notification"](owner, message)
        cases.append({
            "name": name, "state": state, "eligible": flag is True,
            "stamp_failed": stamp_failed, "retry": first_queued == 1,
            "retry_count_after_second_notification": len(queued),
            "attempts": message.delivery_attempts,
            "has_next_attempt": hasattr(message, "next_delivery_attempt"),
            "packed_cleared": message.packed is None,
            "desired_method": message.desired_method,
        })
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps({
        "revision": PIN, "source_sha256": DIGEST,
        "method": "actual message_notification; injected message/database/router/ingest/log dependencies",
        "cases": cases}, indent=4) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
