"""Execute pinned PageList and editor scroll ownership on synthetic local text."""
import argparse
import hashlib
import importlib
import json
from pathlib import Path
import subprocess
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
    browser = importlib.import_module("nomadnet.ui.textui.Browser")
    import urwid

    text = "\n".join(f"line {index:03d}" for index in range(80))
    size = (40, 6)
    page = browser.PageList(urwid.SimpleFocusListWalker([urwid.Text(text)]))
    page.render(size)
    operations = []
    for key in ("page down", "page down", "page up", "end", "page down", "home", "page up"):
        page.keypress(size, key)
        page.render(size)
        operations.append({"key": key, "position": page.get_scrollpos(),
                           "rows": [line.decode() for line in page.render(size).text]})
    positions = []
    for value in (-100, 0, 3, 74, 1000000):
        page.set_scrollpos(value)
        positions.append({"requested": value, "position": page.get_scrollpos()})

    root = Path(args.work).resolve()
    root.mkdir(parents=True, exist_ok=False)
    for name in ("pages", "files", "cache"):
        (root / name).mkdir()
    screen = urwid.raw_display.Screen()
    screen.get_cols_rows = lambda: (120, 30)
    loop = SimpleNamespace(screen=screen, set_alarm_in=lambda *args: 1, remove_alarm=lambda *args: None)
    app = SimpleNamespace(pagespath=str(root / "pages"), filespath=str(root / "files"),
                          cachepath=str(root / "cache"), configdir=str(root),
                          node=SimpleNamespace(destination=SimpleNamespace(hash=bytes(16))),
                          config={"textui": {"image_loading": 0, "clipboard_copy": False, "theme": "dark"}},
                          ui=SimpleNamespace(screen=screen, loop=loop,
                                             glyphs={"arrow_l": "<", "arrow_r": ">", "divider1": "-", "node": "N"}))
    import nomadnet
    nomadnet.NomadNetworkApp._shared_instance = app
    editor = module.PageEditorDisplay(app)
    path = root / "pages" / "scroll.mu"
    editor_text = text.replace("\n", "\n\n")
    path.write_text(editor_text, encoding="utf-8")
    editor.load_file(str(path))
    preview = editor._preview_scrollable()
    initial_canvas = [line.decode() for line in preview.render(size).text]
    preview_rows = preview.rows_max(size)
    if preview_rows != 159 or "line 000" not in initial_canvas[0]:
        raise RuntimeError("Primary editor did not render the synthetic Micron document")
    editor._set_preview_scrollpos(17)
    before = editor._preview_scrollpos()
    editor.toggle_pane("preview")
    editor.toggle_pane("preview")
    toggled = editor._preview_scrollpos()
    editor.editor.set_edit_text(editor_text + "\n\nlatest")
    editor.render_preview()
    immediate = editor._preview_scrollpos()
    editor._preview_scrollable().render(size)
    rerendered = editor._preview_scrollpos()
    editor.save_state()
    state = json.loads((root / "editor_state.json").read_text())
    canonical = {}
    for name in ("nomadnet/ui/textui/Editor.py", "nomadnet/ui/textui/Browser.py",
                 "nomadnet/vendor/Scrollable.py"):
        canonical[name] = hashlib.sha256(subprocess.check_output(
            ["git", "-C", str(source), "show", f"{REVISION}:{name}"])).hexdigest()
    result = {"revision": REVISION, "canonicalSourceSha256": canonical, "text": text,
              "size": list(size), "operations": operations, "positions": positions,
              "editor": {"before": before, "afterToggle": toggled,
                         "totalRows": preview_rows, "initialCanvas": initial_canvas,
                         "immediateRerender": immediate, "afterRender": rerendered,
                         "persistedKeys": sorted(state)},
              "boundary": "Actual PageList and PageEditorDisplay on synthetic text; headless screen/palette/alarm owner and disclosed application singleton shim for MicronParser theme/palette. No full unmodified primary application, terminal, network, physical display or automatic timer evidence."}
    Path(args.output).write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    Path(args.observations).write_text(json.dumps({"imports": imports, "urwid": urwid.__version__,
        "urwidPath": urwid.__file__, "editorModule": module.__file__, "browserModule": browser.__file__},
        indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
