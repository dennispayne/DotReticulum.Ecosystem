"""Thin actual primary daemon-core paper API bridge; no GUI/camera/printing."""
import argparse
import base64
import hashlib
import inspect
import os
from pathlib import Path
import subprocess
import sys

import LXMF
import RNS

from live_lxmf_node import Handler, command_lines, emit, write_config
from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
DIGEST = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"


def summary(message):
    lxm = message["lxm"]
    return {"content": message["content"].decode(), "title": message["title"].decode(),
            "method": message["method"], "state": message["state"],
            "signature_valid": lxm.signature_validated,
            "ticket_field": LXMF.FIELD_TICKET in lxm.fields,
            "appearance_field": LXMF.FIELD_ICON_APPEARANCE in lxm.fields,
            "telemetry_field": LXMF.FIELD_TELEMETRY in lxm.fields,
            "uri": lxm.as_uri(finalise=False) if lxm.desired_method == LXMF.LXMessage.PAPER else None}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, default=0)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--profile", type=Path)
    args = parser.parse_args()
    lxst = os.environ.get("DOTRETICULUM_LXST_UPSTREAM")
    if lxst:
        sys.path.insert(0, str(Path(lxst).resolve()))
    verify_reference(require_lxmf=True)
    source = args.source.resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    dirty = subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True)
    path = source / "sbapp" / "sideband" / "core.py"
    if revision != PIN or dirty or hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest() != DIGEST:
        raise RuntimeError("Unexpected or modified Sideband reference")
    sys.path.insert(0, str(source))
    from sbapp.sideband.core import SidebandCore
    if Path(inspect.getfile(SidebandCore)).resolve() != path:
        raise RuntimeError("Sideband imported from an unexpected path")
    rns = args.config / "rns"
    write_config(str(rns), args.tcp_server_port, args.rnsd_port)
    core = SidebandCore(None, config_path=str(args.profile or args.config / "profile"),
                        rns_config_path=str(rns), is_daemon=True, quiet=True)
    core.start()
    RNS.Transport.register_announce_handler(Handler())
    emit({"event": "ready", "dest": core.lxmf_destination.hash.hex(), "revision": PIN, "source_sha256": DIGEST})
    for line in command_lines():
        parts = line.strip().split()
        if not parts:
            continue
        if parts[0] == "quit":
            core.save_configuration(no_thread=True)
            core.cleanup()
            core.message_router.exit_handler()
            RNS.Reticulum.exit_handler()
            os._exit(0)
        elif parts[0] == "announce":
            core.lxmf_announce()
            emit({"event": "announced"})
        elif parts[0] == "paper" and len(parts) == 4:
            destination = bytes.fromhex(parts[1])
            if not core.has_conversation(destination):
                core.create_conversation(destination)
            if parts[3] == "yes":
                core.trusted_conversation(destination)
            else:
                core.untrusted_conversation(destination)
            content = "" if parts[2] == "-" else base64.b64decode(parts[2], validate=True).decode()
            accepted = core.paper_message(content, destination)
            messages = core.list_messages(destination, limit=1) if accepted else []
            emit({"event": "paper", "accepted": accepted,
                  "message": summary(messages[-1]) if messages else None})
        elif parts[0] == "ingest" and len(parts) == 2:
            core.lxm_ingest_uri(base64.b64decode(parts[1], validate=True).decode())
            emit({"event": "ingest", "response": core.getstate("lxm_uri_ingest.result")})
        elif parts[0] == "snapshot" and len(parts) == 2:
            messages = core.list_messages(bytes.fromhex(parts[1]), limit=32) or []
            emit({"event": "snapshot", "messages": [summary(message) for message in messages]})
        else:
            raise ValueError("Unsupported paper bridge command")


if __name__ == "__main__":
    main()
