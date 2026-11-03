"""Observe pinned primary application policy; only storage/network boundaries are faked."""
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
        raise RuntimeError("Unexpected primary NomadNet revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Modified primary NomadNet source")
    sys.path.insert(0, str(source))
    module = importlib.import_module("nomadnet.NomadNetworkApp")
    app_class = module.NomadNetworkApp
    import RNS
    import LXMF
    conversation = importlib.import_module("nomadnet.Conversation")
    if Path(module.__file__).resolve() != source / "nomadnet/NomadNetworkApp.py":
        raise RuntimeError("Wrong imported application")
    old_hops = RNS.Transport.hops_to
    selections = []
    try:
        for manual, nodes, previous in [
            (None, [(1, 255, 4), (2, 255, 2), (3, 2, 1)], None),
            (None, [(1, 255, 2), (2, 255, 2)], None),
            (None, [(1, 2, 1)], 9),
            (8, [(1, 255, 1)], None),
        ]:
            hops = {bytes([number]) * 16: distance for number, _, distance in nodes}
            RNS.Transport.hops_to = lambda destination: hops.get(destination, RNS.Transport.PATHFINDER_M + 1)
            selected = [bytes([previous]) * 16 if previous else None]
            router = SimpleNamespace(get_outbound_propagation_node=lambda: selected[0],
                                     set_outbound_propagation_node=lambda value: selected.__setitem__(0, value))
            entries = [SimpleNamespace(source_hash=bytes([number]) * 16, trust_level=trust)
                       for number, trust, _ in nodes]
            app = SimpleNamespace(peer_settings={"propagation_node": bytes([manual]) * 16 if manual else None},
                                  message_router=router, directory=SimpleNamespace(known_nodes=lambda: entries))
            app_class.autoselect_propagation_node(app)
            selections.append({"manual": manual, "nodes": nodes, "previous": previous,
                               "selected": selected[0][0] if selected[0] else None})
    finally:
        RNS.Transport.hops_to = old_hops
    syncs = []
    for state in [0, 1, 6, 7, 240]:
        calls = []
        app = SimpleNamespace(
            peer_settings={"last_lxmf_sync": 0}, identity="identity",
            save_peer_settings=lambda: calls.append("saved"),
            message_router=SimpleNamespace(propagation_transfer_state=state,
                request_messages_from_propagation_node=lambda identity, max_messages: calls.append(max_messages)))
        app_class.request_lxmf_sync(app, limit=8)
        syncs.append({"state": state, "started": bool(calls),
                      "persistedBeforeRequest": calls == ["saved", 8]})
    fallbacks = []
    for failed, enabled, stamp_failure in [(True, True, False), (True, True, True),
                                            (True, False, False), (False, True, False)]:
        queued = []
        message = SimpleNamespace(state=LXMF.LXMessage.FAILED if failed else LXMF.LXMessage.DELIVERED,
            try_propagation_on_fail=enabled, stamp_generation_failed=stamp_failure,
            delivery_attempts=5, next_delivery_attempt=123, packed=b"packed",
            desired_method=LXMF.LXMessage.DIRECT)
        owner = SimpleNamespace(app=SimpleNamespace(message_router=SimpleNamespace(
            handle_outbound=lambda value: queued.append(value))), messages=[])
        original_ingest = conversation.Conversation.ingest
        conversation.Conversation.ingest = lambda *args, **kwargs: None
        try:
            conversation.Conversation.message_notification(owner, message)
        finally:
            conversation.Conversation.ingest = original_ingest
        fallbacks.append({"failed": failed, "enabled": enabled, "stampFailure": stamp_failure,
                          "retry": bool(queued)})
    result = {
        "commit": revision,
        "method": "Actual autoselect_propagation_node/request_lxmf_sync/message_notification; network, persistence and directory observation seams only",
        "sourceSha256": {name: hashlib.sha256((source / "nomadnet" / name).read_bytes()).hexdigest()
                         for name in ["NomadNetworkApp.py", "Conversation.py", "Directory.py"]},
        "selection": selections, "sync": syncs, "fallback": fallbacks,
    }
    Path(sys.argv[1]).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
