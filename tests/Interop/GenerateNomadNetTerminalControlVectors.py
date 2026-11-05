"""Execute pinned primary forms, terminal constructor and name/menu consumers.

Headless screen/loop, main-display owner and stty observers replace OS presentation
only. Primary methods, palette/glyph selection, menu construction and timers run.
Separate actual constructors start NoneUI; actual applyConfig then reads a requested
ConfigObj text selection, so malformed TextUI cases can be observed without the
constructor's fatal exit. This is not primary interactive TextUI startup.
No primary implementation or assets are emitted.
"""
import argparse
import hashlib
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
from types import SimpleNamespace
from unittest.mock import patch

from GenerateNomadNetClientRuntimeVectors import reference, REVISION


def observe(args):
    reference()
    from RNS.vendor.configobj import ConfigObj
    from nomadnet.NomadNetworkApp import NomadNetworkApp
    def capture():
        try:
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                try:
                    app = NomadNetworkApp.get_shared_instance()
                except UnboundLocalError:
                    app = None
                if app is not None and hasattr(app, "ui"):
                    break
                time.sleep(.02)
            if app is None or not hasattr(app, "ui"):
                raise RuntimeError("Actual primary constructor did not become ready")
            app.config = ConfigObj(str(Path(args.profile) / "requested.config"))
            try:
                app.applyConfig()
                observed = {"textui": dict(app.config["textui"]), "utilities": dict(app.config["utilities"])}
            except Exception as error:
                observed = {"error": type(error).__name__, "message": str(error)}
            Path(args.output).write_text(json.dumps(observed), encoding="utf-8")
            os._exit(0)
        except Exception:
            os._exit(2)
    threading.Thread(target=capture, daemon=True).start()
    NomadNetworkApp(configdir=args.profile, rnsconfigdir=args.rnsconfig, daemon=True, force_console=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--observations", required=True)
    parser.add_argument("--work", required=True)
    parser.add_argument("--observe", action="store_true")
    parser.add_argument("--profile")
    parser.add_argument("--rnsconfig")
    args = parser.parse_args()
    if args.observe:
        observe(args)
        return
    source, imports = reference()
    from RNS.vendor.configobj import ConfigObj
    from nomadnet.NomadNetworkApp import NomadNetworkApp
    from nomadnet.ui.textui.Config import CONFIG_SECTIONS
    from nomadnet.ui.textui.Main import MenuDisplay, SubDisplays
    from nomadnet.ui.textui.Network import AnnounceTime
    from nomadnet.util import sanitize_name, strip_modifiers
    import urwid
    textui = importlib.import_module("nomadnet.ui.TextUI")

    fields = [dict(key=f["config_key"], default=f["default"], type=f["type"])
              for s in CONFIG_SECTIONS if s["key"] in ("textui", "utilities")
              for f in s["fields"]]
    observations = []
    previous = NomadNetworkApp._shared_instance
    try:
        for glyphs in ("plain", "unicode", "nerdfont", "invalid"):
            for mode in (1, 16, 88, 256, 16777216):
                for theme in (textui.THEME_DARK, textui.THEME_LIGHT):
                    alarms, properties, loops = [], [], []
                    intro = 0 if glyphs == "plain" and mode == 1 else -1 if glyphs == "plain" and mode == 16 else 0.125
                    mouse = glyphs != "plain"
                    config = {"intro_time": intro, "intro_text": "Synthetic title",
                              "theme": theme, "colormode": mode, "mouse_enabled": mouse, "glyphs": glyphs}
                    app = SimpleNamespace(config={"textui": config}, peer_settings_notice=None, version="1.4.4")
                    NomadNetworkApp._shared_instance = app
                    screen = SimpleNamespace(register_palette=lambda p: None,
                        set_terminal_properties=properties.append, reset_default_terminal_palette=lambda: None)
                    display = SimpleNamespace(widget=urwid.Text("main"), start=lambda: None)
                    def loop(widget, **kwargs):
                        loops.append(kwargs["handle_mouse"])
                        return SimpleNamespace(widget=widget,
                            set_alarm_in=lambda seconds, callback: alarms.append(seconds), run=lambda: None)
                    with patch.object(textui.os, "system", return_value=0), \
                         patch.object(textui, "ImageScreen", return_value=screen), \
                         patch.object(textui.Main, "MainDisplay", return_value=display), \
                         patch.object(textui.urwid, "MainLoop", side_effect=loop):
                        ui = textui.TextUI()
                    observations.append({"glyphs": glyphs, "mode": properties[0], "theme": theme,
                        "mouse": loops[0], "intro": intro, "introAlarm": alarms[0] if alarms else None,
                        "introText": ui.intro_display.widget.base_widget.contents[0][0].base_widget.get_text()[0]
                        if intro > 0 else None,
                        "paletteMatchesSelectedTheme": ui.palette == textui.THEMES[theme]["urwid_theme"],
                        "glyphFallbackIsUnicode": ui.glyphs == {
                            g[0]: g[textui.GLYPHSETS["unicode"]] for g in textui.GLYPHS}})
        menus = []
        class Handler:
            def __getattr__(self, _name):
                return lambda *a, **k: None
        for enabled in (False, True):
            for hidden in (False, True):
                config = ConfigObj({"utilities": {"enable_utilities": str(enabled)},
                                    "textui": {"hide_guide": hidden, "animation_interval": 3}})
                app = SimpleNamespace(config=config, enable_node=False, has_unread_conversations=lambda: False,
                    peer_settings={"last_announce": None},
                    ui=SimpleNamespace(glyphs={g[0]: g[1] for g in textui.GLYPHS}))
                menu = MenuDisplay(app, Handler())
                labels = []
                def visit(widget):
                    if isinstance(widget, urwid.Button):
                        labels.append(widget.get_label())
                    elif hasattr(widget, "original_widget"):
                        visit(widget.original_widget)
                    elif hasattr(widget, "contents"):
                        for item in widget.contents:
                            visit(item[0])
                visit(menu.widget)
                timeout = AnnounceTime(app).timeout
                menus.append({"utilities": enabled, "hideGuide": hidden, "labels": labels,
                              "utilityConstructed": SubDisplays._utilities_enabled(None, app),
                              "announceTimeout": timeout})
        samples = ["  Alice  Smith ", "① Ⅰ Ａ", "A😀€+→B", "a\u0301\u0308 \u05d0\u05b0 \u0915\u093e",
                   "A\u2028B\u2029C", "A\u200b\u202eB", "A\ue000B", "A\tB\nC\rD\x1b[31m",
                   "🕵️☝👍🏽", "a﹑b", " \u00a0  ", ""]
        names = [{"input": v, "sanitized": sanitize_name(v), "modifiers": strip_modifiers(v)} for v in samples]
    finally:
        NomadNetworkApp._shared_instance = previous
    files = ("nomadnet/ui/TextUI.py", "nomadnet/ui/textui/Config.py", "nomadnet/ui/textui/Main.py",
             "nomadnet/ui/textui/Extras.py", "nomadnet/ui/textui/Network.py", "nomadnet/util.py",
             "nomadnet/NomadNetworkApp.py", "nomadnet/Directory.py")
    work = Path(args.work).resolve()
    work.mkdir(parents=True, exist_ok=False)
    loader = []
    for name, body in (
        ("missing-defaults", "[textui]\ncolormode=256\n"),
        ("configured", "[textui]\ncolormode=88\nintro_time=.125\nintro_text=Synthetic title\ntheme=light\n"
                       "glyphs=plain\nmouse_enabled=no\nhide_guide=yes\nsanitize_names=no\nanimation_interval=2\n"
                       "[utilities]\nenable_utilities=yes\n"),
        ("missing-colormode", "[textui]\nglyphs=plain\n"),
        ("zero-animation", "[textui]\ncolormode=monochrome\nintro_time=-1\nanimation_interval=0\n"),
        ("invalid-theme", "[textui]\ncolormode=256\ntheme=invalid\n")):
        case = work / name
        profile, rns = case / "profile", case / "rns"
        profile.mkdir(parents=True)
        rns.mkdir()
        text = ("[client]\nuser_interface=text\nannounce_at_start=no\nperiodic_lxmf_sync=no\n"
                "[node]\nenable_node=no\n[logging]\nloglevel=0\ndestination=console\n" + body)
        (profile / "config").write_text(text.replace("user_interface=text", "user_interface=none"), encoding="utf-8")
        (profile / "requested.config").write_text(text, encoding="utf-8")
        (rns / "config").write_text("[reticulum]\nshare_instance=no\nenable_transport=no\n[interfaces]\n", encoding="utf-8")
        output = case / "observed.json"
        child = subprocess.run([sys.executable, __file__, "--observe", "--profile", str(profile),
            "--rnsconfig", str(rns), "--output", str(output), "--observations", args.observations,
            "--work", str(case / "unused")], capture_output=True, timeout=40, check=False)
        (case / "stdout.log").write_bytes(child.stdout)
        (case / "stderr.log").write_bytes(child.stderr)
        if child.returncode != 0:
            raise RuntimeError(f"Actual primary observation {name} failed: {child.returncode}; see retained stdout/stderr")
        loader.append({"name": name, "input": body, "observed": json.loads(output.read_text(encoding="utf-8"))})
    result = {"revision": REVISION, "sources": {name: hashlib.sha256(
        subprocess.check_output(["git", "-C", str(source), "show", f"{REVISION}:{name}"])).hexdigest()
        for name in files}, "fields": fields, "terminal": observations, "menus": menus, "names": names, "loader": loader,
        "method": __doc__}
    Path(args.output).write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    Path(args.observations).write_text(json.dumps(imports, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
