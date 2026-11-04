"""Execute the pinned primary message-menu retry callback, not a rewritten model."""
import argparse
import ast
import contextlib
import hashlib
import json
import sqlite3
import subprocess
import threading
from pathlib import Path
from types import MethodType, SimpleNamespace

from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
CORE_SHA = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"


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

    def blob(path):
        return subprocess.check_output(["git", "-C", str(args.source), "show", f"{PIN}:{path}"])

    core = blob("sbapp/sideband/core.py")
    ui = blob("sbapp/ui/messages.py")
    if hashlib.sha256(core).hexdigest() != CORE_SHA:
        raise RuntimeError("Unexpected core.py source")
    core_class = next(node for node in ast.parse(core).body
                      if isinstance(node, ast.ClassDef) and node.name == "SidebandCore")
    delete_methods = [node for node in core_class.body if isinstance(node, ast.FunctionDef)
                      and node.name in ("delete_message", "_db_delete_message")]
    retries = [node for node in ast.walk(ast.parse(ui))
               if isinstance(node, ast.FunctionDef) and node.name == "gen_retry"]
    if len(retries) != 1 or len(delete_methods) != 2:
        raise RuntimeError("Primary retry/delete contract changed")
    observations = []
    for label, content, fail in (
            ("unicode", "Saved \u03bb\nsecond line".encode(), False),
            ("empty", b"", False),
            ("send-failure", b"Retain me on native validation failure", True),
            ("invalid-utf8", b"\xff", False)):
        with contextlib.closing(sqlite3.connect(":memory:")) as database:
            target = bytes([1]) * 32
            database.execute("CREATE TABLE lxm(lxm_hash BLOB)")
            database.execute("INSERT INTO lxm VALUES(?)", (target,))
            events = []
            core_owner = SimpleNamespace(db_lock=threading.Lock())
            core_owner.__db_connect = lambda: database
            namespace = {
                "Clock": SimpleNamespace(schedule_once=lambda callback, delay: callback(delay)),
                "RNS": SimpleNamespace(log=lambda *a, **k: None, trace_exception=lambda *a: None,
                                       prettyhexrep=lambda value: value.hex(), LOG_ERROR=0, LOG_DEBUG=0)}
            exec(compile(ast.Module(body=delete_methods, type_ignores=[]), "core.py", "exec"), namespace)
            core_owner._db_delete_message = MethodType(namespace["_db_delete_message"], core_owner)

            def delete(message_hash):
                events.append("delete")
                namespace["delete_message"](core_owner, message_hash)

            text = SimpleNamespace(text="Existing draft")

            def send():
                events.append("send")
                if fail:
                    raise RuntimeError("synthetic dispatcher failure")

            owner = SimpleNamespace(
                app=SimpleNamespace(
                    messages_view=SimpleNamespace(ids=SimpleNamespace(message_text=text)),
                    sideband=SimpleNamespace(delete_message=delete), message_send_action=send),
                reload=lambda: events.append("reload"))
            namespace["self"] = owner
            exec(compile(ast.Module(body=retries, type_ignores=[]), "messages.py", "exec"), namespace)
            item = SimpleNamespace(dmenu=SimpleNamespace(dismiss=lambda: events.append("dismiss")))
            error = None
            try:
                namespace["gen_retry"](target, content, item)()
            except (UnicodeDecodeError, RuntimeError) as exception:
                error = type(exception).__name__
            observations.append({
                "name": label, "content_hex": content.hex(), "composer": text.text,
                "events": events, "error": error,
                "saved_rows": database.execute("SELECT COUNT(*) FROM lxm").fetchone()[0]})
    result = {
        "revision": PIN, "core_sha256": CORE_SHA, "messages_sha256": hashlib.sha256(ui).hexdigest(),
        "method": "AST-extracted actual gen_retry factory and actual delete_message/_db_delete_message on private SQLite; disclosed synthetic owner, composer, dispatcher, menu and immediate Clock boundary; no primary GUI or live node",
        "cases": observations}
    with args.output.open("w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(result, indent=4) + "\n")


if __name__ == "__main__":
    main()
