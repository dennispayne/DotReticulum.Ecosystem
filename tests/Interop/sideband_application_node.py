"""Thin process adapter for the actual pinned Sideband desktop daemon core."""
import argparse
import base64
import hashlib
import inspect
import os
from pathlib import Path
import subprocess
import sys
import time

import RNS
import LXMF

from live_lxmf_node import Handler, command_lines, emit, write_config
from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
DIGEST = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, default=0)
    parser.add_argument("--source", type=Path, required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    source = args.source.resolve()
    revision = subprocess.check_output(
        ["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != PIN:
        raise RuntimeError(f"Expected Sideband {PIN}, got {revision}")
    dirty = subprocess.check_output(
        ["git", "-C", str(source), "status", "--porcelain", "--untracked-files=normal"], text=True)
    if dirty:
        raise RuntimeError("The Sideband reference checkout is not clean")
    core_path = source / "sbapp" / "sideband" / "core.py"
    if hashlib.sha256(core_path.read_bytes().replace(b"\r\n", b"\n")).hexdigest() != DIGEST:
        raise RuntimeError("Unexpected Sideband core.py digest")
    sys.path.insert(0, str(source))
    from sbapp.sideband.core import SidebandCore
    if Path(inspect.getfile(SidebandCore)).resolve() != core_path:
        raise RuntimeError("Imported Sideband from an unexpected location")
    rns = args.config / "rns"
    write_config(str(rns), args.tcp_server_port, args.rnsd_port)
    core = SidebandCore(None, config_path=str(args.config / "profile"),
                        rns_config_path=str(rns), is_daemon=True, quiet=True)
    core.start()
    if core.message_router is None:
        raise RuntimeError("The actual Sideband application did not start its LXMF router")
    RNS.Transport.register_announce_handler(Handler())
    emit({"event": "ready", "dest": core.lxmf_destination.hash.hex(),
          "identity_hash": core.identity.hash.hex(), "revision": revision,
          "core_path": str(core_path), "source_sha256": DIGEST})
    for line in command_lines():
        parts = line.strip().split()
        if not parts:
            continue
        if parts[0] == "quit":
            core.save_configuration(no_thread=True)
            core.cleanup()
            RNS.Reticulum.exit_handler()
            os._exit(0)
        if parts[0] == "announce":
            core.lxmf_announce()
            emit({"event": "announced", "dest": core.lxmf_destination.hash.hex()})
        elif parts[0] == "use-pn" and len(parts) == 2:
            core.config["lxmf_propagation_node"] = bytes.fromhex(parts[1])
            core.update_active_lxmf_propagation_node()
            core.save_configuration(no_thread=True)
            emit({"event": "pn-selected"})
        elif parts[0] == "retrieve":
            if not core.request_lxmf_sync():
                raise RuntimeError("The actual application rejected propagation retrieval as busy")
            deadline = time.time() + 60
            while core.message_router.propagation_transfer_state < LXMF.LXMRouter.PR_COMPLETE and time.time() < deadline:
                time.sleep(0.05)
            state = core.message_router.propagation_transfer_state
            if state != LXMF.LXMRouter.PR_COMPLETE:
                raise RuntimeError(f"Actual application retrieval did not complete: {state}")
            emit({"event": "retrieved"})
        elif parts[0] == "trust" and len(parts) == 3 and parts[2] in ("yes", "no"):
            destination = bytes.fromhex(parts[1])
            if not core.has_conversation(destination):
                core.create_conversation(destination)
            if parts[2] == "yes":
                core.trusted_conversation(destination)
            else:
                core.untrusted_conversation(destination)
            emit({"event": "trust", "trusted": core.is_trusted(destination)})
        elif parts[0] in ("send", "send-propagated") and len(parts) == 3:
            destination = bytes.fromhex(parts[1])
            content = base64.b64decode(parts[2], validate=True).decode("utf-8")
            if RNS.Identity.recall(destination) is None:
                RNS.Transport.request_path(destination)
                deadline = time.time() + 20
                while RNS.Identity.recall(destination) is None and time.time() < deadline:
                    time.sleep(0.05)
            if not core.send_message(content, destination, parts[0] == "send-propagated"):
                raise RuntimeError("Actual Sideband send_message rejected the synthetic test message")
            emit({"event": "sent_queued"})
        elif parts[0] == "snapshot" and len(parts) == 2:
            messages = core.list_messages(bytes.fromhex(parts[1]), limit=32)
            if messages is None:
                messages = []
            emit({"event": "snapshot",
                  "has_ticket": core.message_router.get_outbound_ticket(bytes.fromhex(parts[1])) is not None,
                  "messages": [
                {"hash": message["hash"].hex(), "source": message["source"].hex(),
                 "state": message["state"], "method": message["method"],
                 "incoming": message["dest"] == core.lxmf_destination.hash,
                 "includes_ticket": LXMF.FIELD_TICKET in message["lxm"].fields,
                 "sha256": hashlib.sha256(message["content"]).hexdigest()}
                for message in messages]})
        else:
            raise ValueError("Unsupported Sideband adapter command")


if __name__ == "__main__":
    main()
