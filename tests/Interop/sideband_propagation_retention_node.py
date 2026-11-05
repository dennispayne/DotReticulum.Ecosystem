"""Actual pinned lxmd with public link observations and explicit authorization policy changes."""
import argparse
import hashlib
import os
from pathlib import Path
import threading
import time

import LXMF
import RNS
from LXMF.Utilities import lxmd
from live_lxmf_node import command_lines, emit, write_config
from reference_environment import verify_reference


def observe():
    deadline = time.monotonic() + 30
    while not (getattr(lxmd, "message_router", None) is not None
               and lxmd.message_router.propagation_node
               and lxmd.message_router.control_destination is not None):
        if time.monotonic() > deadline:
            raise RuntimeError("Actual lxmd startup timed out")
        time.sleep(0.05)
    router = lxmd.message_router
    destination = router.control_destination
    if destination.callbacks.link_established is not None:
        raise RuntimeError("Reference already owns the control link observation callback")

    def established(link):
        emit({"event": "control-open", "monotonic": time.monotonic(),
              "link": link.link_id.hex(), "allowed": len(router.control_allowed_list)})
        if link.callbacks.link_closed is not None:
            raise RuntimeError("Reference already owns the control close observation callback")
        link.set_link_closed_callback(lambda closed: emit({
            "event": "control-close", "monotonic": time.monotonic(),
            "link": closed.link_id.hex()}))

    destination.set_link_established_callback(established)
    destination.announce()
    lxmd.lxmf_destination.announce()
    emit({"event": "ready", "monotonic": time.monotonic(), "pid": os.getpid(),
          "delivery": lxmd.lxmf_destination.hash.hex(),
          "identity_path": str(Path(lxmd.identitypath).resolve()),
          "identity_hash": router.identity.hash.hex(),
          "rns": RNS.__file__, "rns_version": RNS.__version__,
          "lxmf": LXMF.__file__, "lxmf_version": LXMF.__version__,
          "lxmd": lxmd.__file__,
          "lxmd_sha256": hashlib.sha256(Path(lxmd.__file__).read_bytes()).hexdigest(),
          "policy": "Unmodified ALLOW_LIST and stats callbacks; link callbacks observe only."})
    for raw in command_lines():
        line = raw.strip()
        if line == "control-policy deny":
            # The pinned disallow_control helper incorrectly calls list.pop(bytes).
            router.control_allowed_list.clear()
            emit({"event": "policy", "monotonic": time.monotonic(), "allowed": False})
        elif line == "control-policy own":
            router.allow_control(router.identity.hash)
            emit({"event": "policy", "monotonic": time.monotonic(), "allowed": True})
        elif line == "inbox":
            messages = []
            for path in sorted(Path(lxmd.lxmdir).glob("*")):
                if path.is_file() and len(path.name) == 64:
                    with path.open("rb") as stream:
                        message = LXMF.LXMessage.unpack_from_file(stream)
                    if message is None:
                        raise RuntimeError("Actual LXMF could not read its persisted inbox")
                    messages.append({"hash": message.hash.hex(), "size": len(message.content),
                                     "sha256": hashlib.sha256(message.content).hexdigest(),
                                     "signature_validated": message.signature_validated})
            emit({"event": "inbox", "monotonic": time.monotonic(), "pid": os.getpid(), "messages": messages})
        elif line == "quit":
            router.exit_handler()
            RNS.Reticulum.exit_handler()
            os._exit(0)
        else:
            raise ValueError("Unsupported retention observation command")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--port", type=int, required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    write_config(str(args.config), args.port, 0, False)
    daemon = args.config / "lxmd"
    daemon.mkdir()
    (daemon / "config").write_text(
        "[propagation]\n enable_node = yes\n autopeer = no\n"
        "[lxmf]\n display_name = Synthetic C105 retention peer\n", encoding="utf-8")
    threading.Thread(target=observe, daemon=True).start()
    lxmd.program_setup(configdir=str(daemon), rnsconfigdir=str(args.config),
                       run_pn=True, quietness=4)


if __name__ == "__main__":
    main()
