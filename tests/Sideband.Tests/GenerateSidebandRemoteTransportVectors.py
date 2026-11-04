"""Actual primary command/reply transport selection; explicit offline router/cache seams."""
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
        raise RuntimeError("Unexpected primary reference source")
    sys.path.insert(0, str(source))
    if os.environ.get("DOTRETICULUM_LXST_UPSTREAM"):
        sys.path.insert(0, os.environ["DOTRETICULUM_LXST_UPSTREAM"])
    from sbapp.sideband.core import SidebandCore
    from sbapp.sideband.sense import Commands
    if Path(inspect.getfile(SidebandCore)).resolve() != path:
        raise RuntimeError("Unexpected actual imported primary core")
    origin = RNS.Destination(RNS.Identity(), RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    recipient = RNS.Identity()
    target = RNS.Destination(recipient, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    RNS.Identity.remember(bytes(32), target.hash, recipient.get_public_key())
    recall = RNS.Identity.recall
    RNS.Identity.recall = lambda address: recall(address, _no_use=True)
    # Selection context only: synthetic cached ratchet ID, never a reference crypto/ratchet claim.
    RNS.Identity.current_ratchet_id = lambda _: bytes(16)
    sends, replies = [], []
    for propagation in (False, True):
        for linked in (False, True):
            for pn in (False, True):
                for fallback in (False, True):
                    for large in (False, True):
                        captured, history = [], []
                        owner = SimpleNamespace(allow_service_dispatch=False, is_client=False,
                            lxmf_destination=origin, config={"lxmf_try_propagation_on_fail": fallback},
                            message_router=SimpleNamespace(
                                delivery_link_available=lambda _: linked,
                                get_outbound_propagation_node=lambda: bytes.fromhex("33" * 16) if pn else None,
                                handle_outbound=captured.append),
                            is_trusted=lambda _: False, message_notification=lambda _: None,
                            lxm_ingest=lambda *a, **kw: history.append("originator"))
                        text = "echo " + ("d" * 768 if large else "transport")
                        if SidebandCore.send_command(owner, text, target.hash, propagation) is not True:
                            raise RuntimeError("Actual primary rejected a command selection case")
                        message = captured[0]
                        message.pack()
                        sends.append(dict(propagation=propagation, linked=linked, pn=pn, fallback=fallback,
                            input=text, desired_method=message.desired_method, actual_method=message.method,
                            retry_propagation=bool(getattr(message, "try_propagation_on_fail", False)),
                            history=history))
    for text, commands in (("ping", [{Commands.PING: True}]), ("echo transport", [{Commands.ECHO: b"transport"}]),
                           ("signal", [{Commands.SIGNAL_REPORT: True}])):
        for pn in (False, True):
            for fallback in (False, True):
                captured, history = [], []
                owner = SimpleNamespace(allow_service_dispatch=False, is_client=False,
                    lxmf_destination=origin, config={"lxmf_try_propagation_on_fail": fallback, "compose_in_markdown": False},
                    message_router=SimpleNamespace(delivery_link_available=lambda _: True,
                        get_outbound_propagation_node=lambda: bytes.fromhex("33" * 16) if pn else None,
                        handle_outbound=captured.append),
                    is_trusted=lambda _: False, has_bb_markup=lambda _: False,
                    message_notification_no_display=lambda _: None, message_notification=lambda _: None,
                    lxm_ingest=lambda *a, **kw: history.append("originator"))
                owner.send_message = lambda *a, **kw: SidebandCore.send_message(owner, *a, **kw)
                SidebandCore.handle_commands(owner, commands, SimpleNamespace(
                    source_hash=target.hash, method=LXMF.LXMessage.PROPAGATED, q=None, rssi=None, snr=None))
                message = captured[0]
                message.pack()
                replies.append(dict(input=text, pn=pn, fallback=fallback, actual_method=message.method,
                    content=message.content.decode(), fields=message.fields, history=history,
                    retry_propagation=bool(getattr(message, "try_propagation_on_fail", False))))
    args.output.write_text(json.dumps({
        "revision": PIN, "core_sha256": CORE,
        "method": "Actual imported SidebandCore.send_command/handle_commands/send_message and actual LXMessage.pack. Offline router/trust/storage/link observers; synthetic cached-ratchet selection ID and no-singleton recall seam. Real generated RNS identities. No live, ticket-generation or ratchet-cryptography claim.",
        "sends": sends, "replies": replies,
    }, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps({"imported_core": str(path), "revision": revision, "core_sha256": CORE,
                      "sends": len(sends), "replies": len(replies)}))


if __name__ == "__main__":
    main()
