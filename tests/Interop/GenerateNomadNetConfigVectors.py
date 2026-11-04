"""Observe actual primary application configuration, including its real constructor."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import threading

from reference_environment import verify_reference


def main():
    verify_reference(require_lxmf=True)
    source = Path(os.environ["DOTRETICULUM_NOMADNET_SOURCE"]).resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2":
        raise RuntimeError("Unexpected primary revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Modified primary source")
    cases = [
        ("ordinary", """[client]
enable_client = no
compose_in_markdown = no
announce_at_start = off
announce_interval = 45
periodic_lxmf_sync = no
lxmf_sync_interval = 12
lxmf_sync_limit = 3
try_propagation_on_send_fail = off
required_stamp_cost = 13
accept_invalid_stamps = yes
max_accepted_size = 432.5
downloads_path = ~/Downloads
[node]
enable_node = yes
disable_propagation = yes
node_name = "Public = # Config"
announce_interval = 19
announce_at_start = yes
page_refresh_interval = 2
file_refresh_interval = 3
[rrc]
render_markdown = off
render_micron = no
"""),
        ("clamped", """[client]
enable_client = yes
compose_in_markdown = yes
announce_at_start = yes
announce_interval = -1
lxmf_sync_interval = 0
lxmf_sync_limit = -2
required_stamp_cost = 300
max_accepted_size = -5
[node]
enable_node = true
disable_propagation = on
node_name = None
announce_interval = 0
announce_at_start = no
page_refresh_interval = -1
file_refresh_interval = -4
[rrc]
render_markdown = yes
render_micron = yes
"""),
        ("no-stamps", """[client]
required_stamp_cost = -1
accept_invalid_stamps = false
max_accepted_size = 1000
[node]
enable_node = 1
disable_propagation = 1
"""),
    ]
    root = Path("artifacts/tests/nomadnet-config-reference").resolve()
    root.mkdir(parents=True, exist_ok=True)
    vectors = []
    for name, config in cases:
        work = root / name
        app = work / "app"
        rns = work / "rns"
        app.mkdir(parents=True, exist_ok=True)
        rns.mkdir(parents=True, exist_ok=True)
        (app / "config").write_text(config, encoding="utf-8")
        (rns / "config").write_text("[reticulum]\n share_instance = No\n[logging]\n loglevel = 0\n[interfaces]\n", encoding="utf-8")
        environment = dict(os.environ)
        environment["PYTHONPATH"] = str(source) + os.pathsep + environment.get("PYTHONPATH", "")
        for key in ["TMP", "TEMP", "TMPDIR"]:
            environment[key] = str(work)
        environment["PYTHON_CPU_COUNT"] = "2"
        process = subprocess.Popen([sys.executable, "-u", str(Path(__file__).with_name("nomadnet_live_node.py")),
                                    "--config", str(app), "--rnsconfig", str(rns)],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   text=True, env=environment)
        lines, errors = [], []
        def commands():
            try:
                for line in process.stdout:
                    lines.append(line)
                    if not line.startswith("@@"):
                        continue
                    value = json.loads(line[2:])
                    if value.get("event") == "ready":
                        process.stdin.write("config-stats\n")
                        process.stdin.flush()
                    if value.get("event") in ("error", "failed"):
                        errors.append(value)
                        process.stdin.write("quit\n")
                        process.stdin.flush()
                    if value.get("event") == "configuration":
                        vectors.append({"name": name, "input": config, "observed": value})
                        process.stdin.write("quit\n")
                        process.stdin.flush()
            except Exception as error:
                errors.append(str(error))
        reader = threading.Thread(target=commands)
        reader.start()
        try:
            process.wait(timeout=45)
        finally:
            if process.poll() is None:
                process.kill()
            process.wait()
            reader.join(timeout=5)
        if process.returncode or errors or len(vectors) != cases.index((name, config)) + 1:
            raise RuntimeError(f"Actual primary config failed: {errors}, {lines}, {process.stderr.read()}")
    result = {
        "commit": revision,
        "method": "Actual unmodified NomadNetworkApp constructor/applyConfig in isolated no-interface subprocesses; adapter only observes public attributes. Standard PYTHON_CPU_COUNT=2 bounds workers; no constructor/config handler seam.",
        "sourceSha256": hashlib.sha256((source / "nomadnet/NomadNetworkApp.py").read_bytes()).hexdigest(),
        "vectors": vectors,
    }
    Path(sys.argv[1]).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
