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
[textui]
image_loading = manual
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
[textui]
image_loading = never
"""),
        ("no-stamps", """[client]
required_stamp_cost = -1
accept_invalid_stamps = false
max_accepted_size = 1000
[node]
enable_node = 1
disable_propagation = 1
"""),
        ("always-images", """[client]
max_accepted_size = 1000
[textui]
image_loading = always
[node]
enable_node = yes
disable_propagation = yes
"""),
    ]
    root = Path(f"artifacts/tests/nomadnet-config-reference-{os.getpid()}").resolve()
    root.mkdir(parents=True, exist_ok=True)
    vectors = []
    for name, config in cases:
        work = root / name
        app = work / "app"
        rns = work / "rns"
        app.mkdir(parents=True, exist_ok=True)
        rns.mkdir(parents=True, exist_ok=True)
        # The primary parses TextUI options only while selecting user_interface=text;
        # --daemon subsequently suppresses screen startup. This is a real config prerequisite.
        execution_config = config.replace("[client]\n", "[client]\nuser_interface = text\n", 1) if "[client]\n" in config else "[client]\nuser_interface = text\n" + config
        if "[textui]" not in execution_config:
            execution_config += "\n[textui]\n"
        # The pinned primary's missing-colormode branch references a nonexistent
        # nomadnet.ui.COLORMODE_16; explicitly select its ordinary supported mode.
        execution_config = execution_config.replace("[textui]\n", "[textui]\ncolormode = 256\n", 1)
        execution_config += "\n[logging]\nloglevel = 7\ndestination = console\n"
        (app / "config").write_text(execution_config, encoding="utf-8")
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
                        vectors.append({"name": name, "input": config, "executionConfig": execution_config, "observed": value})
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
            raise RuntimeError(f"Actual primary config timed out for {name}: {errors}, {lines}, {process.stderr.read()}") from error
        finally:
            if process.poll() is None:
                process.kill()
            process.wait()
            reader.join(timeout=5)
        if process.returncode or errors or len(vectors) != cases.index((name, config)) + 1:
            raise RuntimeError(f"Actual primary config failed: {errors}, {lines}, {process.stderr.read()}")
    result = {
        "commit": revision,
        "method": "Actual unmodified NomadNetworkApp constructor/applyConfig in isolated no-interface subprocesses; adapter only observes public attributes. executionConfig explicitly selects client.user_interface=text and textui.colormode=256 to exercise TextUI parsing (the missing-colormode branch references nonexistent nomadnet.ui.COLORMODE_16); --daemon suppresses screen startup. Console debug logging exposes configuration errors. Standard PYTHON_CPU_COUNT=2 bounds workers; no constructor/config handler seam.",
        "sourceSha256": hashlib.sha256((source / "nomadnet/NomadNetworkApp.py").read_bytes()).hexdigest(),
        "vectors": vectors,
    }
    Path(sys.argv[1]).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
