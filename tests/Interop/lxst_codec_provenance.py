"""Observe actual reference imports and native codec versions, without patches."""
import argparse
import ctypes
import hashlib
import importlib.metadata
import json
from pathlib import Path
import sys

from reference_environment import verify_reference


def describe(path):
    path = Path(path).resolve(strict=True)
    return dict(path=str(path), sha256=hashlib.sha256(path.read_bytes()).hexdigest())


def opus_version(library):
    library.opus_get_version_string.restype = ctypes.c_char_p
    library.opus_get_version_string.argtypes = []
    return library.opus_get_version_string().decode("ascii")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-directory", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    import RNS
    import LXST
    import pycodec2
    from LXST.Codecs.libs.pyogg import opus
    from LXST._version import __version__ as lxst_version

    native = Path(args.native_directory).resolve(strict=True)
    reference_opus = describe(opus.libopus._name)
    reference_opus["version"] = opus_version(opus.libopus)
    native_opus = describe(native / "libopus-0.dll")
    native_opus["version"] = opus_version(ctypes.CDLL(str(native / "libopus-0.dll")))
    result = dict(
        interpreter=describe(sys.executable),
        rns=dict(**describe(RNS.__file__), version=RNS.__version__),
        lxst=dict(**describe(LXST.__file__), version=lxst_version),
        reference_opus=reference_opus, native_opus=native_opus,
        reference_codec2=dict(**describe(Path(pycodec2.__file__).parent / "libcodec2.dll"),
                              wrapper_version=importlib.metadata.version("pycodec2"),
                              codec_version_query="No assumed or invented codec2 version ABI; exact loaded DLL hash and wrapper distribution recorded."),
        native_codec2=dict(**describe(native / "libcodec2.dll"),
                           package_version="MSYS2 UCRT64 codec2 1.2.0-2; package provenance in docs/lxst.md"),
        boundary="Actual upstream internal Windows Opus selection is retained; no DLL copied or source/loader patch applied.",
    )
    print("@@" + json.dumps(result))


if __name__ == "__main__":
    main()
