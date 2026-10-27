"""Execute ConfigObj and lxmd.apply_config at primary LXMF 1.2.1 e8ba02483217af43be284a72f1c059c730b7d047."""
import json
import os
import sys

from reference_environment import verify_reference


def main():
    verify_reference(require_lxmf=True)
    from LXMF.Utilities import lxmd
    from RNS.vendor.configobj import ConfigObj

    lxmd.lxmd_config = ConfigObj(sys.argv[1])
    lxmd.ignoredpath = os.path.join(os.path.dirname(sys.argv[1]), "ignored")
    lxmd.allowedpath = os.path.join(os.path.dirname(sys.argv[1]), "allowed")
    lxmd.active_configuration = {}
    lxmd.apply_config()
    print(json.dumps(lxmd.active_configuration, default=lambda value: value.hex() if isinstance(value, bytes) else str(value)))


if __name__ == "__main__":
    main()
