"""Execute primary passive discovery listing and map marker projection with isolated cache/UI seams."""
import argparse
import ast
import hashlib
import inspect
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
UTC = 1700000000
HASHES = {
    "sbapp/main.py": "5b962d9ed2ccbc066bb19865d78c77da9ecf4008900f6f0e7e9dd1eb1c4767aa",
    "sbapp/ui/map.py": "cfd65489d5f94ddac3990ebd50c6b346e3cd6d0e0c3bfc1e30880ca983e5bf11",
    "sbapp/sideband/core.py": "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25",
}


def portable(value):
    if isinstance(value, bytes):
        return value.hex()
    if isinstance(value, dict):
        return {key.hex() if isinstance(key, bytes) else str(key): portable(item)
                for key, item in value.items()}
    if isinstance(value, (tuple, list)):
        return [portable(item) for item in value]
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference, PINNED_RNS_SHA
    verify_reference()
    import RNS
    import RNS.Discovery as discovery
    from RNS.vendor import umsgpack

    root = args.source.resolve(strict=True)

    def git(*arguments):
        return subprocess.check_output(["git", "-C", str(root), *arguments])

    if git("rev-parse", "HEAD").decode().strip() != PIN:
        raise RuntimeError("Unexpected primary revision")
    if git("status", "--porcelain", "--untracked-files=no", "--", "sbapp").strip():
        raise RuntimeError("Modified primary source")
    if git("ls-files", "--others", "--exclude-standard", "--", "sbapp").strip():
        raise RuntimeError("Untracked primary source")
    namespace = {"RNS": RNS, "time": SimpleNamespace(time=lambda: UTC),
                 "TelemetryClusterMarker": object}

    def load(relative, methods):
        blob = git("show", f"{PIN}:{relative}")
        if hashlib.sha256(blob).hexdigest() != HASHES[relative]:
            raise RuntimeError(f"Unexpected primary blob {relative}")
        if (root / relative).read_bytes().replace(b"\r\n", b"\n") != blob:
            raise RuntimeError(f"Modified working source {relative}")
        tree = ast.parse(blob, filename=str(root / relative))
        functions = [node for node in ast.walk(tree)
                     if isinstance(node, ast.FunctionDef) and node.name in methods]
        if {node.name for node in functions} != set(methods):
            raise RuntimeError("Missing or ambiguous primary methods")
        for node in functions:
            exec(compile(ast.fix_missing_locations(ast.Module(body=[node], type_ignores=[])),
                         str(root / relative), "exec"), namespace)
            if Path(namespace[node.name].__code__.co_filename).resolve() != root / relative:
                raise RuntimeError("Unexpected executed method path")

    load("sbapp/main.py", {"list_discovered_interfaces"})
    load("sbapp/ui/map.py", {"create_clustered_marker", "update_clustered_markers"})
    load("sbapp/sideband/core.py", {"iface_icon", "iface_appearance"})
    discovery_blob = Path(inspect.getfile(discovery)).read_bytes().replace(b"\r\n", b"\n")
    discovery_hash = hashlib.sha256(discovery_blob).hexdigest()
    if discovery_hash != "3c7833ccd1a682c7313208874e1a66ff9495280d01827c67b0405d7402d4a89d":
        raise RuntimeError("Unexpected pinned discovery source")

    class Core:
        config = {"map_history_limit": 604800, "map_connection_maps": False,
                  "map_interfaces": True, "map_hide_blocked": False,
                  "telemetry_display_trusted_only": False, "telemetry_icon": "account",
                  "telemetry_fg": [0, 0, 0, 1], "telemetry_bg": [1, 1, 1, 1]}
        lxmf_destination = SimpleNamespace(hash=bytes.fromhex("11" * 16))
        iface_icon = namespace["iface_icon"]
        iface_appearance = namespace["iface_appearance"]

        def list_telemetry(self, **kwargs):
            return {}

        def get_telemetry(self):
            return None

    class Layer:
        def add_marker(self, longitude, latitude, cls=None, options=None):
            return SimpleNamespace(lat=latitude, lon=longitude, **options)

        def remove_marker(self, marker):
            pass

        def reposition(self, rebuild=False):
            pass

    class Map:
        IF_UPDATE_INTERVAL = 60
        create_clustered_marker = namespace["create_clustered_marker"]
        update_clustered_markers = namespace["update_clustered_markers"]

        def __init__(self, app):
            self.app = app
            self.last_interfaces_update = 0
            self.clustered_layer = Layer()
            self.clustered_markers = {}
            self.interface_discovery_hashes = {}
            self.peer_interface_discovery_hashes = {}
            self.connection_map_hashes = {}
            self.connection_map_only_blocked = False
            self.map = SimpleNamespace(trigger_update=lambda *args: None)

    originals = (RNS.Reticulum.get_instance, getattr(RNS.Reticulum, "storagepath", None),
                 RNS.Reticulum.interface_discovery_sources, discovery.time)
    try:
        RNS.Reticulum.get_instance = staticmethod(
            lambda: SimpleNamespace(get_blackholed_identities=lambda: {}))
        RNS.Reticulum.interface_discovery_sources = staticmethod(lambda: [])
        discovery.time = SimpleNamespace(time=lambda: UTC)
        with tempfile.TemporaryDirectory(prefix="sideband-discovery-reference-") as directory:
            RNS.Reticulum.storagepath = directory
            app = SimpleNamespace(if_discovery=None, sideband=Core(), own_appearance_changed=False)
            app.list_discovered_interfaces = lambda: namespace["list_discovered_interfaces"](app)
            first = app.list_discovered_interfaces()
            if first != [] or app.if_discovery is None or hasattr(app.if_discovery, "handler"):
                raise RuntimeError("Passive primary discovery unexpectedly started an announce listener")
            retained = app.if_discovery
            rows = []
            for index, (name, transport, latitude, longitude, age) in enumerate((
                ("recent", True, 51.5, -0.1, 0),
                ("zero-latitude", True, 0.0, -0.1, 0),
                ("zero-longitude", True, 51.5, 0.0, 0),
                ("not-transport", False, 52.0, -0.1, 0),
                ("no-location", True, None, None, 0),
                ("unknown", True, 53.0, -0.1, 86401),
                ("stale", True, 54.0, -0.1, 259201),
                ("expired", True, 55.0, -0.1, 604801),
            ), 1):
                row = {"type": "TCPServerInterface", "transport": transport,
                       "name": name, "discovery_hash": bytes([index]) * 32,
                       "network_id": "22" * 16, "transport_id": "33" * 16,
                       "latitude": latitude, "longitude": longitude,
                       "height": None, "received": UTC - age, "last_heard": UTC - age,
                       "discovered": UTC - age, "heard_count": 0, "value": 16 + index,
                       "reachable_on": "reference.example", "port": 4242,
                       "ifac_netkey": "synthetic-secret-not-for-display"}
                rows.append(row)
                (Path(retained.storagepath) / row["discovery_hash"].hex()).write_bytes(umsgpack.packb(row))
            listed = app.list_discovered_interfaces()
            if retained is not app.if_discovery:
                raise RuntimeError("Primary did not reuse its passive discovery instance")
            instance = Map(app)
            instance.update_clustered_markers()
            markers = [{"key": key, "latitude": marker.lat, "longitude": marker.lon,
                        "appearance": [marker.icon, marker.icon_color, marker.md_bg_color]}
                       for key, marker in sorted(instance.clustered_markers.items())]
            remaining = sorted(path.name for path in Path(retained.storagepath).iterdir())
            listed_public = [{key: value for key, value in row.items()
                              if key != "ifac_netkey"} for row in listed]
    finally:
        RNS.Reticulum.get_instance, RNS.Reticulum.storagepath = originals[:2]
        RNS.Reticulum.interface_discovery_sources, discovery.time = originals[2:]
    result = {"revision": PIN, "source_sha256": HASHES, "rns_revision": PINNED_RNS_SHA,
              "discovery_source_sha256": discovery_hash,
              "method": "Actual AST-selected primary facade/map/appearance methods and real passive RNS discovery constructor/list; isolated metadata-cache inputs with distinct ordering values, fixed clocks, synthetic active-owner blackhole/source lists and inert UI layer; no network, stamp-validation or primary GUI claim.",
              "initially_empty": first == [], "passive_instance_reused": True,
              "listed": listed_public, "cache_files_after_listing": remaining,
              "primary_markers": markers,
              "native_safety_differences": ["Zero coordinates accepted", "Complete refresh removes disappeared or moved markers", "Credentials excluded from native map DTO"]}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes((json.dumps(portable(result), indent=2, ensure_ascii=True) + "\n").encode())


if __name__ == "__main__":
    main()
