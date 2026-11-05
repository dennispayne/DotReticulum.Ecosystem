"""Observe actual pinned primary permission methods on owned synthetic trees."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import subprocess
from types import SimpleNamespace

from GenerateNomadNetClientRuntimeVectors import reference, REVISION


def generate(output, observations, work):
    source, imports = reference()
    from nomadnet.ui.textui.Editor import PageEditorDisplay as Editor
    import urwid

    root = Path(work).resolve()
    root.mkdir(parents=True, exist_ok=False)
    statuses, dialogs = [], []
    owner = SimpleNamespace(close_dialog=lambda: None, refresh_tree=lambda: None,
                            set_status=statuses.append, show_dialog=dialogs.append)
    for name in ("_atomic_write", "_walk_pages", "_read_allowed", "_folder_permissions",
                 "_write_allowed", "_apply_permissions"):
        setattr(owner, name, lambda *args, _name=name: getattr(Editor, _name)(owner, *args))
    a, b = "a" * 32, "b" * 32

    def tree():
        return {p.relative_to(root).as_posix(): base64.b64encode(p.read_bytes()).decode("ascii")
                for p in sorted(root.rglob("*")) if p.is_file()}

    def state():
        common, restricted, total = owner._folder_permissions(str(root))
        return {"common": common, "restricted": restricted, "total": total}

    (root / "nested").mkdir()
    (root / "one").write_bytes(b"synthetic first\n")
    (root / "nested" / "two").write_bytes(bytes([0, 255, 13, 10]))
    (root / "three").write_bytes(b"synthetic third\n")
    owner._atomic_write(str(root / "one.allowed"), " \n " + a + " \n\n" + a + "\n")
    owner._atomic_write(str(root / "nested" / "two.allowed"), b + "\n")
    owner._atomic_write(str(root / "orphan.allowed"), "orphan policy retained\n")
    initial, mixed = tree(), state()
    Editor._save_permissions(owner, str(root), True, " \n " + a + " \n\n " + b + "  \n")
    restricted, uniform = tree(), state()
    Editor._save_permissions(owner, str(root), True, " \n\t ")
    pending = {"dialogs": len(dialogs), "treeUnchanged": tree() == restricted}
    dialog = dialogs[-1]
    dialog.keypress((80, 20), "esc")
    pending["cancelUnchanged"] = tree() == restricted
    Editor._save_permissions(owner, str(root), True, "")
    # Invoke the actual primary urwid button callback, not a replacement method.
    pending_widgets, seen = [dialogs[-1]], set()
    while pending_widgets:
        widget = pending_widgets.pop()
        if id(widget) in seen:
            continue
        seen.add(id(widget))
        if isinstance(widget, urwid.Button) and widget.get_label() == "Make public":
            widget._emit("click")
            break
        if hasattr(widget, "_w"):
            pending_widgets.append(widget._w)
        if hasattr(widget, "original_widget"):
            pending_widgets.append(widget.original_widget)
        if hasattr(widget, "contents"):
            pending_widgets.extend(item[0] for item in widget.contents)
    else:
        raise RuntimeError("Actual primary Make public button not found")
    assert owner._folder_permissions(str(root))[1] == 0
    public, cleared = tree(), state()
    (root / ".hidden").mkdir()
    (root / "__pycache__").mkdir()
    (root / ".hidden" / "skip").write_bytes(b"skip")
    (root / "__pycache__" / "skip").write_bytes(b"skip")
    (root / ".visible-hidden-file").write_bytes(b"included")
    walked = sorted(Path(p).relative_to(root).as_posix() for p in owner._walk_pages(str(root)))
    result = {
        "revision": REVISION, "source": "nomadnet/ui/textui/Editor.py",
        "sourceSha256": hashlib.sha256(subprocess.check_output(
            ["git", "-C", str(source), "show", f"{REVISION}:nomadnet/ui/textui/Editor.py"])).hexdigest(),
        "method": "Actual _folder_permissions/_save_permissions/_apply_permissions/_write_allowed; synthetic UI observer owner, actual ConfirmDialog escape and urwid Make public button callback. No copied guide/assets or rendered UI claim.",
        "initial": initial, "mixed": mixed, "restricted": restricted, "uniform": uniform,
        "makePublic": pending, "public": public, "cleared": cleared,
        "walked": walked, "statuses": statuses,
    }
    Path(output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
    Path(observations).write_text(json.dumps({"imports": imports, "work": str(root)}, indent=2) + "\n",
                                  encoding="utf-8", newline="\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--observations", required=True)
    parser.add_argument("--work", required=True)
    args = parser.parse_args()
    generate(args.output, args.observations, args.work)
