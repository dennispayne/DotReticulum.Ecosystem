"""Verify an lxmd message-store file using the pinned upstream LXMF 1.2.1 decoder.

The reference verifier accepts the official wheel or the clean primary-host checkout
at e8ba02483217af43be284a72f1c059c730b7d047 via DOTRETICULUM_LXMF_UPSTREAM.
This adapter produces no stored vectors; it executes upstream against each live file.
"""
import json
import os
import sys

import LXMF
import RNS.vendor.umsgpack as msgpack
from reference_environment import verify_reference


def main():
    if len(sys.argv) != 4:
        raise SystemExit("usage: verify_lxmd_message.py <path> <title> <content-size> | --notify <output> <path>")
    verify_reference(require_lxmf=True)
    notify = sys.argv[1] == "--notify"
    path = sys.argv[3] if notify else sys.argv[1]
    with open(path, "rb") as message_file:
        container = msgpack.unpackb(message_file.read(), raw=False, strict_map_key=False)
    if not isinstance(container, dict) or set(container) != {
        "state", "lxmf_bytes", "transport_encrypted", "transport_encryption", "method"
    }:
        raise AssertionError("Persisted file is not an upstream LXMF packed container")
    message = LXMF.LXMessage.unpack_from_bytes(container["lxmf_bytes"])
    if not notify and message.title_as_string() != sys.argv[2]:
        raise AssertionError("Persisted title did not survive container decoding")
    if not notify and len(message.content) != int(sys.argv[3]):
        raise AssertionError("Persisted payload length did not survive container decoding")
    if os.path.basename(path) != message.hash.hex():
        raise AssertionError("Message-store filename is not the upstream message hash")
    result = {"hash": message.hash.hex(), "title": message.title_as_string(), "size": len(message.content)}
    if notify:
        with open(sys.argv[2], "w", encoding="utf-8") as notification:
            json.dump(result, notification)
    else:
        print(json.dumps(result))


if __name__ == "__main__":
    main()
