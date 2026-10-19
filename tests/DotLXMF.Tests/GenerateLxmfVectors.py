"""Generates Fixtures/lxmf-vectors.json by EXECUTING upstream LXMF (LXMessage.pack / unpack_from_bytes / LXStamper) with fixed keys and timestamps.

Upstream: LXMF 1.2.1 (PyPI "lxmf") on RNS 1.5.7 @ e40191b3d193b46b7f2d8a44424a594cd758839b
Run:      PYTHONPATH=<upstream RNS checkout> python GenerateLxmfVectors.py Fixtures/lxmf-vectors.json
          (the python must have the lxmf package installed)
"""
import hashlib
import json
import sys

import RNS
import RNS.vendor.umsgpack as msgpack
import LXMF
from LXMF import LXMessage, LXStamper

# No Reticulum instance is running: registering destinations with the Transport is not needed for pack/unpack.
RNS.Transport.register_destination = staticmethod(lambda destination: None)

class _NoReticulum:
    def is_blackholed(self, identity):
        return False


RNS.Reticulum.get_instance = staticmethod(lambda: _NoReticulum())

SRC_PRV = bytes(range(1, 33)) + bytes(range(101, 133))
DST_PRV = bytes(range(33, 65)) + bytes(range(133, 165))


def make(identity_prv):
    identity = RNS.Identity(create_keys=False)
    identity.load_private_key(identity_prv)
    out = RNS.Destination(identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    return identity, out


src_id, src = make(SRC_PRV)
dst_id, dst = make(DST_PRV)
for dest in (src, dst):
    RNS.Identity.remember(b"\x00" * 32, dest.hash, dest.identity.get_public_key())


def hx(b):
    return b.hex() if b is not None else None


def vector(label, content, title, fields, method, timestamp, stamp_cost=None, stamp=None):
    # Packing needs the source destination with a private key
    src_in = RNS.Destination(src_id, RNS.Destination.IN, RNS.Destination.SINGLE, "lxmf", "delivery")
    m = LXMessage(dst, src_in, content, title, fields, desired_method=method, stamp_cost=stamp_cost)
    m.timestamp = timestamp
    if stamp is not None:
        m.stamp = stamp
        m.defer_stamp = False
    m.pack()
    parsed = LXMessage.unpack_from_bytes(m.packed)
    assert parsed.signature_validated, label
    assert parsed.hash == m.hash
    return {
        "label": label,
        "destination_hash": hx(m.destination_hash),
        "source_hash": hx(m.source_hash),
        "timestamp": timestamp,
        "title": hx(m.title),
        "content": hx(m.content),
        "fields_packed": hx(msgpack.packb(fields or {})),
        "desired_method": method,
        "method": m.method,
        "representation": m.representation,
        "packed": hx(m.packed),
        "hash": hx(m.hash),
        "signature": hx(m.signature),
        "stamp": hx(m.stamp),
        "stamp_cost": stamp_cost,
    }


def find_stamp(message_id, cost):
    workblock = LXStamper.stamp_workblock(message_id)
    counter = 0
    while True:
        stamp = hashlib.sha256(b"stamp" + counter.to_bytes(8, "big")).digest()
        if LXStamper.stamp_valid(stamp, cost, workblock):
            return stamp, LXStamper.stamp_value(workblock, stamp)
        counter += 1


vectors = [
    vector("short", "Hello from upstream", "Greeting", None, LXMessage.OPPORTUNISTIC, 1700000000.5),
    vector("fields", "With fields", "F", {1: [b"embedded"], 5: [["a.txt", b"file-bytes"]], 0x0C: [1800000000.0, bytes(range(16))], 255: "dbg"}, LXMessage.DIRECT, 1700000001.25),
    vector("unicode-empty-title", "Grüße \u2603 \U0001F600", "", {}, LXMessage.DIRECT, 1700000002.0),
    vector("opportunistic-too-big", "x" * 400, "big", None, LXMessage.OPPORTUNISTIC, 1700000003.0),
    vector("resource-sized", "y" * 2000, "resource", None, LXMessage.DIRECT, 1700000004.0),
]

# A stamped message: the stamp is computed against the message id, which does not depend on the stamp.
probe = vector("probe", "Stamped", "S", None, LXMessage.DIRECT, 1700000005.0)
stamp, value = find_stamp(bytes.fromhex(probe["hash"]), 6)
stamped = vector("stamped", "Stamped", "S", None, LXMessage.DIRECT, 1700000005.0, stamp_cost=6, stamp=stamp)
assert stamped["hash"] == probe["hash"] and stamped["stamp"] == stamp.hex()
vectors.append(stamped)

message_id = bytes.fromhex(probe["hash"])
workblock = LXStamper.stamp_workblock(message_id)
workblock_pn = LXStamper.stamp_workblock(message_id, expand_rounds=LXStamper.WORKBLOCK_EXPAND_ROUNDS_PN)

json.dump({
    "lxmf_version": "1.2.1",
    "rns_pinned_sha": "e40191b3d193b46b7f2d8a44424a594cd758839b",
    "source_private_key": SRC_PRV.hex(),
    "destination_private_key": DST_PRV.hex(),
    "overhead": LXMessage.LXMF_OVERHEAD,
    "encrypted_packet_max_content": LXMessage.ENCRYPTED_PACKET_MAX_CONTENT,
    "link_packet_max_content": LXMessage.LINK_PACKET_MAX_CONTENT,
    "messages": vectors,
    "stamp": {
        "message_id": message_id.hex(),
        "workblock_sha256": hashlib.sha256(workblock).hexdigest(),
        "workblock_pn_sha256": hashlib.sha256(workblock_pn).hexdigest(),
        "stamp": stamp.hex(),
        "value": value,
        "valid_at_cost": LXStamper.stamp_valid(stamp, 6, workblock),
        "invalid_at_cost": LXStamper.stamp_valid(stamp, 30, workblock),
    },
}, open(sys.argv[1], "w", encoding="utf-8", newline="\n") if len(sys.argv) > 1 else sys.stdout, indent=1)
