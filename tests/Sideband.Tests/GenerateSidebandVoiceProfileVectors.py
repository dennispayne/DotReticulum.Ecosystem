"""Execute primary call-log persistence without constructing audio or network devices."""
import argparse
import hashlib
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
from unittest.mock import patch

from SidebandVoiceGenerateVectors import load_reference
from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    voice, digests = load_reference(args.source.resolve())
    now = 1800000000.0
    clock = [now]
    keep = voice.ReticulumTelephone.CALL_LOG_KEEP
    identity = SimpleNamespace(hash=bytes(range(16)))
    scenarios = [
        ("empty", []),
        ("all-primary-event-types", [(0, event) for event in
         ("outgoing-success", "ongoing-ended", "incoming-success", "outgoing-failure", "incoming-missed")]),
        ("expired", [(-keep - 1, "incoming-missed")]),
        ("strict-retention-boundary", [(-keep, "incoming-missed")]),
        ("inside-retention-boundary", [(-keep + 0.25, "incoming-success")]),
        ("future-is-retained-by-primary", [(10, "outgoing-success")]),
        ("duplicate-events-are-not-deduplicated", [(0, "incoming-missed"), (0, "incoming-missed")]),
    ]
    observations = []
    with tempfile.TemporaryDirectory(prefix="sideband-voice-profile-") as directory:
        with patch.object(voice, "time", SimpleNamespace(time=lambda: clock[0])):
            for index, (name, events) in enumerate(scenarios):
                path = Path(directory) / str(index)
                phone = voice.ReticulumTelephone.__new__(voice.ReticulumTelephone)
                phone.logpath = str(path)
                phone.call_log = None
                phone.clear_call_log()
                for offset, event in events:
                    clock[0] = now + offset
                    phone.log_call(event, identity)
                clock[0] = now
                fresh = voice.ReticulumTelephone.__new__(voice.ReticulumTelephone)
                fresh.logpath = str(path)
                fresh.call_log = None
                entries = fresh.get_call_log()
                observations.append({
                    "name": name, "now": now, "packed_hex": path.read_bytes().hex(),
                    "loaded": [{**entry, "identity": entry["identity"].hex()} for entry in entries],
                })
    result = {
        "revision": "d069122af14880e43607aed0b71add4c7806fbe0",
        "voice_sha256": digests["sbapp/sideband/voice.py"],
        "retention_seconds": keep,
        "method": "Actual ReticulumTelephone.clear_call_log/log_call/get_call_log and owned files. Fresh __new__ instances avoid audio/network construction; fixed clock and hash-only identity descriptor are disclosed synthetic inputs. No source or method is replaced.",
        "observations": observations,
    }
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps({"observations": len(observations),
                      "sha256": hashlib.sha256(args.output.read_bytes()).hexdigest()}))


if __name__ == "__main__":
    main()
