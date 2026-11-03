"""Execute primary 0.5.4 mode/PTT and combined signalling APIs; no audio devices."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import _verify_checkout, verify_reference
import LXST
import RNS
from LXST._version import __version__
from LXST.Network import SignallingReceiver
from LXST.Primitives.Telephony import Profiles, Signalling, Telephone
from RNS.vendor import umsgpack as mp

REVISION = "eba5abc77fef7fef3bcb6c9c091c80b7f7492c9f"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference()
    root = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"])
    _verify_checkout(LXST, root, "LXST", REVISION, allow_filter_build=True)
    if __version__ != "0.5.4":
        raise RuntimeError("The inspected primary revision must report 0.5.4")
    result = {
        "revision": REVISION, "version": __version__,
        "source_sha256": {name: hashlib.sha256((root / "LXST" / name).read_bytes()).hexdigest()
                          for name in ["Network.py", "Primitives/Telephony.py", "Filters.py", "Sources.py",
                                       "Primitives/Players.py"]},
        "substitutes": "Constructor bypass; fake active link, packetizer and AGC. Actual mode selection/switch/PTT APIs execute. RNS.Packet.send is captured only for packet vectors.",
        "modes": [{"value": mode, "name": Profiles.mode_name(mode),
                   "abbreviation": Profiles.mode_abbrevation(mode)}
                  for mode in Profiles.available_modes()],
        "buffers": [{"profile": profile, "frames": Profiles.get_buffer_frames(profile)}
                    for profile in Profiles.available_profiles()],
        "signals": [], "steps": [],
    }
    original_packet = RNS.Packet
    class CapturePacket:
        def __init__(self, destination, data, **kwargs):
            self.data = data
        def send(self):
            result["signals"].append(self.data.hex())
    try:
        RNS.Packet = CapturePacket
        receiver = SignallingReceiver()
        for value in [7, [319, 241], [], [[1, 2]], [None, "hello"]]:
            receiver.signal(value, object())
    finally:
        RNS.Packet = original_packet
    phone = Telephone.__new__(Telephone)
    for row in result["buffers"]:
        waits = []
        phone.target_buffer_frames = row["frames"]
        phone.audio_output = SimpleNamespace(buffer_max_height=3, autostart_min=1, streaming=False,
                                            wait_for_frames=lambda: waits.append(True))
        phone._Telephone__update_output_buffer_targets()
        row.update({"maximum_height": phone.audio_output.buffer_max_height,
                    "start_frames": phone.audio_output.autostart_min,
                    "streaming": phone.audio_output.streaming, "waits": len(waits)})
    signals = []
    packetizer = SimpleNamespace(squelched=False)
    packetizer.squelch = lambda: setattr(packetizer, "squelched", True)
    packetizer.unsquelch = lambda: setattr(packetizer, "squelched", False)
    phone.active_call = SimpleNamespace(call_mode=None, packetizer=packetizer,
                                        filter_agc=SimpleNamespace(paused=False))
    phone.call_status = Signalling.STATUS_ESTABLISHED
    phone.signal = lambda value, link: signals.append(value)
    operations = [
        ("initial-half", lambda: phone._Telephone__select_call_mode(Profiles.MODE_HALF_DUPLEX)),
        ("press", phone.unsquelch_transmit),
        ("release", phone.squelch_transmit),
        ("full", lambda: phone.switch_mode(Profiles.MODE_FULL_DUPLEX)),
        ("remote-half", lambda: phone.switch_mode(Profiles.MODE_HALF_DUPLEX, from_signalling=True)),
        ("disable-follow", phone.disable_remote_mode_follow),
        ("ignored-remote-full", lambda: phone.switch_mode(Profiles.MODE_FULL_DUPLEX, from_signalling=True)),
        ("local-full", lambda: phone.switch_mode(Profiles.MODE_FULL_DUPLEX)),
    ]
    for name, operation in operations:
        signals.clear()
        operation()
        result["steps"].append({"name": name, "mode": phone.active_mode,
                                "squelched": packetizer.squelched,
                                "agc_paused": phone.active_call.filter_agc.paused,
                                "signals": list(signals)})
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
