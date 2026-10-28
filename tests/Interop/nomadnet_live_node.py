"""Thin command adapter around the pinned upstream NomadNetworkApp."""
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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--rnsconfig", required=True)
    args = parser.parse_args()

    verify_reference(require_lxmf=True)
    import nomadnet
    from nomadnet.NomadNetworkApp import NomadNetworkApp

    expected_source = os.environ.get("DOTRETICULUM_NOMADNET_SOURCE")
    imported_source = os.path.realpath(os.path.dirname(nomadnet.__file__))
    if expected_source and os.path.realpath(expected_source) != os.path.realpath(os.path.dirname(imported_source)):
        raise RuntimeError(f"NomadNet imported from unexpected source path: {nomadnet.__file__}")
    from nomadnet._version import __version__ as version
    if version != "1.4.4":
        raise RuntimeError(f"NomadNet version {version} does not match the pinned primary source")

    def command_loop():
        try:
            deadline = time.monotonic() + 60

            def current_app():
                try:
                    return NomadNetworkApp.get_shared_instance()
                except UnboundLocalError:
                    return None

            app = current_app()
            while (app is None or not hasattr(app, "node")) and time.monotonic() < deadline:
                time.sleep(0.05)
                app = current_app()
            if app is None or not hasattr(app, "node") or app.node is None:
                raise RuntimeError("The upstream NomadNet app did not initialize its configured node")

            emit(
                "ready",
                version=version,
                module=nomadnet.__file__,
                node=app.node.destination.hash.hex(),
                delivery=app.lxmf_destination.hash.hex(),
            )

            for raw_line in sys.stdin:
                if len(raw_line) > 65536:
                    raise ValueError("adapter command exceeds 64 KiB")
                parts = raw_line.rstrip("\r\n").split(" ", 2)
                if not parts or not parts[0]:
                    continue
                if parts[0] == "quit":
                    app.exit_handler()
                    os._exit(0)
                if parts[0] == "stats":
                    emit(
                        "stats",
                        served_pages=app.peer_settings["served_page_requests"],
                        served_files=app.peer_settings["served_file_requests"],
                        page_directory=app.pagespath,
                        registered_pages=app.node.servedpages,
                    )
                    continue
                if parts[0] != "send" or len(parts) != 3:
                    raise ValueError("expected send <32-hex-destination> <text>, stats, or quit")

                destination_hash = bytes.fromhex(parts[1])
                if len(destination_hash) != RNS.Reticulum.TRUNCATED_HASHLENGTH // 8:
                    raise ValueError("destination hash must be 32 hexadecimal characters")
                remote_identity = RNS.Identity.recall(destination_hash)
                if remote_identity is None:
                    RNS.Transport.request_path(destination_hash)
                    deadline = time.monotonic() + 30
                    while remote_identity is None and time.monotonic() < deadline:
                        time.sleep(0.2)
                        remote_identity = RNS.Identity.recall(destination_hash)
                if remote_identity is None:
                    emit("failed", reason="No announced identity for destination")
                    continue

                destination = RNS.Destination(
                    remote_identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery"
                )
                message = LXMF.LXMessage(
                    destination,
                    app.lxmf_destination,
                    parts[2],
                    "",
                    desired_method=LXMF.LXMessage.DIRECT,
                )
                message.register_delivery_callback(
                    lambda current: emit("delivered", message_hash=current.hash.hex())
                )
                message.register_failed_callback(
                    lambda current: emit("failed", message_hash=current.hash.hex(), state=current.state)
                )
                app.message_router.handle_outbound(message)
                emit("queued", message_hash=message.hash.hex())
        except Exception as error:
            emit("error", message=str(error))

    threading.Thread(target=command_loop, daemon=True).start()
    NomadNetworkApp(configdir=args.config, rnsconfigdir=args.rnsconfig, daemon=True)


if __name__ == "__main__":
    main()
