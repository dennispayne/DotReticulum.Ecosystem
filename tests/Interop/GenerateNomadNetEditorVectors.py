"""Execute the pinned editor's text operations and config persistence; no guide assets."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
from types import SimpleNamespace

from GenerateNomadNetClientRuntimeVectors import reference, REVISION


def generate(output, observations, work):
    source, imports = reference()
    from nomadnet.ui.textui.Editor import PageEditorDisplay
    from nomadnet.util import write_config_atomic
    from RNS.vendor.configobj import ConfigObj
    import urwid

    root = Path(work).resolve()
    root.mkdir(parents=True, exist_ok=False)
    owner = SimpleNamespace(editor=urwid.Edit(edit_text="synthetic one\nsynthetic two\n", multiline=True, edit_pos=0),
                            set_status=lambda text: None)
    owner.find_next = lambda term: PageEditorDisplay.find_next(owner, term)
    rows = []
    for action, arguments in (
            ("find_next", ["synthetic"]), ("find_next", ["synthetic"]),
            ("find_next", ["synthetic"]), ("replace_one", ["synthetic", "changed"]),
            ("replace_one", ["missing", "ignored"]), ("find_next", ["two"]),
            ("replace_all", ["synthetic", "original"]), ("replace_all", ["", "ignored"]),
            ("_insert_snippet", [">Original sample\n"])):
        owner.close_dialog = lambda: None
        owner.pane_visible = {"editor": False}
        getattr(PageEditorDisplay, action)(owner, *arguments)
        rows.append({"operation": action, "arguments": arguments,
                     "text": owner.editor.edit_text, "cursor": owner.editor.edit_pos})
    config = ConfigObj(encoding="utf-8")
    config.filename = str(root / "config")
    config["client"] = {"display_name": "Synthetic author"}
    write_config_atomic(config)
    config["client"]["display_name"] = "Synthetic changed"
    write_config_atomic(config)
    loaded = ConfigObj(config.filename, encoding="utf-8")
    sources = ("nomadnet/ui/textui/Editor.py", "nomadnet/ui/textui/Config.py",
               "nomadnet/ui/textui/Guide.py", "nomadnet/util.py")
    result = {"revision": REVISION,
              "method": "Actual PageEditorDisplay find_next/replace_one/replace_all/_insert_snippet with actual urwid.Edit and synthetic owner/text; actual write_config_atomic and ConfigObj reload on owned storage. No copied guide/snippet assets or rendered Urwid claim.",
              "sources": {name: hashlib.sha256(subprocess.check_output(
                  ["git", "-C", str(source), "show", f"{REVISION}:{name}"])).hexdigest()
                  for name in sources},
              "initialText": "synthetic one\nsynthetic two\n", "operations": rows,
              "configReload": loaded["client"]["display_name"],
              "temporaryFiles": sorted(item.name for item in root.glob(".*.tmp"))}
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
