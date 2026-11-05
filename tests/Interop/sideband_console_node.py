"""Actual primary CLI registry/handlers over an actual Sideband desktop core.

The JSON/base64 process envelope is harness-only. No CLI handler or application
protocol is replaced. Context methods only observe terminal clear/stream/quit.
"""
import argparse
import base64
import hashlib
import importlib
import inspect
import os
from pathlib import Path
import subprocess
import sys

from live_lxmf_node import Handler, command_lines, emit, write_config
from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
CORE_SHA = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"


class Context:
    def clear(self):
        emit({"event": "context", "action": "clear"})

    def enter_stream_mode(self):
        emit({"event": "context", "action": "stream"})

    def quit(self):
        emit({"event": "context", "action": "quit"})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, default=0)
    args = parser.parse_args()
    lxst_source = os.environ.get("DOTRETICULUM_LXST_UPSTREAM")
    if lxst_source:
        sys.path.insert(0, str(Path(lxst_source).resolve()))
    verify_reference(require_lxmf=True, require_lxst=True)
    source = args.source.resolve()
    if subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip() != PIN:
        raise RuntimeError("Unexpected primary Sideband revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Modified primary Sideband source")
    core_path = source / "sbapp/sideband/core.py"
    if hashlib.sha256(core_path.read_bytes()).hexdigest() != CORE_SHA:
        raise RuntimeError("Unexpected primary core digest")
    sys.path.insert(0, str(source))
    from sbapp.sideband.core import SidebandCore
    import RNS
    import LXMF
    if Path(inspect.getfile(SidebandCore)).resolve() != core_path:
        raise RuntimeError("Wrong imported primary core")
    modules = [importlib.import_module("sbapp.sideband.cli." + name) for name in
               ("commands", "cmd_system", "cmd_conv", "cmd_net", "cmd_config", "cmd_telemetry", "cmd_voice", "cmd_plugins")]
    for module in modules:
        expected = source / "sbapp/sideband/cli" / (module.__name__.split(".")[-1] + ".py")
        if Path(module.__file__).resolve() != expected:
            raise RuntimeError("Wrong actual CLI import")
    write_config(str(args.config / "rns"), args.tcp_server_port, args.rnsd_port)
    core = SidebandCore(None, config_path=str(args.config / "profile"),
                        rns_config_path=str(args.config / "rns"), is_daemon=True, quiet=True)
    core.config["start_announce"] = False
    core.start()
    for module in modules[1:]:
        module.register(core)
    commands = modules[0]
    context = Context()
    RNS.Transport.register_announce_handler(Handler())
    emit({"event": "ready", "dest": core.lxmf_destination.hash.hex(), "identity_hash": core.identity.hash.hex(),
          "revision": PIN, "source_sha256": CORE_SHA, "core_path": str(core_path),
          "cli_modules": {module.__name__: hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest() for module in modules}})
    for line in command_lines():
        parts = line.strip().split()
        if not parts:
            continue
        if parts == ["quit"]:
            core.save_configuration(no_thread=True)
            core.cleanup()
            RNS.Reticulum.exit_handler()
            os._exit(0)
        if parts[0] == "cli" and len(parts) == 2:
            text = base64.b64decode(parts[1], validate=True).decode("utf-8")
            tokens = commands.parse_input(text)
            command, remaining = commands.resolve_command(tokens)
            if command is None:
                emit({"event": "console", "error": "unknown command"})
            elif command.name() in ("raw", "plugins reload"):
                raise ValueError("Unsafe/out-of-scope harness command")
            else:
                error = command.validate_args(remaining)
                try:
                    result = None if error else command.execute(remaining, core, context)
                    emit({"event": "console", "command": command.name(), "result": result, "error": error})
                except Exception as error:
                    emit({"event": "console", "command": command.name(), "error": str(error)})
        elif parts == ["snapshot"]:
            conversations = []
            for conv in core.list_conversations(objects=True):
                dest = conv["dest"]
                messages = core.list_messages(dest, limit=32) or []
                conversations.append({"dest": dest.hex(), "name": core.peer_display_name(dest),
                                      "trust": bool(conv.get("trust")), "unread": bool(conv.get("unread")),
                                      "messages": [{"content": base64.b64encode(message["content"]).decode(),
                                                    "renderer": message["lxm"].fields.get(LXMF.FIELD_RENDERER),
                                                    "state": message["state"], "method": message["method"],
                                                    "state_text": modules[2]._format_msg_state(message["state"], message["method"])}
                                                   for message in messages]})
            emit({"event": "snapshot", "conversations": conversations})
        elif parts == ["enable-pn"]:
            # Explicit actual LXMF router role, not a claim that Sideband UI hosts a PN.
            core.message_router.enable_propagation()
            core.message_router.propagation_destination.announce(app_data=core.message_router.get_propagation_node_app_data())
            emit({"event": "pn-enabled", "dest": core.message_router.propagation_destination.hash.hex()})
        else:
            raise ValueError("Unknown observation command")


if __name__ == "__main__":
    main()
