"""Execute primary Conversation.paper_output(save_qr), decoding the actual PNG independently."""
import hashlib
import importlib
import importlib.metadata
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
    import RNS
    import LXMF
    import zxingcpp
    from PIL import Image
    module = importlib.import_module("nomadnet.Conversation")
    if Path(module.__file__).resolve() != source / "nomadnet/Conversation.py":
        raise RuntimeError("Wrong imported primary conversation")
    work = Path("artifacts/tests/nomadnet-qr-reference").resolve()
    work.mkdir(parents=True, exist_ok=True)
    recipient = RNS.Destination(RNS.Identity(), RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    sender = RNS.Destination(RNS.Identity(), RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    ingested = []
    original = module.Conversation.ingest

    def observe_ingest(message, app, originator):
        ingested.append({"originator": originator, "hash": message.hash.hex(), "method": message.method})
        return str(work / ("observed-" + message.hash.hex()))

    state = SimpleNamespace(ensure_send_destination=lambda: True, send_destination=recipient,
                            app=SimpleNamespace(lxmf_destination=sender, downloads_path=str(work)), messages=[])
    module.Conversation.ingest = staticmethod(observe_ingest)
    try:
        output = module.Conversation.paper_output(state, content="Generated NomadNet QR software acceptance",
                                                 title="Generated", mode="save_qr")
    finally:
        module.Conversation.ingest = staticmethod(original)
    if not isinstance(output, str) or not Path(output).is_file():
        raise RuntimeError("Actual primary paper_output did not save a QR PNG")
    image = Image.open(output).convert("L")
    decoded = zxingcpp.read_barcode(image)
    if decoded is None or not decoded.text.startswith("lxm://"):
        raise RuntimeError("Independent actual zxing-cpp did not decode the primary PNG")
    destination = Path(sys.argv[1])
    destination.with_suffix(".png").write_bytes(Path(output).read_bytes())
    result = {"commit": revision,
              "sourceSha256": hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest(),
              "method": "Actual Conversation.paper_output(save_qr), real RNS identities/LXMF crypto/as_qr and PNG. OUT destinations avoid service registration; only Conversation.ingest is an observation callback to avoid unrelated conversation persistence. Actual zxing-cpp independently decodes PNG; qrcode is not a decoder. No live/printer/camera claim; no private identity material recorded.",
              "uri": decoded.text, "width": image.width, "height": image.height,
              "paperCreationMethod": LXMF.LXMessage.PAPER,
              "paperIngressMethod": LXMF.LXMessage.PROPAGATED,
              "ingressContract": "LXMRouter.py2659-2670 ingest_lxm_uri invokes lxmf_propagation(is_paper_message=True); 2624 explicitly dispatches local delivery as LXMessage.PROPAGATED. Constants executed here; complete ingress separately exercised by actual application acceptance.",
              "lxmfRouterSha256": hashlib.sha256(Path(importlib.import_module("LXMF.LXMRouter").__file__).read_bytes()).hexdigest(),
              "rnsVersion": RNS.__version__, "lxmfVersion": LXMF.__version__,
              "decoderVersion": importlib.metadata.version("zxing-cpp"), "pillowVersion": importlib.metadata.version("Pillow"),
              "pngSha256": hashlib.sha256(Path(output).read_bytes()).hexdigest(),
              "ingested": ingested, "messageCount": len(state.messages)}
    destination.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    Path(output).unlink()


if __name__ == "__main__":
    main()
