"""Observe pinned Sideband geodesy without copying its implementation or grid.

Run with the isolated reference Python, DOTRETICULUM_UPSTREAM and PYTHONPATH
selecting the pinned RNS checkout; pass --sideband and --output explicitly.
"""
import argparse
import hashlib
import importlib
import json
import math
import os
from pathlib import Path
import random
import subprocess
import sys

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
GEO = "af416ae7a1c087cc0c636e994ff58d5c6f7edf9f85379765d7fda9d130e780c7"
SENSE = "80418b1aa4de638f2c65f1bd64e5ea0466378a6f0bc682781672ef89b93a7dab"
GRID = "96d55e88db186ddae892b00c5f8cc42a37cdac8ddb69b6de00af8c75dffb34a3"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sideband", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root = args.sideband.resolve(strict=True)

    def git(*command):
        return subprocess.check_output(["git", "-C", str(root), *command]).decode().strip()

    if git("rev-parse", "HEAD") != PIN or git("status", "--porcelain", "--untracked-files=no"):
        raise RuntimeError("Sideband checkout is not the clean pinned primary")
    for relative, expected in (("sbapp/sideband/geo.py", GEO), ("sbapp/sideband/sense.py", SENSE)):
        canonical = subprocess.check_output(["git", "-C", str(root), "show", f"HEAD:{relative}"])
        if hashlib.sha256(canonical).hexdigest() != expected:
            raise RuntimeError(f"Unexpected primary source: {relative}")
        if (root / relative).read_bytes().replace(b"\r\n", b"\n") != canonical:
            raise RuntimeError(f"Working source differs: {relative}")
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import verify_reference
    verify_reference(require_lxmf=True)
    sys.path.insert(0, str(root))
    geo = importlib.import_module("sbapp.sideband.geo")
    if Path(geo.__file__).resolve() != root / "sbapp" / "sideband" / "geo.py":
        raise RuntimeError("Unexpected actual geo module import")
    grid = root / "sbapp" / "assets" / "geoids" / "egm2008-5.pgm"
    if hashlib.sha256(grid.read_bytes()).hexdigest() != GRID:
        raise RuntimeError("Unexpected external grid")
    os.environ["TELEMETER_GEOID_PATH"] = str(grid.parent)
    rng = random.Random(680)
    coordinates = [
        (0, 0), (51.5, -0.12), (-33.8688, 151.2093), (27.9881, 86.925),
        (0, -180), (0, 180), (0, 360), (0, -360), (0, 720),
        (45, -1), (45, 359), (45, 719), (90, 0), (-90, 0),
        (89.99, 12), (-89.99, 12), (89.9, 12), (-89.9, 12),
        (90.001, 0), (-90.001, 0), (91, 0), (-91, 0),
        (0, -721), (0, 1080), (float("nan"), 0), (0, float("inf")),
        (0, -1e-10), (0, 359.9999999999), (12.0833333333333, 10.0833333333333),
    ] + [(rng.uniform(-89.8, 89.8), rng.uniform(-180, 360)) for _ in range(64)]

    def observe(action):
        instance = geo.GeoidHeight()
        try:
            geo.geoid_height = instance
            value = action(instance)
            return {"value": value} if math.isfinite(value) else {"nonfinite": str(value)}
        except Exception as error:
            return {"error": type(error).__name__, "message": str(error)}
        finally:
            instance.raw.close()

    observations = []
    for index, (lat, lon) in enumerate(coordinates):
        altitude = (-123.5, 0, 125.25, 8848)[index % 4]
        observations.append({
            "latitude": lat if math.isfinite(lat) else str(lat),
            "longitude": lon if math.isfinite(lon) else str(lon),
            "ellipsoid_altitude": altitude,
            "cubic": observe(lambda instance: instance.get(lat, lon)),
            "bilinear": observe(lambda instance: instance.get(lat, lon, cubic=False)),
            "aamsl": observe(lambda instance: geo.altitude_to_aamsl(altitude, lat, lon)),
        })
    import RNS
    cached = geo.GeoidHeight()
    cached_failure_probe = []
    try:
        for lat, lon in ((45, 359), (90, 0), (90, 0)):
            try:
                cached_failure_probe.append({"latitude": lat, "longitude": lon, "value": cached.get(lat, lon)})
            except Exception as error:
                cached_failure_probe.append({"latitude": lat, "longitude": lon, "error": type(error).__name__})
    finally:
        cached.raw.close()
    result = {
        "sideband_revision": PIN, "geo_sha256": GEO, "sense_sha256": SENSE,
        "grid_sha256": GRID, "grid_bytes": grid.stat().st_size,
        "method": "Actual sbapp.sideband.geo.GeoidHeight.get and altitude_to_aamsl; fresh instance per observation, no shims",
        "rns_revision": git_rns_revision(), "rns_version": RNS.__version__,
        "geo_import_relative": str(Path(geo.__file__).relative_to(root)).replace("\\", "/"),
        "tolerance_metres": 1e-8,
        "cached_failure_probe": cached_failure_probe,
        "observations": observations,
    }
    args.output.write_text(json.dumps(result, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def git_rns_revision():
    return subprocess.check_output(
        ["git", "-C", os.environ["DOTRETICULUM_UPSTREAM"], "rev-parse", "HEAD"]
    ).decode().strip()


if __name__ == "__main__":
    main()
