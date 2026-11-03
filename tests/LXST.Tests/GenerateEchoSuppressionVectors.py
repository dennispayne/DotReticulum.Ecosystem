"""Observe actual primary echo processing, including captured real NumPy noise."""
import argparse
import base64
import hashlib
import inspect
import json
import math
import os
from pathlib import Path
import sys
from types import SimpleNamespace

import numpy as np
from LXST import Filters
from LXST.Filters import EchoSuppressor

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_LXST_SHA, verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    root = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"])
    result = {
        "revision": PINNED_LXST_SHA,
        "source_sha256": hashlib.sha256((root / "LXST" / "Filters.py").read_bytes()).hexdigest(),
        "substitutes": "Deterministic wall clock; synthetic PCM; NumPy's actual normal generator is wrapped only to record its returned float32 blocks. Actual EchoSuppressor reference/frame processing and default parameters execute unchanged.",
        "defaults": {name: parameter.default for name, parameter in
                     inspect.signature(EchoSuppressor).parameters.items()},
        "pcm": "Base64 interleaved little-endian float32 fixture data, not a network format.",
        "cases": [],
    }
    original_time = Filters.time
    original_normal = np.random.normal
    clock = [1000.0]
    try:
        Filters.time = SimpleNamespace(time=lambda: clock[0])
        for name, rate, count, channels, attenuation, noise in [
                ("echo-and-double-talk", 8000, 160, 1, 0.5, False),
                ("stereo", 48000, 50, 2, 0.375, False),
                ("first-two-of-three-channels", 8000, 40, 3, 0.5, False),
                ("weak-coupling", 8000, 40, 1, 0.02, False),
                ("coupling-recovery", 8000, 90, 1, 0.5, False),
                ("custom-estimator", 8000, 50, 1, 0.5, False),
                ("unused-parameters", 8000, 160, 1, 0.5, False),
                ("comfort-noise", 8000, 250, 2, 0.5, True),
                ("noise-high-mono", 8000, 30, 1, 0.5, True),
                ("noise-high-stereo", 8000, 30, 2, 0.5, True),
                ("rate-change", 48000, 40, 1, 0.5, False),
                ("rate-drop-reference-error", 48000, 40, 1, 0.5, False),
                ("long-noise-reference-error", 48000, 12, 1, 0.5, True),
                ("mismatched-rate", 8000, 16, 1, 0.5, False)]:
            rng = np.random.RandomState(719)
            np.random.seed(981)
            width = rate * 2 // 5 if name == "long-noise-reference-error" else rate // 100
            samples = rng.normal(0, 0.2, (count + 4) * width).astype(np.float32)
            independent = rng.normal(0, 0.15, count * width).astype(np.float32)
            parameters = {"cng_enabled": noise}
            if name == "custom-estimator":
                parameters.update(max_delay_ms=80, track_window_ms=20, correlation_frame_ms=40,
                                  acc_forget=0.7, ema_alpha=0.5, estimate_every_n=3,
                                  preemph_alpha=0.8, attack_ms=8, release_ms=120,
                                  hangover_ms=80, coupling_percentile=30)
            if name == "unused-parameters":
                parameters.update(rms_threshold=100, coupling_window_s=0.001, gate_ratio_db=99,
                                  dtd_energy_db=99, ref_rms_threshold=100)
            if name.startswith("noise-high"):
                parameters.update(cng_gain=1.8, cng_color=-0.2)
            processor = EchoSuppressor(**parameters)
            row = {"name": name, "channels": channels, "parameters": parameters, "noise_blocks": [], "frames": []}
            result["cases"].append(row)

            def normal(*arguments, **keywords):
                values = original_normal(*arguments, **keywords)
                row["noise_blocks"].append(base64.b64encode(values.astype("<f4").tobytes()).decode("ascii"))
                return values
            np.random.normal = normal
            for index in range(count):
                clock[0] = 1000.0 + index * width / rate
                reference = samples[index * width:(index + 1) * width].copy()
                lag = 1 if name == "long-noise-reference-error" else 3
                microphone = samples[max(0, index - lag) * width:
                                     (max(0, index - lag) + 1) * width].copy() * attenuation
                if name == "coupling-recovery" and index < 35:
                    microphone *= 0.04
                if name in ["echo-and-double-talk", "unused-parameters"]:
                    if 70 <= index < 90:
                        microphone += independent[index * width:(index + 1) * width]
                    elif index >= 90:
                        reference[:] = 0
                        microphone[:] = independent[index * width:(index + 1) * width]
                if channels > 1:
                    reference = np.column_stack((reference * 0.8, reference * 1.2))
                    microphone = np.column_stack((microphone * 0.75, microphone * 1.25))
                    if channels == 3:
                        reference = np.column_stack((reference, np.ones(width, dtype=np.float32)))
                        microphone = np.column_stack((microphone, independent[index * width:(index + 1) * width]))
                current_rate = 8000 if name == "rate-change" and index >= 20 else rate
                if name == "rate-drop-reference-error" and index >= 20:
                    current_rate = 1000
                microphone_rate = current_rate * 2 if name == "mismatched-rate" else current_rate
                processor.handle_reference(reference, current_rate)
                try:
                    output = processor.handle_frame(microphone, microphone_rate)
                except ValueError as error:
                    row["frames"].append({
                        "reference_error": str(error),
                        "reference_rate": current_rate, "microphone_rate": microphone_rate,
                        "reference": base64.b64encode(reference.astype("<f4").tobytes()).decode("ascii"),
                        "microphone": base64.b64encode(microphone.astype("<f4").tobytes()).decode("ascii"),
                    })
                    break
                flat = np.asarray(output, dtype=np.float32).reshape(-1)
                positions = sorted(set([0, 1, 2, len(flat) // 2, len(flat) - 2, len(flat) - 1]))
                row["frames"].append({
                    "reference_rate": current_rate, "microphone_rate": microphone_rate,
                    "reference": base64.b64encode(reference.astype("<f4").tobytes()).decode("ascii"),
                    "microphone": base64.b64encode(microphone.astype("<f4").tobytes()).decode("ascii"),
                    "delay_samples": processor.delay_samples, "delay_ms": processor.delay_ms,
                    "confidence": processor.confidence, "coupling_db": processor.coupling_db,
                    "gain": processor.gain, "ser_db": float(processor.ser_db) if math.isfinite(processor.ser_db)
                    else ("infinity" if processor.ser_db > 0 else "-infinity"),
                    "positions": positions, "output": flat[positions].tolist(),
                    "rms": float(np.sqrt(np.mean(flat.astype(np.float64) ** 2))),
                })
            empty = np.zeros((0, channels), dtype=np.float32) if channels > 1 else np.zeros(0, dtype=np.float32)
            row["empty_preserved"] = processor.handle_frame(empty, current_rate) is empty
    finally:
        Filters.time = original_time
        np.random.normal = original_normal
    Path(args.output).write_text(json.dumps(result, indent=2, allow_nan=False) + "\n")


if __name__ == "__main__":
    main()
