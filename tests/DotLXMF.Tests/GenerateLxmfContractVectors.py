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
import threading
from types import SimpleNamespace
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
text_probe = LXMF.LXMessage(dst, src, b"\xff\x00\x80", b"\xfe")
try:
    text_probe.title_as_string()
    title_decode_error = None
except Exception as error:
    title_decode_error = type(error).__name__
invalid_content_string = text_probe.content_as_string()
try:
    LXMF.LXMessage(dst, src, "\ud800")
    surrogate_error = None
except Exception as error:
    surrogate_error = type(error).__name__
assigned = LXMF.LXMessage(None, None, "Assigned", "A", desired_method=LXMF.LXMessage.DIRECT,
                         destination_hash=dst.hash, source_hash=src.hash)
assignment_errors = []
for setter in (assigned.set_destination, assigned.set_source):
    try: setter("invalid")
    except Exception as error: assignment_errors.append(type(error).__name__)
assigned.set_destination(dst)
assigned.set_source(src)
for setter, value in ((assigned.set_destination, dst), (assigned.set_source, src)):
    try: setter(value)
    except Exception as error: assignment_errors.append(type(error).__name__)
assigned.timestamp = 1700000103.0
assigned.pack()
plain_destination = RNS.Destination(None, RNS.Destination.OUT, RNS.Destination.PLAIN, "lxmf", "delivery")
plain = LXMF.LXMessage(plain_destination, src, "Plain", "P", desired_method=LXMF.LXMessage.OPPORTUNISTIC)
plain.timestamp = 1700000104.0
plain.pack()
plain.determine_transport_encryption()
RNS.Transport.destinations.append(plain_destination)
try:
    LXMF.LXMessage.unpack_from_bytes(plain.packed)
    plain_registered_unpack_error = None
except Exception as error:
    plain_registered_unpack_error = type(error).__name__
finally:
    RNS.Transport.destinations.remove(plain_destination)
group_destination = RNS.Destination(destination, RNS.Destination.OUT, RNS.Destination.GROUP, "lxmf", "delivery")
group_message = LXMF.LXMessage(group_destination, src, "Group", desired_method=LXMF.LXMessage.OPPORTUNISTIC)
try:
    group_message.pack()
    group_pack_error = None
except Exception as error:
    group_pack_error = type(error).__name__
group_key = bytes(range(64))
group_destination.load_private_key(group_key)
group_paper = LXMF.LXMessage(group_destination, src, "Group paper", "G", desired_method=LXMF.LXMessage.PAPER)
group_paper.timestamp = 1700000105.0
group_paper.pack()
assert group_destination.decrypt(group_paper.paper_packed[16:]) == group_paper.packed[16:]
group_paper.determine_transport_encryption()
zero_timestamp = LXMF.LXMessage(dst, src, "Epoch", "Zero")
zero_timestamp.timestamp = 0.0
zero_timestamp.pack()

# Execute ticket policy without starting router threads or doing filesystem I/O.
# A fixed clock is the only behavioral input replaced; ticket policy is upstream.
router_module = importlib.import_module("LXMF.LXMRouter")
router = object.__new__(LXMF.LXMRouter)
router.delivery_destinations = {}
router.enforce_ratchets = False
router.ratchetpath = str(Path("artifacts") / "lxmf-registration-evidence")
first_registration = router.register_delivery_identity(source, display_name="First", stamp_cost=12)
second_registration = router.register_delivery_identity(destination, display_name="Second", stamp_cost=14)
registration_contract = {"first": first_registration.hash.hex(), "second": second_registration,
                          "count": len(router.delivery_destinations), "cost": first_registration.stamp_cost}
assert second_registration is None and len(router.delivery_destinations) == 1
plain_router_result = router.lxmf_delivery(plain.packed, destination_type=RNS.Destination.PLAIN)
for generated in Path(router.ratchetpath).iterdir(): generated.unlink()
Path(router.ratchetpath).rmdir()
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
# Transport is observed, not run: execute actual peer offer preparation and
# completion policies against source-owned entry weights and queue bookkeeping.
selection_cases = []
router.prioritised_list = [bytes([3]) * 16]
for sync_limit in (0.9, 0.756):
    router.propagation_entries = {}
    selection_entries = []
    selected_peer = peer_type(router, dst.hash)
    selected_peer.peering_cost = 4
    selected_peer.peering_key = [peering_key, 4]
    selected_peer.propagation_stamp_cost = 16
    selected_peer.propagation_stamp_cost_flexibility = 3
    selected_peer.propagation_transfer_limit = 1
    selected_peer.propagation_sync_limit = sync_limit
    selected_peer.state = peer_type.LINK_READY
    offers = []
    selected_peer.link = SimpleNamespace(request=lambda path, offer, **kwargs: offers.append(offer))
    for index, size, age, value in ((1, 100, 0, 13), (2, 400, 8, 13), (3, 600, 0, 13),
                                   (4, 1100, 0, 13), (5, 100, 0, 12)):
        transient_id = bytes([index]) * 32
        received = 1700000302.0 - age * 86400
        router.propagation_entries[transient_id] = [bytes([index]) * 16, None, received, size, [], [], value]
        selected_peer.add_unhandled_message(transient_id)
        with patch.object(router_module.time, "time", return_value=1700000302.0):
            weight = router.get_weight(transient_id)
        selection_entries.append({"id": transient_id.hex(), "size": size, "weight": weight, "stamp_value": value})
    selected_peer.add_unhandled_message(bytes([6]) * 32)
    with patch.object(RNS.Transport, "has_path", return_value=True), \
         patch.object(router_module.time, "time", return_value=1700000302.0):
        selected_peer.sync()
    selection_cases.append({"sync_limit": sync_limit * 1000, "entries": selection_entries,
                            "offered": [entry.hex() for entry in offers[0][1]],
                            "handled": [entry.hex() for entry in selected_peer.handled_messages],
                            "unhandled": [entry.hex() for entry in selected_peer.unhandled_messages]})
