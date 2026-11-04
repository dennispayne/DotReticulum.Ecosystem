"""Execute the unchanged primary startup and mutable delivery-limit policy."""
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
    cases = [("default", {}), ("large", {"lxm_limit_1mb": False}),
             ("limited", {"lxm_limit_1mb": True}), ("retained", {}),
             ("null", {"lxm_limit_1mb": None}), ("zero", {"lxm_limit_1mb": 0}),
             ("one", {"lxm_limit_1mb": 1}), ("string", {"lxm_limit_1mb": "false"})]
    commands = ["delivery-limit " + base64.b64encode(json.dumps(values).encode()).decode()
                for _, values in cases]
    if args.profile_output is not None:
        commands += [
            "delivery-limit " + base64.b64encode(b'{"lxm_limit_1mb":false}').decode(),
            "telemetry-profile-snapshot " + base64.b64encode(str(args.profile_output.resolve()).encode()).decode()]
    result = subprocess.run(
        [sys.executable, str(Path(__file__).with_name("sideband_application_node.py")),
         "--source", str(args.source.resolve()), "--config", str(args.private_work.resolve())],
        input="\n".join(commands + ["quit"]) + "\n", text=True, capture_output=True,
        timeout=90, check=False)
    (args.private_work / "stdout.log").write_text(result.stdout, encoding="utf-8")
    (args.private_work / "stderr.log").write_text(result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError(f"Actual primary delivery-limit observer failed:\n{result.stderr}")
    events = [json.loads(line[2:]) for line in result.stdout.splitlines() if line.startswith("@@")]
    ready = next(event for event in events if event["event"] == "ready")
    observations = [event for event in events if event["event"] == "delivery-limit"]
    if len(observations) != len(cases) + (1 if args.profile_output is not None else 0):
        raise RuntimeError("The actual primary did not observe every delivery policy")
    observations = observations[:len(cases)]
    fixture = {
        "upstream": ready["revision"], "coreSha256": ready["source_sha256"],
        "rnsRevision": PINNED_RNS_SHA, "lxmfRevision": PINNED_LXMF_SHA,
        "method": "Actual SidebandCore constructor, start_reticulum and _update_delivery_limits; actual LXMRouter; isolated no-interface RNS, no algorithm replacements.",
        "rows": [{"name": name, "changes": values, "observed": observation}
                 for (name, values), observation in zip(cases, observations, strict=True)]}
    args.output.write_text(json.dumps(fixture, indent=2) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
