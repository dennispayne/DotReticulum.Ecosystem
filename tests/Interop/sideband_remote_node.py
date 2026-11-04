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
import time

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
    delivery_packet_callback = core.lxmf_destination.callbacks.packet
    delivery_link_callback = core.lxmf_destination.callbacks.link_established
    delivery_proof_strategy = core.lxmf_destination.proof_strategy
    RNS.loglevel = RNS.LOG_ERROR

    def delivered(message):
        # Only observes; the unchanged primary method still owns admission and processing.
        emit({"event": "delivery", "source": message.source_hash.hex(),
              "hash": message.hash.hex(),
              "content": base64.b64encode(message.content).decode(),
              "signature_valid": message.signature_validated,
              "signature_reason": message.unverified_reason,
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
        elif command in ("command", "command-propagated") and len(parts) == 3:
            content = base64.b64decode(parts[2], validate=True).decode()
            accepted = core.send_command(content, bytes.fromhex(parts[1]), command == "command-propagated")
            emit({"event": "queued", "accepted": accepted})
        elif command == "use-pn" and len(parts) == 2:
            core.config["lxmf_propagation_node"] = bytes.fromhex(parts[1])
            core.update_active_lxmf_propagation_node()
            core.save_configuration(no_thread=True)
            emit({"event": "pn-selected", "dest": core.message_router.get_outbound_propagation_node().hex()})
        elif command == "delivery-links" and len(parts) == 2 and parts[1] in ("on", "off", "teardown"):
            # Public RNS fault seam: complete the handshake, then reject the link without LXMF ingestion.
            core.lxmf_destination.set_link_established_callback(
                (lambda link: link.teardown()) if parts[1] == "teardown" else delivery_link_callback)
            core.lxmf_destination.accepts_links(parts[1] != "off")
            emit({"event": "delivery-links", "enabled": core.lxmf_destination.accepts_links(),
                  "mode": parts[1]})
        elif command == "delivery-packets" and len(parts) == 2 and parts[1] in ("on", "off"):
            # Disclosed packet/proof loss through public destination callbacks, not a primary patch.
            enabled = parts[1] == "on"
            core.lxmf_destination.set_packet_callback(delivery_packet_callback if enabled else None)
            core.lxmf_destination.set_proof_strategy(delivery_proof_strategy if enabled else RNS.Destination.PROVE_NONE)
            emit({"event": "delivery-packets", "enabled": enabled})
        elif command in ("retrieve", "retrieve-start"):
            accepted = core.request_lxmf_sync()
            if command == "retrieve-start":
                emit({"event": "sync-started", "accepted": accepted})
                continue
            if not accepted:
                raise RuntimeError("Actual primary rejected propagation retrieval as busy")
            deadline = time.time() + 60
            while core.message_router.propagation_transfer_state < LXMF.LXMRouter.PR_COMPLETE and time.time() < deadline:
                time.sleep(0.05)
            if core.message_router.propagation_transfer_state != LXMF.LXMRouter.PR_COMPLETE:
                raise RuntimeError(f"Actual primary retrieval failed: {core.message_router.propagation_transfer_state}")
            emit({"event": "retrieved", "state": core.message_router.propagation_transfer_state,
                  "count": core.message_router.propagation_transfer_last_result})
        elif command == "cancel-retrieval":
            core.cancel_lxmf_sync()
            emit({"event": "sync-cancelled", "state": core.message_router.propagation_transfer_state})
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
                "hash": message["hash"].hex(),
                "content": base64.b64encode(message["content"]).decode(),
                "incoming": message["dest"] == core.lxmf_destination.hash,
                "signature_valid": message["lxm"].signature_validated,
                "commands": LXMF.FIELD_COMMANDS in message["lxm"].fields,
                "commands_packed": base64.b64encode(umsgpack.packb(message["lxm"].fields[LXMF.FIELD_COMMANDS])).decode()
                    if LXMF.FIELD_COMMANDS in message["lxm"].fields else None,
                "method": message["method"], "state": message["state"],
                "delivered": message["state"] == LXMF.LXMessage.DELIVERED
            } for message in messages], "conversation_known": conversation is not None})
        else:
            raise ValueError("Unsupported primary remote adapter operation")


if __name__ == "__main__":
    main()
