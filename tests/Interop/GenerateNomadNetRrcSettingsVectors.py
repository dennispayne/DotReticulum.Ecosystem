"""Execute pinned RRC history/configuration and actual headless Channels widgets."""
import argparse
import ast
import hashlib
import importlib
import json
from pathlib import Path
import re
import subprocess
import time
from types import SimpleNamespace

from GenerateNomadNetClientRuntimeVectors import reference, REVISION


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--observations", required=True)
    parser.add_argument("--work", required=True)
    args = parser.parse_args()
    source, imports = reference()
    import urwid
    from RNS.vendor.configobj import ConfigObj
    from nomadnet.NomadNetworkApp import NomadNetworkApp
    from nomadnet.RRC import RRCManager, RRCHub, RRCMessage
    from nomadnet.ui import TextUI
    from nomadnet.ui.textui import Channels

    root = Path(args.work).resolve()
    root.mkdir(parents=True, exist_ok=False)
    owner = SimpleNamespace(configpath=str(root / "config"), configdir=str(root),
                            force_console_log=False, logfilepath=str(root / "synthetic.log"))
    owner.applyConfig = lambda: NomadNetworkApp.applyConfig(owner)
    NomadNetworkApp.createDefaultConfig(owner)
    fields = {
        "history_per_room_cap": "rrc_history_per_room_cap",
        "filter_loaded_history": "rrc_filter_loaded_history",
        "ephemeral_notices": "rrc_ephemeral_notices",
        "color_mention_timestamps": "rrc_color_mention_timestamps",
        "nick_colors": "rrc_nick_colors",
        "justify_msgs": "rrc_ui_justify_msgs",
        "space_msgs": "rrc_ui_space_msgs",
        "show_gutters": "rrc_show_gutters",
        "mention_color": "rrc_mention_color",
        "nick_colors_theme": "rrc_nick_colors_theme",
        "enable_esoterics": "rrc_enable_esoterics",
    }
    # Constructor defaults for optional fields; applyConfig owns every configured value.
    for key, value in {"rrc_mention_color": None, "rrc_nick_colors_theme": None,
                       "rrc_enable_esoterics": False}.items():
        if not hasattr(owner, key):
            setattr(owner, key, value)
    defaults = {key: getattr(owner, attr) for key, attr in fields.items()}
    owner.identity = SimpleNamespace(hash=b"\x22" * 16)
    owner.peer_settings = {"display_name": "Native"}
    owner.storagepath = str(root / "storage")
    manager = RRCManager(owner)
    hub = RRCHub(manager, b"\x33" * 16)
    manager.hubs.append(hub)
    hub.messages["room"] = []
    owner.rrc_history_per_room_cap = 0
    for kind, body in [("msg", "first"), ("notice", "notice"), ("system", "system"),
                       ("action", "action"), ("error", "error"), ("msg", "last")]:
        hub._record_message(RRCMessage(kind, "room", b"\x11" * 16, "Peer", body, 4102444800000))
    histories = []
    for cap in (0, 1, 2, 500):
        for filtering in (False, True):
            owner.rrc_history_per_room_cap = cap
            owner.rrc_filter_loaded_history = filtering
            hub._load_history()
            histories.append({"cap": cap, "filter": filtering,
                              "texts": [m.text for m in hub.messages["room"]]})
    owner.rrc_history_per_room_cap = 2
    hub.messages["room"] = []
    for body in ("a", "b", "c"):
        hub._record_message(RRCMessage("msg", "room", b"\x11" * 16, "Peer", body, 4102444800000))
    live_cap = [m.text for m in hub.messages["room"]]
    owner.rrc_ephemeral_notices = 60
    now = time.time()
    hub.messages["room"] = [
        RRCMessage(kind, "room", b"\x11" * 16, "Peer", label, int((now-age)*1000))
        for kind, label, age in [("notice", "old notice", 61), ("system", "old system", 61),
                                 ("error", "old error", 61), ("msg", "old message", 61),
                                 ("notice", "fresh notice", 59)]]
    hub._last_history_clean = 0
    hub._clean_history()
    cleaned = [m.text for m in hub.messages["room"]]

    owner.config = {"textui": {"theme": TextUI.THEME_DARK}}
    owner.ui = SimpleNamespace(glyphs={"arrow_l": "<", "arrow_r": ">", "info": "i", "warning": "!"},
                               screen=urwid.raw_display.Screen(), colormode=2**24)
    owner.rrc = manager
    owner.rrc_ui_render_markdown = False
    owner.rrc_ui_render_micron = False
    owner.rrc_nick_colors_theme = ["112233", "445566", "778899"]
    hub.nicks = {b"\x11" * 16: "Peer", b"\x22" * 16: "Native"}
    previous = NomadNetworkApp._shared_instance
    NomadNetworkApp._shared_instance = owner
    widgets = []
    try:
        for colors in (False, True):
            for timestamp in (False, True):
                for justify in (False, True):
                    for spacing in (False, True):
                        owner.rrc_nick_colors = colors
                        owner.rrc_color_mention_timestamps = timestamp
                        owner.rrc_ui_justify_msgs = justify
                        owner.rrc_ui_space_msgs = spacing
                        owner.rrc_mention_color = "ABCDEF"
                        message = RRCMessage("msg", "room", b"\x11" * 16, "Peer",
                                             "Hello @Native and @Peer", 1700000000000)
                        widget = Channels._message_widget(owner, hub, "room", message)
                        canvas = widget.render((48,))
                        segments = []
                        for row in canvas.content():
                            segments.append([{"text": text.decode("utf-8"),
                                              "foreground": getattr(attr, "foreground", str(attr)),
                                              "bold": getattr(attr, "bold", False)}
                                             for attr, _, text in row])
                        widgets.append({"colors": colors, "timestamp": timestamp, "justify": justify,
                                        "spacing": spacing,
                                        "lines": [re.sub(r"\[\d\d:\d\d:\d\d\]", "[<time>]",
                                                        row.decode("utf-8").rstrip()) for row in canvas.text],
                                        "segments": segments})
        gutter = []
        room_owner = SimpleNamespace(users_visible=False, show_gutters=False, chat_box=urwid.Text("chat"),
                                     users_box=urwid.Text("users"), users_gutter=urwid.Text(">"),
                                     columns=urwid.Columns([urwid.Text("chat")]))
        for visible in (False, True):
            for gutters in (False, True):
                room_owner.users_visible, room_owner.show_gutters = visible, gutters
                Channels.RoomWidget._apply_users_visibility(room_owner)
                gutter.append({"usersVisible": visible, "gutters": gutters,
                               "columns": len(room_owner.columns.contents)})
    finally:
        NomadNetworkApp._shared_instance = previous
    esoteric_reads = 0
    for path in (source / "nomadnet").rglob("*.py"):
        tree = ast.parse(path.read_text(encoding="utf-8"))
        esoteric_reads += sum(isinstance(node, ast.Attribute) and node.attr == "rrc_enable_esoterics"
                             and isinstance(node.ctx, ast.Load) for node in ast.walk(tree))
    names = ("nomadnet/RRC.py", "nomadnet/ui/textui/Channels.py", "nomadnet/NomadNetworkApp.py")
    result = {"revision": REVISION, "sources": {name: hashlib.sha256(
        subprocess.check_output(["git", "-C", str(source), "show", f"{REVISION}:{name}"])).hexdigest()
        for name in names}, "defaults": defaults, "history": histories, "liveCap": live_cap,
        "cleaned": cleaned, "colors": {str(n): Channels.get_nick_color(bytes([n])*16,
                    Channels.theme_dark, owner) for n in (0, 1, 17, 34, 255)},
        "widgets": widgets, "gutters": gutter, "mentionScans": [
            {"nick": nick, "text": text, "matches": [text[start:end] for start, end, _, _ in
                                                     Channels._scan_mentions(text, nick)]}
            for nick, text in [("Nätive Name", "Hello @NÄTIVE NAME! x@Nätive Name @Nätive Names"),
                               ("Native", "Hello @native @Native_ x@Native @Native!")]],
        "esotericRuntimeAttributeReads": esoteric_reads,
        "method": "Actual createDefaultConfig/applyConfig; RRCManager/RRCHub constructors and real bounded synthetic history files using _record_message/_load_history/_clean_history; actual Channels._message_widget rendered by Urwid and _apply_users_visibility on synthetic owner. Headless UI host only; no replaced core methods, sockets or copied assets. Esoterics consumers inventoried with AST. Cleanup uses real clock with one-second age margins, not an injected clock."}
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
    Path(args.observations).write_text(json.dumps(imports, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
