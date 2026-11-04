"""Execute primary Directory and Urwid entry code; freeze only the observation clock."""
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

from GenerateNomadNetClientRuntimeVectors import reference, REVISION


def observe(args):
    source, imports = reference()
    from nomadnet.NomadNetworkApp import NomadNetworkApp

    def observations():
        try:
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                app = NomadNetworkApp._shared_instance
                if app is not None and hasattr(app, "ui"):
                    break
                time.sleep(0.02)
            if app is None or not hasattr(app, "ui"):
                raise RuntimeError("Primary constructor did not reach NoneUI")
            directory = app.directory
            rows = []
            for kind, number, name in (("peer", 1, "First"), ("node", 2, "Node"),
                                        ("peer", 1, "Updated"), ("pn", 3, "Relay"),
                                        ("node", 2, "Node again"), ("peer", 4, "")):
                data = name.encode()
                address = bytes([number]) * 16
                if kind == "peer":
                    directory.lxmf_announce_received(address, data)
                elif kind == "node":
                    directory.node_announce_received(address, data, bytes(16))
                else:
                    directory.pn_announce_received(address, data, bytes(16), bytes(16))
                rows.append([{"hash": e[1].hex(), "data": e[2].hex(), "kind": e[3]}
                             for e in directory.announce_stream])
            directory.lxmf_announce_received(bytes([5]) * 16, None)
            null_count = len(directory.announce_stream)
            directory.save_to_disk()
            directory._node_announces = []
            directory._peer_announces = []
            directory._pn_announces = []
            directory.load_from_disk()
            reloaded = [{"hash": e[1].hex(), "data": e[2].hex(), "kind": e[3]}
                        for e in directory.announce_stream]
            for i in range(260):
                directory.lxmf_announce_received(i.to_bytes(16, "big"), b"bounded")
            bound = len(directory._peer_announces)
            print("@@" + json.dumps({"compact": app.compact_stream, "rows": rows,
                                     "nullCount": null_count, "reloaded": reloaded,
                                     "peerBound": bound, "imports": imports}), flush=True)
            os._exit(0)
        except Exception as error:
            print("@@" + json.dumps({"error": str(error)}), flush=True)
            os._exit(1)

    threading.Thread(target=observations, daemon=True).start()
    NomadNetworkApp(configdir=args.profile, rnsconfigdir=args.rnsconfig, daemon=True, force_console=True)


def generate(args):
    source, imports = reference()
    work = Path(args.work).resolve()
    work.mkdir(parents=True, exist_ok=True)
    cases = []
    for name, value in (("generated", None), ("absent", None), ("compact", "yes"), ("full", "no"),
                         ("on", "on"), ("false", "False")):
        case = work / name
        profile, rns = case / "profile", case / "rns"
        if (profile / "config").exists():
            raise RuntimeError("Use a fresh --work directory; primary persisted history must not be reused")
        profile.mkdir(parents=True, exist_ok=True)
        rns.mkdir(parents=True, exist_ok=True)
        config = "[client]\nuser_interface=none\nannounce_at_start=no\nperiodic_lxmf_sync=no\n"
        if value is not None:
            config += "compact_announce_stream=" + value + "\n"
        config += "[node]\nenable_node=no\n[logging]\nloglevel=0\ndestination=console\n"
        if name != "generated":
            (profile / "config").write_text(config, encoding="utf-8")
        (rns / "config").write_text("[reticulum]\nshare_instance=no\n[logging]\nloglevel=0\n[interfaces]\n", encoding="utf-8")
        process = subprocess.run([sys.executable, "-u", str(Path(__file__).resolve()), "--observe",
                                  "--profile", str(profile), "--rnsconfig", str(rns)],
                                 cwd=case, capture_output=True, text=True, timeout=60)
        (case / "stdout").write_text(process.stdout, encoding="utf-8")
        (case / "stderr").write_text(process.stderr, encoding="utf-8")
        events = [json.loads(line[2:]) for line in process.stdout.splitlines() if line.startswith("@@")]
        if process.returncode or len(events) != 1 or "error" in events[0]:
            raise RuntimeError(f"Primary observation failed: {case}")
        events[0].pop("imports")
        cases.append({"name": name, "input": value, "observed": events[0]})
    from RNS.vendor.configobj import ConfigObj
    invalid = []
    for value in ("maybe", "2", ""):
        try:
            ConfigObj(["[client]", "compact_announce_stream=" + value])["client"].as_bool("compact_announce_stream")
        except ValueError:
            invalid.append({"input": value, "rejected": True})
        else:
            raise RuntimeError("Expected ConfigObj invalid boolean rejection")
    import nomadnet.ui.textui.Network as network
    glyphs = {"cross": "X", "unknown": "?", "check": "+", "warning": "!",
              "node": "N", "peer": "P", "sent": "S"}
    host = SimpleNamespace(time_format="%X", config={"textui": {"sanitize_names": True}},
                           ui=SimpleNamespace(glyphs=glyphs),
                           directory=SimpleNamespace(trust_level=lambda _: 2,
                                                     simplest_display_str=lambda h: "<" + h.hex() + ">"))
    entries = []
    original_clock = network.time
    # Replace the module's clock object only, leaving Python's process clock unchanged.
    network.time = SimpleNamespace(time=lambda: 1735732800.0)
    try:
        for kind in ("node", "peer", "pn"):
            for name in ("Public café 雨", "x" * 36, "", "line\nbreak", "① 😀"):
                for destination in (False, True):
                    widget = network.AnnounceStreamEntry(host, (1735732800.0, b"\x11" * 16,
                                                               name.encode(), kind), None, destination)
                    text = widget.display_widget.original_widget.get_text()[0]
                    entries.append({"kind": kind, "input": name, "destination": destination,
                                    "labelSuffix": text[8:]})
    finally:
        network.time = original_clock
    files = ("nomadnet/NomadNetworkApp.py", "nomadnet/Directory.py", "nomadnet/ui/textui/Network.py",
             "nomadnet/util.py")
    (work / "imports.json").write_text(json.dumps(imports, indent=2) + "\n", encoding="utf-8")
    result = {"revision": REVISION,
              "rnsCommit": "a29bfb51c468945c96a1ee22cae0114e993e2391",
              "lxmfCommit": "e8ba02483217af43be284a72f1c059c730b7d047",
              "method": "Actual daemon constructor/applyConfig; Directory receive/save/load; AnnounceStreamEntry using a headless glyph/config host and fixed observation clock. No source modifications; no GUI equivalence claim.",
              "sources": {name: hashlib.sha256(subprocess.check_output(
                  ["git", "-C", str(source), "show", f"{REVISION}:{name}"])).hexdigest() for name in files},
              "cases": cases, "invalidBooleans": invalid, "entries": entries}
    Path(args.output).write_text(json.dumps(result, indent=2, ensure_ascii=True) + "\n",
                                 encoding="utf-8", newline="\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output")
    parser.add_argument("--work")
    parser.add_argument("--observe", action="store_true")
    parser.add_argument("--profile")
    parser.add_argument("--rnsconfig")
    args = parser.parse_args()
    observe(args) if args.observe else generate(args)
