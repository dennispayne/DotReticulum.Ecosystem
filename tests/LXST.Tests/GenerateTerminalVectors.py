"""Observe actual rnphone run-loop state/command precedence without devices."""
import argparse
import builtins
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference
from LXST.Utilities import rnphone


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    root = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"])
    rows = []
    for menu, state_name in [(menu, state) for menu in ["main", "phonebook"]
                             for state in ["available", "ringing", "connecting", "in-call"]]:
        for command in ["", "quit", "q", "exit", "identity", "i", "desthash", "d",
                        "announce", "a", "anounce", "help", "h", "?", "phonebook",
                        "p", "redial", "r", "i extra", "quit trailing", "help anything",
                        " i", "\ti", "q ", "back", "b", "7", "7 ignored", "unknown",
                        "0123456789abcdef0123456789abcdef", "quit" + " " * 28]:
            app = rnphone.ReticulumTelephone.__new__(rnphone.ReticulumTelephone)
            actions = []
            app.service = False
            app.should_run = True
            app.state = {"available": app.STATE_AVAILABLE, "ringing": app.STATE_RINGING,
                         "connecting": app.STATE_CONNECTING, "in-call": app.STATE_IN_CALL}[state_name]
            app.identity = SimpleNamespace(hash=bytes(16))
            app.caller = SimpleNamespace(hash=bytes(16))
            app.last_input = command
            app.telephone = SimpleNamespace(
                announce=lambda: actions.append("announce"),
                answer=lambda caller: actions.append("answer") or True,
                hangup=lambda: actions.append("hangup"))
            app.became_available = lambda: None
            app.quit = lambda: actions.append("quit")
            app.print_identity = lambda *args: actions.append("identity")
            app.print_destination = lambda *args: actions.append("desthash")
            app.phonebook_menu = lambda *args: actions.append("phonebook")
            app.redial = lambda *args: actions.append("redial")
            app.dial = lambda *args: actions.append("dial")
            app.main_menu()
            if menu == "phonebook":
                app.phonebook = {"Alice": "0123456789abcdef0123456789abcdef"}
                app.aliases = {"0123456789abcdef0123456789abcdef": "7"}
                with contextlib.redirect_stdout(io.StringIO()):
                    rnphone.ReticulumTelephone.phonebook_menu(app)
            def finish():
                app.should_run = False
                return ""
            original = builtins.input
            try:
                builtins.input = finish
                transcript = io.StringIO()
                with contextlib.redirect_stdout(transcript):
                    app.run()
            finally:
                builtins.input = original
            if "Available commands" in transcript.getvalue():
                actions.append("help")
            if "Phonebook closed" in transcript.getvalue():
                actions.append("back")
            rows.append({"menu": menu, "state": state_name, "command": command, "actions": actions})
    result = {
        "revision": subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip(),
        "source_sha256": hashlib.sha256((root / "LXST" / "Utilities" / "rnphone.py").read_bytes()).hexdigest(),
        "substitutes": "Constructor bypass; fake telephone/callbacks and one-iteration input boundary. Actual main_menu and run execute.",
        "cases": rows,
    }
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
