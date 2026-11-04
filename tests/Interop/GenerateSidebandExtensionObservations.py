"""Execute pinned Sideband extension methods with synthetic owners, never a production runtime."""
import argparse
import ast
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shlex
import shutil
import struct
import subprocess
import sys
import time
from types import SimpleNamespace

from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
SOURCE_HASHES = {
    "sbapp/sideband/core.py": "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25",
    "sbapp/sideband/plugins.py": "47a051a26a21d53aa8d6232cfc438dfac5e9396814e4bbc6c0149fb74643b6ac",
    "sbapp/sideband/sense.py": "80418b1aa4de638f2c65f1bd64e5ea0466378a6f0bc682781672ef89b93a7dab",
    "sbapp/main.py": "5b962d9ed2ccbc066bb19865d78c77da9ecf4008900f6f0e7e9dd1eb1c4767aa",
}


def jsonable(value):
    if isinstance(value, bytes):
        return {"bytes_hex": value.hex()}
    if isinstance(value, dict):
        return {str(k): jsonable(v) for k, v in value.items()}
    if isinstance(value, (tuple, list)):
        return [jsonable(v) for v in value]
    return value


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def compile_nodes(nodes, namespace, label):
    exec(compile(ast.Module(body=nodes, type_ignores=[]), label, "exec"), namespace)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True, require_lxst=True)
    import RNS
    import LXMF
    from RNS.vendor import umsgpack as mp
    source = args.source.resolve(strict=True)
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != PIN:
        raise RuntimeError(f"Unexpected reference revision {revision}")
    blobs = {}
    hashes = {}
    for path in [*SOURCE_HASHES, "sbapp/sideband/certgen.py", "LICENSE", "setup.py"]:
        blob = subprocess.check_output(["git", "-C", str(source), "show", f"{PIN}:{path}"])
        digest = hashlib.sha256(blob).hexdigest()
        if path in SOURCE_HASHES and digest != SOURCE_HASHES[path]:
            raise RuntimeError(f"Unexpected blob: {path}")
        if (source / path).read_bytes().replace(b"\r\n", b"\n") != blob.replace(b"\r\n", b"\n"):
            raise RuntimeError(f"Modified reference working source: {path}")
        blobs[path] = blob
        hashes[path] = digest
    plugins = load_module("extension_reference_plugins", source / "sbapp/sideband/plugins.py")
    core_tree = ast.parse(blobs["sbapp/sideband/core.py"])
    core_class = next(n for n in core_tree.body if isinstance(n, ast.ClassDef) and n.name == "SidebandCore")
    sense_tree = ast.parse(blobs["sbapp/sideband/sense.py"])
    commands_class = next(n for n in sense_tree.body if isinstance(n, ast.ClassDef) and n.name == "Commands")
    logs = []
    real_log, real_trace = RNS.log, RNS.trace_exception
    RNS.log = lambda message, *args, **kwargs: logs.append(str(message))
    RNS.trace_exception = lambda *args, **kwargs: None
    selected = {
        "__load_plugins", "reload_plugins", "allow_request_from", "requests_allowed_from",
        "handle_commands", "handle_plugin_command", "lxmf_delivery", "send_command",
        "request_latest_telemetry", "create_telemetry_collector_response", "get_message_fields",
    }
    namespace = {"RNS": RNS, "LXMF": LXMF, "struct": struct, "time": SimpleNamespace(time=lambda: 1700000000,
                 strftime=time.strftime, localtime=time.localtime), "os": os, "shlex": shlex,
                 "SidebandCommandPlugin": plugins.SidebandCommandPlugin,
                 "SidebandServicePlugin": plugins.SidebandServicePlugin,
                 "SidebandTelemetryPlugin": plugins.SidebandTelemetryPlugin}
    compile_nodes([commands_class], namespace, "pinned sense.py Commands")
    original_methods = [n for n in core_class.body if isinstance(n, ast.FunctionDef) and n.name in selected]
    # Execute exact AST method bodies on fake owners; no full core startup or hardware imports.
    compile_nodes(original_methods, namespace, "pinned core.py selected methods")
    Commands = namespace["Commands"]
    scratch = Path("artifacts/builds/sideband-extension-observation-inputs").resolve()
    if scratch.exists():
        raise RuntimeError(f"Refusing to overwrite {scratch}")
    scratch.mkdir(parents=True)
    try:
        calls = []
        owner = SimpleNamespace(
            config={"telemetry_collector_enabled": False, "command_plugins_enabled": True},
            active_command_plugins={"sample": SimpleNamespace(handle_command=lambda arguments, message:
                calls.append({"kind": "plugin", "arguments": arguments}))},
            send_message=lambda *args, **kwargs: calls.append({"kind": "message", "args": args, "kwargs": kwargs}),
            send_latest_telemetry=lambda **kwargs: calls.append({"kind": "telemetry", "kwargs": kwargs}),
            create_telemetry_collector_response=lambda **kwargs: calls.append({"kind": "collector", "kwargs": kwargs}))
        owner.handle_plugin_command = lambda text, message: namespace["handle_plugin_command"](owner, text, message)
        message = SimpleNamespace(source_hash=bytes(range(16)), q=91, rssi=-87.5, snr=7.25)
        incoming = []
        cases = [
            ("ping", [{Commands.PING: True}]),
            ("echo_unicode", [{Commands.ECHO: "hello ☃".encode()}]),
            ("signal", [{Commands.SIGNAL_REPORT: True}]),
            ("plugin_quotes", [{Commands.PLUGIN_COMMAND: 'sample one "two words" ""'}]),
            ("telemetry_own", [{Commands.TELEMETRY_REQUEST: [1699999900, False]}]),
            ("telemetry_collector", [{Commands.TELEMETRY_REQUEST: [1699999900, True]}]),
            ("telemetry_legacy", [{Commands.TELEMETRY_REQUEST: 1699999900}]),
            ("first_key_priority", [{Commands.PING: True, Commands.ECHO: b"ignored"}]),
            ("bad_echo_stops_batch", [{Commands.ECHO: b"\xff"}, {Commands.PING: True}]),
            ("bad_telemetry_stops_batch", [{Commands.TELEMETRY_REQUEST: []}, {Commands.PING: True}]),
        ]
        for name, commands in cases:
            owner.config["telemetry_collector_enabled"] = "collector" in name or "legacy" in name
            calls.clear()
            namespace["handle_commands"](owner, commands, message)
            incoming.append({"name": name, "fields_hex": mp.packb({LXMF.FIELD_COMMANDS: commands}).hex(),
                             "effects": jsonable(calls)})
        message.q = message.rssi = message.snr = None
        calls.clear()
        namespace["handle_commands"](owner, [{Commands.SIGNAL_REPORT: True}], message)
        incoming.append({"name": "signal_absent", "effects": jsonable(calls)})
        permissions = []
        for anyone, trusted, conv in [
            (False, False, None), (True, False, None),
            (False, True, {"trust": 1, "data": {}}),
            (False, True, {"trust": 0, "data": {"allow_requests": True}}),
            (False, False, {"trust": 0, "data": {"allow_requests": True}}),
        ]:
            auth = SimpleNamespace(config={"telemetry_allow_requests_from_anyone": anyone,
                "telemetry_allow_requests_from_trusted": trusted}, _db_conversation=lambda address: conv)
            auth.requests_allowed_from = lambda address: namespace["requests_allowed_from"](auth, address)
            permissions.append({"anyone": anyone, "trusted_setting": trusted, "conversation": conv,
                                "allowed": namespace["allow_request_from"](auth, message.source_hash)})
        delivery = []
        for valid, allowed, unknown in [(True, True, False), (True, False, False),
                                       (False, True, False), (True, True, True)]:
            effects = []
            receiver = SimpleNamespace(config={"lxmf_ignore_unknown": unknown},
                _db_conversation=lambda address: None, allow_request_from=lambda address: allowed,
                handle_commands=lambda *a: effects.append("commands"), lxm_ingest=lambda m: effects.append("ingest"))
            inbound = SimpleNamespace(timestamp=1700000000, signature_validated=valid,
                unverified_reason=0, source_hash=message.source_hash, hash=bytes(32),
                fields={LXMF.FIELD_COMMANDS: [{Commands.PING: True}]})
            namespace["lxmf_delivery"](receiver, inbound)
            delivery.append({"valid_signature": valid, "allowed": allowed,
                             "ignore_unknown": unknown, "effects": effects})
        identity = RNS.Identity()
        destination = RNS.Destination(identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
        real_recall, real_ratchet = RNS.Identity.recall, RNS.Identity.current_ratchet_id
        RNS.Identity.recall = lambda address: identity
        RNS.Identity.current_ratchet_id = lambda address: None
        outbound = []
        emitted = []
        sender = SimpleNamespace(allow_service_dispatch=False, is_client=False, lxmf_destination=destination,
            config={"lxmf_try_propagation_on_fail": False, "telemetry_use_propagation_only": False,
                    "telemetry_try_propagation_on_fail": False},
            message_router=SimpleNamespace(delivery_link_available=lambda a: True,
                get_outbound_propagation_node=lambda: None, handle_outbound=emitted.append),
            is_trusted=lambda a: False, message_notification=lambda m: None, lxm_ingest=lambda *a, **k: None,
            getstate=lambda k: False, setstate=lambda *a: None, setpersistent=lambda *a: None,
            _telemetry_timebase=lambda a: 1699999900, telemetry_request_max_history=604800,
            telemetry_request_finished=lambda m: None)
        try:
            for content in ["ping", "ping anything", "signal", "signature", "echo hello echo world", 'sample "two words"', "echo ", ""]:
                emitted.clear()
                result = namespace["send_command"](sender, content, bytes(range(16)), False)
                outbound.append({"input": content, "result": result,
                    "fields_hex": mp.packb(emitted[0].fields).hex() if emitted else None})
            for collector in [False, True]:
                emitted.clear()
                result = namespace["request_latest_telemetry"](sender, bytes(range(16)), is_collector_request=collector)
                outbound.append({"input": f"telemetry collector={collector}", "result": result,
                    "fields_hex": mp.packb(emitted[0].fields).hex() if emitted else None,
                    "include_ticket": emitted[0].include_ticket if emitted else None})
        finally:
            RNS.Identity.recall, RNS.Identity.current_ratchet_id = real_recall, real_ratchet
        sys.path.insert(0, str(source / "sbapp"))
        import sideband.sense as sense
        if Path(sense.__file__).resolve() != source / "sbapp/sideband/sense.py":
            raise RuntimeError("Unexpected sense import")
        namespace["Telemeter"] = sense.Telemeter
        packed = mp.packb({sense.Sensor.SID_TIME: 1700000000})
        own_fields_owner = SimpleNamespace(config={"telemetry_send_appearance": False,
            "telemetry_icon": "account", "telemetry_fg": [1.0, 0.0, 0.5, 1.0],
            "telemetry_bg": [0.0, 0.25, 1.0, 1.0]},
            should_send_telemetry=lambda a: True, latest_packed_telemetry=packed,
            _last_telemetry_send_success_timebase=lambda a: 0)
        own_fields = namespace["get_message_fields"](own_fields_owner, message.source_hash)
        collector_calls = []
        collector = SimpleNamespace(getstate=lambda k: False,
            config={"telemetry_requests_only_send_latest": True},
            telemetry_response_excluded=[],
            list_telemetry=lambda **kw: {
                bytes(range(16, 32)): [[1700000000, packed], [1699999990, packed]],
                message.source_hash: [[1700000000, packed]]},
            _db_get_appearance=lambda *a, **k: ["account", b"\x01\x02\x03", b"\x04\x05\x06"],
            send_latest_telemetry=lambda **kw: collector_calls.append(kw))
        namespace["create_telemetry_collector_response"](collector, message.source_hash, 1699999900,
            is_authorized_telemetry_request=True, is_collector_response=True)
        stream_fields = {LXMF.FIELD_TELEMETRY_STREAM: collector_calls[0]["stream"]}
        plugin_cases = []
        # Original generated test plugins only. Imports json from the Python standard library.
        sample = (
            "import json\n"
            "class Sample(SidebandCommandPlugin):\n"
            "    command_name = 'sample'\n"
            "    def start(self):\n"
            "        self.get_sideband().observations.append(json.loads('{\"imported\": true}'))\n"
            "        super().start()\n"
            "    def handle_command(self, arguments, message):\n"
            "        self.get_sideband().observations.append(arguments)\n"
            "plugin_class = Sample\n")
        for service_enabled, command_enabled in [(False, True), (True, False), (True, True)]:
            directory = scratch / f"plugins-{service_enabled}-{command_enabled}"
            directory.mkdir()
            (directory / "sample.py").write_text(sample, encoding="utf-8")
            for kind, name in [("Service", "service_name"), ("Telemetry", "plugin_name")]:
                (directory / f"{kind.lower()}.py").write_text(
                    f"class Sample{kind}(Sideband{kind}Plugin):\n"
                    f"    {name} = 'synthetic_{kind.lower()}'\n"
                    f"plugin_class = Sample{kind}\n", encoding="utf-8")
            host = SimpleNamespace(config={"command_plugins_path": str(directory),
                "service_plugins_enabled": service_enabled, "command_plugins_enabled": command_enabled},
                active_command_plugins={}, active_service_plugins={}, active_telemetry_plugins={}, observations=[])
            namespace["__load_plugins"](host)
            registered = sorted(host.active_command_plugins)
            plugin_cases.append({"service_enabled": service_enabled, "command_enabled": command_enabled,
                "observations": host.observations.copy(), "registered": registered,
                "services": sorted(host.active_service_plugins), "telemetry": sorted(host.active_telemetry_plugins),
                "same_owner": host.active_command_plugins["sample"].get_sideband() is host if registered else None})
            active = [*host.active_command_plugins.values(), *host.active_service_plugins.values(),
                      *host.active_telemetry_plugins.values()]
            host.__load_plugins = lambda: namespace["__load_plugins"](host)
            namespace["reload_plugins"](host)
            plugin_cases[-1]["reload_stopped_previous"] = all(not plugin.is_running() for plugin in active)
            for registry in [host.active_command_plugins, host.active_service_plugins, host.active_telemetry_plugins]:
                for plugin in registry.values():
                    plugin.stop()
        missing = scratch / "missing-class"
        missing.mkdir()
        (missing / "sample.py").write_text("import json\n", encoding="utf-8")
        host.config["command_plugins_path"] = str(missing)
        try:
            namespace["__load_plugins"](host)
            missing_result = "no exception"
        except Exception as error:
            missing_result = type(error).__name__
        # Run the real nested request handler against in-memory synthetic requests, without sockets/TLS.
        handler_node = next(n for n in ast.walk(core_class) if isinstance(n, ast.ClassDef) and n.name == "RequestHandler")
        import http.server
        handler_ns = {"server": http.server, "webshare_dir": str(scratch), "os": os, "json": json, "RNS": RNS}
        compile_nodes([handler_node], handler_ns, "pinned core.py repository RequestHandler")
        (scratch / "pkg").mkdir()
        (scratch / "pkg" / "z.apk").write_bytes(b"synthetic-apk")
        (scratch / "pkg" / "a.txt").write_bytes(b"synthetic-text")
        (scratch / "index.html").write_bytes(b"synthetic-index")
        routes = []
        handler_type = handler_ns["RequestHandler"]
        for route in ["/", "/pkglist", "/pkg/z.apk?download=1", "/missing", "/pkg/a.txt"]:
            handler = object.__new__(handler_type)
            status, headers = [], []
            handler.path, handler.wfile = route, io.BytesIO()
            handler.send_response = status.append
            handler.send_header = lambda key, value: headers.append([key, value])
            handler.end_headers = lambda: None
            handler.do_GET()
            routes.append({"path": route, "statuses": status, "headers": headers,
                           "body_utf8": handler.wfile.getvalue().decode()})
        examples = []
        for path in sorted((source / "docs/example_plugins").glob("*.py")):
            text = path.read_text(encoding="utf-8")
            tree = ast.parse(text)
            blob = subprocess.check_output(["git", "-C", str(source), "show", f"{PIN}:docs/example_plugins/{path.name}"])
            if text.replace("\r\n", "\n") != blob.decode().replace("\r\n", "\n"):
                raise RuntimeError(f"Modified example {path.name}")
            examples.append({"file": path.name, "sha256": hashlib.sha256(blob).hexdigest(),
                "imports": sorted({ast.unparse(n) for n in ast.walk(tree) if isinstance(n, (ast.Import, ast.ImportFrom))}),
                "core_api_names": sorted({n.func.attr for n in ast.walk(tree) if isinstance(n, ast.Call)
                    and isinstance(n.func, ast.Attribute) and isinstance(n.func.value, ast.Call)
                    and isinstance(n.func.value.func, ast.Attribute) and n.func.value.func.attr == "get_sideband"}),
                "executed": False})
        certgen = load_module("extension_reference_certgen", source / "sbapp/sideband/certgen.py")
        from cryptography import x509
        from cryptography.hazmat.primitives import serialization
        from cryptography.x509.oid import NameOID
        key_path, cert_path = scratch / "key.pem", scratch / "cert.pem"
        certgen.ensure_certificate(str(key_path), str(cert_path))
        first = x509.load_pem_x509_certificate(cert_path.read_bytes())
        public = first.public_key().public_bytes(serialization.Encoding.DER,
                                                serialization.PublicFormat.SubjectPublicKeyInfo)
        certgen.ensure_certificate(str(key_path), str(cert_path))
        second = x509.load_pem_x509_certificate(cert_path.read_bytes())
        cert_observation = {
            "common_name": first.subject.get_attributes_for_oid(NameOID.COMMON_NAME)[0].value,
            "self_issued": first.issuer == first.subject,
            "san_dns": first.extensions.get_extension_for_class(x509.SubjectAlternativeName).value.get_values_for_type(x509.DNSName),
            "key_curve": first.public_key().curve.name, "signature_hash": first.signature_hash_algorithm.name,
            "validity_days": (first.not_valid_after_utc - first.not_valid_before_utc).days,
            "restart_reuses_public_key": public == second.public_key().public_bytes(
                serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo),
            "restart_regenerates_certificate": first.serial_number != second.serial_number,
            "private_material_recorded": False}
        result = {"sideband_sha": PIN, "source_sha256": hashes,
            "reference_pins": {"rns": "a29bfb51c468945c96a1ee22cae0114e993e2391",
                "lxmf": "e8ba02483217af43be284a72f1c059c730b7d047",
                "lxst": "eba5abc77fef7fef3bcb6c9c091c80b7f7492c9f"},
            "method": "Exact pinned AST methods; imported unmodified plugin bases; actual LXMF messages and MessagePack; dependency-controlled owners.",
            "disclosures": [
                "No full SidebandCore startup, sockets, live LXMF peers, TLS handshake, hardware, GUI or remote downloads.",
                "Clock fixed at 1700000000; Identity.recall/current_ratchet_id temporarily injected for outbound messages.",
                "RNS logs captured; observer methods record arguments. Only original synthetic plugins executed.",
                "Repository handler executed with memory-backed response methods and original generated local files.",
                "Shipped plugin imports/APIs inventoried by AST, never executed.",
                "Reference verifier imports LXST; this Windows environment warns and falls back to Python filters. No filter/audio methods are exercised here."],
            "commands": {name: getattr(Commands, name) for name in
                         ["PLUGIN_COMMAND", "TELEMETRY_REQUEST", "PING", "ECHO", "SIGNAL_REPORT"]},
            "field_ids": {"commands": LXMF.FIELD_COMMANDS, "telemetry": LXMF.FIELD_TELEMETRY,
                          "telemetry_stream": LXMF.FIELD_TELEMETRY_STREAM},
            "incoming": incoming, "outgoing": outbound, "permissions": permissions,
            "telemetry_response": {"own_fields_hex": mp.packb(own_fields).hex(),
                "collector_fields_hex": mp.packb(stream_fields).hex(), "collector_effect": jsonable(collector_calls[0])},
            "delivery_gate": delivery, "plugin_loader": plugin_cases,
            "plugin_missing_class": missing_result, "repository_routes": routes,
            "certificate": cert_observation, "examples": examples,
            "inventory": {
                "plugin_boundary": [
                    "plugins.py: SidebandPlugin is an empty marker; three bases own core reference and running flag.",
                    "Constructors require class command_name, service_name or plugin_name. start/stop/is_running/get_sideband are synchronous.",
                    "Command base handle_command declares arguments only, while core dispatch and examples pass arguments and LXMessage.",
                    "Command examples call core.send_message(content,destination_hash,propagation,skip_fields=False,no_display=False,attachment=None,image=None,audio=None); LXMessage supplies source_hash and content.",
                    "core.py:983-1056 loads only with service_plugins_enabled; command_plugins_enabled alone does not load anything.",
                    "Loader scans top-level .py case-insensitively with unsorted os.listdir, execs fresh globals with injected base types and normal builtins.",
                    "Each module exports plugin_class, constructed with full SidebandCore, then start/is_running checked before registration.",
                    "Disabled command plugin can still execute module/start when service loading is enabled; it is stopped as unknown type.",
                    "Module exec failures are logged and skipped; missing plugin_class, constructor and start errors escape this method.",
                    "Duplicate service/telemetry registrations stop newcomer; duplicate command registration logs but does not stop newcomer.",
                    "reload_plugins stops all registered telemetry/command/service plugins, clears registries and loads again.",
                    "core.py:3674-3681 invokes update_telemetry(telemeter) on a copied registry with per-plugin exception isolation.",
                    "No sandbox, dependency manifest, permissions broker, restricted core facade, package resolver or RPC protocol exists here.",
                    "Arbitrary Python plugins require Python language/import/runtime and actual SidebandCore/RNS/LXMF/Telemeter object APIs.",
                    "Native C#/NativeAOT extensions or a new RPC bridge are explicit alternate extension contracts, not arbitrary Python plugin compatibility."
                ],
                "authorization": [
                    "core.py:5665-5691 ignores unknown senders first when configured; valid-signature FIELD_COMMANDS requires allow_request_from.",
                    "Invalid/unverified-signature command messages go to normal lxm_ingest, never command dispatch; valid unauthorized commands are dropped.",
                    "allow_request_from permits anyone if configured, otherwise trusted setting returns only conversation trust==1, otherwise per-conversation data.allow_requests.",
                    "When trusted-only setting is enabled an untrusted conversation's explicit allow_requests does not override it.",
                    "Default anyone/trusted/plugin enable flags are false; same request permission gate protects built-ins and plugin commands.",
                    "No special echo/ping privilege tier, command nonce, expiry or per-command authentication field is present in these handlers.",
                    "Malformed command raises into one outer handler catch, aborting remaining commands in that message."
                ],
                "command_shapes": [
                    "FIELD_COMMANDS=9 maps to array of command maps; ids 0 plugin string, 1 telemetry request, 2 ping, 3 echo bytes, 4 signal.",
                    "Telemetry request value is [timebase, collector_boolean]; legacy scalar timebase means collector enabled.",
                    "Ping/signal request values are True; responses are ordinary text messages, not structured command-result maps.",
                    "Echo bytes decode UTF-8 strictly; response prefix is Echo reply: . Plugin string uses Python shlex.split.",
                    "send_command uses startswith sig/ping, removes every occurrence of echo-space from echo input, and falls back to plugin string.",
                    "Telemetry own response is FIELD_TELEMETRY=2 packed sensor bytes. FIELD_TELEMETRY_STREAM=3 is [[source_hash,timestamp,packed_sensor_bytes,appearance],...].",
                    "Collector excludes requester and excluded sources, honors latest-only, and delegates to send_latest_telemetry; no request ID correlation field.",
                    "Telemetry own-request handler calls send_latest_telemetry without authorized override; collector branch passes authorized and collector flags.",
                    "Ordinary commands choose propagated/direct/opportunistic from caller/ratchet/link policy; include_ticket follows trust.",
                    "Telemetry requests include_ticket=True regardless of trust; fallback obeys telemetry_try_propagation_on_fail and livetrack exclusion."
                ],
                "repository": [
                    "core.py:5540-5632 is optional manually started threaded HTTPS static mirror, not RNS/rngit hosting.",
                    "Fixed bind all interfaces and port 4444, no configuration keys or authentication/access-control/mTLS in inspected server.",
                    "Root maps to index.html; /pkglist returns sorted os.listdir(pkg) as text/json; /pkg/<name> and other static paths read local files.",
                    "Query strings stripped; only %20 decoded; APK/JS MIME headers special; missing file returns 500 rather than 404.",
                    "SimpleHTTPRequestHandler inheritance also supplies HEAD independently of custom GET; native implementation should explicitly constrain methods/root.",
                    "certgen.py generates EC P-256 unencrypted PKCS8 key if absent; self-signed SHA256 cert CN Sideband Repository, SAN localhost.",
                    "Certificate valid from now minus 14 days to now plus 3652 days; regenerated each ensure_certificate using existing private key.",
                    "main.py:4467-4556 Update Content queries first GitHub release for RNode firmware and Sideband, downloads matching firmware assets and last APK asset.",
                    "Downloader streams 32KiB chunks to cwd dl_tmp then os.rename into share/pkg, with no explicit timeout/status/hash/signature checks or old-package pruning.",
                    "No remote update was executed; original upstream static pages/assets were neither copied nor served in observations.",
                    "share contains index, software list, guides and RNode flasher plus CSS/gfx; these are separate distribution/licensing assets.",
                    "Parent native scope choice: bounded user-selected static mirror with original UI/content and explicit TLS/root policy, or documented deferral; do not silently copy assets or reproduce unsafe defaults."
                ],
                "dependencies_and_licenses": [
                    "Sideband root is CC BY-NC-SA 4.0, unlike repository Reticulum License. certgen.py separately carries MIT notice.",
                    "Plugin examples depend on ordinary Python ecosystem modules: requests/Pillow/OpenCV, psutil, IP2Location, gpsdclient, winsdk, smbus2/bme280.",
                    "Examples may start threads, read local files/identities/OS telemetry, open cameras/streams/GPS/I2C and contact HTTP services; none executed here.",
                    "Telemetry plugins directly mutate Telemeter sensors or call synthesis/update_entry/update_consumer/update_producer/add_point/add_interface/set_identity/set_update_time APIs.",
                    "GPS plugins set location latitude/longitude/altitude/speed/bearing/accuracy/stale_time and set_update_time; connection-map reads rns_transport and adds named points; interface telemetry adds typed connectable interface dictionaries.",
                    "Interface telemetry reaches RNS.Transport.identity; LXMd telemetry points to a local lxmd private identity path.",
                    "Python sensor/device/native dependencies and upstream assets must not become production dependencies or be redistributed under root license by assumption.",
                    "Full arbitrary Python compatibility conflicts with production no-Python constraint; decide explicit deferral versus separately specified original native extensions."
                ]}}
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"Recorded {len(incoming)} command effects, {len(outbound)} sends, {len(plugin_cases)} loader states, "
              f"{len(routes)} repository routes and {len(examples)} source-only example inventories")
    finally:
        RNS.log, RNS.trace_exception = real_log, real_trace
        shutil.rmtree(scratch)


if __name__ == "__main__":
    main()
