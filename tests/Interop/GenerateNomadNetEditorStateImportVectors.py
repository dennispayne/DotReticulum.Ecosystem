"""Observe pinned primary editor persistence; no primary assets or UI source are copied."""
import argparse
import hashlib
import importlib
import json
from pathlib import Path
import subprocess
import threading
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
    root = Path(args.work).resolve()
    root.mkdir(parents=True, exist_ok=False)
    pending = {}
    def alarm(delay, callback):
        token = len(pending) + 1
        pending[token] = callback
        return token
    screen = SimpleNamespace(register_palette_entry=lambda *a: None,
                             register_palette=lambda *a: None, get_cols_rows=lambda: (100, 30))
    loop = SimpleNamespace(screen=screen, set_alarm_in=alarm,
                           remove_alarm=lambda token: pending.pop(token, None))
    cases = []
    inputs = [
        ("absent", None),
        ("empty", {}),
        ("saved", {"filter_mu": True, "expanded": ["@/storage/pages", "@/storage/pages/nested"],
                   "pane_visible": {"files": True, "editor": True, "preview": False},
                   "last_file": "@/storage/pages/nested/notice.mu"}),
        ("all-hidden", {"pane_visible": {"files": False, "editor": False, "preview": False}}),
        ("missing-file", {"last_file": "@/storage/files/gone.txt"}),
        ("unknown", {"filter_mu": False, "scroll_offset": 91, "cursor": 2}),
        ("truthy", {"filter_mu": "false", "pane_visible": {"files": [], "preview": "no"}}),
        ("malformed", '{"filter_mu":true'),
        ("wrong-root", []),
    ]
    for name, state in inputs:
        profile = root / name
        pages, files, cache = (profile / "storage" / part for part in ("pages", "files", "cache"))
        for folder in (pages / "nested", files, cache):
            folder.mkdir(parents=True, exist_ok=True)
        (pages / "nested" / "notice.mu").write_text(">Imported synthetic heading\nsaved body\n", encoding="utf-8")
        # A synthetic stopped primary profile for native process acceptance, never a fixture private key.
        import RNS
        RNS.Identity().to_file(str(profile / "storage" / "identity"))
        from nomadnet.NomadNetworkApp import NomadNetworkApp
        owner = NomadNetworkApp.__new__(NomadNetworkApp)
        owner.peer_settings_lock = threading.Lock()
        owner.peersettingspath = str(profile / "storage" / "peersettings")
        owner.peer_settings = {"display_name": "Synthetic editor import"}
        owner.save_peer_settings()
        (profile / "config").write_text(
            "[client]\nuser_interface=text\nannounce_at_start=no\nperiodic_lxmf_sync=no\n"
            "[textui]\nintro_time=0\n[node]\nenable_node=no\n[logging]\nloglevel=0\n", encoding="utf-8")
        path = profile / "editor_state.json"
        def expand(value):
            if isinstance(value, str):
                return value.replace("@/", profile.as_posix() + "/")
            if isinstance(value, list):
                return [expand(item) for item in value]
            if isinstance(value, dict):
                return {key: expand(item) for key, item in value.items()}
            return value
        expanded = expand(state)
        raw = None if state is None else expanded if name == "malformed" else json.dumps(expanded)
        if raw is not None:
            path.write_text(raw, encoding="utf-8")
        app = SimpleNamespace(pagespath=str(pages), filespath=str(files), cachepath=str(cache),
            configdir=str(profile), node=SimpleNamespace(destination=SimpleNamespace(hash=bytes(16))),
            config={"textui": {"image_loading": 0, "clipboard_copy": False}},
            ui=SimpleNamespace(screen=screen, loop=loop,
                glyphs={"arrow_l": "<", "arrow_r": ">", "divider1": "-", "node": "N"}))
        editor = module.PageEditorDisplay(app)
        before = editor.current_path
        editor.start()
        observed = {"filter": editor.filter_mu, "expanded": sorted(editor.expanded),
                    "visible": editor.visible_panes(), "beforeStart": before,
                    "selected": editor.current_path, "dirty": editor.dirty}
        editor.save_state()
        saved = json.loads(path.read_text(encoding="utf-8"))
        def normalize(value):
            if isinstance(value, str):
                return value.replace(str(profile), "@").replace(profile.as_posix(), "@").replace("\\", "/")
            if isinstance(value, list):
                return [normalize(item) for item in value]
            if isinstance(value, dict):
                return {key: normalize(item) for key, item in value.items()}
            return value
        cases.append({"name": name, "input": state, "observed": normalize(observed), "saved": normalize(saved)})
        # Keep the original synthetic input for native import, not the rewritten primary output.
        if raw is None:
            path.unlink()
        else:
            path.write_text(raw, encoding="utf-8")
    files = ("nomadnet/ui/textui/Editor.py", "nomadnet/NomadNetworkApp.py")
    hashes = {name: hashlib.sha256(subprocess.check_output(
        ["git", "-C", str(source), "show", f"{REVISION}:{name}"])).hexdigest() for name in files}
    result = {"revision": REVISION, "canonicalSourceSha256": hashes,
              "boundary": "Actual unmodified PageEditorDisplay constructor/start/load_state/save_state on synthetic "
              "profiles with actual Browser and Urwid widgets, a headless palette sink and recording alarm owner. "
              "Not the full application constructor, interactive GUI, network or audio hardware. "
              "RNS/LXMF pins remain below the newer minimums declared by this primary; no dependency check shim.",
              "cases": cases}
    Path(args.output).write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    Path(args.observations).write_text(json.dumps({"imports": imports, "editorModule": module.__file__,
        "importedSourceSha256": {name: hashlib.sha256((source / name).read_bytes()).hexdigest() for name in files},
        "work": str(root)}, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
