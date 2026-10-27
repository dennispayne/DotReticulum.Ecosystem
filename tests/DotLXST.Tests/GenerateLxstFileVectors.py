"""Observe actual primary LXST file sink/source APIs with generated audio only."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import sys
import time
from types import SimpleNamespace

import numpy as np
import LXST
from LXST.Codecs import Null, Opus
from LXST.Sinks import OpusFileSink
from LXST.Sources import OpusFileSource
from LXST.Primitives.Recorders import FileRecorder

deployed = Path(__file__).resolve().parent / "Interop"
sys.path.insert(0, str(deployed if deployed.is_dir() else Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference


def read_actual_file(path):
    frames = []

    class Observer:
        def can_receive(self, from_source=None):
            return True

        def handle_frame(self, frame, source):
            frames.append(frame.astype(np.float32))

    playback = OpusFileSource(str(path), codec=Null(), sink=Observer(), target_frame_ms=100)
    playback.start()
    playback.ingest_thread.join(10)
    if playback.should_run:
        playback.stop()
        raise TimeoutError("Actual OpusFileSource did not finish")
    decoded = np.concatenate(frames).flatten() if frames else np.array([], dtype=np.float32)
    return {
        "sample_rate": playback.samplerate,
        "channels": playback.channels,
        "samples_per_channel": playback.sample_count,
        "frame_lengths": [len(frame) for frame in frames],
        "decoded_sha256": hashlib.sha256(decoded.astype("<f4").tobytes()).hexdigest(),
        "decoded": decoded.tolist(),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output")
    parser.add_argument("--working-directory")
    parser.add_argument("--read-input")
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    if args.read_input:
        observed = read_actual_file(Path(args.read_input))
        del observed["decoded"]
        print("@@" + json.dumps(observed), flush=True)
        return
    if not args.output or not args.working_directory:
        parser.error("--output and --working-directory are required for fixture generation")
    root = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"])
    directory = Path(args.working_directory)
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / "synthetic-reference.opus"
    sink = OpusFileSink(path=str(path), profile=Opus.PROFILE_AUDIO_MAX)
    source = SimpleNamespace(samplerate=48000)
    for index in range(12):
        samples = np.arange(index * 960, (index + 1) * 960, dtype=np.float32)
        tone = (.15 * np.sin(2 * np.pi * 440 * samples / 48000)).astype(np.float32)
        sink.handle_frame(np.repeat(tone[:, None], 2, axis=1), source=source)
    deadline = time.monotonic() + 10
    while sink.frames_waiting:
        if time.monotonic() > deadline:
            raise TimeoutError("Actual OpusFileSink did not drain its queue")
        time.sleep(.01)
    sink.stop()
    output = {
        "revision": "3b596f48ddd252ddc9c315ef8f3020cfd3688223",
        "module": str(LXST.__file__),
        "source_sha256": {name: hashlib.sha256((root / "LXST" / name).read_bytes()).hexdigest()
                          for name in ["Sources.py", "Sinks.py", "Primitives/Players.py", "Primitives/Recorders.py"]},
        "file_base64": base64.b64encode(path.read_bytes()).decode(),
        **read_actual_file(path),
    }
    # Bypass only device construction; invoke the actual setter with a real sink.
    recorder = FileRecorder.__new__(FileRecorder)
    control_sink = OpusFileSink(path="original.opus", autodigest=False)
    recorder._FileRecorder__sink = control_sink
    recorder.set_output_path("replacement.opus")
    output["recorder_set_output_path"] = {
        "recorder_path": recorder._file_path,
        "sink_path": control_sink._OpusFileSink__output_path,
        "shadow_path": control_sink._FileRecorder__output_path,
    }
    Path(args.output).write_text(json.dumps(output, indent=2) + "\n")
    path.unlink()


if __name__ == "__main__":
    main()