completion_cases = []
for strategy in (peer_type.STRATEGY_LAZY, peer_type.STRATEGY_PERSISTENT):
    for entry in router.propagation_entries.values():
        entry[4] = []
        entry[5] = []
    completed_peer = peer_type(router, dst.hash, sync_strategy=strategy)
    for index in (1, 2, 3): completed_peer.add_unhandled_message(bytes([index]) * 32)
    completed_peer.last_offer = [bytes([1]) * 32, bytes([2]) * 32]
    # A subset response marks the declined ID handled before the transfer.
    router.propagation_entries[bytes([2]) * 32][1] = str(Path(__file__))
    resource_type = RNS.Resource
    with patch.object(RNS, "Resource", return_value=object()):
        completed_peer.offer_response(SimpleNamespace(response=[bytes([2]) * 32]))
    declined_handled = [entry.hex() for entry in completed_peer.handled_messages]
    continued = []
    completed_peer.sync = lambda: continued.append(True)
    fake_resource = SimpleNamespace(status=resource_type.COMPLETE, get_transfer_size=lambda: 128,
                                    get_data_size=lambda: 128)
    with patch.object(router_module.time, "time", return_value=1700000302.0):
        completed_peer.resource_concluded(fake_resource)
    completion_cases.append({"strategy": strategy, "declined_handled": declined_handled,
                             "handled": [entry.hex() for entry in completed_peer.handled_messages],
                             "unhandled": [entry.hex() for entry in completed_peer.unhandled_messages],
                             "continued": bool(continued)})
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
router.propagation_entries = {}
router.delivery_destinations = {dst.hash: dst}
dst.stamp_cost = 254
router._enforce_stamps = True
router.ignored_list = []
router.locally_processed_transient_ids = {}
router.locally_delivered_transient_ids = {}
router.delivered_transient_ids_lock = threading.Lock()
router.processed_transient_ids_lock = threading.Lock()
paper_observations = []
router._LXMRouter__delivery_callback = lambda incoming: paper_observations.append({
    "hash": incoming.hash.hex(), "method": incoming.method, "stamp_valid": incoming.stamp_valid,
    "signature_validated": incoming.signature_validated})
with patch.object(router_module.time, "time", return_value=1700000302.0):
    ingested = router.ingest_lxm_uri(paper.as_uri(finalise=False), "local", "duplicate")
    duplicate_ingest = router.ingest_lxm_uri(paper.as_uri(finalise=False), "local", "duplicate")
    allowed_duplicate = router.ingest_lxm_uri(paper.as_uri(finalise=False), "local", "duplicate", allow_duplicate=True)
assert ingested == "local" and duplicate_ingest == "duplicate" and allowed_duplicate == "local"
assert len(paper_observations) == 2 and all(entry["method"] == LXMF.LXMessage.PROPAGATED for entry in paper_observations)
router.outbound_stamp_costs = {
    dst.hash: [1700000302.0, 12],
    src.hash: [1700000302.0 - router.STAMP_COST_EXPIRY - 1, 18],
    bytes([9]) * 16: [1700000302.0, None],
}
router.cost_file_lock = threading.Lock()
with patch.object(router_module.time, "time", return_value=1700000302.0):
    router.clean_outbound_stamp_costs()
router.save_outbound_stamp_costs()
stamp_cost_cache = Path(router.storagepath, "outbound_stamp_costs").read_bytes()
assert src.hash not in router.outbound_stamp_costs and router.get_outbound_stamp_cost(dst.hash) == 12
Path(router.storagepath, "outbound_stamp_costs").unlink()
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
    "message_api": {"invalid_title_error": title_decode_error, "invalid_content": invalid_content_string,
                    "surrogate_error": surrogate_error, "assignment_errors": assignment_errors,
                    "assigned_packed": assigned.packed.hex(), "assigned_hash": assigned.hash.hex()},
    "delivery_registration": registration_contract,
    "group_paper": {"key": group_key.hex(), "packed": group_paper.packed.hex(), "uri": group_paper.as_uri(finalise=False),
                    "encryption": group_paper.transport_encryption},
    "zero_timestamp_packed": zero_timestamp.packed.hex(),
    "destination_types": {"plain_packed": plain.packed.hex(), "plain_hash": plain.hash.hex(),
                          "plain_destination": plain_destination.hash.hex(), "plain_content_limit": LXMF.LXMessage.PLAIN_PACKET_MAX_CONTENT,
                          "plain_encrypted": plain.transport_encrypted, "plain_encryption": plain.transport_encryption,
                          "plain_router_result": plain_router_result,
                          "plain_registered_unpack_error": plain_registered_unpack_error,
                          "group_opportunistic_error": group_pack_error},
    "delivery_cache_packed": delivery_cache_packed.hex(),
    "peer_rotation": rotation_cases,
    "peer_selection": selection_cases,
    "peer_completion": completion_cases,
    "store_eviction": {"entries": eviction_entries, "prioritised": [entry.hex() for entry in router.prioritised_list],
                       "limit": router.message_storage_limit, "remaining": eviction_remaining},
    "paper_ingestion": {"results": [ingested, duplicate_ingest, allowed_duplicate], "observations": paper_observations},
    "stamp_cost_cache": {"packed": stamp_cost_cache.hex(), "expiry": router.STAMP_COST_EXPIRY},
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
