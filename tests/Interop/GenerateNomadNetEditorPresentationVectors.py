"""Execute actual pinned editor presentation on synthetic inputs, without assets."""
import argparse
import hashlib
import importlib
import json
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

from GenerateNomadNetClientRuntimeVectors import reference, REVISION


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--work", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--observations", required=True)
    args = parser.parse_args()
    source, imports = reference()
    module = importlib.import_module("nomadnet.ui.textui.Editor")
    import urwid

    root = Path(args.work).resolve()
    root.mkdir(parents=True, exist_ok=False)
    pages, files, cache = (root / name for name in ("pages", "files", "cache"))
    for folder in (pages, files, cache, pages / "nested", pages / "empty", pages / ".hidden"):
        folder.mkdir()
    for name, content in {"a.mu": ">Synthetic heading\none", "B.MU": "upper",
                          "plain.txt": "plain", "nested/z.mu": "nested",
                          "nested/visible.txt": "nested plain", ".hidden/secret.mu": "hidden"}.items():
        (pages / name).write_text(content, encoding="utf-8")
    events, pending = [], {}

    def alarm(delay, callback):
        token = len(events) + 1
        events.append({"kind": "schedule", "delay": delay, "token": token})
        pending[token] = callback
        return token

    def remove(token):
        events.append({"kind": "cancel", "token": token})
        return pending.pop(token, None) is not None

    screen = SimpleNamespace(register_palette_entry=lambda *args: None,
                             register_palette=lambda *args: None,
                             get_cols_rows=lambda: (100, 30))
    loop = SimpleNamespace(screen=screen, set_alarm_in=alarm, remove_alarm=remove)
    app = SimpleNamespace(pagespath=str(pages), filespath=str(files), cachepath=str(cache),
                          configdir=str(root), node=SimpleNamespace(destination=SimpleNamespace(hash=bytes(16))),
                          config={"textui": {"image_loading": 0, "clipboard_copy": False}},
                          ui=SimpleNamespace(screen=screen, loop=loop,
                                             glyphs={"arrow_l": "<", "arrow_r": ">", "divider1": "-", "node": "N"}))
    editor = module.PageEditorDisplay(app)

    def tree():
        return [{"path": str(Path(item.path).relative_to(root)), "folder": isinstance(item, module.FolderEntry)}
                for item in editor.build_entries() if hasattr(item, "path")]

    initial = tree()
    editor.on_folder_click(SimpleNamespace(path=str(pages / "nested")))
    expanded = tree()
    editor.on_filter_change(None, True)
    filtered = tree()
    panes = []
    for name in ("files", "preview", "editor", "files", "preview"):
        editor.toggle_pane(name)
        panes.append({"toggle": name, "visible": editor.visible_panes(),
                      "weights": [float(options[1]) for _, options in editor.columns.contents],
                      "focus": editor.columns.focus_position})
    editor.load_file(str(pages / "a.mu"))
    editor.editor.set_edit_text(">Synthetic first")
    editor.editor.set_edit_text(">Synthetic latest")
    debounce = list(events)
    token = editor.preview_alarm
    pending.pop(token)(loop, None)
    alarm_completed = editor.preview_alarm is None
    dirty = editor.dirty
    editor.editor.set_edit_text("x" * 100001)
    pending.pop(editor.preview_alarm)(loop, None)
    too_large = editor.pane_widgets["preview"].original_widget.original_widget.get_text()[0]
    saved = json.loads((root / "editor_state.json").read_text())
    restarted = module.PageEditorDisplay(app)
    before_start = restarted.current_path
    restarted.start()
    after_start = Path(restarted.current_path).name

    cases = ["", "# comment\n>heading\nbody", ">> heading `!bold`!\n",
             "`+>>Fold\n`<24|field`> `[label`:/page] `{x}\n",
             r"\`!literal", "`F123text`f `BTabcdef text", "`:anchor-name\n",
             "`[unterminated", "`<unterminated", "a\n\nb\n", "α😀`!x`!",
             "`😀tail", "`F12😀tail", "`FT12😀456tail", "`:Ⅻ²\n",
             "\\\n>escaped newline heading", "`F12\n>colour-consumed newline"]
    highlighting = [{"text": text, "runs": module.highlight_micron(text)} for text in cases]
    bounds = []
    for length in (50000, 50001):
        widget = module.MicronEdit(edit_text="#" + "x" * (length - 1))
        bounds.append({"length": length, "runs": widget.get_text()[1]})
    gutters = []
    for width in (2, 3, 4, 8, 12):
        widget = module.GutterEdit(edit_text="alpha beta gamma\nnext\n", multiline=True)
        canvas = widget.render((width,), focus=False)
        gutters.append({"width": width, "gutter": widget._gutter_width(width),
                        "rows": [line.decode("utf-8") for line in canvas.text]})
    editor._loading = True
    count = len(events)
    editor.on_edit_change(editor.editor, "")
    loading_ignored = len(events) == count
    editor._loading = False
    editor.is_exec = True
    editor.on_edit_change(editor.editor, "")
    executable_not_scheduled = len(events) == count
    hashes = {}
    imported_hashes = {}
    for name in ("nomadnet/ui/textui/Editor.py", "nomadnet/ui/textui/Browser.py",
                 "nomadnet/ui/textui/ReadlineEdit.py"):
        canonical = subprocess.check_output(["git", "-C", str(source), "show", f"{REVISION}:{name}"])
        hashes[name] = hashlib.sha256(canonical).hexdigest()
        imported_hashes[name] = hashlib.sha256((source / name).read_bytes()).hexdigest()
    saved["expanded"] = [str(Path(item).relative_to(root)).replace("\\", "/") for item in saved["expanded"]]
    saved["last_file"] = str(Path(saved["last_file"]).relative_to(root)).replace("\\", "/")
    for entries in (initial, expanded, filtered):
        for entry in entries:
            entry["path"] = entry["path"].replace("\\", "/")
    result = {"revision": REVISION, "canonicalSourceSha256": hashes,
              "boundary": "Actual PageEditorDisplay/Browser/Urwid widgets on synthetic local files; headless screen, palette sink and recording alarm owner. Actual render callbacks invoked explicitly. No OS terminal, network node, executable page or wall-clock timer claim.",
              "initialTree": initial, "expandedTree": expanded, "filteredTree": filtered, "panes": panes,
              "debounceEvents": debounce, "completedAlarmCleared": alarm_completed,
              "dirty": dirty, "tooLargePreview": too_large, "savedState": saved,
              "restart": {"beforeStart": before_start, "afterStart": after_start},
              "highlighting": highlighting, "highlightBounds": bounds, "gutters": gutters,
              "loadingIgnored": loading_ignored, "executableNotScheduled": executable_not_scheduled}
    Path(args.output).write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    Path(args.observations).write_text(json.dumps({"imports": imports,
        "editorModule": str(Path(module.__file__).resolve()), "importedSourceSha256": imported_hashes,
        "work": str(root)}, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
