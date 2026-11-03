"""Generate deterministic RRC packets by executing primary NomadNet, not a local codec."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

from reference_environment import verify_reference


def main():
    verify_reference(require_lxmf=True)
    source = Path(os.environ["DOTRETICULUM_NOMADNET_SOURCE"]).resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2":
        raise RuntimeError("Unexpected primary NomadNet revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Modified primary NomadNet source")
    sys.path.insert(0, str(source))
    import nomadnet.RRC as rrc
    from nomadnet.vendor import cbor
    if Path(rrc.__file__).resolve() != source / "nomadnet/RRC.py":
        raise RuntimeError("Unexpected imported RRC path")
    cases = [
        ("hello", rrc.T_HELLO, None, {0: "nomadnet", 1: "0.1", 2: {0: True, 1: True}}, None),
        ("welcome", rrc.T_WELCOME, None,
         {0: "reference hub", 1: "0.1", 2: {0: True, 1: True},
          3: {0: 32, 1: 64, 2: 350, 3: 32, 4: 240}}, None),
        ("join", rrc.T_JOIN, "lobby", "secret-room-key", "Alice"),
        ("joined", rrc.T_JOINED, "lobby", [bytes(range(16)), bytes(range(16, 32))], "Alice"),
        ("part", rrc.T_PART, "lobby", None, "Alice"),
        ("message", rrc.T_MSG, "lobby", "Hello @Bob — 日本語", "Alice"),
        ("action", rrc.T_ACTION, "lobby", "waves", "Alice"),
        ("ping", rrc.T_PING, None, bytes(range(8)), None),
        ("pong", rrc.T_PONG, None, bytes(range(8)), None),
        ("error", rrc.T_ERROR, "lobby", "not joined", None),
        ("notice", rrc.T_NOTICE, None, "members in lobby: Alice (000102030405)", None),
        ("resource", rrc.T_RESOURCE_ENVELOPE, None,
         {0: bytes(range(8)), 1: "motd", 2: 4096, 3: bytes(range(32)), 4: "utf-8"}, None),
    ]
    vectors = []
    for name, kind, room, body, nick in cases:
        envelope = rrc._make_envelope(kind, bytes(range(16)), room=room, body=body,
                                     nick=nick, mid=bytes(range(8)), ts=1700000000123)
        wire = cbor.encode(envelope)
        assert cbor.decode(wire) == envelope
        vectors.append({"name": name, "type": kind, "wire": wire.hex()})
    result = {
        "commit": revision,
        "method": "nomadnet.RRC._make_envelope and nomadnet.vendor.cbor.encode/decode",
        "sourceSha256": hashlib.sha256(Path(rrc.__file__).read_bytes()).hexdigest(),
        "vectors": vectors,
        "mentions": [{"nick": nick, "text": text, "match": bool(rrc._mention_re(nick).search(text))}
                     for nick, text in [("Alice", "hello @aLiCe!"), ("Alice", "x@Alice"),
                                        ("Alice", "@Alice_something"), ("Alice", "(@Alice)")]],
        "who": [{"text": text, "result": rrc._parse_who_notice(text)} for text in [
            "members in Lobby: Alice (000102030405), user (alt) (101112131415)",
            "members in empty: (none)", "members in lobby: " + "ab" * 32, "unrelated notice"]],
        "roomLists": [{"text": text, "result": rrc._parse_room_list_notice(text)} for text in [
            "Registered public rooms:\n  lobby - Main room\n  #support\n  empty - ",
            "No public rooms registered", "unrelated notice"]],
    }
    manager = rrc.RRCManager(SimpleNamespace(
        identity=SimpleNamespace(hash=bytes(range(16))), rrc_history_per_room_cap=2,
        peer_settings={"display_name": "Alice"}))
    hub = rrc.RRCHub(manager, bytes(range(16)))
    # Disk append/delete boundaries only; actual record/unread/retention methods execute.
    hub._append_history = lambda room, message: None
    hub._delete_history = lambda room: None
    operations = [
        ("active", "lobby", "", False, False),
        ("message", "lobby", "active mention @Alice", False, True),
        ("message", "other", "unread mention @Alice", False, True),
        ("message", "quiet", "local", True, False),
        ("notice", None, "hub notice", False, False),
        ("notice", "quiet", "room notice", False, False),
        ("active", "other", "", False, False),
        ("read", "quiet", "", False, False),
        ("message", "other", "next", False, False),
        ("message", "other", "newest", False, False),
        ("inactive", None, "", False, False),
        ("message", "other", "inactive", False, False),
        ("clear", "other", "", False, False),
    ]
    trace = []
    for operation, room, text, local, mention in operations:
        if operation == "active":
            manager.set_active(hub, room)
        elif operation == "inactive":
            manager.set_active(None, None)
        elif operation == "read":
            hub.mark_read(room)
        elif operation == "clear":
            hub.clear_messages(room)
        else:
            message = rrc.RRCMessage("notice" if operation == "notice" else "msg", room,
                                     bytes(range(16, 32)), "Bob", text, rrc._now_ms())
            message.mention = mention
            if operation == "notice":
                hub._record_notice(message)
            else:
                hub._record_message(message, local=local)
        trace.append({"operation": operation, "room": room, "text": text, "local": local,
                      "mention": mention, "active": manager.active_room_for(hub),
                      "unread": sorted(hub.unread_rooms), "mentions": sorted(hub.mention_rooms),
                      "messages": {key: [item.text for item in values] for key, values in hub.messages.items()}})
    result["activity"] = trace
    manager._loading = True
    automatic = rrc.RRCHub(manager, bytes(range(16)))
    automatic._append_history = lambda room, message: None
    sent = []
    automatic._send_env = sent.append
    automatic_trace = []
    for operation in ["default-welcome", "enabled-welcome", "join", "own-joined", "other-joined",
                      "list", "fragment", "who", "manual-list", "manual-who"]:
        sent.clear()
        consumed = None
        if operation == "enabled-welcome":
            automatic.set_auto_list(True, save=False)
            automatic.set_auto_who(True, save=False)
        if operation.endswith("welcome"):
            automatic._on_packet(cbor.encode(rrc._make_envelope(rrc.T_WELCOME, bytes(range(16)),
                                                                body={0: "hub", 1: "0.1"})))
        elif operation == "join":
            automatic.join_room("lobby")
        elif operation.endswith("joined"):
            automatic._on_packet(cbor.encode(rrc._make_envelope(rrc.T_JOINED, bytes(range(16)),
                room="lobby", body=[bytes(range(16)) if operation == "own-joined" else bytes(range(16, 32))])))
        else:
            notice = {"list": "Registered public rooms:\n  lobby - Main room",
                      "fragment": "  support - Help", "who": "members in lobby: Alice (000102030405)",
                      "manual-list": "Registered public rooms:\n  lobby - Main room",
                      "manual-who": "members in lobby: Alice (000102030405)"}[operation]
            consumed = automatic._process_notice_text(notice)
        automatic_trace.append({"operation": operation,
                                "commands": [{"type": env[rrc.K_T], "room": env.get(rrc.K_ROOM),
                                              "body": env.get(rrc.K_BODY)} for env in sent],
                                "consumed": consumed})
    result["automaticDirectory"] = automatic_trace
    result["observationSeams"] = "Activity: disk append/delete disabled; automatic directory: captured _send_env transport and disabled manager persistence, actual primary handlers/setters/parsers executed."
    Path(sys.argv[1]).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
