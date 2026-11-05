"""Execute actual primary Node handlers, jobs, persistence and nested UI reset.

Node construction/network/audio/UI are not started. A synthetic loaded-profile
app initializes all counters and owns private files and a synthetic clock; the
actual handlers and save_peer_settings execute. This does not reproduce the
fresh primary constructor's omitted media counter (observed by the live adapter).
The nested stats_query code object is invoked with its real self closure without
constructing the Urwid screen. No primary source or response assets are copied.
"""
import argparse
import hashlib
import importlib
import json
from pathlib import Path
import subprocess
import sys
import threading
from types import FunctionType, MethodType, SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from GenerateNomadNetClientRuntimeVectors import reference, REVISION


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--observations", required=True)
    parser.add_argument("--work", required=True)
    args = parser.parse_args()
    source, imports = reference()
    module = importlib.import_module("nomadnet.Node")
    network = importlib.import_module("nomadnet.ui.textui.Network")
    from nomadnet.NomadNetworkApp import NomadNetworkApp
    import RNS.vendor.umsgpack as msgpack

    root = Path(args.work).resolve()
    root.mkdir(parents=True, exist_ok=False)
    pages, files = root / "pages", root / "files"
    pages.mkdir()
    files.mkdir()
    keys = ("node_connects", "served_page_requests", "served_file_requests", "served_media_requests")
    app = SimpleNamespace(pagespath=str(pages), filespath=str(files),
                          peer_settings={key: 0 for key in keys},
                          peersettingspath=str(root / "peer_settings"),
                          peer_settings_lock=threading.Lock())
    app.save_peer_settings = MethodType(NomadNetworkApp.save_peer_settings, app)
    node = module.Node.__new__(module.Node)
    node.app = app
    node.destination = "synthetic destination (no network)"
    node.settings_dirty = False
    for path in (pages / "ok.mu", pages / "denied.mu", pages / "ok.webp", pages / "denied.webp",
                 files / "ok.bin", files / "denied.bin"):
        path.write_bytes(b"synthetic test content")
    for path in (pages / "denied.mu.allowed", pages / "denied.webp.allowed", files / "denied.bin.allowed"):
        path.write_text("00" * 16)
    cases = []

    def invoke(name, handler, path, data=None):
        before = dict(app.peer_settings)
        node.settings_dirty = False
        if handler == "serve_default_index" or handler == "serve_file":
            result = getattr(node, handler)(path, data, bytes(16), None, 0)
        else:
            result = getattr(node, handler)(path, data, bytes(16), None, None, 0)
        if isinstance(result, list):
            result[0].close()
        cases.append({"name": name, "handler": handler, "path": path, "data": data,
                      "delta": {key: app.peer_settings[key] - before[key] for key in keys},
                      "dirty": node.settings_dirty,
                      "result": "none" if result is None else "false" if result is False
                      else "file" if isinstance(result, list) else "bytes"})

    invoke("default-index", "serve_default_index", "/page/index.mu")
    for kind, extension in (("page", "mu"), ("file", "bin")):
        for item in ("ok", "denied", "missing"):
            invoke(f"{kind}-{item}", f"serve_{kind}", f"/{kind}/{item}.{extension}")
    for name, data in (
        ("not-map", None), ("missing-key", {"path": "/media/ok.webp"}),
        ("missing-path", {"key": None}), ("wrong-type", {"path": "/media/ok.txt", "key": None}),
        ("escape", {"path": "/media/../outside.webp", "key": None}),
        ("long", {"path": "/media/" + "a" * 513 + ".webp", "key": None}),
        ("ok", {"path": "/media/ok.webp", "key": None}),
        ("denied", {"path": "/media/denied.webp", "key": None}),
        ("missing", {"path": "/media/missing.webp", "key": None}),
    ):
        invoke("media-" + name, "serve_media", "/media", data)
    node.peer_connected(SimpleNamespace(set_link_closed_callback=lambda callback: None))
    after_connection = dict(app.peer_settings)
    node.last_announce = node.last_page_refresh = node.last_file_refresh = 1e9
    node.announce_interval = 60
    node.page_refresh_interval = node.file_refresh_interval = 0
    node.last_settings_flush = 0
    node.job_interval = 5
    jobs = []
    for now in (60, 60.001):
        node.should_run_jobs = True
        with patch.object(module.time, "time", return_value=now), patch.object(
            module.time, "sleep", side_effect=lambda seconds: setattr(node, "should_run_jobs", False)
        ):
            node._Node__jobs()
        jobs.append({"now": now, "dirty": node.settings_dirty,
                     "saved": Path(app.peersettingspath).exists()})
    persisted = msgpack.unpackb(Path(app.peersettingspath).read_bytes())
    code = next(item for item in network.NodeInfo.__init__.__code__.co_consts
                if hasattr(item, "co_name") and item.co_name == "stats_query")
    owner = SimpleNamespace(app=app)
    cell = (lambda: owner).__closure__[0]
    FunctionType(code, network.__dict__, closure=(cell,))(None)
    reset = dict(app.peer_settings)
    reloaded = msgpack.unpackb(Path(app.peersettingspath).read_bytes())
    sources = {}
    for relative in ("nomadnet/Node.py", "nomadnet/NomadNetworkApp.py", "nomadnet/ui/textui/Network.py"):
        sources[relative] = hashlib.sha256(subprocess.check_output(
            ["git", "-C", str(source), "show", f"{REVISION}:{relative}"])).hexdigest()
    result = {"revision": REVISION, "sources": sources, "cases": cases,
              "afterConnection": after_connection, "jobs": jobs,
              "persisted": persisted, "reset": reset, "reloaded": reloaded,
              "jobInterval": module.Node.JOB_INTERVAL,
              "flushInterval": module.Node.SETTINGS_FLUSH_INTERVAL, "boundary": __doc__}
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    Path(args.observations).write_text(json.dumps({"imports": imports, "nodeModule": module.__file__,
                                                 "networkModule": network.__file__}, indent=2) + "\n",
                                      encoding="utf-8")


if __name__ == "__main__":
    main()
