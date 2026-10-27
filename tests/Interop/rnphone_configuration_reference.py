"""Invoke primary rnphone configuration APIs on a constructor-free config host.

Constructor bypass avoids creating devices/network threads; actual ConfigObj,
apply_config, load_phonebook and menu functions execute unmodified.
"""
import argparse
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path

from reference_environment import verify_reference
import RNS
from RNS.vendor.configobj import ConfigObj
from LXST.Primitives.Telephony import Telephone
from LXST.Utilities.rnphone import ReticulumTelephone


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--identity-hash", required=True)
    parser.add_argument("--output")
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    host = ReticulumTelephone.__new__(ReticulumTelephone)
    host.service = False
    host.configdir = str(Path(args.config).parent)
    host.identity = type("IdentityHash", (), {"hash": bytes.fromhex(args.identity_hash)})()
    host.ringtone_path = host.speaker_device = host.microphone_device = host.ringer_device = None
    host.allow_phonebook = False
    host.allowed_list = []
    host.blocked_list = []
    host.allowed = Telephone.ALLOW_ALL
    host.phonebook = {}
    host.names = {}
    host.aliases = {}
    host.config = ConfigObj(args.config)
    with contextlib.redirect_stdout(io.StringIO()):
        host.apply_config()
        host.main_menu()
    allowed = "all" if host.allowed == Telephone.ALLOW_ALL else "none" if host.allowed == Telephone.ALLOW_NONE else "list"
    output = {
        "revision": "3b596f48ddd252ddc9c315ef8f3020cfd3688223",
        "source_sha256": hashlib.sha256((Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"]) / "LXST" / "Utilities" / "rnphone.py").read_bytes()).hexdigest(),
        "allowed": allowed,
        "allowed_hashes": [value.hex() for value in host.allowed_list],
        "blocked_hashes": [value.hex() for value in host.blocked_list],
        "phonebook": host.phonebook,
        "aliases": host.aliases,
        "menu": list(host.active_menu),
        "config": Path(args.config).read_text(),
    }
    if args.output:
        Path(args.output).write_text(json.dumps(output, indent=2) + "\n")
    else:
        print(json.dumps(output))


if __name__ == "__main__":
    main()
