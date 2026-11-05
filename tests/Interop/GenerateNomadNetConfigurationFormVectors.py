"""Execute primary section forms/default/reset/save and ConfigObj grammar."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
from types import SimpleNamespace

from GenerateNomadNetClientRuntimeVectors import reference, REVISION


def button(widget, label):
    import urwid
    pending, seen = [widget], set()
    while pending:
        item = pending.pop()
        if id(item) in seen:
            continue
        seen.add(id(item))
        if isinstance(item, urwid.Button) and item.get_label() == label:
            item._emit("click")
            return
        for name in ("_w", "original_widget", "top_w"):
            if hasattr(item, name):
                pending.append(getattr(item, name))
        if hasattr(item, "contents"):
            values = item.contents
            if hasattr(values, "values"):
                values = values.values()
            pending.extend(entry[0] for entry in values)
    raise RuntimeError("Actual primary button not found: " + label)


def generate(output, observations, work):
    source, imports = reference()
    from RNS.vendor.configobj import ConfigObj
    import RNS.vendor.configobj as configobj
    from nomadnet.ui.textui.Config import ConfigSectionView, CONFIG_SECTIONS
    from nomadnet.NomadNetworkApp import NomadNetworkApp
    from nomadnet.util import write_config_atomic

    root = Path(work).resolve()
    root.mkdir(parents=True, exist_ok=False)
    primary, rns = root / "config", root / "rns-config"
    initial = ("# Synthetic original observation\n"
               "[client] # retained section comment\n"
               "announce_interval = 90 # retained inline\n"
               "downloads_path = 'quoted, # path'\n"
               "unknown = retained\n"
               "[textui]\neditor = installed-editor\n"
               "[other]\nvalue = '''first\n# literal second\nthird'''\n"
               "[[child]]\nquoted = 'a,b'\n")
    primary.write_text(initial, encoding="utf-8", newline="\n")
    rns.write_text("[reticulum]\nshare_instance = no\n[interfaces]\n", encoding="utf-8", newline="\n")
    transitions, notices = [], []
    app = SimpleNamespace(configpath=str(primary), rns=SimpleNamespace(config=ConfigObj(str(rns))),
                          ui=SimpleNamespace(screen=SimpleNamespace(get_cols_rows=lambda: (100, 40)),
                                             main_display=SimpleNamespace(update_active_sub_display=lambda: None)))
    parent = SimpleNamespace(app=app, glyphset="plain",
                             shortcuts_display=SimpleNamespace(set_section_shortcuts=lambda: None),
                             mark_restart_pending=lambda: transitions.append("restart"),
                             switch_to_list=lambda: transitions.append("list"))
    previous = NomadNetworkApp._shared_instance
    NomadNetworkApp._shared_instance = app
    try:
        catalog = []
        for section in CONFIG_SECTIONS:
            view = ConfigSectionView(parent, section)
            # The observer substitutes only modal notice display, not form/save/reset methods.
            view.show_message = lambda message, **kwargs: notices.append(message)
            fields = []
            for entry in view.fields.values():
                field = entry["field"]
                fields.append({"key": field["config_key"], "type": field["type"],
                               "default": view._default_write_value(field),
                               "options": field.get("options", []),
                               "validation": field.get("validation", [])})
            catalog.append({"key": section["key"], "target": section.get("configfile", "nomadnet"), "fields": fields})
        client = ConfigSectionView(parent, next(s for s in CONFIG_SECTIONS if s["key"] == "client"))
        client.show_message = lambda message, **kwargs: notices.append(message)
        read = lambda: {key: client._widget_write_value(entry["field"], entry["widget"])
                        for key, entry in client.fields.items()}
        loaded = read()
        client.fields["announce_interval"]["widget"].edit_text = "123"
        client.on_back(None)
        cancel_unchanged = primary.read_text(encoding="utf-8") == initial
        client.on_reset(None)
        button(parent.widget, "No")
        reset_cancel = read()["announce_interval"]
        client.on_reset(None)
        button(parent.widget, "Yes")
        reset = read()
        reset_unsaved = primary.read_text(encoding="utf-8") == initial
        client.on_save(None)
        saved = ConfigObj(str(primary)).dict()
        saved_comments = {"section": ConfigObj(str(primary)).inline_comments["client"],
                          "inline": ConfigObj(str(primary))["client"].inline_comments["announce_interval"]}
    finally:
        NomadNetworkApp._shared_instance = previous

    defaults_path = root / "defaults" / "config"
    default_owner = SimpleNamespace(configpath=str(defaults_path), configdir=str(defaults_path.parent),
                                    force_console_log=False, logfilepath="synthetic.log")
    default_owner.applyConfig = lambda: NomadNetworkApp.applyConfig(default_owner)
    NomadNetworkApp.createDefaultConfig(default_owner)
    defaults = ConfigObj(str(defaults_path)).dict()
    applied_defaults = {name: getattr(default_owner, name) for name in
                        ("announce_interval", "node_announce_interval", "node_announce_at_start",
                         "page_refresh_interval", "file_refresh_interval", "lxmf_max_incoming_size",
                         "required_stamp_cost", "rrc_show_gutters")}
    cases = [
        ("root-setting", "enable_node=yes"),
        ("literal-inner-quotes", "[section#literal] # comment\nkey'part = unquoted \"quote # comment\n"),
        ("quoted-lists", '# original\nroot = scalar\n[one]\na = "a,b#c"\nb = alpha, "two,#", \'three\'\nc = x,\nd = ,\ne =\n'),
        ("nested-comments", '[one] # section\nkey = "value#not-comment" # inline\n[[child]]\n"quoted key" = literal\\path\n[[[deep]]]\nvalue = yes\n[two]\nvalue = no\n'),
        ("multiline", '[one]\na = """first\n# literal\nlast""" # tail\nb = \'\'\'inline """ literal\'\'\'\n'),
        ("interpolation", 'base = root\n[DEFAULT]\nfallback = inherited\n[one]\na = %(base)s/%(fallback)s\n[[child]]\nb = %(a)s\n'),
        ("quoted-sections", '["a # b"]\n\'key=quoted\' = "value"\n'),
        ("percent", '[one]\na = 100%%\nb = 100% literal\n'),
        ("empty-middle", '[one]\na = a,,b\n'),
        ("duplicate-key", '[one]\na = x\na = y\n'),
        ("duplicate-section", '[one]\na = x\n[one]\nb = y\n'),
        ("bad-depth", '[one]\n[[[deep]]]\na = x\n'),
        ("unterminated", '[one]\na = """first\n'),
        ("bad-list", '[one]\na = "x" broken\n'),
        ("missing-interpolation", '[one]\na = %(absent)s\n'),
        ("cyclic-interpolation", '[one]\na = %(b)s\nb = %(a)s\n'),
    ]
    grammar = []
    for name, text in cases:
        try:
            cfg = ConfigObj(text.splitlines())
            value = cfg.dict()
            file = root / (name + ".config")
            cfg.filename = str(file)
            write_config_atomic(cfg)
            grammar.append({"name": name, "text": text, "value": value,
                            "written": file.read_text(encoding="utf-8")})
        except Exception as error:
            grammar.append({"name": name, "text": text, "error": type(error).__name__})
    def digest(path):
        return hashlib.sha256(subprocess.check_output(["git", "-C", str(source), "show", f"{REVISION}:{path}"])).hexdigest()
    result = {"revision": REVISION,
              "sources": {name: digest(name) for name in
                          ("nomadnet/ui/textui/Config.py", "nomadnet/NomadNetworkApp.py", "nomadnet/util.py",
                           "nomadnet/vendor/additional_urwid_widgets/FormWidgets.py")},
              "configobjSha256": hashlib.sha256(Path(configobj.__file__).read_bytes()).hexdigest(),
              "method": "Actual ConfigSectionView construction/population/default/reset/save/back, actual confirmation buttons and atomic writer, actual createDefaultConfig/applyConfig on minimal owner; UI notice/screen/restart observers only. Actual pinned RNS ConfigObj parser/dict/write. Synthetic config; no UI guide/assets copied.",
              "catalog": catalog, "initial": initial, "loaded": loaded, "cancelUnchanged": cancel_unchanged,
              "resetCancelledInterval": reset_cancel, "reset": reset, "resetUnsaved": reset_unsaved,
              "saved": saved, "savedComments": saved_comments, "transitions": transitions,
              "defaults": defaults, "appliedDefaults": applied_defaults, "grammar": grammar}
    Path(output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
    Path(observations).write_text(json.dumps({"imports": imports, "configobj": configobj.__file__,
                                             "work": str(root)}, indent=2) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--observations", required=True)
    parser.add_argument("--work", required=True)
    args = parser.parse_args()
    generate(args.output, args.observations, args.work)
