"""Observe actual primary Browser image policy without starting Urwid or replacing policy methods."""
import hashlib
import importlib
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
    import RNS
    from nomadnet.ui import TextUI
    module = importlib.import_module("nomadnet.ui.textui.Browser")
    if Path(module.__file__).resolve() != source / "nomadnet/ui/textui/Browser.py":
        raise RuntimeError("Unexpected imported primary Browser")
    RNS.loglevel = 0
    cases = [
        ("local-no-link", True, False, None, None, None, None),
        ("remote-no-link", False, False, None, None, None, None),
        ("missing-rtt", False, True, None, 10001, None, None),
        ("zero-rtt", False, True, 0, 10001, None, None),
        ("rtt-below", False, True, 1.499999, 10001, None, None),
        ("rtt-boundary", False, True, 1.5, 10001, None, None),
        ("rate-boundary", False, True, 0.1, 10000, None, None),
        ("rate-above", False, True, 0.1, 10001, None, None),
        ("fallback-rate-boundary", False, True, 0.1, None, 1500, 1.2),
        ("fallback-rate-above", False, True, 0.1, None, 1501, 1.2),
        ("zero-rate-fallback", False, True, 0.1, 0, 1501, 1.2),
        ("no-rate", False, True, 0.1, None, None, None),
        ("zero-size", False, True, 0.1, None, 0, 1),
        ("zero-time", False, True, 0.1, None, 2000, 0),
        ("minimum-time-clamp", False, True, 0.1, None, 1, 0.000001),
    ]
    modes = {"never": TextUI.IMGLOAD_NEVER, "manual": TextUI.IMGLOAD_MANUAL,
             "auto": TextUI.IMGLOAD_AUTO, "always": TextUI.IMGLOAD_ALWAYS}
    vectors = []
    for mode_name, mode in modes.items():
        for name, local, linked, rtt, rate, size, seconds in cases:
            host = SimpleNamespace(image_mode=mode, loopback=b"\x11" * 16,
                                   destination_hash=(b"\x11" if local else b"\x22") * 16,
                                   link=SimpleNamespace(rtt=rtt, get_expected_rate=lambda: rate) if linked else None,
                                   response_transfer_size=size, response_time=seconds)
            host.last_response_speed = MethodType(module.Browser.last_response_speed, host)
            vectors.append({"name": name, "mode": mode_name, "value": mode, "local": local,
                            "linked": linked, "rtt": rtt, "expectedRate": rate,
                            "transferBytes": size, "responseSeconds": seconds,
                            "fallbackRate": host.last_response_speed(),
                            "load": bool(module.Browser.should_load_images(host))})
    result = {"commit": revision, "sourceSha256": hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest(),
              "method": "Actual Browser.should_load_images and last_response_speed via a data-only host; no policy implementation replacement, screen, network or codec. Rendering capability is a separate upstream Kitty check; native portable ASCII capability is explicitly not Kitty parity.",
              "modes": modes, "vectors": vectors}
    Path(sys.argv[1]).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
