"""Thin lifecycle adapter for an unmodified, fingerprinted supplemental rrcd hub."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--rnsconfig", required=True)
    args = parser.parse_args()
    verify_reference()
    source = Path(os.environ["DOTRETICULUM_NOMADNET_RRCD_SOURCE"]).resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != "f6d7e9d72bf83c70d7a9373ae8c5edaa052a7bf2":
        raise RuntimeError("Unexpected supplemental rrcd revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Modified supplemental rrcd source")
    sys.path.insert(0, str(source))
    import RNS
    import rrcd
    from rrcd.config import HubRuntimeConfig
    from rrcd.service import HubService
    if Path(rrcd.__file__).resolve().parent != source / "rrcd":
        raise RuntimeError("Unexpected imported rrcd module")
    directory = Path(args.config)
    directory.mkdir(parents=True, exist_ok=True)
    identity_path = directory / "identity"
    if not identity_path.exists():
        RNS.Identity().to_file(str(identity_path))
    hub = HubService(HubRuntimeConfig(configdir=args.rnsconfig, identity_path=str(identity_path),
                                     room_registry_path=str(directory / "rooms.toml"),
                                     greeting="RRC_RESOURCE_MOTD_" + "x" * 2048,
                                     hub_name="NomadNet reference acceptance", announce_period_s=10,
                                     include_joined_member_list=True))
    hub.start()
    print("@@" + json.dumps({"event": "hub-ready", "hash": hub.destination.hash.hex(),
                            "revision": revision, "module": rrcd.__file__, "version": rrcd.__version__}), flush=True)
    for line in sys.stdin:
        command = line.strip()
        if command == "quit":
            hub.stop()
            return
        if command == "announce":
            hub.destination.announce()
            print('@@{"event":"hub-announced"}', flush=True)
        else:
            raise ValueError("Unsupported hub lifecycle command")


if __name__ == "__main__":
    main()
