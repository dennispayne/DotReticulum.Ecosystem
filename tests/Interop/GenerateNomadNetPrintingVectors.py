"""Observe pinned primary printing; no upstream templates or real printer access."""
import argparse
import ast
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
import types
from datetime import datetime

from GenerateNomadNetClientRuntimeVectors import reference, REVISION

HASH = "abcdef0123456789abcdef0123456789"
TEMPLATE = "From {origin}\nSent {stime}\nReceived {rtime}\nTitle {mtitle}\n{{message}}\n{mbody}\n"
CASES = [
    ("absent", ""),
    ("command-alone", "[printing]\nprint_command=ignored\n"),
    ("disabled", "[printing]\nprint_messages=no\nprint_command='printer --flag'\nprint_from=everywhere\n"),
    ("no-filter", "[printing]\nprint_messages=yes\n"),
    ("everywhere", "[printing]\nprint_messages=yes\nprint_from=EvErYwHeRe\n"),
    ("trusted", "[printing]\nprint_messages=yes\nprint_from=TrUsTeD\n"),
    ("lower-hash", "[printing]\nprint_messages=yes\nprint_from=" + HASH + "\n"),
    ("upper-hash", "[printing]\nprint_messages=yes\nprint_from=" + HASH.upper() + "\n"),
    ("trusted-list", "[printing]\nprint_messages=yes\nprint_from=TRUSTED," + HASH + "\n"),
    ("everywhere-list", "[printing]\nprint_messages=yes\nprint_from=everywhere,unused\n"),
    ("single-list", "[printing]\nprint_messages=yes\nprint_from=" + HASH + ",\n"),
]


