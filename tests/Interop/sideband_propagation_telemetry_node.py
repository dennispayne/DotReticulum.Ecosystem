"""Drive the actual pinned lxmd daemon/status API. No protocol replacement or behavioral shim."""
import argparse
import os
from pathlib import Path
import threading
import time

import RNS
from RNS.vendor import umsgpack
from LXMF.Utilities import lxmd
from live_lxmf_node import command_lines, emit, write_config
from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, default=0)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    write_config(str(args.config), args.tcp_server_port, args.rnsd_port, False)
    daemon = args.config / "lxmd"
    daemon.mkdir()
    (daemon / "config").write_text("[propagation]\n enable_node = yes\n autopeer = no\n[lxmf]\n display_name = Propagation sensor acceptance\n", encoding="utf-8")
    threading.Thread(target=observe, args=(daemon,), daemon=True).start()
    # Keep real Reticulum startup/signal registration on the interpreter's main thread.
    lxmd.program_setup(configdir=str(daemon), rnsconfigdir=str(args.config), run_pn=True, quietness=4)


def observe(daemon):
    deadline = time.time() + 30
    while not (getattr(lxmd, "message_router", None) is not None
               and lxmd.message_router.propagation_node and lxmd.message_router.control_destination is not None):
        if time.time() > deadline:
            raise RuntimeError("Actual lxmd startup timed out")
        time.sleep(0.05)
    router = lxmd.message_router
    router.control_destination.announce()
    emit({"event": "ready", "dest": lxmd.lxmf_destination.hash.hex(),
          "identity_path": str(daemon / "identity"), "public_key": router.identity.get_public_key().hex(),
          "rns_module": RNS.__file__, "lxmd_module": lxmd.__file__})
    for line in command_lines():
        parts = line.strip().split()
        if not parts:
            continue
        if parts[0] == "allow" and len(parts) == 2:
            router.allow_control(bytes.fromhex(parts[1]))
            emit({"event": "allowed"})
        elif parts[0] == "query" and len(parts) == 2:
            remote = RNS.Identity(create_keys=False)
            remote.load_public_key(bytes.fromhex(parts[1]))
            response = lxmd.query_status(router.identity, remote_identity=remote, timeout=5)
            emit({"event": "query", "response_hex": umsgpack.packb(response).hex()})
        elif parts[0] == "announce":
            router.control_destination.announce()
            emit({"event": "announced"})
        elif parts == ["control-policy", "deny"]:
            # Policy-only state mutation: pinned disallow_control incorrectly calls list.pop(bytes).
            # Existing ALLOW_LIST and stats callbacks remain untouched.
            router.control_allowed_list.clear()
            emit({"event": "control-policy", "allowed": False})
        elif parts == ["control-policy", "own"]:
            router.allow_control(router.identity.hash)
            emit({"event": "control-policy", "allowed": True})
        elif parts[0] == "quit":
            router.exit_handler()
            RNS.Reticulum.exit_handler()
            os._exit(0)
        else:
            raise ValueError("Unsupported propagation telemetry observation")


if __name__ == "__main__":
    main()
