"""Unmodified LXMF retry/refusal node; only read-only lifecycle observations.

The refused destination uses RNS's public accepts_links(False) policy. No
reference callback, scheduler, timeout, retry count or lock is replaced.
"""
import argparse
import os
import threading
import time

import LXMF
import RNS
from live_lxmf_node import emit, command_lines, write_config, Handler
from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, default=0)
    parser.add_argument("--transport", action="store_true")
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    write_config(args.config, args.tcp_server_port, args.rnsd_port, args.transport)
    RNS.Reticulum(configdir=args.config, loglevel=RNS.LOG_WARNING)
    RNS.Transport.register_announce_handler(Handler())
    router = LXMF.LXMRouter(identity=RNS.Identity(), storagepath=os.path.join(args.config, "lxmf"), autopeer=False)
    local = router.register_delivery_identity(RNS.Identity(), display_name="RetryHealthy")
    refused = RNS.Destination(RNS.Identity(), RNS.Destination.IN, RNS.Destination.SINGLE, "lxmf", "delivery")
    refused.accepts_links(False)
    router.register_delivery_callback(lambda message: emit(dict(
        event="received", title=message.title_as_string(), hash=message.hash.hex(),
        signature_validated=message.signature_validated)))
    emit(dict(event="ready", dest=local.hash.hex(), refused_dest=refused.hash.hex()))
    stop = threading.Event()
    observations = []
    observation_lock = threading.Lock()

    def report(link, message, started, seen):
        key = (link.link_id.hex(), link.status)
        with observation_lock:
            if key in seen:
                return
            seen.add(key)
        emit(dict(event="link-observation", message=message.hash.hex(),
                  link=key[0], status=key[1], attempts=message.delivery_attempts,
                  establishment_timeout=link.establishment_timeout,
                  elapsed=time.monotonic() - started))

    def observe():
        while not stop.wait(0.02):
            for record in observations.copy():
                message, started, seen = record
                link = router.direct_links.get(message.destination_hash)
                if link is not None:
                    # A watchdog and process_outbound can close/remove a link
                    # between polls. Register only an observational public API
                    # callback, never replace an existing callback or wake LXMF.
                    if link.callbacks.link_closed is None:
                        link.set_link_closed_callback(
                            lambda closed, message=message, started=started, seen=seen:
                            report(closed, message, started, seen))
                    report(link, message, started, seen)

    threading.Thread(target=observe, daemon=True).start()
    try:
        for line in command_lines():
            parts = line.split()
            if not parts:
                continue
            if parts[0] == "quit":
                break
            if parts[0] == "announce":
                local.announce()
                refused.announce()
                emit(dict(event="announced"))
            elif parts[0] == "send" and len(parts) == 3:
                target = bytes.fromhex(parts[1])
                identity = RNS.Identity.recall(target)
                if identity is None:
                    raise ValueError("No announced target identity")
                destination = RNS.Destination(identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
                message = LXMF.LXMessage(destination, local, "retry probe", title=parts[2],
                                         desired_method=LXMF.LXMessage.DIRECT)
                started = time.monotonic()
                observations.append((message, started, set()))

                def concluded(outcome, event, started=started):
                    link = router.direct_links.get(outcome.destination_hash)
                    emit(dict(event=event, hash=outcome.hash.hex(), attempts=outcome.delivery_attempts,
                              state=outcome.state, elapsed=time.monotonic() - started,
                              link_status=link.status if link else None))

                message.register_delivery_callback(lambda result, callback=concluded: callback(result, "delivered"))
                message.register_failed_callback(lambda result, callback=concluded: callback(result, "failed"))
                router.handle_outbound(message)
                emit(dict(event="sent_queued", hash=message.hash.hex()))
    finally:
        stop.set()


if __name__ == "__main__":
    main()
