"""Observe pinned primary MQTT projections and actual bundled-paho encoding."""
import argparse
from collections import deque
import hashlib
import importlib
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import struct
import threading
from types import SimpleNamespace

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    source = args.source.resolve()
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference
    verify_reference(require_lxmf=True)
    revision = subprocess.check_output(
        ["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != PIN:
        raise RuntimeError("Unexpected primary revision")
    hashes = {}
    for name in ("sbapp/sideband/mqtt.py", "sbapp/sideband/sense.py",
                 "sbapp/sideband/geo.py", "sbapp/pmqtt/client.py"):
        blob = subprocess.check_output(
            ["git", "-C", str(source), "show", f"{PIN}:{name}"])
        if (source / name).read_bytes().replace(b"\r\n", b"\n") != blob:
            raise RuntimeError(f"Modified actual primary source: {name}")
        hashes[name] = hashlib.sha256(blob).hexdigest()
    os.environ["TELEMETER_GEOID_PATH"] = str(source / "sbapp/assets/geoids")
    sys.path.insert(0, str(source))
    primary = importlib.import_module("sbapp.sideband.mqtt")
    if Path(primary.__file__).resolve() != source / "sbapp/sideband/mqtt.py":
        raise RuntimeError("Unexpected actual MQTT import")
    sense = importlib.import_module("sbapp.sideband.sense")
    sense.time = SimpleNamespace(time=lambda: 1700000000)
    background_starts = []

    class ThreadBoundary:
        def __init__(self, *, target, daemon):
            self.target = target

        def start(self):
            background_starts.append(self.target.__qualname__)

    sense.threading = SimpleNamespace(Thread=ThreadBoundary, Lock=threading.Lock)
    from RNS.vendor import umsgpack as mp
    from RNS.Interfaces.Interface import Interface
    fixture_path = Path(__file__).resolve().parent / "Fixtures/sideband-telemetry.json"
    fixture = json.loads(fixture_path.read_text(encoding="utf-8"))
    context = bytes(range(16))
    cases = []
    inputs = fixture["cases"] + [{"name": "all-sensors", "wire_hex": fixture["all_wire_hex"]}]
    location_wire = next(item["wire_hex"] for item in inputs if item["name"] == "location")
    for index, (latitude, longitude, altitude) in enumerate((
            (51.5, -0.125, 120), (0, 0, 100), (89.999, 179.999, 1000),
            (-90, -180, -100), (35.2, 120.1, 2000))):
        telemeter = sense.Telemeter.from_packed(bytes.fromhex(location_wire))
        sensor = telemeter.sensors["location"]
        sensor.data = dict(sensor.data, latitude=latitude, longitude=longitude, altitude=altitude)
        inputs.append({"name": f"location-geoid-{index}", "wire_hex": telemeter.packed().hex()})
    modes = {name: getattr(Interface, name) for name in (
        "MODE_FULL", "MODE_POINT_TO_POINT", "MODE_ACCESS_POINT", "MODE_ROAMING",
        "MODE_BOUNDARY", "MODE_GATEWAY")}
    transport_wire = next(item["wire_hex"] for item in inputs if item["name"] == "rns_transport")
    for mode_name, mode in modes.items():
        telemeter = sense.Telemeter.from_packed(bytes.fromhex(transport_wire))
        sensor = telemeter.sensors["rns_transport"]
        sensor.sparse = False
        data = dict(sensor.data)
        name = "Synthetic MQTT interface"
        data["ifstats"] = {"interfaces": [{
            "name": name, "short_name": "MQTT", "hash": bytes(range(16)),
            "status": True, "mode": mode, "type": "TCPClientInterface", "bitrate": 120000,
            "rxs": 1.25, "txs": 2.5, "rxb": 1000, "txb": 2000,
            "ifac_signature": bytes(range(32)), "ifac_size": 8, "ifac_netname": None,
            "incoming_announce_frequency": 0.0, "outgoing_announce_frequency": 0.125,
            "held_announces": 0}]}
        data["path_table"] = [{
            "interface": name, "via": bytes(range(16, 32)), "hash": bytes(range(32, 48)),
            "hops": 2, "timestamp": 1699999990, "expires": 1700000010}]
        sensor.data = data
        inputs.append({"name": f"transport-{mode_name.lower()}", "wire_hex": telemeter.packed().hex()})
    for item in inputs:
        owner = object.__new__(primary.MQTT)
        owner.waiting_msgs = deque(maxlen=primary.MQTT.QUEUE_MAXLEN)
        owner.waiting_telemetry = set()
        owner.client = primary.mqtt.Client(primary.mqtt.CallbackAPIVersion.VERSION2)
        result = {"name": item["name"], "wire_hex": item["wire_hex"], "messages": []}
        try:
            owner.handle(context, bytes.fromhex(item["wire_hex"]))
            while owner.waiting_msgs:
                topic, data = owner.waiting_msgs.pop()
                try:
                    owner.post_message(topic, data)
                    outgoing = next(reversed(owner.client._out_messages.values()))
                    result["messages"].append({
                        "topic": topic, "payload_hex": bytes(outgoing.payload).hex(),
                        "qos": outgoing.qos, "retain": outgoing.retain})
                except Exception as error:
                    result["messages"].append({"topic": topic, "error": type(error).__name__})
        except Exception as error:
            result["error"] = type(error).__name__
        cases.append(result)
    ports = []
    for value in (None, "", "invalid", "0", 0, "1884", -1, 65536):
        owner = object.__new__(primary.MQTT)
        owner.set_server("example.invalid", value)
        ports.append({"input": value, "host": owner.host, "port": owner.port})
    scalars = []
    scalar_values = [None, True, False, "", "Synthetic \u2603", 0, -1, 2**64 - 1,
                     0.0, -0.0, 0.0001, 0.00001, 1e15, 1e16, 1e20,
                     1.2345678901234567, b"\x00\xff"]
    for index in range(2048):
        digest = hashlib.sha256(f"mqtt-scalar-{index}".encode("ascii")).digest()
        value = struct.unpack(">d", digest[:8])[0]
        if math.isfinite(value):
            scalar_values.append(value)
    for value in scalar_values:
        encoded = primary.mqtt._encode_payload(value)
        scalars.append({"type": type(value).__name__, "value_repr": repr(value),
                        "value_hex": mp.packb(value).hex(),
                        "payload_hex": bytes(encoded).hex()})
    geoid = source / "sbapp/assets/geoids/egm2008-5.pgm"
    document = {
        "revision": PIN, "source_sha256": hashes,
        "input_fixture_sha256": hashlib.sha256(fixture_path.read_bytes().replace(b"\r\n", b"\n")).hexdigest(),
        "reference_geoid_sha256": hashlib.sha256(geoid.read_bytes()).hexdigest(),
        "method": "Actual MQTT.handle/post_message and actual bundled-paho publish, independent disconnected outgoing packet observations (not process_queue success or error-drain semantics); actual sensor render_mqtt/Location.render with unchanged primary EGM2008-5 data. Constructor scheduler bypassed and sensor thread starts observed but not run; no sockets, live broker, sensor polling, hardware or GUI. Actual sense clock fixed. Public synthetic input fixture executed by GenerateTelemetryVectors.py. No reference implementation or geoid data distributed.",
        "suppressed_sensor_thread_starts": background_starts,
        "context_hex": context.hex(), "queue_maxlen": primary.MQTT.QUEUE_MAXLEN,
        "interface_modes": modes,
        "scheduler_sleep": primary.MQTT.SCHEDULER_SLEEP, "ports": ports,
        "scalars": scalars, "cases": cases,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes((json.dumps(document, indent=2, ensure_ascii=True) + "\n").encode("utf-8"))


if __name__ == "__main__":
    main()
