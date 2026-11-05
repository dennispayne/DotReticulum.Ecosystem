"""Execute primary desktop paper-print callbacks with disclosed private software observers."""
import argparse
import ast
import hashlib
import importlib.metadata
import json
import os
import shlex
import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--software-directory", type=Path)
    parser.add_argument("--sink", type=Path)
    parser.add_argument("--pwsh", type=Path)
    args = parser.parse_args()
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference
    verify_reference(require_lxmf=True)
    source = args.source.resolve()
    if subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip() != PIN:
        raise RuntimeError("Unexpected primary revision")
    blobs = {}
    for path in ("sbapp/ui/messages.py", "sbapp/sideband/core.py", "sbapp/main.py", "sbapp/ui/layouts.py"):
        blob = subprocess.check_output(["git", "-C", str(source), "show", f"{PIN}:{path}"])
        if (source / path).read_bytes().replace(b"\r\n", b"\n") != blob.replace(b"\r\n", b"\n"):
            raise RuntimeError(f"Modified primary source: {path}")
        blobs[path] = blob
    tree = ast.parse(blobs["sbapp/ui/messages.py"])
    callback = [node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == "gen_print_qr"]
    if len(callback) != 1:
        raise RuntimeError("Unexpected callback contract")
    defaults = [node for node in ast.walk(ast.parse(blobs["sbapp/sideband/core.py"]))
                if isinstance(node, ast.Assign) and len(node.targets) == 1
                and isinstance(node.targets[0], ast.Subscript)
                and isinstance(node.targets[0].slice, ast.Constant)
                and node.targets[0].slice.value == "print_command"]
    default_values = []
    for node in defaults:
        owner = SimpleNamespace(config={})
        exec(compile(ast.Module(body=[node], type_ignores=[]), "core.py", "exec"), {"self": owner})
        default_values.append(owner.config["print_command"])
    if default_values != ["lp", "lp"]:
        raise RuntimeError("Unexpected primary default")
    settings_callback = [node for node in ast.walk(ast.parse(blobs["sbapp/main.py"]))
                         if isinstance(node, ast.FunctionDef) and node.name == "save_print_command"]
    if len(settings_callback) != 1:
        raise RuntimeError("Unexpected primary settings callback")
    settings = []
    for text, focus in (("", False), ("  printer --flag  ", False), ("   ", False), ("ignored", True)):
        saves = []
        box = SimpleNamespace(text=text)
        owner = SimpleNamespace(settings_screen=SimpleNamespace(ids=SimpleNamespace(settings_print_command=box)),
                                sideband=SimpleNamespace(config={"print_command": "previous"},
                                                         save_configuration=lambda: saves.append("save")))
        namespace = {"self": owner}
        exec(compile(ast.Module(body=settings_callback, type_ignores=[]), "main.py", "exec"), namespace)
        namespace["save_print_command"](SimpleNamespace(focus=focus))
        settings.append({"text": text, "focus": focus, "saved": owner.sideband.config["print_command"],
                         "displayed": box.text, "save_count": len(saves)})
    cases = []
    for name, command, folder, exit_code, android, failure in (
            ("default", "lp", "private", 0, False, False),
            ("quoted-options", 'printer --flag "two words" ""', "private", 0, False, False),
            ("literal-metacharacters", "printer ; $(whoami)", "private", 0, False, False),
            ("space-path-primary-splits", "printer", "private folder", 0, False, False),
            ("nonzero-primary-silent", "printer", "private", 7, False, False),
            ("start-failure-primary-retains-file", "printer", "private", 0, False, True),
            ("android-dismiss-only", "printer", "private", 0, True, False)):
        events, calls, errors = [], [], []
        lxm = SimpleNamespace(hash=bytes(range(32)), as_qr=lambda: (
            events.append("as_qr") or SimpleNamespace(save=lambda path: events.append("save:" + path))))
        owner = SimpleNamespace(app=SimpleNamespace(sideband=SimpleNamespace(
            tmp_dir=folder, config={"print_command": command})))
        def call(argv, **kwargs):
            calls.append(argv)
            events.append("subprocess")
            if failure:
                raise OSError("synthetic start failure")
            return exit_code
        class Dialog:
            def __init__(self, **kwargs):
                errors.append(kwargs["text"])
            def open(self):
                events.append("dialog")
        namespace = {
            "self": owner, "RNS": SimpleNamespace(
                vendor=SimpleNamespace(platformutils=SimpleNamespace(is_android=lambda: android)),
                hexrep=lambda value, delimit: value.hex()),
            "subprocess": SimpleNamespace(call=call, DEVNULL=subprocess.DEVNULL),
            "shlex": shlex, "os": SimpleNamespace(unlink=lambda path: events.append("unlink:" + path)),
            "MDRectangleFlatButton": lambda **kwargs: SimpleNamespace(bind=lambda **bindings: None),
            "MDDialog": Dialog, "dp": lambda value: value,
        }
        exec(compile(ast.Module(body=callback, type_ignores=[]), "messages.py", "exec"), namespace)
        item = SimpleNamespace(dmenu=SimpleNamespace(dismiss=lambda: events.append("dismiss")))
        namespace["gen_print_qr"](lxm, item)()
        cases.append({"name": name, "command": command, "calls": calls, "events": events, "errors": errors})
    result = {
        "revision": PIN,
        "source_sha256": {path: hashlib.sha256(blob).hexdigest() for path, blob in blobs.items()},
        "defaults": default_values,
        "settings": settings,
        "method": "Executed unchanged AST gen_print_qr and actual default assignments; synthetic image/menu/subprocess/filesystem/dialog observers. Actual shlex.split. No primary GUI, real printer, subprocess or QR codec claim.",
        "cases": cases,
    }
    if args.software_directory:
        import LXMF
        import RNS
        if not args.sink or not args.pwsh:
            raise RuntimeError("Software observation requires explicit private PowerShell sink")
        workspace = args.software_directory.resolve()
        workspace.mkdir(parents=True, exist_ok=False)
        captures = workspace / "captures"
        origin = RNS.Destination(RNS.Identity(), RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
        target = RNS.Destination(RNS.Identity(), RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
        lxm = LXMF.LXMessage(target, origin, "Public synthetic paper QR print", desired_method=LXMF.LXMessage.PAPER)
        lxm.timestamp = 1700000000.25
        lxm.pack()
        uri = lxm.as_uri(finalise=False)
        command = shlex.join([str(args.pwsh.resolve()), "-NoProfile", "-File", str(args.sink.resolve()),
                              "-Mode", "capture", "-Output", str(captures),
                              "-Marker", str(workspace / "marker"), "-Extra", "public primary ; $(ignored)"])
        events = []
        owner = SimpleNamespace(app=SimpleNamespace(sideband=SimpleNamespace(
            tmp_dir=workspace.as_posix(), config={"print_command": command})))
        namespace = {"self": owner, "RNS": RNS, "subprocess": subprocess, "shlex": shlex, "os": os}
        exec(compile(ast.Module(body=callback, type_ignores=[]), "messages.py", "exec"), namespace)
        namespace["gen_print_qr"](lxm, SimpleNamespace(
            dmenu=SimpleNamespace(dismiss=lambda: events.append("dismiss"))))()
        receipts = list(captures.glob("*.json"))
        if len(receipts) != 1 or events != ["dismiss"]:
            raise RuntimeError("Actual primary callback did not complete its software sink")
        receipt = json.loads(receipts[0].read_text(encoding="utf-8-sig"))
        if Path(receipt["original"]).exists():
            raise RuntimeError("Actual primary callback did not unlink its private image")
        result["software_observation"] = {
            "method": "Unchanged AST callback with actual LXMF.LXMessage.pack/as_uri/as_qr, real subprocess.call/shlex.split/os.unlink and private PowerShell capture sink; only outer app/menu are synthetic. No primary GUI or physical printer.",
            "uri": uri, "command": command, "events": events, "receipt": receipt,
            "png_sha256": hashlib.sha256(Path(receipt["copy"]).read_bytes()).hexdigest(),
            "rns_module": str(Path(RNS.__file__).resolve()), "lxmf_module": str(Path(LXMF.__file__).resolve()),
            "qr_packages": {name: importlib.metadata.version(name) for name in ("qrcode", "Pillow", "zxing-cpp")},
        }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
