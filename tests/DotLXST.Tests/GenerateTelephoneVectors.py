"""Execute primary Telephone caller-policy and profile APIs without audio I/O."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference

import RNS
from LXST.Primitives.Telephony import Profiles, Telephone


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    root = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"])
    config = Path("artifacts") / ("lxst-telephone-vectors-" + uuid.uuid4().hex)
    config.mkdir(parents=True)
    (config / "config").write_text("[reticulum]\n  share_instance = No\n[logging]\n  loglevel = 0\n[interfaces]\n")
    RNS.Reticulum(configdir=str(config), loglevel=RNS.LOG_CRITICAL)
    phone = Telephone(RNS.Identity())
    caller = RNS.Identity()
    other = RNS.Identity()
    policies = []
    try:
        for allowed_name, allowed in [
                ("all", Telephone.ALLOW_ALL), ("none", Telephone.ALLOW_NONE),
                ("list-match", [caller.hash]), ("list-other", [other.hash]),
                ("callback-match", lambda identity_hash: identity_hash == caller.hash),
                ("callback-other", lambda identity_hash: identity_hash == other.hash)]:
            phone.set_allowed(allowed)
            for blocked in [False, True]:
                phone.set_blocked([caller.hash] if blocked else None)
                policies.append({"allowed": allowed_name, "blocked": blocked,
                                 "result": bool(phone._Telephone__is_allowed(caller))})
        profiles = []
        for profile in Profiles.available_profiles():
            codec = Profiles.get_codec(profile)
            profiles.append({
                "profile": profile, "name": Profiles.profile_name(profile),
                "abbreviation": Profiles.profile_abbrevation(profile),
                "index": Profiles.profile_index(profile),
                "next": Profiles.next_profile(profile),
                "frame_ms": Profiles.get_frame_time(profile),
                "codec": type(codec).__name__,
            })
        output = {
            "revision": "3b596f48ddd252ddc9c315ef8f3020cfd3688223",
            "telephony_sha256": hashlib.sha256((root / "LXST" / "Primitives" / "Telephony.py").read_bytes()).hexdigest(),
            "policies": policies, "profiles": profiles,
        }
        Path(args.output).write_text(json.dumps(output, indent=2) + "\n")
    finally:
        phone.teardown()
        RNS.Reticulum.exit_handler()
        shutil.rmtree(config)


if __name__ == "__main__":
    main()
