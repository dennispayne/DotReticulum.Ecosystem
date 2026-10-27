"""Execute primary rnphone hardware UI with fake telephone/display/network/clock boundaries."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference
from LXST.Utilities import rnphone


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    app = rnphone.ReticulumTelephone.__new__(rnphone.ReticulumTelephone)
    actions = []
    now = [0.0]
    rnphone.time = SimpleNamespace(time=lambda: now[0])
    app.service = True
    class Display:
        def __init__(self):
            self.lines = ["Telephone Ready", ""]
            self.sleeping = False
        def print(self, text, x=0, y=0):
            self.lines[y] = text
        def clear(self):
            self.lines = ["", ""]
        def wake(self):
            self.sleeping = False
        def sleep(self):
            self.sleeping = True
    app.display = Display()
    app.keypad = SimpleNamespace(ec=SimpleNamespace(DOWN="down", UP="up"))
    app.hw_state = app.HW_STATE_IDLE
    app.hw_input = ""
    app.hw_last_event = 0
    app.state = app.STATE_AVAILABLE
    app.aliases = {"0123456789abcdef0123456789abcdef": "12"}
    app.names = {"0123456789abcdef0123456789abcdef": "Alice"}
    app.caller = SimpleNamespace(hash=bytes.fromhex("0123456789abcdef0123456789abcdef"))
    app.telephone = SimpleNamespace(
        answer=lambda caller: actions.append({"kind": "answer"}) or True,
        hangup=lambda: actions.append({"kind": "hangup"}), busy=False)
    app.dial = lambda identity: actions.append({"kind": "dial", "identity": identity})
    result = {
        "revision": "3b596f48ddd252ddc9c315ef8f3020cfd3688223",
        "source_sha256": hashlib.sha256((Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"]) / "LXST" / "Utilities" / "rnphone.py").read_bytes()).hexdigest(),
        "substitutes": "Constructor bypass; fake telephone/display/clock and dial boundary. Actual _keypad_event, became_available and _update_display execute.",
        "events": [], "sleep": [],
    }
    sequence = [
        ("available", "A", "down"), ("available", "1", "down"),
        ("available", "2", "down"), ("available", "B", "down"),
        ("available", "3", "down"), ("available", "C", "down"),
        ("available", "9", "down"), ("available", "D", "down"),
        ("available", "C", "down"), ("available", "1", "down"),
        ("available", "2", "down"), ("available", "D", "down"),
        ("ringing", "D", "down"), ("ringing", "hook", "up"),
        ("ringing", "C", "down"), ("in-call", "hook", "up"),
        ("in-call", "hook", "down"), ("connecting", "D", "down"),
    ]
    for index, (state, key, transition) in enumerate(sequence):
        now[0] = (index + 1) / 10
        app.state = {"available": app.STATE_AVAILABLE, "ringing": app.STATE_RINGING,
                     "in-call": app.STATE_IN_CALL, "connecting": app.STATE_CONNECTING}[state]
        actions.clear()
        app._keypad_event(app.keypad, (key, transition))
        result["events"].append({
            "elapsed": now[0], "state": state, "key": key, "transition": transition,
            "input": app.hw_input, "dialing": app.hw_is_dialing, "actions": list(actions),
            "lines": list(app.display.lines),
        })
    class EndIteration(Exception):
        pass
    def stop_after_iteration(seconds):
        raise EndIteration()
    rnphone.time = SimpleNamespace(time=lambda: now[0], sleep=stop_after_iteration)
    for elapsed in [14.0, 15.0]:
        now[0] = elapsed
        app.state = app.STATE_AVAILABLE
        app.hw_state = app.HW_STATE_IDLE
        app.hw_last_event = 0
        app.display.sleeping = False
        try:
            app._display_job()
        except EndIteration:
            pass
        result["sleep"].append({"elapsed": elapsed, "sleeping": app.display.sleeping})
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
