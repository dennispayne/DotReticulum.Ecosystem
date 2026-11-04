"""Execute primary message formatting and RRC widgets with a disclosed headless UI host."""
import hashlib
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

from reference_environment import verify_reference


def flatten(widget):
    if hasattr(widget, "get_text"):
        return [widget.get_text()[0]]
    base = getattr(widget, "base_widget", widget)
    if base is not widget:
        return flatten(base)
    if hasattr(widget, "contents"):
        return [line for child, _ in widget.contents for line in flatten(child)]
    if type(widget).__name__ == "Divider":
        return [line.decode("utf-8") for line in widget.render((80,)).text]
    raise RuntimeError(f"Unsupported observed widget: {type(widget).__name__}")


def main():
    verify_reference(require_lxmf=True)
    source = Path(os.environ["DOTRETICULUM_NOMADNET_SOURCE"]).resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2":
        raise RuntimeError("Unexpected primary revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Modified primary source")
    sys.path.insert(0, str(source))
    import LXMF
    import urwid
    from RNS.Utilities.rngit.util import MarkdownToMicron
    from RNS.Utilities.rngit.highlight import SyntaxHighlighter
    from nomadnet import util
    from nomadnet.ui import TextUI
    from nomadnet.ui.textui import MicronParser, Channels
    from nomadnet.NomadNetworkApp import NomadNetworkApp

    if Path(Channels.__file__).resolve() != source / "nomadnet/ui/textui/Channels.py":
        raise RuntimeError("Wrong imported primary Channels")
    host = SimpleNamespace(config={"textui": {"theme": TextUI.THEME_DARK}},
                           ui=SimpleNamespace(glyphs={"arrow_l": "<", "arrow_r": ">", "info": "i", "warning": "!"},
                                              screen=urwid.raw_display.Screen(), colormode=256),
                           rrc=SimpleNamespace(get_nickname=lambda: "Native"),
                           identity=SimpleNamespace(hash=b"\x22" * 16),
                           rrc_nick_colors=False, rrc_mention_color=None, rrc_color_mention_timestamps=False,
                           rrc_ui_justify_msgs=False, rrc_ui_space_msgs=False)
    original = NomadNetworkApp.get_shared_instance
    NomadNetworkApp.get_shared_instance = staticmethod(lambda: host)
    bodies = [
        ("empty", ""),
        ("ordinary", "Plain generated message"),
        ("emphasis", "**BOLD** and *ITALIC* with `literal_code`"),
        ("escaped-micron", "¦!MICRON_BOLD¦! and ¦*MICRON_ITALIC¦*"),
        ("raw-micron", "`!RAW_BOLD`! and `*RAW_ITALIC`*"),
        ("heading", "# First heading\n\n## Second heading\nBody"),
        ("list", "List:\n- alpha\n- **beta**"),
        ("quote", "> first quotation\n> second quotation"),
        ("link", "Read [label](https://example.invalid/test)"),
        ("code", "```\nconst example = 1;\n```"),
        ("table", "| Name | Value |\n| --- | --- |\n| A | B |"),
        ("alignment", "`rRight `cCenter `lLeft"),
        ("multiline", "First line\n\nSecond line"),
        ("live-formatted", "**PRIMARY_FORMATTED** and ¦!MICRON_FORMATTED¦!"),
    ]
    conversation = []
    rrc = []
    converter = MarkdownToMicron(max_width=80, syntax_highlighter=SyntaxHighlighter(), url_scope=None)
    try:
        for name, body in bodies:
            formatted = converter.format_block(body)
            message_body = util.strip_non_formatting_tags(formatted)
            widgets = MicronParser.markup_to_attrmaps(util.strip_modifiers(message_body),
                                                     url_delegate=None, fg_color="bbb", bg_color=None)
            conversation.append({"name": name, "input": body, "micron": formatted,
                                 "visible": "\n".join(line for widget in widgets for line in flatten(widget))})
            for markdown in (False, True):
                for micron in (False, True):
                    host.rrc_ui_render_markdown = markdown
                    host.rrc_ui_render_micron = micron
                    message = SimpleNamespace(kind="msg", text=body, nick="Reference",
                                              src=b"\x11" * 16, ts=100000.0)
                    widget = Channels._message_widget(host, None, "generated", message)
                    lines = flatten(widget)
                    prefix = lines[0].index("<Reference>") + len("<Reference> ")
                    lines[0] = lines[0][prefix:]
                    rrc.append({"name": name, "input": body, "markdown": markdown, "micron": micron,
                                "visible": "\n".join(lines)})
    finally:
        NomadNetworkApp.get_shared_instance = staticmethod(original)
    names = []
    for name in ["Native profile", "Native café 雨", "", None, "line\nbreak"]:
        calls = []
        state = SimpleNamespace(peer_settings={"display_name": "old", "last_announce": 123.0},
                                lxmf_destination=SimpleNamespace(display_name="old"),
                                save_peer_settings=lambda: calls.append("save"),
                                message_router=SimpleNamespace(announce=lambda *_: calls.append("announce")))
        NomadNetworkApp.set_display_name(state, name)
        names.append({"input": name, "stored": state.peer_settings["display_name"],
                      "destination": state.lxmf_destination.display_name,
                      "lastAnnounce": state.peer_settings["last_announce"], "calls": calls})
    rns_module = importlib.import_module("RNS.Utilities.rngit.util")
    result = {
        "commit": revision,
        "rnsCommit": "a29bfb51c468945c96a1ee22cae0114e993e2391",
        "sourceSha256": {name: hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest()
                         for name, module in [("Channels", Channels), ("MicronParser", MicronParser),
                                              ("util", util), ("RNSMarkdown", rns_module),
                                              ("NomadNetworkApp", importlib.import_module("nomadnet.NomadNetworkApp"))]},
        "method": "Actual MarkdownToMicron.format_block, primary util functions/MicronParser, Channels._message_widget and NomadNetworkApp.set_display_name. Only a headless shared UI supplies theme/glyphs/settings; formatting implementations are not replaced. Timestamp/nickname prefix is structurally excluded. No live or pixel-identical UI claim.",
        "rendererMarkdown": LXMF.RENDERER_MARKDOWN,
        "conversation": conversation, "rrc": rrc, "displayNames": names,
    }
    Path(sys.argv[1]).write_text(json.dumps(result, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
