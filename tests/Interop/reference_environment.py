"""Verify the actual reference code used by live interoperability harnesses."""
import argparse
import fnmatch
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import subprocess
import sysconfig

PINNED_RNS_SHA = "a29bfb51c468945c96a1ee22cae0114e993e2391"
PINNED_LXMF_VERSION = "1.2.1"
PINNED_LXST_SHA = "721b2eee6dd37606e423914a4937a11a3c3a761f"
PINNED_LXST_VERSION = "0.4.5"
# PyPI wheel SHA-256: 4bd95325b00e5fae81241e28f26a5ed22b38fea941e128d1c63a33c99e931440.
# The GitHub mirror does not publish a 1.2.1 tag; do not relabel its 1.2.0 HEAD.
PINNED_LXMF_SOURCES = {
    "__init__.py": "03d60f351b9c23f79d69c42463d98b5a4796d7840624cf2f71c02a6bf648eeb1",
    "_version.py": "3259b81af99bffac90c7051bbf62ac73e0491572cf83d1f556dda257bc17ac0e",
    "Handlers.py": "8cfdd0b3681694f5ff1b2b2b0aa2a215ee52c6c36f69052b31468f43d7dde23c",
    "LXMessage.py": "2fe8a1cb610e70f3d1c0b4f5086f660a45607484ce8dd53bd925943e95398aea",
    "LXMF.py": "0e56a16b713d08224d6fec9826c1c7c96cd494add52f2657919bfe39afeae251",
    "LXMPeer.py": "64b066894d854734ac4c1238ebef74cca3060a2fddc88c27a7793c84beb525ef",
    "LXMRouter.py": "56b6a8e6b9f79256c4ae297340791eebcc1b6cda3cec04ab2dbe2f1efb53caa9",
    "LXStamper.py": "eeeba0158546d2e9878ca485ffa4b96dd13ce3e71880d784087c1fdae22538d0",
    "Utilities/lxmd.py": "8aa3696376baae4a4d9135fcdab3f81f92203275516b60c3ac1b269529851ef2",
}


def _verify_checkout(module, root, package, revision, allow_filter_build=False):
    root = Path(root).resolve(strict=True)
    imported = Path(module.__file__).resolve(strict=True)
    if not imported.is_relative_to(root / package):
        raise RuntimeError(f"{package} imported from {imported}, not {root / package}")
    def git(*args):
        return subprocess.run(
            ["git", "-C", str(root), *args],
            check=True, capture_output=True, text=True,
        ).stdout.strip()

    actual = git("rev-parse", "HEAD")
    if actual != revision:
        raise RuntimeError(f"{package} revision {actual} does not match {revision}")
    if git("status", "--porcelain", "--untracked-files=no", "--", package):
        raise RuntimeError(f"Reference {package} source contains modifications or untracked files")
    for path in git("ls-files", "--others", "--exclude-standard", "-z", "--", package).split("\0"):
        if not path:
            continue
        # Unmodified LXST builds its CFFI filter during installation/import.
        if allow_filter_build and (
            path == f"{package}/filterlib{sysconfig.get_config_var('EXT_SUFFIX')}"
            or fnmatch.fnmatchcase(path, f"{package}/__pycache__/_cffi__*.c")
        ):
            continue
        raise RuntimeError(f"Reference {package} source contains modifications or untracked files: {path}")


def verify_reference(require_lxmf=False, require_lxst=False):
    import RNS

    _verify_checkout(RNS, os.environ["DOTRETICULUM_UPSTREAM"], "RNS", PINNED_RNS_SHA)
    if RNS.__version__ != "1.5.7":
        raise RuntimeError(f"RNS module version {RNS.__version__} does not match 1.5.7")

    if require_lxmf:
        import LXMF

        version = importlib.metadata.version("lxmf")
        distribution = importlib.metadata.distribution("lxmf")
        expected = Path(distribution.locate_file("LXMF")).resolve(strict=True)
        if not Path(LXMF.__file__).resolve(strict=True).is_relative_to(expected):
            raise RuntimeError(f"LXMF imported from an unexpected path: {LXMF.__file__}")
        if version != PINNED_LXMF_VERSION:
            raise RuntimeError(f"LXMF version {version} does not match {PINNED_LXMF_VERSION}")
        sources = {path.relative_to(expected).as_posix() for path in expected.rglob("*.py")}
        if sources != PINNED_LXMF_SOURCES.keys():
            raise RuntimeError("LXMF source file inventory does not match the pinned wheel")
        for name, digest in PINNED_LXMF_SOURCES.items():
            if hashlib.sha256((expected / name).read_bytes()).hexdigest() != digest:
                raise RuntimeError(f"LXMF source {name} does not match the pinned wheel")

    if require_lxst:
        import LXST
        from LXST._version import __version__

        _verify_checkout(
            LXST, os.environ["DOTRETICULUM_LXST_UPSTREAM"], "LXST", PINNED_LXST_SHA,
            allow_filter_build=True,
        )
        if __version__ != PINNED_LXST_VERSION:
            raise RuntimeError(f"LXST version {__version__} does not match {PINNED_LXST_VERSION}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--require-lxmf", action="store_true")
    parser.add_argument("--require-lxst", action="store_true")
    args = parser.parse_args()
    verify_reference(require_lxmf=args.require_lxmf, require_lxst=args.require_lxst)
    import RNS

    result = {"rns": str(Path(RNS.__file__).resolve()), "rns_revision": PINNED_RNS_SHA}
    if args.require_lxmf:
        import LXMF

        result["lxmf"] = str(Path(LXMF.__file__).resolve())
        result["lxmf_version"] = PINNED_LXMF_VERSION
        result["lxmf_distribution"] = str(
            Path(importlib.metadata.distribution("lxmf").locate_file("")).resolve()
        )
    if args.require_lxst:
        import LXST

        result["lxst"] = str(Path(LXST.__file__).resolve())
        result["lxst_revision"] = PINNED_LXST_SHA
        result["lxst_version"] = PINNED_LXST_VERSION
    print(json.dumps(result))
