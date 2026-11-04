"""Thin actual primary application announce and directory-observation adapter."""
import argparse
import os
from pathlib import Path
import threading
import time

from GenerateNomadNetClientRuntimeVectors import reference, REVISION
from nomadnet_live_node import command_lines, emit


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--rnsconfig", required=True)
    args = parser.parse_args()
    source, imports = reference()
    from nomadnet.NomadNetworkApp import NomadNetworkApp
    import RNS

    def commands():
        try:
            deadline = time.monotonic() + 45
            while time.monotonic() < deadline:
                app = NomadNetworkApp._shared_instance
                if app is not None and getattr(app, "node", None) is not None:
                    break
                time.sleep(0.05)
            if app is None or getattr(app, "node", None) is None:
                raise RuntimeError("Actual primary node failed to initialize")
            emit("ready", revision=REVISION, imports=imports, node=app.node.destination.hash.hex(),
                 delivery=app.lxmf_destination.hash.hex(), compact=app.compact_stream)
            for line in command_lines():
                command = line.strip()
                if command == "wait-network":
                    deadline = time.monotonic() + 30
                    while time.monotonic() < deadline:
                        clients = [i for i in RNS.Transport.interfaces if type(i).__name__ == "TCPClientInterface"]
                        if clients and all(i.online for i in clients):
                            emit("network-ready")
                            break
                        time.sleep(0.05)
                    else:
                        raise RuntimeError("Primary TCP client did not connect")
                elif command == "announce":
                    app.announce_now()
                    # Keep independent originals outside the reference interface's immediate announce queue.
                    time.sleep(0.25)
                    app.node.announce()
                    emit("announced", nodeTime=app.node.last_announce, clientTime=app.peer_settings["last_announce"])
                elif command == "stream":
                    emit("stream", rows=[{"hash": e[1].hex(), "kind": e[3], "data": e[2].hex()}
                                          for e in app.directory.announce_stream])
                elif command == "quit":
                    app.exit_handler()
                    app.message_router.exit_handler()
                    RNS.Reticulum.exit_handler()
                    os._exit(0)
                else:
                    raise ValueError("Expected wait-network, announce, stream, quit")
        except Exception as error:
            emit("error", detail=str(error))
            os._exit(1)

    threading.Thread(target=commands, daemon=True).start()
    NomadNetworkApp(configdir=args.config, rnsconfigdir=args.rnsconfig, daemon=True, force_console=True)


if __name__ == "__main__":
    main()
