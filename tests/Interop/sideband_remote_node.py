"""Actual primary daemon-core command bridge, with a delivery observation callback."""
import argparse
import base64
import hashlib
import inspect
import json
import os
from pathlib import Path
import subprocess
import sys

import LXMF
import RNS
from RNS.vendor import umsgpack

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
    parser.add_argument("--profile", type=Path)
    args = parser.parse_args()
    if os.environ.get("DOTRETICULUM_LXST_UPSTREAM"):
        sys.path.insert(0, os.environ["DOTRETICULUM_LXST_UPSTREAM"])
    verify_reference(require_lxmf=True, require_lxst=True)
    source = args.source.resolve()
    path = source / "sbapp" / "sideband" / "core.py"
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    dirty = subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True)
    if revision != PIN or dirty or hashlib.sha256(path.read_bytes()).hexdigest() != DIGEST:
        raise RuntimeError("Unexpected or modified primary Sideband source")
    sys.path.insert(0, str(source))
    from sbapp.sideband.core import SidebandCore
    if Path(inspect.getfile(SidebandCore)).resolve() != path:
        raise RuntimeError("Unexpected actual Sideband import")
    rns = args.config / "rns"
    write_config(str(rns), args.tcp_server_port, args.rnsd_port)
    core = SidebandCore(None, config_path=str(args.profile or args.config / "profile"),
                        rns_config_path=str(rns), is_daemon=True, quiet=True)
    core.start()
    RNS.loglevel = RNS.LOG_ERROR

    def delivered(message):
        # Only observes; the unchanged primary method still owns admission and processing.
        emit({"event": "delivery", "source": message.source_hash.hex(),
              "content": base64.b64encode(message.content).decode(),
              "signature_valid": message.signature_validated,
              "method": message.method,
              "commands": LXMF.FIELD_COMMANDS in message.fields,
              "telemetry": LXMF.FIELD_TELEMETRY in message.fields,
              "appearance": LXMF.FIELD_ICON_APPEARANCE in message.fields})
        core.lxmf_delivery(message)

    core.message_router.register_delivery_callback(delivered)
    RNS.Transport.register_announce_handler(Handler())
    emit({"event": "ready", "dest": core.lxmf_destination.hash.hex(),
          "revision": revision, "source_sha256": DIGEST, "imported_core": str(path)})
    for line in command_lines():
        parts = line.strip().split()
        if not parts:
            continue
        command = parts[0]
        if command == "quit":
            core.save_configuration(no_thread=True)
            core.cleanup()
            core.message_router.exit_handler()
            RNS.Reticulum.exit_handler()
            os._exit(0)
        elif command == "announce":
            core.lxmf_announce()
            emit({"event": "announced"})
        elif command == "configure" and len(parts) == 2:
            values = json.loads(base64.b64decode(parts[1], validate=True))
            permitted = ("telemetry_allow_requests_from_anyone", "telemetry_allow_requests_from_trusted",
                         "lxmf_ignore_unknown", "lxmf_try_propagation_on_fail", "start_announce")
            if any(key not in permitted or type(value) is not bool for key, value in values.items()):
                raise ValueError("Only actual bounded boolean authorization keys are supported")
            core.config.update(values)
            core.save_configuration(no_thread=True)
            emit({"event": "configured", "values": {key: core.config[key] for key in permitted}})
        elif command == "permission" and len(parts) == 4:
            destination = bytes.fromhex(parts[1])
            if not core.has_conversation(destination):
                core.create_conversation(destination)
            if parts[2] == "yes":
                core.trusted_conversation(destination)
            else:
                core.untrusted_conversation(destination)
            core._db_conversation_set_requests(destination, allow_requests=parts[3] == "yes")
            core.save_configuration(no_thread=True)
            emit({"event": "permission", "allowed": core.allow_request_from(destination)})
        elif command == "command" and len(parts) == 3:
            content = base64.b64decode(parts[2], validate=True).decode()
            accepted = core.send_command(content, bytes.fromhex(parts[1]), False)
            emit({"event": "queued", "accepted": accepted})
        elif command == "message" and len(parts) == 3:
            content = base64.b64decode(parts[2], validate=True).decode()
            accepted = core.send_message(content, bytes.fromhex(parts[1]), False, skip_fields=True)
            emit({"event": "queued", "accepted": accepted})
        elif command in ("fields", "bad-signature") and len(parts) == 3:
            destination = bytes.fromhex(parts[1])
            peer = RNS.Identity.recall(destination)
            if peer is None:
                raise ValueError("Destination must be announced first")
            fields = umsgpack.unpackb(base64.b64decode(parts[2], validate=True))
            message = LXMF.LXMessage(
                RNS.Destination(peer, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery"),
                core.lxmf_destination, "", fields=fields, desired_method=LXMF.LXMessage.DIRECT)
            if command == "bad-signature":
                # Explicit wire fault injection; not an unmodified message-generation claim.
                message.pack()
                message.packed = message.packed[:32] + bytes(64) + message.packed[96:]
            core.message_router.handle_outbound(message)
            emit({"event": "queued", "accepted": True})
        elif command == "snapshot" and len(parts) == 2:
            destination = bytes.fromhex(parts[1])
            messages = core.list_messages(destination, limit=256) or []
            conversation = core._db_conversation(destination)
            emit({"event": "snapshot", "messages": [{
                "content": base64.b64encode(message["content"]).decode(),
                "incoming": message["dest"] == core.lxmf_destination.hash,
                "signature_valid": message["lxm"].signature_validated,
                "commands": LXMF.FIELD_COMMANDS in message["lxm"].fields,
                "delivered": message["state"] == LXMF.LXMessage.DELIVERED
            } for message in messages], "conversation_known": conversation is not None})
        else:
            raise ValueError("Unsupported primary remote adapter operation")


if __name__ == "__main__":
    main()
