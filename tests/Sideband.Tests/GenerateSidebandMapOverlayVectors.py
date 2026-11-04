"""Observe pinned primary map methods and sensors; no map algorithm is implemented here."""
import argparse
import ast
import hashlib
import importlib
import inspect
import json
import math
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
UTC = 1700000000
SOURCES = {
    "sbapp/ui/map.py": "cfd65489d5f94ddac3990ebd50c6b346e3cd6d0e0c3bfc1e30880ca983e5bf11",
    "sbapp/mapview/clustered_marker_layer.py": "783ef113242283d7906e00375e0f5dd1b9e503aad0e0a0cd62912cdb0ec2cca7",
    "sbapp/mapview/view.py": "a38470966bec4f9301cffaecaab448319e901ba439769d1a546f6c9903d249f3",
    "sbapp/mapview/source.py": "d3162e1acd36c02617842f93e2e7b67f96a582136819c25903c7d631c7790986",
    "sbapp/mapview/constants.py": "fd354dadb7ef72d1bb4c5bb87cde893c81e7379fd76e05912d8ead27a97308d9",
    "sbapp/mapview/utils.py": "90728387d1e28870ca9632032e37077448195582958c6b6baf7175879e5b1393",
    "sbapp/sideband/core.py": "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25",
    "sbapp/sideband/sense.py": "80418b1aa4de638f2c65f1bd64e5ea0466378a6f0bc682781672ef89b93a7dab",
    "sbapp/sideband/geo.py": "af416ae7a1c087cc0c636e994ff58d5c6f7edf9f85379765d7fda9d130e780c7",
}


