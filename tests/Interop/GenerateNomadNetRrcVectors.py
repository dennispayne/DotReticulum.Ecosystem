"""Generate deterministic RRC packets by executing primary NomadNet, not a local codec."""
import hashlib
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
    Path(sys.argv[1]).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
