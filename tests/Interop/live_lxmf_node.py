"""Live Python LXMF node (upstream LXMF 1.2.1 LXMRouter) for the DotLXMF interop checks.

Upstream: Reticulum-hosted snapshot pinned by reference_environment.py (PYTHONPATH) + the pinned "lxmf" package from PyPI.
Driven by tests/Interop/PyLinkNode.cs from DOTRETICULUM_LIVE=1 xUnit tests (DotLXMF.Tests/LiveLxmfTests.cs); not a unit test.

Interfaces: --tcp-server-port opens a TCPServerInterface on 127.0.0.1 (the .NET side connects to it), --rnsd-port a TCPClientInterface to a .NET rnsd.
One LXMRouter with one delivery destination "lxmf.delivery" (display name "PyLxmf").

stdout: lines starting with "@@" followed by JSON.
  {"event":"ready", "dest", "identity_hash"}
  {"event":"announce", "dest", "app_data", "display_name", "hops"}     lxmf.delivery announce seen
  {"event":"announced", "dest"}                                       reply to "announce"
  {"event":"received", "hash", "source", "title", "size", "sha256", "method", "signature_validated", "fields"}
  {"event":"delivered", "hash"} / {"event":"failed", "hash", "state"} outcome of "send"
  {"event":"sent_queued", "hash", "method", "representation"}          reply to "send" once packed and queued
stdin: announce | send <dest hex> <opportunistic|direct> <size> <seed> <title> | quit
  Content of a sent message: deterministic bytes (random.Random(seed).randbytes(size)); fields carry {1: [b"py-field"]}.
"""
import argparse
import hashlib
import json
import os
import random
import sys
import threading
import time

import RNS
import LXMF
import RNS.vendor.umsgpack as msgpack
from reference_environment import verify_reference

LOCK = threading.Lock()


def emit(obj):
    with LOCK:
        sys.stdout.write("@@" + json.dumps(obj) + "\n")
        sys.stdout.flush()


def command_lines():
    # A buffered stdin read holds its lock while idle. Forked upstream stamp
    # workers close stdin during startup and would inherit that locked reader.
    pending = bytearray()
    while True:
        chunk = os.read(sys.stdin.fileno(), 4096)
        if not chunk:
            if pending:
                yield pending.decode("utf-8")
            return
        pending.extend(chunk)
        while (end := pending.find(b"\n")) >= 0:
            if end > 65536:
                raise ValueError("Harness command exceeds 64 KiB")
            yield pending[:end].decode("utf-8")
            del pending[:end + 1]
        if len(pending) > 65536:
            raise ValueError("Harness command exceeds 64 KiB")


def write_config(path, tcp_server_port, rnsd_port, enable_transport=False):
    interfaces = ""
    if tcp_server_port:
        interfaces += f"""  [[PyTCPServer]]
    type = TCPServerInterface
    enabled = yes
    listen_ip = 127.0.0.1
    listen_port = {tcp_server_port}
"""
    if rnsd_port:
        interfaces += f"""  [[ToDotNet]]
    type = TCPClientInterface
    enabled = yes
    target_host = 127.0.0.1
    target_port = {rnsd_port}
"""
    config = f"""[reticulum]
  enable_transport = {"Yes" if enable_transport else "No"}
  share_instance = No
  instance_name = dotreticulum_interop_p07
  panic_on_interface_error = No

[logging]
  loglevel = 4

[interfaces]
{interfaces}"""
    os.makedirs(path, exist_ok=True)
    with open(os.path.join(path, "config"), "w", encoding="utf-8") as f:
        f.write(config)


class Handler:
    aspect_filter = "lxmf.delivery"

    def received_announce(self, destination_hash, announced_identity, app_data):
        emit({"event": "announce", "dest": destination_hash.hex(), "app_data": app_data.hex() if app_data else "",
              "display_name": LXMF.display_name_from_app_data(app_data), "hops": RNS.Transport.hops_to(destination_hash)})


