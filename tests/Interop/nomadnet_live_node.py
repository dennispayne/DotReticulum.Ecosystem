"""Thin command adapter around the pinned upstream NomadNetworkApp."""
import argparse
import json
import os
import sys
import traceback
import threading
import time
import subprocess
import hashlib
from pathlib import Path

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
                identity=app.identity.hash.hex(),
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
                     renderer=message.fields.get(LXMF.FIELD_RENDERER),
                     files=[{"name": item[0].decode("utf-8", errors="replace") if isinstance(item[0], bytes) else str(item[0]),
                             "size": len(item[1]), "sha256": hashlib.sha256(item[1]).hexdigest()}
                            for item in files])
            app.message_router.register_delivery_callback(observe_delivery)
            browser_links = []
            browser_lock = threading.Lock()
            browser_overflow = threading.Event()
            observing_browser = False
            node_announces = {}
            announce_lock = threading.Lock()
            announce_overflow = threading.Event()
            observing_announces = False
            client_announces = {}
            observing_clients = False

            class ClientAnnounceObserver:
                aspect_filter = "lxmf.delivery"
                receive_path_responses = False

                def received_announce(self, destination_hash, announced_identity, app_data):
                    with announce_lock:
                        previous = client_announces.get(destination_hash)
                        if ((len(client_announces) >= 256 and previous is None) or
                                (previous is not None and previous["count"] >= 1024)):
                            announce_overflow.set()
                            return
                        client_announces[destination_hash] = {
                            "count": previous["count"] + 1 if previous else 1,
                            "utc": time.time(),
                            "stamp_cost": LXMF.stamp_cost_from_app_data(app_data),
                            "name": LXMF.display_name_from_app_data(app_data),
                        }

            class NodeAnnounceObserver:
                aspect_filter = "nomadnetwork.node"

                def received_announce(self, destination_hash, announced_identity, app_data):
                    with announce_lock:
                        if len(node_announces) >= 256 and destination_hash not in node_announces:
                            announce_overflow.set()
                            return
                        node_announces[destination_hash] = node_announces.get(destination_hash, 0) + 1

            def observe_browser_connected(link):
                app.node.peer_connected(link)

                def closed(current):
                    app.node.peer_disconnected(current)
                    remote = current.get_remote_identity()
                    with browser_lock:
                        if len(browser_links) < 64:
                            browser_links.append(remote.hash.hex() if remote else None)
                        else:
                            browser_overflow.set()

                link.set_link_closed_callback(closed)
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
                if parts[0] == "rrc-send-json" and len(parts) == 3:
                    text = json.loads(parts[2])
                    if not isinstance(text, str):
                        raise ValueError("RRC JSON composition requires a string")
                    rrc_hub.send_message(parts[1], text)
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
                if parts[0] == "profile-stats":
                    emit("profile", name=app.get_display_name(), last_announce=app.peer_settings["last_announce"])
                    continue
                if parts[0] == "set-display-name" and len(parts) >= 2:
                    app.set_display_name(raw_line.rstrip("\r\n").split(" ", 1)[1])
                    emit("profile", name=app.get_display_name(), last_announce=app.peer_settings["last_announce"])
                    continue
                if parts[0] == "wait-network":
                    deadline = time.monotonic() + 30
                    while time.monotonic() < deadline:
                        clients = [interface for interface in RNS.Transport.interfaces
                                   if type(interface).__name__ == "TCPClientInterface"]
                        if clients and all(interface.online for interface in clients):
                            emit("network-ready", connected=len(clients))
                            break
                        time.sleep(0.05)
                    else:
                        raise RuntimeError("Configured reference TCP clients did not reconnect")
                    continue
                if parts[0] == "path-stats" and len(parts) == 2:
                    target = bytes.fromhex(parts[1])
                    if len(target) != 16:
                        raise RuntimeError("Path observation requires a destination hash")
                    interface = RNS.Transport.next_hop_interface(target)
                    next_hop = RNS.Transport.next_hop(target)
                    emit("path-stats", target=target.hex(), hops=RNS.Transport.hops_to(target),
                         next_hop=next_hop.hex() if isinstance(next_hop, bytes) else None,
                         interface=str(interface) if interface else None,
                         interface_id=id(interface) if interface else None,
                         online=getattr(interface, "online", None),
                         active=interface in RNS.Transport.interfaces if interface else False,
                         interfaces=[{"name": str(item), "id": id(item), "online": getattr(item, "online", None)}
                                     for item in RNS.Transport.interfaces[:32]])
                    continue
                if parts[0] == "observe-client-announces":
                    if observing_clients:
                        raise RuntimeError("Client announce observation already enabled")
                    RNS.Transport.register_announce_handler(ClientAnnounceObserver())
                    observing_clients = True
                    emit("observing-client-announces")
                    continue
                if parts[0] == "client-announces" and len(parts) >= 2:
                    target = bytes.fromhex(parts[1])
                    minimum = int(parts[2]) if len(parts) == 3 else 0
                    if not observing_clients or len(target) != 16 or not 0 <= minimum <= 1024:
                        raise RuntimeError("Enable client observation and select a bounded target/count")
                    deadline = time.monotonic() + 10
                    while time.monotonic() < deadline:
                        if announce_overflow.is_set():
                            raise RuntimeError("Announce observation overflow; evidence is incomplete")
                        with announce_lock:
                            observation = dict(client_announces.get(target, {"count": 0}))
                        if observation["count"] >= minimum:
                            break
                        time.sleep(0.01)
                    else:
                        raise RuntimeError("Expected client announce was not observed")
                    emit("client-announces", delivery=target.hex(), **observation)
                    continue
                if parts[0] in ("observe-node-announces", "observe-propagation-announces"):
                    if observing_announces:
                        raise RuntimeError("Node announce observation already enabled")
                    observer = NodeAnnounceObserver()
                    if parts[0] == "observe-propagation-announces":
                        observer.aspect_filter = "lxmf.propagation"
                    RNS.Transport.register_announce_handler(observer)
                    observing_announces = True
                    emit("observing-node-announces")
                    continue
                if parts[0] == "node-announces" and len(parts) >= 2:
                    target = bytes.fromhex(parts[1])
                    minimum = int(parts[2]) if len(parts) == 3 else 0
                    if not observing_announces or len(target) != 16 or not 0 <= minimum <= 1024:
                        raise RuntimeError("Enable node observation and select a bounded target/count")
                    if announce_overflow.is_set():
                        raise RuntimeError("Node announce observation overflow; evidence is incomplete")
                    deadline = time.monotonic() + 10
                    while time.monotonic() < deadline:
                        with announce_lock:
                            count = node_announces.get(target, 0)
                        if count >= minimum:
                            break
                        time.sleep(0.01)
                    else:
                        raise RuntimeError("Expected node announce was not observed")
                    emit("node-announces", count=count, node=target.hex())
                    continue
                if parts[0] == "observe-browser-links":
                    if observing_browser:
                        raise RuntimeError("Browser observation already enabled")
                    app.node.destination.set_link_established_callback(observe_browser_connected)
                    observing_browser = True
                    emit("observing-browser-links")
                    continue
                if parts[0] == "browser-links" and len(parts) == 2:
                    count = int(parts[1])
                    if not observing_browser or not 0 <= count <= 64:
                        raise RuntimeError("Enable browser observation and use a bounded count")
                    if browser_overflow.is_set():
                        raise RuntimeError("Browser observation overflow; evidence is incomplete")
                    deadline = time.monotonic() + 10
                    while time.monotonic() < deadline:
                        with browser_lock:
                            if len(browser_links) >= count:
                                observed = list(browser_links)
                                browser_links.clear()
                                break
                        time.sleep(0.01)
                    else:
                        raise RuntimeError("Browser observation did not reach expected link count")
                    emit("browser-links", identities=observed)
                    continue
                if parts[0] == "conversation-stats":
                    emit("conversations", entries=[
                        {"hash": entry[0], "unread": entry[4], "failures": entry[6]}
                        for entry in app.conversations()])
                    continue
                if parts[0] == "config-stats":
                    emit("configuration", enable_node=app.enable_node, node_name=app.node_name,
                         compose_in_markdown=app.compose_markdown, rrc_render_markdown=app.rrc_ui_render_markdown,
                         rrc_render_micron=app.rrc_ui_render_micron,
                         enable_client=app.enable_client, client_announce_at_start=app.peer_announce_at_start,
                         client_announce_interval=app.announce_interval,
                         client_delivery_registered=app.lxmf_destination is not None,
                         disable_propagation=app.disable_propagation, announce_interval=app.node_announce_interval,
                         periodic_lxmf_sync=app.periodic_lxmf_sync, lxmf_sync_interval=app.lxmf_sync_interval,
                         lxmf_sync_limit=app.lxmf_sync_limit, try_propagation_on_send_fail=app.try_propagation_on_fail,
                         required_stamp_cost=app.required_stamp_cost, accept_invalid_stamps=app.accept_invalid_stamps,
                         max_accepted_size=app.lxmf_max_incoming_size, pages_path=app.pagespath,
                         files_path=app.filespath, downloads_path=app.downloads_path,
                         node_announce_at_start=app.node_announce_at_start,
                         page_refresh_interval=app.page_refresh_interval,
                         file_refresh_interval=app.file_refresh_interval)
                    continue
                if parts[0] == "conversation-read" and len(parts) == 2:
                    app.mark_conversation_read(parts[1])
                    emit("conversation-read", hash=parts[1])
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
                if parts[0] == "decode-qr" and len(parts) == 2:
                    import zxingcpp
                    from PIL import Image
                    if Path(parts[1]).stat().st_size > 12 * 1024 * 1024:
                        raise ValueError("QR image exceeds adapter bound")
                    with Image.open(parts[1]) as image:
                        if image.width > 2048 or image.height > 2048 or image.width * image.height > 4 * 1024 * 1024:
                            raise ValueError("QR raster exceeds adapter bound")
                        decoded = zxingcpp.read_barcode(image)
                        if decoded is None or not decoded.text.startswith("lxm://"):
                            raise ValueError("Actual zxing-cpp did not decode an LXMF URI")
                        emit("qr-decoded", uri=decoded.text, width=image.width, height=image.height)
                    continue
                if parts[0] not in ("send", "send-file", "send-files", "send-formatted", "send-propagated", "paper", "request", "request-media") or len(parts) != 3:
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

                if parts[0] in ("request", "request-media"):
                    media_request = parts[0] == "request-media"
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
                        if media_request:
                            try:
                                from nomadnet.ui.textui.images._imagedata import ImageData
                                image_path = Path(args.config) / "reference-media.webp"
                                image_path.write_bytes(data)
                                image = ImageData(str(image_path), remote_source=True)
                                if not image.ok:
                                    emit("error", reason=image.error)
                                else:
                                    emit("image-response", width=image.width, height=image.height,
                                         source=image.source_format, format=image.format,
                                         backend=image.conversion_backend, size=len(image.data))
                            except Exception as error:
                                emit("error", context="actual-primary-image-load", reason=str(error))
                            finally:
                                link.teardown()
                            return
                        emit("response", size=len(data), sha256=hashlib.sha256(data).hexdigest(),
                             text=data.decode("utf-8", errors="replace") if len(data) < 4096 else None)
                        link.teardown()
                    link.request("/media" if media_request else parts[2],
                                 data={"path": parts[2], "key": None} if media_request else None, response_callback=response,
                                 failed_callback=lambda receipt: emit("request-failed", status=receipt.status))
                    continue

                destination = RNS.Destination(
                    remote_identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery"
                )
                fields = {}
                content = parts[2]
                if parts[0] == "send-formatted":
                    payload = json.loads(parts[2])
                    if not isinstance(payload, dict) or not isinstance(payload.get("text"), str) or type(payload.get("renderer")) is not int:
                        raise ValueError("Formatted composition requires text and integer renderer")
                    content = payload["text"]
                    fields[LXMF.FIELD_RENDERER] = payload["renderer"]
                if parts[0] == "send-file":
                    with open(parts[2], "rb") as stream:
                        attachment = stream.read(16000001)
                    if len(attachment) > 16000000:
                        raise ValueError("Reference attachment exceeds adapter bound")
                    fields[LXMF.FIELD_FILE_ATTACHMENTS] = [[os.path.basename(parts[2]), attachment]]
                    content = "upstream-attachment"
                if parts[0] == "send-files":
                    paths = json.loads(parts[2])
                    if not isinstance(paths, list) or not 1 <= len(paths) <= 64 or any(not isinstance(path, str) for path in paths):
                        raise ValueError("Reference multi-attachment command requires 1..64 file paths")
                    attachments = []
                    total = 0
                    for path in paths:
                        with open(path, "rb") as stream:
                            attachment = stream.read(16000001 - total)
                        total += len(attachment)
                        if total > 16000000:
                            raise ValueError("Reference attachments exceed aggregate adapter bound")
                        attachments.append([os.path.basename(path), attachment])
                    fields[LXMF.FIELD_FILE_ATTACHMENTS] = attachments
                    content = "upstream-multiple-attachments"
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
            traceback.print_exc(file=sys.stderr)
            emit("error", message=str(error))

    threading.Thread(target=command_loop, daemon=True).start()
    NomadNetworkApp(configdir=args.config, rnsconfigdir=args.rnsconfig, daemon=True)


if __name__ == "__main__":
    main()
