"""Execute primary directory persistence and ignored-list persistence contracts."""
import base64
import hashlib
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

from reference_environment import verify_reference


def main():
    verify_reference(require_lxmf=True)
    source = Path(os.environ["DOTRETICULUM_NOMADNET_SOURCE"]).resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2":
        raise RuntimeError("Unexpected primary revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Modified primary source")
    sys.path.insert(0, str(source))
    directory_module = importlib.import_module("nomadnet.Directory")
    app_module = importlib.import_module("nomadnet.NomadNetworkApp")
    import RNS.vendor.umsgpack as msgpack
    for module, name in [(directory_module, "Directory.py"), (app_module, "NomadNetworkApp.py")]:
        if Path(module.__file__).resolve() != source / "nomadnet" / name:
            raise RuntimeError("Unexpected actual imported primary module")
    root = Path(sys.argv[2]).resolve()
    root.mkdir(parents=True, exist_ok=True)
    vectors = []

    def observe(name, packed=None):
        profile = root / name
        storage = profile / "storage"
        storage.mkdir(parents=True, exist_ok=True)
        path = storage / "directory"
        if packed is not None:
            path.write_bytes(msgpack.packb(packed))
        app = SimpleNamespace(directorypath=str(path), ignoredpath=str(profile / "ignored"),
                              ignored_list=[bytes([1]) * 16, bytes([77]) * 16])
        directory = directory_module.Directory(app)
        if packed is None:
            for index, (display_name, trust, node, delivery, identify, rank, notes) in enumerate([
                ("Trusted node", 255, True, 2, True, -9, "first line\nsecond line\tTabbed"),
                ("Untrusted peer", 1, False, 3, False, None, "Delivery preference"),
                ("Unknown 日本語", 2, False, 1, False, 34, ""),
                ("Warning node", 0, True, 1, False, 0, "Not authenticated"),
            ]):
                key = bytes([index + 1]) * 16
                directory.directory_entries[key] = directory_module.DirectoryEntry(
                    key, display_name, trust, node, delivery, identify, rank, notes)
            directory._node_announces = [(1700000000.25, bytes([6]) * 16, b"public node", "node")]
            directory.save_to_disk()
            directory = directory_module.Directory(app)
        observed = [
            {"hash": e.source_hash.hex(), "name": e.display_name, "trust": e.trust_level,
             "node": e.hosts_node, "delivery": e.preferred_delivery, "identify": e.identify,
             "rank": e.sort_rank, "notes": e.notes}
            for e in directory.directory_entries.values()
        ]
        app_module.NomadNetworkApp._persist_ignored_list(app)
        vectors.append({
            "name": name, "directory": base64.b64encode(path.read_bytes()).decode(),
            "ignored": base64.b64encode(Path(app.ignoredpath).read_bytes()).decode(),
            "observed": observed, "ignoredHashes": [h.hex() for h in app.ignored_list],
            "announceCount": len(directory.announce_stream),
        })

    observe("current")
    observe("legacy", {"entry_list": [
        [bytes([11]) * 16, None, 2],
        [bytes([12]) * 16, "Four fields", 255, True],
        [bytes([13]) * 16, "Five fields", 1, False, None],
        [bytes([14]) * 16, "Six fields", 0, True, 3, True],
        [bytes([15]) * 16, "Seven fields", 2, True, 2, False, -42],
        [bytes([16]) * 16, "Eight fields", 255, False, 1, True, 0, None],
    ], "announce_stream": []})
    observe("duplicate-last-wins", {"entry_list": [
        [bytes([20]) * 16, "Before", 255],
        [bytes([20]) * 16, "After", 1, True, 3, True, 19, "last saved entry"],
    ], "announce_stream": []})
    source_hashes = {}
    working_sources = {}
    for filename in ["Directory.py", "NomadNetworkApp.py"]:
        relative = "nomadnet/" + filename
        blob = subprocess.check_output(["git", "-C", str(source), "show", "HEAD:" + relative])
        working = (source / relative).read_bytes()
        canonical = working.replace(b"\r\n", b"\n")
        if canonical != blob:
            raise RuntimeError(f"Imported source differs from its pinned Git blob after LF normalization: {relative}")
        source_hashes[relative] = {
            "canonicalLfSha256": hashlib.sha256(canonical).hexdigest(),
            "blobSha256": hashlib.sha256(blob).hexdigest(),
        }
        working_sources[relative] = {
            "path": str(source / relative),
            "workingSha256": hashlib.sha256(working).hexdigest(),
            **source_hashes[relative],
        }
    result = {
        "commit": revision, "sources": source_hashes,
        "method": "Actual Directory constructor/load_from_disk/save_to_disk and DirectoryEntry; legacy public inputs packed by actual RNS.vendor.umsgpack and loaded by primary. Actual NomadNetworkApp._persist_ignored_list produces ignored bytes. Only the app's filesystem paths are supplied by SimpleNamespace; no replacement parser or persistence implementation. Source LF-normalized bytes must equal their pinned Git blobs exactly; per-host raw working hashes and imported paths are retained in the generation root's source-provenance.json, not mislabeled as canonical hashes. No identities/private material.",
        "vectors": vectors,
    }
    output = Path(sys.argv[1])
    output.write_text(json.dumps(result, indent=2, ensure_ascii=True) + "\n", encoding="utf-8", newline="\n")
    provenance = {
        "commit": revision, "sources": working_sources,
        "outputSha256": hashlib.sha256(output.read_bytes()).hexdigest(),
    }
    (root / "source-provenance.json").write_text(
        json.dumps(provenance, indent=2, ensure_ascii=True) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
