"""Execute actual primary FileBrowser with a disclosed headless shared-UI host."""
import hashlib
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

from reference_environment import verify_reference


def main():
    verify_reference(require_lxmf=True)
    source = Path(os.environ["DOTRETICULUM_NOMADNET_SOURCE"]).resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2":
        raise RuntimeError("Unexpected primary revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Modified primary source")
    sys.path.insert(0, str(source))
    module = importlib.import_module("nomadnet.ui.textui.FileBrowser")
    if Path(module.__file__).resolve() != source / "nomadnet/ui/textui/FileBrowser.py":
        raise RuntimeError("Wrong imported primary FileBrowser")
    app_class = importlib.import_module("nomadnet.NomadNetworkApp").NomadNetworkApp
    work = Path("artifacts/tests/nomadnet-file-browser-reference").resolve()
    work.mkdir(parents=True, exist_ok=True)
    for name in ["AlphaDir", "zdir", ".hidden-dir"]:
        (work / name).mkdir(exist_ok=True)
    for name, value in [("A.txt", b"1234"), ("B.txt", b"B"), ("oversize.txt", b"12345"),
                        (".hidden", b"H"), ("\ue000.txt", b"P"), ("\U0001f600.txt", b"E")]:
        (work / name).write_bytes(value)
    original = app_class.get_shared_instance
    host = SimpleNamespace(ui=SimpleNamespace(glyphs={"arrow_l": "<", "arrow_r": ">", "check": "x", "file": "f", "divider1": "-"}))
    app_class.get_shared_instance = staticmethod(lambda: host)
    events = []
    try:
        browser = module.FileBrowser(start_path=str(work), max_file_size=4,
                                     on_change=lambda values: events.append(("change", [Path(path).name for path in values])),
                                     on_done=lambda values: events.append(("done", [Path(path).name for path in values])),
                                     on_cancel=lambda: events.append(("cancel", [])))
        rows = [{"name": entry.name, "directory": entry.is_dir, "parent": entry.is_parent}
                for entry in browser.file_walker if isinstance(entry, module.FileBrowserEntry)]
        steps = []
        for name in ["A.txt", "B.txt", "A.txt", "oversize.txt", "AlphaDir"]:
            entry = next(item for item in browser.file_walker
                         if isinstance(item, module.FileBrowserEntry) and item.name == name)
            browser._entry_clicked(entry)
            steps.append({"name": name, "selected": [Path(path).name for path in browser.selected],
                          "directory": Path(browser.current_path).name, "notice": browser._notice_open})
            if browser._notice_open:
                browser._dismiss_notice()
        browser._cancel(None)
        single = module.FileBrowser(start_path=str(work), multi_select=False,
                                    on_done=lambda values: events.append(("single-done", [Path(path).name for path in values])))
        single._entry_clicked(next(item for item in single.file_walker if getattr(item, "name", None) == "A.txt"))
    finally:
        app_class.get_shared_instance = staticmethod(original)
    result = {
        "commit": revision,
        "sourceSha256": hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest(),
        "method": "Actual FileBrowser constructor/populate/entry-click/cancel and single-select callbacks. Only get_shared_instance supplies a headless UI glyph host; no selection/listing/size/callback implementation is replaced. Not a full unmodified application UI or live network claim.",
        "rows": rows, "steps": steps, "events": events,
    }
    Path(sys.argv[1]).write_text(json.dumps(result, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
