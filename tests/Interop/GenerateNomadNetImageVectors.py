"""Execute primary node conversion and the pinned RNS backend on generated public images."""
import base64
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import shutil
import sys
import tempfile
import threading
from types import SimpleNamespace

from reference_environment import verify_reference


def main():
    verify_reference(require_lxmf=True)
    source = Path(os.environ["DOTRETICULUM_NOMADNET_SOURCE"]).resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2":
        raise RuntimeError("Unexpected primary revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip():
        raise RuntimeError("Modified primary reference")
    sys.path.insert(0, str(source))
    from nomadnet.Node import Node
    import RNS.Utilities.rngit.media as media
    from PIL import Image
    if Path(sys.modules[Node.__module__].__file__).resolve() != source / "nomadnet/Node.py":
        raise RuntimeError("Wrong imported Node")
    work = Path("artifacts/tests/nomadnet-image-reference").resolve()
    work.mkdir(parents=True, exist_ok=True)
    # Primary helper uses NamedTemporaryFile; keep its actual outputs inside the project.
    tempfile.tempdir = str(work)
    node = Node.__new__(Node)
    node.app = SimpleNamespace(convcachepath=str(work / "cache"))
    node.conversion_cache_lock = threading.Lock()
    vectors = []
    for fmt, size in [("PNG", (40, 20)), ("JPEG", (40, 20)), ("BMP", (40, 20)),
                      ("GIF", (40, 20)), ("TIFF", (40, 20)), ("WEBP", (40, 20)),
                      ("PNG", (2400, 1200)), ("PNG", (1201, 2401))]:
        image = Image.new("RGB", size, (31, 95, 191))
        buffer = io.BytesIO()
        image.save(buffer, format=fmt)
        original = buffer.getvalue()
        path = work / f"{fmt}-{size[0]}-{size[1]}.{fmt.lower()}"
        path.write_bytes(original)
        result = node.convert_media_to_webp(str(path))
        if result is False:
            raise RuntimeError(f"Actual primary image conversion failed; available backends: {media.available_backends()}")
        encoded = Path(result).read_bytes()
        decoded = Image.open(io.BytesIO(encoded))
        if node.convert_media_to_webp(str(path)) != result:
            raise RuntimeError("Actual primary conversion cache missed")
        vectors.append({"format": fmt.lower(), "input": base64.b64encode(original).decode(),
                        "inputWidth": size[0], "inputHeight": size[1],
                        "width": decoded.width, "height": decoded.height,
                        "referenceWebp": base64.b64encode(encoded).decode(),
                        "cacheName": Path(result).name})
    malformed = work / "malformed.png"
    malformed.write_bytes(b"not an image")
    rejected = node.convert_media_to_webp(str(malformed)) is False
    if not rejected:
        raise RuntimeError("Actual primary accepted the malformed image")
    backend_executable = shutil.which(media._selected_backend()[1][0])
    result = {"commit": revision, "rnsVersion": __import__("RNS").__version__,
              "nodeSha256": hashlib.sha256((source / "nomadnet/Node.py").read_bytes()).hexdigest(),
              "backendSha256": hashlib.sha256(Path(media.__file__).read_bytes()).hexdigest(),
              "backend": media._selected_backend()[0], "quality": Node.CONVERSION_QUALITY,
              "backendVersion": subprocess.check_output([backend_executable, "-version"], text=True).splitlines()[0],
              "backendExecutableSha256": hashlib.sha256(Path(backend_executable).read_bytes()).hexdigest(),
              "malformedRejected": rejected,
              "maximumDimension": Node.CONVERSION_MAX_DIMENSION,
              "cacheFiles": Node.CONVERSION_CACHE_MAX_FILES, "cacheBytes": Node.CONVERSION_CACHE_MAX_BYTES,
              "method": "Actual Node.convert_media_to_webp, pinned RNS convert_file_to_webp, actual selected external encoder; deterministic generated Pillow sources. tempfile output redirected into repository artifacts only.",
              "vectors": vectors}
    Path(sys.argv[1]).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
