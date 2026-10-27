"""Verify the actual reference code used by live interoperability harnesses."""
import importlib.metadata
import os
from pathlib import Path
import subprocess

PINNED_RNS_SHA = "a29bfb51c468945c96a1ee22cae0114e993e2391"
PINNED_LXMF_VERSION = "1.2.1"


def verify_reference(require_lxmf=False):
    import RNS

    root = Path(os.environ["DOTRETICULUM_UPSTREAM"]).resolve(strict=True)
    module = Path(RNS.__file__).resolve(strict=True)
    if not module.is_relative_to(root / "RNS"):
        raise RuntimeError(f"RNS imported from {module}, not {root / 'RNS'}")

    def git(*args):
        return subprocess.run(
            ["git", "-C", str(root), *args],
            check=True, capture_output=True, text=True,
        ).stdout.strip()

    revision = git("rev-parse", "HEAD")
    if revision != PINNED_RNS_SHA:
        raise RuntimeError(f"RNS revision {revision} does not match {PINNED_RNS_SHA}")
    if git("status", "--porcelain", "--untracked-files=no", "--", "RNS"):
        raise RuntimeError("Reference RNS source contains tracked modifications")

    if require_lxmf:
        import LXMF

        version = importlib.metadata.version("lxmf")
        distribution = importlib.metadata.distribution("lxmf")
        expected = Path(distribution.locate_file("LXMF")).resolve(strict=True)
        if not Path(LXMF.__file__).resolve(strict=True).is_relative_to(expected):
            raise RuntimeError(f"LXMF imported from an unexpected path: {LXMF.__file__}")
        if version != PINNED_LXMF_VERSION:
            raise RuntimeError(f"LXMF version {version} does not match {PINNED_LXMF_VERSION}")
