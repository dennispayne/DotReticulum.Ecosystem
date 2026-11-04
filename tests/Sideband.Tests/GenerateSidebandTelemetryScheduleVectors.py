"""Execute primary periodic jobs with an injected clock and observed dispatch calls."""
import argparse
import ast
import hashlib
import json
from pathlib import Path
import subprocess
from types import MethodType, SimpleNamespace

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
DIGEST = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"
PEER = bytes([2]) * 16
NOW = 1700000000


class Finished(BaseException):
    pass


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    source = subprocess.check_output(["git", "-C", str(args.source), "show", f"{PIN}:sbapp/sideband/core.py"])
    if hashlib.sha256(source).hexdigest() != DIGEST:
        raise RuntimeError("Unexpected primary source")
    if subprocess.check_output(["git", "-C", str(args.source), "rev-parse", "HEAD"], text=True).strip() != PIN:
        raise RuntimeError("Unexpected primary revision")
    core = next(n for n in ast.parse(source).body if isinstance(n, ast.ClassDef) and n.name == "SidebandCore")
    method = next(n for n in core.body if isinstance(n, ast.FunctionDef) and n.name == "_periodic_jobs")
    scenarios = [
        ("initial", True, True, 0, 0, 0, 0),
        ("not-due", True, True, NOW, NOW, 0, 0),
        ("send-boundary", True, False, NOW - 60, 0, 0, 0),
        ("blocked", True, False, 0, 0, 1, NOW + 100),
        ("exhausted", True, False, 0, 0, 2, 0),
        ("last-retry", True, False, 0, 0, 1, 0),
        ("disabled-send", False, True, 0, 0, 0, 0),
        ("request-boundary", False, True, 0, NOW - 60, 0, 0),
    ]
    results = []
    for name, send, request, success, request_success, tries, blocked in scenarios:
        calls = []
        ticks = []
        owner = SimpleNamespace(
            is_service=False, is_standalone=True, owner_service=None, last_telemetry_clean=NOW,
            telemetry_clean_interval=3600, telemetry_send_blocked_until=blocked,
            pending_telemetry_send_try=tries, pending_telemetry_send_maxtries=2,
            live_tracked_objects={PEER: [60, NOW - 61, NOW + 60]},
            lxmf_destination=SimpleNamespace(hash=bytes(16)),
            _update_delivery_limits=lambda: None,
            _last_telemetry_send_success_timebase=lambda _: success,
            getpersistent=lambda _: request_success,
            send_latest_telemetry=lambda **kw: calls.append("send"),
            request_latest_telemetry=lambda **kw: calls.append("track" if kw.get("is_livetrack") else "request"),
        )
        owner.config = {
            "lxmf_periodic_sync": False, "telemetry_enabled": True, "telemetry_send_to_collector": send,
            "telemetry_request_from_collector": request, "telemetry_collector": PEER,
            "telemetry_send_interval": 60, "telemetry_request_interval": 60,
            "telemetry_send_all_to_collector": False,
        }
        clock = SimpleNamespace(now=NOW)

        def sleep(_):
            if ticks:
                raise Finished()
            ticks.append(True)

        namespace = {"time": SimpleNamespace(time=lambda: clock.now, sleep=sleep),
                     "SidebandCore": SimpleNamespace(PERIODIC_JOBS_INTERVAL=60),
                     "RNS": SimpleNamespace(log=lambda *a: None, hexrep=lambda b, **kw: b.hex(),
                                            prettyhexrep=lambda b: b.hex(), prettytime=str, LOG_DEBUG=0,
                                            LOG_EXTREME=0, LOG_WARNING=0, LOG_ERROR=0)}
        exec(compile(ast.Module(body=[method], type_ignores=[]), "core.py:periodic", "exec"), namespace)
        try:
            MethodType(namespace["_periodic_jobs"], owner)()
        except Finished:
            pass
        results.append({"name": name, "now": NOW, "send_enabled": send, "request_enabled": request,
                        "last_send_success": success, "last_request_success": request_success,
                        "attempts": tries, "blocked_until": blocked, "send": "send" in calls,
                        "request": "request" in calls, "track": "track" in calls,
                        "next_attempts": owner.pending_telemetry_send_try,
                        "next_blocked_until": owner.telemetry_send_blocked_until})
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps({"revision": PIN, "source_sha256": DIGEST,
        "method": "Actual AST-extracted SidebandCore._periodic_jobs, one iteration; synthetic clock/dispatch/persistence, no devices or network",
        "vectors": results}, indent=2) + "\n", encoding="utf-8")
    print(f"Executed {len(results)} actual primary schedule observations")


if __name__ == "__main__":
    main()
