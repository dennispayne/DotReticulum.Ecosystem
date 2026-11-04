"""Observe pinned primary provider definitions and map-layer selection, without Kivy/network."""
import argparse
import ast
import hashlib
import json
import subprocess
from pathlib import Path
from types import SimpleNamespace

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = args.source.resolve()
    if subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip() != PIN:
        raise RuntimeError("Unexpected primary revision")
    hashes = {}

    def tree(path):
        blob = subprocess.check_output(["git", "-C", str(root), "show", f"{PIN}:{path}"])
        if (root / path).read_bytes().replace(b"\r\n", b"\n") != blob:
            raise RuntimeError(f"Modified primary {path}")
        hashes[path] = hashlib.sha256(blob).hexdigest()
        return ast.parse(blob)

    source_tree = tree("sbapp/mapview/source.py")
    map_tree = tree("sbapp/ui/map.py")
    core_tree = tree("sbapp/sideband/core.py")
    namespace = {"hashlib": hashlib, "dp": lambda value: value, "CACHE_DIR": "synthetic-cache"}

    def load_class(module, name, methods, assignments=False):
        original = next(node for node in module.body if isinstance(node, ast.ClassDef) and node.name == name)
        body = [node for node in original.body
                if isinstance(node, ast.FunctionDef) and node.name in methods
                or assignments and isinstance(node, ast.Assign)]
        selected = ast.ClassDef(name=name, bases=[], keywords=[], body=body, decorator_list=[])
        exec(compile(ast.fix_missing_locations(ast.Module(body=[selected], type_ignores=[])),
                     f"actual-primary-{name}", "exec"), namespace)
        return namespace[name]

    source_type = load_class(source_tree, "MapSource", {"__init__", "update_providers", "from_provider"}, True)
    source_type.mt_key = "synthetic-maptiler-key"
    source_type.st_key = "synthetic-stadia-key"
    source_type.custom_1_url = "https://maps.example.invalid/{z}/{x}/{y}.png"
    source_type.update_providers()
    providers = []
    for key in source_type.providers:
        source = source_type.from_provider(key, quad_key=key == "virtualearth")
        providers.append({"id": key, "name": source.name, "url": source.url,
                          "attribution": source.attribution, "minimum_zoom": source.min_zoom,
                          "maximum_zoom": source.max_zoom, "quad": source.quad_key})

    defaults = SimpleNamespace(config={})
    default_nodes = []
    for node in ast.walk(core_tree):
        if not isinstance(node, ast.If) or len(node.body) != 1 or not isinstance(node.body[0], ast.Assign):
            continue
        test = node.test.operand if isinstance(node.test, ast.UnaryOp) else node.test
        if (isinstance(test, ast.Compare) and isinstance(test.left, ast.Constant)
                and isinstance(test.left.value, str) and test.left.value.startswith("map_")):
            default_nodes.append(node)
    exec(compile(ast.Module(body=default_nodes, type_ignores=[]), "actual-primary-map-defaults", "exec"),
         {"self": defaults})
    # Do not execute/copy licensed UI constructor or its bundled provider keys.
    map_type = load_class(map_tree, "Map", {"get_layers", "get_source"})
    map_type.DEFAULT_LAYER = "osm"
    map_type.QUAD_KEY_LAYERS = ["virtualearth"]
    original_map = next(node for node in map_tree.body if isinstance(node, ast.ClassDef) and node.name == "Map")
    constructor = next(node for node in original_map.body if isinstance(node, ast.FunctionDef) and node.name == "__init__")
    clamp_nodes = [node for node in ast.walk(constructor) if isinstance(node, ast.If)
                   and len(node.body) == 1 and isinstance(node.body[0], ast.Assign)
                   and any(isinstance(name, ast.Name) and name.id in {"mzoom", "mlat", "mlon"}
                           for name in ast.walk(node.test))]
    if len(clamp_nodes) != 6:
        raise RuntimeError("Unexpected primary map constructor clamp surface")
    clamp_observations = []
    for latitude, longitude, zoom in ((100, -200, 100), (-100, 200, -1), (48.8566, 2.3522, 12)):
        environment = {"mzoom": zoom, "mlat": latitude, "mlon": longitude,
                       "msource": SimpleNamespace(min_zoom=0, max_zoom=19)}
        exec(compile(ast.Module(body=clamp_nodes, type_ignores=[]), "actual-primary-map-clamps", "exec"), environment)
        clamp_observations.append({"latitude": latitude, "longitude": longitude, "zoom": zoom,
                                   "result_latitude": environment["mlat"], "result_longitude": environment["mlon"],
                                   "result_zoom": environment["mzoom"]})
    cases = []
    for selected, enabled in ((None, {}), ("unknown", {}), ("atlas", {}),
                              ("atlas", {"map_use_atlas": True}),
                              ("virtualearth", {}), ("custom_1", {"map_use_custom_1": True})):
        config = {**defaults.config, **enabled, "map_layer": selected,
                  "map_custom_1_url": source_type.custom_1_url}
        instance = map_type()
        instance.app = SimpleNamespace(sideband=SimpleNamespace(config=config))
        instance.map_cache = "synthetic-cache"
        source = instance.get_source()
        cases.append({"selected": selected, "enabled": enabled, "result": source.cache_key})
    args.output.write_text(json.dumps({
        "revision": PIN, "source_sha256": hashes,
        "method": "Executed AST-selected actual primary MapSource methods, core map defaults, Map.get_layers/get_source and the six online-source constructor clamp statements. Kivy dp is identity; no complete UI constructor, bundled keys, HTTP or rendering.",
        "providers": providers, "selection": cases,
        "constructor_clamps": clamp_observations,
        "defaults": defaults.config,
    }, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(f"Executed {len(providers)} providers and {len(cases)} layer selections at {PIN}")


if __name__ == "__main__":
    main()
