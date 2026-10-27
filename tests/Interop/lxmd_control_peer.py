"""Thin live adapter for official LXMF 1.2.1 propagation control APIs."""
import json
import os
import sys

from reference_environment import verify_reference


def emit(value):
    print("@@" + json.dumps(value), flush=True)


def main():
    verify_reference(require_lxmf=True)
    import RNS
    import LXMF
    from LXMF.Utilities.lxmd import query_status

    rns_directory, storage, identity_path = sys.argv[1:]
    RNS.Reticulum(configdir=rns_directory, loglevel=RNS.LOG_WARNING)
    class PropagationHandler:
        aspect_filter = "lxmf.propagation"

        def received_announce(self, destination_hash, announced_identity, app_data):
            emit({"event": "pn-announce", "dest": destination_hash.hex()})

    RNS.Transport.register_announce_handler(PropagationHandler())
    identity = RNS.Identity()
    descriptor = os.open(identity_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    os.close(descriptor)
    if not identity.to_file(identity_path):
        raise RuntimeError("Could not save reference control identity")
    router = LXMF.LXMRouter(identity=identity, storagepath=storage, autopeer=False)
    router.register_delivery_identity(identity, display_name="lxmd control acceptance", stamp_cost=12)
    router.enable_propagation()
    router.propagation_destination.announce(app_data=router.get_propagation_node_app_data())
    router.control_destination.announce()
    emit({"event": "ready", "dest": router.propagation_destination.hash.hex(), "identity_hash": identity.hash.hex()})
    while True:
        line = sys.stdin.buffer.raw.readline(65_537)
        if not line:
            break
        if len(line) > 65_536:
            raise ValueError("Adapter command exceeds 64 KiB")
        command = line.decode("utf-8").strip().split()
        if not command:
            continue
        if command[0] == "quit":
            break
        if command[0] == "status":
            target = bytes.fromhex(command[1])
            remote = RNS.Identity.recall(target)
            if remote is None:
                raise RuntimeError("Native propagation identity not learned from its announce")
            response = query_status(identity, remote_identity=remote, timeout=15)
            if not isinstance(response, dict):
                raise AssertionError(f"Native control statistics failed: {response}")
            emit({"event": "status", "dest": response["destination_hash"].hex(),
                  "messages": response["messagestore"]["count"], "peers": response["total_peers"]})
        else:
            raise ValueError(f"Unsupported adapter command: {command[0]}")


if __name__ == "__main__":
    main()
