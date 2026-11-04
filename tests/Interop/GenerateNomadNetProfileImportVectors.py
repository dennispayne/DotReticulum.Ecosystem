"""Execute unchanged primary profile persistence APIs; never export private keys."""
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
import io

from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--profile", required=True)
    parser.add_argument("--rnsconfig", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    source = Path(os.environ["DOTRETICULUM_NOMADNET_SOURCE"]).resolve(strict=True)
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2":
        raise RuntimeError("Unexpected primary revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain", "--untracked-files=no"], text=True).strip():
        raise RuntimeError("Modified primary source")
    sys.path.insert(0, str(source))
    import RNS
    import LXMF
    import RNS.vendor.umsgpack as msgpack
    import nomadnet
    conversation = importlib.import_module("nomadnet.Conversation")
    if Path(conversation.__file__).resolve() != source / "nomadnet" / "Conversation.py":
        raise RuntimeError("Unexpected primary module")
    profile = Path(args.profile).resolve()
    profile.mkdir(parents=True, exist_ok=False)
    rns = Path(args.rnsconfig).resolve()
    rns.mkdir(parents=True, exist_ok=False)
    (rns / "config").write_text("[reticulum]\nshare_instance=No\nenable_transport=No\n[logging]\nloglevel=0\n[interfaces]\n", encoding="utf-8")
    (profile / "config").write_text(
        "[client]\nenable_client=Yes\nannounce_at_start=No\nperiodic_lxmf_sync=No\n"
        "[node]\nenable_node=No\n[logging]\nloglevel=0\n", encoding="utf-8")
    def observe():
        try:
            seed(source, revision, profile, args.output)
        except BaseException:
            import traceback
            traceback.print_exc(file=sys.__stderr__)
            os._exit(1)

    threading.Thread(target=observe, daemon=True).start()
    nomadnet.NomadNetworkApp(configdir=str(profile), rnsconfigdir=str(rns), daemon=True, force_console=True)


def seed(source, revision, profile, output):
    import RNS
    import LXMF
    import nomadnet
    from PIL import Image
    conversation = importlib.import_module("nomadnet.Conversation")
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        app = nomadnet.NomadNetworkApp._shared_instance
        if app is not None and getattr(app, "ui", None) is not None:
            break
        time.sleep(0.05)
    else:
        raise RuntimeError("Actual primary constructor did not become ready")
    app.set_display_name("Imported primary user")
    peer_identity = RNS.Identity()
    peer = RNS.Destination(peer_identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    RNS.Identity.remember(packet_hash=None, destination_hash=peer.hash, public_key=peer_identity.get_public_key())
    from nomadnet.Directory import DirectoryEntry
    app.directory.remember(DirectoryEntry(peer.hash, "Profile peer", 255))
    app.directory.save_to_disk()
    pixels = io.BytesIO()
    Image.new("RGB", (4, 4), (255, 255, 255)).save(pixels, format="PNG")
    cases = []
    for label, outgoing, state, fields in (
        ("incoming", False, LXMF.LXMessage.DELIVERED, {}),
        ("failed", True, LXMF.LXMessage.FAILED, {}),
        ("media-stripped", False, LXMF.LXMessage.DELIVERED,
         {LXMF.FIELD_FILE_ATTACHMENTS: [["public.txt", b"public profile attachment"]],
          LXMF.FIELD_IMAGE: ["png", pixels.getvalue()],
          LXMF.FIELD_RENDERER: 2}),
        ("paper", True, LXMF.LXMessage.PAPER, {}),
    ):
        dest, sender = (peer, app.lxmf_destination) if outgoing else (app.lxmf_destination, peer)
        method = LXMF.LXMessage.PAPER if label == "paper" else LXMF.LXMessage.DIRECT
        message = LXMF.LXMessage(dest, sender, label + " body", label + " title", fields=fields, desired_method=method)
        message.timestamp = 1700000000 + len(cases)
        message.pack()
        message.state = state
        path = conversation.Conversation.ingest(message, app, originator=outgoing)
        record = conversation.ConversationMessage(path)
        record.load()
        conversation.ConversationMessage.write_index(str(Path(path).parent), [record])
        entry = record.to_index_entry()
        cases.append({"label": label, "state": state, "method": method,
                      "title": entry["title"], "content": entry["content"],
                      "indexKeys": sorted(entry), "hasAttachments": entry["has_attachments"]})
    result = {
        "commit": revision, "rnsVersion": RNS.__version__, "lxmfVersion": LXMF.__version__,
        "method": "Complete unmodified NomadNetworkApp constructor on main thread; observer executes DirectoryEntry/save_to_disk, LXMessage.pack, Conversation.ingest, ConversationMessage.load/write_index (including actual attachment stripping), set_display_name/save_peer_settings. Random private identities remain only in the explicitly supplied ignored runtime profile; fixture contains no keys or random hashes.",
        "sources": {name: hashlib.sha256(subprocess.check_output(
            ["git", "-C", str(source), "show", revision + ":" + name])).hexdigest()
            for name in ("nomadnet/NomadNetworkApp.py", "nomadnet/Conversation.py", "nomadnet/Directory.py", "nomadnet/RRC.py")},
        "cases": cases,
    }
    Path(output).write_bytes((json.dumps(result, indent=2) + "\n").encode("utf-8"))
    print(json.dumps({"profile": str(profile), "delivery": app.lxmf_destination.hash.hex(), "peer": peer.hash.hex()}), flush=True)
    os._exit(0)


if __name__ == "__main__":
    main()
