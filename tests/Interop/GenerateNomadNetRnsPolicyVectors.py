"""Observe actual pinned Nomad selected-RNS forms; no copied UI or wire implementation."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
from types import SimpleNamespace

from GenerateNomadNetClientRuntimeVectors import reference, REVISION
from GenerateNomadNetConfigurationFormVectors import button


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--observations", required=True)
    parser.add_argument("--work", required=True)
    args = parser.parse_args()
    source, imports = reference()
    from RNS.vendor.configobj import ConfigObj
    from nomadnet.NomadNetworkApp import NomadNetworkApp
    from nomadnet.ui.textui.Config import ConfigSectionView, CONFIG_SECTIONS

    root = Path(args.work).resolve()
    root.mkdir(parents=True, exist_ok=False)
    primary, selected = root / "nomad-config", root / "selected-rns-config"
    primary.write_text("# Unrelated Nomad configuration\n[client]\nenable_client=yes\n", encoding="utf-8")
    initial = ("# Selected RNS original\n[reticulum]\nshare_instance=no # unchanged\n"
               "[logging]\nloglevel=0\n[interfaces]\n"
               "# Unrelated interface preserved\n[[Disabled]]\ntype=TCPServerInterface\nenabled=no\n"
               "listen_ip=127.0.0.1\nlisten_port=42424\n")
    selected.write_text(initial, encoding="utf-8")
    unchanged_primary = primary.read_bytes()
    transitions = []
    app = SimpleNamespace(configpath=str(primary), rns=SimpleNamespace(config=ConfigObj(str(selected))),
        ui=SimpleNamespace(screen=SimpleNamespace(get_cols_rows=lambda: (100, 40)),
            main_display=SimpleNamespace(update_active_sub_display=lambda: None)))
    parent = SimpleNamespace(app=app, glyphset="plain",
        shortcuts_display=SimpleNamespace(set_section_shortcuts=lambda: None),
        mark_restart_pending=lambda: transitions.append("restart"),
        switch_to_list=lambda: transitions.append("list"))
    section = next(value for value in CONFIG_SECTIONS if value["key"] == "reticulum")
    keys = ("respond_to_probes", "use_implicit_proof", "panic_on_interface_error")
    previous = NomadNetworkApp._shared_instance
    NomadNetworkApp._shared_instance = app
    try:
        def form():
            view = ConfigSectionView(parent, section)
            view.show_message = lambda *a, **k: None
            return view

        def values(view):
            return {key: view._widget_write_value(view.fields[key]["field"], view.fields[key]["widget"])
                    for key in keys}

        view = form()
        defaults = values(view)
        view.fields["respond_to_probes"]["widget"].set_state(True)
        view.fields["use_implicit_proof"]["widget"].set_state(False)
        staged = values(view)
        view.on_back(None)
        cancel_unchanged = selected.read_text(encoding="utf-8") == initial
        view = form()
        view.fields["respond_to_probes"]["widget"].set_state(True)
        view.fields["use_implicit_proof"]["widget"].set_state(False)
        view.on_save(None)
        saved = ConfigObj(str(selected))
        activated = {key: saved["reticulum"].as_bool(key) if key in saved["reticulum"]
                     else defaults[key] == "yes" for key in keys}
        saved_text = selected.read_text(encoding="utf-8")
        restored = form()
        reloaded = values(restored)
        restored.on_reset(None)
        button(parent.widget, "No")
        reset_cancelled = values(restored)
        restored.on_reset(None)
        button(parent.widget, "Yes")
        reset_values = values(restored)
        reset_unsaved = selected.read_text(encoding="utf-8") == saved_text
        restored.on_save(None)
        reset_saved = ConfigObj(str(selected)).dict()
        primary_unchanged = primary.read_bytes() == unchanged_primary
        panic = form()
        panic.fields["panic_on_interface_error"]["widget"].set_state(True)
        panic.on_save(None)
        primary_accepts_panic = ConfigObj(str(selected))["reticulum"].as_bool("panic_on_interface_error")
    finally:
        NomadNetworkApp._shared_instance = previous
    names = ("nomadnet/ui/textui/Config.py", "nomadnet/util.py")
    result = {"revision": REVISION, "sources": {name: hashlib.sha256(
        subprocess.check_output(["git", "-C", str(source), "show", f"{REVISION}:{name}"])).hexdigest()
        for name in names}, "initial": initial, "defaults": defaults, "staged": staged,
        "cancelUnchanged": cancel_unchanged, "saved": activated, "savedText": saved_text,
        "reloaded": reloaded, "resetCancelled": reset_cancelled, "reset": reset_values,
        "resetUnsaved": reset_unsaved, "resetSaved": reset_saved,
        "primaryConfigUnchanged": primary_unchanged, "primaryAcceptsPanicTrue": primary_accepts_panic,
        "transitions": transitions,
        "method": "Actual pinned ConfigSectionView reticulum path resolution/population, real CheckBox.set_state, on_back/on_save/on_reset and actual Yes/No buttons; actual ConfigObj and write_config_atomic. Headless owner/screen/modal notice/restart observers only. No router methods or source replaced; no wire claim."}
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
    Path(args.observations).write_text(json.dumps(imports, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
