"""Execute pinned router scheduling; fake only clock, medium inputs and path I/O."""
import hashlib
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
from types import SimpleNamespace

import LXMF
import RNS

SHA = "e8ba02483217af43be284a72f1c059c730b7d047"
source_root = Path(os.environ["DOTRETICULUM_LXMF_UPSTREAM"]).resolve(strict=True)
assert Path(LXMF.__file__).resolve().is_relative_to(source_root / "LXMF")
assert subprocess.check_output(["git", "-C", str(source_root), "rev-parse", "HEAD"], text=True).strip() == SHA
rns_sha = subprocess.check_output(
    ["git", "-C", str(Path(RNS.__file__).parent.parent), "rev-parse", "HEAD"], text=True).strip()
assert rns_sha == "a29bfb51c468945c96a1ee22cae0114e993e2391"
module = importlib.import_module("LXMF.LXMRouter")
router_type = module.LXMRouter
router = router_type.__new__(router_type)
router.path_request_lock = threading.Lock()
router.path_request_times = {}
router.slow_path_cache = {}
router.outbound_propagation_node = b"p" * 16
now = 1700000300.0
has_path = False
requests = []
medium = SimpleNamespace(timeout=0.0, first_hop=6.0, bitrate=None)
medium.get_medium_path_timeout = lambda: medium.timeout
medium.get_first_hop_timeout = lambda destination: medium.first_hop
medium.get_lowest_interface_bitrate = lambda: medium.bitrate
RNS.Reticulum.get_instance = staticmethod(lambda: medium)
RNS.Transport.has_path = staticmethod(lambda destination: has_path)
RNS.Transport.request_path = staticmethod(lambda destination: requests.append(destination))
module.time.time = lambda: now
module.random.uniform = lambda low, high: low + (high - low) * 0.25
destination = b"d" * 16
cases = []
for label, timeout, first_hop, bitrate, path, path_request, attempts in (
    ("unknown-fast", 0.0, 6.0, None, False, False, 0),
    ("known-fast", 0.0, 6.0, 1000000, True, False, 1),
    ("airtime-boundary", 7.0, 6.5, 2000, True, False, 1),
    ("airtime-aware", 8.0, 6.5001, 1999, True, False, 2),
    ("known-slow", 60.0, 33.0, 1200, True, False, 1),
    ("pathless-slow", 60.0, 6.0, 1200, False, False, 3),
    ("request-slow", 60.0, 6.0, 1200, False, True, 2),
    ("request-path-arrived", 60.0, 6.0, 1200, True, True, 2),
    ("rounding-to-even", 20.0, (50 / 4.5 + 6) / 2, 1200, True, False, 1),
):
    now = 1700000300.0
    has_path = path
    medium.timeout, medium.first_hop, medium.bitrate = timeout, first_hop, bitrate
    message = SimpleNamespace(max_delivery_attempts=None, delivery_attempts=attempts,
                              awaiting_path=False)
    router.schedule_attempt(message, destination, path_request=path_request)
    row = dict(label=label, medium_timeout=timeout, first_hop=first_hop, bitrate=bitrate,
               has_path=path, path_request=path_request, delivery_attempts=attempts,
               round_trip=router.destination_round_trip(destination),
               path_wait=router.path_request_wait(), slow_interface=router.slow_interface_online(),
               next_attempt=message.next_delivery_attempt, attempt_limit=router.attempt_limit(message),
               receipt_timeout=message.receipt_timeout, awaiting_path=message.awaiting_path)
    now = message.next_delivery_attempt
    row["due_at_deadline"] = router.attempt_due(message, destination)
    now += 0.001
    row["due_after_deadline"] = router.attempt_due(message, destination)
    cases.append(row)

debounce = []
for bitrate in (None, 2000, 1999):
    now = 1700000300.0
    medium.timeout, medium.bitrate = 12.0, bitrate
    router.path_request_times.clear()
    accepted = router.request_path(destination)
    now += 12.0
    at_path_wait = router.request_path(destination)
    pending = router.path_request_pending(destination)
    now = 1700000360.0
    at_slow_window = router.request_path(destination)
    debounce.append(dict(bitrate=bitrate, initial=accepted, at_path_wait=at_path_wait,
                         pending_after_request=pending, at_slow_window=at_slow_window))

