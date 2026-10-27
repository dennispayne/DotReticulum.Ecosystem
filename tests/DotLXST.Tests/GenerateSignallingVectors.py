"""Observe actual LXST signalling APIs at the transport boundary."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference

import RNS
from RNS.vendor import umsgpack as mp
from LXST.Network import SignallingReceiver


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    root = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"])
    destination = RNS.Destination(None, RNS.Destination.OUT, RNS.Destination.PLAIN, "lxst", "signals")
    vectors = []
    receiver = SignallingReceiver()
    observed = []
    receiver.proxy = SimpleNamespace(signalling_received=lambda signals, source: observed.append(signals))
    original_send = RNS.Packet.send
    try:
        def capture(packet):
            receiver._packet(packet.data, SimpleNamespace(link=destination))
            vectors.append({
                "encoded": packet.data.hex(),
                "signals": [mp.packb(value).hex() for value in observed.pop()],
            })
            return True
        RNS.Packet.send = capture
        for value in [None, False, True, "hello", b"\x00\xff", -2, 2**64 - 1,
                      1.25, [1, "two", None], {"nested": [True, b"bytes"]},
                      {b"\xff\x01": "binary-key"},
                      mp.Ext(42, b"extension")]:
            receiver.signal(value, destination)
    finally:
        RNS.Packet.send = original_send
    output = {
        "revision": "3b596f48ddd252ddc9c315ef8f3020cfd3688223",
        "network_sha256": hashlib.sha256((root / "LXST" / "Network.py").read_bytes()).hexdigest(),
        "vectors": vectors,
    }
    Path(args.output).write_text(json.dumps(output, indent=2) + "\n")


if __name__ == "__main__":
    main()
