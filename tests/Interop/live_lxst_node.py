"""Thin live adapter around unmodified LXST and RNS APIs.

Stream mode uses actual Packetizer/LinkSource with a hardware-free application
sink. Telephone mode uses actual Telephone and an explicitly isolated virtual
PulseAudio server provided by the C# test, never a physical microphone.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import threading
import time
from types import SimpleNamespace

import numpy as np
import RNS
import LXST
from LXST.Codecs import Raw, Opus, Codec2
from LXST.Network import LinkSource, Packetizer, SignallingReceiver
from RNS.vendor import umsgpack as mp
from reference_environment import verify_reference

LOCK = threading.Lock()


def emit(event, **values):
    with LOCK:
        print("@@" + json.dumps({"event": event, **values}), flush=True)


class ObservationSink:
    samplerate = 48000
    channels = 2

    def handle_frame(self, frame, source, decoded=False):
        samples = frame.astype("<f4").tobytes()
        emit("frame", codec=type(source.codec).__name__, samples=frame.shape[0],
             channels=frame.shape[1], sha256=hashlib.sha256(samples).hexdigest())

    def can_receive(self, from_source=None):
        return True


class Signals(SignallingReceiver):
    def signalling_received(self, signals, source):
        try:
            json.dumps(signals)
            values = signals
        except TypeError:
            values = None
        emit("signals", values=values, packed=[mp.packb(value).hex() for value in signals])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--tcp-server-port", type=int, required=True)
    parser.add_argument("--connect-port", type=int, default=0)
    parser.add_argument("--telephone", action="store_true")
    parser.add_argument("--transport", action="store_true")
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    path = Path(args.config)
    path.mkdir(parents=True, exist_ok=True)
    (path / "config").write_text(
        "[reticulum]\n  share_instance = No\n  enable_transport = "
        + ("Yes" if args.transport else "No")
        + "\n[logging]\n  loglevel = 4\n[interfaces]\n  [[Server]]\n"
        + "    type = TCPServerInterface\n    enabled = Yes\n"
        + "    listen_ip = 127.0.0.1\n    listen_port = "
        + str(args.tcp_server_port) + "\n"
        + (f"  [[Relay]]\n    type = TCPClientInterface\n    enabled = Yes\n"
           f"    target_host = 127.0.0.1\n    target_port = {args.connect_port}\n" if args.connect_port else ""))
    RNS.Reticulum(configdir=str(path), loglevel=RNS.LOG_WARNING)
    identity = RNS.Identity()
    connections = []
    signals = Signals()
    phone = None

    if args.telephone:
        if not os.environ.get("DOTLXST_VIRTUAL_PULSE"):
            raise RuntimeError("Telephone testing requires an explicitly isolated virtual PulseAudio server")
        from LXST.Primitives.Telephony import Telephone
        phone = Telephone(identity, auto_answer=.3)
        phone.set_speaker("lxst_output")
        phone.set_microphone("lxst_input")
        phone.set_ringer("lxst_output")
        phone.set_ringing_callback(lambda remote: emit("ringing", identity=remote.hash.hex()))
        phone.set_established_callback(lambda remote: emit("established", identity=remote.hash.hex()))
        phone.set_ended_callback(lambda remote: emit("ended"))
        phone.set_busy_callback(lambda remote: emit("busy"))
        phone.set_rejected_callback(lambda remote: emit("rejected"))
        destination = phone.destination
    else:
        destination = RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.SINGLE, "lxst", "interop")

        def established(link):
            source = LinkSource(link, signals, sink=ObservationSink())
            source.start()
            packetizer = Packetizer(link)
            connections.append((link, source, packetizer))
            emit("linked")
        destination.set_link_established_callback(established)

    emit("ready", dest=destination.hash.hex(), identity_hash=identity.hash.hex(), public_key=identity.get_public_key().hex(),
         lxst=str(Path(LXST.__file__).resolve()))
    for line in sys.stdin:
        parts = line.strip().split()
        if not parts:
            continue
        try:
            if parts[0] == "quit":
                break
            if parts[0] == "wait-client":
                deadline = time.monotonic() + 15
                while not any(getattr(interface, "clients", 0) > 0 for interface in RNS.Transport.interfaces):
                    if time.monotonic() >= deadline:
                        raise TimeoutError("TCP server did not register an accepted client")
                    time.sleep(.01)
                emit("client-ready")
            elif parts[0] == "announce":
                destination.announce()
                emit("announced")
            elif parts[0] == "signal":
                signals.signal(int(parts[1]), connections[-1][0])
                emit("sent")
            elif parts[0] == "signal-packed":
                signals.signal(mp.unpackb(bytes.fromhex(parts[1])), connections[-1][0])
                emit("sent")
            elif parts[0] == "send":
                kind = parts[1]
                codec = {"raw": Raw(channels=2, bitdepth=32),
                         "opus": Opus(profile=Opus.PROFILE_VOICE_LOW),
                         "codec2": Codec2(mode=2400)}[kind]
                rate = 8000 if kind in ["opus", "codec2"] else 48000
                count = 320 if kind == "codec2" else 160 if kind == "opus" else 32
                samples = np.array([(((i * 17) % 257) - 128) / 512 for i in range(count)], dtype=np.float32)
                frame = np.repeat(samples[:, None], 2, axis=1)
                source = SimpleNamespace(codec=codec, samplerate=rate)
                codec.source = source
                packetizer = connections[-1][2]
                packetizer.source = source
                for _ in range(int(parts[2]) if len(parts) > 2 else 1):
                    packetizer.handle_frame(codec.encode(frame))
                    time.sleep(.025)
                emit("sent", codec=kind)
            elif parts[0] == "call":
                dest_hash = bytes.fromhex(parts[1])
                remote = RNS.Identity.recall(dest_hash)
                if remote is None:
                    RNS.Transport.request_path(dest_hash)
                    deadline = time.time() + 15
                    while remote is None and time.time() < deadline:
                        time.sleep(.1)
                        remote = RNS.Identity.recall(dest_hash)
                if remote is None:
                    raise RuntimeError("No identity for remote telephone")
                phone.call(remote, profile=int(parts[2]) if len(parts) > 2 else None)
                emit("calling")
            elif parts[0] == "allowed":
                from LXST.Primitives.Telephony import Telephone
                phone.set_allowed(Telephone.ALLOW_ALL if parts[1] == "all" else
                                  Telephone.ALLOW_NONE if parts[1] == "none" else [bytes.fromhex(parts[1])])
                emit("configured")
            elif parts[0] == "auto-answer":
                phone.auto_answer = float(parts[1])
                emit("configured")
            elif parts[0] == "busy-tone":
                phone.set_busy_tone_time(float(parts[1]))
                emit("configured")
            elif parts[0] == "ring-time":
                phone.ring_time = float(parts[1])
                emit("configured")
            elif parts[0] == "hangup":
                phone.hangup()
                emit("hungup")
            elif parts[0] == "profile":
                if len(parts) > 1:
                    phone.switch_profile(int(parts[1]))
                emit("profile", value=phone.active_profile)
        except Exception as error:
            emit("error", message=str(error))
    if phone:
        phone.teardown()
    for link, _, _ in connections:
        link.teardown()
    os._exit(0)


if __name__ == "__main__":
    main()
