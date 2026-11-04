"""Observe primary propagation peer persistence; no reference algorithm is replaced."""
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
# Published RFC 7748 and RFC 8032 public keys, not a private identity.
PUBLIC_KEY = bytes.fromhex(
    "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f"
    "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a")


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
            "[reticulum]\nshare_instance=No\nenable_transport=No\n"
            "[logging]\nloglevel=0\n[interfaces]\n", encoding="utf-8")

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
    import RNS.vendor.umsgpack as msgpack
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        app = nomadnet.NomadNetworkApp._shared_instance
        if app is not None and getattr(app, "ui", None) is not None:
            break
        time.sleep(0.05)
    else:
        raise RuntimeError("Primary constructor did not become ready")
    router = app.message_router
    if not router.propagation_node:
        raise RuntimeError("Actual primary propagation node is not enabled")
    public = RNS.Identity(create_keys=False)
    public.load_public_key(PUBLIC_KEY)
    destination = RNS.Destination(public, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "propagation")
    if not args.inspect:
        RNS.Identity.remember(bytes(32), destination.hash, PUBLIC_KEY)
        router.peer(destination.hash, 2000000000, 256, 10240, 16, 3, 18, None)
        peer = router.peers[destination.hash]
        peer.peering_timebase = 2000000000
        peer.last_heard = 2000000000
        peer.offered = 23
        peer.outgoing = 17
        router.client_propagation_messages_received = 7
        router.client_propagation_messages_served = 8
        router.unpeered_propagation_incoming = 9
        router.unpeered_propagation_rx_bytes = 10
        RNS.Identity.save_known_destinations()
    result = {
        "commit": REVISION,
        "rnsVersion": RNS.__version__,
        "lxmfVersion": LXMF.__version__,
        "method": "Complete unmodified NomadNetworkApp constructor with propagation enabled. Seed only public peer/cache attributes through real peer/save/exit APIs. A published public-key-only synthetic peer is not live traffic. No private identity or peering key is exported.",
        "layout": "storage/lxmf",
        "propagationNode": router.propagation_node,
        "peerIdentityRecalled": RNS.Identity.recall(destination.hash) is not None,
        "peersOnLoad": len(router.peers),
        "offered": router.peers[destination.hash].offered if destination.hash in router.peers else None,
        "publicKeyHex": PUBLIC_KEY.hex(),
        "peerHashHex": destination.hash.hex(),
        "sources": {},
    }
    router.exit_handler()
    path = Path(router.storagepath)
    serialised = msgpack.unpackb((path / "peers").read_bytes())
    result["peersAfterSave"] = len(serialised)
    result["peerFields"] = sorted(msgpack.unpackb(serialised[0])) if serialised else []
    result["peerHex"] = serialised[0].hex() if serialised else None
    result["statsHex"] = (path / "node_stats").read_bytes().hex()
    result["nodeStats"] = msgpack.unpackb((path / "node_stats").read_bytes())
    for label, file in (
        ("nomadnet/NomadNetworkApp.py", source / "nomadnet" / "NomadNetworkApp.py"),
        ("LXMF/LXMRouter.py", Path(importlib.import_module("LXMF.LXMRouter").__file__)),
        ("LXMF/LXMPeer.py", Path(importlib.import_module("LXMF.LXMPeer").__file__)),
        ("RNS/Identity.py", Path(importlib.import_module("RNS.Identity").__file__)),
    ):
        canonical = subprocess.check_output(["git", "-C", str(file.parent.parent), "show", "HEAD:" + label])
        if file.read_bytes().replace(b"\r\n", b"\n") != canonical.replace(b"\r\n", b"\n"):
            raise RuntimeError("Imported source differs from pinned Git blob: " + label)
        result["sources"][label] = hashlib.sha256(canonical).hexdigest()
    Path(args.output).write_bytes((json.dumps(result, indent=2) + "\n").encode("utf-8"))
    print(json.dumps({"profile": str(profile), "peers": len(router.peers)}), flush=True)
    os._exit(0)


if __name__ == "__main__":
    main()
