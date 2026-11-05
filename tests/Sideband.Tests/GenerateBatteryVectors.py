"""Observe actual primary Battery with disclosed platform/sysfs boundaries, not hardware."""
import argparse
import hashlib
import io
import json
import subprocess
import sys
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    if subprocess.check_output(["git", "-C", str(args.source), "rev-parse", "HEAD"], text=True).strip() != PIN:
        raise RuntimeError("Unexpected primary revision")
    sys.path.insert(0, str(args.source / "sbapp"))
    from sideband import sense
    expected = args.source / "sbapp" / "sideband" / "sense.py"
    if Path(sense.__file__).resolve() != expected.resolve():
        raise RuntimeError("Unexpected imported sensor source")
    source = subprocess.check_output(["git", "-C", str(args.source), "show", f"{PIN}:sbapp/sideband/sense.py"])
    if expected.read_bytes().replace(b"\r\n", b"\n") != source:
        raise RuntimeError("Imported sensor source differs from the pinned source")
    cases = []
    for name, nodes, text, linux in (
        ("charging", [0], "POWER_SUPPLY_STATUS=Charging\nPOWER_SUPPLY_CAPACITY=42.25\n", True),
        ("discharging", [0], "POWER_SUPPLY_STATUS=Discharging\nPOWER_SUPPLY_CAPACITY=87.65\n", True),
        ("full", [9], "POWER_SUPPLY_STATUS=Full\nPOWER_SUPPLY_CAPACITY=100\n", True),
        ("first-node", [0, 9], "POWER_SUPPLY_STATUS=Unknown\nPOWER_SUPPLY_CAPACITY=0\n", True),
        ("whitespace", [3], "  POWER_SUPPLY_STATUS=Charging  \n POWER_SUPPLY_CAPACITY=12.35 \n", True),
        ("missing-status", [0], "POWER_SUPPLY_CAPACITY=50\n", True),
        ("invalid-capacity", [0], "POWER_SUPPLY_STATUS=Charging\nPOWER_SUPPLY_CAPACITY=invalid\n", True),
        ("malformed-line", [0], "BROKEN\nPOWER_SUPPLY_STATUS=Charging\nPOWER_SUPPLY_CAPACITY=50\n", True),
        ("no-battery", [], "", True),
        ("windows", [], "", False),
        ("negative-primary", [0], "POWER_SUPPLY_STATUS=Charging\nPOWER_SUPPLY_CAPACITY=-1\n", True),
        ("overfull-primary", [0], "POWER_SUPPLY_STATUS=Charging\nPOWER_SUPPLY_CAPACITY=101\n", True),
    ):
        reads = []
        directories = []

        def isdir(path):
            normalized = str(path).replace("\\", "/")
            directories.append(normalized)
            return normalized in [f"/sys/class/power_supply/BAT{index}" for index in nodes]

        def open_observed(path, mode):
            normalized = str(path).replace("\\", "/")
            reads.append(normalized)
            if mode != "rb" or normalized not in [f"/sys/class/power_supply/BAT{index}/uevent" for index in nodes]:
                raise RuntimeError("Unexpected physical filesystem access")
            return io.BytesIO(text.encode("utf-8"))

        with patch.object(sense.RNS.vendor.platformutils, "is_android", return_value=False), \
                patch.object(sense.RNS.vendor.platformutils, "is_linux", return_value=linux), \
                patch.object(sense.os.path, "isdir", side_effect=isdir), \
                patch("builtins.open", side_effect=open_observed), \
                patch.object(sense.time, "time", return_value=1700000000):
            battery = sense.Battery()
            battery.start()
            packed = battery.pack()
            data = battery.data
            cases.append({"name": name, "linux": linux, "nodes": nodes, "uevent": text,
                          "packed": packed, "data": data, "selected_node": getattr(battery, "battery_node_name", None),
                          "directories": directories, "reads": reads})
    result = {
        "revision": PIN, "source_sha256": hashlib.sha256(source).hexdigest(),
        "method": "Actual imported Battery constructor/start/data/pack; observed synthetic Linux/platform flags, BAT0..9 directory and uevent file boundaries, fixed clock. No physical battery, Linux process or full application evidence.",
        "cases": cases,
    }
    with args.output.open("w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(result, indent=4, allow_nan=False) + "\n")


if __name__ == "__main__":
    main()
