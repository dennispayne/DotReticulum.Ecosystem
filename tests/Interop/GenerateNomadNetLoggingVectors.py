"""Observe pinned NomadNet configuration/CLI and actual RNS logging, without a UI."""
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

from GenerateNomadNetClientRuntimeVectors import reference, REVISION


def observe(args):
    source, imports = reference()
    import RNS
    from RNS.vendor.configobj import ConfigObj
    from nomadnet.NomadNetworkApp import NomadNetworkApp
    from nomadnet import nomadnet as cli

    def observations():
        try:
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                app = NomadNetworkApp._shared_instance
                if app is not None and hasattr(app, "ui"):
                    break
                time.sleep(0.02)
            if app is None or not hasattr(app, "ui"):
                raise RuntimeError("Actual primary constructor did not reach NoneUI")
            constructor = {"level": RNS.loglevel, "destination": "file" if RNS.logdest == RNS.LOG_FILE else "stdout",
                           "defaultFile": Path(app.logfilepath) == Path(args.profile) / "logfile"}
            # Reset only the process logger to the deterministic RNS config baseline;
            # execute unchanged applyConfig, including its missing-option behavior.
            RNS.loglevel = 2
            RNS.logdest = RNS.LOG_STDOUT
            RNS.logfile = None
            RNS.logtimestamps = False
            RNS._always_override_destination = False
            app.force_console_log = args.force_console
            app.logfilepath = str(Path(args.profile) / "logfile")
            app.config = ConfigObj(str(Path(args.profile) / "config"))
            app.applyConfig()
            level = RNS.loglevel
            destination = "file" if RNS.logdest == RNS.LOG_FILE else "stdout"
            selected = RNS.logfile
            output = io.StringIO()
            # Avoid constructor noise in the observed append target.
            if selected and Path(selected).is_file():
                Path(selected).unlink()
            with contextlib.redirect_stdout(output):
                for severity in range(9):
                    RNS.log("probe-" + str(severity), severity)
            file_output = Path(selected).read_text() if selected and Path(selected).is_file() else ""
            rotation_path = Path(args.profile) / "rotation-probe"
            RNS.logdest = RNS.LOG_FILE
            RNS.logfile = str(rotation_path)
            RNS.loglevel = 7
            with rotation_path.open("wb") as stream:
                stream.truncate(RNS.LOG_MAXSIZE)
            RNS.log("rotate-probe", RNS.LOG_NOTICE)
            rotation = {"maximumBytes": RNS.LOG_MAXSIZE,
                        "rotatedAfterExceeding": not rotation_path.exists() and Path(str(rotation_path) + ".1").exists()}
            RNS.log("after-rotation", RNS.LOG_NOTICE)
            rotation["recreated"] = rotation_path.is_file()
            # Execute the reference's failure policy in a bounded daemon observer:
            # pinned RNS recursively calls log while holding a non-reentrant lock.
            failed = io.StringIO()
            RNS.loglevel = 7
            RNS.logdest = RNS.LOG_FILE
            RNS.logfile = str(Path(args.profile))
            worker = threading.Thread(target=lambda: RNS.log("failure-probe", RNS.LOG_ERROR), daemon=True)
            with contextlib.redirect_stdout(failed):
                worker.start()
                worker.join(1)
            failures = {"visibleWriteError": "Exception occurred while writing log message to log file:" in failed.getvalue(),
                        "emergencyConsoleFallback": "Dumping future log events to console!" in failed.getvalue(),
                        "originalMessageRetried": "failure-probe" in failed.getvalue(),
                        "completed": not worker.is_alive(),
                        "fallbackFlagSet": RNS._always_override_destination}
            cli_cases = []
            original, argv = cli.program_setup, sys.argv
            try:
                for command in ([], ["-c"], ["--console"], ["-d"], ["-d", "-c"],
                                ["--console", "--daemon"], ["-c", "-d", "-t"],
                                ["-t", "--daemon"], ["--daemon", "--textui", "--console"]):
                    captured = []
                    cli.program_setup = lambda *values: captured.append(values)
                    sys.argv = ["nomadnet"] + command
                    cli.main()
                    cli_cases.append({"args": command, "daemon": captured[0][2], "forceConsole": captured[0][3]})
            finally:
                cli.program_setup, sys.argv = original, argv
            print("@@" + json.dumps({"observed": {"constructor": constructor, "level": level,
                  "destination": destination, "defaultFile": selected == str(Path(args.profile) / "logfile"),
                  "customFile": selected if selected and selected != str(Path(args.profile) / "logfile") else None,
                  "stdout": output.getvalue(), "file": file_output, "failure": failures,
                  "rotation": rotation, "cli": cli_cases}, "imports": imports}), flush=True)
            os._exit(0)
        except Exception as error:
            print("@@" + json.dumps({"error": str(error)}), flush=True)
            os._exit(1)

    threading.Thread(target=observations, daemon=True).start()
    NomadNetworkApp(configdir=args.profile, rnsconfigdir=args.rnsconfig,
                    daemon=True, force_console=False)


