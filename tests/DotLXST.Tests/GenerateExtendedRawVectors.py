"""Observe pinned Linux/x86 NumPy long-double payloads without recording native padding."""
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
    if sys.byteorder != "little" or np.finfo(np.longdouble).nmant != 63 or np.dtype(np.longdouble).itemsize != 16:
        raise RuntimeError("This fixture specifically targets little-endian 80-bit extended precision in a 16-byte NumPy slot.")
    bits = np.array([0, 0x80000000, 1, 0x007fffff, 0x00800000, 0x3e800000,
                     0xbf000000, 0x7f7fffff, 0x7f800000, 0xff800000, 0x7fc00000, 0xffc00000,
                     0x7f800001, 0xff800001], dtype=np.uint32)
    frame = bits.view(np.float32).reshape(-1, 1)
    encoded = Raw(bitdepth=128, channels=1).encode(frame)
    decoded = Raw().decode(encoded).astype(np.float32)
    normalized = bytearray(encoded)
    for offset in range(1, len(normalized), 16):
        normalized[offset + 10:offset + 16] = bytes(6)
    zero_padding_decoded = Raw().decode(bytes(normalized)).astype(np.float32)
    if not np.array_equal(decoded.view(np.uint32), zero_padding_decoded.view(np.uint32)):
        raise RuntimeError("Actual reference did not accept zero padding with identical decoded values.")
    result = {
        "revision": "3b596f48ddd252ddc9c315ef8f3020cfd3688223",
        "source_sha256": hashlib.sha256((Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"]) / "LXST" / "Codecs" / "Raw.py").read_bytes()).hexdigest(),
        "format": "Linux x86 little-endian 80-bit extended precision, 16-byte slot",
        "padding": "Six native padding bytes are intentionally omitted; zero padding is separately accepted by actual Raw.decode with identical decoded float32 bits.",
        "input_bits": bits.tolist(), "decoded_bits": decoded.flatten().view(np.uint32).tolist(),
        "meaningful_payloads": [encoded[offset:offset + 10].hex() for offset in range(1, len(encoded), 16)],
    }
    one = np.longdouble(1)
    boundaries = []
    for midpoint, increment in [
        (one + one * 2 ** -24, one * 2 ** -63),
        (-one - one * 2 ** -24, one * 2 ** -63),
        (one * 2 ** -150, one * 2 ** -213),
        (np.longdouble(np.finfo(np.float32).max) + one * 2 ** 103, one * 2 ** 64),
        (one * 2 ** -126 - one * 2 ** -150, one * 2 ** -189),
    ]:
        boundaries.extend([midpoint - increment, midpoint, midpoint + increment])
    boundary_frame = np.array(boundaries, dtype=np.longdouble).reshape(-1, 1)
    boundary_encoded = Raw(bitdepth=128, channels=1).encode(boundary_frame)
    boundary_decoded = Raw().decode(boundary_encoded).astype(np.float32)
    result["decode_payloads"] = [boundary_encoded[offset:offset + 10].hex() for offset in range(1, len(boundary_encoded), 16)]
    result["decode_bits"] = boundary_decoded.flatten().view(np.uint32).tolist()
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
