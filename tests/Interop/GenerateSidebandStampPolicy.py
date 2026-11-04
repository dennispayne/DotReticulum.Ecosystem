"""Observe actual pinned Sideband startup and public stamp-policy methods.

Runs the unchanged primary daemon core with an isolated no-interface RNS config.
The adapter reads the actual router enforcement flag; it does not replace stamp
generation, admission, announce formatting or any primary method. This is vector
generation, not live-peer, hardware or complete configuration acceptance.
"""
import argparse
import base64
import json
from pathlib import Path
import subprocess
import sys

from reference_environment import verify_reference, PINNED_LXMF_SHA, PINNED_RNS_SHA


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--private-work", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--profile-output", type=Path)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    args.private_work.mkdir(parents=True, exist_ok=False)
    cases = [
        ("defaults", {}),
        ("advertise8", {"lxmf_require_stamps": True, "lxmf_inbound_stamp_cost": 8}),
        ("rejectOff", {"lxmf_ignore_invalid_stamps": False}),
        ("disabledCost", {"lxmf_require_stamps": False, "lxmf_inbound_stamp_cost": 12}),
        ("reenabled", {"lxmf_require_stamps": True}),
        ("zero", {"lxmf_inbound_stamp_cost": 0}),
        ("negative", {"lxmf_inbound_stamp_cost": -1}),
        ("maximum", {"lxmf_inbound_stamp_cost": 254}),
        ("tooHigh", {"lxmf_inbound_stamp_cost": 255}),
        ("boolean", {"lxmf_inbound_stamp_cost": True}),
        ("fractional", {"lxmf_inbound_stamp_cost": 2.5}),
        ("string", {"lxmf_inbound_stamp_cost": "4"}),
        ("noneAndReject", {"lxmf_inbound_stamp_cost": None, "lxmf_ignore_invalid_stamps": True}),
    ]
    commands = [
        "stamp-policy " + base64.b64encode(json.dumps(settings).encode()).decode()
        for _, settings in cases
    ]
    if args.profile_output is not None:
        commands.append("telemetry-profile-snapshot " +
                        base64.b64encode(str(args.profile_output.resolve()).encode()).decode())
    commands.append("quit")
    result = subprocess.run(
        [sys.executable, str(Path(__file__).with_name("sideband_application_node.py")),
         "--source", str(args.source.resolve()), "--config", str(args.private_work.resolve())],
        input="\n".join(commands) + "\n", text=True, capture_output=True, timeout=90, check=False,
    )
    (args.private_work / "reference.stdout").write_text(result.stdout, encoding="utf-8")
    (args.private_work / "reference.stderr").write_text(result.stderr, encoding="utf-8")
    if result.returncode != 0:
        raise RuntimeError(f"Actual primary stamp observer exited {result.returncode}:\n{result.stderr}")
    events = [json.loads(line[2:]) for line in result.stdout.splitlines() if line.startswith("@@")]
    ready = next(event for event in events if event["event"] == "ready")
    observations = [event for event in events if event["event"] == "stamp-policy"]
    if len(observations) != len(cases):
        raise RuntimeError("The actual primary did not report every stamp-policy observation")
    rows = []
    for (name, settings), observation in zip(cases, observations, strict=True):
        rows.append({"name": name, "changes": settings, "observed": observation})
    fixture = {
        "upstream": ready["revision"], "coreSha256": ready["source_sha256"],
        "rnsRevision": PINNED_RNS_SHA, "lxmfRevision": PINNED_LXMF_SHA,
        "method": "Actual SidebandCore constructor, update_ignore_invalid_stamps and lxmf_announce with actual LXMRouter; no-interface RNS; enforcement is a read-only private-field observation.",
        "rows": rows,
    }
    args.output.write_text(json.dumps(fixture, indent=2) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
