"""Execute upstream node admission, signalling, jobs and message-file persistence."""
from contextlib import ExitStack
import hashlib
import importlib
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import threading
from types import SimpleNamespace
from unittest.mock import patch

import LXMF
import RNS
import RNS.vendor.umsgpack as msgpack

SHA = "e8ba02483217af43be284a72f1c059c730b7d047"
source_root = Path(os.environ["DOTRETICULUM_LXMF_UPSTREAM"]).resolve(strict=True)
assert Path(LXMF.__file__).resolve().is_relative_to(source_root / "LXMF")
assert subprocess.check_output(["git", "-C", str(source_root), "rev-parse", "HEAD"], text=True).strip() == SHA
rns_sha = subprocess.check_output(
    ["git", "-C", str(Path(RNS.__file__).parent.parent), "rev-parse", "HEAD"], text=True).strip()
assert rns_sha == "a29bfb51c468945c96a1ee22cae0114e993e2391"
module = importlib.import_module("LXMF.LXMRouter")
peer_type = importlib.import_module("LXMF.LXMPeer").LXMPeer
jobs = []
path_requests = []
now = 1700000400.0
medium = SimpleNamespace(is_blackholed=lambda identity: False, _used_destination_data=lambda destination: None,
                         get_medium_path_timeout=lambda: 0, get_lowest_interface_bitrate=lambda: None)
directory = Path("artifacts") / "lxmf-node-policy-reference"
assert not directory.exists(), "Never replace an existing evidence directory"


def new_router(name, **options):
    return LXMF.LXMRouter(identity=RNS.Identity(), storagepath=str(directory / name), **options)


