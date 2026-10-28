"""Execute actual pinned LXMessage packing/unpacking for non-conventional fields."""
import hashlib
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys

import LXMF
import RNS

SHA = "e8ba02483217af43be284a72f1c059c730b7d047"
source_root = Path(os.environ["DOTRETICULUM_LXMF_UPSTREAM"]).resolve(strict=True)
assert Path(LXMF.__file__).resolve().is_relative_to(source_root / "LXMF")
assert subprocess.check_output(["git", "-C", str(source_root), "rev-parse", "HEAD"], text=True).strip() == SHA
RNS.Transport.register_destination = staticmethod(lambda destination: None)


class NoReticulum:
    def is_blackholed(self, identity):
        return False

    def _used_destination_data(self, destination_hash):
        pass


RNS.Reticulum.get_instance = staticmethod(lambda: NoReticulum())
source = RNS.Identity(create_keys=False)
source.load_private_key(bytes(range(1, 33)) + bytes(range(101, 133)))
destination = RNS.Identity(create_keys=False)
destination.load_private_key(bytes(range(33, 65)) + bytes(range(133, 165)))
src = RNS.Destination(source, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
dst = RNS.Destination(destination, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
for item in (src, dst):
    RNS.Identity.remember(b"\0" * 32, item.hash, item.identity.get_public_key())
cases = []
for label, fields in (
    ("nil-key", {None: "nil"}),
    ("tuple-key", {(1, b"two", None): "tuple"}),
    ("infinite-keys", {float("inf"): "positive", -float("inf"): "negative"}),
    ("nan-key", {float("nan"): "nan"}),
    ("nested-map", {0xFC: {None: "nested", (1, 2): b"\x01"}}),
    ("nil-fields", None),
    ("list-fields", ["Not a map"]),
):
    message = LXMF.LXMessage(dst, src, "Fields", "F", fields=fields if isinstance(fields, dict) else None)
    if label in ("nil-fields", "list-fields"):
        message.fields = fields
    message.timestamp = 1700000108.0
    try:
        message.pack()
        unpacked = LXMF.LXMessage.unpack_from_bytes(message.packed)
        assert unpacked.signature_validated
        cases.append({"label": label, "packed": message.packed.hex(), "hash": message.hash.hex(),
                      "key_types": [type(key).__name__ for key in unpacked.fields], "error": None})
    except Exception as error:
        cases.append({"label": label, "error": type(error).__name__,
                      "packed": message.packed.hex() if message.packed else None})
module = importlib.import_module("LXMF.LXMessage")
output = {"lxmf_sha": SHA, "source_sha256": hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest(),
          "rns_sha": subprocess.check_output(["git", "-C", str(Path(RNS.__file__).parent.parent),
                                              "rev-parse", "HEAD"], text=True).strip(),
          "cases": cases}
Path(sys.argv[1]).write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