def portable(value):
    if isinstance(value, bytes):
        return value.hex()
    if isinstance(value, float):
        if not math.isfinite(value):
            raise ValueError("Nonfinite observation")
        return round(value, 10)
    if isinstance(value, dict):
        return {key.hex() if isinstance(key, bytes) else str(key): portable(item)
                for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [portable(item) for item in value]
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference
    verify_reference()
    import RNS

    root = args.source.resolve(strict=True)

    def git(*arguments):
        return subprocess.check_output(["git", "-C", str(root), *arguments])

    if git("rev-parse", "HEAD").decode().strip() != PIN:
        raise RuntimeError("Unexpected primary revision")
    if git("status", "--porcelain", "--untracked-files=no", "--", "sbapp").strip():
        raise RuntimeError("Modified primary source")
    if git("ls-files", "--others", "--exclude-standard", "--", "sbapp").strip():
        raise RuntimeError("Untracked primary source")
    trees = {}
    for relative, expected in SOURCES.items():
        blob = git("show", f"{PIN}:{relative}")
        if hashlib.sha256(blob).hexdigest() != expected:
            raise RuntimeError(f"Unexpected primary blob {relative}")
        if (root / relative).read_bytes().replace(b"\r\n", b"\n") != blob:
            raise RuntimeError(f"Modified working source {relative}")
        trees[relative] = ast.parse(blob, filename=str(root / relative))

    sys.path.insert(0, str(root / "sbapp"))
    sense = importlib.import_module("sideband.sense")
    geo = importlib.import_module("sideband.geo")
    for module, relative in ((sense, "sbapp/sideband/sense.py"), (geo, "sbapp/sideband/geo.py")):
        if Path(module.__file__).resolve() != root / relative:
            raise RuntimeError(f"Unexpected imported module {module.__name__}")
    if Path(inspect.getfile(sense.Telemeter)).resolve() != root / "sbapp/sideband/sense.py":
        raise RuntimeError("Unexpected imported Telemeter")
    clock = SimpleNamespace(time=lambda: UTC)
    sense.time = clock
    namespace = {**vars(math), "__name__": "actual_primary_map_observations",
                 "time": clock, "RNS": RNS, "Telemeter": sense.Telemeter,
                 "dp": lambda value: value, "MapMarker": object,
                 "TelemetryClusterMarker": object,
                 "id": lambda marker: marker.observation_order}

    def execute(relative, nodes):
        code = compile(ast.fix_missing_locations(ast.Module(body=nodes, type_ignores=[])),
                       str(root / relative), "exec")
        exec(code, namespace)

    def original_class(relative, name):
        return next(node for node in trees[relative].body
                    if isinstance(node, ast.ClassDef) and node.name == name)

    def load_class(relative, name, methods=None):
        original = original_class(relative, name)
        if methods is None:
            selected = original
        else:
            selected = ast.ClassDef(name=name, bases=[], keywords=[], decorator_list=[], body=[
                node for node in original.body
                if isinstance(node, ast.FunctionDef) and node.name in methods])
        execute(relative, [selected])
        result = namespace[name]
        for item in result.__dict__.values():
            if isinstance(item, staticmethod):
                item = item.__func__
            if inspect.isfunction(item) and Path(item.__code__.co_filename).resolve() != root / relative:
                raise RuntimeError(f"Unexpected executed method path {name}")
        return result

    cluster_path = "sbapp/mapview/clustered_marker_layer.py"
    execute(cluster_path, [node for node in trees[cluster_path].body
                           if isinstance(node, ast.FunctionDef)])
    for name in ("KDBush", "Cluster", "Marker", "SuperCluster"):
        load_class(cluster_path, name)
    Map = load_class("sbapp/ui/map.py", "Map", {
        "create_clustered_marker", "update_clustered_markers", "interfaces_action",
        "connection_maps_action", "connection_maps_blocked_action", "show_bbox"})
    Core = load_class("sbapp/sideband/core.py", "SidebandCore", {
        "iface_icon", "iface_appearance", "telemetry_iface_from_ife",
        "config_entry_from_iface", "fixed_location"})
    View = load_class("sbapp/mapview/view.py", "MapView", {"scatter_markers", "zoom_to_bbox"})
    ClusterButton = load_class(cluster_path, "ClusterMapMarker", {"on_release"})
    constants_path = "sbapp/mapview/constants.py"
    execute(constants_path, [node for node in trees[constants_path].body
                             if isinstance(node, ast.Assign)])
    utils_path = "sbapp/mapview/utils.py"
    execute(utils_path, [next(node for node in trees[utils_path].body
                             if isinstance(node, ast.FunctionDef) and node.name == "clamp")])
    Projection = load_class("sbapp/mapview/source.py", "MapSource", {
        "get_x", "get_y", "get_min_zoom", "get_max_zoom"})

    config = SimpleNamespace(config={})
    default_keys = {"map_cluster", "map_interfaces", "map_connection_maps",
                    "map_hide_blocked", "map_history_limit"}
    defaults = []
    for node in ast.walk(trees["sbapp/sideband/core.py"]):
        if not isinstance(node, ast.If) or len(node.body) != 1 or not isinstance(node.body[0], ast.Assign):
            continue
        test = node.test.operand if isinstance(node.test, ast.UnaryOp) else node.test
        if isinstance(test, ast.Compare) and isinstance(test.left, ast.Constant) and test.left.value in default_keys:
            defaults.append(node)
    exec(compile(ast.Module(body=defaults, type_ignores=[]), "actual-primary-map-defaults", "exec"),
         {"self": config})
    if set(config.config) != default_keys:
        raise RuntimeError("Missing primary overlay defaults")
    map_original = original_class("sbapp/ui/map.py", "Map")
    interval = next(node for node in map_original.body if isinstance(node, ast.Assign)
                    and any(isinstance(target, ast.Name) and target.id == "IF_UPDATE_INTERVAL"
                            for target in node.targets))
    Map.IF_UPDATE_INTERVAL = ast.literal_eval(interval.value)
    constructor = next(node for node in map_original.body
                       if isinstance(node, ast.FunctionDef) and node.name == "__init__")
    cluster_constructor = next(node for node in ast.walk(constructor) if isinstance(node, ast.Call)
                               and isinstance(node.func, ast.Name) and node.func.id == "ClusteredMarkerLayer")
    cluster_options = {keyword.arg: ast.literal_eval(keyword.value) for keyword in cluster_constructor.keywords
                       if keyword.arg in {"cluster_radius", "cluster_min_zoom", "cluster_max_zoom"}}
    layer = original_class(cluster_path, "ClusteredMarkerLayer")
    extent = next(node.value.args[0] for node in layer.body if isinstance(node, ast.Assign)
                  and any(isinstance(target, ast.Name) and target.id == "cluster_extent"
                          for target in node.targets))
    cluster_options["cluster_extent"] = ast.literal_eval(extent)
    cluster_library_defaults = {
        name: parameter.default for name, parameter in inspect.signature(
            namespace["SuperCluster"].__init__).parameters.items() if name != "self"}
    cluster_layer_defaults = {
        node.targets[0].id: ast.literal_eval(node.value.args[0]) for node in layer.body
        if isinstance(node, ast.Assign) and isinstance(node.targets[0], ast.Name)
        and node.targets[0].id in {
            "cluster_min_zoom", "cluster_max_zoom", "cluster_radius", "cluster_extent", "cluster_node_size"}}

    own, peer = bytes.fromhex("11" * 16), bytes.fromhex("22" * 16)

    def telemetry(timestamp, location=None, interfaces=(), points=()):
        meter = sense.Telemeter()
        meter.sensors["time"].data = {"utc": timestamp}
        if location is not None:
            meter.synthesize("location")
            meter.sensors["location"].data = {
                "latitude": location[0], "longitude": location[1], "altitude": 0,
                "speed": 0, "bearing": 0, "accuracy": 1, "last_update": timestamp}
        if interfaces:
            meter.synthesize("interfaces")
            for name, latitude, longitude in interfaces:
                meter.sensors["interfaces"].add_interface(
                    name=name, type="TCPServerInterface", reachable_on="127.0.0.1",
                    port=4242, lat=latitude, lon=longitude, height=0,
                    transport=True, transport_id="33" * 16)
            meter.sensors["interfaces"].update_data()
        if points:
            meter.synthesize("connection_map")
            for point in points:
                meter.sensors["connection_map"].add_point(**point)
            meter.sensors["connection_map"].update_data()
        return meter

    input_points = [
        {"lat": 48.5, "lon": 2.5, "altitude": 0, "type_label": "connection", "name": "ordinary"},
        {"lat": 48.75, "lon": 2.5, "altitude": 0, "type_label": "blocked", "name": "blocked"},
        {"lat": 49.0, "lon": 2.5, "altitude": 0, "type_label": "connection", "name": "strong", "signal_q": 81},
        {"lat": 0.0, "lon": 0.0, "altitude": 0, "type_label": "connection", "name": "zero"}]
    meters = {
        "newest_without_location": telemetry(UTC - 5, interfaces=(("Synthetic TCP", 48.25, 2.25),),
                                               points=input_points),
        "older_with_location": telemetry(UTC - 10, location=(48.1, 2.1)),
        "own": telemetry(UTC - 4, location=(48.0, 2.0),
                         interfaces=(("Synthetic TCP", 48.25, 2.25),))}
    packed_reports = {key: meter.packed() for key, meter in meters.items()}
    report_rows = {peer: [[UTC - 5, packed_reports["newest_without_location"]],
                         [UTC - 10, packed_reports["older_with_location"]]]}
    discovered = [
        {"discovery_hash": bytes.fromhex("44" * 32), "transport": True,
         "type": "TCPServerInterface", "latitude": 48.3, "longitude": 2.3},
        {"discovery_hash": bytes.fromhex("55" * 32), "transport": True,
         "type": "TCPServerInterface", "latitude": 0.0, "longitude": 2.3},
        {"discovery_hash": bytes.fromhex("66" * 32), "transport": False,
         "type": "TCPServerInterface", "latitude": 48.6, "longitude": 2.3}]

    class ObservedLayer:
        def __init__(self):
            self.markers = []
            self.rebuilds = 0

        def add_marker(self, longitude, latitude, cls=None, options=None):
            marker = namespace["Marker"](longitude, latitude, options=options)
            self.markers.append(marker)
            return marker

        def remove_marker(self, marker):
            self.markers.remove(marker)

        def reposition(self, rebuild=False):
            self.rebuilds += int(rebuild)

    def new_map(overrides=None, trusted=True, rows=None, own_readings=None, fixed=None):
        core = Core()
        core.config = {**config.config, "map_connection_maps": True,
                       "telemetry_display_trusted_only": False,
                       "telemetry_icon": "account", "telemetry_fg": [0, 0, 0, 1],
                       "telemetry_bg": [1, 1, 1, 1], **(overrides or {})}
        core.telemetry_interfaces = {}
        core.fixed_locations = fixed or {}
        core.lxmf_destination = SimpleNamespace(hash=own)
        core.peer_appearance = lambda source: ["account", [0.2, 0.3, 0.4, 1], [1, 1, 1, 1]]
        core.is_trusted = lambda source: trusted
        rows = report_rows if rows is None else rows
        query_calls = []
        core.list_telemetry = lambda **kwargs: query_calls.append(kwargs) or dict(rows)
        core.get_telemetry = lambda: meters["own"].read_all() if own_readings is None else own_readings
        app = SimpleNamespace(sideband=core, list_discovered_interfaces=lambda: discovered,
                              own_appearance_changed=False)
        instance = Map()
        instance.app = app
        instance.clustered_layer = ObservedLayer()
        instance.clustered_markers = {}
        instance.interface_discovery_hashes = {}
        instance.peer_interface_discovery_hashes = {}
        instance.connection_map_hashes = {}
        instance.connection_map_only_blocked = False
        instance.last_interfaces_update = 0
        instance.map = SimpleNamespace(trigger_update=lambda *args: None)
        instance.update_markers = instance.update_clustered_markers
        return instance, query_calls

    def snapshot(instance):
        return [{
            "key": key, "source": marker.source_dest, "latitude": marker.lat, "longitude": marker.lon,
            "appearance": [marker.icon, marker.icon_color, marker.md_bg_color],
            "small": marker.small, "inspectable": marker.inspectable,
            "parent": getattr(marker, "parent", None)}
            for key, marker in sorted(instance.clustered_markers.items())]

    overlays = []

    def observe(name, instance, calls):
        instance.update_clustered_markers()
        overlays.append({"name": name, "config": dict(instance.app.sideband.config),
                         "markers": snapshot(instance), "query": list(calls),
                         "only_blocked": instance.connection_map_only_blocked})

    for name, settings in (
        ("initial", {}),
        ("interfaces_off_fresh", {"map_interfaces": False}),
        ("connections_off_fresh", {"map_connection_maps": False}),
        ("both_overlays_off_fresh", {"map_interfaces": False, "map_connection_maps": False}),
        ("hide_blocked_fresh", {"map_hide_blocked": True}),
        ("untrusted_filtered_fresh", {"telemetry_display_trusted_only": True})):
        instance, calls = new_map(settings, trusted=False)
        observe(name, instance, calls)
    instance, calls = new_map()
    instance.connection_map_only_blocked = True
    observe("blocked_only_fresh", instance, calls)
    instance, calls = new_map(fixed={peer: {"latitude": "50.0", "longitude": "3.0"}})
    observe("fixed_location_override", instance, calls)
    zero_interfaces = telemetry(UTC - 5, interfaces=(
        ("Equator", 0.0, 2.25), ("Meridian", 48.25, 0.0)))
    instance, calls = new_map(rows={peer: [[UTC - 5, zero_interfaces.packed()]]}, own_readings={})
    observe("zero_telemetry_interface_coordinates", instance, calls)
    instance, calls = new_map()
    observe("before_overlay_disable", instance, calls)
    instance.app.sideband.config["map_interfaces"] = False
    instance.app.sideband.config["map_connection_maps"] = False
    observe("after_overlay_disable", instance, calls)
    instance, calls = new_map()
    observe("before_blocked_only_toggle", instance, calls)
    instance.connection_maps_blocked_action()
    observe("after_blocked_only_toggle", instance, calls)
    rows = dict(report_rows)
    instance, calls = new_map(rows=rows)
    observe("before_reports_removed", instance, calls)
    rows.clear()
    observe("after_reports_removed", instance, calls)
    connection_markers = []
    for type_label, quality_value in (
        ("connection", None), ("connection", -1), ("connection", 0), ("connection", 20),
        ("connection", 20.9), ("connection", 21), ("connection", 40), ("connection", 41),
        ("connection", 60), ("connection", 61), ("connection", 80), ("connection", 81),
        ("connection", "81"), ("connection", "invalid"), ("blocked", 100)):
        meter = telemetry(UTC - 5, points=[{
            "lat": 48.5, "lon": 2.5, "altitude": 0, "name": "quality",
            "type_label": type_label, "signal_q": quality_value}])
        instance, _ = new_map({"map_interfaces": False},
                              rows={peer: [[UTC - 5, meter.packed()]]}, own_readings={})
        instance.update_clustered_markers()
        connection_markers.append({"type_label": type_label, "quality": quality_value,
                                   "wire_hex": meter.packed().hex(),
                                   "markers": snapshot(instance)})
    identities = []
    identity_meter = telemetry(UTC - 5, interfaces=(
        ("Synthetic TCP", 48.25, 2.25), ("Synthetic TCP \u2603", 48.25, 2.25)))
    for source in (own, peer):
        instance, _ = new_map({"map_interfaces": False}, own_readings={},
                              rows={source: [[UTC - 5, identity_meter.packed()]]})
        if source == own:
            instance.app.sideband.get_telemetry = identity_meter.read_all
        instance.update_clustered_markers()
        identities.append({"source": source, "interface_names": ["Synthetic TCP", "Synthetic TCP \u2603"],
                           "wire_hex": identity_meter.packed().hex(),
                           "markers": snapshot(instance)})
    icon_owner = Core()
    quality = [{"type": name, "appearance": icon_owner.iface_appearance(name)} for name in (
        "RNodeInterface", "WeaveInterface", "KISSInterface", "BackboneInterface",
        "TCPServerInterface", "I2PInterface", "unknown", "connection", "blocked",
        "signal--1", "signal-0", "signal-20", "signal-21", "signal-40", "signal-41",
        "signal-60", "signal-61", "signal-80", "signal-81", "signal-100", "signal-invalid")]

    clusters = []
    point_sets = {
        "empty": [],
        "single": [(2, 48)],
        "nearby_and_far": [(2, 48), (2.0001, 48.0001), (-120, 35)],
        "coincident": [(2, 48), (2, 48), (2, 48)],
        "poles_and_edges": [(-180, -90), (180, 90), (0, 0)],
        "indexed_grid": [(index * 5 - 175, 20 + index % 3) for index in range(70)]}
    for name, coordinates in point_sets.items():
        for zoom in (0, 10, 18, 19, 24):
            points = [namespace["Marker"](longitude, latitude) for longitude, latitude in coordinates]
            cluster = namespace["SuperCluster"](
                min_zoom=cluster_options["cluster_min_zoom"], max_zoom=cluster_options["cluster_max_zoom"],
                radius=cluster_options["cluster_radius"], extent=cluster_options["cluster_extent"])
            cluster.load(points)
            result = cluster.get_clusters([-180, -90, 180, 90], zoom)
            clusters.append({"name": name, "coordinates": coordinates, "zoom": zoom,
                             "bbox": [-180, -90, 180, 90],
                             "result": [{"latitude": point.lat, "longitude": point.lon,
                                         "count": getattr(point, "num_points", 1),
                                         "extents": getattr(point, "extents", None)}
                                        for point in result]})
    viewport_points = [(2, 48), (-120, 35)]
    for bbox in ([1, 47, 3, 49], [-121, 34, -119, 36], [20, 20, 30, 30]):
        cluster = namespace["SuperCluster"](
            min_zoom=0, max_zoom=18, radius=100, extent=512)
        cluster.load([namespace["Marker"](*point) for point in viewport_points])
        clusters.append({"name": "viewport_filter", "coordinates": viewport_points, "zoom": 19,
                         "bbox": bbox, "result": [
                             {"latitude": point.lat, "longitude": point.lon,
                              "count": getattr(point, "num_points", 1),
                              "extents": getattr(point, "extents", None)}
                             for point in cluster.get_clusters(bbox, 19)]})

    scatter = []
    for name, coordinates, positions in (
        ("coincident", [(2, 48)] * 8, [(100, 100)] * 8),
        ("within_coordinate_tolerance", [(2, 48), (2.00000001, 48.00000001)],
         [(100, 100), (100, 100)]),
        ("close_distinct", [(2, 48), (2.00001, 48.00001)], [(100, 100), (104, 106)]),
        ("far", [(2, 48), (3, 49)], [(100, 100), (200, 200)])):
        for zoom in (18, 19):
            points = [namespace["Marker"](longitude, latitude) for longitude, latitude in coordinates]
            for order, point in enumerate(points):
                point.observation_order = order
            view = View()
            view._zoom = zoom
            view.center_x = view.center_y = 0
            window_positions = dict(zip(coordinates, positions))
            view.get_window_xy_from = lambda latitude, longitude, z: window_positions[(longitude, latitude)]
            offsets = view.scatter_markers(points)
            scatter.append({"name": name, "coordinates": coordinates, "zoom": zoom,
                            "window_positions": positions,
                            "result": [{"index": point.observation_order, "offset": offsets[point]}
                                       for point in points if point in offsets]})

    bounds = []
    for extents in ([48, 48.0001, 2, 2.0001], [35, 48, -120, 2], [48, 48, 2, 2]):
        for float_zoom in (False, True):
            projection = Projection()
            projection.dp_tile_size, projection.min_zoom, projection.max_zoom = 256, 0, 19
            view = View()
            view.map_source = projection
            view.width, view.height = 800, 600
            view.center_x, view.center_y = 400, 300
            captured = {}
            view.set_zoom_at = lambda zoom, x, y: captured.update(zoom=zoom, anchor=[x, y])
            view.center_on = lambda latitude, longitude: captured.update(center=[latitude, longitude])
            view.zoom_to_bbox(*extents, float_zoom=float_zoom)
            bounds.append({"extents": extents, "size": [800, 600], "padding": 0.85,
                           "float_zoom": float_zoom, "result": captured})
    cluster_clicks = []
    for extents in ([48, 48.0001, 2, 2.0001], [90, 90, 180, 180]):
        calls = []
        button = ClusterButton()
        button.extents = extents
        button.delegate = SimpleNamespace(show_bbox=lambda *args: calls.append(args))
        button.on_release()
        cluster_clicks.append({"extents": extents, "show_bbox_calls": calls})

    output = {
        "revision": PIN, "source_sha256": SOURCES,
        "method": "Executed verified Git-blob AST-selected primary Map/SidebandCore/MapView/MapSource and complete Marker/Cluster/KDBush/SuperCluster classes; imported primary Telemeter sensors generated all packed reports. No clustering, filtering, projection, scatter or sensor algorithm is reimplemented in this adapter.",
        "seams": [
            "Pinned RNS actual imported checkout is verified by tests/Interop/reference_environment.py; actual primary sense/geo import paths and source hashes are verified. Absolute host paths are printed, not embedded in this portable fixture.",
            "No Kivy imports, licensed assets or UI constructors. dp is identity; marker widget type is object; a synthetic layer records actual Marker objects and rebuild requests. Map.update_markers dispatches directly to the actual clustered implementation.",
            "Core owners supply in-window telemetry rows, trust, appearance, own readings and discovered-interface records. list_telemetry records the actual after bound but does not execute SQL or discovery validation. Configuration text is not retained.",
            "Only the sensor-module and selected-method clock dependencies are fixed at UTC 1700000000. No global Python time-module mutation, devices, network or live-node claim.",
            "Scatter's process-dependent id(marker) ordering is replaced only in the executed namespace by the input marker index. Randomness is not used. Its UI owner supplies explicitly recorded synthetic window positions; this avoids cross-platform cancellation of large projected coordinates. Bounding-box fitting separately executes the actual primary MapSource projection at 256 pixels per tile.",
            "Floating observation values are rounded to ten decimal places for portable cross-host output; packed MessagePack hex is exact. JSON output uses UTF-8 and LF.",
            "Observed primary quirks are retained as observations: zero-coordinate interface omission, telemetry interfaces surviving disabled overlays/report removal, and source destination ownership of connection points. They are not native implementation requirements without an explicit decision."],
        "utc": UTC, "defaults": config.config,
        "cluster_options": cluster_options, "cluster_library_defaults": cluster_library_defaults,
        "cluster_layer_defaults": cluster_layer_defaults,
        "interface_update_interval": Map.IF_UPDATE_INTERVAL,
        "reports": [{"name": name, "source": own if name == "own" else peer,
                     "wire_hex": packed_reports[name].hex(), "readings": meters[name].read_all()}
                    for name in meters],
        "discovered_inputs": discovered, "connection_point_inputs": input_points,
        "overlay_cases": overlays, "appearance_cases": quality,
        "connection_marker_cases": connection_markers, "identity_cases": identities,
        "cluster_cases": clusters, "scatter_cases": scatter,
        "bounds_cases": bounds, "cluster_click_cases": cluster_clicks}
    args.output.write_text(json.dumps(portable(output), indent=2, allow_nan=False) + "\n",
                           encoding="utf-8", newline="\n")
    print(json.dumps({"revision": PIN, "rns": RNS.__file__, "sense": sense.__file__,
                      "geo": geo.__file__, "output": str(args.output.resolve()),
                      "overlays": len(overlays), "appearances": len(quality), "clusters": len(clusters),
                      "connection_markers": len(connection_markers), "identities": len(identities),
                      "scatter": len(scatter), "bounds": len(bounds), "cluster_clicks": len(cluster_clicks)}))


if __name__ == "__main__":
    main()
