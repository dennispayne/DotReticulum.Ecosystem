"""Thin bridge: execute the exact pinned primary Node.request_allowed on real files.

No source/authorization/process shim. Synthetic Node has no app or network runtime;
the real method only needs the supplied file path and public identity hash.
"""
import argparse
import hashlib
import importlib
import json
import os
from pathlib import Path
import shutil
import sys
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from GenerateNomadNetClientRuntimeVectors import reference, REVISION


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--generator", required=True)
    parser.add_argument("--work", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    source, imports = reference()
    module = importlib.import_module("nomadnet.Node")
    node = module.Node.__new__(module.Node)
    root = Path(args.work).resolve()
    root.mkdir(parents=True, exist_ok=False)
    public_hash = bytes.fromhex("12" * 16)
    identity = SimpleNamespace(hash=public_hash)
    logs = []
    original_log = module.RNS.log
    module.RNS.log = lambda message, *values, **kwargs: logs.append(str(message))
    cases = []
    try:
        for mode in ("allow", "mixed", "cr", "malformed", "nonzero", "stderr"):
            page = root / (mode + ".mu")
            page.write_bytes(b"synthetic page")
            generator = Path(str(page) + ".allowed")
            shutil.copyfile(args.generator, generator)
            Path(str(generator) + ".input").write_text(mode + "\n" + public_hash.hex() + "\n")
            for name, peer in (("matching", identity), ("outsider", SimpleNamespace(hash=bytes(16))), ("anonymous", None)):
                logs.clear()
                allowed = node.request_allowed(str(page), peer)
                cases.append({"mode": mode, "peer": name, "allowed": allowed,
                              "osAccessExecute": os.access(generator, os.X_OK), "logs": list(logs)})
        page = root / "static.mu"
        page.write_bytes(b"synthetic page")
        Path(str(page) + ".allowed").write_text(public_hash.hex() + "\n")
        logs.clear()
        cases.append({"mode": "static", "peer": "matching",
                      "allowed": node.request_allowed(str(page), identity), "logs": list(logs)})
        cases.append({"mode": "missing-companion", "peer": "anonymous",
                      "allowed": node.request_allowed(str(root / "public.mu"), None)})
        cases.append({"mode": "companion-direct", "peer": "matching",
                      "allowed": node.request_allowed(str(page) + ".allowed", identity)})
    finally:
        module.RNS.log = original_log
    result = {"revision": REVISION, "imports": imports, "nodeModule": module.__file__,
              "nodeSha256": hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest(),
              "generatorSha256": hashlib.sha256(Path(args.generator).read_bytes()).hexdigest(),
              "boundary": __doc__, "cases": cases}
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
