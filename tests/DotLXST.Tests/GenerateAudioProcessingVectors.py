import hashlib
import json
import os
import subprocess
from pathlib import Path

import numpy as np

from LXST.Codecs.Codec import resample
from LXST.Codecs.Codec import Null
from LXST import Filters
from LXST.Filters import AGC, BandPass, HighPass, LowPass
from LXST.Generators import ToneSource


ROOT = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"]).resolve(strict=True)
PINNED_REVISION = "3b596f48ddd252ddc9c315ef8f3020cfd3688223"
SOURCE_FILES = [
    "LXST/Codecs/Codec.py",
    "LXST/Filters.py",
    "LXST/Filters.c",
    "LXST/Filters.h",
    "LXST/Mixer.py",
    "LXST/Generators.py",
    "LXST/Pipeline.py",
    "LXST/Sources.py",
    "LXST/Sinks.py",
]


def samples(values):
    return np.asarray(values, dtype=np.float32)


def filter_vector(filter_type, values, rate):
    processor = filter_type
    result = processor.handle_frame(samples(values), rate)
    return np.asarray(result, dtype=np.float32).reshape(-1).tolist()


def main():
    revision = subprocess.check_output(
        ["git", "-C", str(ROOT), "rev-parse", "HEAD"], text=True
    ).strip()
    if revision != PINNED_REVISION:
        raise RuntimeError(f"Expected LXST {PINNED_REVISION}, found {revision}")
    hashes = {
        name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest()
        for name in SOURCE_FILES
    }

    resample_input = [
        [-0.75, 0.25],
        [-0.5, 0.5],
        [-0.25, 0.75],
        [0.0, -0.75],
        [0.25, -0.5],
        [0.5, -0.25],
        [0.75, 0.0],
        [0.4, 0.2],
    ]
    resampled = resample(samples(resample_input), 16, 2, 8000, 12000)
    normalized = resample(samples(resample_input), 16, 2, 8000, 12000, normalize=True)

    filter_input = [
        0.0, 0.5, -0.25, 0.75, -0.5, 0.25, 1.0, -1.0,
        0.4, -0.2, 0.8, -0.6, 0.1, -0.9, 0.3, 0.7,
    ]
    high = filter_vector(HighPass(180), filter_input, 8000)
    low = filter_vector(LowPass(1200), filter_input, 8000)
    band = filter_vector(BandPass(180, 1200), filter_input, 8000)

    agc_input = np.asarray(
        [[0.08, -0.04], [0.1, -0.05], [0.12, -0.06], [0.15, -0.08]] * 40,
        dtype=np.float32,
    )
    agc = AGC(target_level=-12.0, max_gain=12.0)
    agc_output = agc.handle_frame(agc_input, 8000).reshape(-1).tolist()

    tone = ToneSource(frequency=440, gain=0.2, ease=False, target_frame_ms=10, codec=Null())
    tone.ease_gain = 1.0
    tone_output = tone._ToneSource__generate().reshape(-1).tolist()

    output = {
        "reference": {
            "revision": revision,
            "version": "0.4.5",
            "method": "Executed pinned LXST APIs in the isolated reference venv; filters use the loaded upstream filter backend.",
            "filterBackend": "native" if Filters.USE_NATIVE_FILTERS else "python",
            "sourceSha256": hashes,
        },
        "resample": {
            "input": resample_input,
            "bitDepth": 16,
            "channels": 2,
            "inputRate": 8000,
            "outputRate": 12000,
            "output": resampled.tolist(),
            "normalizedOutput": normalized.tolist(),
        },
        "filters": {
            "input": filter_input,
            "sampleRate": 8000,
            "highPassCut": 180,
            "highPass": high,
            "lowPassCut": 1200,
            "lowPass": low,
            "bandPass": band,
            "agcInput": agc_input.reshape(-1).tolist(),
            "agcSampleRate": 8000,
            "agc": agc_output,
        },
        "tone": {
            "frequency": 440,
            "gain": 0.2,
            "sampleRate": 48000,
            "channels": 1,
            "samples": tone_output,
        },
    }
    fixture = Path(__file__).parent / "Fixtures" / "lxst-audio-processing.json"
    fixture.write_text(json.dumps(output, separators=(",", ":")) + "\n")
    print(f"Wrote {fixture} from LXST {revision}")


if __name__ == "__main__":
    main()
