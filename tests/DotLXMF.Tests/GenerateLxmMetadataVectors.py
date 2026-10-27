"""Execute pinned message delivery with explicit blackhole and physical observations."""
import hashlib
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys
import threading

import LXMF
import RNS

SHA = "e8ba02483217af43be284a72f1c059c730b7d047"
source_root = Path(os.environ["DOTRETICULUM_LXMF_UPSTREAM"]).resolve(strict=True)
assert Path(LXMF.__file__).resolve().is_relative_to(source_root / "LXMF")
assert subprocess.check_output(["git", "-C", str(source_root), "rev-parse", "HEAD"], text=True).strip() == SHA
rns_sha = subprocess.check_output(
    ["git", "-C", str(Path(RNS.__file__).parent.parent), "rev-parse", "HEAD"], text=True).strip()
assert rns_sha == "a29bfb51c468945c96a1ee22cae0114e993e2391"
RNS.Transport.register_destination = staticmethod(lambda destination: None)
blocked = False


class Observation:
    def is_blackholed(self, identity):
        return blocked

    def _used_destination_data(self, destination_hash):
        pass


RNS.Reticulum.get_instance = staticmethod(lambda: Observation())
source = RNS.Identity(create_keys=False)
source.load_private_key(bytes(range(1, 33)) + bytes(range(101, 133)))
destination = RNS.Identity(create_keys=False)
destination.load_private_key(bytes(range(33, 65)) + bytes(range(133, 165)))
src = RNS.Destination(source, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
dst = RNS.Destination(destination, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
for item in (src, dst):
    RNS.Identity.remember(b"\0" * 32, item.hash, item.identity.get_public_key())
message = LXMF.LXMessage(dst, src, "Observed", "Metadata")
message.timestamp = 1700000109.0
message.pack()
dst.stamp_cost = None
module = importlib.import_module("LXMF.LXMRouter")
rows = []
for destination_type, method, ratchet in (
    (RNS.Destination.SINGLE, LXMF.LXMessage.OPPORTUNISTIC, b"r" * 10),
    (RNS.Destination.LINK, LXMF.LXMessage.DIRECT, b"l" * 16),
    (RNS.Destination.GROUP, LXMF.LXMessage.PAPER, None),
    (RNS.Destination.PLAIN, LXMF.LXMessage.OPPORTUNISTIC, None),
):
    for blocked in (False, True):
        router = module.LXMRouter.__new__(module.LXMRouter)
        router.delivery_destinations = {dst.hash: dst}
        router.ignored_list = []
        router.locally_delivered_transient_ids = {}
        router.delivered_transient_ids_lock = threading.Lock()
        observed = []
        router._LXMRouter__delivery_callback = observed.append
        result = router.lxmf_delivery(message.packed, destination_type,
                                     phy_stats={"rssi": -91.5, "snr": 7.25, "q": 86},
                                     ratchet_id=ratchet, method=method)
        delivered = observed[0] if observed else None
        rows.append(dict(destination_type=destination_type, method=method, blocked=blocked,
                         accepted=result, ratchet_id=delivered.ratchet_id.hex() if delivered and delivered.ratchet_id else None,
                         encrypted=delivered.transport_encrypted if delivered else None,
                         encryption=delivered.transport_encryption if delivered else None,
                         rssi=delivered.rssi if delivered else None,
                         snr=delivered.snr if delivered else None, quality=delivered.q if delivered else None))
output = dict(lxmf_sha=SHA, rns_sha=rns_sha,
              router_source_sha256=hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest(),
              message_source_sha256=hashlib.sha256(Path(importlib.import_module("LXMF.LXMessage").__file__).read_bytes()).hexdigest(),
              packed=message.packed.hex(), cases=rows)
assert all(row["accepted"] is not row["blocked"] for row in rows)
Path(sys.argv[1]).write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
