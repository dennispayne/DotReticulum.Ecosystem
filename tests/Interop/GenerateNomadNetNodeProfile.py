"""Execute actual primary node storage APIs with public synthetic opaque propagation data."""
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
from ObserveNomadNetNodeProfile import PUBLIC_KEY, REVISION


RECEIVED = 2000000000.0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--profile", required=True)
    parser.add_argument("--rnsconfig", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--stamp", help="Previously upstream-generated public stamp hex; omitted for bootstrap only")
    parser.add_argument("--payload-repetitions", type=int, default=2)
    parser.add_argument("--inspect", action="store_true")
    args = parser.parse_args()
    if not 2 <= args.payload_repetitions <= 200:
        raise ValueError("Synthetic payload size outside fixture bounds")
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
    if not args.inspect:
        profile.mkdir(parents=True, exist_ok=False)
        (profile / "config").write_text(
            "[client]\nenable_client=Yes\nannounce_at_start=No\nperiodic_lxmf_sync=No\n"
            "[node]\nenable_node=Yes\ndisable_propagation=No\nannounce_at_start=No\n"
            "[logging]\nloglevel=0\n", encoding="utf-8")
    rns = Path(args.rnsconfig).resolve()
    if not rns.exists():
        rns.mkdir(parents=True, exist_ok=False)
        (rns / "config").write_text(
            "[reticulum]\nshare_instance=No\nenable_transport=No\n[logging]\nloglevel=0\n[interfaces]\n",
            encoding="utf-8")

    def observe():
        try:
            execute(args, source)
        except BaseException:
            import traceback
            traceback.print_exc(file=sys.__stderr__)
            os._exit(1)

    threading.Thread(target=observe, daemon=True).start()
    nomadnet.NomadNetworkApp(configdir=str(profile), rnsconfigdir=str(rns), daemon=True, force_console=True)


def execute(args, source):
    import RNS
    import LXMF
    import LXMF.LXStamper as stamper
    import RNS.vendor.umsgpack as msgpack
    import nomadnet
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        app = nomadnet.NomadNetworkApp._shared_instance
        if app is not None and getattr(app, "ui", None) is not None:
            break
        time.sleep(0.05)
    else:
        raise RuntimeError("Primary node did not become ready")
    router = app.message_router
    if not router.propagation_node:
        raise RuntimeError("Primary propagation node not enabled")
    public = RNS.Identity(create_keys=False)
    public.load_public_key(PUBLIC_KEY)
    peer_hash = RNS.Destination(public, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "propagation").hash
    # Public synthetic opaque transport data, not a decryptable conversation.
    payload = bytes(range(256)) * args.payload_repetitions
    transient = RNS.Identity.full_hash(payload)
    workblock = stamper.stamp_workblock(transient, expand_rounds=stamper.WORKBLOCK_EXPAND_ROUNDS_PN)
    if not args.inspect:
        if args.stamp:
            stamp = bytes.fromhex(args.stamp)
            value = stamper.stamp_value(workblock, stamp)
        else:
            stamp, value = stamper.generate_stamp(transient, 13, expand_rounds=stamper.WORKBLOCK_EXPAND_ROUNDS_PN)
        if not stamper.stamp_valid(stamp, 13, workblock):
            raise RuntimeError("Supplied/generated upstream stamp is not valid at cost13")
        RNS.Identity.remember(bytes(32), peer_hash, PUBLIC_KEY)
        router.peer(peer_hash, RECEIVED, 256, 10240, 16, 3, 18, None)
        if not router.lxmf_propagation(payload, stamp_value=value, stamp_data=stamp):
            raise RuntimeError("Actual primary storage API rejected synthetic opaque payload")
        router.flush_queues()
        entry = router.propagation_entries[transient]
        # Set public historical metadata, then let the actual maintenance API rename nothing.
        # The primary API chose the real filename; moving it only fixes the test timestamp
        # to a public future value so this deterministic fixture does not expire.
        original = Path(entry[1])
        fixed = original.parent / (transient.hex() + "_" + str(RECEIVED) + "_" + str(value))
        original.rename(fixed)
        entry[1] = str(fixed)
        entry[2] = RECEIVED
        peer = router.peers[peer_hash]
        peer.peering_timebase = RECEIVED
        peer.last_heard = RECEIVED
        peer.offered = 23
        peer.outgoing = 17
        peer.add_unhandled_message(transient)
        router.client_propagation_messages_received = 7
        router.client_propagation_messages_served = 8
        router.unpeered_propagation_incoming = 9
        router.unpeered_propagation_rx_bytes = 10
        RNS.Identity.save_known_destinations()
    router.flush_queues()
    result = {
        "commit": REVISION, "rnsVersion": RNS.__version__, "lxmfVersion": LXMF.__version__,
        "method": "Complete unmodified primary node constructor, real peer/lxmf_propagation/flush_queues/exit_handler APIs and actual LXStamper generation/validation. Public RFC key-only synthetic peer and opaque synthetic payload. Only receive metadata/filename fixed to a public future timestamp for reproducible nonexpiring fixture; no reference algorithm/class/clock patch. Not live or decryptable message evidence.",
        "layout": "storage/lxmf", "propagationNode": router.propagation_node,
        "peers": len(router.peers), "messages": len(router.propagation_entries),
        "publicKeyHex": PUBLIC_KEY.hex(), "peerHashHex": peer_hash.hex(),
        "transientIdHex": transient.hex(), "sources": {},
    }
    router.exit_handler()
    path = Path(router.storagepath)
    peers = msgpack.unpackb((path / "peers").read_bytes())
    result["peersHex"] = (path / "peers").read_bytes().hex()
    result["statsHex"] = (path / "node_stats").read_bytes().hex()
    result["peerUnhandled"] = len(msgpack.unpackb(peers[0])["unhandled_ids"]) if peers else 0
    files = sorted(Path(router.messagepath).iterdir())
    result["storeFiles"] = [{"name": file.name, "hex": file.read_bytes().hex()} for file in files]
    if files:
        data = files[0].read_bytes()
        result["stampHex"] = data[-32:].hex()
        result["stampValue"] = stamper.stamp_value(workblock, data[-32:])
    for label, file in (
        ("nomadnet/NomadNetworkApp.py", source / "nomadnet" / "NomadNetworkApp.py"),
        ("LXMF/LXMRouter.py", Path(importlib.import_module("LXMF.LXMRouter").__file__)),
        ("LXMF/LXMPeer.py", Path(importlib.import_module("LXMF.LXMPeer").__file__)),
        ("LXMF/LXStamper.py", Path(stamper.__file__)),
    ):
        canonical = subprocess.check_output(["git", "-C", str(file.parent.parent), "show", "HEAD:" + label])
        if file.read_bytes().replace(b"\r\n", b"\n") != canonical.replace(b"\r\n", b"\n"):
            raise RuntimeError("Imported source differs from pinned Git blob: " + label)
        result["sources"][label] = hashlib.sha256(canonical).hexdigest()
    Path(args.output).write_bytes((json.dumps(result, indent=2) + "\n").encode("utf-8"))
    print(json.dumps({"profile": str(Path(args.profile).resolve()), "peers": len(router.peers),
                      "messages": len(router.propagation_entries)}), flush=True)
    os._exit(0)


if __name__ == "__main__":
    main()
