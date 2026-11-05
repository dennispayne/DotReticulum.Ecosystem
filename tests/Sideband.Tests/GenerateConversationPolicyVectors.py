"""Execute primary Sideband trust and send policy with disclosed API dependencies."""
import argparse
import ast
import contextlib
import hashlib
import json
from pathlib import Path
import sqlite3
import subprocess
import threading
import time
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
               and node.name in ("is_trusted", "send_message", "raw_display_name", "peer_display_name",
                                 "named_conversation", "_db_conversation_set_name",
                                 "delete_message", "_db_delete_message", "clear_conversation",
                                 "delete_conversation", "_db_clear_conversation",
                                 "_db_clear_telemetry", "_db_delete_conversation")]
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
    namespace = {"RNS": rns, "time": time, "LXMF": SimpleNamespace(
        LXMessage=message, FIELD_RENDERER=LXMF.FIELD_RENDERER,
        RENDERER_MARKDOWN=LXMF.RENDERER_MARKDOWN,
        RENDERER_BBCODE=LXMF.RENDERER_BBCODE,
        display_name_from_app_data=LXMF.display_name_from_app_data)}
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
    names = []
    for saved in (None, "", "Saved peer", "Saved \u03b1"):
        for announced in ("Announced first", "Announced replacement"):
            rns.Identity.recall_app_data = lambda _, value=announced: value.encode("utf-8")
            owner = SimpleNamespace(
                lxmf_destination=SimpleNamespace(hash=bytes(16)),
                config={"display_name": "Local"},
                _db_conversation=lambda _, value=saved: {"name": value, "trust": 1})
            names.append({"saved": saved, "announced": announced,
                          "raw": namespace["raw_display_name"](owner, bytes([1]) * 16),
                          "display": namespace["peer_display_name"](owner, bytes([1]) * 16)})
    renames = []
    peer = bytes([1]) * 16
    for label, value in (("clear", ""), ("replace", "New saved \u03bb"), ("null", None)):
        with contextlib.closing(sqlite3.connect(":memory:")) as database:
            database.execute("CREATE TABLE conv(dest_context BLOB, name BLOB, trust INTEGER)")
            database.execute("INSERT INTO conv VALUES(?,?,?)", (peer, b"Saved peer", 1))

            def conversation(destination):
                row = database.execute(
                    "SELECT name,trust FROM conv WHERE dest_context=?", (destination,)).fetchone()
                return None if row is None else {"name": row[0].decode("utf-8"), "trust": row[1]}

            observed = {"name": "Announced first"}
            rns.Identity.recall_app_data = lambda _: observed["name"].encode("utf-8")
            owner = SimpleNamespace(
                lxmf_destination=SimpleNamespace(hash=bytes(16)), config={"display_name": "Local"},
                db_lock=threading.Lock(), _db_conversation=conversation)
            owner.__db_connect = lambda: database
            owner._db_conversation_set_name = MethodType(namespace["_db_conversation_set_name"], owner)
            error = None
            try:
                namespace["named_conversation"](owner, value, peer)
            except AttributeError as exception:
                if value is not None:
                    raise
                error = type(exception).__name__
            row = {"name": label, "input": value, "error": error,
                   "raw": namespace["raw_display_name"](owner, peer),
                   "display": namespace["peer_display_name"](owner, peer)}
            observed["name"] = "Announced replacement"
            row["after_announce"] = namespace["peer_display_name"](owner, peer)
            renames.append(row)
    deletions = []
    with contextlib.closing(sqlite3.connect(":memory:")) as database:
        database.execute("CREATE TABLE lxm(lxm_hash BLOB, peer TEXT, content TEXT, state TEXT)")
        for value, state in ((1, "received"), (2, "outbound"), (3, "received")):
            database.execute("INSERT INTO lxm VALUES(?,?,?,?)",
                             (bytes([value]) * 32, "other" if value == 3 else "selected", "Same text", state))
        owner = SimpleNamespace(db_lock=threading.Lock())
        owner.__db_connect = lambda: database
        owner._db_delete_message = MethodType(namespace["_db_delete_message"], owner)
        for label, value in (("incoming", 1), ("unknown", 9), ("repeat", 1), ("outbound", 2)):
            namespace["delete_message"](owner, bytes([value]) * 32)
            deletions.append({
                "name": label, "target": value,
                "remaining": [row[0][0] for row in database.execute(
                    "SELECT lxm_hash FROM lxm ORDER BY lxm_hash")]})
    conversation_actions = []
    for action in ("clear", "delete"):
        with contextlib.closing(sqlite3.connect(":memory:")) as database:
            selected, other, local = (bytes([value]) * 16 for value in (1, 2, 0))
            database.execute("CREATE TABLE conv(dest_context BLOB, name TEXT, trust INTEGER)")
            database.execute("CREATE TABLE lxm(id INTEGER, dest BLOB, source BLOB)")
            database.execute("CREATE TABLE telemetry(dest_context BLOB)")
            for destination in (selected, other):
                database.execute("INSERT INTO conv VALUES(?,?,?)", (destination, "Saved peer", 1))
                database.execute("INSERT INTO telemetry VALUES(?)", (destination,))
            for identifier, destination, origin in ((1, local, selected), (2, selected, local), (3, local, other)):
                database.execute("INSERT INTO lxm VALUES(?,?,?)", (identifier, destination, origin))
            flags = []
            owner = SimpleNamespace(db_lock=threading.Lock(), setstate=lambda key, _: flags.append(key))
            owner.__db_connect = lambda: database
            for name in ("_db_clear_conversation", "_db_clear_telemetry", "_db_delete_conversation"):
                setattr(owner, name, MethodType(namespace[name], owner))
            for label, destination in (("selected", selected), ("repeat", selected), ("unknown", bytes([9]) * 16)):
                namespace[action + "_conversation"](owner, destination)
                conversation_actions.append({
                    "action": action, "target": label,
                    "remaining_messages": [row[0] for row in database.execute("SELECT id FROM lxm ORDER BY id")],
                    "remaining_conversations": [row[0][0] for row in database.execute("SELECT dest_context FROM conv ORDER BY dest_context")],
                    "remaining_telemetry": [row[0][0] for row in database.execute("SELECT dest_context FROM telemetry ORDER BY dest_context")],
                    "telemetry_changed": bool(flags)})
    result = {
        "revision": PIN, "source_sha256": DIGEST,
        "method": "actual is_trusted/send_message/raw_display_name/peer_display_name plus named_conversation/_db_conversation_set_name, delete_message/_db_delete_message and clear_conversation/delete_conversation with their three actual SQL methods on real private SQLite; injected connection/read/announce cache/message/router/state-notification APIs and synthetic owner; no network or primary GUI",
        "cases": cases, "name_cases": names, "rename_cases": renames,
        "deletion_cases": deletions, "conversation_actions": conversation_actions}
    with args.output.open("w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(result, indent=4) + "\n")


if __name__ == "__main__":
    main()
