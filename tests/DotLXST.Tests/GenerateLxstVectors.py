"""Execute LXST 0.4.5 APIs; never synthesize codec output in this adapter.

The network observation temporarily captures RNS.Packet.send at its transport
boundary. LXST's actual Packetizer and signalling methods still generate bytes.
No reference source or protocol implementation is modified.
"""
import argparse
import ctypes
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

import numpy as np
import RNS
import LXST
from LXST.Codecs import Raw, Opus, Codec2
from LXST.Network import Packetizer, SignallingReceiver
from LXST.Codecs.libs.pyogg import opus as opus_bindings

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference

PIN = "3b596f48ddd252ddc9c315ef8f3020cfd3688223"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    root = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"]).resolve(strict=True)
    if not Path(LXST.__file__).resolve().is_relative_to(root / "LXST"):
        raise RuntimeError("Unexpected LXST module path")
    revision = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
    if revision != PIN:
        raise RuntimeError("Unexpected LXST revision")
    if subprocess.check_output(["git", "-C", str(root), "status", "--porcelain", "--untracked-files=no", "--", "LXST"], text=True).strip():
        raise RuntimeError("Modified reference source")

    opus_bindings.libopus.opus_get_version_string.restype = ctypes.c_char_p
    import pycodec2
    codec2_libraries = list((Path(pycodec2.__file__).resolve().parent.parent / "pycodec2.libs").glob("*codec2*"))
    output = {
        "revision": revision,
        "rns_revision": "a29bfb51c468945c96a1ee22cae0114e993e2391",
        "module": str(LXST.__file__),
        "python": sys.version.split()[0],
        "byteorder": sys.byteorder,
        "numpy": np.__version__,
        "pycodec2": importlib.metadata.version("pycodec2"),
        "libopus": opus_bindings.libopus.opus_get_version_string().decode(),
        "codec2_library_sha256": {file.name: hashlib.sha256(file.read_bytes()).hexdigest() for file in codec2_libraries},
        "source_sha256": {}, "raw": [], "opus": [], "codec2": [], "packets": [],
    }
    for name in ["Network.py", "Codecs/Raw.py", "Codecs/Opus.py", "Codecs/Codec2.py"]:
        output["source_sha256"][name] = hashlib.sha256((root / "LXST" / name).read_bytes()).hexdigest()
    samples = np.array([[0, .25], [-.5, 1], [.125, -.125], [.75, -.75]], dtype=np.float32)
    for bits in [16, 32, 64]:
        for channels in [1, 2, 3]:
            codec = Raw(channels=channels, bitdepth=bits)
            encoded = codec.encode(samples)
            decoded = codec.decode(encoded)
            output["raw"].append({"bits": bits, "channels": channels, "input": samples.flatten().tolist(), "encoded": encoded.hex(), "decoded": decoded.flatten().tolist()})
    for profile in range(9):
        for duration_ms in [2.5, 5, 10, 20, 40, 60]:
            codec = Opus(profile=profile)
            rate = codec.profile_samplerate(profile)
            channels = codec.profile_channels(profile)
            codec.source = SimpleNamespace(samplerate=rate)
            codec.sink = SimpleNamespace(samplerate=rate, channels=channels)
            waveform = np.array([(((i * 17) % 257) - 128) / 512 for i in range(int(rate * duration_ms / 1000))], dtype=np.float32)
            frame = np.repeat(waveform[:, None], channels, axis=1)
            encoded = codec.encode(frame)
            decoded = codec.decode(encoded)
            output["opus"].append({"profile": profile, "duration_ms": duration_ms, "sample_rate": rate, "channels": channels, "input": frame.flatten().tolist(), "encoded": encoded.hex(), "decoded": decoded.flatten().tolist()})
    for mode in [700, 1200, 1300, 1400, 1600, 2400, 3200]:
        codec = Codec2(mode=mode)
        waveform = np.array([(((i * 17) % 257) - 128) / 512 for i in range(320)], dtype=np.float32).reshape(-1, 1)
        # This revision passes a 2-D mono buffer to pycodec2, which rejects it.
        # Its public stereo path selects a 1-D channel; exercise that path.
        encoded = codec.encode(np.repeat(waveform, 2, axis=1))
        decoded = codec.decode(encoded)
        output["codec2"].append({"mode": mode, "input": waveform.flatten().tolist(), "encoded": encoded.hex(), "decoded": decoded.flatten().tolist()})

    destination = RNS.Destination(None, RNS.Destination.OUT, RNS.Destination.PLAIN, "lxst", "vectors")
    captured = []
    original_send = RNS.Packet.send
    try:
        def capture(packet):
            captured.append(packet.data.hex())
            return True
        RNS.Packet.send = capture
        packetizer = Packetizer(destination)
        packetizer.source = SimpleNamespace(codec=Raw())
        packetizer.handle_frame(bytes.fromhex(output["raw"][0]["encoded"]))
        output["packets"].append({"kind": "frame", "encoded": captured[-1]})
        receiver = SignallingReceiver()
        for signal in [0, 1, 2, 3, 4, 5, 6, 319, 383]:
            receiver.signal(signal, destination)
            output["packets"].append({"kind": "signal", "signal": signal, "encoded": captured[-1]})
    finally:
        RNS.Packet.send = original_send
    Path(args.output).parent.mkdir(parents=True, exist_ok=True)
    Path(args.output).write_text(json.dumps(output, indent=2) + "\n")


if __name__ == "__main__":
    main()
