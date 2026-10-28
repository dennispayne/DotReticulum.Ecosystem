"""Observe actual primary codec control, channel conversion and stateful mode APIs."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

import numpy as np
from LXST.Codecs import Opus, Codec2

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    root = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"])
    result = {
        "revision": "3b596f48ddd252ddc9c315ef8f3020cfd3688223",
        "source_sha256": {name: hashlib.sha256((root / "LXST" / name).read_bytes()).hexdigest()
                          for name in ["Codecs/Opus.py", "Codecs/Codec2.py"]},
        "opus_channels": [], "opus_controls": [], "codec2_sequence": [],
    }
    for profile in range(9):
        for channels in [1, 2, 3]:
            codec = Opus()
            codec.set_profile(profile)
            rate = codec.profile_samplerate(profile)
            codec.source = SimpleNamespace(samplerate=rate)
            frame = np.array([[(((i * 17 + channel * 31) % 257) - 128) / 512
                               for channel in range(channels)] for i in range(rate // 50)], dtype=np.float32)
            encoded = codec.encode(frame)
            result["opus_channels"].append({
                "profile": profile, "channels": channels, "rate": rate,
                "input": frame.flatten().tolist(), "encoded": encoded.hex(),
            })
            try:
                codec.set_profile(profile)
                post_encode = "accepted"
            except Exception as error:
                post_encode = type(error).__name__
            codec.update_bitrate(2.5)
            result["opus_controls"].append({
                "profile": profile, "post_encode_set_profile": post_encode,
                "ceiling": codec.bitrate_ceiling,
                "max_bytes": codec.opus_encoder._max_bytes_per_frame.value,
            })
    codec = Codec2(mode=2400)
    frame = np.array([[(((i * 17 + channel * 31) % 257) - 128) / 512
                       for channel in range(2)] for i in range(320)], dtype=np.float32)
    for mode in [2400, 700, 700, 3200, 1200, 2400]:
        fresh = Codec2(mode=mode)
        incoming = fresh.encode(frame)
        decoded = codec.decode(incoming)
        encoded = codec.encode(frame)
        unknown = bytes([255]) + incoming[1:]
        unknown_decoded = codec.decode(unknown)
        result["codec2_sequence"].append({
            "mode": mode, "incoming": incoming.hex(), "encoded_after_decode": encoded.hex(),
            "decoded_count": decoded.shape[0], "unknown_count": unknown_decoded.shape[0],
        })
    result["codec2_input"] = frame.flatten().tolist()
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
