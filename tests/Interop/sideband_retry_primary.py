"""Actual primary core; public LXMessage callback observers delegate the original callbacks."""
import argparse
import base64
import hashlib
import inspect
import os
from pathlib import Path
import subprocess
import sys
import time

import LXMF
import RNS

from live_lxmf_node import Handler, command_lines, emit, write_config
from reference_environment import verify_reference
from sideband_remote_node import PIN, DIGEST


def main(*, diagnostic=None, failed_returned=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, default=0)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--profile", type=Path, required=True)
    args = parser.parse_args()
    if os.environ.get("DOTRETICULUM_LXST_UPSTREAM"):
        sys.path.insert(0, os.environ["DOTRETICULUM_LXST_UPSTREAM"])
    verify_reference(require_lxmf=True, require_lxst=True)
    source = args.source.resolve()
    path = source / "sbapp" / "sideband" / "core.py"
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    dirty = subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True)
    if revision != PIN or dirty or hashlib.sha256(path.read_bytes()).hexdigest() != DIGEST:
        raise RuntimeError("Unexpected or modified actual primary")
    sys.path.insert(0, str(source))
    from sbapp.sideband.core import SidebandCore
    if Path(inspect.getfile(SidebandCore)).resolve() != path:
        raise RuntimeError("Unexpected primary import")
    rns = args.config / "rns"
    write_config(str(rns), args.tcp_server_port, args.rnsd_port)
    core = SidebandCore(None, config_path=str(args.profile), rns_config_path=str(rns),
                        is_daemon=True, quiet=True)
    core.start()
    RNS.loglevel = RNS.LOG_ERROR
    jobs = {}

    def delivered(message):
        try:
            emit({"event": "delivery", "content": base64.b64encode(message.content).decode(),
                  "signature_valid": message.signature_validated, "method": message.method,
                  "commands": LXMF.FIELD_COMMANDS in message.fields, "hash": message.hash.hex()})
        finally:
            core.lxmf_delivery(message)

    core.message_router.register_delivery_callback(delivered)
    RNS.Transport.register_announce_handler(Handler())
    emit({"event": "ready", "dest": core.lxmf_destination.hash.hex(),
          "revision": revision, "source_sha256": DIGEST, "imported_core": str(path)})

    def pending():
        return tuple(core.message_router.pending_outbound) + tuple(core.message_router.pending_deferred_stamps.values())

    def observe(message, tag):
        failed = message.failed_callback
        delivered_callback = core.message_notification

        def failure(value):
            try:
                emit({"event": "group-failed", "tag": tag, "hash": value.hash.hex(),
                      "state": value.state, "method": value.method,
                      "eligible_before": bool(getattr(value, "try_propagation_on_fail", False)),
                      "stamp_failure": bool(getattr(value, "stamp_generation_failed", False)),
                      "attempts": value.delivery_attempts,
                      **(diagnostic(core.message_router, value) if diagnostic else {})})
            finally:
                failed(value)
                if failed_returned:
                    failed_returned(tag)

        def sent(value):
            try:
                emit({"event": "group-sent", "tag": tag, "hash": value.hash.hex(),
                      "state": value.state, "method": value.method})
            finally:
                delivered_callback(value)

        message.register_failed_callback(failure)
        message.register_delivery_callback(sent)
        jobs[tag] = message

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
        elif command == "setup" and len(parts) == 4:
            peer, pn = bytes.fromhex(parts[1]), bytes.fromhex(parts[2])
            if not core.has_conversation(peer):
                core.create_conversation(peer)
            core.config["telemetry_allow_requests_from_anyone"] = True
            core.config["lxmf_ignore_unknown"] = True
            core.config["lxmf_try_propagation_on_fail"] = parts[3] == "yes"
            core.config["lxmf_propagation_node"] = pn
            core.update_active_lxmf_propagation_node()
            core.save_configuration(no_thread=True)
            emit({"event": "configured", "fallback": core.config["lxmf_try_propagation_on_fail"]})
        elif command == "send" and len(parts) == 4:
            tag = parts[1]
            if tag in jobs or len(jobs) >= 4:
                raise ValueError("Duplicate or excessive observation tag")
            before = {id(message) for message in pending()}
            accepted = core.send_command(base64.b64decode(parts[3], validate=True).decode(),
                                         bytes.fromhex(parts[2]), True)
            if accepted is not True:
                raise RuntimeError("Actual primary send_command returned false")
            found = [message for message in pending() if id(message) not in before]
            if len(found) != 1:
                raise RuntimeError(f"Expected one real pending command, found {len(found)}")
            observe(found[0], tag)
            emit({"event": "queued", "accepted": accepted, "tag": tag})
        elif command == "job" and len(parts) == 2:
            message = jobs[parts[1]]
            emit({"event": "job", "tag": parts[1], "state": message.state,
                  "method": message.method, "hash": message.hash.hex(),
                  "eligible": bool(getattr(message, "try_propagation_on_fail", False)),
                  **(diagnostic(core.message_router, message) if diagnostic else {})})
        elif command == "retrieve":
            if not core.request_lxmf_sync():
                raise RuntimeError("Actual primary rejected sync")
            deadline = time.time() + 60
            while core.message_router.propagation_transfer_state < LXMF.LXMRouter.PR_COMPLETE and time.time() < deadline:
                time.sleep(0.05)
            if core.message_router.propagation_transfer_state != LXMF.LXMRouter.PR_COMPLETE:
                raise RuntimeError(f"Actual retrieval failed: {core.message_router.propagation_transfer_state}")
            emit({"event": "retrieved", "count": core.message_router.propagation_transfer_last_result})
        else:
            raise ValueError("Unsupported primary retry observation")


if __name__ == "__main__":
    main()
