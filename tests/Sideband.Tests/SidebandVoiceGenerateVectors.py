"""Observe pinned Sideband voice policies/callbacks without opening audio devices."""
import argparse
import ast
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import threading
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference
import RNS

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"


def load_reference(source):
    revision = subprocess.check_output(
        ["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != PIN:
        raise RuntimeError(f"Expected {PIN}, got {revision}")
    files = ("sbapp/sideband/voice.py", "sbapp/sideband/core.py", "sbapp/ui/voice.py")
    digests = {name: hashlib.sha256((source / name).read_bytes()).hexdigest() for name in files}
    if digests["sbapp/sideband/core.py"] != "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25":
        raise RuntimeError("Unexpected primary core.py")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain",
                                "--", *files], text=True).strip():
        raise RuntimeError("Reference voice sources are modified")
    spec = importlib.util.spec_from_file_location("sideband_reference_voice", source / files[0])
    voice = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(voice)
    return voice, digests


def observe_call_log(voice, directory):
    directory.mkdir(parents=True, exist_ok=False)
    path = directory / "call_log"
    clock = [1800000000.0]
    identity = SimpleNamespace(hash=bytes(range(16)))

    def telephone():
        result = voice.ReticulumTelephone.__new__(voice.ReticulumTelephone)
        result.logpath = str(path)
        result.call_log = None
        return result

    def entries(values):
        return [{**entry, "identity": entry["identity"].hex()} for entry in values]

    try:
        with patch.object(voice, "time", SimpleNamespace(time=lambda: clock[0])):
            phone = telephone()
            initial = entries(phone.get_call_log())
            logged = []
            for event in ("outgoing-success", "ongoing-ended", "incoming-success",
                          "outgoing-failure", "incoming-missed"):
                logged.append(phone.log_call(event, identity))
                clock[0] += 1
            before_restart = entries(phone.get_call_log())
            restarted = telephone()
            after_restart = entries(restarted.get_call_log())
            clear_result = restarted.clear_call_log()
            packed_empty = path.read_bytes().hex()
            after_clear = entries(restarted.get_call_log())
            after_clear_restart = entries(telephone().get_call_log())
        return {
            "method": "Actual ReticulumTelephone.log_call/get_call_log/clear_call_log with a real owned file; __new__ avoids audio/network construction. Fixed clock and hash-only identity descriptor are disclosed oracle inputs. Restart means a fresh instance with call_log=None reading the same file.",
            "initial": initial, "log_results": logged,
            "before_restart": before_restart, "restart_loaded": after_restart,
            "clear_result": clear_result, "packed_empty_hex": packed_empty,
            "after_clear": after_clear, "restart_after_clear": after_clear_restart,
        }
    finally:
        path.unlink(missing_ok=True)
        directory.rmdir()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    voice, digests = load_reference(args.source)
    telephone_module = sys.modules[voice.Telephone.__module__]
    telephony_path = Path(telephone_module.__file__)
    digests["LXST/Primitives/Telephony.py"] = hashlib.sha256(telephony_path.read_bytes().replace(b"\r\n", b"\n")).hexdigest()
    core_tree = ast.parse((args.source / "sbapp/sideband/core.py").read_text(encoding="utf-8"))
    trust = next(node for node in ast.walk(core_tree)
                 if isinstance(node, ast.FunctionDef) and node.name == "voice_is_trusted")
    namespace = {"RNS": RNS}
    exec(compile(ast.Module(body=[trust], type_ignores=[]), "core.py:voice_is_trusted", "exec"), namespace)
    defaults = {}
    for node in ast.walk(core_tree):
        if isinstance(node, ast.Assign) and isinstance(node.value, ast.Constant):
            for target in node.targets:
                if isinstance(target, ast.Subscript) and ast.unparse(target.value) == "self.config":
                    key = target.slice.value if isinstance(target.slice, ast.Constant) else None
                    if key and key.startswith("voice_"):
                        defaults[key] = node.value.value

    peer = RNS.Identity()
    identity_hash = peer.hash
    delivery = RNS.Destination.hash_from_name_and_identity("lxmf.delivery", identity_hash)
    policies = []
    for trusted_only in (False, True):
        for delivery_trust in (None, False, True):
            for voice_trust in (False, True):
                rows = {identity_hash: {"trust": int(voice_trust)}}
                if delivery_trust is not None:
                    rows[delivery] = {"trust": int(delivery_trust)}
                owner = SimpleNamespace(config={"voice_trusted_only": trusted_only},
                                        _db_conversation=lambda key: rows.get(key))
                owner.voice_is_trusted = lambda key: namespace["voice_is_trusted"](owner, key)
                phone = voice.ReticulumTelephone.__new__(voice.ReticulumTelephone)
                phone.owner = owner
                policies.append({"trusted_only": trusted_only, "delivery_trust": delivery_trust,
                                 "voice_trust": voice_trust, "allowed": phone._ReticulumTelephone__is_allowed(identity_hash)})
    direct_ptt = []
    for enabled in (False, True):
        for trusted in (False, True):
            for ptt in (False, True):
                owner = SimpleNamespace(config={"voice_hdx_direct_ptt": enabled},
                                        is_trusted=lambda key: key == delivery and trusted,
                                        ptt_enabled=lambda key: key == delivery and ptt)
                phone.owner = owner
                direct_ptt.append({"enabled": enabled, "delivery_trusted": trusted,
                                   "ptt_enabled": ptt, "allowed": phone.direct_ptt_allowed(peer)})

    events = []
    transitions = []
    owner = SimpleNamespace(config={"voice_hdx_direct_ptt": False}, incoming_call=lambda peer: None,
                            missed_call=lambda peer: None, ended_call=lambda peer: None,
                            setstate=lambda *args: None)
    phone.owner = owner
    phone.hw_state = phone.HW_STATE_IDLE
    phone.telephone = SimpleNamespace(active_mode=voice.Profiles.MODE_FULL_DUPLEX)
    phone.log_call = lambda event, peer: events.append(event)
    for name, initial, establish in (
            ("outgoing-success", phone.STATE_CONNECTING, True),
            ("incoming-success", phone.STATE_RINGING, True),
            ("outgoing-failure", phone.STATE_CONNECTING, False),
            ("incoming-missed", phone.STATE_RINGING, False)):
        phone.state = initial
        phone.caller = peer
        phone.user_hung_up = False
        phone.direction = "to" if initial == phone.STATE_CONNECTING else "from"
        events.clear()
        if establish:
            phone.call_established(peer)
        established_state = phone.state
        phone.call_ended(peer)
        transitions.append({"name": name, "initial": initial, "established_state": established_state,
                            "final_state": phone.state, "events": list(events)})
    ringer_observations = []
    work = args.output.parent / "sideband-voice-ringer-oracle"
    work.mkdir(parents=True, exist_ok=True)
    supplied_file = work / "synthetic-placeholder"
    supplied_file.write_bytes(b"test oracle path; media reading is stubbed")
    originals = {name: getattr(telephone_module, name) for name in ("LineSink", "OpusFileSource", "Pipeline")}
    try:
        for incoming, early_answer in ((True, False), (True, True), (False, False)):
            observations = []
            started = threading.Event()
            stopped = threading.Event()

            class Source:
                def __init__(self, path, loop, target_frame_ms):
                    observations.append({"loop": loop, "frame_ms": target_frame_ms})

                def stop(self):
                    stopped.set()

            class Pipeline:
                def __init__(self, source, codec, sink):
                    self.running = False

                def start(self):
                    self.running = True
                    started.set()

            telephone_module.LineSink = lambda preferred_device: observations.append({"device": preferred_device})
            telephone_module.OpusFileSource = Source
            telephone_module.Pipeline = Pipeline
            oracle = voice.Telephone.__new__(voice.Telephone)
            oracle.ringtone_path = str(supplied_file)
            oracle.ringer_pipeline = None
            oracle.ringer_output = None
            oracle.ringer_device = "oracle-ringer"
            oracle.ringer_lock = threading.Lock()
            oracle.active_call = SimpleNamespace(is_incoming=incoming)
            oracle.call_status = telephone_module.Signalling.STATUS_RINGING
            oracle._Telephone__activate_ring_tone()
            if early_answer:
                oracle.call_status = telephone_module.Signalling.STATUS_ESTABLISHED
            premature = started.wait(.4)
            if incoming and not early_answer:
                if not started.wait(2):
                    raise RuntimeError("Actual reference ringer did not start")
                oracle.call_status = telephone_module.Signalling.STATUS_ESTABLISHED
            if not stopped.wait(2):
                raise RuntimeError("Actual reference ringer did not stop")
            ringer_observations.append({"incoming": incoming, "early_answer": early_answer,
                                        "started_before_400ms": premature, "started": started.is_set(),
                                        "stopped": stopped.is_set(), "construction": observations})
    finally:
        for name, original in originals.items():
            setattr(telephone_module, name, original)
        supplied_file.unlink()
        work.rmdir()
    ring_tree = ast.parse(telephony_path.read_text(encoding="utf-8"))
    activate = next(node for node in ast.walk(ring_tree)
                    if isinstance(node, ast.FunctionDef) and node.name == "__activate_ring_tone")
    delays = [node.args[0].value for node in ast.walk(activate) if isinstance(node, ast.Call)
              and ast.unparse(node.func) == "time.sleep" and isinstance(node.args[0], ast.Constant)]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    call_log = observe_call_log(voice, args.output.parent / "sideband-voice-call-log-oracle")
    args.output.write_text(json.dumps({
        "sideband_sha": PIN, "source_sha256": digests,
        "method": "Actual ReticulumTelephone policy/callback methods and unchanged core voice_is_trusted AST. Fake owner/database/telephone; no devices, network or GUI. Constants read from imported class; config assignments observed via AST.",
        "constants": {name: getattr(voice.ReticulumTelephone, name) for name in (
            "STATE_AVAILABLE", "STATE_CONNECTING", "STATE_RINGING", "STATE_IN_CALL",
            "RING_TIME", "WAIT_TIME", "PATH_TIME", "CALL_LOG_KEEP")},
        "defaults": defaults, "policies": policies, "direct_ptt": direct_ptt, "transitions": transitions,
        "destination_vector": {
            "identity": bytes(range(16)).hex(),
            "delivery": RNS.Destination.hash_from_name_and_identity("lxmf.delivery", bytes(range(16))).hex(),
        },
        "call_log": call_log,
        "ringer": {
            "method": "Actual Telephone.__activate_ring_tone with fake LineSink/OpusFileSource/Pipeline; real thread and waits, synthetic file-existence placeholder. No upstream audio asset or device is read.",
            "initial_delay_seconds": delays[0], "poll_seconds": delays[1], "observations": ringer_observations,
        },
    }, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
