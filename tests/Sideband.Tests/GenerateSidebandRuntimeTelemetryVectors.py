"""Execute unchanged pinned RNSTransport collection against disclosed synthetic runtime observations."""
import argparse
import copy
import hashlib
import importlib
import json
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
SENSE = "80418b1aa4de638f2c65f1bd64e5ea0466378a6f0bc682781672ef89b93a7dab"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference
    verify_reference(require_lxmf=True)
    source = args.source.resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != PIN:
        raise RuntimeError("Unexpected primary revision")
    blob = subprocess.check_output(["git", "-C", str(source), "show", f"{PIN}:sbapp/sideband/sense.py"])
    if hashlib.sha256(blob).hexdigest() != SENSE:
        raise RuntimeError("Unexpected sense.py Git blob")
    actual = source / "sbapp" / "sideband" / "sense.py"
    if actual.read_bytes().replace(b"\r\n", b"\n") != blob:
        raise RuntimeError("Actual sense.py differs from the pinned source")
    sys.path.insert(0, str(source / "sbapp"))
    sense = importlib.import_module("sideband.sense")
    if Path(sense.__file__).resolve() != actual:
        raise RuntimeError("Unexpected actual sensor import")
    from RNS.vendor import umsgpack as msgpack
    import RNS
    now = [1700000000.0]
    sense.time = SimpleNamespace(time=lambda: now[0])
    identity = bytes(range(16))
    statistics = {
        "rxb": 1000, "txb": 2000, "rxs": 80.0, "txs": 160.0, "rss": 65536,
        "transport_uptime": 3600.0,
        "interfaces": [{"name": "Synthetic TCP", "rxb": 1000, "txb": 2000}],
    }
    paths = [
        {"hash": bytes([2]) * 16, "via": bytes([3]) * 16, "interface": "Synthetic TCP", "hops": 3},
        {"hash": bytes([4]) * 16, "via": bytes([5]) * 16, "interface": "Synthetic TCP", "hops": 1},
    ]
    calls = []

    def get_statistics():
        calls.append("interface_stats")
        return copy.deepcopy(statistics)

    def get_paths(max_hops):
        calls.append(["path_table", max_hops])
        return copy.deepcopy(paths)

    def get_links():
        calls.append("link_count")
        return 2

    runtime = SimpleNamespace(get_interface_stats=get_statistics, get_path_table=get_paths, get_link_count=get_links)
    sense.RNS = SimpleNamespace(
        Reticulum=SimpleNamespace(get_instance=lambda: runtime),
        Transport=SimpleNamespace(identity=SimpleNamespace(hash=identity), PATHFINDER_M=RNS.Transport.PATHFINDER_M))
    sensor = sense.RNSTransport()
    observations = []

    def capture(name, *, explicit=False):
        if explicit:
            sensor.update_data()
        data = sensor.data
        observations.append({
            "name": name, "now": now[0], "explicit": explicit, "sparse": sensor.sparse,
            "input_stats_hex": msgpack.packb(statistics).hex(),
            "input_paths_hex": msgpack.packb(paths).hex(),
            "transport_identity": identity.hex(), "link_count": 2,
            "data_hex": msgpack.packb(data).hex(), "packed_hex": sensor.packb().hex(),
            "calls": copy.deepcopy(calls), "last_update": sensor.last_update,
        })
        calls.clear()

    sensor.start()
    capture("first-read-uses-instantaneous-speeds-and-sparse-default")
    statistics["rxb"] = 1250
    statistics["txb"] = 2500
    now[0] += 59
    capture("read-before-sixty-seconds-retains-observation")
    now[0] += 1
    capture("read-exactly-sixty-seconds-remains-cached")
    now[0] += 1
    capture("read-after-sixty-seconds-uses-counter-deltas")
    now[0] += 60
    statistics["rxb"] = 2000
    statistics["txb"] = 4000
    sensor.sparse = False
    capture("explicit-update-at-sixty-seconds-includes-dense-tables", explicit=True)
    now[0] += 61
    statistics.pop("rss")
    statistics.pop("transport_uptime")
    capture("missing-memory-and-disabled-transport-remain-explicit")
    now[0] += 61
    statistics["rxb"] = 2**63 + 3
    statistics["txb"] = 2**63 + 4
    capture("large-integer-counter-baseline")
    now[0] += 61
    statistics["rxb"] += 1
    statistics["txb"] += 1
    capture("one-byte-delta-above-double-integer-precision")
    now[0] += 61
    statistics["rxb"] = 1
    statistics["txb"] = 2
    capture("counter-reset-preserves-reference-negative-delta")
    received = sense.Telemeter.from_packed(msgpack.packb({sensor.sid: sensor.data})).sensors["rns_transport"]
    receiver = {
        "input_sensor_hex": msgpack.packb(sensor.data).hex(),
        "default_sparse": received.sparse,
        "repacked_sensor_hex": received.packb().hex(),
        "rendered_interface_names": list(received.render()["values"]["interfaces"]),
    }
    sensor.stop()
    stopped = {"active": sensor.active, "data_is_none": sensor._data is None}
    result = {
        "revision": PIN, "sense_sha256": SENSE, "stale_time": sense.RNSTransport.STALE_TIME,
        "method": "Actual imported RNSTransport/Sensor constructors, start, data getter, update_data, packb and stop; only module clock and public runtime-query dependencies are synthetic observers. This is executed collection/timing evidence, not live transport, physical sensors or primary GUI acceptance.",
        "observations": observations, "receiver": receiver, "stopped": stopped,
    }
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps({"revision": PIN, "observations": len(observations),
                      "sha256": hashlib.sha256(args.output.read_bytes()).hexdigest()}))


if __name__ == "__main__":
    main()
