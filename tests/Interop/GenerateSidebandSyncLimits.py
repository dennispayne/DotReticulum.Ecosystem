"""Execute pinned manual-sync UI routing and real core/router admission separately."""
import argparse
import ast
import base64
import hashlib
import json
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

from reference_environment import verify_reference, PINNED_LXMF_SHA, PINNED_RNS_SHA

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
MAIN_SHA = "5b962d9ed2ccbc066bb19865d78c77da9ecf4008900f6f0e7e9dd1eb1c4767aa"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--private-work", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--profile-output", type=Path)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    import RNS
    import LXMF
    source = args.source.resolve()
    blob = subprocess.check_output(["git", "-C", str(source), "show", f"{PIN}:sbapp/main.py"])
    if hashlib.sha256(blob).hexdigest() != MAIN_SHA or (
            source / "sbapp/main.py").read_bytes().replace(b"\r\n", b"\n") != blob:
        raise RuntimeError("Unexpected primary manual-sync UI source")
    tree = ast.parse(blob)
    app = next(node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == "SidebandApp")
    method = next(node for node in app.body if isinstance(node, ast.FunctionDef) and node.name == "lxmf_sync_request")
    namespace = {"RNS": RNS, "LXMF": LXMF,
                 "Clock": SimpleNamespace(schedule_once=lambda callback, delay: callback(delay)),
                 "time": SimpleNamespace(time=lambda: 1700000000)}
    exec(compile(ast.Module(body=[method], type_ignores=[]), "pinned Sideband manual sync method", "exec"), namespace)
    cases = [("default", {}), ("limited", {"lxmf_sync_limit": True}),
             ("changed", {"lxmf_sync_max": 1}), ("disabled", {"lxmf_sync_limit": False}),
             ("retained", {}), ("null", {"lxmf_sync_limit": None}),
             ("zero", {"lxmf_sync_limit": True, "lxmf_sync_max": 0}),
             ("negative", {"lxmf_sync_max": -1}),
             ("string", {"lxmf_sync_max": "3"})]
    configuration = {"lxmf_sync_limit": None, "lxmf_sync_max": 3}
    commands, observations = [], []
    for name, changes in cases:
        configuration.update(changes)
        requests, persistent = [], {}
        progress = SimpleNamespace(value=0, start=lambda: None, stop=lambda: None)
        content = SimpleNamespace(ids=SimpleNamespace(node_info=SimpleNamespace(text=""),
                                  sync_status=SimpleNamespace(text=""), sync_progress=progress))
        router = SimpleNamespace(get_outbound_propagation_node=lambda: bytes(16),
                                 propagation_transfer_state=LXMF.LXMRouter.PR_IDLE)
        owner = SimpleNamespace(sideband=SimpleNamespace(config=configuration, message_router=router,
                                get_sync_status=lambda: "idle", setstate=lambda *args: None,
                                setpersistent=lambda key, value: persistent.__setitem__(key, value),
                                request_lxmf_sync=lambda limit: requests.append(limit)),
                                message_sync_dialog=SimpleNamespace(d_content=content, open=lambda: None),
                                sync_dialog=content)
        namespace["lxmf_sync_request"](owner, None)
        if len(requests) != 1:
            raise RuntimeError("The exact UI method did not schedule exactly one sync")
        requested = requests[0]
        observations.append({"name": name, "changes": changes, "requested": requested,
                             "persistent": persistent})
        commands.append("sync-limit-observe " + base64.b64encode(json.dumps(
            {"changes": changes, "manual": requested}).encode()).decode())
    if args.profile_output is not None:
        commands += ["sync-limit-observe " + base64.b64encode(json.dumps(
            {"changes": {"lxmf_sync_limit": True, "lxmf_sync_max": 2}, "manual": 2}).encode()).decode(),
            "telemetry-profile-snapshot " + base64.b64encode(str(args.profile_output.resolve()).encode()).decode()]
    args.private_work.mkdir(parents=True, exist_ok=False)
    process = subprocess.run([sys.executable, str(Path(__file__).with_name("sideband_application_node.py")),
        "--source", str(source), "--config", str(args.private_work.resolve())],
        input="\n".join(commands + ["quit"]) + "\n", text=True, capture_output=True, timeout=90, check=False)
    (args.private_work / "stdout.log").write_text(process.stdout, encoding="utf-8")
    (args.private_work / "stderr.log").write_text(process.stderr, encoding="utf-8")
    if process.returncode:
        raise RuntimeError(f"Actual primary core observer failed:\n{process.stderr}")
    events = [json.loads(line[2:]) for line in process.stdout.splitlines() if line.startswith("@@")]
    ready = next(event for event in events if event["event"] == "ready")
    received = [event for event in events if event["event"] == "sync-limit-observed"]
    if len(received) != len(commands) - (1 if args.profile_output is not None else 0):
        raise RuntimeError("Missing real-core observations")
    fixture = {"upstream": ready["revision"], "coreSha256": ready["source_sha256"], "mainSha256": MAIN_SHA,
        "rnsRevision": PINNED_RNS_SHA, "lxmfRevision": PINNED_LXMF_SHA,
        "method": "Exact lxmf_sync_request AST with synthetic dialog/clock/owner, then actual SidebandCore constructor and request_lxmf_sync with real LXMRouter, an explicitly unreachable zero-hash propagation address and public cancellation between requests. No Kivy GUI or network claim. Default no-argument request observes periodic routing separately.",
        "rows": [{**row, "observed": observed} for row, observed in zip(observations, received[:len(cases)], strict=True)]}
    args.output.write_text(json.dumps(fixture, indent=2) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
