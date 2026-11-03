"""Execute pinned primary directory and conversation policies without a network."""
import hashlib
import importlib
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
        raise RuntimeError("Unexpected primary revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Modified primary source")
    sys.path.insert(0, str(source))
    import LXMF
    directory_module = importlib.import_module("nomadnet.Directory")
    conversation_module = importlib.import_module("nomadnet.Conversation")
    app_class = importlib.import_module("nomadnet.NomadNetworkApp").NomadNetworkApp
    if Path(conversation_module.__file__).resolve() != source / "nomadnet/Conversation.py":
        raise RuntimeError("Wrong imported primary module")
    directory = directory_module.Directory.__new__(directory_module.Directory)
    entry_class = directory_module.DirectoryEntry
    peer = bytes.fromhex("11" * 16)
    other = bytes.fromhex("22" * 16)
    directory.directory_entries = {
        peer: entry_class(peer, "Alice"),
        other: entry_class(other, "Trusted Alice", entry_class.TRUSTED),
    }
    trust = []
    for level, name in [(2, None), (2, "Trusted Alice"), (255, "Trusted Alice"), (1, "Alice"), (0, None)]:
        directory.directory_entries[peer].trust_level = level
        trust.append({"saved": level, "announced": name, "actual": directory.trust_level(peer, name)})
    trust.append({"saved": None, "announced": "Trusted Alice", "actual": directory.trust_level(bytes(16), "Trusted Alice")})
    work = Path("artifacts/tests/nomadnet-conversation-reference").resolve()
    work.mkdir(parents=True, exist_ok=True)
    app = SimpleNamespace(conversationpath=str(work))
    conversation = conversation_module.Conversation
    conversation.unread_conversations = {}
    conversation.failed_conversations = {}
    conversation.cached_conversations = {}
    conversation.created_callback = None
    observations = []
    sequence = [("inbound", LXMF.LXMessage.DELIVERED), ("inbound", LXMF.LXMessage.DELIVERED),
                ("outbound", LXMF.LXMessage.SENT), ("outbound", LXMF.LXMessage.FAILED)]
    for index, (direction, state) in enumerate(sequence):
        def write(path, number=index):
            target = Path(path) / f"generated-{number}"
            target.write_bytes(b"public generated fixture; not an LXMF wire vector")
            return str(target)
        message = SimpleNamespace(source_hash=peer, destination_hash=peer, state=state, write_to_directory=write)
        conversation.ingest(message, app, originator=direction == "outbound")
        observations.append({"direction": direction, "failed": state == LXMF.LXMessage.FAILED,
                             "unread": conversation.unread_conversations.get(peer, 0),
                             "failures": conversation.failed_conversations.get(peer, 0)})
    app_class.mark_conversation_read(app, peer.hex())
    read = {"unread": conversation.unread_conversations.get(peer, 0),
            "failures": conversation.failed_conversations.get(peer, 0),
            "unreadFile": (work / peer.hex() / "unread").exists(),
            "failedFile": (work / peer.hex() / "failed").exists()}
    conversation.delete_conversation(peer.hex(), app)
    result = {
        "commit": revision,
        "method": "Actual Directory.trust_level/DirectoryEntry and Conversation.ingest, application mark_conversation_read, delete_conversation. Message persistence callback writes a public marker instead of LXMF bytes; no wire/transport claim.",
        "sourceSha256": {name: hashlib.sha256((source / "nomadnet" / name).read_bytes()).hexdigest()
                         for name in ["Directory.py", "Conversation.py", "NomadNetworkApp.py"]},
        "trust": trust, "sequence": observations, "read": read,
        "deleted": not (work / peer.hex()).exists(),
        "defaults": {"trust": entry_class(peer).trust_level, "delivery": entry_class(peer).preferred_delivery,
                     "identify": entry_class(peer).identify, "notes": entry_class(peer).notes},
    }
    Path(sys.argv[1]).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
