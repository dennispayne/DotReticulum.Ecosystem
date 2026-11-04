"""Observe pinned primary node configuration through real application constructors."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import threading

from reference_environment import verify_reference, PINNED_RNS_SHA, PINNED_LXMF_SHA


def main():
    verify_reference(require_lxmf=True)
    source = Path(os.environ["DOTRETICULUM_NOMADNET_SOURCE"]).resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2":
        raise RuntimeError("Unexpected primary revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Modified primary source")
    cases = [
        ("defaults", ""),
        ("minimum-clamps", """max_transfer_size = 0.25
max_sync_size = 0.5
message_storage_limit = 0.00001
propagation_cost = 1
max_peers = -2
static_peers =
prioritise_destinations =
"""),
        ("fractional-and-lists", """max_transfer_size = 1.5
max_sync_size = 3.75
message_storage_limit = 0.125
propagation_cost = 19
max_peers = 3
static_peers = 00112233445566778899AABBCCDDEEFF, invalid, 11223344556677889900aabbccddeeff, 00112233445566778899aabbccddeeff
prioritise_destinations = 11223344556677889900AABBCCDDEEFF, invalid, 11223344556677889900aabbccddeeff
"""),
        ("absent-sync-router-clamp", "max_transfer_size = 20000\n"),
        ("reversed-config-order", "max_sync_size = 1\nmax_transfer_size = 4000\n"),
        ("configured-peer-cap-zero", """max_transfer_size = 4000
max_sync_size = 8000
message_storage_limit = 16
propagation_cost = 13
max_peers = 0
static_peers = 00112233445566778899aabbccddeeff,
prioritise_destinations = 11223344556677889900aabbccddeeff
"""),
        ("quoted-byte-whitespace", """static_peers = "00 11 22 33 44 55 66 77 88 99 aa bb cc dd ee ff", "0 0112233445566778899aabbccddeeff"
prioritise_destinations = "11 22 33 44 55 66 77 88 99 00 aa bb cc dd ee ff", "1 1223344556677889900aabbccddeeff"
"""),
    ]
    root = Path(f"artifacts/tests/nomadnet-node-config-reference-{os.getpid()}").resolve()
    vectors = []
    for name, body in cases:
        config = "[node]\nenable_node = yes\ndisable_propagation = yes\n" + body
        execution = "[client]\nuser_interface = text\n" + config
        execution += "\n[textui]\ncolormode = 256\n[logging]\nloglevel = 7\ndestination = console\n"
        work = root / name
        app, rns = work / "app", work / "rns"
        app.mkdir(parents=True)
        rns.mkdir()
        (app / "config").write_text(execution, encoding="utf-8")
        (rns / "config").write_text("[reticulum]\n share_instance = No\n[logging]\n loglevel = 0\n[interfaces]\n", encoding="utf-8")
        environment = dict(os.environ)
        environment["PYTHONPATH"] = str(source) + os.pathsep + environment.get("PYTHONPATH", "")
        environment["PYTHON_CPU_COUNT"] = "2"
        for key in ["TMP", "TEMP", "TMPDIR"]:
            environment[key] = str(work)
        process = subprocess.Popen([sys.executable, "-u", str(Path(__file__).with_name("nomadnet_live_node.py")),
                                    "--config", str(app), "--rnsconfig", str(rns)],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   text=True, env=environment)
        lines, errors, observed = [], [], []

        def commands():
            try:
                for line in process.stdout:
                    lines.append(line)
                    marker = line.find("@@")
                    if marker < 0:
                        continue
                    value, _ = json.JSONDecoder().raw_decode(line[marker + 2:])
                    event = value.get("event")
                    if event == "ready":
                        process.stdin.write("node-config-stats\n")
                        process.stdin.flush()
                    if event == "node-configuration":
                        observed.append(value)
                    if event in ("error", "failed"):
                        errors.append(value)
                    if event in ("node-configuration", "error", "failed"):
                        process.stdin.write("quit\n")
                        process.stdin.flush()
            except Exception as error:
                errors.append(str(error))

        reader = threading.Thread(target=commands)
        reader.start()
        try:
            process.wait(timeout=45)
        except subprocess.TimeoutExpired as error:
            process.kill()
            process.wait()
            reader.join(timeout=5)
            raise RuntimeError(f"Primary node configuration timed out: {name}, {lines}, {errors}, {process.stderr.read()}") from error
        finally:
            if process.poll() is None:
                process.kill()
            process.wait()
            reader.join(timeout=5)
        if process.returncode or errors or len(observed) != 1:
            raise RuntimeError(f"Primary node configuration failed: {name}, {lines}, {errors}, {process.stderr.read()}")
        vectors.append({"name": name, "input": config, "executionConfig": execution, "observed": observed[0]})
    result = {
        "commit": revision,
        "sourceSha256": hashlib.sha256((source / "nomadnet/NomadNetworkApp.py").read_bytes()).hexdigest(),
        "sourceBlobSha256": hashlib.sha256(subprocess.check_output(
            ["git", "-C", str(source), "show", "HEAD:nomadnet/NomadNetworkApp.py"])).hexdigest(),
        "rnsCommit": PINNED_RNS_SHA,
        "lxmfCommit": PINNED_LXMF_SHA,
        "method": "Actual unchanged NomadNetworkApp constructor, ConfigObj/apply_config and actual pinned LXMF router; isolated subprocess per vector with no network interfaces. Adapter only observes configuration and effective router values. Execution config explicitly selects text/colormode256 and console logging; daemon suppresses screen startup. No config/router handler seam.",
        "vectors": vectors,
    }
    Path(sys.argv[1]).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
