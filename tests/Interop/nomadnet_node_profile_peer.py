"""Thin full-primary propagation-node process adapter, with no protocol replacements."""
import argparse
import importlib
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
import traceback

from reference_environment import verify_reference
from ObserveNomadNetNodeProfile import REVISION
from nomadnet_live_node import command_lines, emit


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--rnsconfig", required=True)
    parser.add_argument("--client-only", action="store_true")
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    source = Path(os.environ["DOTRETICULUM_NOMADNET_SOURCE"]).resolve(strict=True)
    if subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip() != REVISION:
        raise RuntimeError("Unexpected primary revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Modified primary source")
    sys.path.insert(0, str(source))
    import nomadnet
    import RNS
    module = importlib.import_module("nomadnet.NomadNetworkApp")
    if Path(module.__file__).resolve() != source / "nomadnet" / "NomadNetworkApp.py":
        raise RuntimeError("Unexpected primary module")

    def commands():
        try:
            deadline = time.monotonic() + 60
            while time.monotonic() < deadline:
                app = nomadnet.NomadNetworkApp._shared_instance
                if app is not None and getattr(app, "ui", None) is not None:
                    break
                time.sleep(0.05)
            else:
                raise RuntimeError("Primary node constructor did not complete")
            router = app.message_router
            if not router.propagation_node and not args.client_only:
                raise RuntimeError("Primary propagation node not enabled")
            if router.propagation_node and args.client_only:
                raise RuntimeError("Primary initialization client unexpectedly enabled propagation")
            emit("ready", revision=REVISION, identity=app.identity.hash.hex(),
                 delivery=app.lxmf_destination.hash.hex(),
                 propagation=router.propagation_destination.hash.hex() if router.propagation_node else None)
            for line in command_lines():
                parts = line.strip().split(" ", 1)
                if parts[0] == "quit":
                    app.exit_handler()
                    router.exit_handler()
                    RNS.Reticulum.exit_handler()
                    os._exit(0)
                elif args.client_only:
                    raise ValueError("Initialization-only primary client supports quit only")
                elif parts[0] == "announce":
                    app.announce_now()
                    router.announce_propagation_node()
                    emit("announced")
                elif parts[0] == "stats":
                    router.flush_queues()
                    emit("stats", messages=len(router.propagation_entries),
                         received=router.client_propagation_messages_received,
                         served=router.client_propagation_messages_served,
                         peers=[{"hash": key.hex(), "offered": peer.offered, "outgoing": peer.outgoing,
                                 "incoming": peer.incoming, "handled": len(peer.handled_messages),
                                 "unhandled": len(peer.unhandled_messages),
                                 "key_ready": peer.peering_key_ready(),
                                 "stamp_cost": peer.propagation_stamp_cost}
                                for key, peer in router.peers.copy().items()])
                elif parts[0] == "request-path" and len(parts) == 2:
                    router.request_path(bytes.fromhex(parts[1]))
                    emit("path-requested")
                elif parts[0] == "peer-sync" and len(parts) == 2:
                    peer = router.peers[bytes.fromhex(parts[1])]
                    def synchronize(current=peer):
                        try:
                            current.sync()
                            emit("peer-sync-returned")
                        except Exception as error:
                            emit("error", context="actual-primary-peer-sync", message=str(error))
                    threading.Thread(target=synchronize, daemon=True).start()
                    emit("peer-sync-requested")
                else:
                    raise ValueError("Unsupported bounded node observation command")
        except BaseException as error:
            traceback.print_exc(file=sys.__stderr__)
            emit("error", message=str(error))
            os._exit(1)

    threading.Thread(target=commands, daemon=True).start()
    nomadnet.NomadNetworkApp(configdir=args.config, rnsconfigdir=args.rnsconfig, daemon=True, force_console=True)


if __name__ == "__main__":
    main()