def generate(args):
    source, imports = reference()
    work = Path(args.work).resolve()
    work.mkdir(parents=True, exist_ok=True)
    cases = [
        ("generated-default", None, False),
        ("absent", "", False),
        ("level-only", "[logging]\nloglevel=6\n", False),
        ("low-clamp", "[logging]\nloglevel=-99\ndestination=console\n", False),
        ("high-clamp", "[logging]\nloglevel=99\ndestination=FiLe\n", False),
        ("explicit-info", "[logging]\nloglevel=4\ndestination=file\n", False),
        ("console", "[logging]\nloglevel=7\ndestination=console\nlogfile=ignored.log\n", False),
        ("unknown-console", "[logging]\nloglevel=3\ndestination=other\n", False),
        ("relative-file", "[logging]\nloglevel=5\ndestination=file\nlogfile=relative.log\n", False),
        ("tilde-is-literal", "[logging]\nloglevel=1\ndestination=file\nlogfile=~/literal.log\n", False),
        ("file-option-alone", "[logging]\nlogfile=ignored.log\n", False),
        ("forced", "[logging]\nloglevel=4\ndestination=file\nlogfile=ignored.log\n", True),
        ("huge-level", "[logging]\nloglevel=999999999999999999999999999999\ndestination=console\n", False),
        ("unicode-level", "[logging]\nloglevel=٣\ndestination=console\n", False),
        ("astral-level", "[logging]\nloglevel=𝟟\ndestination=console\n", False),
        ("underscore-level", "[logging]\nloglevel=+0_7\ndestination=console\n", False),
    ]
    vectors = []
    import_observations = []
    from nomadnet.NomadNetworkApp import __default_nomadnet_config__
    from RNS.vendor.configobj import ConfigObj
    for name, config, force in cases:
        case = work / name
        profile, rns = case / "profile", case / "rns"
        profile.mkdir(parents=True, exist_ok=True)
        rns.mkdir(parents=True, exist_ok=True)
        (case / "~").mkdir(exist_ok=True)
        # Execute ConfigObj on the exact shipped defaults, with only the UI switched
        # to none so a hardware terminal is not needed.
        original_config = "\n".join(__default_nomadnet_config__) + "\n" if config is None else config
        fixture_input = original_config
        if config is None:
            logging_only = ConfigObj()
            logging_only["logging"] = ConfigObj(__default_nomadnet_config__)["logging"].dict()
            fixture_input = "\n".join(logging_only.write()) + "\n"
        execution = original_config + ("\n[client]\nuser_interface=none\nannounce_at_start=no\n"
                                      "periodic_lxmf_sync=no\n[node]\nenable_node=no\n") if config is not None else \
            original_config.replace("user_interface = text", "user_interface = none")
        (profile / "config").write_text(execution, encoding="utf-8")
        (rns / "config").write_text("[reticulum]\nshare_instance=No\n[logging]\nloglevel=2\n[interfaces]\n", encoding="utf-8")
        environment = dict(os.environ)
        environment["PYTHON_CPU_COUNT"] = "2"
        for key in ("TMP", "TEMP", "TMPDIR"):
            environment[key] = str(case)
        command = [sys.executable, "-u", str(Path(__file__).resolve()), "--observe",
                   "--profile", str(profile), "--rnsconfig", str(rns)]
        if force:
            command.append("--force-console")
        process = subprocess.run(command, cwd=case, env=environment, capture_output=True,
                                 text=True, timeout=60, check=False)
        (case / "stdout").write_text(process.stdout, encoding="utf-8")
        (case / "stderr").write_text(process.stderr, encoding="utf-8")
        events = [json.loads(line[2:]) for line in process.stdout.splitlines() if line.startswith("@@")]
        if process.returncode or len(events) != 1 or "observed" not in events[0]:
            raise RuntimeError(f"Actual primary observation failed: {case}")
        import_observations.append(events[0]["imports"])
        vectors.append({"name": name, "input": fixture_input, "forceConsole": force,
                        "observed": events[0]["observed"]})
    rns = Path(imports["RNS"])
    result = {"commit": REVISION,
              "sourceBlobSha256": {name: hashlib.sha256(subprocess.check_output(
                  ["git", "-C", str(source), "show", f"{REVISION}:{name}"])).hexdigest()
                  for name in ("nomadnet/NomadNetworkApp.py", "nomadnet/nomadnet.py")},
              "rnsCommit": "a29bfb51c468945c96a1ee22cae0114e993e2391",
              "rnsLoggerSha256": hashlib.sha256(subprocess.check_output(
                  ["git", "-C", str(rns.parent.parent), "show", "HEAD:RNS/__init__.py"])).hexdigest(),
              "method": "Actual unchanged NomadNetworkApp constructor reaches NoneUI; actual ConfigObj/applyConfig "
                        "then actual RNS.log at severities0–8 with timestamps disabled for deterministic text. "
                        "Process logger reset to RNS-config level2/stdout only before reapplication. "
                        "Actual RNS.log bad-directory file failure runs on a daemon thread joined for one second: "
                        "the pinned non-reentrant logging lock prevents its recursive emergency fallback from completing. "
                        "CLI main executes with setup argument observer. No primary source changes.",
              "vectors": vectors}
    with Path(args.output).open("w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(result, indent=2) + "\n")
    (work / "imports.json").write_text(json.dumps(import_observations, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output")
    parser.add_argument("--work")
    parser.add_argument("--observe", action="store_true")
    parser.add_argument("--profile")
    parser.add_argument("--rnsconfig")
    parser.add_argument("--force-console", action="store_true")
    arguments = parser.parse_args()
    if arguments.observe:
        observe(arguments)
    elif arguments.output and arguments.work:
        generate(arguments)
    else:
        parser.error("--output and --work required")
