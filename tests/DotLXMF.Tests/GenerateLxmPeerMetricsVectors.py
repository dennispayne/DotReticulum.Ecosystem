"""Execute actual peer completion accounting with observed Resource dimensions."""
import hashlib
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

import LXMF
import RNS

SHA = "e8ba02483217af43be284a72f1c059c730b7d047"
source_root = Path(os.environ["DOTRETICULUM_LXMF_UPSTREAM"]).resolve(strict=True)
assert Path(LXMF.__file__).resolve().is_relative_to(source_root / "LXMF")
assert subprocess.check_output(["git", "-C", str(source_root), "rev-parse", "HEAD"], text=True).strip() == SHA
rns_sha = subprocess.check_output(
    ["git", "-C", str(Path(RNS.__file__).parent.parent), "rev-parse", "HEAD"], text=True).strip()
assert rns_sha == "a29bfb51c468945c96a1ee22cae0114e993e2391"
module = importlib.import_module("LXMF.LXMPeer")
module.time.time = lambda: 1700000302.0
cases = []
for label, data_size, transfer_size, elapsed in (
    ("uncompressed", 4096, 4128, 2.0),
    ("compressed", 4096, 320, 2.0),
    ("short-transfer", 17, 64, 0.125),
):
    router = SimpleNamespace(propagation_entries={})
    peer = module.LXMPeer(router, b"p" * 16, sync_strategy=module.LXMPeer.STRATEGY_LAZY)
    ids = [b"a" * 32, b"b" * 32]
    for transient_id in ids:
        router.propagation_entries[transient_id] = [b"d" * 16, None, 1, data_size, [], []]
        peer.add_unhandled_message(transient_id)
    peer.last_offer = ids + [b"c" * 32]
    peer.currently_transferring_messages = ids
    peer.current_sync_transfer_started = module.time.time() - elapsed
    resource = SimpleNamespace(status=RNS.Resource.COMPLETE, get_transfer_size=lambda: transfer_size,
                               get_data_size=lambda: data_size)
    peer.resource_concluded(resource)
    cases.append(dict(label=label, data_size=data_size, transfer_size=transfer_size, elapsed=elapsed,
                      offered=peer.offered, outgoing=peer.outgoing, tx_bytes=peer.tx_bytes,
                      sync_transfer_rate=peer.sync_transfer_rate, alive=peer.alive,
                      last_heard=peer.last_heard, unhandled_count=peer.unhandled_message_count))
output = dict(lxmf_sha=SHA, rns_sha=rns_sha,
              source_sha256=hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest(),
              fixed_now=module.time.time(), cases=cases)
Path(sys.argv[1]).write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
