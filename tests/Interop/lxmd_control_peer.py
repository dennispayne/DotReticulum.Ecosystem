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
            import RNS.vendor.umsgpack as msgpack
            config = msgpack.unpackb(app_data)
            emit({"event": "pn-announce", "dest": destination_hash.hex(), "transfer_limit": config[3],
                  "sync_limit": config[4]})

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

    def connect(target, sender=None, aspect="propagation"):
        deadline = time.time() + 20
        if not RNS.Transport.has_path(target):
            RNS.Transport.request_path(target)
        while not RNS.Transport.has_path(target) and time.time() < deadline:
            time.sleep(0.05)
        remote = RNS.Identity.recall(target)
        if remote is None:
            raise TimeoutError("Propagation identity was not resolved")
        destination = RNS.Destination(remote, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", aspect)
        link = RNS.Link(destination)
        while link.status != RNS.Link.ACTIVE and time.time() < deadline:
            time.sleep(0.05)
        if link.status != RNS.Link.ACTIVE:
            link.teardown()
            raise TimeoutError("Propagation link was not established")
        link.identify(sender or identity)
        return link, remote

    def request_offer(link, key, transient):
        ids = transient if isinstance(transient, list) else [transient]
        request = link.request("/offer", data=[key, ids], timeout=15)
        deadline = time.time() + 20
        while request.get_status() != RNS.RequestReceipt.READY and time.time() < deadline:
            time.sleep(0.05)
        if request.get_status() != RNS.RequestReceipt.READY:
            raise TimeoutError("Propagation offer was not answered")
        return request.get_response()

    throttled = None
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
        elif command[0] == "unstamped":
            target = bytes.fromhex(command[1])
            link, remote = connect(target, aspect="delivery")
            try:
                destination = RNS.Destination(remote, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
                message = LXMF.LXMessage(destination, local, b"unstamped delivery", "unstamped", desired_method=LXMF.LXMessage.DIRECT)
                message.pack()
                if message.stamp is not None:
                    raise AssertionError("Reference unexpectedly stamped the raw direct message")
                receipt = RNS.Packet(link, message.packed).send()
                deadline = time.monotonic() + 20
                while receipt.status == RNS.PacketReceipt.SENT and time.monotonic() < deadline:
                    time.sleep(0.05)
                if receipt.status != RNS.PacketReceipt.DELIVERED:
                    raise AssertionError(f"Default daemon rejected unstamped delivery: {receipt.status}")
                emit({"event": "unstamped-delivered", "hash": message.hash.hex()})
            finally:
                link.teardown()
        elif command[0] == "effective-limits":
            from LXMF.Utilities import lxmd
            from RNS.vendor.configobj import ConfigObj
            lxmd.lxmd_config = ConfigObj(" ".join(command[1:]))
            lxmd.ignoredpath = os.path.join(storage, "missing-ignored")
            lxmd.allowedpath = os.path.join(storage, "missing-allowed")
            lxmd.active_configuration = {}
            lxmd.apply_config()
            options = lxmd.active_configuration
            reference = LXMF.LXMRouter(identity=RNS.Identity(), storagepath=os.path.join(storage, "config-oracle"),
                                       autopeer=False, propagation_limit=options["propagation_transfer_max_accepted_size"],
                                       sync_limit=options["propagation_sync_max_accepted_size"])
            emit({"event": "effective-limits", "transfer_limit": reference.propagation_per_transfer_limit,
                  "sync_limit": reference.propagation_per_sync_limit})
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
                throttled = (target, key, message)
            finally:
                link.teardown()
                if retry is not None:
                    retry.teardown()
        elif command[0] == "retry-expired":
            import RNS.vendor.umsgpack as msgpack
            if throttled is None:
                raise AssertionError("No previous rejected stamp transfer")
            target, key, message = throttled
            link, _ = connect(target)
            try:
                offered = request_offer(link, key, message.transient_id)
                if offered is not True:
                    raise AssertionError(f"Expired throttle still rejected valid peer: {offered}")
                encrypted = msgpack.unpackb(message.propagation_packed)[1][0]
                stamp = message.get_propagation_stamp(13)
                resource = RNS.Resource(msgpack.packb([time.time(), [encrypted + stamp]]), link)
                deadline = time.monotonic() + 30
                while resource.status not in (RNS.Resource.COMPLETE, RNS.Resource.FAILED) and time.monotonic() < deadline:
                    time.sleep(0.05)
                if resource.status != RNS.Resource.COMPLETE:
                    raise AssertionError(f"Valid post-expiry Resource failed: {resource.status}")
                emit({"event": "throttle-expired", "response": offered, "transient": message.transient_id.hex()})
            finally:
                link.teardown()
        elif command[0] == "admission":
            import LXMF.LXStamper as stamper
            import RNS.vendor.umsgpack as msgpack
            from RNS.Resource import ResourceAdvertisement
            from LXMF.LXMPeer import LXMPeer
            target = bytes.fromhex(command[1])
            links = []
            probe = None
            try:
                first, remote = connect(target)
                links.append(first)
                config = msgpack.unpackb(RNS.Identity.recall_app_data(target))
                key, _ = stamper.generate_stamp(remote.hash + identity.hash, config[5][2],
                                                expand_rounds=stamper.WORKBLOCK_EXPAND_ROUNDS_PEERING)
                message = LXMF.LXMessage(local, local, b"admission known message", "admission", desired_method=LXMF.LXMessage.PROPAGATED)
                message.pack()
                encrypted = msgpack.unpackb(message.propagation_packed)[1][0]
                stamp = message.get_propagation_stamp(13)
                if request_offer(first, key, message.transient_id) is not True:
                    raise AssertionError("Initial admission offer rejected")
                resource = RNS.Resource(msgpack.packb([time.time(), [encrypted + stamp]]), first)
                deadline = time.monotonic() + 30
                while resource.status != RNS.Resource.COMPLETE and time.monotonic() < deadline:
                    time.sleep(0.05)
                if resource.status != RNS.Resource.COMPLETE:
                    raise AssertionError("Initial admission Resource failed")
                # Resource proof can precede application stamp validation.
                deadline = time.monotonic() + 20
                while time.monotonic() < deadline:
                    if request_offer(first, key, message.transient_id) is False:
                        break
                    time.sleep(0.05)
                else:
                    raise AssertionError("Known message did not enter native store")
                first.teardown()
                links.clear()
                fresh = os.urandom(32)
                sender = RNS.Identity()
                probe, _ = connect(target, sender)
                probe_key, _ = stamper.generate_stamp(remote.hash + sender.hash, config[5][2],
                                                      expand_rounds=stamper.WORKBLOCK_EXPAND_ROUNDS_PEERING)
                for _ in range(3):
                    link, _ = connect(target)
                    links.append(link)
                    offered = request_offer(link, key, [message.transient_id, fresh])
                    if offered != [fresh]:
                        raise AssertionError(f"Subset admission offer rejected: {offered}")
                    incomplete = RNS.Resource(os.urandom(8192), link, advertise=False, auto_compress=False)
                    # Send the real reference advertisement, deliberately omit its data parts.
                    # No Resource sender is registered, so it cannot answer part requests.
                    RNS.Packet(link, ResourceAdvertisement(incomplete).pack(), context=RNS.Packet.RESOURCE_ADV).send()
                deadline = time.monotonic() + 10
                while time.monotonic() < deadline:
                    response = request_offer(probe, probe_key, fresh)
                    if response == LXMPeer.ERROR_THROTTLED:
                        break
                    time.sleep(0.05)
                else:
                    raise AssertionError(f"Three incomplete Resources did not saturate admission: {response}")
                rejected_resource = RNS.Resource(os.urandom(8192), probe, auto_compress=False)
                deadline = time.monotonic() + 10
                while rejected_resource.status not in (RNS.Resource.REJECTED, RNS.Resource.FAILED, RNS.Resource.COMPLETE) and time.monotonic() < deadline:
                    time.sleep(0.05)
                if rejected_resource.status != RNS.Resource.REJECTED:
                    raise AssertionError(f"Saturated node did not reject fourth independent Resource: {rejected_resource.status}")
                static_link, _ = connect(target)
                links.append(static_link)
                static_response = request_offer(static_link, key, fresh)
                if static_response is not True:
                    raise AssertionError(f"Configured static peer did not bypass saturated admission: {static_response}")
                for link in links:
                    link.teardown()
                links.clear()
                deadline = time.monotonic() + 15
                while time.monotonic() < deadline:
                    response = request_offer(probe, probe_key, fresh)
                    if response is True:
                        break
                    time.sleep(0.05)
                else:
                    raise AssertionError(f"Admission did not recover after failed senders: {response}")
                emit({"event": "admission-recovered", "saturated": LXMPeer.ERROR_THROTTLED,
                      "recovered": response, "held_resources": 3, "static_bypass": static_response,
                      "fourth_resource_status": rejected_resource.status})
            finally:
                for link in links:
                    link.teardown()
                if probe is not None:
                    probe.teardown()
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
