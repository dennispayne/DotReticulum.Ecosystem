"""Execute primary Raw constructor buckets and first-frame channel latching."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys

import numpy as np
from LXST.Codecs import Raw

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    mono = np.array([[.25], [-.5]], dtype=np.float32)
    stereo = np.array([[.25, .75], [-.5, 1]], dtype=np.float32)
    result = {
        "revision": "3b596f48ddd252ddc9c315ef8f3020cfd3688223",
        "source_sha256": hashlib.sha256((Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"]) / "LXST" / "Codecs" / "Raw.py").read_bytes()).hexdigest(),
        "depths": [], "channels": [], "implicit_wide": [],
    }
    for depth in [-1, 16, 31, 32, 63, 64, 127, 128, 256]:
        result["depths"].append({"requested": depth, "header": Raw(bitdepth=depth).encode(mono)[0]})
    for channels in [-1, 0, 1, 32, 99]:
        codec = Raw(channels=channels)
        try:
            result["channels"].append({"requested": channels, "encoded": codec.encode(mono).hex()})
        except Exception as error:
            result["channels"].append({"requested": channels, "error": type(error).__name__, "message": str(error)})
    codec = Raw(bitdepth=32)
    result["encode_mono"] = codec.encode(mono).hex()
    result["encode_stereo_latched_mono"] = codec.encode(stereo).hex()
    incoming = Raw(channels=2, bitdepth=32).encode(stereo)
    codec.decode(incoming)
    result["encode_after_decode_stays_mono"] = codec.encode(stereo).hex()
    fresh = Raw(bitdepth=32)
    fresh.decode(incoming)
    result["incoming_stereo"] = incoming.hex()
    result["encode_after_first_decode_expands_stereo"] = fresh.encode(mono).hex()
    for channels in [33, 64]:
        frame = np.repeat(mono, channels, axis=1)
        result["implicit_wide"].append({"channels": channels, "encoded": Raw(bitdepth=32).encode(frame).hex()})
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
