"""Read-only public lifecycle observations around the verified actual-primary bridge."""
from live_lxmf_node import emit
from sideband_retry_primary import main


def snapshot(router, message):
    return {
        "outbound_locked": router.outbound_processing_lock.locked(),
        "pending": len(router.pending_outbound),
        "deferred": len(router.pending_deferred_stamps),
        "defer_stamp": message.defer_stamp,
        "defer_propagation_stamp": message.defer_propagation_stamp,
    }


def returned(tag):
    emit({"event": "failed-callback-returned", "tag": tag})


if __name__ == "__main__":
    main(diagnostic=snapshot, failed_returned=returned)
