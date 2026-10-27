"""Execute primary LineSource with disclosed deterministic device/clock substitutes."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

import numpy as np
import LXST.Sources as sources
from LXST.Codecs import Null

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    result = {
        "revision": "3b596f48ddd252ddc9c315ef8f3020cfd3688223",
        "source_sha256": hashlib.sha256((Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"]) / "LXST" / "Sources.py").read_bytes()).hexdigest(),
        "substitutes": "Synthetic recorder and deterministic clock only; actual LineSource ingestion and Null codec execute unchanged.",
        "vectors": [],
    }
    original_backend, original_time = sources.Backend, sources.time
    try:
        for gain, ease, skip in [(0, .225, .075), (-6, .125, 0), (3, 0, .075), (0, 0, 0)]:
            clock = [0.0]
            observed = {}
            class Device:
                SAMPLERATE = 8000
                def __init__(self, preferred_device=None, samplerate=8000):
                    self.samplerate, self.channels, self.bitdepth = samplerate, 1, 32
                def get_recorder(self, samples_per_frame):
                    return self
                def __enter__(self):
                    return self
                def __exit__(self, *args):
                    return False
                def record(self, numframes):
                    clock[0] = round(clock[0] + .06, 8)
                    if clock[0] >= .6:
                        source.stop()
                    return np.full((numframes, 1), .25, dtype=np.float32)
            class Observer:
                def can_receive(self, from_source=None):
                    return True
                def handle_frame(self, frame, from_source):
                    observed[clock[0]] = float(frame[0, 0])
            sources.Backend = Device
            sources.time = SimpleNamespace(time=lambda: clock[0])
            source = sources.LineSource(target_frame_ms=60, codec=Null(), sink=Observer(),
                                        gain=gain, ease_in=ease, skip=skip)
            source.start()
            source.ingest_thread.join(5)
            if source.ingest_thread.is_alive():
                raise TimeoutError("Actual LineSource did not stop.")
            result["vectors"].append({
                "gain": gain, "ease": ease, "skip": skip,
                "frames": [{"elapsed": round(index * .06, 8), "sample": observed.get(round(index * .06, 8))}
                           for index in range(1, 11)],
            })
    finally:
        sources.Backend, sources.time = original_backend, original_time
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
