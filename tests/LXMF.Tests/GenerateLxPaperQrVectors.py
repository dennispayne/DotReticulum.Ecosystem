"""Execute pinned LXMessage.as_qr using only the existing synthetic paper fixture.

PYTHONPATH must select pinned RNS and tests/Interop. No crypto/QR shims are used.
Destination registration is suppressed (no running Reticulum singleton).
Optional --decode-png PATH executes the independent zxing-cpp reference decoder,
not qrcode (which is only an encoder), and Pillow's real PNG reader.
"""
import argparse
import base64
import hashlib
import importlib.metadata
import inspect
import json
from pathlib import Path
import zlib

from reference_environment import verify_reference, PINNED_LXMF_SHA, PINNED_RNS_SHA

verify_reference(require_lxmf=True)
import LXMF
import RNS
import qrcode
from PIL import Image

parser = argparse.ArgumentParser()
parser.add_argument("--decode-png", type=Path)
args = parser.parse_args()
here = Path(__file__).resolve().parent
fixture = json.loads((here / "Fixtures" / "lxmf-contract-vectors.json").read_text())
uri = fixture["paper"]["uri"]

if args.decode_png:
    import zxingcpp
    image = Image.open(args.decode_png)
    result = zxingcpp.read_barcode(image)
    assert result is not None and result.text == uri
    print(json.dumps({"decoder": "zxing-cpp",
                      "decoder_version": importlib.metadata.version("zxing-cpp"),
                      "decoder_binary_sha256": hashlib.sha256(Path(zxingcpp.__file__).read_bytes()).hexdigest(),
                      "pillow_version": importlib.metadata.version("pillow"),
                      "png_sha256": hashlib.sha256(args.decode_png.read_bytes()).hexdigest(),
                      "width": image.width, "height": image.height,
                      "uri_matches_synthetic_fixture": True}))
else:
    RNS.Transport.register_destination = staticmethod(lambda destination: None)
    source = RNS.Identity(create_keys=False)
    source.load_private_key(bytes.fromhex(fixture["source_private_key"]))
    destination = RNS.Identity(create_keys=False)
    destination.load_private_key(bytes.fromhex(fixture["destination_private_key"]))
    src = RNS.Destination(source, RNS.Destination.IN, RNS.Destination.SINGLE, "lxmf", "delivery")
    dst = RNS.Destination(destination, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    message = LXMF.LXMessage(dst, src, "Paper Grüße", "P", desired_method=LXMF.LXMessage.PAPER)
    # Reuse the previously reference-generated encrypted observation, not fresh random ciphertext.
    message.packed = bytes.fromhex(fixture["paper"]["packed"])
    message.paper_packed = bytes.fromhex(fixture["paper"]["paper_packed"])
    message.method = LXMF.LXMessage.PAPER
    image = message.as_qr()
    assert message.as_uri(finalise=False) == uri
    gray = image.convert("L")
    output = here / "Fixtures"
    image.save(output / "lx-paper-qr.png")
    (output / "lx-paper-qr.gray.zlib").write_bytes(zlib.compress(gray.tobytes(), 9))
    qr = qrcode.QRCode(error_correction=qrcode.constants.ERROR_CORRECT_L, border=1)
    qr.add_data(uri)
    qr.make()
    assert qr.make_image().convert("L").tobytes() == gray.tobytes()
    failures = {}
    original = message.paper_packed
    for size in (2210, 2211, 2300):
        message.paper_packed = (original * ((size + len(original) - 1) // len(original)))[:size]
        try:
            result = message.as_qr()
            failures[str(size)] = {"result": "success", "width": result.size[0]}
        except Exception as error:
            failures[str(size)] = {"error": type(error).__name__}
    source_file = Path(inspect.getfile(LXMF.LXMessage))
    record = {
        "lxmf_sha": PINNED_LXMF_SHA, "rns_sha": PINNED_RNS_SHA,
        "lxmf_version": importlib.metadata.version("lxmf"),
        "lxmessage_source_sha256": hashlib.sha256(source_file.read_bytes()).hexdigest(),
        "method": "LXMF.LXMessage.as_qr", "qrcode_version": importlib.metadata.version("qrcode"),
        "qrcode_source_sha256": hashlib.sha256(Path(inspect.getfile(qrcode.QRCode)).read_bytes()).hexdigest(),
        "pillow_version": importlib.metadata.version("pillow"),
        "error_correction": "L", "border": qr.border, "box_size": qr.box_size,
        "version": qr.version, "uri": uri, "width": gray.width, "height": gray.height,
        "gray_sha256": hashlib.sha256(gray.tobytes()).hexdigest(),
        "png_sha256": hashlib.sha256((output / "lx-paper-qr.png").read_bytes()).hexdigest(),
        "modules": ["".join("1" if pixel else "0" for pixel in row) for row in qr.get_matrix()],
        "capacity": failures,
        "capacity_method": "as_qr on already-packed synthetic message, repeating existing encrypted bytes; not decryptable message probes",
        "reference_shim": "Destination registration suppressed; no transport instance, crypto or QR replacements",
    }
    (output / "lx-paper-qr.json").write_text(json.dumps(record, indent=2) + "\n")
    print(json.dumps({key: value for key, value in record.items() if key != "modules"}))
