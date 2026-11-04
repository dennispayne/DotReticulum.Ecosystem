"""Observe the actual pinned CLI registry, parser, conversions and handlers.

The recording core/context below are API-boundary doubles, not Sideband core
implementations. Live application interoperability requires a separate adapter.
"""
import argparse
import contextlib
import hashlib
import importlib
import io
import json
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"


class Observation:
    def __init__(self):
        self.calls = []
        self.config = {"display_name": "CLI reference", "telemetry_enabled": False}
        self.identity = None
        self.message_router = None
        self.lxmf_destination = None
        self.voice_running = False
        self.reticulum = None
        self.is_daemon = True
        self.active_propagation_node = None
        self.active_service_plugins = {}
        self.active_command_plugins = {}
        self.active_telemetry_plugins = {}

    def __getattr__(self, name):
        def observe(*args, **kwargs):
            self.calls.append({"method": name, "args": clean(args), "kwargs": clean(kwargs)})
            if name in ("list_conversations", "list_messages", "list_announces", "peer_telemetry"):
                return []
            if name == "list_telemetry":
                return {}
            if name == "get_sync_progress":
                return 0
            if name == "get_sync_status":
                return "idle"
            if name == "peer_display_name":
                return "Recorded peer"
            if name == "has_conversation":
                return False
            return True
        return observe


def clean(value):
    if isinstance(value, bytes):
        return {"hex": value.hex()}
    if isinstance(value, dict):
        return {key: clean(item) for key, item in value.items()}
    if isinstance(value, (tuple, list)):
        return [clean(item) for item in value]
    return value


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    source = args.source.resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != PIN:
        raise RuntimeError(f"Unexpected reference revision {revision}")
    names = ["commands", "config_tree", "cmd_system", "cmd_conv", "cmd_net",
             "cmd_config", "cmd_telemetry", "cmd_voice", "cmd_plugins"]
    paths = [f"sbapp/sideband/cli/{name}.py" for name in names]
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain", "--",
                                *paths, "sbapp/sideband/core.py"], text=True).strip():
        raise RuntimeError("Reference CLI source is modified")
    sys.path.insert(0, str(source / "sbapp"))
    modules = {name: importlib.import_module("sideband.cli." + name) for name in names}
    for name, module in modules.items():
        if Path(module.__file__).resolve() != source / f"sbapp/sideband/cli/{name}.py":
            raise RuntimeError(f"Wrong actual import for {name}")
    registry = modules["commands"].registry
    for name in names[2:]:
        modules[name].register()
    commands = []
    for command in registry.all_commands():
        observations = Observation()
        arguments = ["0123456789abcdef0123456789abcdef"] * command.min_args
        if command.name() == "telemetry track":
            arguments = [arguments[0], "60", "120"]
        if command.name() == "config get":
            arguments = ["general", "display_name"]
        if command.name() == "config set":
            arguments = ["general", "display_name", "Observed Unicode Ω"]
        if command.name() == "raw":
            result = "not executed: arbitrary evaluation excluded"
        else:
            result = command.execute(arguments, observations, observations)
        commands.append({"command": command.name(),
                         "source": command.handler.__module__.split(".")[-1] + ".py" if command.handler else "cmd_voice.py",
                         "aliases": [" ".join(alias) for alias in command.aliases],
                         "min": command.min_args, "max": command.max_args,
                         "handler": command.handler is not None, "arguments": arguments,
                         "result": clean(result), "calls": observations.calls,
                         "too_few": command.validate_args(["x"] * max(0, command.min_args - 1)),
                         "too_many": command.validate_args(["x"] * (command.max_args + 1)) if command.max_args is not None else None})
    lines = ["", "conv send peer \"hello world\"", "conv create abc ''",
             "conv name abc 'Unicode Ω 雪'", r'conv send abc escaped\ space',
             r'conv send abc "quote\"here"', "conv sync status", "config set general display_name a b",
             "conv send abc 'unclosed", "conv send abc trailing\\", "status; quit",
             "conv\tsend abc\tmessage", "conv send abc \"a\\q\""]
    parsed = []
    for line in lines:
        tokens = modules["commands"].parse_input(line)
        command, remaining = modules["commands"].resolve_command(tokens)
        parsed.append({"line": line, "tokens": tokens, "command": command.name() if command else None,
                       "remaining": remaining})
    tree = modules["config_tree"]
    import RNS
    log_cases = []
    log_filter_cases = []
    log_startup_cases = []
    previous_level = RNS.loglevel
    previous_destination = RNS.logdest
    try:
        for arguments in [[], *[[str(level)] for level in range(8)],
                          ["-1"], ["8"], ["invalid"], ["1.5"], ["+6"], [" 2 "]]:
            RNS.loglevel = 3
            result = modules["cmd_system"]._cmd_log(arguments, Observation(), Observation())
            log_cases.append({"arguments": arguments, "result": result, "level": RNS.loglevel})
        RNS.logdest = RNS.LOG_STDOUT
        for threshold in range(8):
            RNS.loglevel = threshold
            for level in range(9):
                capture = io.StringIO()
                with contextlib.redirect_stdout(capture):
                    RNS.log("Synthetic runtime filter observation", level=level)
                log_filter_cases.append({"threshold": threshold, "level": level, "emitted": bool(capture.getvalue())})
        sys.path.insert(0, str(source))
        core = importlib.import_module("sideband.core")
        core_path = source / "sbapp/sideband/core.py"
        if Path(core.__file__).resolve() != core_path:
            raise RuntimeError("Wrong actual Sideband core import")
        for quiet in (False, True):
            for verbose in (False, True):
                owner = SimpleNamespace(log_quiet=quiet, log_verbose=False, is_client=False)
                core.SidebandCore._reticulum_log_debug(owner, verbose)
                log_startup_cases.append({"quiet": quiet, "verbose": verbose, "level": RNS.loglevel})
    finally:
        RNS.loglevel = previous_level
        RNS.logdest = previous_destination
    conversions = []
    for key, values in {"debug": ["true", "enabled", "disable", "invalid"],
                        "lxmf_sync_limit": ["none", "3", "true", "-1"],
                        "display_name": ["none", "", "Unicode Ω"],
                        "map_lat": ["1.25", "null", "oops"],
                        "telemetry_s_fixed_latlon": ["[1,2]", "1, 2", "3"]}.items():
        for value in values:
            try:
                conversions.append({"key": key, "input": value, "result": tree.convert_value(key, value)})
            except ValueError as error:
                conversions.append({"key": key, "input": value, "error": str(error)})
    result = {"sideband_sha": revision,
              "source_sha256": {path: hashlib.sha256((source / path).read_bytes()).hexdigest() for path in paths},
              "method": "Actual registrations, Command validation/execution, shlex parsing, config conversions, log-level handlers, RNS.log stdout filtering and SidebandCore._reticulum_log_debug on a boundary owner; no application constructor or live core in these observations.",
              "commands": commands, "parsing": parsed,
              "log_cases": log_cases,
              "log_filter_cases": log_filter_cases,
              "log_startup_cases": log_startup_cases,
              "log_startup_source_sha256": hashlib.sha256(core_path.read_bytes()).hexdigest(),
              "rns_logging_source_sha256": hashlib.sha256(Path(RNS.__file__).read_bytes()).hexdigest(),
              "configuration": [{"key": key, "category": meta["category"], "alias": meta["alias"], "type": meta["type"]}
                                for key, meta in tree.CONFIG_METADATA.items()],
              "conversions": conversions}
    args.output.write_bytes((json.dumps(result, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))


if __name__ == "__main__":
    main()
