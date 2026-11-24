# Copied unchanged below this header from DotReticulum tests/Interop/proof_probe_node.py at commit
# 7eb03e7d280d757dc1a3e676e7650c15482f9838 (git blob 6d3e68bc7ad27906059a7e56f93b904a88f724af, SHA-256 of the
# committed LF bytes 94c487aca8c8e2bf8ff615021d7ee05965a46a0e51d7db09372004d3ec4c4c8c); identical at 99275e6b.
# It imports this repository's tests/Interop/reference_environment.py, which enforces the same RNS pin.
"""Thin actual-RNS probe/receipt observer; no protocol or source substitutions."""
import argparse
import json
from pathlib import Path
import sys
import time

from reference_environment import verify_reference

verify_reference()
import RNS


def emit(event, **values):
    print("@@" + json.dumps({"event": event, **values}), flush=True)


class Announcements:
    aspect_filter = None

    def received_announce(self, destination_hash, announced_identity, app_data):
        emit("announce", dest=destination_hash.hex(), hops=RNS.Transport.hops_to(destination_hash))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--name")
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, required=True)
    parser.add_argument("--implicit-proof", choices=("yes", "no"), required=True)
    args = parser.parse_args()
    root = Path(args.config)
    root.mkdir(parents=True, exist_ok=True)
    (root / "config").write_text(
        "[reticulum]\nshare_instance=no\nenable_transport=yes\nrespond_to_probes=yes\n"
        f"use_implicit_proof={args.implicit_proof}\n[interfaces]\n[[Native]]\n"
        f"type=TCPClientInterface\nenabled=yes\ntarget_host=127.0.0.1\ntarget_port={args.rnsd_port}\n",
        encoding="utf-8")
    RNS.Reticulum(configdir=str(root), loglevel=0)
    RNS.Transport.register_announce_handler(Announcements())
    probe = RNS.Transport.probe_destination
    emit("ready", dest=probe.hash.hex(), revision=RNS.__version__, module=str(Path(RNS.__file__).resolve()))
    sequence = 0
    try:
        for line in sys.stdin:
            parts = line.split()
            if not parts:
                continue
            if parts[0] == "quit":
                break
            if parts[0] == "announce":
                probe.announce()
                emit("announced", dest=probe.hash.hex())
                continue
            target = bytes.fromhex(parts[1])
            if not RNS.Transport.has_path(target):
                RNS.Transport.request_path(target)
                deadline = time.monotonic() + 15
                while not RNS.Transport.has_path(target) and time.monotonic() < deadline:
                    time.sleep(0.02)
            identity = RNS.Identity.recall(target)
            if identity is None:
                raise RuntimeError("Probe target identity was not announced")
            destination = RNS.Destination(identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "rnstransport", "probe")
            if destination.hash != target:
                raise RuntimeError("Probe target does not have the primary transport probe aspect")
            if parts[0] == "probe":
                sequence += 1
                packet = RNS.Packet(destination, f"actual-primary-probe-{sequence}".encode("ascii"))
                receipt = packet.send()
                if receipt is None:
                    raise RuntimeError("Actual primary did not create an eligible packet receipt")
                deadline = time.monotonic() + 15
                while not receipt.proved and time.monotonic() < deadline:
                    time.sleep(0.01)
                emit("receipt", proved=receipt.proved, status=receipt.status,
                     proof_length=len(receipt.proof_packet.data) if receipt.proof_packet else None)
            elif parts[0] == "link":
                link = RNS.Link(destination)
                deadline = time.monotonic() + 8
                while link.status not in (RNS.Link.ACTIVE, RNS.Link.CLOSED) and time.monotonic() < deadline:
                    time.sleep(0.02)
                active = link.status == RNS.Link.ACTIVE
                link.teardown()
                emit("link_attempt", active=active)
            else:
                raise ValueError(f"Unknown reference observation command: {parts[0]}")
    finally:
        RNS.Reticulum.exit_handler()


if __name__ == "__main__":
    main()
