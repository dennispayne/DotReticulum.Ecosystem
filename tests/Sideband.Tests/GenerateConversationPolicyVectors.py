"""Execute primary Sideband trust and send policy with disclosed API dependencies."""
import argparse
import ast
import hashlib
import json
from pathlib import Path
import subprocess
from types import MethodType, SimpleNamespace

import LXMF

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
    if revision != PIN:
        raise RuntimeError(f"Expected {PIN}, got {revision}")
    source = subprocess.check_output(
        ["git", "-C", str(args.source), "show", f"{PIN}:sbapp/sideband/core.py"])
    if hashlib.sha256(source).hexdigest() != DIGEST:
        raise RuntimeError("Unexpected core.py digest")
    core = next(node for node in ast.parse(source).body
                if isinstance(node, ast.ClassDef) and node.name == "SidebandCore")
    methods = [node for node in core.body if isinstance(node, ast.FunctionDef)
               and node.name in ("is_trusted", "send_message")]
    created = []

    def message(*_, **kwargs):
        created.append(kwargs)
        return SimpleNamespace(
            register_delivery_callback=lambda _: None,
            register_failed_callback=lambda _: None)

    rns = SimpleNamespace(
        Identity=SimpleNamespace(recall=lambda _: object(), current_ratchet_id=lambda _: None),
        Destination=lambda *_: object(), log=lambda *_, **__: None,
        prettyhexrep=lambda _: "synthetic-peer", trace_exception=lambda _: None,
        LOG_ERROR=0, LOG_DEBUG=0)
    rns.Destination = type("Destination", (), {
        "OUT": 1, "SINGLE": 0, "__new__": lambda *_, **__: object()})
    namespace = {"RNS": rns, "LXMF": SimpleNamespace(
        LXMessage=message, FIELD_RENDERER=LXMF.FIELD_RENDERER,
        RENDERER_MARKDOWN=LXMF.RENDERER_MARKDOWN,
        RENDERER_BBCODE=LXMF.RENDERER_BBCODE)}
    for name in ("DIRECT", "OPPORTUNISTIC", "PROPAGATED"):
        setattr(message, name, getattr(LXMF.LXMessage, name))
    exec(compile(ast.Module(body=methods, type_ignores=[]), "core.py", "exec"), namespace)
    cases = []
    for name, trust in [("missing", None), ("untrusted", 0), ("trusted", 1),
                        ("other-nonzero", 2), ("negative", -1)]:
        owner = SimpleNamespace(
            allow_service_dispatch=False, is_client=False, lxmf_destination=object(),
            _db_conversation=lambda _, trust=trust: None if trust is None else {"trust": trust},
            config={"compose_in_markdown": False, "lxmf_try_propagation_on_fail": True},
            get_message_fields=lambda _: {}, has_bb_markup=lambda _: False,
            message_notification=lambda _: None, lxm_ingest=lambda *_, **__: None,
            message_router=SimpleNamespace(
                delivery_link_available=lambda _: True,
                get_outbound_propagation_node=lambda: None,
                handle_outbound=lambda _: None))
        owner.is_trusted = MethodType(namespace["is_trusted"], owner)
        accepted = namespace["send_message"](owner, "synthetic message", bytes(16), False)
        if not accepted:
            raise RuntimeError("The actual send_message did not accept the policy case")
        cases.append({"name": name, "database_trust": trust,
                      "trusted": owner.is_trusted(bytes(16)),
                      "include_ticket": created[-1]["include_ticket"]})
    args.output.write_text(json.dumps({
        "revision": PIN, "source_sha256": DIGEST,
        "method": "actual is_trusted and send_message; injected database, message factory, destination, router and ingestion APIs",
        "cases": cases}, indent=4) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
