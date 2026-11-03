"""Execute actual primary Browser history policy with a disclosed retrieval observer."""
import hashlib
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys

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
    module = importlib.import_module("nomadnet.ui.textui.Browser")
    if Path(module.__file__).resolve() != source / "nomadnet/ui/textui/Browser.py":
        raise RuntimeError("Wrong imported primary Browser")
    browser = module.Browser.__new__(module.Browser)
    browser.history = []
    browser.history_ptr = 0
    browser.history_inc = browser.history_dec = False
    requests = []
    browser.retrieve_url = lambda url, request_data: requests.append({"url": url, "data": request_data})
    steps = []
    for action, path, data in [
        ("visit", "/page/a.mu", None),
        ("visit", "/page/b.mu", {"field_user": "one"}),
        ("visit", "/page/c.mu", {"field_user": "two"}),
        ("back", None, None), ("back", None, None), ("back", None, None),
        ("forward", None, None), ("visit", "/page/d.mu", None),
        ("forward", None, None), ("back", None, None), ("forward", None, None),
    ]:
        before = len(requests)
        if action == "visit":
            browser.destination_hash = bytes.fromhex("11" * 16)
            browser.path = path
            browser.request_data = data
            browser.write_history()
        else:
            getattr(browser, action)()
        steps.append({"action": action, "path": path, "data": data,
                      "position": browser.history_ptr,
                      "paths": [entry[1] for entry in browser.history],
                      "request": requests[-1] if len(requests) > before else None})
        # A completed load clears these flags; only retrieval I/O is observed here.
        browser.history_inc = browser.history_dec = False
    result = {
        "commit": revision,
        "sourceSha256": hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest(),
        "method": "Actual Browser.write_history/back/forward; retrieve_url is an observation boundary, and load-completion flags are cleared between actions. No live UI or transport claim.",
        "steps": steps,
    }
    Path(sys.argv[1]).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
