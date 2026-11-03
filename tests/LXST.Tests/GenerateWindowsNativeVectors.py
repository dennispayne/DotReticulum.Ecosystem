"""Execute unchanged pinned APIs with explicitly selected independent Windows DLLs.

The only shim is native binding selection. No Python codec/source method is
replaced. This is not a claim about the reference's bundled native binaries.
"""
import argparse
import base64
import ctypes
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

import numpy as np


def pcm_observation(samples):
    observed = np.asarray(samples, dtype=np.float32).flatten()
    pcm = np.rint(observed * np.float32(32767)).astype("<i2")
    if not np.array_equal(observed, pcm.astype(np.float32) / np.float32(32767)):
        raise RuntimeError("Observed normalized PCM cannot be recorded losslessly as signed 16-bit samples")
    return {"pcm_s16le": base64.b64encode(pcm.tobytes()).decode()}


def rebind(module, name, path):
    original = getattr(module, name)
    replacement = ctypes.CDLL(str(path))
    for symbol, function in vars(original).items():
        if isinstance(function, ctypes._CFuncPtr):
            target = getattr(replacement, symbol, None)
            if target is None:
                continue
            target.argtypes = function.argtypes
            target.restype = function.restype
    setattr(module, name, replacement)
    return replacement


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--libraries", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    if sys.platform != "win32":
        raise RuntimeError("This observation requires actual Windows")
    native = Path(args.libraries).resolve(strict=True)
    directory = os.add_dll_directory(str(native))
    codec2_handle = ctypes.CDLL(str(native / "libcodec2.dll"))
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
    from reference_environment import PINNED_LXST_SHA, verify_reference
    verify_reference(require_lxst=True)
    from LXST.Codecs import Opus, Codec2
    from LXST.Codecs.libs.pyogg import opus
    libopus = rebind(opus, "libopus", native / "libopus-0.dll")
    rebind(opus, "libopusfile", native / "libopusfile-0.dll")
    libopus.opus_get_version_string.restype = ctypes.c_char_p
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetModuleFileNameW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_uint]
    kernel.GetModuleFileNameW.restype = ctypes.c_uint
    module_path = ctypes.create_unicode_buffer(32768)
    if not kernel.GetModuleFileNameW(codec2_handle._handle, module_path, len(module_path)):
        raise ctypes.WinError()
    result = {
        "revision": PINNED_LXST_SHA,
        "platform": sys.platform,
        "python": sys.version,
        "pycodec2": importlib.metadata.version("pycodec2"),
        "libopus": libopus.opus_get_version_string().decode(),
        "codec2_loaded_path": module_path.value,
        "shim": "Only native binding selection: preload independent libcodec2 before importing pycodec2; rebind ctypes libopus/libopusfile with original ABI declarations. Unchanged actual LXST encode/decode/file methods execute.",
        "libraries": {name: hashlib.sha256((native / name).read_bytes()).hexdigest()
                      for name in ["libopus-0.dll", "libcodec2.dll", "libopusfile-0.dll"]},
        "source_sha256": {}, "opus": [], "codec2": [],
    }
    root = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"])
    for name in ["Codecs/Opus.py", "Codecs/Codec2.py", "Sources.py"]:
        result["source_sha256"][name] = hashlib.sha256((root / "LXST" / name).read_bytes()).hexdigest()
    fixture_path = Path(__file__).parent / "Fixtures" / "lxst-vectors.json"
    # Use the exact already recorded reference inputs, not a second waveform implementation.
    baseline = json.loads(fixture_path.read_text(encoding="utf-8-sig"))
    result["baseline_sha256"] = hashlib.sha256(fixture_path.read_text(encoding="utf-8-sig").encode()).hexdigest()
    for row in baseline["opus"]:
        codec = Opus(profile=row["profile"])
        codec.source = SimpleNamespace(samplerate=row["sample_rate"])
        codec.sink = SimpleNamespace(samplerate=row["sample_rate"], channels=row["channels"])
        frame = np.array(row["input"], dtype=np.float32).reshape(-1, row["channels"])
        encoded = codec.encode(frame)
        if encoded.hex() != row["encoded"]:
            raise RuntimeError("Windows reference bitstream differs")
        result["opus"].append(pcm_observation(codec.decode(encoded)))
    for row in baseline["codec2"]:
        codec = Codec2(mode=row["mode"])
        frame = np.repeat(np.array(row["input"], dtype=np.float32).reshape(-1, 1), 2, axis=1)
        encoded = codec.encode(frame)
        if encoded.hex() != row["encoded"]:
            raise RuntimeError("Windows Codec2 bitstream differs")
        result["codec2"].append(pcm_observation(codec.decode(encoded)))
    from GenerateLxstFileVectors import read_actual_file
    file_fixture_path = Path(__file__).parent / "Fixtures" / "lxst-file-vectors.json"
    result["file_baseline_sha256"] = hashlib.sha256(file_fixture_path.read_text(encoding="utf-8-sig").encode()).hexdigest()
    file_fixture = json.loads(file_fixture_path.read_text())
    path = Path(args.output).parent / "windows-reference.opus"
    path.write_bytes(base64.b64decode(file_fixture["file_base64"]))
    try:
        result["file"] = read_actual_file(path)
        result["file"].update(pcm_observation(result["file"].pop("decoded")))
    finally:
        path.unlink()
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")
    directory.close()


if __name__ == "__main__":
    main()
