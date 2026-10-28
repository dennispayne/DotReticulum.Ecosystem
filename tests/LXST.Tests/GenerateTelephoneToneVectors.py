"""Observe primary Telephone ringback/busy cadence with a fake mixer/tone and clock."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import threading
from types import SimpleNamespace

import LXST.Primitives.Telephony as telephony

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    output = {
        "revision": "3b596f48ddd252ddc9c315ef8f3020cfd3688223",
        "source_sha256": hashlib.sha256((Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"]) / "LXST" / "Primitives" / "Telephony.py").read_bytes()).hexdigest(),
        "frequency": telephony.Telephone.DIAL_TONE_FREQUENCY,
        "ease_ms": telephony.Telephone.DIAL_TONE_EASE_MS,
        "substitutes": "Fake tone/mixer, synchronous thread boundary and deterministic time only; actual cadence jobs and gain methods execute unchanged.",
    }
    original_time, original_threading = telephony.time, telephony.threading
    try:
        for kind in ["ringback", "busy"]:
            clock, observed = [0.0], []
            host = telephony.Telephone.__new__(telephony.Telephone)
            class Tone:
                running = False
                gain = 0.0
                def start(self):
                    self.running = True
            class Mixer:
                should_run = False
                def start(self):
                    self.should_run = True
            class InlineThread:
                def __init__(self, target, daemon):
                    self.target = target
                def start(self):
                    self.target()
            host.audio_output = object()
            host.receive_mixer = Mixer()
            host.dial_tone = Tone()
            host.pipeline_lock = threading.Lock()
            host.busy_tone_seconds = 4.25
            host.active_call = SimpleNamespace(is_outgoing=True)
            host.call_status = telephony.Signalling.STATUS_RINGING
            def sleep(duration):
                if duration < .5:
                    observed.append({"elapsed": clock[0], "audible": host.dial_tone.gain != 0})
                clock[0] = round(clock[0] + duration, 8)
                if clock[0] >= 7.6:
                    host.active_call = None
            telephony.time = SimpleNamespace(time=lambda: clock[0], sleep=sleep)
            telephony.threading = SimpleNamespace(Thread=InlineThread)
            if kind == "ringback":
                host._Telephone__activate_dial_tone()
            else:
                host._Telephone__play_busy_tone()
            output[kind] = observed
    finally:
        telephony.time, telephony.threading = original_time, original_threading
    Path(args.output).write_text(json.dumps(output, indent=2) + "\n")


if __name__ == "__main__":
    main()
