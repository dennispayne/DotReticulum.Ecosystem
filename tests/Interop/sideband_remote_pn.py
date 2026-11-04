"""Pinned real LXMF propagation node; API-only durable fixture and observations."""
import argparse
import os
from pathlib import Path

import LXMF
import RNS

from live_lxmf_node import command_lines, emit, write_config
from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, default=0)
    parser.add_argument("--transport", action="store_true")
    parser.add_argument("--identity", type=Path, required=True)
    parser.add_argument("--storage", type=Path, required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    write_config(str(args.config), args.tcp_server_port, args.rnsd_port, args.transport)
    RNS.Reticulum(configdir=str(args.config), loglevel=RNS.LOG_ERROR)
    if args.identity.exists():
        identity = RNS.Identity.from_file(str(args.identity))
    else:
        identity = RNS.Identity()
        identity.to_file(str(args.identity))
        if os.name != "nt":
            args.identity.chmod(0o600)
    if identity is None:
        raise RuntimeError("Durable propagation identity could not be loaded")
    router = LXMF.LXMRouter(identity=identity, storagepath=str(args.storage), autopeer=False)
    router.set_message_storage_limit(megabytes=8)
    router.enable_propagation()

    def announce():
        router.propagation_destination.announce(app_data=router.get_propagation_node_app_data())

    announce()
    emit({"event": "ready", "dest": router.propagation_destination.hash.hex(),
          "propagation_cost": router.propagation_stamp_cost, "storage_limit_mb": 8})
    for line in command_lines():
        parts = line.strip().split()
        if not parts:
            continue
        if parts[0] == "announce":
            announce()
            emit({"event": "announced"})
        elif parts[0] == "pn-count" and len(parts) in (1, 2):
            entries = tuple(router.propagation_entries.values())
            if len(parts) == 2:
                # Actual pinned LXMRouter entry[0] is the destination hash (enable_propagation/ingest).
                destination = bytes.fromhex(parts[1])
                entries = tuple(entry for entry in entries if entry[0] == destination)
            emit({"event": "pn-count", "count": len(entries)})
        elif parts[0] == "quit":
            router.exit_handler()
            RNS.Reticulum.exit_handler()
            os._exit(0)
        else:
            raise ValueError("Unsupported propagation observation operation")


if __name__ == "__main__":
    main()
