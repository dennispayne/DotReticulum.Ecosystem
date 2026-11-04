"""Thin lifecycle adapter for an unmodified pinned Python RNS transport."""
import argparse
import json
import os

import RNS

from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    args = parser.parse_args()
    verify_reference()
    RNS.Reticulum(configdir=args.config, loglevel=RNS.LOG_ERROR)
    print("@@" + json.dumps({"event": "transport-ready", "module": RNS.__file__}), flush=True)
    for line in __import__("sys").stdin:
        parts = line.strip().split()
        if not parts:
            continue
        if parts == ["quit"]:
            RNS.Reticulum.exit_handler()
            os._exit(0)
        if len(parts) == 2 and parts[0] == "path-stats":
            destination = bytes.fromhex(parts[1])
            if len(destination) != 16:
                raise ValueError("Transport path observation requires a 16-byte hash")
            interface = RNS.Transport.next_hop_interface(destination)
            print("@@" + json.dumps({
                "event": "path-stats", "target": destination.hex(),
                "hops": RNS.Transport.hops_to(destination),
                "online": getattr(interface, "online", None),
                "active": interface in RNS.Transport.interfaces if interface else False,
            }), flush=True)
            continue
        raise ValueError("Expected quit or path-stats")


if __name__ == "__main__":
    main()
