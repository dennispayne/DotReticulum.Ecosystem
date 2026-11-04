"""Execute unchanged primary constructor/configuration and terminal attachment helpers."""
import argparse
import hashlib
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
    from nomadnet.NomadNetworkApp import NomadNetworkApp
    from nomadnet.ui.textui.Conversations import _save_attachment_to_disk, _copy_attachment_to_dest

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
            work = Path.cwd()
            home = str(Path.home())

            def normal(value):
                if value is None:
                    return None
                return str(value).replace(str(work), "$WORK").replace(home, "$HOME").replace("\\", "/")

            result = {"configured": normal(app.attachment_save_path),
                      "downloads": normal(app.downloads_path),
                      "existsBeforeExport": os.path.exists(app.attachment_save_path or app.downloads_path)}
            # Never write an observation into the operator's home.
            if args.home_case:
                result["exports"] = []
            else:
                exports = []
                for filename in ("public.txt", "public.txt", "../public.txt", ".hidden.txt",
                                 "nested\\audio.ogg", "", "archive.tar.gz", "archive.tar.gz"):
                    path = _save_attachment_to_disk(filename, b"public attachment observation")
                    exports.append({"input": filename, "name": Path(path).name,
                                    "sha256": hashlib.sha256(Path(path).read_bytes()).hexdigest()})
                src = work / "public-source"
                src.write_bytes(b"public copy observation")
                copied = _copy_attachment_to_dest("public.txt", str(src))
                result["copyName"] = Path(copied).name
                result["copySha256"] = hashlib.sha256(Path(copied).read_bytes()).hexdigest()
                result["exports"] = exports
                blocker = work / "blocked"
                blocker.write_bytes(b"public blocker")
                app.attachment_save_path = str(blocker / "child")
                try:
                    _save_attachment_to_disk("fail.txt", b"public")
                    raise RuntimeError("Expected blocked-directory failure")
                except OSError as error:
                    result["blockedDirectoryFailed"] = True
                    result["failureType"] = type(error).__name__
                app.attachment_save_path = str(work / "recovery")
                recovered = _save_attachment_to_disk("healthy.txt", b"public recovery")
                result["recovered"] = Path(recovered).read_bytes() == b"public recovery"
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
    cases = []
    import_observations = []
    for name, value in (("absent", None), ("empty", ""), ("relative", "exported/nested"),
                        ("literal-env", "$UNEXPANDED/export"), ("home", "~/attachment-observation"),
                        ("named-home", "~__dotreticulum_nonexistent_user__/export")):
        case = work / name
        profile, rns = case / "profile", case / "rns"
        profile.mkdir(parents=True, exist_ok=True)
        rns.mkdir(parents=True, exist_ok=True)
        config = ("[client]\nuser_interface=none\nannounce_at_start=no\nperiodic_lxmf_sync=no\n"
                  'downloads_path="' + str(case / "downloads") + '"\n')
        if value is not None:
            config += 'attachment_save_path="' + value + '"\n'
        config += "[node]\nenable_node=no\n[logging]\nloglevel=0\ndestination=console\n"
        (profile / "config").write_text(config, encoding="utf-8")
        (rns / "config").write_text("[reticulum]\nshare_instance=No\n[logging]\nloglevel=0\n[interfaces]\n", encoding="utf-8")
        environment = dict(os.environ)
        environment["PYTHON_CPU_COUNT"] = "2"
        for key in ("TMP", "TEMP", "TMPDIR"):
            environment[key] = str(case)
        command = [sys.executable, "-u", str(Path(__file__).resolve()), "--observe",
                   "--profile", str(profile), "--rnsconfig", str(rns)]
        if name in ("home", "named-home"):
            command.append("--home-case")
        process = subprocess.run(command, cwd=case, env=environment, capture_output=True,
                                 text=True, timeout=60, check=False)
        (case / "stdout").write_text(process.stdout, encoding="utf-8")
        (case / "stderr").write_text(process.stderr, encoding="utf-8")
        events = [json.loads(line[2:]) for line in process.stdout.splitlines() if line.startswith("@@")]
        if process.returncode or len(events) != 1 or "observed" not in events[0]:
            raise RuntimeError(f"Actual primary observation failed: {case}")
        observed = events[0]["observed"]
        # Windows and Unix report different OSError subclasses for ENOTDIR.
        observed.pop("failureType", None)
        if name != "named-home":
            cases.append({"name": name, "input": value, "observed": observed})
        import_observations.append({"case": name, "imports": events[0]["imports"], "observed": observed})
    files = ("nomadnet/NomadNetworkApp.py", "nomadnet/Conversation.py",
             "nomadnet/ui/textui/Conversations.py")
    result = {"revision": REVISION, "method": "Actual daemon constructor, _save_attachment_to_disk and _copy_attachment_to_dest; no primary modifications",
              "sources": {name: hashlib.sha256(subprocess.check_output(
                  ["git", "-C", str(source), "show", f"{REVISION}:{name}"])).hexdigest() for name in files},
              "cases": cases}
    Path(args.output).write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    (work / "imports.json").write_text(json.dumps(import_observations, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output")
    parser.add_argument("--work")
    parser.add_argument("--observe", action="store_true")
    parser.add_argument("--profile")
    parser.add_argument("--rnsconfig")
    parser.add_argument("--home-case", action="store_true")
    options = parser.parse_args()
    observe(options) if options.observe else generate(options)
