"""Execute pinned primary paper methods; disclosed persistence/field/router seams."""
import argparse
import ast
import hashlib
import json
from pathlib import Path
import subprocess
from types import SimpleNamespace

import LXMF
import RNS

from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
DIGEST = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    revision = subprocess.check_output(
        ["git", "-C", str(args.source), "rev-parse", "HEAD"], text=True).strip()
    source = subprocess.check_output(
        ["git", "-C", str(args.source), "show", f"{PIN}:sbapp/sideband/core.py"])
    if revision != PIN or hashlib.sha256(source).hexdigest() != DIGEST:
        raise RuntimeError("Unexpected primary Sideband reference")
    core = next(n for n in ast.parse(source).body
                if isinstance(n, ast.ClassDef) and n.name == "SidebandCore")
    methods = [n for n in core.body if isinstance(n, ast.FunctionDef)
               and n.name in ("paper_message", "lxm_ingest_uri")]
    # Offline execution has no Reticulum singleton for destination-use accounting.
    rns = SimpleNamespace(Identity=SimpleNamespace(
        recall=lambda address: RNS.Identity.recall(address, _no_use=True)),
        Destination=RNS.Destination, log=RNS.log, LOG_ERROR=RNS.LOG_ERROR)
    namespace = {"RNS": rns, "LXMF": LXMF}
    exec(compile(ast.Module(body=methods, type_ignores=[]), "core.py", "exec"), namespace)
    sender = RNS.Identity()
    recipient = RNS.Identity()
    origin = RNS.Destination(sender, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    target = RNS.Destination(recipient, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    RNS.Identity.remember(bytes(32), target.hash, recipient.get_public_key())
    fields = {LXMF.FIELD_ICON_APPEARANCE: ["paperclip", bytes.fromhex("112233"), bytes.fromhex("aabbcc")]}
    cases = []
    for label, content, trusted, destination in [
        ("untrusted", "Original signed paper ✓", False, target.hash),
        ("trusted", "Original trusted paper", True, target.hash),
        ("whitespace", " ", False, target.hash),
        ("empty", "", False, target.hash),
        ("oversize", "x" * 3000, False, target.hash),
        ("unknown", "Unknown identity", False, bytes.fromhex("01" * 16)),
    ]:
        observed = {}

        def ingest(message, originator=False):
            observed["originator"] = originator
            observed["include_ticket"] = message.include_ticket
            message.timestamp = 1700000000.25
            message.pack()
            observed.update(uri=message.as_uri(), packed=message.packed.hex(),
                            field_ticket=LXMF.FIELD_TICKET in message.fields,
                            title=message.title.decode(), method=message.method, state=message.state)

        owner = SimpleNamespace(lxmf_destination=origin, get_message_fields=lambda _: fields.copy(),
                                is_trusted=lambda _: trusted, lxm_ingest=ingest)
        accepted = namespace["paper_message"](owner, content, destination)
        cases.append({"name": label, "content": content, "trusted": trusted, "accepted": accepted, **observed})
    statuses = []
    for value in (False, "local_delivery_occurred", "duplicate_lxm", True):
        state = {}

        def router(uri, **kwargs):
            state["uri"] = uri
            state["signals"] = kwargs
            return value

        owner = SimpleNamespace(message_router=SimpleNamespace(ingest_lxm_uri=router),
                                setstate=lambda key, response: state.update(key=key, response=response))
        namespace["lxm_ingest_uri"](owner, "lxm://reference-seam")
        statuses.append(state)
    args.output.write_text(json.dumps({
        "revision": PIN, "source_sha256": DIGEST,
        "method": "Actual paper_message/lxm_ingest_uri AST; real RNS identities/destinations and LXMF packing. Identity recall uses upstream _no_use=True (no runtime destination-use accounting). Injected get_message_fields, trust, originator persistence and URI router result. No GUI/database/live application claim.",
        "sender_public": sender.get_public_key().hex(), "destination": target.hash.hex(),
        "cases": cases, "statuses": statuses}, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
