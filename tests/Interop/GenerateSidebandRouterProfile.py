"""Execute actual pinned router persistence and ratchet APIs, with no reference-side shims.

Generated profiles contain ephemeral identities, tickets and ratchet keys: output
only into ignored artifacts. Public fixtures exclude all private keys and ticket tokens.
Sideband's core.py selects app_storage as the LXMRouter storage root (lines 251/4770).
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sideband", required=True, type=Path)
    parser.add_argument("--rns", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--identity-file", type=Path,
                        help="Load an existing primary_identity for generation; copy it only into the new output profile")
    parser.add_argument("--public-fixture", type=Path)
    parser.add_argument("--verify-native", type=Path)
    parser.add_argument("--profile", type=Path)
    args = parser.parse_args()
    if bool(args.output) == bool(args.verify_native) or args.verify_native and not args.profile:
        parser.error("Specify --output or --verify-native plus --profile")
    if args.identity_file and not args.output:
        parser.error("--identity-file requires --output")
    if args.identity_file and (not args.identity_file.is_file() or args.identity_file.stat().st_size != 64):
        parser.error("--identity-file must be an existing 64-byte RNS private identity")
    sideband = args.sideband.resolve()
    revision = subprocess.check_output(["git", "-C", str(sideband), "rev-parse", "HEAD"], text=True).strip()
    if revision != "d069122af14880e43607aed0b71add4c7806fbe0":
        raise RuntimeError("Unexpected primary Sideband revision")
    core_sha = hashlib.sha256((sideband / "sbapp" / "sideband" / "core.py").read_bytes()).hexdigest()
    if core_sha != "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25":
        raise RuntimeError("Unexpected Sideband core fingerprint")
    sys.path.insert(0, str(args.rns.resolve()))
    import RNS
    import LXMF
    from reference_environment import verify_reference, PINNED_RNS_SHA, PINNED_LXMF_SHA
    os.environ["DOTRETICULUM_UPSTREAM"] = str(args.rns.resolve())
    verify_reference(require_lxmf=True)
    root = args.output.resolve() if args.output else args.verify_native.resolve()
    if args.output:
        root.mkdir(parents=True, exist_ok=False)
    rns_config = root / "reference-rns"
    rns_config.mkdir(mode=0o700, exist_ok=True)
    (rns_config / "config").write_text(
        "[reticulum]\n  enable_transport = No\n  share_instance = No\n[logging]\n  loglevel = 0\n[interfaces]\n",
        encoding="utf-8")
    RNS.Reticulum(configdir=str(rns_config), loglevel=0)
    if args.verify_native:
        metadata = json.loads((args.profile / "observations.json").read_text(encoding="utf-8"))
        identity = RNS.Identity.from_file(str(args.profile / "app_storage" / "primary_identity"))
        router = LXMF.LXMRouter(identity=identity, storagepath=str(root), autopeer=False)
        destination = router.register_delivery_identity(identity, display_name="Native cross-load")
        peer = bytes.fromhex(metadata["peer"])
        if hashlib.sha256(router.get_outbound_ticket(peer)).hexdigest() != metadata["outboundTicketSha256"]:
            raise RuntimeError("Native outbound ticket cross-load failed")
        if hashlib.sha256(router.get_inbound_tickets(peer)[0]).hexdigest() != metadata["inboundTicketSha256"]:
            raise RuntimeError("Native inbound ticket cross-load failed")
        if router.outbound_stamp_costs[peer][1] != metadata["stampCost"]:
            raise RuntimeError("Native stamp cost cross-load failed")
        ciphertext = bytes.fromhex(metadata["ratchetCiphertext"])
        if destination.decrypt(ciphertext).decode() != metadata["ratchetPlaintext"]:
            raise RuntimeError("Native signed ratchet cross-load failed")
        if bytes.fromhex(metadata["messageHash"]) not in router.locally_delivered_transient_ids:
            raise RuntimeError("Native duplicate history cross-load failed")
        if bytes.fromhex(metadata["transientId"]) not in router.locally_processed_transient_ids:
            raise RuntimeError("Native processed history cross-load failed")
        print(json.dumps({"crossLoad": True, "delivery": destination.hash.hex()}))
        router.exit_handler()
        return
    storage = root / "app_storage"
    storage.mkdir(mode=0o700)
    identity = RNS.Identity.from_file(str(args.identity_file.resolve())) if args.identity_file else RNS.Identity()
    if identity is None:
        raise RuntimeError("Could not load the supplied primary identity")
    identity.to_file(str(storage / "primary_identity"))
    os.chmod(storage / "primary_identity", 0o600)
    peer_identity = RNS.Identity()
    router = LXMF.LXMRouter(identity=identity, storagepath=str(storage), autopeer=False)
    destination = router.register_delivery_identity(identity, display_name="Profile persistence")
    peer_destination = RNS.Destination(peer_identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    peer = peer_destination.hash
    router.save_available_tickets()
    empty_tickets = (storage / "lxmf" / "available_tickets").read_bytes()
    inbound = router.generate_ticket(peer)
    outbound = [time.time() + LXMF.LXMessage.TICKET_EXPIRY, os.urandom(LXMF.LXMessage.TICKET_LENGTH)]
    router.remember_ticket(peer, outbound)
    router.save_available_tickets()
    router.update_stamp_cost(peer, 5)
    router.save_outbound_stamp_costs()
    destination.rotate_ratchets()
    plaintext = "Synthetic migrated ratchet"
    ciphertext = identity.encrypt(plaintext.encode(), ratchet=RNS.Identity._ratchet_public_bytes(destination.ratchets[0]))
    paper = LXMF.LXMessage(destination, peer_destination, "Synthetic router persistence",
                           desired_method=LXMF.LXMessage.PAPER)
    paper.pack()
    uri = paper.as_uri()
    delivered = router.ingest_lxm_uri(uri, signal_local_delivery="delivered", signal_duplicate="duplicate")
    if delivered != "delivered":
        raise RuntimeError("Actual paper ingestion did not deliver")
    duplicate = router.ingest_lxm_uri(uri, signal_local_delivery="delivered", signal_duplicate="duplicate")
    if duplicate != "duplicate":
        raise RuntimeError("Actual duplicate observation failed")
    router.save_locally_delivered_transient_ids()
    router.save_locally_processed_transient_ids()
    router.save_node_stats()
    timestamp = time.time()
    metadata = {
        "sideband": revision, "coreSha256": core_sha, "rns": PINNED_RNS_SHA, "lxmf": PINNED_LXMF_SHA,
        "routerSha256": hashlib.sha256(Path(sys.modules["LXMF.LXMRouter"].__file__).read_bytes()).hexdigest(),
        "destinationSha256": hashlib.sha256(Path(sys.modules["RNS.Destination"].__file__).read_bytes()).hexdigest(),
        "rnsModule": str(Path(RNS.__file__).resolve()), "lxmfModule": str(Path(LXMF.__file__).resolve()),
        "delivery": destination.hash.hex(), "peer": peer.hex(), "timestamp": timestamp,
        "inboundTicketSha256": hashlib.sha256(inbound[1]).hexdigest(),
        "outboundTicketSha256": hashlib.sha256(outbound[1]).hexdigest(),
        "stampCost": router.outbound_stamp_costs[peer][1], "ratchetCount": len(destination.ratchets),
        "ratchetCiphertext": ciphertext.hex(), "ratchetPlaintext": plaintext,
        "uri": uri, "messageHash": paper.hash.hex(),
        "transientId": RNS.Identity.full_hash(paper.paper_packed).hex(),
        "duplicateResult": duplicate,
        "methods": "Actual LXMRouter constructor, ticket APIs, update_stamp_cost, ingest_lxm_uri, persistence writers and Destination.rotate_ratchets",
        "shims": [],
    }
    (root / "observations.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    if args.public_fixture:
        public = {key: metadata[key] for key in ["sideband", "coreSha256", "rns", "lxmf", "routerSha256", "destinationSha256", "timestamp", "methods", "shims"]}
        public["emptyTickets"] = base64.b64encode(empty_tickets).decode()
        public["files"] = {
            name: base64.b64encode((storage / "lxmf" / name).read_bytes()).decode()
            for name in ["local_deliveries", "locally_processed", "outbound_stamp_costs", "node_stats"]
        }
        public["peer"] = peer.hex()
        public["messageHash"] = paper.hash.hex()
        public["transientId"] = metadata["transientId"]
        args.public_fixture.write_text(json.dumps(public, indent=2) + "\n", encoding="utf-8")
    router.exit_handler()
    print(json.dumps({key: metadata[key] for key in ["sideband", "rns", "lxmf", "routerSha256", "destinationSha256", "methods", "shims"]}))


if __name__ == "__main__":
    main()
