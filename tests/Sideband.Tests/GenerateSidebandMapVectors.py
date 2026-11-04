"""Execute pinned map projection, URL and raster-MBTiles methods without Kivy or network."""
import argparse
import ast
import hashlib
import io
import json
import math
import os
from pathlib import Path
import sqlite3
import subprocess
import tempfile
from types import SimpleNamespace
from PIL import Image, ImageDraw

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--mbtiles-output", type=Path, required=True)
    args = parser.parse_args()
    root = args.source.resolve()
    if subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip() != PIN:
        raise RuntimeError("Unexpected primary map revision")
    sources, hashes = {}, {}
    for name in ("source", "constants", "utils", "downloader", "mbtsource"):
        relative = f"sbapp/mapview/{name}.py"
        blob = subprocess.check_output(["git", "-C", str(root), "show", f"{PIN}:{relative}"])
        if (root / relative).read_bytes().replace(b"\r\n", b"\n") != blob:
            raise RuntimeError(f"Modified primary {relative}")
        sources[name] = ast.parse(blob)
        hashes[relative] = hashlib.sha256(blob).hexdigest()

    core_path = "sbapp/sideband/core.py"
    core_blob = subprocess.check_output(["git", "-C", str(root), "show", f"{PIN}:{core_path}"])
    hashes[core_path] = hashlib.sha256(core_blob).hexdigest()
    if (root / core_path).read_bytes().replace(b"\r\n", b"\n") != core_blob:
        raise RuntimeError("Modified primary core")
    default_keys = {"map_lat", "map_lon", "map_zoom", "map_use_offline", "map_use_online"}
    def is_default(node):
        if not isinstance(node, ast.If):
            return False
        test = node.test
        if isinstance(test, ast.UnaryOp) and isinstance(test.op, ast.Not):
            test = test.operand
        return (isinstance(test, ast.Compare) and isinstance(test.left, ast.Constant)
                and test.left.value in default_keys
                and len(node.body) == 1 and isinstance(node.body[0], ast.Assign))

    default_nodes = [node for node in ast.walk(ast.parse(core_blob)) if is_default(node)]
    defaults = SimpleNamespace(config={})
    exec(compile(ast.Module(body=default_nodes, type_ignores=[]), "primary-map-defaults", "exec"), {"self": defaults})
    if set(defaults.config) != default_keys:
        raise RuntimeError("Primary map defaults were not all executed")

    namespace = {name: getattr(math, name) for name in ("atan", "ceil", "cos", "exp", "log", "pi", "tan")}
    namespace.update(dp=lambda value: value, hashlib=hashlib)
    constant_nodes = [node for node in sources["constants"].body if isinstance(node, ast.Assign)]
    clamp = next(node for node in sources["utils"].body if isinstance(node, ast.FunctionDef) and node.name == "clamp")
    exec(compile(ast.Module(body=constant_nodes + [clamp], type_ignores=[]), "primary-map-constants", "exec"), namespace)

    def load_class(module, name, methods, bases=None):
        original = next(node for node in sources[module].body if isinstance(node, ast.ClassDef) and node.name == name)
        selected = [node for node in original.body if isinstance(node, ast.FunctionDef) and node.name in methods]
        cls = ast.ClassDef(name=name, bases=bases or [], keywords=[], body=selected, decorator_list=[])
        exec(compile(ast.fix_missing_locations(ast.Module(body=[cls], type_ignores=[])), f"primary-{module}", "exec"), namespace)
        return namespace[name]

    map_type = load_class("source", "MapSource", {
        "__init__", "get_x", "get_y", "get_lon", "get_lat", "get_row_count", "get_col_count"
    })
    projection = []
    errors = []
    for size in (128, 256, 512):
        source = map_type(tile_size=size)
        for zoom in (0, 1, 5, 12, 19):
            for latitude, longitude in ((0, 0), (51.5, -0.125), (-33.8688, 151.2093),
                                        (85.0511287798066, 180), (-85.0511287798066, -180),
                                        (89, 200), (-89, -200)):
                x, y = source.get_x(zoom, longitude), source.get_y(zoom, latitude)
                projection.append({"tile_size": size, "zoom": zoom, "latitude": latitude, "longitude": longitude,
                                   "x": x, "y": y, "inverse_latitude": source.get_lat(zoom, y),
                                   "inverse_longitude": source.get_lon(zoom, x),
                                   "rows": source.get_row_count(zoom), "columns": source.get_col_count(zoom)})
        for latitude in (-90, 90):
            try:
                errors.append({"latitude": latitude, "tile_size": size, "result": source.get_y(0, latitude)})
            except Exception as error:
                errors.append({"latitude": latitude, "tile_size": size, "exception": type(error).__name__})

    captured = []
    class Session:
        def get(self, uri, **kwargs):
            captured.append({"uri": uri, **kwargs})
            return SimpleNamespace(content=b"synthetic-controlled-tile", raise_for_status=lambda: None)

    namespace.update(exists=os.path.exists, RNS=SimpleNamespace(), os=os, tempfile=tempfile,
                     time=lambda: 1700000000, choice=lambda values: values[0], USER_AGENT="Kivy-garden.mapview")
    downloader_type = load_class("downloader", "Downloader", {"_load_tile", "__to_quad"})
    downloader = downloader_type()
    downloader.session = Session()
    downloader.ensure_session = lambda: None
    urls = []
    with tempfile.TemporaryDirectory(prefix="sideband-map-vectors-") as directory:
        for zoom, x, y in ((0, 0, 0), (1, 0, 0), (1, 1, 1), (5, 17, 9), (19, 524287, 524287)):
            for quad in (False, True):
                source = map_type(url="https://{s}.example.invalid/{q}.png" if quad else "https://{s}.example.invalid/{z}/{x}/{y}.png",
                                  quad_key=quad)
                tile = SimpleNamespace(state="pending", map_source=source, zoom=zoom, tile_x=x, tile_y=y,
                                       cache_fn=str(Path(directory) / f"{zoom}-{x}-{y}-{quad}.png"),
                                       set_source=lambda _: None)
                result = downloader._load_tile(tile)
                if result is None:
                    raise RuntimeError("Actual downloader did not execute its controlled request")
                urls.append({"zoom": zoom, "x": x, "tms_y": y, "quad": quad, **captured[-1]})

        namespace.update(sqlite3=sqlite3, RNS=SimpleNamespace(log=lambda *_: None, LOG_DEBUG=0))
        mbtiles_type = load_class("mbtsource", "MBTilesMapSource", {"__init__", "get_x", "get_y", "get_lon", "get_lat", "_load_tile", "_load_tile_done"},
                                 [ast.Name(id="MapSource", ctx=ast.Load())])
        databases = []
        for index, extra in enumerate((
            {"center": "2.3522,48.8566,4", "bounds": "-5,40,10,55"},
            {"bounds": "-5,40,10,55"},
            {"projection": "xy", "center": "128,64,2"},
            {"format": "pbf"},
        )):
            path = Path(directory) / f"map-{index}.mbtiles"
            metadata = {"format": "png", "minzoom": "1", "maxzoom": "5", **extra}
            database = sqlite3.connect(path)
            try:
                database.execute("CREATE TABLE metadata (name TEXT, value TEXT)")
                database.executemany("INSERT INTO metadata VALUES (?,?)", metadata.items())
                database.commit()
            finally:
                database.close()
            source = mbtiles_type.__new__(mbtiles_type)
            try:
                source.__init__(str(path))
                databases.append({"metadata": metadata, "min_zoom": source.min_zoom, "max_zoom": source.max_zoom,
                                  "stored_max_zoom": source._max_zoom, "latitude": source.default_lat,
                                  "longitude": source.default_lon, "zoom": source.default_zoom,
                                  "bounds": source.bounds, "xy": source.is_xy, "attribution": source.attribution})
            except Exception as error:
                if metadata["format"] != "pbf" or not isinstance(error, ValueError):
                    raise
                databases.append({"metadata": metadata, "exception": type(error).__name__})
            finally:
                if hasattr(source, "db"):
                    source.db.close()

    if args.mbtiles_output.exists():
        args.mbtiles_output.unlink()
    tile_observations = []
    database = sqlite3.connect(args.mbtiles_output)
    try:
        database.execute("CREATE TABLE metadata (name TEXT, value TEXT)")
        database.execute("CREATE TABLE tiles (zoom_level INTEGER, tile_column INTEGER, tile_row INTEGER, tile_data BLOB)")
        database.executemany("INSERT INTO metadata VALUES (?,?)", {
            "format": "png", "minzoom": "0", "maxzoom": "1", "center": "0,0,1",
            "attribution": "Synthetic acceptance map; no geographic content"
        }.items())
        for x in range(2):
            for y in range(2):
                image = Image.new("RGB", (256, 256), (40 + x * 100, 50 + y * 100, 100))
                ImageDraw.Draw(image).rectangle((1, 1, 254, 254), outline="white")
                encoded = io.BytesIO()
                image.save(encoded, format="PNG")
                database.execute("INSERT INTO tiles VALUES (1,?,?,?)", (x, y, encoded.getvalue()))
        database.commit()
        namespace.update(io=io, threading=SimpleNamespace(local=lambda: SimpleNamespace(db=database)),
                         CoreImage=lambda data, **_: data.getvalue())
        source = mbtiles_type(str(args.mbtiles_output))
        try:
            for x, y in ((0, 0), (0, 1), (1, 0), (1, 1), (2, 0)):
                tile = SimpleNamespace(zoom=1, tile_x=x, tile_y=y, state="pending")
                result = source._load_tile(tile)
                tile_observations.append({
                    "zoom": 1, "x": x, "tms_y": y,
                    "sha256": hashlib.sha256(result[1][1]).hexdigest() if result else None,
                    "state": tile.state,
                })
        finally:
            source.db.close()
    finally:
        database.close()

    args.output.write_text(json.dumps({
        "revision": PIN, "source_sha256": hashes,
        "method": "Actual AST-selected primary classes/methods, real SQLite and file replacement; dp is fixed to identity, HTTP is a recorded controlled response, no Kivy window/image/clock/device/network execution.",
        "projection": projection, "pole_observations": errors, "urls": urls, "mbtiles": databases,
        "mbtiles_tiles": tile_observations,
        "mbtiles_fixture_sha256": hashlib.sha256(args.mbtiles_output.read_bytes()).hexdigest(),
        "primary_defaults": defaults.config,
        "boundaries": [
            "Source Y is bottom-origin; HTTP XYZ tile rows invert the native TMS row.",
            "Primary latitude constants allow poles; the north pole fails projection while the south pole gives an out-of-world finite result.",
            "Native rendering clips to the finite Mercator viewport rather than mirroring the pole defect.",
            "MBTiles metadata exposes max(stored_max_zoom,19), but stored_max_zoom still bounds available raster content.",
            "No map tiles, upstream assets or attribution rights are supplied by these synthetic observations."
        ]
    }, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(f"Executed {len(projection)} projection, {len(urls)} URL and {len(databases)} MBTiles observations at {PIN}")


if __name__ == "__main__":
    main()
