"""Observe pinned primary policy methods; injected APIs never transmit or open hardware."""
import argparse
import ast
import hashlib
import importlib
import itertools
import json
from pathlib import Path
import subprocess
import sys
from types import MethodType, SimpleNamespace

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
HASH = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"
SENSE_HASH = "80418b1aa4de638f2c65f1bd64e5ea0466378a6f0bc682781672ef89b93a7dab"
NOW = 1700000000
OWN, PEER, COLLECTOR = (bytes([i]) * 16 for i in (0, 1, 4))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import PINNED_LXMF_SHA, PINNED_LXMF_VERSION, PINNED_RNS_SHA, verify_reference
    verify_reference(require_lxmf=True)
    import RNS
    import LXMF
    from RNS.vendor import umsgpack as mp
    root = args.source.resolve()
    revision = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
    source = subprocess.check_output(["git", "-C", str(root), "show", f"{PIN}:sbapp/sideband/core.py"])
    if revision != PIN or hashlib.sha256(source).hexdigest() != HASH:
        raise RuntimeError("Unexpected primary core")
    if (root / "sbapp" / "sideband" / "core.py").read_bytes().replace(b"\r\n", b"\n") != source.replace(b"\r\n", b"\n"):
        raise RuntimeError("Modified primary working source")
    sys.path.insert(0, str(root / "sbapp"))
    sense = importlib.import_module("sideband.sense")
    sense_source = subprocess.check_output(["git", "-C", str(root), "show", f"{PIN}:sbapp/sideband/sense.py"])
    if hashlib.sha256(sense_source).hexdigest() != SENSE_HASH:
        raise RuntimeError("Unexpected primary telemetry source")
    if Path(sense.__file__).resolve() != (root / "sbapp" / "sideband" / "sense.py").resolve():
        raise RuntimeError("Unexpected actual telemetry import")
    if Path(sense.__file__).read_bytes().replace(b"\r\n", b"\n") != sense_source.replace(b"\r\n", b"\n"):
        raise RuntimeError("Modified primary telemetry working source")
    sense.time = SimpleNamespace(time=lambda: NOW)
    core = next(n for n in ast.parse(source).body if isinstance(n, ast.ClassDef) and n.name == "SidebandCore")
    names = ("is_trusted", "should_send_telemetry", "allow_request_from", "requests_allowed_from",
             "get_message_fields", "send_latest_telemetry", "request_latest_telemetry", "lxmf_delivery")
    methods = [n for n in core.body if isinstance(n, ast.FunctionDef) and n.name in names]
    history_node = next(n.value for n in ast.walk(core) if isinstance(n, ast.Assign) and any(
        isinstance(t, ast.Attribute) and t.attr == "telemetry_request_max_history" for t in n.targets))
    history = eval(compile(ast.Expression(history_node), "core.py:request-history", "eval"), {"__builtins__": {}})
    defaults = {}
    for node in ast.walk(core):
        if isinstance(node, ast.Assign) and isinstance(node.value, ast.Constant):
            for target in node.targets:
                if isinstance(target, ast.Subscript) and isinstance(target.value, ast.Attribute) and target.value.attr == "config":
                    if isinstance(target.slice, ast.Constant) and str(target.slice.value).startswith("telemetry_"):
                        defaults.setdefault(target.slice.value, node.value.value)
    state = {}
    captured = []
    paths = []
    route = {}

    def message(*_, **kwargs):
        result = SimpleNamespace(**kwargs, register_delivery_callback=lambda _: None, register_failed_callback=lambda _: None)
        captured.append(result)
        return result

    for name in ("DIRECT", "OPPORTUNISTIC", "PROPAGATED", "SIGNATURE_INVALID", "SOURCE_UNKNOWN"):
        setattr(message, name, getattr(LXMF.LXMessage, name))
    destination = type("Destination", (), {"OUT": 1, "SINGLE": 0, "__new__": lambda *_, **__: object()})
    rns = SimpleNamespace(
        Identity=SimpleNamespace(recall=lambda _: object() if route.get("identity", True) else None,
                                 current_ratchet_id=lambda _: b"ratchet" if route.get("ratchet", False) else None),
        Destination=destination, Transport=SimpleNamespace(request_path=lambda _: paths.append(True)),
        log=lambda *_, **__: None, trace_exception=lambda _: None, hexrep=RNS.hexrep,
        prettyhexrep=RNS.prettyhexrep, prettytime=RNS.prettytime, LOG_DEBUG=0, LOG_WARNING=0, LOG_ERROR=0)
    clock = SimpleNamespace(time=lambda: NOW, strftime=lambda *_: "synthetic", localtime=lambda _: None)
    ns = {"RNS": rns, "LXMF": SimpleNamespace(LXMessage=message, FIELD_ICON_APPEARANCE=LXMF.FIELD_ICON_APPEARANCE,
          FIELD_TELEMETRY=LXMF.FIELD_TELEMETRY, FIELD_TELEMETRY_STREAM=LXMF.FIELD_TELEMETRY_STREAM,
          FIELD_COMMANDS=LXMF.FIELD_COMMANDS), "time": clock, "Telemeter": sense.Telemeter,
          "Commands": sense.Commands, "valid_telemetry_timebase": sense.valid_telemetry_timebase, "struct": __import__("struct")}
    exec(compile(ast.Module(body=methods, type_ignores=[]), "core.py", "exec"), ns)
    owner = SimpleNamespace()
    for name in names:
        setattr(owner, name, MethodType(ns[name], owner))

    def reset(data):
        state.clear(); captured.clear(); paths.clear(); route.clear()
        route.update(identity=data.get("identity", True), ratchet=data.get("ratchet", False))
        flags = {"telemetry_enabled": data.get("enabled", False), "telemetry_send_to_trusted": data.get("send_trusted", False),
                 "telemetry_allow_requests_from_anyone": data.get("allow_anyone", False),
                 "telemetry_allow_requests_from_trusted": data.get("allow_trusted", False),
                 "telemetry_send_appearance": data.get("appearance", True), "telemetry_collector": COLLECTOR,
                 "telemetry_receive_trusted_only": data.get("receive_trusted", False),
                 "telemetry_use_propagation_only": data.get("propagation_only", False),
                 "telemetry_try_propagation_on_fail": data.get("retry", True),
                 "telemetry_icon": "synthetic", "telemetry_fg": [1, 1, 1, 1], "telemetry_bg": [0, 0, 0, 1],
                 "lxmf_ignore_unknown": data.get("ignore_unknown", False)}
        owner.config = flags
        conv = None if not data.get("exists", True) else {"trust": data.get("trust", 0), "data": {
            "telemetry": data.get("conversation_send", False), "allow_requests": data.get("conversation_requests", False)}}
        owner._db_conversation = lambda _: conv
        owner.allow_service_dispatch = False
        owner.is_client = False
        owner.lxmf_destination = SimpleNamespace(hash=OWN)
        owner.latest_packed_telemetry = mp.packb({1: data.get("own_time", NOW)}) if data.get("packed", True) else None
        owner.latest_telemetry = {"time": {"utc": data.get("own_time", NOW)}} if data.get("readings", True) else None
        owner._last_telemetry_send_success_timebase = lambda _: data.get("last_success", 0)
        owner._telemetry_timebase = lambda _: data.get("last_received", None)
        owner.telemetry_request_max_history = history
        owner.telemetry_response_excluded = [OWN] if data.get("excluded", False) else []
        owner.getstate = lambda _: data.get("busy", False)
        owner.setstate = lambda key, value: state.update({key: value})
        owner.setpersistent = lambda key, value: state.update({key: value})
        owner.message_router = SimpleNamespace(
            delivery_link_available=lambda _: data.get("link", False),
            get_outbound_propagation_node=lambda: PEER if data.get("node", False) else None,
            handle_outbound=lambda _: None)
        owner.outbound_telemetry_finished = lambda _: None
        owner.telemetry_request_finished = lambda _: None

    permissions = []
    conversations = [{"exists": False}] + [
        {"exists": True, "trust": trust, "conversation_send": send, "conversation_requests": requests}
        for trust, send, requests in itertools.product((0, 1, 2), (False, True), (False, True))]
    for enabled, send_trusted, allow_anyone, allow_trusted in itertools.product((False, True), repeat=4):
        for conversation in conversations:
            data = dict(conversation, enabled=enabled, send_trusted=send_trusted,
                        allow_anyone=allow_anyone, allow_trusted=allow_trusted)
            reset(data)
            permissions.append({"input": data, "send": owner.should_send_telemetry(PEER),
                                "request": owner.allow_request_from(PEER), "trusted": owner.is_trusted(PEER)})
    fields = []
    for enabled, conversation_send, authorized, force, packed, stale, excluded, appearance, signal in itertools.product((False, True), repeat=9):
        data = dict(enabled=enabled, conversation_send=conversation_send, authorized=authorized,
                    force=force, packed=packed, own_time=NOW, last_success=NOW if stale else 0,
                    excluded=excluded, response=excluded, appearance=appearance, signal=signal)
        reset(data)
        value = owner.get_message_fields(PEER, telemetry_update=force, is_authorized_telemetry_request=authorized,
                                         is_collector_response=excluded, signal_already_sent=signal)
        fields.append({"input": data, "suppressed": value is False,
                       "telemetry": value is not False and LXMF.FIELD_TELEMETRY in value,
                       "appearance": value is not False and LXMF.FIELD_ICON_APPEARANCE in value})
    scenarios = [
        ("no-address", {"address": False}), ("self", {"self": True}), ("busy", {"busy": True}),
        ("no-identity", {"identity": False, "conversation_send": True, "enabled": True}),
        ("no-own", {"packed": False, "readings": False}), ("disabled", {}),
        ("conversation", {"enabled": True, "conversation_send": True}),
        ("collector-bypasses-disabled", {"collector": True}),
        ("trusted", {"enabled": True, "send_trusted": True, "trust": 1}),
        ("authorized", {"authorized": True}), ("excluded-own", {"authorized": True, "excluded": True, "response": True}),
        ("stale", {"authorized": True, "last_success": NOW}),
        ("empty-stream", {"stream": [], "last_success": NOW, "authorized": True}),
        ("stream-only", {"packed": False, "readings": False, "stream": [NOW - 10]}),
        ("stream-invalid-outer", {"packed": False, "readings": False, "stream": [NOW + 241, NOW - 10]}),
        ("stream-all-invalid", {"packed": False, "readings": False, "stream": [NOW + 241]}),
        ("own-time-precedes-stream", {"authorized": True, "own_time": NOW - 100, "stream": [NOW]}),
        ("opportunistic", {"authorized": True, "ratchet": True}),
        ("linked-ratchet", {"authorized": True, "ratchet": True, "link": True}),
        ("propagation", {"authorized": True, "propagation_only": True, "node": True}),
        ("retry", {"authorized": True, "node": True}),
        ("retry-disabled", {"authorized": True, "node": True, "retry": False}),
        ("live-request-no-retry", {"live": True, "node": True}),
        ("request-zero-cursor", {"last_received": 0}), ("request-negative-cursor", {"last_received": -1}),
        ("request-positive-cursor", {"last_received": NOW - 123}),
        ("collector-request-command", {"collector_request": True}),
        ("stream-future-boundary", {"packed": False, "readings": False, "stream": [NOW + 240]}),
        ("packed-without-readings", {"readings": False, "authorized": True}),
    ]
    sends, requests = [], []
    for name, changes in scenarios:
        reset(changes)
        address = None if not changes.get("address", True) else OWN if changes.get("self") else COLLECTOR if changes.get("collector") else PEER
        stream = None if "stream" not in changes else [[PEER, ts, mp.packb({1: NOW - 100}), None] for ts in changes["stream"]]
        result = owner.send_latest_telemetry(address, stream=stream,
            is_authorized_telemetry_request=changes.get("authorized", False), is_collector_response=changes.get("response", False))
        outcome = {"status": result, "path_requested": bool(paths)}
        if captured:
            m = captured[-1]
            outcome.update(method=m.desired_method, ticket=m.include_ticket,
                           retry=getattr(m, "try_propagation_on_fail", False), timebase=m.telemetry_timebase,
                           own=LXMF.FIELD_TELEMETRY in m.fields, stream=LXMF.FIELD_TELEMETRY_STREAM in m.fields,
                           appearance=LXMF.FIELD_ICON_APPEARANCE in m.fields)
        sends.append({"name": name, "input": changes, "output": outcome})
        reset(changes)
        result = owner.request_latest_telemetry(address, is_livetrack=changes.get("live", False), is_collector_request=changes.get("collector_request", False))
        outcome = {"status": result, "path_requested": bool(paths)}
        if captured:
            m = captured[-1]
            outcome.update(method=m.desired_method, ticket=m.include_ticket, retry=getattr(m, "try_propagation_on_fail", False),
                           timebase=m.request_timebase, commands_hex=mp.packb(m.fields[LXMF.FIELD_COMMANDS]).hex())
        requests.append({"name": name, "input": changes, "output": outcome})
    deliveries = []
    for exists, signature, commands, ignore, allow in itertools.product((False, True), repeat=5):
        data = dict(exists=exists, ignore_unknown=ignore, allow_anyone=allow)
        reset(data); observed = []
        owner.handle_commands = lambda *_: observed.append("commands")
        owner.lxm_ingest = lambda *_: observed.append("ingest")
        owner.lxmf_delivery(SimpleNamespace(source_hash=PEER, timestamp=NOW, signature_validated=signature,
            fields={LXMF.FIELD_COMMANDS: []} if commands else {}, unverified_reason=message.SIGNATURE_INVALID))
        deliveries.append({"input": dict(data, signature=signature, commands=commands),
                           "output": observed[0] if observed else "dropped"})
    # Execute only the exact original pre-storage telemetry admission block; subsequent message/database/UI processing is not run.
    save = next(n for n in core.body if isinstance(n, ast.FunctionDef) and n.name == "_db_save_lxm")
    prefix = []
    for node in save.body:
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "attach_msg" for t in node.targets):
            break
        prefix.append(node)
    receipt = []
    physical_cases = ((None, None, None), (0, 0, 0), (-90, 0, 0), (0, 10, 0), (0, 0, 1))
    for trusted_only, trust, originator, stream, physical in itertools.product(
            (False, True), (0, 1, 2), (False, True), (False, True), physical_cases):
        data = dict(receive_trusted=trusted_only, trust=trust, originator=originator, stream=stream, physical=physical)
        reset(data); saved = []; cursors = []
        owner._db_save_telemetry = lambda context, packed, **kwargs: (saved.append({
            "source": context.hex(), "kwargs": {k: v.hex() if isinstance(v, bytes) else v for k, v in kwargs.items()}
        }) or b"saved")
        owner._db_update_appearance = lambda *_, **__: None
        owner.setpersistent = lambda _, value: cursors.append(value)
        value = [[COLLECTOR, NOW + 10000, mp.packb({1: NOW - 1}), None]] if stream else mp.packb({1: NOW - 1})
        ns.update(self=owner, lxm=SimpleNamespace(state=0, fields={
            LXMF.FIELD_TELEMETRY_STREAM if stream else LXMF.FIELD_TELEMETRY: value}, rssi=physical[0], snr=physical[1], q=physical[2]),
            context_dest=PEER, originator=originator)
        exec(compile(ast.Module(body=prefix, type_ignores=[]), "core.py:telemetry-admission-prefix", "exec"), ns)
        receipt.append({"input": data, "accepted": bool(saved), "saves": saved, "cursor": cursors[-1] if cursors else None})
    args.output.write_text(json.dumps({
        "revision": PIN, "source_sha256": HASH, "sense_sha256": SENSE_HASH, "clock": NOW,
        "reference": {"rns_revision": PINNED_RNS_SHA, "rns_version": RNS.__version__,
                      "lxmf_target_revision": PINNED_LXMF_SHA, "lxmf_version": PINNED_LXMF_VERSION,
                      "actual_imports_verified": True},
        "source_methods": {n.name: {"start": n.lineno, "end": n.end_lineno} for n in methods},
        "request_history_seconds": history,
        "method": "AST-extracted actual primary methods; actual Telemeter/MessagePack. Injected clock, identity/path, message factory, router, conversation/state APIs. Receipt executes exact _db_save_lxm admission prefix only with observed save/cursor callbacks; no full core/database/UI/device/network.",
        "defaults": defaults, "permissions": permissions, "fields": fields, "sends": sends, "requests": requests,
        "deliveries": deliveries, "receipts": receipt,
        "contracts": [
            "conversation data telemetry=True enables ordinary sends only when globally enabled; trusted means trust exactly 1.",
            "allow_requests_from_trusted=True bypasses the per-conversation allow_requests fallback, denying explicitly permitted untrusted peers; global telemetry_enabled is not consulted.",
            "Explicit telemetry_update/authorized requests bypass ordinary send policy; the collector destination is always treated as authorized by send_latest_telemetry, irrespective of send_to_collector/global enable.",
            "Receipt checks relay conversation trust only, not inner stream-source trust or telemetry_enabled. Stream shape is [source hash, outer timestamp, packed report, appearance]. Receipt provenance is by local destination/via sender for direct data; forwarded data retains by/distance and sets via relay.",
            "Primary ordinary telemetry is ingested even without validated signature in this delivery path; validated command fields require request authorization. Native callers may choose stricter authentication before applying these source decisions.",
            "Stream outer timestamps are not checked in the receipt cursor update, allowing a future cursor even when packed report time is valid. Send accounting skips invalid outer timestamps but leaves those entries in the outgoing stream.",
            "Own payload timestamp takes precedence over stream maximum for outbound accounting. An empty provided stream differs from no stream on stale-field admission.",
            "Policy helper implements decisions only; configuration persistence, authorization enforcement, source annotation, packets, streams and scheduling remain application integration."
        ]}, indent=2) + "\n", encoding="utf-8")
    print(f"Generated {len(permissions)} permissions, {len(fields)} fields, {len(sends)} sends, {len(requests)} requests, {len(deliveries)} delivery and {len(receipt)} receipt cases")


if __name__ == "__main__":
    main()
