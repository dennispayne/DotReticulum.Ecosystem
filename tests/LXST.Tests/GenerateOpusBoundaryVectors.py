"""Execute actual primary PyOgg automatic-bitrate packets and encoder lookahead."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_LXST_SHA, verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    from LXST.Codecs.libs.pyogg import OpusEncoder
    encoder = OpusEncoder()
    encoder.set_channels(1)
    encoder.set_sampling_frequency(12000)
    encoder.set_application("audio")
    encoder.set_max_bytes_per_frame(4000)
    delay = encoder.get_algorithmic_delay()
    rows = []
    for index in range(3):
        clock = np.arange(index * 720, (index + 1) * 720, dtype=np.float32)
        samples = (.15 * np.sin(2 * np.pi * 440 * clock / 12000)).astype(np.float32)
        pcm = (samples * np.float32(32767)).astype("<i2").tobytes()
        rows.append({"input": samples.tolist(), "encoded": bytes(encoder.encode(pcm)).hex()})
    source = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"]) / "LXST" / "Codecs" / "libs" / "pyogg" / "opus_encoder.py"
    Path(args.output).write_text(json.dumps({
        "revision": PINNED_LXST_SHA,
        "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "method": "Actual primary bundled PyOgg OpusEncoder, native 12k mono AUDIO, automatic bitrate, 4000-byte capacity; three sequential 60ms packets.",
        "delay_samples": delay, "maximum_bytes_per_frame": 4000, "frames": rows,
    }, indent=2) + "\n")


if __name__ == "__main__":
    main()
