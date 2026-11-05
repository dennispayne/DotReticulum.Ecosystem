"""Execute the pinned Guide reader, topic selection and delegates without emitting assets.

The real GuideDisplay/GuideEntry and MicronParser execute on every real topic.
A synthetic app singleton, Urwid screen/palette and immediate alarm owner replace
the terminal/application shell. Browser handoffs are observed, not fetched.
The image resolver returns None: upstream demo assets are neither decoded nor
copied; image-bearing topics exercise the primary's unresolved-image placeholder.
Synthetic markup exercises anchor offsets and scrolling without copying a demo.
This is headless reader evidence, not an unmodified primary fullscreen session.
"""
import argparse
import hashlib
import importlib
import json
from pathlib import Path
import subprocess
from types import SimpleNamespace
from unittest.mock import patch

from GenerateNomadNetClientRuntimeVectors import reference, REVISION


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--observations", required=True)
    args = parser.parse_args()
    source, imports = reference()
    guide = importlib.import_module("nomadnet.ui.textui.Guide")
    textui = importlib.import_module("nomadnet.ui.TextUI")
    import nomadnet
    import urwid

    calls = []
    alarms = []
    screen = urwid.raw_display.Screen()
    screen.get_cols_rows = lambda: (120, 30)

    def alarm(delay, callback):
        alarms.append(delay)
        callback(None, None)

    app = SimpleNamespace(firstrun=False,
        config={"textui": {"image_loading": 0, "clipboard_copy": False, "theme": "dark"}},
        ui=SimpleNamespace(screen=screen, colormode=256, glyphs={row[0]: row[1] for row in textui.GLYPHS},
            loop=SimpleNamespace(screen=screen, set_alarm_in=alarm),
            main_display=SimpleNamespace(show_network=lambda event: calls.append({"showNetwork": True}),
                sub_displays=SimpleNamespace(network_display=SimpleNamespace(browser=SimpleNamespace(
                    handle_link=lambda target, fields: calls.append({"target": target, "fields": fields})))))))
    previous = nomadnet.NomadNetworkApp._shared_instance
    try:
        nomadnet.NomadNetworkApp._shared_instance = app
        reader = guide.GuideDisplay(app)
        initial = {"focus": reader.columns.focus_position,
                   "hasContent": hasattr(reader, "_content_scrollable")}
        listed = [entry._original_widget.topic_name for entry in reader.left_area.topic_list]
        topics = []
        for name, markup in guide.TOPICS.items():
            entry = guide.GuideEntry(app, reader.left_area, reader, name)
            with patch.object(guide.GuideLinkDelegate, "resolve_image", return_value=None):
                entry.display_topic(None, name)
            maps = reader._content_attrmaps
            topics.append({"name": name, "listed": name in listed,
                "characters": len(markup), "utf8Sha256": hashlib.sha256(markup.encode()).hexdigest(),
                "widgets": len(maps), "anchors": len(getattr(maps, "anchors", {})),
                "headings": len(getattr(maps, "header_rows", [])),
                "focusAfterSelection": reader.columns.focus_position})
        app.firstrun = True
        first = guide.GuideDisplay(app)
        first_run = {"hasContent": hasattr(first, "_content_scrollable"),
                     "focus": first.columns.focus_position,
                     "selected": first.left_area.ilb.get_selected_item()._original_widget.topic_name}
        synthetic = ">Synthetic heading\n\n" + "\n\n".join(f"row {i:03d}" for i in range(40)) + "\n\n`:destination\n>>Synthetic end\n"
        maps = guide.markup_to_attrmaps(synthetic, url_delegate=guide.GuideLinkDelegate(app, reader))
        reader.set_content_widgets(maps)
        size = (75, 8)
        reader._content_scrollable.render(size)
        positions = []
        for anchor in ("destination", "missing", ""):
            before = reader._content_scrollable.get_scrollpos()
            reader.jump_to_anchor(anchor)
            positions.append({"anchor": anchor, "before": before,
                              "after": reader._content_scrollable.get_scrollpos()})
        reader.focus_topics()
        focus_topics = reader.columns.focus_position
        reader.focus_reader()
        focus_reader = reader.columns.focus_position
        delegate = guide.GuideLinkDelegate(app, reader)
        for target in ("", "#destination", "#missing", "0123456789abcdef0123456789abcdef:/page/synthetic.mu"):
            delegate.handle_link(target, {"field_sample": "synthetic"})
        activations = []
        text = guide.ListEntry("synthetic")
        urwid.connect_signal(text, "click", lambda *args: activations.append("click"))
        keys = [{"key": key, "returned": text.keypress((40,), key)} for key in ("x", "enter", " ")]
        mouse = [{"button": button, "accepted": text.mouse_event((40,), "mouse press", button, 0, 0, True)}
                 for button in (1, 2, 3)]
        files = ("nomadnet/ui/textui/Guide.py", "nomadnet/ui/textui/Main.py",
                 "nomadnet/ui/textui/MicronParser.py", "nomadnet/vendor/Scrollable.py")
        result = {"revision": REVISION,
            "sources": {name: hashlib.sha256(subprocess.check_output(
                ["git", "-C", str(source), "show", f"{REVISION}:{name}"])).hexdigest() for name in files},
            "listedTopics": listed, "topics": topics, "initial": initial, "firstRun": first_run,
            "syntheticAnchorPositions": positions, "focus": {"topics": focus_topics, "reader": focus_reader},
            "delegate": {"calls": calls, "alarms": alarms}, "activation": {"keys": keys, "mouse": mouse, "clicks": len(activations)},
            "boundary": __doc__}
    finally:
        nomadnet.NomadNetworkApp._shared_instance = previous
    Path(args.output).write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    Path(args.observations).write_text(json.dumps({"imports": imports, "guideModule": guide.__file__,
        "urwidPath": urwid.__file__, "urwidVersion": urwid.__version__}, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
