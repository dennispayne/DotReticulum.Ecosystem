"""Execute primary route toggles, hangup reset, and terminal display timing."""
import argparse
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import sys
import threading
from types import SimpleNamespace
import numpy as np

import RNS
import LXST.Primitives.Telephony as telephony
from LXST.Utilities import rnphone
from LXST.Mixer import Mixer

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
        "source_sha256": {name: hashlib.sha256((root / "LXST" / name).read_bytes()).hexdigest()
                          for name in ["Primitives/Telephony.py", "Utilities/rnphone.py", "Mixer.py"]},
        "substitutes": "Constructor bypass; fake device/pipeline/mixer leases, call link and deterministic clock/thread/display boundaries. Actual route setters/toggles/hangup and terminal status job execute.",
        "routes": [], "display": [], "outcomes": [],
    }
    host = telephony.Telephone.__new__(telephony.Telephone)
    host.identity = SimpleNamespace(hash=bytes(16))
    host._Telephone__loudspeaker_on = False
    host._Telephone__receive_muted = False
    host._Telephone__transmit_muted = False
    host._Telephone__ended_callback = None
    host.call_handler_lock = threading.Lock()
    host.pipeline_lock = threading.Lock()
    host.speaker_device = "speaker-A"
    host.loudspeaker_device = "handsfree-B"
    host.active_call = SimpleNamespace(get_remote_identity=lambda: None,
                                      is_incoming=False, status=RNS.Link.CLOSED)
    host.call_status = telephony.Signalling.STATUS_ESTABLISHED
    host.dial_tone = None
    host.transmit_mixer = None
    host.transmit_pipeline = None
    host._Telephone__stop_pipelines = lambda: None
    actions = []
    class Output:
        def __init__(self, preferred_device):
            self.device = preferred_device
            actions.append(["open", preferred_device])
        def stop(self):
            actions.append(["close", self.device])
    class Pipeline:
        def __init__(self, source, codec, sink):
            self.sink = sink
        def start(self):
            actions.append(["start", self.sink.device])
        def stop(self):
            actions.append(["stop", self.sink.device])
    host.receive_mixer = SimpleNamespace(start=lambda: actions.append(["mixer-start"]))
    host.audio_output = Output("speaker-A")
    host.receive_pipeline = Pipeline(host.receive_mixer, None, host.audio_output)
    old_output, old_pipeline = telephony.LineSink, telephony.Pipeline
    try:
        telephony.LineSink, telephony.Pipeline = Output, Pipeline
        operations = [
            ("enable", lambda: host.enable_loudspeaker()),
            ("enable-again", lambda: host.enable_loudspeaker()),
            ("change-speaker", lambda: host.set_speaker("speaker-C")),
            ("disable", lambda: host.disable_loudspeaker()),
            ("disable-false", lambda: host.disable_loudspeaker(False)),
            ("enable-false", lambda: host.enable_loudspeaker(False)),
            ("enable-before-hangup", lambda: host.enable_loudspeaker()),
            ("hangup", lambda: host.hangup()),
        ]
        for name, operation in operations:
            actions.clear()
            operation()
            result["routes"].append({"name": name, "enabled": host.loudspeaker_on,
                                     "device": host.audio_output.device if host.audio_output else None,
                                     "actions": list(actions)})
    finally:
        telephony.LineSink, telephony.Pipeline = old_output, old_pipeline

    observations = []
    class MixedSink:
        def can_receive(self):
            return True
        def handle_frame(self, frame, source):
            observations.append(["output", frame.tolist()])
            source.stop()
    class Reference:
        def handle_reference(self, frame, samplerate):
            observations.append(["reference", samplerate, frame.tolist()])
    mixer = Mixer(samplerate=8000, sink=MixedSink(), gain=3)
    mixer.reference_outs = [Reference()]
    class Input:
        samplerate = 8000
        channels = 1
    mixer.handle_frame(np.asarray([0.6, -0.6, 0.1, -0.1, 0.2, -0.2, 0, 1], dtype=np.float32), Input(), decoded=True)
    mixer.should_run = True
    mixer._mixer_job()
    result["mixer"] = observations

    old_telephony_time = telephony.time
    try:
        for duration in [4.25, 0.4, 0.501, 0]:
            for reason in [telephony.Signalling.STATUS_BUSY, telephony.Signalling.STATUS_REJECTED]:
                elapsed = [0.0]
                observed = []
                class Tone:
                    running = True
                    gain = 0.0
                    def stop(self):
                        observed.append(["tone-stop", elapsed[0]])
                    def start(self):
                        self.running = True
                host.active_call = SimpleNamespace(get_remote_identity=lambda: None,
                                                  is_incoming=False, status=RNS.Link.CLOSED)
                host.call_status = telephony.Signalling.STATUS_CALLING
                host.audio_output = object()
                host.receive_mixer = SimpleNamespace(should_run=True)
                host.dial_tone = Tone()
                host.busy_tone_seconds = duration
                callback = lambda identity: observed.append(["callback", elapsed[0], host.call_status])
                host._Telephone__busy_callback = callback
                host._Telephone__rejected_callback = callback
                def sleep(seconds):
                    elapsed[0] = round(elapsed[0] + seconds, 8)
                telephony.time = SimpleNamespace(time=lambda: elapsed[0], sleep=sleep)
                host.signalling_received([reason], host.active_call)
                result["outcomes"].append({"duration": duration, "reason": reason, "events": observed})
    finally:
        telephony.time = old_telephony_time

    old_time, old_threading = rnphone.time, rnphone.threading
    class InlineThread:
        def __init__(self, target, daemon):
            self.target = target
        def start(self):
            self.target()
    try:
        rnphone.threading = SimpleNamespace(Thread=InlineThread)
        for has_display in [False, True]:
            app = rnphone.ReticulumTelephone.__new__(rnphone.ReticulumTelephone)
            app.state = app.STATE_IN_CALL
            elapsed = [0.0]
            row = {"has_display": has_display, "sleeps": [], "updates": []}
            result["display"].append(row)
            app.display = SimpleNamespace(print=lambda text, **kwargs: None) if has_display else None
            def sleep(seconds):
                row["sleeps"].append(seconds)
                elapsed[0] += seconds
                if len(row["sleeps"]) == 5:
                    app.state = app.STATE_AVAILABLE
            rnphone.time = SimpleNamespace(time=lambda: 1000 + elapsed[0], sleep=sleep)
            stream = io.StringIO()
            with contextlib.redirect_stdout(stream):
                app.display_call_status()
            row["output"] = stream.getvalue()
        result["durations"] = [{"elapsed": value, "rounded": round(value), "text": RNS.prettytime(round(value))}
                               for value in [0, 0.49, 0.5, 0.51, 1.5, 2.5, 59.5, 60.5, 61.5, 3599.5, 3661.5,
                                             86399.5, 86400, 90061.5]]
    finally:
        rnphone.time, rnphone.threading = old_time, old_threading
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
