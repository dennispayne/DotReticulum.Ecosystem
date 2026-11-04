"""Execute actual imported primary command methods with disclosed offline seams."""
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
import RNS

from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
CORE = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"


def wire(value):
    if isinstance(value, bytes):
        return {"binary": value.hex()}
    if isinstance(value, dict):
        return {str(k): wire(v) for k, v in value.items()}
    if isinstance(value, list):
        return [wire(v) for v in value]
    return value


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
        raise RuntimeError("Unexpected primary reference source")
    sys.path.insert(0, str(source))
    if os.environ.get("DOTRETICULUM_LXST_UPSTREAM"):
        sys.path.insert(0, os.environ["DOTRETICULUM_LXST_UPSTREAM"])
    from sbapp.sideband.core import SidebandCore
    from sbapp.sideband.sense import Commands
    if Path(inspect.getfile(SidebandCore)).resolve() != path:
        raise RuntimeError("Unexpected actual imported primary core")
    sender, recipient = RNS.Identity(), RNS.Identity()
    origin = RNS.Destination(sender, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    target = RNS.Destination(recipient, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    RNS.Identity.remember(bytes(32), target.hash, recipient.get_public_key())
    # Offline vectors have no singleton for identity-use accounting; cryptography is real.
    recall = RNS.Identity.recall
    RNS.Identity.recall = lambda address: recall(address, _no_use=True)
    sends = []
    for content in ("", "echo ", "echo hello ✓", "echo echo a echo b", "ping", "pinganything",
                    "sig", "signal", " ping", "PING", "plugin 'two words'", " "):
        captured = []
        router = SimpleNamespace(delivery_link_available=lambda _: True,
                                 get_outbound_propagation_node=lambda: None,
                                 handle_outbound=captured.append)
        owner = SimpleNamespace(allow_service_dispatch=False, is_client=False,
                                lxmf_destination=origin, message_router=router,
                                is_trusted=lambda _: True, message_notification=lambda _: None,
                                lxm_ingest=lambda *a, **kw: None)
        accepted = SidebandCore.send_command(owner, content, target.hash, False)
        sends.append({"input": content, "accepted": accepted,
                      "commands": wire(captured[0].fields[LXMF.FIELD_COMMANDS]) if captured else None,
                      "include_ticket": captured[0].include_ticket if captured else None})
    responses = []
    for name, commands, observations in (
        ("ping_false", [{Commands.PING: False}], (None, None, None)),
        ("echo_unicode", [{Commands.ECHO: "hello ✓".encode()}], (None, None, None)),
        ("echo_empty", [{Commands.ECHO: b""}], (None, None, None)),
        ("signal_absent", [{Commands.SIGNAL_REPORT: True}], (None, None, None)),
        ("signal_zero", [{Commands.SIGNAL_REPORT: True}], (0.0, 0.0, 0.0)),
        ("signal_all", [{Commands.SIGNAL_REPORT: True}], (83.5, -101.25, 9.5)),
        ("signal_scientific", [{Commands.SIGNAL_REPORT: True}], (1e-5, -1e16, -0.0)),
        ("signal_rssi", [{Commands.SIGNAL_REPORT: False}], (None, -77.0, None)),
        ("precedence", [{Commands.PING: True, Commands.ECHO: b"ignored"}], (None, None, None)),
        ("unknown", [{99: True}], (None, None, None)),
        ("invalid_utf8_stops_batch", [{Commands.ECHO: b"\xff"}, {Commands.PING: True}], (None, None, None)),
        ("multiple", [{Commands.PING: True}, {Commands.ECHO: b"second"}], (None, None, None)),
    ):
        replies = []
        owner = SimpleNamespace(config={"command_plugins_enabled": False},
                                send_message=lambda content, destination, propagation, **kw:
                                replies.append({"content": content, "propagation": propagation, **kw}))
        q, rssi, snr = observations
        message = SimpleNamespace(source_hash=target.hash, q=q, rssi=rssi, snr=snr)
        SidebandCore.handle_commands(owner, commands, message)
        responses.append({"name": name, "commands": wire(commands), "quality": q,
                          "rssi": rssi, "snr": snr, "replies": replies})
    admissions = []
    for mode in ("conversation", "trusted", "anyone"):
        for exists in (False, True):
            for trusted in (False, True):
                for allow in (False, True):
                    for signature in (False, True):
                        for ignore_unknown in (False, True):
                            events = []
                            conversation = {"trust": int(trusted), "data": {"allow_requests": allow}} if exists else None
                            owner = SimpleNamespace(config={
                                "telemetry_allow_requests_from_anyone": mode == "anyone",
                                "telemetry_allow_requests_from_trusted": mode == "trusted",
                                "lxmf_ignore_unknown": ignore_unknown},
                                _db_conversation=lambda _: conversation,
                                handle_commands=lambda *a: events.append("handled"),
                                lxm_ingest=lambda *a: events.append("ingested"))
                            owner.requests_allowed_from = lambda address: SidebandCore.requests_allowed_from(owner, address)
                            owner.allow_request_from = lambda address: SidebandCore.allow_request_from(owner, address)
                            message = SimpleNamespace(timestamp=1700000000, signature_validated=signature,
                                unverified_reason=LXMF.LXMessage.SIGNATURE_INVALID, source_hash=target.hash,
                                hash=bytes(32), fields={LXMF.FIELD_COMMANDS: [{Commands.PING: True}]})
                            SidebandCore.lxmf_delivery(owner, message)
                            admissions.append({"mode": mode, "exists": exists, "trusted": trusted,
                                               "allow": allow, "signature": signature,
                                               "ignore_unknown": ignore_unknown, "events": events})
    args.output.write_text(json.dumps({
        "revision": PIN, "core_sha256": CORE,
        "sense_sha256": hashlib.sha256((source / "sbapp" / "sideband" / "sense.py").read_bytes()).hexdigest(),
        "method": "Actual imported SidebandCore.send_command/handle_commands; real LXMF constructors and RNS identities. Offline router, trust, persistence, reply-send and no-singleton identity-use seams; no plugin/GUI/live claim.",
        "constants": {name: getattr(Commands, name) for name in
                      ("PLUGIN_COMMAND", "TELEMETRY_REQUEST", "PING", "ECHO", "SIGNAL_REPORT")},
        "sends": sends, "responses": responses, "admissions": admissions}, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8", newline="\n")
    print(json.dumps({"imported_core": str(path), "revision": revision, "sha256": CORE,
                      "send_cases": len(sends), "response_cases": len(responses)}))


if __name__ == "__main__":
    main()
