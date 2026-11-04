"""Observe pinned configuration methods; terminal construction is captured, not emulated."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
from types import SimpleNamespace
from unittest.mock import patch

from GenerateNomadNetClientRuntimeVectors import reference, REVISION


def generate(output, observations, work):
    source, imports = reference()
    import nomadnet.ui.textui.Config as module
    import urwid
    from RNS.vendor.configobj import ConfigObj

    root = Path(work).resolve()
    root.mkdir(parents=True, exist_ok=False)
    launches = []
    for system, command in (("Windows", "nano"), ("Linux", "editor"),
                            ("Darwin", "editor"), ("Darwin", "custom editor"),
                            ("Windows", "editor --option")):
        captured = {}
        callbacks = []
        events = []
        app = SimpleNamespace(config={"textui": {"editor": command}}, configpath="owned/native config",
                              ui=SimpleNamespace(loop=None, main_display=SimpleNamespace(
                                  request_redraw=lambda: events.append("redraw"))))
        parent = SimpleNamespace(switch_to_list=lambda: events.append("list"))

        def terminal(argv, **kwargs):
            captured["argv"] = list(argv)
            captured["encoding"] = kwargs["encoding"]
            return urwid.Text("Synthetic terminal boundary")

        with patch.object(module.platform, "system", return_value=system), \
                patch.object(module.urwid, "Terminal", terminal, create=True), \
                patch.object(module.urwid, "connect_signal",
                             side_effect=lambda widget, name, callback: callbacks.append((name, callback))):
            module.EditorTerminal(app, parent)
            callbacks[0][1]()
        launches.append({"platform": system, "configured": command, **captured,
                         "signal": callbacks[0][0], "onClosed": events})

    owner = SimpleNamespace(parent=SimpleNamespace(app=SimpleNamespace(
        configpath="owned/native config", rns=SimpleNamespace(
            config=SimpleNamespace(filename="owned/selected RNS")))))
    paths = {"native": module.ConfigSectionView._resolve_config_path(owner, {}),
             "rns": module.ConfigSectionView._resolve_config_path(owner, {"configfile": "reticulum"})}
    path = root / "selected-rns-config"
    initial = "# Original synthetic comment\n[reticulum]\n  enable_transport = no\n[interfaces]\n"
    path.write_text(initial, encoding="utf-8")
    config = ConfigObj(str(path), encoding="utf-8")
    events = []
    field = {"type": "checkbox", "default": False}
    owner = SimpleNamespace(
        validate_all=lambda: True, file_config=config, section_key="reticulum",
        section={"configfile": "reticulum", "name": "Reticulum (global)"},
        fields={"enable_transport": {"field": field, "widget": urwid.CheckBox("", state=True)}},
        _widget_write_value=lambda f, w: module.ConfigSectionView._widget_write_value(None, f, w),
        _default_write_value=lambda f: module.ConfigSectionView._default_write_value(None, f),
        parent=SimpleNamespace(app=SimpleNamespace(rns=SimpleNamespace(
            config=SimpleNamespace(reload=lambda: events.append("reload")))),
            mark_restart_pending=lambda: events.append("restart")),
        show_message=lambda text, **kwargs: events.append(text))
    module.ConfigSectionView.on_save(owner, None)
    result = {"revision": REVISION,
              "method": "Actual EditorTerminal constructor/closed callback with captured urwid.Terminal boundary (Windows urwid lacks Terminal; explicitly supplied observation boundary, not a primary PTY claim); actual ConfigSectionView path resolution and on_save, actual ConfigObj and atomic filesystem write; synthetic owners and checkbox. No primary guide/assets.",
              "sources": {name: hashlib.sha256(subprocess.check_output(
                  ["git", "-C", str(source), "show", f"{REVISION}:{name}"])).hexdigest()
                  for name in ("nomadnet/ui/textui/Config.py", "nomadnet/NomadNetworkApp.py", "nomadnet/util.py")},
              "launches": launches, "paths": paths, "rnsInitial": initial,
              "rnsSaved": path.read_text(encoding="utf-8"), "saveEvents": events,
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
