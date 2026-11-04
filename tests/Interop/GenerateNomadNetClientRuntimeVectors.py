"""Execute primary client configuration/notification methods and CLI argument selection."""
import argparse
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time

from reference_environment import verify_reference

REVISION = "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2"
SOURCES = ("nomadnet/NomadNetworkApp.py", "nomadnet/nomadnet.py", "nomadnet/ui/__init__.py",
           "nomadnet/ui/NoneUI.py", "nomadnet/ui/MenuUI.py")


def reference():
    verify_reference(require_lxmf=True)
    source = Path(os.environ["DOTRETICULUM_NOMADNET_SOURCE"]).resolve()
    if subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip() != REVISION:
        raise RuntimeError("Unexpected primary revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Modified primary source")
    import nomadnet
    import LXMF
    import RNS
    if Path(nomadnet.__file__).resolve().parent.parent != source:
        raise RuntimeError("Wrong imported primary module")
    return source, {"nomadnet": str(Path(nomadnet.__file__).resolve()),
                    "LXMF": str(Path(LXMF.__file__).resolve()), "RNS": str(Path(RNS.__file__).resolve())}


def observe(args):
    source, imports = reference()
    from nomadnet.NomadNetworkApp import NomadNetworkApp
    from nomadnet import nomadnet as cli
    from RNS.vendor.configobj import ConfigObj
    result = {}

    def observations():
        try:
            deadline = time.monotonic() + 30
            app = None
            while time.monotonic() < deadline:
                try:
                    app = NomadNetworkApp.get_shared_instance()
                except UnboundLocalError:
                    pass
                if app is not None and hasattr(app, "ui"):
                    break
                time.sleep(0.02)
            if app is None or not hasattr(app, "ui"):
                raise RuntimeError("Actual primary constructor did not reach NoneUI")
            result["constructor_daemon_ui"] = app.uimode
            # Reapply the actual configuration after --daemon's constructor override.
            # This observes text-mode notification without starting an Urwid screen.
            app.config = ConfigObj(app.configpath)
            app.applyConfig()
            result["configured_ui"] = app.uimode
            result["notify_on_new_message"] = app.notify_on_new_message
            captured = io.StringIO()
            with contextlib.redirect_stdout(captured):
                if app.notify_on_new_message:
                    app.notify_message_recieved()
            result["notification"] = captured.getvalue()
            command_lines = ([], ["--daemon"], ["-d", "-t"], ["--textui", "--daemon"],
                             ["-d", "-c"], ["--console"], ["-c", "-d", "-t"])
            selected = []
            original = cli.program_setup
            original_argv = sys.argv
            try:
                for command in command_lines:
                    captured_setup = []
                    cli.program_setup = lambda *values: captured_setup.append(values)
                    sys.argv = ["nomadnet"] + command
                    cli.main()
                    if len(captured_setup) != 1:
                        raise RuntimeError("Primary CLI did not select one setup")
                    selected.append({"args": command, "daemon": captured_setup[0][2],
                                     "force_console": captured_setup[0][3]})
            finally:
                cli.program_setup = original
                sys.argv = original_argv
            result["cli"] = selected
            print("@@" + json.dumps({"observed": result, "imports": imports}), flush=True)
            os._exit(0)
        except Exception as error:
            print("@@" + json.dumps({"error": str(error)}), flush=True)
            os._exit(1)

    threading.Thread(target=observations, daemon=True).start()
    NomadNetworkApp(configdir=args.profile, rnsconfigdir=args.rnsconfig, daemon=True, force_console=True)


def generate(args):
    source, imports = reference()
    work = Path(args.work).resolve()
    work.mkdir(parents=True, exist_ok=True)
    vectors = []
    for name, mode, notify in (("text-default-notify", "text", None), ("text-muted", "text", "no"),
                               ("text-notify", "TeXt", "yes"), ("none-default-notify", "none", None),
                               ("none-muted", "none", "off"), ("none-notify", "NoNe", "on")):
        config = f"[client]\nuser_interface = {mode}\n"
        if notify is not None:
            config += f"notify_on_new_message = {notify}\n"
        execution_config = config + ("announce_at_start = no\nperiodic_lxmf_sync = no\n"
                                    "[textui]\ncolormode = 256\n"
                                    "[node]\nenable_node = no\n[logging]\nloglevel = 7\ndestination = console\n")
        case = work / name
        profile, rns = case / "profile", case / "rns"
        profile.mkdir(parents=True, exist_ok=True)
        rns.mkdir(parents=True, exist_ok=True)
        (profile / "config").write_text(execution_config, encoding="utf-8")
        (rns / "config").write_text("[reticulum]\nshare_instance=No\n[logging]\nloglevel=0\n[interfaces]\n", encoding="utf-8")
        environment = dict(os.environ)
        for key in ("TMP", "TEMP", "TMPDIR"):
            environment[key] = str(case)
        environment["PYTHON_CPU_COUNT"] = "2"
        process = subprocess.run([sys.executable, "-u", str(Path(__file__).resolve()), "--observe",
                                  "--profile", str(profile), "--rnsconfig", str(rns)],
                                 env=environment, capture_output=True, text=True, timeout=60, check=False)
        (case / "stdout").write_text(process.stdout, encoding="utf-8")
        (case / "stderr").write_text(process.stderr, encoding="utf-8")
        events = [json.loads(line[2:]) for line in process.stdout.splitlines() if line.startswith("@@")]
        if process.returncode or len(events) != 1 or "observed" not in events[0]:
            raise RuntimeError(f"Actual primary observation failed: {case}")
        vectors.append({"name": name, "input": config, "executionConfig": execution_config,
                        "observed": events[0]["observed"]})
    blobs = {name: hashlib.sha256(subprocess.check_output(
        ["git", "-C", str(source), "show", f"{REVISION}:{name}"])).hexdigest() for name in SOURCES}
    result = {"commit": REVISION, "sourceBlobSha256": blobs,
              "method": "Actual unchanged NomadNetworkApp constructor reaches NoneUI with daemon=True; "
                        "then unchanged applyConfig and notify_message_recieved run. Captured stdout observes the BEL, "
                        "not an Urwid screen or hardware. Actual unchanged CLI main executes with program_setup "
                        "replaced only by an argument observer, not a network/UI startup. No application source changes.",
              "vectors": vectors}
    with Path(args.output).open("w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(result, indent=2) + "\n")
    (work / "imports.json").write_text(json.dumps(imports, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output")
    parser.add_argument("--work")
    parser.add_argument("--observe", action="store_true")
    parser.add_argument("--profile")
    parser.add_argument("--rnsconfig")
    args = parser.parse_args()
    if args.observe:
        observe(args)
    elif args.output and args.work:
        generate(args)
    else:
        parser.error("--output and --work are required")


if __name__ == "__main__":
    main()
