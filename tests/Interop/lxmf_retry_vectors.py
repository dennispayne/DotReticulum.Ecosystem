"""Execute pinned process_outbound with controlled link/path/clock observations.

Only the environmental inputs and Link constructor are substituted. The router's
outbound branches, scheduling, attempt limit and terminal callback execute intact.
This is scheduling evidence, not live transport evidence.
"""
import argparse
import hashlib
import importlib
import json
from pathlib import Path
import threading
from types import SimpleNamespace

import LXMF
import RNS
from reference_environment import verify_reference, PINNED_LXMF_SHA, PINNED_RNS_SHA


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    module = importlib.import_module("LXMF.LXMRouter")
    router_type = module.LXMRouter
    actual_link = RNS.Link
    now = [1700000300.0]
    has_path = [True]
    observations = []

    class ObservedLink:
        ACTIVE = actual_link.ACTIVE
        CLOSED = actual_link.CLOSED
        PENDING = actual_link.PENDING

        def __init__(self, destination, **kwargs):
            self.status = self.PENDING
            self.activated_at = None

        def set_link_established_callback(self, callback):
            pass

        def set_packet_callback(self, callback):
            pass

    RNS.Link = ObservedLink
    medium = SimpleNamespace(get_medium_path_timeout=lambda: 0.0,
                             get_first_hop_timeout=lambda destination: 6.0,
                             get_lowest_interface_bitrate=lambda: 1000000000)
    RNS.Reticulum.get_instance = staticmethod(lambda: medium)
    RNS.Transport.has_path = staticmethod(lambda destination: has_path[0])
    RNS.Transport.request_path = staticmethod(lambda destination: None)
    identity = RNS.Identity()
    RNS.Identity.recall = staticmethod(lambda destination: identity)
    module.time.time = lambda: now[0]
    destination = b"d" * 16
    for method in (LXMF.LXMessage.DIRECT, LXMF.LXMessage.PROPAGATED):
        for scenario in ("refused-known-path", "closed-before-deadline", "path-arrives", "no-path", "active", "backchannel"):
            if scenario == "backchannel" and method != LXMF.LXMessage.DIRECT:
                continue
            router = router_type.__new__(router_type)
            router.outbound_processing_lock = threading.Lock()
            router.path_request_lock = threading.Lock()
            router.path_request_times = {}
            router.slow_path_cache = {}
            router.direct_links = {}
            router.backchannel_links = {}
            router.outbound_propagation_link = None
            router.outbound_propagation_node = destination
            message = SimpleNamespace(method=method, state=LXMF.LXMessage.OUTBOUND,
                                      destination_hash=destination, delivery_attempts=0,
                                      max_delivery_attempts=None, next_delivery_attempt=0,
                                      awaiting_path=False, progress=0.0, failed_callback=None)
            message.get_destination = lambda: SimpleNamespace(hash=destination)
            message.set_delivery_destination = lambda link: None
            message.send = lambda: setattr(message, "state", LXMF.LXMessage.SENDING)
            router.pending_outbound = [message]
            has_path[0] = scenario not in ("path-arrives", "no-path", "backchannel")
            now[0] = 1700000300.0
            if scenario in ("active", "backchannel"):
                message.delivery_attempts = 4
                message.next_delivery_attempt = now[0] + 100
                link = ObservedLink(None)
                link.status = actual_link.ACTIVE
                if method == LXMF.LXMessage.PROPAGATED:
                    router.outbound_propagation_link = link
                elif scenario == "backchannel":
                    router.backchannel_links[destination] = link
                else:
                    router.direct_links[destination] = link
            rows = []
            for step in range(13):
                if scenario == "path-arrives" and step == 1:
                    has_path[0] = True
                if scenario not in ("active", "backchannel") and step > 0:
                    now[0] += (1 if step == 1 and scenario in ("path-arrives", "closed-before-deadline")
                               else 11 if scenario == "no-path" else 20)
                existing = router.outbound_propagation_link if method == LXMF.LXMessage.PROPAGATED else router.direct_links.get(destination)
                if existing and scenario not in ("active", "backchannel"):
                    existing.status = actual_link.CLOSED
                router.process_outbound()
                link = router.outbound_propagation_link if method == LXMF.LXMessage.PROPAGATED else router.direct_links.get(destination)
                rows.append(dict(time=now[0] - 1700000300.0, attempts=message.delivery_attempts,
                                 state=message.state, next_attempt=message.next_delivery_attempt,
                                 awaiting_path=message.awaiting_path,
                                 link_status=link.status if link else None,
                                 path_retried=getattr(message, "path_request_retried", False)))
                if message.state in (LXMF.LXMessage.FAILED, LXMF.LXMessage.SENDING):
                    break
            observations.append(dict(method=method, scenario=scenario, rows=rows))
    result = dict(lxmf_sha=PINNED_LXMF_SHA, rns_sha=PINNED_RNS_SHA,
                  source_sha256=hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest(),
                  method="LXMRouter.process_outbound; actual schedule_attempt/attempt_limit/fail_message",
                  fixed_now=1700000300.0, cases=observations)
    Path(args.output).write_bytes((json.dumps(result, indent=2) + "\n").encode("utf-8"))


if __name__ == "__main__":
    main()
