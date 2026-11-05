"""Observe pinned primary peer-control callbacks, not propagation wire behavior."""
import argparse
import hashlib
import importlib
import json
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace


REVISION = "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2"


class StopBeforeDialog(Exception):
    pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root = args.source.resolve(strict=True)

    def git(*arguments):
        return subprocess.run(
            ["git", "-C", str(root), *arguments], check=True,
            capture_output=True, text=True,
        ).stdout.strip()

    if git("rev-parse", "HEAD") != REVISION:
        raise RuntimeError("The NomadNet checkout is not the selected primary revision")
    if git("status", "--porcelain", "--untracked-files=all", "--", "nomadnet"):
        raise RuntimeError("The primary NomadNet source is not clean")
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference
    verify_reference(require_lxmf=True)
    sys.path.insert(0, str(root))
    module = importlib.import_module("nomadnet.ui.textui.Network")
    source = Path(module.__file__).resolve(strict=True)
    if not source.is_relative_to(root / "nomadnet"):
        raise RuntimeError("The primary Network module came from another installation")
    destination = bytes.fromhex("00112233445566778899aabbccddeeff")
    rows = []
    for name, selected, exists, previous, now in [
        ("no-selection", False, True, 100.0, 1000.0),
        ("missing-peer", True, False, 100.0, 1000.0),
        ("before-grace", True, True, 100.0, 109.999),
        ("exact-grace", True, True, 100.0, 110.0),
        ("after-grace", True, True, 100.0, 110.0001),
        ("future-attempt", True, True, 110.0, 100.0),
        ("never-attempted", True, True, 0.0, 1000.0),
    ]:
        events = []
        peer = SimpleNamespace(
            last_sync_attempt=previous, next_sync_attempt=999.0,
            sync=lambda: events.append("sync"),
        )
        item = SimpleNamespace(original_widget=SimpleNamespace(destination_hash=destination))
        owner = SimpleNamespace(
            ilb=SimpleNamespace(get_selected_item=lambda: item if selected else None),
            app=SimpleNamespace(message_router=SimpleNamespace(
                peers={destination: peer} if exists else {},
            )),
        )

        class ObservedThread:
            def __init__(self, target, daemon):
                if daemon is not True:
                    raise RuntimeError("The observed callback no longer starts a daemon worker")
                self.target = target

            def start(self):
                events.append("start")
                self.target()
                raise StopBeforeDialog()

        original_time = module.time.time
        original_thread = module.threading.Thread
        try:
            module.time.time = lambda: now
            module.threading.Thread = ObservedThread
            try:
                module.LXMFPeers.sync_selected_entry(owner)
            except StopBeforeDialog:
                pass
        finally:
            module.time.time = original_time
            module.threading.Thread = original_thread
        rows.append(dict(name=name, selected=selected, exists=exists, lastAttempt=previous,
                         now=now, started="start" in events, nextAttempt=peer.next_sync_attempt,
                         events=events))

    removals = []
    for selected in (False, True):
        events = []
        item = SimpleNamespace(original_widget=SimpleNamespace(destination_hash=destination))
        owner = SimpleNamespace(
            ilb=SimpleNamespace(get_selected_item=lambda: item if selected else None),
            app=SimpleNamespace(message_router=SimpleNamespace(
                unpeer=lambda value: events.append("unpeer:" + value.hex()),
            )),
            delegate=SimpleNamespace(
                reinit_lxmf_peers=lambda: events.append("rebuild"),
                show_peers=lambda: events.append("show"),
            ),
        )
        module.LXMFPeers.delete_selected_entry(owner)
        removals.append(dict(selected=selected, events=events))
    result = dict(
        revision=REVISION, sourceSha256=hashlib.sha256(source.read_bytes()).hexdigest(),
        modulePath=str(source), python=sys.executable,
        method="Actual imported LXMFPeers sync_selected_entry/delete_selected_entry callbacks",
        boundary="Synthetic selected-item/router/peer and clock. The recording thread invokes the "
                 "synthetic peer then stops before dialog construction; no actual scheduling, "
                 "primary GUI or propagation-wire evidence.",
        synchronization=rows, removal=removals,
    )
    text = json.dumps(result, indent=2, ensure_ascii=True) + "\n"
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        "// Generated by executing pinned primary callbacks; do not edit.\n"
        "namespace NomadNet.Tests;\n\n"
        "internal static class NomadNetPeerControlVectors\n{\n"
        '    internal const string Json = """\n' + text + '""";\n}\n',
        encoding="utf-8", newline="\n",
    )
    print(json.dumps({"revision": REVISION, "sourceSha256": result["sourceSha256"],
                      "cases": len(rows) + len(removals)}))


if __name__ == "__main__":
    main()
