"""Execute primary NomadNet helpers; no reference behavior is reimplemented."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
from types import SimpleNamespace

from reference_environment import verify_reference


PIN = "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    source = Path(os.environ["DOTRETICULUM_NOMADNET_SOURCE"]).resolve()
    actual = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if actual != PIN:
        raise RuntimeError(f"NomadNet revision mismatch: {actual}")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("The NomadNet reference source must be clean")
    sys.path.insert(0, str(source))
    import nomadnet
    from nomadnet.Conversation import ConversationMessage
    from nomadnet.NomadNetworkApp import NomadNetworkApp
    from nomadnet.ui.textui import MicronParser
    import urwid
    if Path(nomadnet.__file__).resolve().parent != source / "nomadnet":
        raise RuntimeError("Unexpected imported NomadNet path")
    names = ["../../unsafe.txt", r"C:\capture\secret.txt", "..hidden", "\x1bhello\x00.txt", "", None,
             "abc:def:ghi", "x" * 250 + ".reallylongextension", "file.txt"]
    media = [("web/p", b"012345678901", False), (16, b"012345678901", True),
             (1, b"012345678901", True), (None, b"\x89PNG\r\n\x1a\n0000", False),
             (None, b"RIFF0000WEBP", False), (None, b"OggS00000000", True),
             (None, b"RIFF0000WAVE", True), (None, b"tiny", False)]
    result = {
        "commit": actual,
        "method": "ConversationMessage.safe_attachment_name and _ext_from_media_format",
        "sourceSha256": hashlib.sha256((source / "nomadnet/Conversation.py").read_bytes()).hexdigest(),
        "names": [{"input": name, "output": ConversationMessage.safe_attachment_name(name)} for name in names],
        "media": [{"format": fmt, "data": data.hex(), "audio": audio,
                   "extension": ConversationMessage._ext_from_media_format(fmt, data, is_audio=audio)}
                  for fmt, data, audio in media],
    }
    # Configuration-only fixture: parser requires a shared UI's theme/colour mode.
    # No parser or protocol function is replaced; this is not full UI acceptance.
    app = NomadNetworkApp.__new__(NomadNetworkApp)
    app.config = {"textui": {"theme": nomadnet.ui.TextUI.THEME_DARK}}
    app.ui = SimpleNamespace(colormode=256, screen=urwid.raw_display.Screen())
    NomadNetworkApp._shared_instance = app
    inline = ["plain [brackets]", "`!bold`! and `*italic`*", "`Ff00red`f normal",
              "`FTff0000red`f", "`B000black`b", r"\`!literal", "`[next`:/page/next.mu]",
              "`:anchor text", "one ``two", r"one\\two"]
    result["inlineMethod"] = "MicronParser.make_output(default_state(), source, None)"
    result["inlineConfiguration"] = "Uninitialised app instance with dark theme and 256-colour UI configuration only"
    result["inlineSourceSha256"] = hashlib.sha256((source / "nomadnet/ui/textui/MicronParser.py").read_bytes()).hexdigest()
    result["inline"] = [{"source": text, "text": "".join(part[1] for part in MicronParser.make_output(
        MicronParser.default_state(), text, None) or [] if isinstance(part, tuple))} for text in inline]
    fields = ["`<name`Alice>", "`<!12|password`not-a-real-secret>",
              "`<40x3|message`initial text>", "`<?|flags|a|*`Alpha>",
              "`<?|flags|b|*`Beta>", "`<?|unchecked|x`No>",
              "`<^|choice|one|*`One>", "`<^|choice|two`Two>"]
    result["fields"] = [{"source": text, "field": next(
        {key: value for key, value in part.items() if key != "style"}
        for part in MicronParser.make_output(MicronParser.default_state(), text, None) if isinstance(part, dict))}
        for text in fields]
    from nomadnet.ui.textui.Browser import Browser
    result["browserSourceSha256"] = hashlib.sha256((source / "nomadnet/ui/textui/Browser.py").read_bytes()).hexdigest()
    browser = Browser.__new__(Browser)
    browser.status = Browser.DONE
    browser.attr_maps = MicronParser.markup_to_attrmaps("\n".join(fields))
    observed = []
    browser.retrieve_url = lambda target, data: observed.append({"target": target, "data": data})
    for submission in [["*", "mode=test"], ["name", "flags", "mode=selected"], ["unchecked"], ["bad=x=y"]]:
        Browser.handle_link(browser, ":/page/submit.mu", submission)
    result["submissions"] = [{"fields": selection, **observation} for selection, observation in zip(
        [["*", "mode=test"], ["name", "flags", "mode=selected"], ["unchecked"], ["bad=x=y"]], observed)]
    result["submissionMethod"] = "Browser.handle_link with real parsed Urwid fields; retrieve_url I/O seam captures request arguments, no form logic replaced"
    result["slug"] = [{"source": text, "slug": MicronParser.slugify_micron(text)}
                      for text in ["`!Hello World`!", "日本語 Hello---World!", "`:anchor Heading_Name"]]
    descriptors = ["/page/once.mu}", "/page/refresh.mu`2`*|pid=summary}",
                   "/page/static.mu`0`name}", "/page/invalid.mu`bad`*}",
                   "/page/invalid.mu`1`a`b}"]
    result["partials"] = []
    for text in descriptors:
        parsed = MicronParser.parse_partial(text)
        widget = parsed[0] if parsed else None
        result["partials"].append({"source": text, "valid": widget is not None,
            "id": widget.partial_id if widget else None, "refresh": widget.partial_refresh if widget else None,
            "fields": widget.partial_fields if widget else None})
    browser.attr_maps = MicronParser.markup_to_attrmaps("`<root`outer>\n`{/page/child.mu`2`*|pid=child}")
    browser.page_partials = {}
    browser.start_partial_updater = lambda: None
    browser.refresh_page_layout = lambda: None
    browser.page_foreground_color = browser.page_background_color = None
    browser.detect_partials()
    partial = next(iter(browser.page_partials.values()))
    partial["request_id"] = bytes(range(8))
    partial["update_requested"] = 100
    browser.partial_received(SimpleNamespace(request_id=bytes(range(8)), response=b"`<child`inner>\nChild text"))
    result["composedSubmission"] = browser._Browser__get_partial_request_data(partial)
    result["emptyPartialSubmission"] = browser._Browser__get_partial_request_data({"fields": [""]})
    result["partialUpdated"] = partial["updated"]
    scheduled = []
    completed = threading.Event()
    browser.partial_updater_lock = threading.Lock()
    browser.app = SimpleNamespace(ui=SimpleNamespace(loop=SimpleNamespace(set_alarm_in=lambda seconds, callback: None)))
    now = time.time()
    browser.page_partials = {
        "initial": {"url": "initial", "updated": None, "refresh": None, "failed": False},
        "due": {"url": "due", "updated": now-10, "refresh": 2, "failed": False},
        "fresh": {"url": "fresh", "updated": now+100, "refresh": 2, "failed": False},
        "static": {"url": "static", "updated": now-10, "refresh": None, "failed": False},
        "failed": {"url": "failed", "updated": None, "refresh": 2, "failed": True}}
    def observe_load(item):
        scheduled.append(item["url"])
        if len(scheduled) == 2:
            completed.set()
    browser._Browser__load_partial = observe_load
    browser.update_partials()
    if not completed.wait(5):
        raise RuntimeError("Actual primary partial scheduler did not finish")
    result["partialScheduled"] = sorted(scheduled)
    result["partialObservationSeams"] = "Actual parser/detect/partial_received/request-data/scheduler; only layout, alarm installation and network load boundaries captured. No protocol function replaced."
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
