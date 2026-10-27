"""Execute LXMF 1.2.1 to generate ticket, paper and announce contract evidence.

Run with the isolated reference Python and PYTHONPATH pointing at pinned RNS.
No Reticulum instance is needed; destination registration and blackhole lookup
are disabled, as in GenerateLxmfVectors.py. No cryptographic calls are replaced.
"""
import hashlib
import importlib
import importlib.metadata
import json
from pathlib import Path
import subprocess
import sys
import os
from unittest.mock import patch

import LXMF
import RNS
import RNS.vendor.umsgpack as msgpack

assert importlib.metadata.version("lxmf") == "1.2.1"
LXMF_SHA = "e8ba02483217af43be284a72f1c059c730b7d047"
source_root = Path(os.environ["DOTRETICULUM_LXMF_UPSTREAM"]).resolve(strict=True)
assert Path(LXMF.__file__).resolve().is_relative_to(source_root / "LXMF")
assert subprocess.check_output(["git", "-C", str(source_root), "rev-parse", "HEAD"], text=True).strip() == LXMF_SHA
RNS.Transport.register_destination = staticmethod(lambda destination: None)


class NoReticulum:
    def is_blackholed(self, identity):
        return False

    def _used_destination_data(self, destination_hash):
        pass


RNS.Reticulum.get_instance = staticmethod(lambda: NoReticulum())
source_key = bytes(range(1, 33)) + bytes(range(101, 133))
destination_key = bytes(range(33, 65)) + bytes(range(133, 165))
source = RNS.Identity(create_keys=False)
source.load_private_key(source_key)
destination = RNS.Identity(create_keys=False)
destination.load_private_key(destination_key)
src = RNS.Destination(source, RNS.Destination.IN, RNS.Destination.SINGLE, "lxmf", "delivery")
dst = RNS.Destination(destination, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
for item in (src, dst):
    RNS.Identity.remember(b"\0" * 32, item.hash, item.identity.get_public_key())

ticket = bytes(range(16))
message = LXMF.LXMessage(dst, src, "Ticket", "T", desired_method=LXMF.LXMessage.DIRECT)
message.timestamp = 1700000100.25
message.outbound_ticket = ticket
message.defer_stamp = False
message.pack()
assert message.validate_stamp(254, tickets=[ticket])

paper = LXMF.LXMessage(dst, src, "Paper Grüße", "P", desired_method=LXMF.LXMessage.PAPER)
paper.timestamp = 1700000101.5
paper.pack()
assert destination.decrypt(paper.paper_packed[16:]) == paper.packed[16:]
paper.determine_transport_encryption()
paper_container = paper.packed_container()
binary_fields = LXMF.LXMessage(dst, src, "Binary key", fields={0xfc: {b"\xff\x01": [1, b"\x00"]}}, desired_method=LXMF.LXMessage.DIRECT)
binary_fields.timestamp = 1700000102.0
binary_fields.stamp = bytes(range(32))
binary_fields.defer_stamp = False
binary_fields.stamp_cost = 1
binary_fields.pack()

# Execute ticket policy without starting router threads or doing filesystem I/O.
# A fixed clock is the only behavioral input replaced; ticket policy is upstream.
router_module = importlib.import_module("LXMF.LXMRouter")
router = object.__new__(LXMF.LXMRouter)
router.available_tickets = {"outbound": {}, "inbound": {}, "last_deliveries": {}}
router.save_available_tickets = lambda: None
with patch.object(router_module.time, "time", return_value=1700000200.0):
    issued = router.generate_ticket(dst.hash)
    reused = router.generate_ticket(dst.hash)
    assert issued == reused
    router.remember_ticket(dst.hash, issued)
    assert router.get_outbound_ticket(dst.hash) == issued[1]
    inbound_at_issue = router.get_inbound_tickets(dst.hash)
    store_packed = msgpack.packb(router.available_tickets)
    router.available_tickets["last_deliveries"][dst.hash] = 1700000200.0
    assert router.generate_ticket(dst.hash) is None
with patch.object(router_module.time, "time", return_value=issued[0]):
    outbound_at_expiry = router.get_outbound_ticket(dst.hash)
    inbound_at_expiry = router.get_inbound_tickets(dst.hash)

propagated = LXMF.LXMessage(dst, src, "Propagated", "PN", desired_method=LXMF.LXMessage.PROPAGATED)
propagated.timestamp = 1700000300.0
with patch.object(router_module.time, "time", return_value=1700000301.0):
    propagated.pack()
pn_workblock = LXMF.LXStamper.stamp_workblock(propagated.transient_id, expand_rounds=1000)
counter = 0
while True:
    pn_stamp = hashlib.sha256(b"pn-vector" + counter.to_bytes(8, "big")).digest()
    if LXMF.LXStamper.stamp_valid(pn_stamp, 4, pn_workblock):
        break
    counter += 1
validated = LXMF.LXStamper.validate_pn_stamp(msgpack.unpackb(propagated.propagation_packed)[1][0] + pn_stamp, 4)
assert validated[0] == propagated.transient_id
peering_id = destination.hash + source.hash
peering_workblock = LXMF.LXStamper.stamp_workblock(peering_id, expand_rounds=25)
counter = 0
while True:
    peering_key = hashlib.sha256(b"peer-vector" + counter.to_bytes(8, "big")).digest()
    if LXMF.LXStamper.validate_peering_key(peering_id, peering_key, 4):
        break
    counter += 1
router.name = "Vector PN"
router.propagation_node = True
router.from_static_only = False
router.propagation_stamp_cost = 16
router.propagation_stamp_cost_flexibility = 3
router.peering_cost = 18
router.propagation_per_transfer_limit = 256
router.propagation_per_sync_limit = 10240
with patch.object(router_module.time, "time", return_value=1700000301.0):
    pn_announce = router.get_propagation_node_app_data()
peer_type = importlib.import_module("LXMF.LXMPeer").LXMPeer
router.propagation_entries = {propagated.transient_id: [dst.hash, None, 1700000301.0, len(propagated.packed), [], [], validated[2]]}
peer = peer_type(router, dst.hash)
peer.alive = True
peer.last_heard = 1700000302.0
peer.peering_timebase = 1700000301.0
peer.propagation_transfer_limit = 256
peer.propagation_sync_limit = 10240
peer.propagation_stamp_cost = 16
peer.propagation_stamp_cost_flexibility = 3
peer.peering_cost = 18
peer.peering_key = [peering_key, LXMF.LXStamper.stamp_value(peering_workblock, peering_key)]
peer.add_handled_message(propagated.transient_id)
peer_bytes = peer.to_bytes()
assert peer_type.from_bytes(peer_bytes, router).handled_message_count == 1
router.storagepath = str(Path("artifacts") / "lxmf-vector-evidence")
router.locally_delivered_transient_ids = {message.hash: 1700000302.0}
with patch.object(router_module.time, "time", return_value=1700000302.0):
    router.save_locally_delivered_transient_ids()
delivery_cache_path = Path(router.storagepath) / "local_deliveries"
delivery_cache_packed = delivery_cache_path.read_bytes()
delivery_cache_path.unlink()
Path(router.storagepath).rmdir()
rotation_cases = []
router.max_peers = 3
router.prioritise_rotating_unreachable_peers = False
for protect_static in (False, True):
    router.peers = {}
    for index, outgoing in enumerate((0, 1, 8), 1):
        rotation_peer = peer_type(router, bytes([index]) * 16)
        rotation_peer.alive = True
        rotation_peer.last_sync_attempt = 1.0
        rotation_peer.last_heard = 1700000302.0
        rotation_peer.offered = 10
        rotation_peer.outgoing = outgoing
        router.peers[rotation_peer.destination_hash] = rotation_peer
    router.static_peers = [bytes([1]) * 16] if protect_static else []
    before = [entry.to_bytes().hex() for entry in router.peers.values()]
    router.rotate_peers()
    rotation_cases.append({"static": [entry.hex() for entry in router.static_peers],
                           "peers": before, "remaining": [entry.hex() for entry in router.peers]})
Path(router.storagepath).mkdir(parents=True)
router.propagation_entries = {}
router.prioritised_list = [bytes([3]) * 16]
router.message_storage_limit = 700
eviction_entries = []
for index, size, age_days in ((1, 100, 0), (2, 400, 8), (3, 600, 1)):
    transient_id = bytes([index]) * 32
    destination_hash = bytes([index]) * 16
    received_at = 1700000302.0 - age_days * 86400
    file_path = Path(router.storagepath) / f"{transient_id.hex()}_{received_at}_13"
    file_path.write_bytes(b"x" * size)
    router.propagation_entries[transient_id] = [destination_hash, str(file_path), received_at, size, [], [], 13]
    with patch.object(router_module.time, "time", return_value=1700000302.0):
        weight = router.get_weight(transient_id)
    eviction_entries.append({"id": transient_id.hex(), "destination": destination_hash.hex(),
                             "received": received_at, "size": size, "weight": weight})
with patch.object(router_module.time, "time", return_value=1700000302.0):
    router.clean_message_store()
eviction_remaining = [entry.hex() for entry in router.propagation_entries]
for file_path in Path(router.storagepath).iterdir():
    file_path.unlink()
Path(router.storagepath).rmdir()

announces = [
    b"Legacy",
    msgpack.packb([b"  Alice\x00  ", 8, [0]]),
    msgpack.packb([None, None]),
    msgpack.packb([b"Bob", 4, []]),
    msgpack.packb([b"Bob", 4, None]),
]
lxmf_root = Path(LXMF.__file__).parent
rns_root = Path(RNS.__file__).parent.parent
output = {
    "lxmf_version": importlib.metadata.version("lxmf"),
    "lxmf_sha": LXMF_SHA,
    "rns_sha": subprocess.check_output(["git", "-C", str(rns_root), "rev-parse", "HEAD"], text=True).strip(),
    "lxmf_source_sha256": {
        p.name: hashlib.sha256(p.read_bytes()).hexdigest()
        for p in sorted(lxmf_root.glob("*.py"))
    },
    "source_private_key": source_key.hex(),
    "destination_private_key": destination_key.hex(),
    "ticket": {
        "ticket": ticket.hex(), "packed": message.packed.hex(),
        "hash": message.hash.hex(), "stamp": message.stamp.hex(),
        "valid": message.validate_stamp(254, tickets=[ticket]),
        "invalid": message.validate_stamp(254, tickets=[b"\xff" * 16]),
    },
    "ticket_policy": {
        "now": 1700000200.0, "destination": dst.hash.hex(),
        "issued_expiry": issued[0], "issued_ticket": issued[1].hex(),
        "store_packed": store_packed.hex(),
        "inbound_at_issue": [t.hex() for t in inbound_at_issue],
        "outbound_at_expiry": outbound_at_expiry,
        "inbound_at_expiry": inbound_at_expiry,
    },
    "paper": {
        "packed": paper.packed.hex(), "paper_packed": paper.paper_packed.hex(),
        "uri": paper.as_uri(finalise=False), "mdu": LXMF.LXMessage.PAPER_MDU,
        "container": paper_container.hex(),
    },
    "binary_fields": {"packed": binary_fields.packed.hex(), "hash": binary_fields.hash.hex()},
    "delivery_cache_packed": delivery_cache_packed.hex(),
    "peer_rotation": rotation_cases,
    "store_eviction": {"entries": eviction_entries, "prioritised": [entry.hex() for entry in router.prioritised_list],
                       "limit": router.message_storage_limit, "remaining": eviction_remaining},
    "propagation": {
        "packed": propagated.packed.hex(),
        "envelope": propagated.propagation_packed.hex(),
        "transient_id": propagated.transient_id.hex(),
        "stamp": pn_stamp.hex(), "stamp_value": validated[2],
        "workblock_sha256": hashlib.sha256(pn_workblock).hexdigest(),
        "peering_id": peering_id.hex(), "peering_key": peering_key.hex(),
        "peering_workblock_sha256": hashlib.sha256(peering_workblock).hexdigest(),
        "announce": pn_announce.hex(),
        "peer": peer_bytes.hex(),
    },
    "announces": [
        {"packed": data.hex(), "name": LXMF.display_name_from_app_data(data),
         "stamp_cost": LXMF.stamp_cost_from_app_data(data),
         "compression": LXMF.compression_support_from_app_data(data)}
        for data in announces
    ],
}
Path(sys.argv[1]).write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
