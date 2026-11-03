"""Execute actual Windows platform channel mapping with synthetic native buffers."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_LXST_SHA, verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    if sys.platform != "win32":
        raise RuntimeError("Requires actual Windows platform module")
    from LXST.Platforms.windows import soundcard
    result = {"revision": PINNED_LXST_SHA, "method": "Actual _Player.play/_Recorder.record; only native buffers/record chunks are synthetic. No physical input.",
              "source_sha256": hashlib.sha256(Path(soundcard.__file__).read_bytes()).hexdigest(), "cases": []}
    for channel_map in [[0], [1, 0], [2, 0, 1], [0, 0]]:
        count = len(set(channel_map))
        data = np.arange(1, count * 3 + 1, dtype=np.float32).reshape(3, count) / 10
        storage = soundcard._ffi.new("char[]", data.nbytes * 2)
        player = SimpleNamespace(channelmap=channel_map, _render_available_frames=lambda: 3,
                                 _render_buffer=lambda frames: [storage], _render_release=lambda frames: None)
        row = {"map": channel_map, "input": data.flatten().tolist()}
        try:
            soundcard._Player.play(player, data)
            row["playback"] = np.frombuffer(bytes(soundcard._ffi.buffer(storage, data.nbytes)), dtype=np.float32).tolist()
        except Exception as error:
            row["playback_error"] = type(error).__name__
        recorder = SimpleNamespace(channelmap=channel_map, _pending_chunk=np.zeros(0, dtype=np.float32),
                                   _record_chunk=lambda: data.flatten())
        row["capture"] = soundcard._Recorder.record(recorder, 3).flatten().tolist()
        result["cases"].append(row)
    speaker = soundcard.default_speaker()
    try:
        with speaker.player(samplerate=48000, channels=2, blocksize=960, exclusive_mode=True) as player:
            player.play(np.zeros((960, 2), dtype=np.float32))
        result["exclusive_default"] = {"id": speaker.id, "result": "rendered synthetic silence"}
    except Exception as error:
        result["exclusive_default"] = {"id": speaker.id, "error": type(error).__name__, "message": str(error)}
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
