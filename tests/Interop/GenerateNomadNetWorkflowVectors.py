"""Observe pinned clipboard framing and local editor contracts, without copying UI assets."""
import argparse
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
from unittest.mock import patch

from GenerateNomadNetClientRuntimeVectors import reference, REVISION


def generate(output, observations):
    source, imports = reference()
    import RNS
    from nomadnet.ui.textui import Helpers, Editor, Browser

    clipboard = []
    for label, value in (("absent", None), ("empty", ""), ("ascii", "Synthetic message"),
                         ("unicode", "Synthetic \u03bb \U0001f30d"), ("multiline", "one\ntwo"),
                         ("controls", "\x1b]52;c;untrusted\x07\x00"),
                         ("invalid-utf8", "\ud800")):
        captured = io.StringIO()
        errors = []
        with contextlib.redirect_stdout(captured), patch.object(
                RNS, "log", side_effect=lambda text, level: errors.append(level)):
            accepted = Helpers.osc52_copy(value)
        row = {"name": label, "accepted": accepted,
               "output": captured.getvalue(), "errorLevels": errors}
        if label == "invalid-utf8":
            row["inputUtf16"] = [ord(character) for character in value]
        else:
            row["input"] = value
        clipboard.append(row)

    urls = []
    for label, destination, path, fields in (
            ("absent", None, None, None),
            ("empty-path", bytes(range(16)), None, None),
            ("page", bytes(range(16)), "/page/index.mu", None),
            ("fields", bytes(range(16)), "/page/search.mu",
             {"var_query": "Synthetic \u03bb", "ignored": "not-in-url", "var_empty": ""})):
        owner = SimpleNamespace(destination_hash=destination, path=path, request_data=fields)
        urls.append({"name": label, "hash": None if destination is None else destination.hex(),
                     "path": path, "fields": fields, "url": Browser.Browser.current_url(owner)})
    confinement = []
    with tempfile.TemporaryDirectory(prefix="nomad-workflow-oracle-") as directory:
        root = Path(directory)
        pages = root / "pages"
        files = root / "files"
        pages.mkdir()
        files.mkdir()
        owner = SimpleNamespace(app=SimpleNamespace(pagespath=str(pages), filespath=str(files)))
        owner._roots = lambda: Editor.PageEditorDisplay._roots(owner)
        cases = (("empty", ""), ("blank", "  "), ("relative", "index.mu"),
                 ("nested", "manual/chapter.mu"), ("sibling-root", "../files/data.txt"),
                 ("escape", "../outside.txt"), ("traversal", "../../outside.txt"),
                 ("absolute-pages", str(pages / "absolute.mu")),
                 ("absolute-files", str(files / "data.bin")),
                 ("outside-absolute", str(root / "outside.txt")),
                 ("root", str(pages)), ("home", "~"))
        for label, path in cases:
            result = Editor.PageEditorDisplay._confine(owner, path)
            confinement.append({"name": label, "input": path.replace(str(root), "<root>").replace(os.sep, "/"),
                                "result": None if result is None else
                                Path(result).relative_to(root).as_posix()})
        path = pages / "synthetic.mu"
        path.write_text("before", encoding="utf-8")
        if os.name != "nt":
            path.chmod(0o640)
        before_mode = path.stat().st_mode
        text = ">Original preview\nSynthetic \u03bb\n"
        Editor.PageEditorDisplay._atomic_write(owner, str(path), text)
        atomic = {"text": path.read_text(encoding="utf-8"),
                  "modePreserved": path.stat().st_mode == before_mode,
                  "temporaryFiles": sorted(item.name for item in pages.glob(".*.tmp"))}

    sources = ("nomadnet/ui/textui/Helpers.py", "nomadnet/ui/textui/Editor.py",
               "nomadnet/ui/textui/Browser.py", "nomadnet/ui/textui/Conversations.py",
               "nomadnet/ui/textui/Config.py")
    result = {
        "revision": REVISION,
        "method": "Actual imported Helpers.osc52_copy, Browser.current_url and PageEditorDisplay._confine/_atomic_write; captured stdout/log levels and synthetic owners/storage, canonical root/separator path projections, no rendered primary UI or clipboard-device claim",
        "sources": {name: hashlib.sha256(subprocess.check_output(
            ["git", "-C", str(source), "show", f"{REVISION}:{name}"])).hexdigest()
            for name in sources},
        "clipboard": clipboard, "urls": urls, "confinement": confinement, "atomicWrite": atomic,
    }
    Path(output).write_text(json.dumps(result, indent=2, ensure_ascii=True) + "\n",
                            encoding="utf-8", newline="\n")
    Path(observations).write_text(json.dumps({"imports": imports,
        "disclosures": ["Clipboard stdout and RNS logging are captured, not a physical clipboard.",
                        "Editor uses synthetic paths and real private temporary filesystem writes.",
                        "Confinement is lexical upstream; native link refusal requires separate safety tests."]},
        indent=2) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--observations", required=True)
    args = parser.parse_args()
    generate(args.output, args.observations)
