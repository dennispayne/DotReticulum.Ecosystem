"""Execute primary conversation image extraction; no inline-conversation parity claim."""
import base64
import hashlib
import importlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
from types import MethodType, SimpleNamespace

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
    import LXMF
    import RNS
    from PIL import Image
    import RNS.vendor.umsgpack as msgpack
    module = importlib.import_module("nomadnet.Conversation")
    if Path(module.__file__).resolve() != source / "nomadnet/Conversation.py":
        raise RuntimeError("Unexpected primary conversation import")
    work = Path("artifacts/tests/nomadnet-conversation-image-reference-" + str(os.getpid())).resolve()
    work.mkdir(parents=True, exist_ok=False)
    formats = {}
    for fmt in ("PNG", "WEBP"):
        image = Image.new("RGB", (32, 16), (0, 0, 0))
        for x in range(16, 32):
            for y in range(16):
                image.putpixel((x, y), (255, 255, 255))
        buffer = io.BytesIO()
        image.save(buffer, format=fmt)
        formats[fmt.lower()] = buffer.getvalue()
    identity = RNS.Identity()
    destination = RNS.Destination(identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    cases = [
        ("raw-png", formats["png"], []),
        ("formatted-png", ["png", formats["png"]], []),
        ("formatted-webp", ["webp", formats["webp"]], []),
        ("decoy-and-image", ["png", formats["png"]], [["image.png", b"ordinary file, not FIELD_IMAGE"]]),
        ("empty", b"", []),
        ("invalid-pair", ["png", "not bytes"], []),
        ("decoy-only", None, [["image.png", formats["png"]]]),
    ]
    vectors = []
    for name, field, files in cases:
        fields = {}
        if field is not None:
            fields[LXMF.FIELD_IMAGE] = field
        if files:
            fields[LXMF.FIELD_FILE_ATTACHMENTS] = files
        message = LXMF.LXMessage(destination, destination, name, fields=fields)
        message.pack()
        attachment_root = work / name
        app = SimpleNamespace(attachmentpath=str(attachment_root))
        module.ConversationMessage.extract_attachments_from_lxm(message, app)
        directory = attachment_root / message.hash.hex()
        manifest_path = directory / "manifest"
        manifest = msgpack.unpackb(manifest_path.read_bytes(), raw=False) if manifest_path.exists() else {"files": []}
        normalized_format, normalized = module.ConversationMessage._unpack_media_field(field)
        host = SimpleNamespace(_attachment_dir=lambda: str(directory), get_fields=message.get_fields)
        host._read_attachment_manifest = MethodType(module.ConversationMessage._read_attachment_manifest, host)
        observations = []
        for index, entry in enumerate(manifest["files"]):
            actual_path = module.ConversationMessage.get_attachment_file_path(host, "file", index)
            data = Path(actual_path).read_bytes()
            observations.append({"name": entry["name"], "size": entry["size"],
                                 "sha256": hashlib.sha256(data).hexdigest()})
        vectors.append({
            "name": name, "imagePresent": LXMF.FIELD_IMAGE in fields,
            "imageFormat": normalized_format,
            "imageBytes": base64.b64encode(normalized).decode() if normalized is not None else None,
            "invalidPair": name == "invalid-pair",
            "files": [{"name": filename, "bytes": base64.b64encode(data).decode()} for filename, data in files],
            "attachments": observations,
        })
    result = {
        "commit": revision, "rnsVersion": RNS.__version__, "lxmfVersion": LXMF.__version__,
        "method": "Actual LXMessage pack/get_fields, ConversationMessage._unpack_media_field, extract_attachments_from_lxm and get_attachment_file_path with a data-only application host; Pillow-generated public pixels. Primary Conversations.py represents these as ClickableAttachment labels, not inline image widgets. Native conversation ASCII/policy is an original extension, not an upstream rendering contract.",
        "sourceSha256": {name: hashlib.sha256((source / "nomadnet" / name).read_bytes()).hexdigest()
                         for name in ("Conversation.py", "ui/textui/Conversations.py")},
        "vectors": vectors,
    }
    Path(sys.argv[1]).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