now = 1700000300.0
medium.first_hop, medium.timeout = 33.0, 60.0
has_path = True
ordering = []
for label, ahead, other_method, other_state, sent_on_slow_path in (
    ("same-target-ahead", True, LXMF.LXMessage.DIRECT, LXMF.LXMessage.OUTBOUND, False),
    ("same-target-behind", False, LXMF.LXMessage.DIRECT, LXMF.LXMessage.OUTBOUND, False),
    ("different-target-in-flight", False, LXMF.LXMessage.DIRECT, LXMF.LXMessage.SENDING, True),
    ("opportunistic-in-flight", False, LXMF.LXMessage.OPPORTUNISTIC, LXMF.LXMessage.SENT, True),
    ("propagated-concluded", True, LXMF.LXMessage.PROPAGATED, LXMF.LXMessage.SENT, True),
    ("delivered-ahead", True, LXMF.LXMessage.DIRECT, LXMF.LXMessage.DELIVERED, True),
):
    message = SimpleNamespace(method=LXMF.LXMessage.DIRECT, state=LXMF.LXMessage.OUTBOUND,
                              destination_hash=destination)
    other = SimpleNamespace(method=other_method, state=other_state,
                            destination_hash=destination if "same-target" in label else b"x" * 16,
                            sent_on_slow_path=sent_on_slow_path)
    router.pending_outbound = [other, message] if ahead else [message, other]
    ordering.append(dict(label=label, ahead=ahead, other_method=other_method,
                         other_state=other_state, sent_on_slow_path=sent_on_slow_path,
                         same_target="same-target" in label,
                         held=router.slow_path_held(message, destination)))

output = dict(lxmf_sha=SHA, rns_sha=rns_sha,
              source_sha256=hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest(),
              fixed_now=1700000300.0, jitter_fraction=0.25,
              cases=cases, debounce=debounce, ordering=ordering,
              delivery_link_idle=router_type.LINK_MAX_INACTIVITY,
              propagation_link_idle=router_type.P_LINK_MAX_INACTIVITY)


class ObservedLink:
    def __init__(self, elapsed, link_id):
        self.elapsed, self.link_id, self.closed = elapsed, link_id, False

    def no_data_for(self):
        return self.elapsed

    def teardown(self):
        self.closed = True


idle = []
for direct_idle, propagation_idle in ((600.0, 180.0), (600.001, 180.001)):
    direct = ObservedLink(direct_idle, b"d" * 16)
    propagation = ObservedLink(propagation_idle, b"p" * 16)
    router.direct_links = {destination: direct}
    router.validated_peer_links = {direct.link_id: True}
    router.active_propagation_links = [propagation]
    router.accepted_offer_links_lock = threading.Lock()
    router.accepted_offer_links = {propagation.link_id: True, b"s" * 16: True}
    router.outbound_propagation_link = None
    router.clean_links()
    idle.append(dict(direct_idle=direct_idle, propagation_idle=propagation_idle,
                     direct_closed=direct.closed, propagation_closed=propagation.closed,
                     validated_links=len(router.validated_peer_links),
                     accepted_offers=len(router.accepted_offer_links)))
output["idle"] = idle
router.outbound_processing_lock = threading.Lock()
router.direct_links = {destination: SimpleNamespace(status=RNS.Link.ACTIVE)}
router.backchannel_links = {}
router.slow_path_cache.clear()
medium.first_hop, medium.timeout = 6.0, 0.0
has_path = True
message = SimpleNamespace(method=LXMF.LXMessage.DIRECT, state=LXMF.LXMessage.OUTBOUND,
                          destination_hash=destination, delivery_attempts=1, max_delivery_attempts=None,
                          progress=0.0)
message.get_destination = lambda: SimpleNamespace(hash=destination)
message.set_delivery_destination = lambda link: None
message.send = lambda: setattr(message, "state", LXMF.LXMessage.SENDING)
router.pending_outbound = [message]
router.process_outbound()
assert message.state == LXMF.LXMessage.SENDING
output["active_link_send_attempts"] = message.delivery_attempts
Path(sys.argv[1]).write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