class PropagationHandler:
    aspect_filter = "lxmf.propagation"

    def received_announce(self, destination_hash, announced_identity, app_data):
        emit({"event": "pn-announce", "dest": destination_hash.hex(), "app_data": app_data.hex() if app_data else "",
              "hops": RNS.Transport.hops_to(destination_hash)})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, default=0)
    parser.add_argument("--stamp-cost", type=int, default=None)
    parser.add_argument("--loglevel", type=int, default=RNS.LOG_WARNING)
    parser.add_argument("--transport", action="store_true")
    parser.add_argument("--delivery-limit-kb", type=int, default=1000)
    parser.add_argument("--propagation-limit-kb", type=int, default=256)
    parser.add_argument("--propagation-sync-limit-kb", type=float, default=10240)
    parser.add_argument("--identity")
    parser.add_argument("--storage")
    parser.add_argument("--enforce-ratchets", action="store_true")
    parser.add_argument("--parallel-pn-validation", action="store_true")
    parser.add_argument("--observe-resources", action="store_true")
    parser.add_argument("--no-compression", action="store_true")
    args = parser.parse_args()

    verify_reference(require_lxmf=True)
    if args.delivery_limit_kb <= 0 or args.propagation_limit_kb <= 0 or args.propagation_sync_limit_kb <= 0:
        parser.error("Delivery and propagation limits must be positive")
    write_config(args.config, args.tcp_server_port, args.rnsd_port, args.transport)
    RNS.Reticulum(configdir=args.config, loglevel=args.loglevel)
    RNS.Transport.register_announce_handler(Handler())
    RNS.Transport.register_announce_handler(PropagationHandler())

    identity = RNS.Identity.from_file(args.identity) if args.identity else RNS.Identity()
    if identity is None:
        parser.error("Could not load the requested identity")
    router = LXMF.LXMRouter(identity=identity, storagepath=args.storage or os.path.join(args.config, "lxmf"), autopeer=False,
                            enforce_stamps=True, enforce_ratchets=args.enforce_ratchets,
                            propagation_cost=13, propagation_limit=args.propagation_limit_kb,
                            sync_limit=args.propagation_sync_limit_kb,
                            sequential_validation=not args.parallel_pn_validation,
                            delivery_limit=args.delivery_limit_kb)
    local = router.register_delivery_identity(identity, display_name="PyLxmf", stamp_cost=args.stamp_cost)
    if args.observe_resources:
        def established(link):
            router.delivery_link_established(link)
            started = link.callbacks.resource_started

            def observed(resource):
                started(resource)
                emit({"event": "resource-started", "compressed": resource.compressed})

            link.set_resource_started_callback(observed)

        local.set_link_established_callback(established)

    def on_message(message):
        emit({"event": "received", "hash": message.hash.hex(), "source": message.source_hash.hex(), "title": message.title_as_string(),
              "size": len(message.content), "sha256": hashlib.sha256(message.content).hexdigest(), "method": message.method,
              "signature_validated": message.signature_validated,
              "ratchet_id": message.ratchet_id.hex() if message.ratchet_id else None,
              "stamp_valid": message.stamp_valid, "stamp_size": len(message.stamp) if message.stamp else 0,
              "has_ticket": LXMF.FIELD_TICKET in message.fields,
              "fields_sha256": hashlib.sha256(msgpack.packb(message.fields)).hexdigest(),
              "fields": {str(k): ("<ticket-redacted>" if k == LXMF.FIELD_TICKET else v.hex() if isinstance(v, bytes) else repr(v))
                         for k, v in (message.fields or {}).items()}})

    router.register_delivery_callback(on_message)
    emit({"event": "ready", "dest": local.hash.hex(), "identity_hash": identity.hash.hex()})
    prepared = {}

    for line in command_lines():
        parts = line.strip().split()
        if not parts:
            continue
        cmd = parts[0]
        if cmd == "prepare-opportunistic" and len(parts) == 4:
            if len(prepared) >= 4:
                raise ValueError("Prepared packet capacity exceeded")
            destination_hash = bytes.fromhex(parts[1])
            remote = RNS.Identity.recall(destination_hash)
            if remote is None or RNS.Identity.get_ratchet(destination_hash) is None:
                raise ValueError("The recipient has not announced an identity and ratchet")
            destination = RNS.Destination(remote, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
            content = random.Random(parts[2]).randbytes(96)
            message = LXMF.LXMessage(destination, local, content, parts[3], desired_method=LXMF.LXMessage.OPPORTUNISTIC)
            message.defer_stamp = False
            message.pack()
            packet = RNS.Packet(destination, message.packed[LXMF.LXMessage.DESTINATION_LENGTH:])
            packet.pack()
            prepared[parts[3]] = (packet, message.hash)
            emit({"event": "prepared", "tag": parts[3], "hash": message.hash.hex(),
                  "ratchet_id": packet.ratchet_id.hex(), "sha256": hashlib.sha256(content).hexdigest()})
            continue
        if cmd == "send-prepared" and len(parts) == 2:
            packet, message_hash = prepared.pop(parts[1])
            receipt = packet.send()
            if not receipt:
                raise RuntimeError("The prepared packet could not be sent")
            receipt.set_delivery_callback(lambda received, hashed=message_hash:
                                          emit({"event": "prepared-delivered", "hash": hashed.hex()}))
            emit({"event": "prepared-sent", "hash": message_hash.hex()})
            continue
        if cmd == "quit":
            break
        if cmd == "announce":
            if args.no_compression:
                app_data = msgpack.unpackb(router.get_announce_app_data(local.hash))
                app_data[2] = []
                local.announce(app_data=msgpack.packb(app_data))
            else:
                router.announce(local.hash)
            emit({"event": "announced", "dest": local.hash.hex()})
        elif cmd == "enable-pn":
            router.enable_propagation()
            router.propagation_destination.announce(app_data=router.get_propagation_node_app_data())
            emit({"event": "pn-ready", "dest": router.propagation_destination.hash.hex()})
        elif cmd == "use-pn":
            router.set_outbound_propagation_node(bytes.fromhex(parts[1]))
            emit({"event": "pn-selected"})
        elif cmd == "pn-count":
            emit({"event": "pn-count", "count": len(router.propagation_entries)})
        elif cmd == "retrieve":
            before = len(router.locally_delivered_transient_ids)
            router.request_messages_from_propagation_node(identity)
            deadline = time.time() + 60
            while router.propagation_transfer_state < LXMF.LXMRouter.PR_COMPLETE and time.time() < deadline:
                time.sleep(0.05)
            emit({"event": "retrieved", "state": router.propagation_transfer_state,
                  "count": router.propagation_transfer_last_result})
        elif cmd == "cancel-retrieval":
            router.cancel_propagation_node_requests()
            emit({"event": "retrieval-cancelled"})
        elif cmd == "peer-sync":
            peer_hash = bytes.fromhex(parts[1])
            config = msgpack.unpackb(RNS.Identity.recall_app_data(peer_hash))
            router.peer(peer_hash, config[1], config[3], config[4], config[5][0], config[5][1], config[5][2], config[6])
            peer = router.peers[peer_hash]
            for transient_id in router.propagation_entries:
                peer.add_unhandled_message(transient_id)
            peer.generate_peering_key()
            peer.sync()
            deadline = time.time() + 60
            while peer.state != peer.IDLE and time.time() < deadline:
                time.sleep(0.05)
            emit({"event": "peer-synced", "outgoing": peer.outgoing, "state": peer.state})
        elif cmd in ("send", "send-plain", "send-extended"):
            dest_hash = bytes.fromhex(parts[1])
            method = {"opportunistic": LXMF.LXMessage.OPPORTUNISTIC, "direct": LXMF.LXMessage.DIRECT,
                      "propagated": LXMF.LXMessage.PROPAGATED}[parts[2]]
            size, seed, title = int(parts[3]), parts[4], parts[5] if len(parts) > 5 else ""
            if cmd == "send-plain":
                destination = RNS.Destination(None, RNS.Destination.OUT, RNS.Destination.PLAIN, "lxmf", "delivery")
                if destination.hash != dest_hash:
                    raise ValueError("Plain destination hash does not match lxmf.delivery")
            else:
                remote_identity = identity if dest_hash == local.hash else RNS.Identity.recall(dest_hash)
                if remote_identity is None:
                    RNS.Transport.request_path(dest_hash)
                    deadline = time.time() + 20
                    while remote_identity is None and time.time() < deadline:
                        time.sleep(0.2)
                        remote_identity = RNS.Identity.recall(dest_hash)
                destination = RNS.Destination(remote_identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
            content = random.Random(seed).randbytes(size)
            fields = ({None: "nil", (1, b"two", None): "tuple", float("inf"): "positive", float("nan"): "nan",
                       0xFC: {None: "nested", (1, 2): b"\x01"}} if cmd == "send-extended" else {1: [b"py-field"]})
            message = LXMF.LXMessage(destination, local, content, title, fields=fields, desired_method=method,
                                    include_ticket=len(parts) > 6 and parts[6] == "ticket")
            message.register_delivery_callback(lambda m: emit({"event": "delivered", "hash": m.hash.hex(), "state": m.state}))
            message.register_failed_callback(lambda m: emit({"event": "failed", "hash": m.hash.hex(), "state": m.state}))
            router.handle_outbound(message)
            emit({"event": "sent_queued", "hash": message.hash.hex(), "method": message.method, "representation": message.representation,
                  "stamp_size": len(message.stamp) if message.stamp else 0,
                  "fields_sha256": hashlib.sha256(msgpack.packb(message.fields)).hexdigest(),
                  "size": len(content), "sha256": hashlib.sha256(content).hexdigest()})

    os._exit(0)


if __name__ == "__main__":
    main()
