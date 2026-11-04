"""Execute pinned LXMF's announce provider after changing its destination name."""

import hashlib
import inspect
import json
import pathlib
import sys
import types

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "Interop"))
from reference_environment import PINNED_LXMF_SHA, verify_reference


def main():
    verify_reference(require_lxmf=True)
    import LXMF

    source = pathlib.Path(inspect.getfile(LXMF.LXMRouter))
    destination_hash = bytes(16)
    destination = types.SimpleNamespace(display_name=None, stamp_cost=13)
    owner = types.SimpleNamespace(delivery_destinations={destination_hash: destination})
    vectors = []
    for name in (None, "Initial", "Renamed", "", "Gr\u00fc\u00dfe", "\U0001f30d", None):
        destination.display_name = name
        vectors.append(
            {
                "name": name,
                "stamp_cost": destination.stamp_cost,
                "app_data": LXMF.LXMRouter.get_announce_app_data(owner, destination_hash).hex(),
            }
        )
    destination.display_name = "\ud800"
    try:
        LXMF.LXMRouter.get_announce_app_data(owner, destination_hash)
    except UnicodeEncodeError:
        invalid_name_error = "UnicodeEncodeError"
    else:
        raise AssertionError("Expected actual primary strict UTF-8 rejection")
    result = {
        "revision": PINNED_LXMF_SHA,
        "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "method": "Actual LXMRouter.get_announce_app_data with synthetic destination/owner; sequential display_name mutation, no network or router lifecycle",
        "actual_imports_verified": True,
        "vectors": vectors,
        "invalid_name_error": invalid_name_error,
    }
    output = HERE / "Fixtures" / "lxmf-display-name-vectors.json"
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(output)


if __name__ == "__main__":
    main()
