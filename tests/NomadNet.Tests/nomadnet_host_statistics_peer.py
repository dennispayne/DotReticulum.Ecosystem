"""Thin actual-primary app request adapter; not the Urwid Browser.

The complete pinned NomadNetworkApp constructor executes. Requests use its
actual RNS dependency and identity on one real link, without protocol shims.
Synthetic hosted payloads test counters, not image decoding or audio hardware.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import threading
import time
import traceback

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from GenerateNomadNetClientRuntimeVectors import reference, REVISION
from nomadnet_live_node import command_lines, emit


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--rnsconfig", required=True)
    args = parser.parse_args()
    source, imports = reference()
    import nomadnet
    import RNS

    def commands():
        try:
            deadline = time.monotonic() + 45
            while time.monotonic() < deadline:
                try:
                    app = nomadnet.NomadNetworkApp.get_shared_instance()
                except UnboundLocalError:
                    app = None
                if app is not None and getattr(app, "ui", None) is not None:
                    break
                time.sleep(0.05)
            else:
                raise RuntimeError("Actual primary constructor did not complete")
            keys = ("node_connects", "served_page_requests", "served_file_requests", "served_media_requests")
            emit("ready", revision=REVISION, imports=imports, identity=app.identity.hash.hex(),
                 counterPresence={key: key in app.peer_settings for key in keys},
                 counters={key: app.peer_settings.get(key) for key in keys})
            link = None
            destination_hash = None
            for line in command_lines():
                if line.strip() == "quit":
                    if link is not None:
                        link.teardown()
                    app.exit_handler()
                    app.message_router.exit_handler()
                    RNS.Reticulum.exit_handler()
                    os._exit(0)
                command = json.loads(line)
                requested_hash = bytes.fromhex(command["destination"])
                if link is None or link.status != RNS.Link.ACTIVE or requested_hash != destination_hash:
                    remote = RNS.Identity.recall(requested_hash)
                    deadline = time.monotonic() + 25
                    while remote is None and time.monotonic() < deadline:
                        RNS.Transport.request_path(requested_hash)
                        time.sleep(0.25)
                        remote = RNS.Identity.recall(requested_hash)
                    if remote is None:
                        raise RuntimeError("Native hosted destination was not discovered")
                    destination = RNS.Destination(remote, RNS.Destination.OUT, RNS.Destination.SINGLE,
                                                  "nomadnetwork", "node")
                    link = RNS.Link(destination)
                    deadline = time.monotonic() + 20
                    while link.status != RNS.Link.ACTIVE and time.monotonic() < deadline:
                        time.sleep(0.05)
                    if link.status != RNS.Link.ACTIVE:
                        raise RuntimeError("Actual primary/native link did not establish")
                    link.identify(app.identity)
                    destination_hash = requested_hash
                finished = threading.Event()
                observed = {}

                def response(receipt):
                    try:
                        value = receipt.response
                        if hasattr(value, "read"):
                            value = value.read()
                        observed.update(kind="bytes" if isinstance(value, bytes) else type(value).__name__,
                                        size=len(value) if isinstance(value, bytes) else None,
                                        sha256=hashlib.sha256(value).hexdigest() if isinstance(value, bytes) else None,
                                        value=value if not isinstance(value, bytes) else None)
                    finally:
                        finished.set()

                def failed(receipt):
                    observed.update(kind="failed", status=receipt.status)
                    finished.set()

                receipt = link.request(command["path"], data=command.get("data"), response_callback=response,
                                       failed_callback=failed, timeout=3)
                if receipt is None or not finished.wait(12):
                    raise RuntimeError("Actual primary request did not complete or fail within the bound")
                emit("response", path=command["path"], **observed)
        except BaseException as error:
            traceback.print_exc(file=sys.__stderr__)
            emit("error", message=str(error))
            os._exit(1)

    threading.Thread(target=commands, daemon=True).start()
    nomadnet.NomadNetworkApp(configdir=args.config, rnsconfigdir=args.rnsconfig,
                            daemon=True, force_console=False)


if __name__ == "__main__":
    main()
