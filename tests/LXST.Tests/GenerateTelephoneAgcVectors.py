"""Execute pinned telephone AGC controls with real AGC and fake call boundaries."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--interop-directory", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    sys.path.insert(0, str(Path(args.interop_directory).resolve()))
    from reference_environment import verify_reference, PINNED_LXST_SHA
    verify_reference(require_lxst=True)
    import numpy as np
    import LXST.Filters as filters
    from LXST.Primitives.Telephony import Telephone

    root = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"])
    phone = Telephone.__new__(Telephone)
    packetizer = SimpleNamespace(squelched=False)
    packetizer.squelch = lambda: setattr(packetizer, "squelched", True)
    packetizer.unsquelch = lambda: setattr(packetizer, "squelched", False)
    agc = filters.AGC(target_level=-15.0)
    phone.active_call = SimpleNamespace(packetizer=packetizer, filter_agc=agc)
    steps = []
    operations = [
        ("initial", None), ("pause_agc", True), ("pause_agc", False),
        ("resume_agc", False), ("resume_agc", True),
        ("squelch_transmit", True), ("resume_agc", True),
        ("pause_agc", True), ("resume_agc", False),
        ("unsquelch_transmit", True), ("pause_agc", True),
        ("resume_agc", True),
    ]
    samples = np.asarray([0.125 if index % 8 < 4 else -0.125 for index in range(1440)], dtype=np.float32)
    for operation, argument in operations:
        if argument is not None:
            getattr(phone, operation)(argument)
        before_gain = np.asarray(agc._current_gain_lin).reshape(-1).tolist()
        before_hold = agc._hold_counter
        output = agc.handle_frame(samples.copy(), 24000)
        steps.append({
            "operation": operation, "argument": argument,
            "paused": agc.paused, "squelched": packetizer.squelched,
            "gain_before": before_gain, "gain_after": np.asarray(agc._current_gain_lin).reshape(-1).tolist(),
            "hold_before": before_hold, "hold_after": agc._hold_counter,
            "samples": output.tolist(),
        })
    no_filter = []
    for active_call in [None, SimpleNamespace(packetizer=packetizer)]:
        for operation in ["pause_agc", "resume_agc"]:
            for argument in [None, False, True]:
                phone.active_call = active_call
                if argument is None:
                    value = getattr(phone, operation)()
                else:
                    value = getattr(phone, operation)(argument)
                no_filter.append({
                    "active_call": active_call is not None, "operation": operation,
                    "argument": argument, "returned_none": value is None,
                    "filter_created": active_call is not None and hasattr(active_call, "filter_agc"),
                })
    defaults = []
    phone.active_call = SimpleNamespace(filter_agc=agc)
    for operation in ["pause_agc", "resume_agc"]:
        getattr(phone, operation)()
        defaults.append({"operation": operation, "paused": agc.paused})
    result = {
        "revision": PINNED_LXST_SHA,
        "source_sha256": {name: hashlib.sha256((root / "LXST" / name).read_bytes()).hexdigest()
                          for name in ["Primitives/Telephony.py", "Filters.py", "Filters.c", "Filters.h"]},
        "method": "Actual telephone pause/resume/PTT methods and actual AGC.handle_frame; only constructor, call and packetizer boundaries substituted; no telephone/filter method replaced.",
        "native_filters": filters.USE_NATIVE_FILTERS,
        "numpy_version": np.__version__,
        "sample_rate": 24000, "input_samples": samples.tolist(),
        "steps": steps, "without_filter": no_filter, "default_arguments": defaults,
    }
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
