"""Execute pinned Sideband sensors without devices; no telemetry implementation lives here."""
import argparse
import ast
import hashlib
import importlib
import json
import subprocess
import sys
import threading
from pathlib import Path
from types import SimpleNamespace

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
SENSE_HASH = "80418b1aa4de638f2c65f1bd64e5ea0466378a6f0bc682781672ef89b93a7dab"
CORE_HASH = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"
UTC = 1700000000


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--verify-csharp", type=Path)
    args = parser.parse_args()
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference
    verify_reference(require_lxmf=True)
    source = args.source.resolve()
    revision = subprocess.check_output(
        ["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != PIN:
        raise RuntimeError(f"Expected {PIN}, got {revision}")
    hashes = {}
    sources = {}
    for name, digest in [("sense", SENSE_HASH), ("core", CORE_HASH)]:
        blob = subprocess.check_output(
            ["git", "-C", str(source), "show", f"{PIN}:sbapp/sideband/{name}.py"])
        if hashlib.sha256(blob).hexdigest() != digest:
            raise RuntimeError(f"Unexpected {name}.py Git blob")
        # The imported working copy must match, allowing only checkout newline conversion.
        actual = (source / "sbapp" / "sideband" / f"{name}.py").read_bytes()
        if actual.replace(b"\r\n", b"\n") != blob.replace(b"\r\n", b"\n"):
            raise RuntimeError(f"Modified working source {name}.py")
        hashes[name] = digest
        sources[name] = blob
    sys.path.insert(0, str(source / "sbapp"))
    sense = importlib.import_module("sideband.sense")
    if Path(sense.__file__).resolve() != source / "sbapp" / "sideband" / "sense.py":
        raise RuntimeError("Unexpected sense import")
    from RNS.vendor import umsgpack as mp
    import RNS
    import RNS.Discovery as discovery
    import LXMF
    # Only replace the module's clock dependency, not Python's shared time module.
    sense.time = SimpleNamespace(time=lambda: UTC)
    if args.verify_csharp:
        cases = json.loads(args.verify_csharp.read_text(encoding="utf-8"))
        for case in cases:
            decoded = sense.Telemeter.from_packed(bytes.fromhex(case["wire_hex"]))
            if decoded is None:
                raise RuntimeError(f"Upstream rejected {case['name']}")
            actual = decoded.read_all()
            expected = mp.unpackb(bytes.fromhex(case["expected_readings_hex"]))
            if actual != expected:
                raise RuntimeError(f"Upstream mismatch for {case['name']}: {actual!r} != {expected!r}")
        print(f"Actual pinned Telemeter accepted {len(cases)} C#-encoded reports")
        return
    if not args.output:
        parser.error("--output or --verify-csharp required")
    t = sense.Telemeter(from_packed=True)
    synthetic = {
        "time": {"utc": UTC}, "information": {"contents": "Synthetic desktop telemetry \u2603"},
        "received": {"by": bytes(range(16)), "via": bytes(range(16, 32)),
                     "distance": {"geodesic": 1250.5, "euclidian": 1249.25}},
        "battery": {"charge_percent": 72.5, "charging": True, "temperature": 31.25},
        "pressure": {"mbar": 1013.25},
        "location": {"latitude": 51.5, "longitude": -0.125, "altitude": -12.5,
                     "speed": 36.0, "bearing": 270.0, "accuracy": 3.25, "last_update": UTC - 7},
        "physical_link": {"rssi": -87.5, "snr": 7.25, "q": 91.0},
        "temperature": {"c": -4.25}, "humidity": {"percent_relative": 67.5},
        "ambient_light": {"lux": 120.5}, "proximity": True,
        "rns_transport": {
            "transport_enabled": True, "transport_identity": bytes(range(16)),
            "transport_uptime": 3600.5, "traffic_rxb": 1000, "traffic_txb": 2000,
            "speed_rx": 80.0, "speed_tx": 160.0, "speed_rx_inst": 88.0,
            "speed_tx_inst": 168.0, "memory_used": 4096,
            "ifstats": {"interfaces": [{"name": "Synthetic interface", "rxb": 1000}]},
            "path_table": [{"interface": "Synthetic interface", "via": bytes(range(16)),
                            "hash": bytes(range(16, 32)), "hops": 2}],
            "interface_count": 1, "link_count": 2, "path_count": 1},
        "lxmf_propagation": {
            "destination_hash": bytes(range(16)), "identity_hash": bytes(range(16, 32)),
            "uptime": 7200, "delivery_limit": 1000.0, "propagation_limit": 2000.0,
            "autopeer_maxdepth": 4, "from_static_only": False,
            "messagestore": {"count": 3, "bytes": 1000, "limit": 10000},
            "clients": {"client_propagation_messages_received": 7, "client_propagation_messages_served": 5},
            "unpeered_propagation_incoming": 2, "unpeered_propagation_rx_bytes": 100,
            "static_peers": 1, "total_peers": 1, "max_peers": 8,
            "peers": {bytes(range(16)): {
                "type": "static", "state": 1, "alive": True, "last_heard": UTC - 1,
                "next_sync_attempt": UTC + 10, "last_sync_attempt": UTC - 10,
                "sync_backoff": 0, "peering_timebase": UTC - 100, "ler": 0.1, "str": 2.0,
                "transfer_limit": 1000.0, "network_distance": 2, "rx_bytes": 500, "tx_bytes": 400,
                "messages": {"offered": 2, "outgoing": 1, "incoming": 1, "unhandled": 0}}}},
    }
    for name in ("magnetic_field", "gravity", "angular_velocity", "acceleration"):
        synthetic[name] = {"x": 1.25, "y": -2.5, "z": 3.75}
    for name in t.available:
        t.synthesize(name)
        sensor = t.sensors[name]
        if name in synthetic:
            sensor.data = synthetic[name]
        if name == "received":
            sensor.geodesic_distance = 1250.5
            sensor.euclidian_distance = 1249.25
        elif name == "power_consumption":
            sensor.update_consumer(12.5, custom_icon="synthetic")
            sensor.update_consumer(3.0, "Radio")
        elif name == "power_production":
            sensor.update_producer(45.0, custom_icon="synthetic")
        elif name == "processor":
            sensor.update_entry(0.25, [0.1, 0.2, 0.3], 2400000000)
        elif name in ("ram", "nvm"):
            sensor.update_entry(8192, 2048)
            sensor.update_entry(1024, 1024, "Secondary")
        elif name == "custom":
            sensor.update_entry({"mode": "synthetic", "count": 2}, custom_icon="ruler")
        elif name in ("tank", "fuel"):
            sensor.update_entry(100.0, 25.0)
            sensor.update_entry(50.0, 10.0, "kg", "Reserve", "synthetic")
        elif name == "connection_map":
            sensor.add_point(51.5, -0.125, 10.0, "node", "Synthetic", signal_rssi=-80)
            sensor.update_data()
        elif name == "interfaces":
            sensor.add_interface(name="Synthetic TCP", type="TCPServerInterface",
                                 reachable_on="example.invalid", port=4242, lat=0.0, lon=0.0,
                                 transport=True, transport_id=bytes(range(16)).hex(),
                                 network_id=bytes(range(16, 32)).hex())
            for interface_type in discovery.InterfaceAnnouncer.DISCOVERABLE_INTERFACE_TYPES:
                sensor.add_interface(
                    name=f"Synthetic {interface_type}", type=interface_type,
                    reachable_on=("a" * 52 + ".b32.i2p") if interface_type == "I2PInterface" else "example.invalid",
                    port=4242, lat=51.5, lon=-0.125, height=10.5,
                    frequency=868000000, bandwidth=125000, modulation="LoRa",
                    spreadingfactor=7, codingrate=5, channel=1,
                    ifac_netname="public-synthetic-network", ifac_netkey="public-synthetic-key",
                    transport=False, transport_id=bytes(range(16)).hex())
            sensor.update_data()
    cases = []

    def add(name, sid, packed, method):
        wire = mp.packb({1: UTC, sid: packed})
        decoded = sense.Telemeter.from_packed(wire)
        if decoded is None:
            raise RuntimeError(f"Actual Telemeter rejected synthetic {name}")
        cases.append({
            "name": name, "sid": sid, "method": method,
            "wire_hex": wire.hex(), "packed_value_hex": mp.packb(packed).hex(),
            "unpacked_value_hex": mp.packb(decoded.sensors[t.names[sid]]._data).hex(),
            "expected_readings_hex": mp.packb(decoded.read_all()).hex()})

    for name, sid in t.available.items():
        add(name, sid, t.sensors[name].pack(), "actual synthesized sensor.pack and Telemeter.from_packed/read_all")
        add(f"{name}-nil", sid, None, "actual sensor.unpack(None) through Telemeter.from_packed")
    add("battery-legacy-two", 4, [50.0, False], "actual Battery.unpack legacy two-element shape")
    add("physical-link-partial", 5, [None, 0.0, None], "actual PhysicalLink.unpack nullable metrics")
    for percent in (72.55, -0.05, 2.675, 100.0):
        t.sensors["battery"].data = {"charge_percent": percent, "charging": False, "temperature": None}
        add(f"battery-rounding-{percent}", 4, t.sensors["battery"].pack(), "actual Battery.pack binary-float decimal rounding")
    location_input = dict(synthetic["location"], latitude=-1.2345675, longitude=12.3456785,
                          altitude=2.675, bearing=72.555, accuracy=1.255)
    t.sensors["location"].data = location_input
    add("location-rounding", 2, t.sensors["location"].pack(), "actual Location.pack binary-float round/multiply/truncate")
    t.sensors["rns_transport"].sparse = False
    add("rns-transport-detailed", 25, t.sensors["rns_transport"].pack(), "actual RNSTransport.pack sparse=False")
    # These are observations of upstream oddities, not desired behavior implemented by the adapter.
    received = sense.Telemeter.from_packed(mp.packb({16: t.sensors["received"].pack()})).sensors["received"]
    observations = {
        "received_repack_hex": mp.packb(received.pack()).hex(),
        "received_initial_unpack_hex": mp.packb(received._data).hex(),
        "interfaces_empty_unpack_hex": mp.packb(sense.Interfaces().unpack({})).hex(),
        "interfaces_nil_unpack_hex": mp.packb(sense.Interfaces().unpack(None)).hex(),
        "location_rounding_input_hex": mp.packb(location_input).hex(),
        "battery_rounding_inputs": [72.55, -0.05, 2.675, 100.0],
        "malformed_sensor_unpack": [],
    }
    for name in t.available:
        try:
            result = t.sensors[name].unpack(b"\x01")
            observation = {"name": name, "returned_hex": mp.packb(result).hex()}
        except Exception as error:
            observation = {"name": name, "exception": type(error).__name__}
        observations["malformed_sensor_unpack"].append(observation)
    zero = sense.RandomAccessMemory()
    zero.synthesized = True
    zero.update_entry(0, 0)
    try:
        zero.render()
        observations["zero_capacity_ram_render"] = "returned"
    except Exception as error:
        observations["zero_capacity_ram_render"] = type(error).__name__
    try:
        sense.Time().unpackb(mp.packb(UTC))
        observations["base_sensor_unpackb"] = "returned"
    except Exception as error:
        observations["base_sensor_unpackb"] = type(error).__name__
    for label, kwargs in [("longitude_above_180", {"lat": 0.0, "lon": 181.0}),
                          ("longitude_without_latitude", {"lon": 1.0})]:
        interfaces = sense.Interfaces()
        try:
            interfaces.add_interface(name="Synthetic validation", type="TCPClientInterface",
                                     transport_id=bytes(range(16)).hex(), **kwargs)
            observations[label] = "accepted"
        except Exception as error:
            observations[label] = type(error).__name__
    connection = sense.ConnectionMap()
    connection.synthesized = True
    connection.add_point(0.0, 0.0, name="boundary")
    connection.add_point(1.0, 1.0, name="expired")
    for point in connection.maps[0]["points"].values():
        point["added"] = UTC - connection.POINT_STALE_TIME - (1 if point["name"] == "expired" else 0)
    connection.update_data()
    add("connection-map-expiration", 26, connection.pack(), "actual ConnectionMap.pack strict point expiry boundary")
    t.sensors["battery"].data = synthetic["battery"]
    t.sensors["location"].data = synthetic["location"]
    all_wire = t.packed()
    core = next(n for n in ast.parse(sources["core"]).body if isinstance(n, ast.ClassDef) and n.name == "SidebandCore")
    methods = [n for n in core.body if isinstance(n, ast.FunctionDef) and n.name in ("handle_commands", "_db_save_telemetry")]
    namespace = {"RNS": RNS, "Commands": sense.Commands, "Telemeter": sense.Telemeter,
                 "valid_telemetry_timebase": sense.valid_telemetry_timebase,
                 "time": sense.time, "threading": threading}
    exec(compile(ast.Module(body=methods, type_ignores=[]), "core.py", "exec"), namespace)
    callbacks = []

    def observe(name):
        return lambda *args, **kwargs: callbacks.append({"method": name, "args_hex": mp.packb(list(args)).hex(),
                                                       "kwargs_hex": mp.packb(kwargs).hex()})

    owner = SimpleNamespace(config={"telemetry_collector_enabled": True, "command_plugins_enabled": True},
                            create_telemetry_collector_response=observe("collector"),
                            send_latest_telemetry=observe("latest"), send_message=observe("send_message"),
                            handle_plugin_command=observe("plugin"))
    command_inputs = [{1: [UTC - 10, False]}, {1: UTC - 30}, {2: True},
                      {3: b"synthetic echo"}, {4: True}, {0: "synthetic argument"}]
    message = SimpleNamespace(source_hash=bytes(range(16)), q=91, rssi=-87.5, snr=7.25)
    # Plugin dispatch is observed separately because its callback includes the synthetic message object.
    owner.handle_plugin_command = lambda text, _: callbacks.append({"method": "plugin", "text": text})
    namespace["handle_commands"](owner, command_inputs, message)
    if len(callbacks) != len(command_inputs):
        raise RuntimeError("Not all actual command paths invoked their observed callback")
    receipt_observations = []

    class Cursor:
        def execute(self, *_):
            pass

        def fetchall(self):
            return []

    owner = SimpleNamespace(
        db_lock=threading.Lock(), __db_connect=lambda: SimpleNamespace(cursor=Cursor, commit=lambda: None),
        lxmf_destination=SimpleNamespace(hash=bytes(range(32, 48))),
        latest_telemetry=None, config={"telemetry_to_mqtt": False}, setstate=lambda *_: None)
    for name, kwargs in [
        ("direct-source-and-link", {"physical_link": {"rssi": -40.0, "snr": None, "q": 0.0},
                                    "source_dest": bytes(range(48, 64))}),
        ("forwarded-via", {"via": bytes(range(64, 80))}),
    ]:
        output = namespace["_db_save_telemetry"](owner, bytes(range(16)), all_wire, **kwargs)
        if output is None:
            raise RuntimeError("Actual receipt annotation returned no telemetry")
        receipt_observations.append({"name": name, "wire_hex": output.hex(),
                                     "readings_hex": mp.packb(sense.Telemeter.from_packed(output).read_all()).hex()})
    shapes = {
        "time": "numeric Unix UTC seconds",
        "information": "text",
        "received": "[by hash or nil, via hash or nil, geodesic distance or nil, euclidian distance or nil]",
        "battery": "[charge_percent rounded to 1 decimal, charging, temperature]; decoder also accepts legacy first two elements",
        "pressure": "numeric mbar", "location": "[binary i32 BE latitude*1e6, binary i32 BE longitude*1e6, binary i32 BE altitude*100, binary u32 BE speed km/h*100, binary i32 BE bearing*100, binary u16 BE accuracy*100, last_update Unix seconds]; values rounded before multiply then truncated",
        "physical_link": "[rssi dBm or nil, snr dB or nil, q percent or nil]",
        "temperature": "numeric c", "humidity": "numeric percent_relative", "ambient_light": "numeric lux",
        "proximity": "boolean triggered",
        "magnetic_field": "[x,y,z]", "gravity": "[x,y,z]", "angular_velocity": "[x,y,z]", "acceleration": "[x,y,z]",
        "power_consumption": "[[label text or 0, [watts, custom_icon]], ...]",
        "power_production": "[[label text or 0, [watts, custom_icon]], ...]",
        "processor": "[[label text or 0, [current_load, load_avgs [1m,5m,15m] or nil, clock]], ...]",
        "ram": "[[label text or 0, [capacity, used]], ...]",
        "nvm": "[[label text or 0, [capacity, used]], ...]",
        "custom": "[[label text or 0, [arbitrary safe value, custom_icon]], ...]",
        "tank": "[[label text or 0, [capacity, level, unit or nil (presentation default L), custom_icon]], ...]",
        "fuel": "[[label text or 0, [capacity, level, unit or nil (presentation default L), custom_icon]], ...]",
        "rns_transport": "self-describing map; sparse pack replaces ifstats/path_table with nil; other named counters unchanged",
        "lxmf_propagation": "self-describing lxmd status map; delivery_limit and propagation_limit are decimal kB; messagestore/clients/peers nested; peer keys binary hashes",
        "connection_map": "{maps: {map key: {name: map key, points: {binary point hash: {latitude,longitude,altitude,type_label,name,signal:{rssi,snr,q},added}}}}}; pack expires points only when now > added+259200",
        "interfaces": "{interface name: {integer Discovery field ID: value}}; pack omits false/zero/empty/nil fields; transport/network IDs binary; unpack exposes hex strings; empty/nil unpack returns {}",
    }
    sense_ast = ast.parse(sources["sense"])
    contracts = []
    for name, sid in t.available.items():
        class_name = type(t.sensors[name]).__name__
        node = next(n for n in sense_ast.body if isinstance(n, ast.ClassDef) and n.name == class_name)
        contracts.append({"sid": sid, "name": name, "class": class_name,
                          "source": f"sbapp/sideband/sense.py:{node.lineno}",
                          "wire": shapes[name], "nil": "unavailable; Interfaces uniquely unpacks nil as {}",
                          "evidence": "actual pack/unpack synthetic vectors; no hardware or live-node claim"})
    args.output.write_text(json.dumps({
        "revision": PIN, "source_sha256": hashes, "rns_revision": "a29bfb51c468945c96a1ee22cae0114e993e2391",
        "lxmf_revision": "e8ba02483217af43be284a72f1c059c730b7d047",
        "imported": {"sense": str(Path(sense.__file__).resolve()), "rns": str(Path(RNS.__file__).resolve()),
                     "lxmf": str(Path(LXMF.__file__).resolve())},
        "dependencies": "Actual sense source import; synthetic sensor data and actual update_entry/add_point/add_interface APIs; sense.time replaced by fixed clock. Core observations execute AST-extracted actual handle_commands/_db_save_telemetry methods with injected globals/callbacks/database sink, not full SidebandCore initialization. No hardware/start/setup/thread/GUI/geoid/native codec opened.",
        "commands": {n: getattr(sense.Commands, n) for n in
                     ("PLUGIN_COMMAND", "TELEMETRY_REQUEST", "PING", "ECHO", "SIGNAL_REPORT")},
        "lxmf_fields": {n: getattr(LXMF, n) for n in ("FIELD_TELEMETRY", "FIELD_TELEMETRY_STREAM", "FIELD_COMMANDS")},
        "interface_keys": {n: getattr(discovery, n) for n in
                           ("INTERFACE_TYPE", "TRANSPORT", "TRANSPORT_ID", "REACHABLE_ON",
                            "PORT", "FREQUENCY", "BANDWIDTH", "MODULATION", "SPREADINGFACTOR",
                            "CODINGRATE", "CHANNEL", "LATITUDE", "LONGITUDE", "HEIGHT", "IFAC_NETNAME", "IFAC_NETKEY")},
        "timebase_cases": [{"value": v, "valid": sense.valid_telemetry_timebase(v)}
                           for v in (None, True, UTC, UTC + 240, UTC + 241, -1, 1.5)],
        "observations": observations,
        "contracts": contracts,
        "command_observations": {"wire_hex": mp.packb(command_inputs).hex(), "callbacks": callbacks,
                                 "method": "AST-extracted actual core.handle_commands; injected globals and response/dispatch observers, no command service or plugin executed"},
        "receipt_observations": receipt_observations,
        "safety_differences": [
            "Native parsing rejects nonfinite values, extensions, nil/aggregate map keys, duplicate keys/entry labels, invalid UTF, non-byte-integer sensor IDs and resource-limit violations.",
            "Native known sensors validate shape/types/hash lengths and use exact array lengths, except legacy Battery accepts 2 or 3. Python frequently accepts arbitrary scalar types, trailing list elements, duplicate labels, or returns nil on sensor errors.",
            "Unknown safe IDs are retained by native code with Unknown status, not rendered as known sensors; Python Telemeter discards them.",
            "Native serialization is explicit-time/stateless and preserves safe wire sensor values; Python Telemeter.packed inserts current time when time is absent, and Received.pack after unpack loses distance attributes.",
            "Native zero-capacity percentages and nonfinite derived presentation results are nil instead of raising or exposing infinity. Transmitted source data stays available.",
            "Native typed location exposes transmitted altitude/speed/bearing/accuracy and last_update, not geoid/AAMSL/horizon/distance map calculations."
        ],
        "integration_exclusions": [
            "No app state, store, UI, maps, sensor hardware, clock scheduling, telemetry transport service, permissions/trust policy, plugin execution or remote command service is implemented by these contracts.",
            "LXMF FIELD_TELEMETRY is packed Telemeter bytes; FIELD_TELEMETRY_STREAM entries are [source hash, timestamp, packed Telemeter bytes, appearance]. Stream admission/storage and provenance updates remain application integration.",
            "Actual core command handling requires caller signature validation and allow_request_from policy; observers here execute only already-admitted handle_commands.",
            "Receipt observations execute actual _db_save_telemetry with an empty synthetic cursor/commit sink, lock, fixed clock and no local location; no SQLite persistence, geo calculation or MQTT service is verified."
        ],
        "all_wire_hex": all_wire.hex(),
        "all_readings_hex": mp.packb(sense.Telemeter.from_packed(all_wire).read_all()).hex(),
        "cases": cases}, indent=2) + "\n", encoding="utf-8")
    print(f"Generated {len(cases)} actual sensor cases; {len(t.available)} sensor IDs; {args.output}")


if __name__ == "__main__":
    main()
