"""Thin live LXMF peer for the native NomadNet CLI acceptance test.

The protocol implementation is the pinned upstream RNS/LXMF imported from the
live-test environment; this file only exposes delivery operations to Pester.
"""
import argparse
import json
import os
import sys
import threading
import time

import LXMF
import RNS

from reference_environment import verify_reference


_output_lock = threading.Lock()


def emit(event, **values):
    with _output_lock:
        sys.stdout.write("@@" + json.dumps({"event": event, **values}) + "\n")
        sys.stdout.flush()


def commands():
    pending = bytearray()
    while True:
        data = os.read(sys.stdin.fileno(), 4096)
        if not data:
            return
        pending.extend(data)
        while b"\n" in pending:
            line, _, remainder = pending.partition(b"\n")
            pending = bytearray(remainder)
            if len(line) > 65536:
                raise ValueError("peer command exceeds 64 KiB")
            yield line.decode("utf-8")
        if len(pending) > 65536:
            raise ValueError("peer command exceeds 64 KiB")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--tcp-server-port", required=True, type=int)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    os.makedirs(args.config, exist_ok=True)
    with open(os.path.join(args.config, "config"), "w", encoding="utf-8") as config:
        config.write(f"""[reticulum]
  enable_transport = No
  share_instance = No
  instance_name = nomadnet_live_reference
  panic_on_interface_error = No

[logging]
  loglevel = 4

[interfaces]
  [[NomadNetPeerServer]]
    type = TCPServerInterface
    enabled = Yes
    listen_ip = 127.0.0.1
    listen_port = {args.tcp_server_port}
""")
    RNS.Reticulum(configdir=args.config, loglevel=RNS.LOG_ERROR)
    identity = RNS.Identity()
    router = LXMF.LXMRouter(identity=identity, storagepath=os.path.join(args.config, "lxmf"), autopeer=False)
    local = router.register_delivery_identity(identity, display_name="NomadNet live peer")

    def received(message):
        try:
            text = message.content.decode("utf-8", errors="strict")
        except UnicodeDecodeError:
            text = None
        emit("received", source=message.source_hash.hex(), content=text, title=message.title_as_string())

    router.register_delivery_callback(received)
    emit("ready", destination=local.hash.hex())
    for line in commands():
        parts = line.strip().split(" ", 2)
        if not parts:
            continue
        if parts[0] == "quit":
            break
        if parts[0] == "announce":
            router.announce(local.hash)
            emit("announced", destination=local.hash.hex())
        elif parts[0] == "send" and len(parts) == 3:
            destination_hash = bytes.fromhex(parts[1])
            remote = RNS.Identity.recall(destination_hash)
            if remote is None:
                RNS.Transport.request_path(destination_hash)
                deadline = time.time() + 30
                while remote is None and time.time() < deadline:
                    time.sleep(0.2)
                    remote = RNS.Identity.recall(destination_hash)
            if remote is None:
                raise RuntimeError("The native NomadNet destination identity was not announced")
            destination = RNS.Destination(remote, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
            message = LXMF.LXMessage(destination, local, parts[2], "", desired_method=LXMF.LXMessage.DIRECT)
            message.register_delivery_callback(lambda current: emit("delivered", message_hash=current.hash.hex()))
            message.register_failed_callback(lambda current: emit("failed", message_hash=current.hash.hex(), state=current.state))
            router.handle_outbound(message)
            emit("queued", message_hash=message.hash.hex())
        else:
            raise ValueError("expected announce, send <hash> <text>, or quit")
    os._exit(0)


if __name__ == "__main__":
    main()
