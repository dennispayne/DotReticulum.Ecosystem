"""Feed independently generated native Ogg files through actual primary playback."""
import argparse
import base64
import ctypes
import hashlib
import json
import os
from pathlib import Path
import sys

import numpy as np
from GenerateLxstFileVectors import read_actual_file
from LXST.Codecs import Null
from LXST.Sources import OpusFileSource

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_LXST_SHA, verify_reference


def observe_gain_passes(path, gain):
    observations = []

    class Observer:
        def can_receive(self, from_source=None):
            return True

        def handle_frame(self, frame, source):
            observations.append(frame[:8].copy().astype(np.float32).flatten().tolist())
            if len(observations) == 9:
                source.stop()

    playback = OpusFileSource(str(path), codec=Null(), sink=Observer(), loop=True, gain=gain)
    playback.start()
    playback.ingest_thread.join(10)
    if playback.should_run:
        playback.stop()
        raise TimeoutError("Actual looping source did not stop")
    return {"gain_db": gain, "frames_per_pass": 3, "pass_prefixes": observations[::3]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--working-directory", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    directory = Path(args.working_directory)
    directory.mkdir(parents=True, exist_ok=True)
    library = ctypes.CDLL("libopusenc.so.0")
    library.ope_comments_create.restype = ctypes.c_void_p
    library.ope_comments_destroy.argtypes = [ctypes.c_void_p]
    library.ope_encoder_create_file.argtypes = [ctypes.c_char_p, ctypes.c_void_p, ctypes.c_int,
                                               ctypes.c_int, ctypes.c_int, ctypes.POINTER(ctypes.c_int)]
    library.ope_encoder_create_file.restype = ctypes.c_void_p
    library.ope_encoder_write_float.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_float), ctypes.c_int]
    library.ope_encoder_drain.argtypes = [ctypes.c_void_p]
    library.ope_encoder_destroy.argtypes = [ctypes.c_void_p]
    root = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"])
    result = {
        "revision": PINNED_LXST_SHA,
        "source_sha256": hashlib.sha256((root / "LXST" / "Sources.py").read_bytes()).hexdigest(),
        "method": "libopusenc creates synthetic mapping-family-1 files; actual primary OpusFileSource/Null observes playback unchanged. No physical devices.",
        "cases": [],
    }
    files = {}
    for channels in [3, 6, 8]:
        path = directory / f"synthetic-{channels}.opus"
        comments = library.ope_comments_create()
        error = ctypes.c_int()
        encoder = library.ope_encoder_create_file(os.fsencode(path), comments, 48000, channels, 1, ctypes.byref(error))
        if not encoder or error.value:
            raise RuntimeError(f"Native file creation failed: {error.value}")
        try:
            clock = np.arange(11520, dtype=np.float32) / 48000
            samples = np.stack([0.1 * np.sin(2 * np.pi * (300 + channel * 70) * clock)
                                for channel in range(channels)], axis=1).astype(np.float32)
            if library.ope_encoder_write_float(encoder, samples.ctypes.data_as(ctypes.POINTER(ctypes.c_float)), len(samples)):
                raise RuntimeError("Native write failed")
            if library.ope_encoder_drain(encoder):
                raise RuntimeError("Native finalization failed")
        finally:
            library.ope_encoder_destroy(encoder)
            library.ope_comments_destroy(comments)
        observation = read_actual_file(path)
        observation["decoded"] = observation["decoded"][:channels * 64]
        result["cases"].append({"file_base64": base64.b64encode(path.read_bytes()).decode(), **observation})
        files[channels] = path.read_bytes()
        if channels == 3:
            result["loop_gain"] = [observe_gain_passes(path, gain) for gain in [-6, 6]]
        path.unlink()
    path = directory / "synthetic-chain.opus"
    path.write_bytes(files[3] + files[6])
    try:
        observation = read_actual_file(path)
        observation["transition_prefix"] = observation["decoded"][11520 * 3:11520 * 3 + 64]
        observation["decoded"] = observation["decoded"][:64]
        result["changing_channel_chain"] = {"channels": [3, 6], "observation": observation}
    except Exception as error:
        result["changing_channel_chain"] = {"channels": [3, 6], "error": type(error).__name__, "message": str(error)}
    path.unlink()
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
