"""Observe actual pinned core/router cancellation with disclosed queue boundaries."""
import argparse
import ast
import hashlib
import inspect
import json
import subprocess
from pathlib import Path
from types import MethodType, SimpleNamespace

import LXMF

from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
CORE_SHA = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"


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
    if hashlib.sha256(source).hexdigest() != CORE_SHA:
        raise RuntimeError("Unexpected core source")
    core = next(node for node in ast.parse(source).body
                if isinstance(node, ast.ClassDef) and node.name == "SidebandCore")
    method = next(node for node in core.body
                  if isinstance(node, ast.FunctionDef) and node.name == "cancel_message")
    rows = []
    for label, representation, deferred in (
            ("packet", LXMF.LXMessage.PACKET, False),
            ("resource", LXMF.LXMessage.RESOURCE, False),
            ("deferred", LXMF.LXMessage.PACKET, True),
            ("unknown", LXMF.LXMessage.PACKET, False),
            ("router-error", LXMF.LXMessage.PACKET, False)):
        events = []
        message_id = bytes([1]) * 32
        message = SimpleNamespace(
            message_id=message_id, state=LXMF.LXMessage.OUTBOUND, representation=representation,
            resource_representation=SimpleNamespace(cancel=lambda: events.append("resource-cancel")))
        router = SimpleNamespace(
            pending_deferred_stamps={message_id: message} if deferred else {},
            pending_outbound=[] if deferred or label == "unknown" else [message],
            process_outbound=lambda: events.append("process-outbound"))
        router.cancel_outbound = MethodType(LXMF.LXMRouter.cancel_outbound, router)
        if label == "router-error":
            def reject(_):
                raise RuntimeError("Synthetic cancellation refusal")
            router.cancel_outbound = reject
        namespace = {"RNS": SimpleNamespace(
            log=lambda *a, **k: events.append("core-error"), trace_exception=lambda *a: events.append("core-trace"),
            LOG_ERROR=0)}
        exec(compile(ast.Module(body=[method], type_ignores=[]), "core.py", "exec"), namespace)
        owner = SimpleNamespace(allow_service_dispatch=False, is_client=False, message_router=router)
        accepted = namespace["cancel_message"](owner, message_id)
        rows.append({"name": label, "returned": accepted, "state": message.state, "events": events})
    router_source = Path(inspect.getfile(LXMF.LXMRouter)).read_bytes()
    result = {
        "revision": PIN, "core_sha256": CORE_SHA,
        "lxmf_revision": "e8ba02483217af43be284a72f1c059c730b7d047",
        "router_sha256": hashlib.sha256(router_source).hexdigest(),
        "method": "Actual AST-extracted SidebandCore.cancel_message calling actual LXMF.LXMRouter.cancel_outbound; synthetic queue/message/resource and process_outbound observation boundaries; deferred cancel_work is actual API, not running work; no GUI, full worker, network or recall claim",
        "cancelled_state": LXMF.LXMessage.CANCELLED, "outbound_state": LXMF.LXMessage.OUTBOUND, "cases": rows}
    with args.output.open("w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(result, indent=4) + "\n")


if __name__ == "__main__":
    main()
