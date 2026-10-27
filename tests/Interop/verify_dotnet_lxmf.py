"""Opens DotLXMF-produced LXMF messages with upstream LXMF (unpack_from_bytes, signature validation, stamps, announce app_data).

Usage: python verify_dotnet_lxmf.py dotnet-lxmf.json   (written by LxmfDotNetToPythonTests; PYTHONPATH = pinned upstream RNS checkout, lxmf installed)
Exits non-zero on the first mismatch.
"""
import json
import base64
import sys

import RNS
import RNS.vendor.umsgpack as msgpack
import LXMF
from LXMF import LXMessage, LXStamper
from reference_environment import verify_reference

verify_reference(require_lxmf=True)

RNS.Transport.register_destination = staticmethod(lambda destination: None)


class _NoReticulum:
    def is_blackholed(self, identity):
        return False


RNS.Reticulum.get_instance = staticmethod(lambda: _NoReticulum())

data = json.load(open(sys.argv[1], encoding="utf-8"))
sys.stderr.write(f"LXMF from {LXMF.__file__}\n")

for ident in data["identities"]:
    identity = RNS.Identity(create_keys=False)
    identity.load_public_key(bytes.fromhex(ident["public_key"]))
    destination = RNS.Destination(identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    assert destination.hash.hex() == ident["delivery_hash"], "delivery hash mismatch"
    RNS.Identity.remember(b"\x00" * 32, destination.hash, identity.get_public_key())

for m in data["messages"]:
    parsed = LXMessage.unpack_from_bytes(bytes.fromhex(m["packed"]))
    label = m["label"]
    assert parsed.signature_validated, f"{label}: signature rejected by upstream ({parsed.unverified_reason})"
    assert parsed.hash.hex() == m["hash"], f"{label}: hash mismatch"
    assert parsed.title_as_string() == m["title"], f"{label}: title mismatch"
    assert parsed.content_as_string() == m["content"], f"{label}: content mismatch"
    assert parsed.timestamp == m["timestamp"], f"{label}: timestamp mismatch"
    assert msgpack.packb(parsed.fields).hex() == m["fields_packed"], f"{label}: fields mismatch"
    if m.get("stamp"):
        assert parsed.stamp.hex() == m["stamp"], f"{label}: stamp mismatch"
        if m.get("ticket"):
            assert parsed.validate_stamp(254, tickets=[bytes.fromhex(m["ticket"])]), f"{label}: ticket stamp rejected by upstream"
        else:
            assert LXStamper.stamp_valid(parsed.stamp, m["stamp_cost"], LXStamper.stamp_workblock(parsed.hash)), f"{label}: stamp rejected by upstream"
    sys.stderr.write(f"ok {label}\n")

if "paper" in data:
    paper = data["paper"]
    destination = RNS.Identity(create_keys=False)
    destination.load_private_key(bytes.fromhex(paper["destination_private_key"]))
    uri = paper["uri"]
    assert uri.startswith("lxm://")
    raw = base64.urlsafe_b64decode(uri[6:] + "==")
    packed = raw[:16] + destination.decrypt(raw[16:])
    assert packed.hex() == paper["packed"], "paper encryption mismatch"
    parsed = LXMessage.unpack_from_bytes(packed, original_method=LXMessage.PAPER)
    assert parsed.signature_validated, "paper signature rejected"
    from io import BytesIO
    restored = LXMessage.unpack_from_file(BytesIO(bytes.fromhex(paper["container"])))
    assert restored.signature_validated and restored.packed == packed, "paper container rejected"
    assert restored.method == LXMessage.PAPER and restored.transport_encrypted is True
    sys.stderr.write("ok paper encryption, URI and container\n")

for a in data["app_data"]:
    raw = bytes.fromhex(a["data"])
    assert LXMF.display_name_from_app_data(raw) == a["display_name"], "display name mismatch"
    assert LXMF.stamp_cost_from_app_data(raw) == a["stamp_cost"], "stamp cost mismatch"
    assert LXMF.compression_support_from_app_data(raw) is True, "compression flag mismatch"

sys.stderr.write("all LXMF output accepted by upstream\n")
