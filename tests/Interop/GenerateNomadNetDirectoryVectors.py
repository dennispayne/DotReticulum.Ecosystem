"""Observe actual primary node ordering, identify policy and associated-node trust."""
import hashlib
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

from reference_environment import verify_reference


def main():
    verify_reference(require_lxmf=True)
    source = Path(os.environ["DOTRETICULUM_NOMADNET_SOURCE"]).resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2":
        raise RuntimeError("Unexpected primary revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Modified primary source")
    sys.path.insert(0, str(source))
    import RNS
    module = importlib.import_module("nomadnet.Directory")
    if Path(module.__file__).resolve() != source / "nomadnet/Directory.py":
        raise RuntimeError("Wrong imported primary Directory")
    directory = module.Directory.__new__(module.Directory)
    directory.directory_entries = {}
    directory._node_announces = directory._peer_announces = directory._pn_announces = []
    work = Path("artifacts/tests/nomadnet-directory-reference").resolve()
    work.mkdir(parents=True, exist_ok=True)
    config = work / "rns"
    config.mkdir(exist_ok=True)
    (config / "config").write_text("[reticulum]\nshare_instance=no\nenable_transport=no\n[logging]\nloglevel=0\n[interfaces]\n", encoding="utf-8")
    RNS.Reticulum(configdir=str(config))
    directory.app = SimpleNamespace(directorypath=str(work / "public-directory"))
    inputs = []
    for index, (name, rank, trust, node) in enumerate([
        ("Rank 100", 100, 255, True), ("Unknown", None, 2, True),
        ("Trusted", None, 255, True), ("Warning", None, 0, True),
        ("Rank minus", -3, 0, True), ("Rank 34", 34, 255, True),
        ("\ue000", 12, 2, True), ("\U0001f600", 12, 2, True),
        ("Peer only", -100, 255, False),
    ]):
        source_hash = bytes([index + 1]) * 16
        directory.directory_entries[source_hash] = module.DirectoryEntry(
            source_hash, name, trust, node, sort_rank=rank)
        inputs.append({"hash": source_hash.hex(), "name": name, "rank": rank, "trust": trust, "node": node})
    ordering = [entry.source_hash.hex() for entry in directory.known_nodes()]
    source_hash = bytes.fromhex(inputs[1]["hash"])
    identify = [directory.should_identify_on_connect(bytes(16)), directory.should_identify_on_connect(source_hash)]
    directory.set_identify_on_connect(source_hash, True)
    identify.append(directory.should_identify_on_connect(source_hash))
    identity = RNS.Identity()
    peer_hash = RNS.Destination.hash_from_name_and_identity("lxmf.delivery", identity)
    node_hash = RNS.Destination.hash_from_name_and_identity("nomadnetwork.node", identity)
    RNS.Identity.remember(bytes(32), peer_hash, identity.get_public_key())
    directory.directory_entries[node_hash] = module.DirectoryEntry(node_hash, "Associated", hosts_node=True)
    directory.remember(module.DirectoryEntry(peer_hash, "Delivery", module.DirectoryEntry.TRUSTED))
    associated = directory.directory_entries[node_hash].trust_level
    result = {
        "commit": revision,
        "sourceSha256": hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest(),
        "method": "Actual Directory.known_nodes, should_identify_on_connect, set_identify_on_connect and remember with real RNS.Identity.remember and actual directory persistence; ephemeral identity is not exported.",
        "inputs": inputs, "ordering": ordering, "identify": identify,
        "associatedTrust": associated,
    }
    Path(sys.argv[1]).write_text(json.dumps(result, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
