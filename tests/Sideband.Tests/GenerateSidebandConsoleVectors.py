"""Execute the pinned primary console registry, without starting Sideband or devices."""
import argparse
import hashlib
import importlib
import json
from pathlib import Path
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    source = args.source.resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != PIN:
        raise RuntimeError(f"Expected {PIN}, got {revision}")
    names = ("sbapp/sideband/cli/commands.py", "sbapp/sideband/cli/cmd_voice.py")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain", "--", *names], text=True).strip():
        raise RuntimeError("Reference console files are modified")
    sys.path.insert(0, str(source / "sbapp"))
    voice = importlib.import_module("sideband.cli.cmd_voice")
    if Path(voice.__file__).resolve() != source / names[1]:
        raise RuntimeError("Imported an unexpected console module")
    voice.register()
    cases = []
    for command in voice.registry.all_commands():
        arguments = ["0123456789abcdef0123456789abcdef"] * command.min_args
        if command.validate_args(arguments) is not None:
            raise RuntimeError("Reference command validation failed")
        cases.append({"command": command.name(), "handler": command.handler is not None,
                      "result": command.execute(arguments, None, None)})
    result = {"sideband_sha": revision,
              "source_sha256": {name: hashlib.sha256((source / name).read_bytes()).hexdigest() for name in names},
              "method": "Import actual cmd_voice, register, validate and execute each actual Command",
              "cases": cases}
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
