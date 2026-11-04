"""Observe primary RRC persistence through the complete, unchanged application."""
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

from reference_environment import verify_reference

PIN = "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--profile", required=True)
    parser.add_argument("--rnsconfig", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--inspect-history", action="store_true")
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    source = Path(os.environ["DOTRETICULUM_NOMADNET_SOURCE"]).resolve(strict=True)
    if subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip() != PIN:
        raise RuntimeError("Unexpected primary revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain", "--untracked-files=no"], text=True).strip():
        raise RuntimeError("Modified primary source")
    sys.path.insert(0, str(source))
    import nomadnet
    rrc = importlib.import_module("nomadnet.RRC")
    if Path(rrc.__file__).resolve() != source / "nomadnet" / "RRC.py":
        raise RuntimeError("Unexpected primary RRC import")
    blob = subprocess.check_output(["git", "-C", str(source), "show", PIN + ":nomadnet/RRC.py"])
    if (source / "nomadnet" / "RRC.py").read_bytes().replace(b"\r\n", b"\n") != blob:
        raise RuntimeError("Imported primary source differs from the canonical Git blob")
    if args.inspect_history:
        for path in (Path(args.profile) / "storage" / "rrc_history").rglob("*.log"):
            with path.open("rb") as stream:
                while True:
                    try:
                        entry = rrc.cbor.load(stream)
                    except EOFError:
                        break
                    print(json.dumps({"file": path.name, "kind": entry.get("k"),
                                      "types": {key: type(value).__name__ for key, value in entry.items()},
                                      "sourceBytes": len(entry["s"]) if isinstance(entry.get("s"), bytes) else None}))
        return
    profile, rns = Path(args.profile).resolve(), Path(args.rnsconfig).resolve()
    profile.mkdir(parents=True, exist_ok=False)
    rns.mkdir(parents=True, exist_ok=False)
    (profile / "config").write_text(
        "[client]\nenable_client=Yes\nannounce_at_start=No\nperiodic_lxmf_sync=No\n"
        "[node]\nenable_node=No\n[logging]\nloglevel=0\n", encoding="utf-8")
    (rns / "config").write_text(
        "[reticulum]\nshare_instance=No\nenable_transport=No\n[logging]\nloglevel=0\n[interfaces]\n",
        encoding="utf-8")

    def observe():
        try:
            deadline = time.monotonic() + 40
            while time.monotonic() < deadline:
                app = nomadnet.NomadNetworkApp._shared_instance
                if app is not None and getattr(app, "ui", None) is not None:
                    break
                time.sleep(0.05)
            else:
                raise RuntimeError("Primary application did not start")
            app.set_display_name("Primary RRC fallback")
            manager = app.rrc
            hub = manager.add_hub(bytes.fromhex("11" * 16), name="Saved primary hub")
            hub.set_nick_override("Migrated nick")
            hub.set_auto_list(True)
            hub.set_auto_who(True)
            hub.set_auto_reconnect(True)
            hub.add_room(" Lobby ")
            hub.add_room("old/room")
            for room, kind, text, ts, mention in (
                ("lobby", "msg", "Retained primary message", 1700000000123, True),
                ("lobby", "action", "Retained primary action\nsecond line", 1700000000456, False),
                ("lobby", "notice", "Filtered primary notice", 1700000000789, False),
                ("old/room", "msg", "Retained parted history", 1700000000999, False),
            ):
                message = rrc.RRCMessage(kind, room, bytes.fromhex("22" * 16), "Historical claim", text, ts)
                message.mention = mention
                hub._record_message(message)
            # Acknowledged PART uses this exact retained-history state; no peer is contacted.
            hub.rooms.discard("old/room")
            hub.messages["empty-parted"] = []
            manager.save()
            fallback = manager.add_hub(bytes.fromhex("33" * 16), dest_name="custom.chat", name="Alternate aspect")
            fallback.add_room("unicode-æ")
            manager.save()
            reloaded = rrc.RRCManager(app)
            reloaded.load()
            raw = (profile / "storage" / "rrc_hubs").read_bytes()
            histories = {}
            for item in (profile / "storage" / "rrc_history").rglob("*.log"):
                histories[item.relative_to(profile).as_posix()] = item.read_bytes().hex()
            result = {
                "commit": PIN,
                "sourceSha256": hashlib.sha256(blob).hexdigest(),
                "sourceBlob": subprocess.check_output(
                    ["git", "-C", str(source), "rev-parse", PIN + ":nomadnet/RRC.py"], text=True).strip(),
                "actualImport": "nomadnet/RRC.py",
                "method": "Complete unchanged NomadNetworkApp; RRCManager.add_hub/save/load, RRCHub.add_room/set_nick_override/set_auto_list/set_auto_who/set_auto_reconnect/_record_message; retained PART state observed via save/load. No keys exported, no network.",
                "hubsHex": raw.hex(), "historyHex": dict(sorted(histories.items())),
                "loaded": [{"name": h.name, "aspect": h.dest_name, "nick": h.get_effective_nick(),
                            "rooms": sorted(h.rooms), "parted": sorted(set(h.messages) - h.rooms),
                            "history": [{"room": r, "kind": m.kind, "text": m.text, "ts": m.ts,
                                         "mention": m.mention}
                                        for r in sorted(h.messages) for m in h.messages[r]]}
                           for h in reloaded.hubs],
                "cap": app.rrc_history_per_room_cap, "filter": app.rrc_filter_loaded_history,
            }
            Path(args.output).write_bytes((json.dumps(result, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))
            print("@@" + json.dumps({"event": "ready", "module": str(Path(rrc.__file__).resolve()),
                                    "commit": PIN, "hubs": len(reloaded.hubs)}), flush=True)
            os._exit(0)
        except BaseException:
            import traceback
            traceback.print_exc(file=sys.__stderr__)
            os._exit(1)

    threading.Thread(target=observe, daemon=True).start()
    nomadnet.NomadNetworkApp(configdir=str(profile), rnsconfigdir=str(rns), daemon=True, force_console=True)


if __name__ == "__main__":
    main()
