"""Actual pinned PN with a disclosed public link-teardown fault, no protocol replacement."""
import argparse
import os
from pathlib import Path
import threading

import LXMF
import RNS

from live_lxmf_node import command_lines, emit, write_config
from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, default=0)
    parser.add_argument("--identity", type=Path, required=True)
    parser.add_argument("--storage", type=Path, required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    write_config(str(args.config), args.tcp_server_port, args.rnsd_port, True)
    RNS.Reticulum(configdir=str(args.config), loglevel=RNS.LOG_ERROR)
    identity = RNS.Identity.from_file(str(args.identity)) if args.identity.exists() else RNS.Identity()
    if identity is None:
        raise RuntimeError("Could not load PN identity")
    identity.to_file(str(args.identity))
    if os.name != "nt":
        args.identity.chmod(0o600)
    router = LXMF.LXMRouter(identity=identity, storagepath=str(args.storage), autopeer=False)
    router.set_message_storage_limit(megabytes=8)
    router.enable_propagation()
    original = router.propagation_destination.callbacks.link_established
    gate = threading.Lock()
    state = {"fault": True, "teardowns": 0}

    def established(link):
        with gate:
            fault = state["fault"]
            if fault:
                state["teardowns"] += 1
            count = state["teardowns"]
        if fault:
            emit({"event": "pn-teardown", "count": count, "link": link.link_id.hex()})
            link.teardown()
        else:
            original(link)

    router.propagation_destination.set_link_established_callback(established)

    def announce():
        router.propagation_destination.announce(app_data=router.get_propagation_node_app_data())

    announce()
    emit({"event": "ready", "dest": router.propagation_destination.hash.hex(),
          "fault": True, "stamp_cost": router.propagation_stamp_cost})
    for line in command_lines():
        parts = line.strip().split()
        if not parts:
            continue
        if parts[0] == "fault" and len(parts) == 2 and parts[1] in ("on", "off"):
            with gate:
                state["fault"] = parts[1] == "on"
                snapshot = dict(state)
            emit({"event": "fault", **snapshot})
        elif parts[0] == "announce":
            announce()
            emit({"event": "announced"})
        elif parts[0] == "pn-count" and len(parts) == 2:
            target = bytes.fromhex(parts[1])
            entries = tuple(router.propagation_entries.values())
            emit({"event": "pn-count", "count": sum(entry[0] == target for entry in entries)})
        elif parts[0] == "quit":
            router.exit_handler()
            RNS.Reticulum.exit_handler()
            os._exit(0)
        else:
            raise ValueError("Unsupported PN retry observation")


if __name__ == "__main__":
    main()