def observe(args):
    source, imports = reference()
    from nomadnet.NomadNetworkApp import NomadNetworkApp
    from nomadnet.Directory import DirectoryEntry
    from RNS.vendor.configobj import ConfigObj
    import RNS

    def observations():
        try:
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                app = NomadNetworkApp._shared_instance
                if app is not None and hasattr(app, "ui"):
                    break
                time.sleep(0.02)
            if app is None or not hasattr(app, "ui"):
                raise RuntimeError("Primary constructor did not reach NoneUI")
            vectors = []
            message = types.SimpleNamespace(source_hash=bytes.fromhex(HASH))
            for name, config in CASES:
                app.config = ConfigObj((config + "\n[client]\nuser_interface=none\n[textui]\nsanitize_names=yes\n").splitlines())
                app.applyConfig()
                filters = []
                for trust in (0, 1, 2, 255):
                    app.directory.directory_entries[message.source_hash] = DirectoryEntry(
                        message.source_hash, "Public Sender", trust)
                    filters.append({"trust": trust, "print": app.should_print(message)})
                vectors.append({"name": name, "input": config, "enabled": app.print_messages,
                                "all": app.print_all_messages, "trusted": app.print_trusted_messages,
                                "command": app.print_command, "filters": filters})
            custom = Path(args.profile).resolve() / "public-synthetic-template.txt"
            custom.write_text(TEMPLATE, encoding="utf-8", newline="\n")
            app.config = ConfigObj(("[printing]\nprint_messages=yes\nprint_from=trusted\nmessage_template=" +
                str(custom) + "\n[client]\nuser_interface=none\n[textui]\nsanitize_names=yes\n").splitlines())
            app.applyConfig()
            if app.printing_template_msg != TEMPLATE or custom.read_text(encoding="utf-8") != TEMPLATE:
                raise RuntimeError("Actual primary custom-template configuration did not retain the public synthetic template")
            # Execute only the unchanged print_message AST with a synthetic owner and
            # instrumented file boundary: Windows primary hardcodes /tmp. No global
            # directory creation and no primary-source patch is used.
            file = source / "nomadnet" / "NomadNetworkApp.py"
            tree = ast.parse(file.read_text(encoding="utf-8"))
            cls = next(node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == "NomadNetworkApp")
            method = next(node for node in cls.body if isinstance(node, ast.FunctionDef) and node.name == "print_message")
            captured, deleted, printed = [], [], []

            class Capture(io.BytesIO):
                def close(self):
                    if not self.closed:
                        captured.append(self.getvalue().decode("utf-8"))
                    super().close()

            namespace = {"datetime": datetime, "time": time, "RNS": RNS,
                         "open": lambda *args: Capture(),
                         "os": types.SimpleNamespace(unlink=lambda path: deleted.append(path))}
            exec(compile(ast.Module(body=[method], type_ignores=[]), str(file), "exec"), namespace)
            owner = types.SimpleNamespace(printing_template_msg=app.printing_template_msg, time_format="%Y-%m-%d %H:%M:%S",
                                          directory=app.directory, ui=types.SimpleNamespace(glyphs=None),
                                          print_file=lambda path: printed.append(path))
            formatting = []
            for trust, title in ((0, ""), (1, "Public title"), (2, "Public title"), (255, "Public title")):
                app.directory.directory_entries[message.source_hash] = DirectoryEntry(message.source_hash, "Public Sender", trust)
                stamp, receipt = [2024, 2, 3, 4, 5, 6], [2024, 3, 4, 5, 6, 7]
                sample = types.SimpleNamespace(source_hash=message.source_hash,
                    timestamp=time.mktime(tuple(stamp) + (0, 0, -1)),
                    title_as_string=lambda: title, content_as_string=lambda: "Public body\nline two")
                namespace["print_message"](owner, sample, time.mktime(tuple(receipt) + (0, 0, -1)))
                formatting.append({"trust": trust, "title": title, "sentLocal": stamp, "receivedLocal": receipt,
                                   "content": "Public body\nline two", "output": captured[-1]})
            if len(captured) != 4 or deleted != printed:
                raise RuntimeError("Formatting observation did not clean up each synthetic file")
            # print_file is the actual class method with its subprocess callback
            # observed rather than invoking a printer.
            import importlib
            module = importlib.import_module("nomadnet.NomadNetworkApp")
            original = module.subprocess.call
            arguments = []
            try:
                module.subprocess.call = lambda argv, **kwargs: arguments.append(argv) or 0
                app.print_command = 'printer --flag "two words"'
                success = app.print_file("public-file.txt")
                app.print_file("public file.txt")
            finally:
                module.subprocess.call = original
            Path(args.profile).parent.joinpath("imports.json").write_text(json.dumps(
                {"imports": imports, "sourceWorktreeSha256": hashlib.sha256(file.read_bytes()).hexdigest()},
                indent=2) + "\n", encoding="utf-8")
            result = {"commit": REVISION,
                      "sourceBlobSha256": {"nomadnet/NomadNetworkApp.py": hashlib.sha256(subprocess.check_output(
                          ["git", "-C", str(source), "show", REVISION + ":nomadnet/NomadNetworkApp.py"])).hexdigest()},
                      "method": "Actual unchanged NomadNetworkApp constructor/ConfigObj/applyConfig/should_print and real DirectoryEntry trust; "
                                "actual applyConfig reads an existing public synthetic UTF-8 template unchanged; "
                                "unchanged print_message AST on synthetic owner with in-memory open/unlink/print_file observers only, "
                                "local mktime calendar dates (no timezone shim); actual print_file with subprocess.call argv observer. "
                                "No real printer, copied default template, primary modification, or /tmp creation.",
                      "hash": HASH, "template": TEMPLATE, "customTemplateLoadedUnchanged": True,
                      "vectors": vectors, "formatting": formatting,
                      "subprocess": {"success": success, "arguments": arguments}}
            Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
            print("@@printing-vectors", flush=True)
            os._exit(0)
        except Exception as error:
            import traceback
            traceback.print_exc()
            print("@@error " + repr(error), flush=True)
            os._exit(1)

    threading.Thread(target=observations, daemon=True).start()
    NomadNetworkApp(configdir=args.profile, rnsconfigdir=args.rnsconfig, daemon=True, force_console=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--profile", required=True)
    parser.add_argument("--rnsconfig", required=True)
    args = parser.parse_args()
    for path in (Path(args.profile), Path(args.rnsconfig)):
        path.mkdir(parents=True, exist_ok=True)
    (Path(args.profile) / "config").write_text(
        "[client]\nuser_interface=none\nannounce_at_start=no\nperiodic_lxmf_sync=no\n[node]\nenable_node=no\n",
        encoding="utf-8")
    (Path(args.rnsconfig) / "config").write_text(
        "[reticulum]\nshare_instance=No\n[logging]\nloglevel=0\n[interfaces]\n", encoding="utf-8")
    observe(args)
