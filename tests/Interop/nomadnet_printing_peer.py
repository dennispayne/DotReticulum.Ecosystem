"""Thin actual primary application messaging and independent QR observation."""
import argparse
import os
from pathlib import Path
import threading
import time
import traceback

from GenerateNomadNetClientRuntimeVectors import reference, REVISION
from nomadnet_live_node import command_lines, emit


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--rnsconfig", required=True)
    args = parser.parse_args()
    source, imports = reference()
    import RNS
    import LXMF
    from nomadnet.NomadNetworkApp import NomadNetworkApp

    def commands():
        try:
            deadline = time.monotonic() + 60
            while time.monotonic() < deadline:
                app = NomadNetworkApp._shared_instance
                if app is not None and hasattr(app, "ui"):
                    break
                time.sleep(0.025)
            else:
                raise RuntimeError("Actual primary constructor did not complete")
            if app.print_messages:
                raise RuntimeError("Reference printer must remain disabled")

            def receive(message):
                app.lxmf_delivery(message)
                emit("received", content=message.content_as_string(), hash=message.hash.hex(),
                     signature=message.signature_validated, method=message.method)

            app.message_router.register_delivery_callback(receive)
            emit("ready", revision=REVISION, imports=imports, delivery=app.lxmf_destination.hash.hex())
            for line in command_lines():
                parts = line.strip().split(" ", 2)
                if parts[0] == "quit":
                    app.exit_handler()
                    app.message_router.exit_handler()
                    RNS.Reticulum.exit_handler()
                    os._exit(0)
                elif parts[0] == "announce":
                    app.lxmf_destination.announce()
                    emit("announced")
                elif parts[0] == "send" and len(parts) == 3:
                    destination_hash = bytes.fromhex(parts[1])
                    deadline = time.monotonic() + 30
                    if not RNS.Transport.has_path(destination_hash):
                        RNS.Transport.request_path(destination_hash)
                    while time.monotonic() < deadline and (
                            not RNS.Transport.has_path(destination_hash) or RNS.Identity.recall(destination_hash) is None):
                        time.sleep(0.025)
                    identity = RNS.Identity.recall(destination_hash)
                    if identity is None:
                        raise RuntimeError("Native identity was not recalled")
                    destination = RNS.Destination(identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
                    message = LXMF.LXMessage(destination, app.lxmf_destination, parts[2], "Public primary title",
                                             desired_method=LXMF.LXMessage.DIRECT)
                    message.register_delivery_callback(lambda current: emit("delivered", hash=current.hash.hex()))
                    message.register_failed_callback(lambda current: emit("error", context="delivery", state=current.state))
                    app.message_router.handle_outbound(message)
                    emit("queued", hash=message.hash.hex())
                elif parts[0] == "decode-paper" and len(parts) >= 2:
                    import zxingcpp
                    from PIL import Image
                    # Preserve spaces in the explicitly selected public capture path.
                    path = line.strip()[len("decode-paper "):]
                    with Image.open(path) as image:
                        decoded = zxingcpp.read_barcode(image)
                    if decoded is None or not decoded.text.startswith("lxm://"):
                        raise RuntimeError("Independent zxing-cpp did not decode native printed QR")
                    accepted = app.message_router.ingest_lxm_uri(decoded.text)
                    emit("decoded", accepted=accepted, scheme=decoded.text[:6])
                else:
                    raise ValueError("Unsupported bounded printing observation command")
        except BaseException as error:
            traceback.print_exc()
            emit("error", message=str(error))
            os._exit(1)

    threading.Thread(target=commands, daemon=True).start()
    NomadNetworkApp(configdir=args.config, rnsconfigdir=args.rnsconfig, daemon=True, force_console=True)


if __name__ == "__main__":
    main()
