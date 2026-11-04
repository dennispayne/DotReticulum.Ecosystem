"""Independent image decoding oracle, not an LXMF implementation."""
import importlib.metadata
import sys
from pathlib import Path

import PIL
from PIL import Image
import zxingcpp

for module in (PIL, zxingcpp):
    if not Path(module.__file__).resolve().is_relative_to(Path(sys.prefix).resolve()):
        raise RuntimeError("Independent QR oracle must import from the selected isolated environment")
if importlib.metadata.version("zxing-cpp") != "2.3.0":
    raise RuntimeError("Independent QR oracle requires zxing-cpp 2.3.0")
with Image.open(sys.argv[1]) as image:
    if image.width > 2048 or image.height > 2048 or image.width * image.height > 4 * 1024 * 1024:
        raise ValueError("QR image exceeds acceptance bounds")
    results = zxingcpp.read_barcodes(image.convert("RGB"))
if len(results) != 1:
    raise RuntimeError(f"Expected one QR symbol, observed {len(results)}")
print(results[0].text)
