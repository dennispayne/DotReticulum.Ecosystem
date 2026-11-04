"""Observe actual primary client-router persistence; synthetic public cache values only."""
import argparse
import hashlib
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time

from reference_environment import verify_reference


REVISION = "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2"
PEER = bytes.fromhex("11" * 16)
TICKET = bytes.fromhex("22" * 16)
DELIVERED = bytes.fromhex("33" * 32)
PROCESSED = bytes.fromhex("44" * 32)
SYNTHETIC_TIME = 2000000000.0
EXPIRY = 2100000000.0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--profile", required=True)
    parser.add_argument("--rnsconfig", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--inspect", action="store_true")
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    source = Path(os.environ["DOTRETICULUM_NOMADNET_SOURCE"]).resolve(strict=True)
    if subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip() != REVISION:
        raise RuntimeError("Unexpected primary revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain", "--untracked-files=no"], text=True).strip():
        raise RuntimeError("Modified primary source")
    sys.path.insert(0, str(source))
    import nomadnet
    app_module = importlib.import_module("nomadnet.NomadNetworkApp")
    if Path(app_module.__file__).resolve() != source / "nomadnet" / "NomadNetworkApp.py":
        raise RuntimeError("Unexpected primary module")
    profile = Path(args.profile).resolve()
    profile.mkdir(parents=True, exist_ok=args.inspect)
    rns = Path(args.rnsconfig).resolve()
    rns.mkdir(parents=True, exist_ok=False)
    (rns / "config").write_text(
        "[reticulum]\nshare_instance=No\nenable_transport=No\n[logging]\nloglevel=0\n[interfaces]\n",
        encoding="utf-8")
    if not args.inspect:
        (profile / "config").write_text(
            "[client]\nenable_client=Yes\nannounce_at_start=No\nperiodic_lxmf_sync=No\n"
            "[node]\nenable_node=No\n[logging]\nloglevel=0\n", encoding="utf-8")

    def observe():
        try:
            execute(args, source, profile)
        except BaseException:
            import traceback
            traceback.print_exc(file=sys.__stderr__)
            os._exit(1)

    threading.Thread(target=observe, daemon=True).start()
    nomadnet.NomadNetworkApp(configdir=str(profile), rnsconfigdir=str(rns), daemon=True, force_console=True)


def execute(args, source, profile):
    import RNS
    import LXMF
    import nomadnet
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        app = nomadnet.NomadNetworkApp._shared_instance
        if app is not None and getattr(app, "ui", None) is not None:
            break
        time.sleep(0.05)
    else:
        raise RuntimeError("Actual primary constructor did not become ready")
    router = app.message_router
    if not args.inspect:
        app.set_display_name("Router migration synthetic user")
        # Public mutable cache attributes are seeded with explicit, non-secret test data.
        # No persistence codec or protocol algorithm is reimplemented here.
        router.remember_ticket(PEER, [EXPIRY, TICKET])
        router.available_tickets["inbound"][PEER] = {TICKET: [EXPIRY]}
        router.available_tickets["last_deliveries"][PEER] = SYNTHETIC_TIME
        router.outbound_stamp_costs[PEER] = [SYNTHETIC_TIME, 9]
        router.locally_delivered_transient_ids[DELIVERED] = SYNTHETIC_TIME
        router.locally_processed_transient_ids[PROCESSED] = SYNTHETIC_TIME
        app.lxmf_destination.rotate_ratchets()
    router.save_available_tickets()
    router.save_outbound_stamp_costs()
    router.save_locally_delivered_transient_ids()
    router.save_locally_processed_transient_ids()
    router.save_node_stats()
    paths = ("available_tickets", "outbound_stamp_costs", "local_deliveries", "locally_processed")
    import RNS.vendor.umsgpack as msgpack
    ratchet_path = Path(router.ratchetpath) / (app.lxmf_destination.hash.hex() + ".ratchets")
    persisted_ratchets = msgpack.unpackb(ratchet_path.read_bytes())
    ratchet_valid = app.identity.validate(persisted_ratchets["signature"], persisted_ratchets["ratchets"])
    result = {
        "commit": REVISION,
        "rnsVersion": RNS.__version__,
        "lxmfVersion": LXMF.__version__,
        "method": "Complete unmodified primary NomadNetworkApp constructor; seed public router attributes with explicitly public synthetic cache values, execute real save methods and Destination.rotate_ratchets. No clocks, classes or algorithms patched. Future timestamps deliberately avoid fixture expiry, not a real traffic observation. Random identity/private ratchets remain only in ignored runtime profile; never in this fixture.",
        "sources": {},
        "layout": "storage/lxmf",
        "clientFiles": list(paths) + ["ratchets/<own-delivery-hash>.ratchets"],
        "nodeStatsWrittenByClient": (Path(router.storagepath) / "node_stats").exists(),
        "propagationNode": router.propagation_node,
        "pendingInbound": len(router.pending_inbound),
        "pendingOutbound": len(router.pending_outbound),
        "ownRatchetsSignatureValid": ratchet_valid,
        "ownRatchetsCount": len(msgpack.unpackb(persisted_ratchets["ratchets"])),
        "outboundTicket": router.get_outbound_ticket(PEER).hex(),
        "inboundTickets": [ticket.hex() for ticket in router.get_inbound_tickets(PEER)],
        "stampCost": router.outbound_stamp_costs[PEER][1],
        "deliveredRetained": DELIVERED in router.locally_delivered_transient_ids,
        "processedRetained": PROCESSED in router.locally_processed_transient_ids,
        "publicFilesHex": {name: (Path(router.storagepath) / name).read_bytes().hex() for name in paths},
    }
    files = (
        ("nomadnet/NomadNetworkApp.py", source / "nomadnet" / "NomadNetworkApp.py"),
        ("LXMF/LXMRouter.py", Path(importlib.import_module("LXMF.LXMRouter").__file__)),
        ("RNS/Destination.py", Path(importlib.import_module("RNS.Destination").__file__)),
    )
    for label, path in files:
        canonical = subprocess.check_output(["git", "-C", str(path.parent.parent), "show", "HEAD:" + label])
        if path.read_bytes().replace(b"\r\n", b"\n") != canonical.replace(b"\r\n", b"\n"):
            raise RuntimeError("Imported source differs from pinned Git blob: " + label)
        result["sources"][label] = hashlib.sha256(canonical).hexdigest()
    Path(args.output).write_bytes((json.dumps(result, indent=2) + "\n").encode("utf-8"))
    print(json.dumps({"profile": str(profile), "delivery": app.lxmf_destination.hash.hex()}), flush=True)
    os._exit(0)


if __name__ == "__main__":
    main()
