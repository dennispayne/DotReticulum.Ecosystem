"""Observe actual primary delivery admission; ingestion/authorization/DB are explicit seams."""
import argparse
import hashlib
import inspect
import json
import os
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

import LXMF

from reference_environment import verify_reference
from GenerateSidebandRemoteVectors import CORE, PIN


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    source = args.source.resolve()
    path = source / "sbapp" / "sideband" / "core.py"
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    dirty = subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True)
    if revision != PIN or dirty or hashlib.sha256(path.read_bytes()).hexdigest() != CORE:
        raise RuntimeError("Unexpected primary reference source")
    sys.path.insert(0, str(source))
    if os.environ.get("DOTRETICULUM_LXST_UPSTREAM"):
        sys.path.insert(0, os.environ["DOTRETICULUM_LXST_UPSTREAM"])
    from sbapp.sideband.core import SidebandCore
    if Path(inspect.getfile(SidebandCore)).resolve() != path:
        raise RuntimeError("Unexpected actual imported primary core")
    observations = []
    kinds = {
        "text": (b"text", b"", {}),
        "title": (b"", b"title", {}),
        "empty": (b"", b"", {}),
        # Delivery dispatch does not parse these values. No payload-validation claim.
        "telemetry": (b"", b"", {LXMF.FIELD_TELEMETRY: None}),
        "attachment": (b"", b"", {LXMF.FIELD_FILE_ATTACHMENTS: None}),
        "commands": (b"", b"", {LXMF.FIELD_COMMANDS: []}),
        "commands_with_text": (b"text", b"title", {LXMF.FIELD_COMMANDS: []}),
    }
    for kind, (content, title, fields) in kinds.items():
        for ignore in (False, True):
            for known in (False, True):
                for signature in (False, True):
                    for authorized in (False, True):
                        trace = []
                        def conversation(_):
                            trace.append("conversation")
                            return {} if known else None
                        def authorize(_):
                            trace.append("authorize")
                            return authorized
                        owner = SimpleNamespace(
                            config={"lxmf_ignore_unknown": ignore},
                            _db_conversation=conversation,
                            allow_request_from=authorize,
                            handle_commands=lambda *a: trace.append("commands"),
                            lxm_ingest=lambda *a: trace.append("ingest"))
                        message = SimpleNamespace(
                            timestamp=1700000000, signature_validated=signature,
                            unverified_reason=LXMF.LXMessage.SIGNATURE_INVALID,
                            source_hash=bytes.fromhex("11" * 16), hash=bytes(32),
                            fields=fields, content=content, title=title)
                        SidebandCore.lxmf_delivery(owner, message)
                        observations.append(dict(kind=kind, ignore_unknown=ignore,
                            known=known, signature=signature, authorized=authorized, trace=trace))
    announce_trace = []
    owner = SimpleNamespace(
        _db_save_announce=lambda *a: announce_trace.append("save_announce"),
        setstate=lambda *a: announce_trace.append("announce_flag"))
    SidebandCore.log_announce(owner, bytes.fromhex("11" * 16), b"Observed", "lxmf.delivery")
    ingestions = []
    for kind, (content, title, fields) in {
        **kinds,
        "telemetry_stream": (b"", b"", {LXMF.FIELD_TELEMETRY_STREAM: None}),
        "telemetry_attachment": (b"", b"", {LXMF.FIELD_TELEMETRY: None, LXMF.FIELD_FILE_ATTACHMENTS: None}),
        "telemetry_attachment_text": (b"text", b"", {LXMF.FIELD_TELEMETRY: None, LXMF.FIELD_FILE_ATTACHMENTS: None}),
    }.items():
        trace = []
        owner = SimpleNamespace(
            is_client=True, is_trusted=lambda _: False, ptt_enabled=lambda _: False,
            _db_message=lambda _: None, _db_save_lxm=lambda *a, **kw: trace.append("save_message"),
            _db_conversation=lambda _: None, _db_create_conversation=lambda _: trace.append("create_conversation"),
            gui_display=lambda: None, unread_conversation=lambda *a, **kw: trace.append("unread"),
            setstate=lambda *a: None)
        message = SimpleNamespace(source_hash=bytes.fromhex("11" * 16), hash=bytes(32),
                                  fields=fields, content=content, title=title)
        SidebandCore.lxm_ingest(owner, message)
        ingestions.append(dict(kind=kind, trace=trace))
    args.output.write_text(json.dumps({
        "revision": PIN, "core_sha256": CORE,
        "method": "Actual imported SidebandCore.lxmf_delivery/log_announce/lxm_ingest. Explicit conversation, authorization, command, ingestion, message/announce-storage and GUI observers; no cryptographic, payload-validation, SQLite or live claim.",
        "announce_trace": announce_trace, "observations": observations, "ingestions": ingestions,
    }, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps({"imported_core": str(path), "revision": revision,
                      "core_sha256": CORE, "observations": len(observations)}))


if __name__ == "__main__":
    main()
