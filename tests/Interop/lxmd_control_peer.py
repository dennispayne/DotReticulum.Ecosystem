"""Thin live adapter for official LXMF 1.2.1 propagation control APIs."""
import json
import os
import sys
import time

from reference_environment import verify_reference


def emit(value):
    print("@@" + json.dumps(value), flush=True)


def main():
    verify_reference(require_lxmf=True)
    import RNS
    import LXMF
    from LXMF.Utilities.lxmd import query_status

    rns_directory, storage, identity_path = sys.argv[1:4]
    RNS.Reticulum(configdir=rns_directory, loglevel=int(os.environ.get("DOTRETICULUM_LXMF_LOGLEVEL", RNS.LOG_WARNING)))
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
    router = LXMF.LXMRouter(identity=identity, storagepath=storage, autopeer="--autopeer" in sys.argv[4:], propagation_cost=13)
    local = router.register_delivery_identity(identity, display_name="lxmd control acceptance", stamp_cost=12)
    router.enable_propagation()
    router.propagation_destination.announce(app_data=router.get_propagation_node_app_data())
    router.control_destination.announce()
    emit({"event": "ready", "dest": router.propagation_destination.hash.hex(), "identity_hash": identity.hash.hex(),
          "delivery": local.hash.hex()})

    def connect(target):
        deadline = time.time() + 20
        if not RNS.Transport.has_path(target):
            RNS.Transport.request_path(target)
        while not RNS.Transport.has_path(target) and time.time() < deadline:
            time.sleep(0.05)
        remote = RNS.Identity.recall(target)
        if remote is None:
            raise TimeoutError("Propagation identity was not resolved")
        destination = RNS.Destination(remote, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "propagation")
        link = RNS.Link(destination)
        while link.status != RNS.Link.ACTIVE and time.time() < deadline:
            time.sleep(0.05)
        if link.status != RNS.Link.ACTIVE:
            link.teardown()
            raise TimeoutError("Propagation link was not established")
        link.identify(identity)
        return link, remote

    def request_offer(link, key, transient):
        request = link.request("/offer", data=[key, [transient]], timeout=15)
        deadline = time.time() + 20
        while request.get_status() != RNS.RequestReceipt.READY and time.time() < deadline:
            time.sleep(0.05)
        if request.get_status() != RNS.RequestReceipt.READY:
            raise TimeoutError("Propagation offer was not answered")
        return request.get_response()

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
                  "messages": response["messagestore"]["count"], "peers": response["total_peers"],
                  "peer_details": [{"dest": key.hex(), **{name: value for name, value in peer.items()
                                                        if name in ("alive", "last_sync_attempt", "sync_backoff", "messages", "type")}}
                                   for key, peer in response["peers"].items()]})
        elif command[0] == "local-status":
            emit({"event": "local-status", "messages": len(router.propagation_entries),
                  "ids": [key.hex() for key in router.propagation_entries],
                  "peers": [{"dest": key.hex(), "state": peer.state, "alive": peer.alive,
                             "unhandled": len(peer.unhandled_messages), "backoff": peer.sync_backoff,
                             "last_sync": peer.last_sync_attempt, "next_sync": peer.next_sync_attempt,
                             "last_heard": peer.last_heard, "peering_key_ready": peer.peering_key is not None}
                             for key, peer in router.peers.items()]})
        elif command[0] == "announce":
            router.announce_propagation_node()
            emit({"event": "announced"})
        elif command[0] == "bad-stamp":
            import LXMF.LXStamper as stamper
            import RNS.vendor.umsgpack as msgpack
            from LXMF.LXMPeer import LXMPeer
            target = bytes.fromhex(command[1])
            link, remote = connect(target)
            retry = None
            try:
                config = msgpack.unpackb(RNS.Identity.recall_app_data(target))
                key, _ = stamper.generate_stamp(remote.hash + identity.hash, config[5][2],
                                                expand_rounds=stamper.WORKBLOCK_EXPAND_ROUNDS_PEERING)
                destination = RNS.Destination(RNS.Identity(), RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
                message = LXMF.LXMessage(destination, local, os.urandom(1024), "invalid-stamp-rejection",
                                         desired_method=LXMF.LXMessage.PROPAGATED)
                message.pack()
                encrypted = msgpack.unpackb(message.propagation_packed)[1][0]
                workblock = stamper.stamp_workblock(message.transient_id, expand_rounds=stamper.WORKBLOCK_EXPAND_ROUNDS_PN)
                invalid = bytes(32)
                while stamper.stamp_valid(invalid, config[5][0] - config[5][1], workblock):
                    invalid = os.urandom(32)
                offered = request_offer(link, key, message.transient_id)
                if offered is not True:
                    raise AssertionError(f"Native node did not accept valid peer offer: {offered}")
                resource = RNS.Resource(msgpack.packb([time.time(), [encrypted + invalid]]), link)
                deadline = time.time() + 30
                while link.status != RNS.Link.CLOSED and time.time() < deadline:
                    time.sleep(0.05)
                if link.status != RNS.Link.CLOSED:
                    raise AssertionError("Invalid propagation stamp did not close the identified link")
                retry, _ = connect(target)
                response = request_offer(retry, key, message.transient_id)
                emit({"event": "bad-stamp-rejected", "response": response,
                      "expected": LXMPeer.ERROR_THROTTLED, "resource_status": resource.status})
            finally:
                link.teardown()
                if retry is not None:
                    retry.teardown()
        elif command[0] in ("seed", "send"):
            offline = RNS.Identity()
            destination = RNS.Destination(offline, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
            seed = command[0] == "seed"
            title = command[1] if seed else command[2]
            size = int(command[2] if seed else command[3])
            if size > 4_000_000:
                raise ValueError("Adapter payload exceeds 4 MB")
            message = LXMF.LXMessage(destination, local, os.urandom(size), title, desired_method=LXMF.LXMessage.PROPAGATED)
            if seed:
                import RNS.vendor.umsgpack as msgpack
                message.pack()
                encrypted = msgpack.unpackb(message.propagation_packed)[1][0]
                stamp = message.get_propagation_stamp(13)
                if not router.lxmf_propagation(encrypted, stamp_value=message.propagation_stamp_value, stamp_data=stamp):
                    raise AssertionError("Reference rejected its generated propagated message")
                emit({"event": "seeded", "hash": message.hash.hex(), "transient": message.transient_id.hex()})
            else:
                router.set_outbound_propagation_node(bytes.fromhex(command[1]))
                message.register_delivery_callback(lambda current: emit({"event": "sent", "hash": current.hash.hex()}))
                message.register_failed_callback(lambda current: emit({"event": "failed", "hash": current.hash.hex()}))
                router.handle_outbound(message)
                emit({"event": "queued", "hash": message.hash.hex()})
        else:
            raise ValueError(f"Unsupported adapter command: {command[0]}")


if __name__ == "__main__":
    main()
