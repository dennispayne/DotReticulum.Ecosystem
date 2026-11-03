"""Thin pinned LXMF peer for Sideband file-field observations, not a Sideband GUI."""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import traceback

from reference_environment import verify_reference


def emit(value):
    print("@@" + json.dumps(value), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, default=0)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    import RNS
    import LXMF

    args.config.mkdir(parents=True, exist_ok=True)
    interfaces = ""
    if args.tcp_server_port:
        interfaces += f"""
  [[Server]]
    type = TCPServerInterface
    enabled = yes
    listen_ip = 127.0.0.1
    listen_port = {args.tcp_server_port}
"""
    if args.rnsd_port:
        interfaces += f"""
  [[Client]]
    type = TCPClientInterface
    enabled = yes
    target_host = 127.0.0.1
    target_port = {args.rnsd_port}
"""
    (args.config / "config").write_text(
        "[reticulum]\n  enable_transport = no\n  share_instance = no\n"
        "\n[logging]\n  loglevel = 0\n\n[interfaces]\n" + interfaces, encoding="utf-8")
    RNS.Reticulum(configdir=str(args.config))
    identity = RNS.Identity()
    router = LXMF.LXMRouter(storagepath=str(args.config / "lxmf"), delivery_limit=8192)
    local = router.register_delivery_identity(identity, display_name="Attachment reference")

    class Announces:
        aspect_filter = "lxmf.delivery"

        def received_announce(self, destination_hash, announced_identity, app_data):
            emit({"event": "announce", "dest": destination_hash.hex()})

    RNS.Transport.register_announce_handler(Announces())

    def received(message):
        attachments = message.fields.get(LXMF.FIELD_FILE_ATTACHMENTS, [])
        emit({"event": "received", "sha256": hashlib.sha256(message.content).hexdigest(),
              "attachments": [{"name": entry[0], "size": len(entry[1]),
                               "sha256": hashlib.sha256(entry[1]).hexdigest()}
                              for entry in attachments]})

    router.register_delivery_callback(received)
    emit({"event": "ready", "dest": local.hash.hex()})
    for line in sys.stdin:
        parts = line.strip().split()
        if not parts:
            continue
        try:
            if parts[0] == "quit":
                break
            if parts[0] == "announce":
                router.announce(local.hash)
                emit({"event": "announced"})
            elif parts[0] == "send-file":
                destination_hash = bytes.fromhex(parts[1])
                remote = RNS.Identity.recall(destination_hash)
                if remote is None:
                    raise RuntimeError("The destination identity has not been announced")
                size = int(parts[2])
                if not 0 <= size <= 2 * 1024 * 1024:
                    raise ValueError("Attachment size is outside the test bound")
                seed = parts[3].encode("ascii")
                data = (seed * (size // len(seed) + 1))[:size]
                destination = RNS.Destination(remote, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
                message = LXMF.LXMessage(destination, local, "reference attachment", title="file",
                                        fields={LXMF.FIELD_FILE_ATTACHMENTS: [[parts[4], data]]},
                                        desired_method=LXMF.LXMessage.DIRECT)
                router.handle_outbound(message)
                emit({"event": "sent_queued", "sha256": hashlib.sha256(data).hexdigest()})
            else:
                raise ValueError("Unknown command")
        except Exception as error:
            traceback.print_exc(file=sys.stderr)
            emit({"event": "error", "message": str(error)})
    RNS.Reticulum.exit_handler()


if __name__ == "__main__":
    main()
