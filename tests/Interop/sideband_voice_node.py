"""Actual primary SidebandCore + ReticulumTelephone bridge.

Default audio uses an explicitly supplied isolated PulseAudio server. The
--synthetic-audio opt-in replaces only LXST's two device backend factories with
test PCM I/O. That mode is not unmodified device/backend interoperability.
Signalling, codecs, pipelines, application policy and call logs remain actual
pinned reference code in either mode. No physical devices or private audio.
"""
import argparse
import hashlib
import inspect
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
from types import SimpleNamespace

import numpy as np
import RNS

from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
DIGESTS = {
    "sbapp/sideband/core.py": "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25",
    "sbapp/sideband/voice.py": "51e61510ea5eee832e7b9ad452459998c76561d970e0e773a253fe17cc7f2ab8",
}
LOCK = threading.Lock()


def emit(event, **values):
    with LOCK:
        print("@@" + json.dumps({"event": event, **values}), flush=True)


class SyntheticBackend:
    """Disclosed test-only device boundary; no reference protocol is replaced."""
    SAMPLERATE = 48000

    def __init__(self, preferred_device=None, samplerate=48000):
        self.samplerate = samplerate
        self.channels = 2
        self.bitdepth = 32
        self.device = SimpleNamespace(channels=2)
        self.offset = 0

    def get_recorder(self, samples_per_frame):
        return self

    def get_player(self, samples_per_frame=None, low_latency=None):
        return self

    def __enter__(self):
        return self

    def __exit__(self, *args):
        pass

    def record(self, numframes):
        time.sleep(numframes / self.samplerate)
        indices = np.arange(self.offset, self.offset + numframes)
        self.offset += numframes
        samples = (.15 * np.sin(indices * (2 * np.pi * 660 / self.samplerate))).astype(np.float32)
        return np.repeat(samples[:, None], 2, axis=1)

    def play(self, frame):
        time.sleep(len(frame) / self.samplerate)

    def flush(self):
        pass

    def release_player(self):
        pass

    def release_recorder(self):
        pass


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--tcp-server-port", type=int, required=True)
    parser.add_argument("--connect-port", type=int, default=0)
    parser.add_argument("--synthetic-audio", action="store_true")
    args = parser.parse_args()
    verify_reference(require_lxmf=True, require_lxst=True)
    source = args.source.resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != PIN:
        raise RuntimeError(f"Expected {PIN}, got {revision}")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("The Sideband reference must be clean")
    for name, digest in DIGESTS.items():
        if hashlib.sha256((source / name).read_bytes().replace(b"\r\n", b"\n")).hexdigest() != digest:
            raise RuntimeError(f"Unexpected primary source: {name}")
    if args.synthetic_audio:
        import LXST.Sources
        import LXST.Sinks
        LXST.Sources.Backend = SyntheticBackend
        LXST.Sinks.Backend = SyntheticBackend
    elif not os.environ.get("DOTRETICULUM_SIDEBAND_VOICE_PULSE"):
        raise RuntimeError("Supply isolated DOTRETICULUM_SIDEBAND_VOICE_PULSE or explicit --synthetic-audio")
    else:
        os.environ["PULSE_SERVER"] = os.environ["DOTRETICULUM_SIDEBAND_VOICE_PULSE"]

    sys.path.insert(0, str(source))
    from sbapp.sideband.core import SidebandCore
    if Path(inspect.getfile(SidebandCore)).resolve() != source / "sbapp/sideband/core.py":
        raise RuntimeError("Actual Sideband import path mismatch")
    rns = args.config / "rns"
    rns.mkdir(parents=True, exist_ok=True)
    (rns / "config").write_text(
        "[reticulum]\n share_instance = No\n enable_transport = No\n"
        "[logging]\n loglevel = 2\n[interfaces]\n [[Voice server]]\n"
        " type = TCPServerInterface\n enabled = Yes\n listen_ip = 127.0.0.1\n"
        f" listen_port = {args.tcp_server_port}\n"
        + (f" [[Relay]]\n type = TCPClientInterface\n enabled = Yes\n target_host = 127.0.0.1\n target_port = {args.connect_port}\n"
           if args.connect_port else ""), encoding="utf-8")
    core = SidebandCore(None, config_path=str(args.config / "profile"),
                        rns_config_path=str(rns), is_daemon=True, quiet=True)
    core.start()
    core.config["voice_output"] = "lxst_output"
    core.config["voice_input"] = "lxst_input"
    core.config["voice_ringer"] = "lxst_output"
    core.config["voice_loudspeaker"] = "lxst_output"
    if not core.start_voice():
        raise RuntimeError("Actual SidebandCore.start_voice failed")
    phone = core.telephone
    from sbapp.sideband.voice import ReticulumTelephone
    if type(phone) is not ReticulumTelephone:
        raise RuntimeError("Actual Sideband voice coordinator did not load")
    emit("ready", dest=phone.telephone.destination.hash.hex(), identity_hash=core.identity.hash.hex(),
         public_key=core.identity.get_public_key().hex(), lxmf_dest=core.lxmf_destination.hash.hex(),
         sideband_sha=revision, source_sha256=DIGESTS,
         audio="synthetic-backend-shim" if args.synthetic_audio else "isolated-pulseaudio-unmodified-backend")
    try:
        for line in sys.stdin:
            parts = line.strip().split()
            if not parts:
                continue
            try:
                command = parts[0]
                if command == "quit":
                    break
                if command == "wait-client":
                    deadline = time.monotonic() + 15
                    while not any(getattr(interface, "clients", 0) > 0 for interface in RNS.Transport.interfaces):
                        if time.monotonic() >= deadline:
                            raise TimeoutError("Voice TCP server has no connected client")
                        time.sleep(.01)
                    emit("client-ready")
                elif command == "announce":
                    phone.announce()
                    emit("announced")
                elif command == "wait-path":
                    identity = bytes.fromhex(parts[1])
                    if len(identity) != RNS.Reticulum.TRUNCATED_HASHLENGTH // 8:
                        raise ValueError("Expected a 16-byte peer identity hash")
                    destination = RNS.Destination.hash_from_name_and_identity("lxst.telephony", identity)
                    if not RNS.Transport.has_path(destination):
                        RNS.Transport.request_path(destination)
                    deadline = time.monotonic() + 15
                    while not RNS.Transport.has_path(destination):
                        if time.monotonic() >= deadline:
                            raise TimeoutError("Actual RNS did not discover the requested telephone path")
                        time.sleep(.025)
                    emit("path-ready", dest=destination.hex())
                elif command == "call":
                    identity = bytes.fromhex(parts[1])
                    result = phone.dial(identity, profile=int(parts[2]), mode=int(parts[3]))
                    if result == "no_path":
                        raise RuntimeError("No announced path to voice peer")
                    emit("calling")
                elif command == "answer":
                    phone.answer()
                    emit("answered")
                elif command == "hangup":
                    phone.hangup()
                    emit("hungup")
                elif command == "state":
                    emit("state", value=phone.state, stats=phone.call_stats,
                         mode=phone.active_mode, profile=phone.active_profile)
                elif command == "history":
                    emit("history", entries=[{"event": row["event"], "identity": row["identity"].hex()}
                                             for row in phone.get_call_log()])
                elif command == "policy":
                    core.config["voice_trusted_only"] = parts[1] == "true"
                    core.config["voice_hdx_direct_ptt"] = parts[2] == "true"
                    emit("configured")
                elif command == "trust":
                    context = RNS.Destination.hash_from_name_and_identity("lxmf.delivery", bytes.fromhex(parts[1]))
                    if not core.has_conversation(context):
                        core.create_conversation(context)
                    if parts[2] == "true":
                        core.trusted_conversation(context)
                    else:
                        core.untrusted_conversation(context)
                    core.conversation_set_ptt_enabled(context, parts[3] == "true")
                    emit("configured")
                elif command == "mode":
                    phone.switch_mode(int(parts[1]))
                    emit("configured")
                elif command == "profile":
                    phone.switch_profile(int(parts[1]))
                    emit("configured")
                elif command == "ptt":
                    phone.squelch_transmit(parts[1] != "on")
                    emit("configured")
                else:
                    raise ValueError(f"Unknown command: {command}")
            except Exception as error:
                emit("error", message=str(error))
    finally:
        core.stop_voice()
        core.cleanup()
        RNS.Reticulum.exit_handler()
    os._exit(0)


if __name__ == "__main__":
    main()
