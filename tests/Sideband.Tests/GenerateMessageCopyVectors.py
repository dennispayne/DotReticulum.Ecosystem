"""Execute the primary raw-text copy factory with an observing clipboard boundary."""
import argparse
import ast
import hashlib
import json
import subprocess
from pathlib import Path
from types import SimpleNamespace

from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
SOURCE_SHA = "9b2580f76310356d2e1a30b038857fbad4ee346aad9e2a25ab237515fff39928"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    if subprocess.check_output(["git", "-C", str(args.source), "rev-parse", "HEAD"], text=True).strip() != PIN:
        raise RuntimeError("Unexpected primary revision")
    source = subprocess.check_output(
        ["git", "-C", str(args.source), "show", f"{PIN}:sbapp/ui/messages.py"])
    if hashlib.sha256(source).hexdigest() != SOURCE_SHA:
        raise RuntimeError("Unexpected primary messages.py")
    factories = [node for node in ast.walk(ast.parse(source))
                 if isinstance(node, ast.FunctionDef) and node.name == "gen_copy"]
    if len(factories) != 1:
        raise RuntimeError("Primary copy factory changed")
    cases = []
    for label, content, fail in (
            ("unicode", "Raw \u00e6\nline \u03bb".encode("utf-8"), False),
            ("markup", b"[b]raw[/b] **markdown** https://example.invalid", False),
            ("empty", b"", False), ("controls", b"raw\x00\t\r\nend", False),
            ("clipboard-error", b"Retain selected text", True), ("invalid-utf8", b"\xff", False)):
        events = []
        copied = []

        def copy(text):
            events.append("copy")
            if fail:
                raise RuntimeError("Synthetic clipboard unavailable")
            copied.append(text)

        namespace = {"Clipboard": SimpleNamespace(copy=copy)}
        exec(compile(ast.Module(body=factories, type_ignores=[]), "messages.py", "exec"), namespace)
        item = SimpleNamespace(dmenu=SimpleNamespace(dismiss=lambda: events.append("dismiss")))
        error = None
        try:
            namespace["gen_copy"](content.decode("utf-8"), item)()
        except Exception as observed:
            error = type(observed).__name__
        cases.append({"name": label, "content_hex": content.hex(), "copied": copied,
                      "events": events, "error": error})
    result = {
        "revision": PIN, "source_sha256": SOURCE_SHA,
        "method": "Actual AST-extracted gen_copy; actual menu supplies message_source.decode('utf-8') where source is saved content, before renderer transformations. Synthetic observing Clipboard/menu only; no Kivy renderer, OS clipboard, store or live-node claim",
        "cases": cases}
    with args.output.open("w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(result, indent=4) + "\n")


if __name__ == "__main__":
    main()
