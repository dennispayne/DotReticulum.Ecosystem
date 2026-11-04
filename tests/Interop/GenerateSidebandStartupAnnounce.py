"""Execute the exact pinned deferred-start method with controlled thread/clock owners."""
import argparse
import ast
import base64
import hashlib
import json
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
DIGEST = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--private-work", type=Path)
    parser.add_argument("--profile-output", type=Path)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    source = args.source.resolve()
    if subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip() != PIN:
        raise RuntimeError("Unexpected Sideband revision")
    blob = subprocess.check_output(["git", "-C", str(source), "show", f"{PIN}:sbapp/sideband/core.py"])
    if hashlib.sha256(blob).hexdigest() != DIGEST or (
            source / "sbapp/sideband/core.py").read_bytes().replace(b"\r\n", b"\n") != blob:
        raise RuntimeError("Unexpected primary deferred-start source")
    tree = ast.parse(blob)
    core = next(node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == "SidebandCore")
    method = next(node for node in core.body if isinstance(node, ast.FunctionDef) and node.name == "__start_jobs_deferred")
    rows = []
    for configured in (True, False, None, 0, 1, "false"):
        for voice in (False, True):
            events = []

            class Thread:
                def __init__(self, target, daemon):
                    self.target = target

                def start(self):
                    self.target()

            namespace = {"threading": SimpleNamespace(Thread=Thread),
                         "time": SimpleNamespace(sleep=lambda delay: events.append({"kind": "sleep", "seconds": delay}),
                                                 time=lambda: 1700000008)}
            exec(compile(ast.Module(body=[method], type_ignores=[]), "pinned deferred startup", "exec"), namespace)
            owner = SimpleNamespace(is_service=False, is_standalone=True, is_client=False,
                config={"start_announce": configured, "telemetry_enabled": False, "voice_enabled": False},
                telephone=SimpleNamespace(announce=lambda: events.append({"kind": "voice"})) if voice else None,
                lxmf_announce=lambda: events.append({"kind": "delivery"}),
                _periodic_jobs=lambda: events.append({"kind": "periodic"}), last_if_change_announce=0)
            namespace["__start_jobs_deferred"](owner)
            rows.append({"configured": configured, "voice": voice, "events": events,
                         "lastInterfaceAnnounce": owner.last_if_change_announce})
    fixture = {"upstream": PIN, "coreSha256": DIGEST,
        "method": "Exact __start_jobs_deferred AST, standalone desktop, synchronous fake threads/clock and fake delivery/telephone observers. Does not execute periodic workers, audio, Android service or actual elapsed time.",
        "rows": rows}
    args.output.write_text(json.dumps(fixture, indent=2) + "\n", encoding="utf-8", newline="\n")
    if args.profile_output is not None:
        if args.private_work is None:
            parser.error("--profile-output requires --private-work")
        args.private_work.mkdir(parents=True, exist_ok=False)
        command = "telemetry-profile-snapshot " + base64.b64encode(str(args.profile_output.resolve()).encode()).decode()
        process = subprocess.run([sys.executable, str(Path(__file__).with_name("sideband_application_node.py")),
            "--source", str(source), "--config", str(args.private_work.resolve()), "--startup-announce", "off"],
            input=command + "\nquit\n", text=True, capture_output=True, timeout=90, check=False)
        (args.private_work / "stdout.log").write_text(process.stdout, encoding="utf-8")
        (args.private_work / "stderr.log").write_text(process.stderr, encoding="utf-8")
        if process.returncode:
            raise RuntimeError(f"Actual primary startup profile failed:\n{process.stderr}")
        events = [json.loads(line[2:]) for line in process.stdout.splitlines() if line.startswith("@@")]
        if not any(event["event"] == "telemetry-profile-snapshotted" for event in events):
            raise RuntimeError("The actual primary did not save its profile snapshot")


if __name__ == "__main__":
    main()
