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
from reference_environment import verify_reference

LOCK = threading.Lock()


def emit(obj):
    with LOCK:
        sys.stdout.write("@@" + json.dumps(obj) + "\n")
        sys.stdout.flush()


def write_config(path, tcp_server_port, rnsd_port):
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
  enable_transport = No
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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, default=0)
    args = parser.parse_args()

    verify_reference(require_lxmf=True)
    write_config(args.config, args.tcp_server_port, args.rnsd_port)
    RNS.Reticulum(configdir=args.config, loglevel=RNS.LOG_WARNING)
    RNS.Transport.register_announce_handler(Handler())

    identity = RNS.Identity()
    router = LXMF.LXMRouter(identity=identity, storagepath=os.path.join(args.config, "lxmf"), autopeer=False)
    local = router.register_delivery_identity(identity, display_name="PyLxmf")

    def on_message(message):
        emit({"event": "received", "hash": message.hash.hex(), "source": message.source_hash.hex(), "title": message.title_as_string(),
              "size": len(message.content), "sha256": hashlib.sha256(message.content).hexdigest(), "method": message.method,
              "signature_validated": message.signature_validated,
              "fields": {str(k): (v.hex() if isinstance(v, bytes) else repr(v)) for k, v in (message.fields or {}).items()}})

    router.register_delivery_callback(on_message)
    emit({"event": "ready", "dest": local.hash.hex(), "identity_hash": identity.hash.hex()})

    for line in sys.stdin:
        parts = line.strip().split()
        if not parts:
            continue
        cmd = parts[0]
        if cmd == "quit":
            break
        if cmd == "announce":
            router.announce(local.hash)
            emit({"event": "announced", "dest": local.hash.hex()})
        elif cmd == "send":
            dest_hash = bytes.fromhex(parts[1])
            method = LXMF.LXMessage.OPPORTUNISTIC if parts[2] == "opportunistic" else LXMF.LXMessage.DIRECT
            size, seed, title = int(parts[3]), parts[4], parts[5] if len(parts) > 5 else ""
            remote_identity = RNS.Identity.recall(dest_hash)
            if remote_identity is None:
                RNS.Transport.request_path(dest_hash)
                deadline = time.time() + 20
                while remote_identity is None and time.time() < deadline:
                    time.sleep(0.2)
                    remote_identity = RNS.Identity.recall(dest_hash)
            destination = RNS.Destination(remote_identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
            content = random.Random(seed).randbytes(size)
            message = LXMF.LXMessage(destination, local, content, title, fields={1: [b"py-field"]}, desired_method=method)
            message.register_delivery_callback(lambda m: emit({"event": "delivered", "hash": m.hash.hex()}))
            message.register_failed_callback(lambda m: emit({"event": "failed", "hash": m.hash.hex(), "state": m.state}))
            router.handle_outbound(message)
            emit({"event": "sent_queued", "hash": message.hash.hex(), "method": message.method, "representation": message.representation,
                  "size": len(content), "sha256": hashlib.sha256(content).hexdigest()})

    os._exit(0)


if __name__ == "__main__":
    main()