try:
    with ExitStack() as stack:
        for owner, name, replacement in (
            (RNS.Reticulum, "get_instance", staticmethod(lambda: medium)),
            (RNS.Transport, "register_destination", staticmethod(lambda destination: None)),
            (RNS.Transport, "register_announce_handler", staticmethod(lambda handler: None)),
            (RNS.Transport, "request_path", staticmethod(lambda destination: path_requests.append(destination))),
            (RNS.Transport, "hops_to", staticmethod(lambda destination: 2)),
            (module.signal, "signal", lambda *args: None),
            (module.atexit, "register", lambda *args: None),
            (module.time, "time", lambda: now),
            (threading.Thread, "start", lambda thread: jobs.append(thread)),
        ):
            stack.enter_context(patch.object(owner, name, replacement))
        static = new_router("static", max_peers=0, static_peers=[b"s" * 16])
        static.enable_propagation()
        static_case = dict(max_peers=static.max_peers, peer_count=len(static.peers),
                           static_present=b"s" * 16 in static.peers, path_requests=len(path_requests))
        delayed = [job for job in jobs if job._target.__name__ == "delayed_announce"][-1]
        sleeps = []
        announces = []
        static.propagation_destination.announce = lambda **kwargs: announces.append(
            msgpack.unpackb(kwargs["app_data"])[2])
        with patch.object(module.time, "sleep", side_effect=sleeps.append):
            delayed._target()
        controls = []
        static.control_destination.announce = lambda: controls.append(True)
        static.control_allowed_list.append(b"c" * 16)
        static.disable_propagation()
        with patch.object(module.time, "sleep", side_effect=sleeps.append):
            jobs[-1]._target()
        static.propagation_node = True
        delay_case = dict(sleeps=sleeps, propagation_enabled=announces, control_announces=len(controls))

        known_identity = RNS.Identity(create_keys=False)
        known_identity.load_private_key(bytes(range(1, 33)) + bytes(range(101, 133)))
        known_destination = b"k" * 16
        RNS.Identity.remember(b"\0" * 32, known_destination, known_identity.get_public_key())
        reload = new_router("reload", max_peers=0)
        serialized = [peer_type(reload, destination).to_bytes() for destination in (known_destination, b"u" * 16)]
        Path(reload.storagepath).mkdir(parents=True, exist_ok=True)
        Path(reload.storagepath, "peers").write_bytes(msgpack.packb(serialized))
        reload.enable_propagation()
        reload_case = dict(max_peers=reload.max_peers, restored=[key.hex() for key in reload.peers],
                           serialized=[value.hex() for value in serialized],
                           known_public_key=known_identity.get_public_key().hex())

        incoming = []
        for autopeer, enabled, hops, cost, depth in ((True, True, 1, 18, 4), (False, True, 1, 18, 4),
                                                    (True, False, 1, 18, 4), (True, True, 1, 18, 0),
                                                    (True, True, 1, 27, 4)):
            receiver = new_router(f"incoming-{len(incoming)}", autopeer=autopeer, autopeer_maxdepth=depth)
            receiver.enable_propagation()
            remote = RNS.Destination(static.identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "propagation")
            config = msgpack.unpackb(static.get_propagation_node_app_data())
            config[2] = enabled
            config[5][2] = cost
            RNS.Identity.remember(b"\0" * 32, remote.hash, static.identity.get_public_key(), msgpack.packb(config))
            resource = SimpleNamespace(status=RNS.Resource.COMPLETE, data=io.BytesIO(msgpack.packb([now, []])),
                                       link=SimpleNamespace(link_id=b"l" * 16, get_remote_identity=lambda: static.identity,
                                                            teardown=lambda: None))
            with patch.object(RNS.Transport, "hops_to", return_value=hops):
                receiver.propagation_resource_concluded(resource)
            incoming.append(dict(autopeer=autopeer, enabled=enabled, hops=hops, max_depth=depth, peering_cost=cost,
                                 peer_count=len(receiver.peers)))
        assert [row["peer_count"] for row in incoming] == [1, 0, 0, 0, 0]
        rejected_packets = []
        closed = []
        packet = SimpleNamespace(destination_type=RNS.Destination.LINK,
                                 link=SimpleNamespace(teardown=lambda: closed.append(True)),
                                 prove=lambda: None)
        with patch.object(RNS, "Packet", side_effect=lambda destination, data: SimpleNamespace(
                send=lambda: rejected_packets.append(data))):
            static.propagation_packet(msgpack.packb([now, [b"invalid"]]), packet)
        cancelled = []
        static.cancel_outbound = lambda message_id, cancel_state: cancelled.append((message_id.hex(), cancel_state))
        static.propagation_transfer_signalling_packet(rejected_packets[0], SimpleNamespace(
            link=SimpleNamespace(for_lxmessage=SimpleNamespace(message_id=b"m" * 32))))
        rejection = dict(packet=rejected_packets[0].hex(), closed=bool(closed), cancelled=cancelled)
        calls = {}
        probe = SimpleNamespace(exit_handler_running=False, processing_count=0, propagation_node=True)
        for name in ("process_outbound", "process_deferred_stamps", "clean_links", "clean_resource_tracking",
                     "clean_transient_id_caches", "clean_message_store", "flush_queues", "rotate_peers",
                     "sync_peers", "clean_throttled_peers"):
            setattr(probe, name, lambda name=name: calls.setdefault(name, []).append(probe.processing_count))
        with patch.object(threading.Thread, "start", lambda thread: thread._target()):
            for tick in range(336):
                module.LXMRouter.jobs(probe)
        cadence = {name: dict(first=positions[0], count=len(positions),
                             interval=positions[1] - positions[0] if len(positions) > 1 else positions[0])
                   for name, positions in calls.items()}
        retention = module.LXMRouter.MESSAGE_EXPIRY * 6
        history = {bytes([index]) * 32: now - retention + offset for index, offset in enumerate((-1, 0, 1))}
        cache = SimpleNamespace(locally_delivered_transient_ids=history.copy(),
                                locally_processed_transient_ids=history.copy(),
                                delivered_transient_ids_lock=threading.Lock(),
                                processed_transient_ids_lock=threading.Lock())
        module.LXMRouter.clean_transient_id_caches(cache)
        throttled = SimpleNamespace(throttled_peers={b"p" * 16: now})
        module.LXMRouter.clean_throttled_peers(throttled)
        throttle_boundary = len(throttled.throttled_peers)
        with patch.object(module.time, "time", lambda: now + 0.001):
            module.LXMRouter.clean_throttled_peers(throttled)
        jobs_case = dict(interval=module.LXMRouter.PROCESSING_INTERVAL, cadence=cadence,
                         history={key.hex(): timestamp for key, timestamp in history.items()},
                         delivered=[key.hex() for key in cache.locally_delivered_transient_ids],
                         processed=[key.hex() for key in cache.locally_processed_transient_ids],
                         throttle_at_boundary=throttle_boundary, throttle_after_boundary=len(throttled.throttled_peers))
        static.set_outbound_propagation_node(remote.hash)
        cached_cost = static.get_outbound_propagation_cost()
        static.set_active_propagation_node(b"z" * 16)
        clock = [now]
        requests_before = len(path_requests)
        with patch.object(module.time, "time", lambda: clock[0]), patch.object(
                module.time, "sleep", lambda seconds: clock.__setitem__(0, clock[0] + seconds)):
            missing_cost = static.get_outbound_propagation_cost()
        cost_discovery = dict(cached=cached_cost, missing=missing_cost, wait=clock[0] - now,
                              path_requests=len(path_requests) - requests_before,
                              inbound_alias=static.get_inbound_propagation_node() == static.get_outbound_propagation_node())
        distribution = new_router("distribution")
        origin = peer_type(distribution, b"a" * 16)
        other = peer_type(distribution, b"b" * 16)
        distribution.peers = {origin.destination_hash: origin, other.destination_hash: other}
        distribution.enqueue_peer_distribution(b"h" * 32, origin)
        distribution.flush_peer_distribution_queue()
        distributed = dict(origin_queued=len(origin.unhandled_messages_queue),
                           other_queued=len(other.unhandled_messages_queue))
        ticket_router = new_router("ticket-reload")
        ticket_router.generate_ticket(b"d" * 16)
        ticket_path = Path(ticket_router.storagepath, "available_tickets")
        ticket_router.available_tickets["outbound"][b"x" * 16] = [now + 100, b"z" * 16]
        ticket_router.reload_available_tickets()
        replaced = b"x" * 16 not in ticket_router.available_tickets["outbound"]
        loaded = ticket_router.available_tickets
        ticket_path.write_bytes(b"\xc1")
        ticket_router.reload_available_tickets()
        corrupt_preserved = ticket_router.available_tickets is loaded
        ticket_path.unlink()
        ticket_router.reload_available_tickets()
        ticket_reload = dict(replaces_memory=replaced, corrupt_preserved=corrupt_preserved,
                             missing_preserved=ticket_router.available_tickets is loaded)
        message_source = RNS.Destination(static.identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
        RNS.Identity.remember(b"\0" * 32, message_source.hash, static.identity.get_public_key())
        message = LXMF.LXMessage(RNS.Destination(known_identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery"),
                                message_source, "filesystem contract", title="reference")
        message.pack()
        message_directory = directory / "message-files"
        message_directory.mkdir()
        message_path = Path(message.write_to_directory(str(message_directory)))
        with message_path.open("rb") as stream:
            restored = LXMF.LXMessage.unpack_from_file(stream)
        assert restored.signature_validated
        message_file = dict(filename=message_path.name, container=message_path.read_bytes().hex(),
                            source_public_key=static.identity.get_public_key().hex())
        counter = new_router("duplicate-counter", propagation_cost_flexibility=13, propagation_cost=13)
        counter.enable_propagation()
        deposit = msgpack.packb([now, [message.packed + b"\0" * 32]])
        for repeat in range(2):
            counter.propagation_packet(deposit, packet)
        duplicates = dict(received=counter.client_propagation_messages_received, stored=len(counter.propagation_entries))
        output = dict(lxmf_sha=SHA, rns_sha=rns_sha,
                      source_sha256=hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest(),
                      message_source_sha256=hashlib.sha256((source_root / "LXMF" / "LXMessage.py").read_bytes()).hexdigest(),
                      static=static_case, delayed_announce=delay_case, reload=reload_case, incoming=incoming,
                      rejection=rejection, jobs=jobs_case, message_file=message_file, cost_discovery=cost_discovery,
                      distribution=distributed, ticket_reload=ticket_reload, duplicate_packets=duplicates)
        Path(sys.argv[1]).write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
finally:
    if directory.exists():
        shutil.rmtree(directory)
