"""Thin command adapter around the pinned upstream NomadNetworkApp."""
import argparse
import json
import os
import sys
import threading
import time
import subprocess
import hashlib

import LXMF
import RNS

from reference_environment import verify_reference


_output_lock = threading.Lock()


def emit(event, **values):
    with _output_lock:
        sys.stdout.write("@@" + json.dumps({"event": event, **values}) + "\n")
        sys.stdout.flush()


def command_lines():
    # Upstream forks stamp workers. A blocked BufferedReader lock survives fork
    # and deadlocks child stdin closure; raw OS reads do not hold that Python lock.
    pending = bytearray()
    while True:
        chunk = os.read(sys.stdin.fileno(), 4096)
        if not chunk:
            if pending:
                yield pending.decode("utf-8")
            return
        pending.extend(chunk)
        while b"\n" in pending:
            line, _, remaining = pending.partition(b"\n")
            pending = bytearray(remaining)
            if len(line) > 65536:
                raise ValueError("adapter command exceeds 64 KiB")
            yield line.decode("utf-8")
        if len(pending) > 65536:
            raise ValueError("adapter command exceeds 64 KiB")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--rnsconfig", required=True)
    args = parser.parse_args()

    verify_reference(require_lxmf=True)
    import nomadnet
    from nomadnet.NomadNetworkApp import NomadNetworkApp

    expected_source = os.environ.get("DOTRETICULUM_NOMADNET_SOURCE")
    imported_source = os.path.realpath(os.path.dirname(nomadnet.__file__))
    if expected_source and os.path.realpath(expected_source) != os.path.realpath(os.path.dirname(imported_source)):
        raise RuntimeError(f"NomadNet imported from unexpected source path: {nomadnet.__file__}")
    if not expected_source:
        raise RuntimeError("DOTRETICULUM_NOMADNET_SOURCE must select the primary source")
    revision = subprocess.check_output(["git", "-C", expected_source, "rev-parse", "HEAD"], text=True).strip()
    if revision != "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2":
        raise RuntimeError(f"Unexpected primary NomadNet revision: {revision}")
    if subprocess.check_output(["git", "-C", expected_source, "status", "--porcelain"], text=True).strip():
        raise RuntimeError("The NomadNet reference source must be clean")
    from nomadnet._version import __version__ as version
    if version != "1.4.4":
        raise RuntimeError(f"NomadNet version {version} does not match the pinned primary source")

    def command_loop():
        try:
            deadline = time.monotonic() + 60

            def current_app():
                try:
                    return NomadNetworkApp.get_shared_instance()
                except UnboundLocalError:
                    return None

            app = current_app()
            while (app is None or not hasattr(app, "node")) and time.monotonic() < deadline:
                time.sleep(0.05)
                app = current_app()
            if app is None or not hasattr(app, "node") or app.node is None:
                raise RuntimeError("The upstream NomadNet app did not initialize its configured node")

            emit(
                "ready",
                version=version,
                module=nomadnet.__file__,
                node=app.node.destination.hash.hex(),
                delivery=app.lxmf_destination.hash.hex(),
                propagation=(app.message_router.propagation_destination.hash.hex()
                             if app.message_router.propagation_node else None),
                revision=revision,
            )
            app.rrc.set_message_callback(lambda hub, message: emit(
                "rrc-message", kind=message.kind, room=message.room, text=message.text,
                nick=message.nick, source=message.src.hex() if isinstance(message.src, bytes) else None))
            def observe_delivery(message):
                app.lxmf_delivery(message)
                files = message.fields.get(LXMF.FIELD_FILE_ATTACHMENTS, [])
                emit("received", content=message.content.decode("utf-8", errors="replace"),
                     method=message.method,
                     files=[{"size": len(item[1]), "sha256": hashlib.sha256(item[1]).hexdigest()}
                            for item in files])
            app.message_router.register_delivery_callback(observe_delivery)
            rrc_hub = None

            for raw_line in command_lines():
                if len(raw_line) > 65536:
                    raise ValueError("adapter command exceeds 64 KiB")
                parts = raw_line.rstrip("\r\n").split(" ", 2)
                if not parts or not parts[0]:
                    continue
                if parts[0] == "quit":
                    app.exit_handler()
                    app.message_router.exit_handler()
                    RNS.Reticulum.exit_handler()
                    os._exit(0)
                if parts[0] == "rrc-connect" and len(parts) >= 2:
                    rrc_hub = app.rrc.add_hub(bytes.fromhex(parts[1]))
                    rrc_hub.nick_override = parts[2] if len(parts) == 3 else "Python"
                    rrc_hub.connect()
                    deadline = time.monotonic() + 40
                    while not rrc_hub.welcomed and time.monotonic() < deadline:
                        time.sleep(0.05)
                    if not rrc_hub.welcomed:
                        raise RuntimeError("The actual primary RRC client did not receive WELCOME")
                    emit("rrc-connected", hub=rrc_hub.hub_hash.hex())
                    continue
                if parts[0] == "rrc-join" and len(parts) >= 2:
                    rrc_hub.join_room(parts[1])
                    deadline = time.monotonic() + 20
                    while parts[1] in rrc_hub._pending_joins and time.monotonic() < deadline:
                        time.sleep(0.05)
                    if parts[1] in rrc_hub._pending_joins or parts[1] not in rrc_hub.rooms:
                        raise RuntimeError("The actual primary RRC client did not join the room")
                    emit("rrc-joined", room=parts[1])
                    continue
                if parts[0] in ("rrc-send", "rrc-action") and len(parts) == 3:
                    if parts[0] == "rrc-send":
                        rrc_hub.send_message(parts[1], parts[2])
                    else:
                        rrc_hub.send_action(parts[1], parts[2])
                    emit("rrc-sent")
                    continue
                if parts[0] == "stats":
                    emit(
                        "stats",
                        served_pages=app.peer_settings["served_page_requests"],
                        served_files=app.peer_settings["served_file_requests"],
                        page_directory=app.pagespath,
                        registered_pages=app.node.servedpages,
                        propagation_messages=len(app.message_router.propagation_entries),
                        sync_state=app.message_router.propagation_transfer_state,
                    )
                    continue
                if parts[0] == "announce":
                    app.announce_now()
                    if app.message_router.propagation_node:
                        app.message_router.announce_propagation_node()
                    emit("announced")
                    continue
                if parts[0] == "propagation" and len(parts) >= 2:
                    app.set_user_selected_propagation_node(bytes.fromhex(parts[1]))
                    emit("propagation-selected", node=app.message_router.get_outbound_propagation_node().hex())
                    continue
                if parts[0] == "sync":
                    app.request_lxmf_sync(limit=int(parts[1]) if len(parts) >= 2 else None)
                    deadline = time.monotonic() + 120
                    while (app.message_router.propagation_transfer_state < LXMF.LXMRouter.PR_COMPLETE
                           and time.monotonic() < deadline):
                        time.sleep(0.1)
                    emit("synced", state=app.message_router.propagation_transfer_state,
                         result=app.message_router.propagation_transfer_last_result)
                    continue
                if parts[0] == "ingest" and len(parts) >= 2:
                    accepted = app.message_router.ingest_lxm_uri(parts[1])
                    emit("ingested", accepted=accepted)
                    continue
                if parts[0] not in ("send", "send-file", "send-propagated", "paper", "request") or len(parts) != 3:
                    raise ValueError("expected send, send-file, paper, request, ingest, stats, or quit")

                destination_hash = bytes.fromhex(parts[1])
                if len(destination_hash) != RNS.Reticulum.TRUNCATED_HASHLENGTH // 8:
                    raise ValueError("destination hash must be 32 hexadecimal characters")
                remote_identity = RNS.Identity.recall(destination_hash)
                if remote_identity is None:
                    RNS.Transport.request_path(destination_hash)
                    deadline = time.monotonic() + 30
                    next_request = time.monotonic() + 1
                    while remote_identity is None and time.monotonic() < deadline:
                        if time.monotonic() >= next_request:
                            RNS.Transport.request_path(destination_hash)
                            next_request = time.monotonic() + 1
                        time.sleep(0.2)
                        remote_identity = RNS.Identity.recall(destination_hash)
                if remote_identity is None:
                    emit("failed", reason="No announced identity for destination")
                    continue

                if parts[0] == "request":
                    destination = RNS.Destination(remote_identity, RNS.Destination.OUT, RNS.Destination.SINGLE,
                                                  "nomadnetwork", "node")
                    link = RNS.Link(destination)
                    deadline = time.monotonic() + 30
                    while link.status != RNS.Link.ACTIVE and time.monotonic() < deadline:
                        time.sleep(0.05)
                    if link.status != RNS.Link.ACTIVE:
                        raise RuntimeError("Native hosted node link did not establish")
                    link.identify(app.identity)
                    def response(receipt):
                        data = receipt.response
                        if hasattr(data, "read"):
                            data = data.read()
                        if not isinstance(data, bytes):
                            raise RuntimeError("Expected bytes or file response")
                        emit("response", size=len(data), sha256=hashlib.sha256(data).hexdigest(),
                             text=data.decode("utf-8", errors="replace") if len(data) < 4096 else None)
                        link.teardown()
                    link.request(parts[2], response_callback=response,
                                 failed_callback=lambda receipt: emit("request-failed", status=receipt.status))
                    continue

                destination = RNS.Destination(
                    remote_identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery"
                )
                fields = {}
                content = parts[2]
                if parts[0] == "send-file":
                    with open(parts[2], "rb") as stream:
                        attachment = stream.read(16000001)
                    if len(attachment) > 16000000:
                        raise ValueError("Reference attachment exceeds adapter bound")
                    fields[LXMF.FIELD_FILE_ATTACHMENTS] = [[os.path.basename(parts[2]), attachment]]
                    content = "upstream-attachment"
                message = LXMF.LXMessage(
                    destination,
                    app.lxmf_destination,
                    content,
                    "",
                    desired_method=(LXMF.LXMessage.PAPER if parts[0] == "paper"
                                    else LXMF.LXMessage.PROPAGATED if parts[0] == "send-propagated"
                                    else LXMF.LXMessage.DIRECT),
                    fields=fields,
                )
                if parts[0] == "paper":
                    message.pack()
                    emit("paper", uri=message.as_uri())
                    continue
                message.register_delivery_callback(
                    lambda current: emit("delivered", message_hash=current.hash.hex())
                )
                message.register_failed_callback(
                    lambda current: emit("failed", message_hash=current.hash.hex(), state=current.state)
                )
                app.message_router.handle_outbound(message)
                emit("queued", message_hash=message.hash.hex())
        except Exception as error:
            emit("error", message=str(error))

    threading.Thread(target=command_loop, daemon=True).start()
    NomadNetworkApp(configdir=args.config, rnsconfigdir=args.rnsconfig, daemon=True)


if __name__ == "__main__":
    main()
