"""Execute pinned retrieval callbacks with observed link requests and delivery outcomes."""
import hashlib
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys
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


def json_value(value):
    if isinstance(value, bytes):
        return value.hex()
    if isinstance(value, (tuple, list)):
        return [json_value(item) for item in value]
    return value


class ObservedLink:
    status = RNS.Link.ACTIVE

    def __init__(self):
        self.requests = []
        self.closed = False
        self.identifications = 0

    def request(self, path, data, **callbacks):
        self.requests.append({"path": path, "data": json_value(data),
                              "callbacks": sorted(callbacks)})

    def identify(self, identity):
        self.identifications += 1

    def teardown(self):
        self.closed = True


def new_router(retain=False):
    router = router_type.__new__(router_type)
    router.outbound_propagation_node = b"p" * 16
    router.outbound_propagation_link = ObservedLink()
    router.propagation_transfer_state = router_type.PR_IDLE
    router.propagation_transfer_progress = 0.0
    router.propagation_transfer_size = None
    router.propagation_transfer_last_result = None
    router.propagation_transfer_last_duplicates = 0
    router.propagation_transfer_max_messages = 0
    router.retain_synced_on_node = retain
    router.delivery_per_transfer_limit = 1000
    router.locally_delivered_transient_ids = {b"a" * 32: 1700000000.0}
    router.save_locally_delivered_transient_ids = lambda: None
    return router


rows = []
for label, retain, maximum, response in (
    ("empty-list", False, 0, []),
    ("wants-haves", False, 1, [b"a" * 32, b"b" * 32, b"c" * 32]),
    ("retain-known", True, 1, [b"a" * 32, b"b" * 32, b"c" * 32]),
    ("all-known-retained", True, 0, [b"a" * 32]),
    ("no-identity-list", False, 0, 0xF0),
    ("no-access-list", False, 0, 0xF1),
):
    router = new_router(retain)
    router.propagation_transfer_max_messages = maximum
    link = router.outbound_propagation_link
    router.message_list_response(SimpleNamespace(response=response, link=link))
    rows.append(dict(label=label, retain=retain, maximum=maximum,
                     response=json_value(response), state=router.propagation_transfer_state,
                     progress=router.propagation_transfer_progress,
                     last_result=router.propagation_transfer_last_result,
                     closed=link.closed, requests=link.requests))

for label, retain, response, results in (
    ("download-new", False, [b"new encrypted message"], [True]),
    ("download-retain-reference-defect", True, [b"new encrypted message"], [True]),
    ("download-duplicate", False, [b"old encrypted message"], [router_type.DUPLICATE_SIGNAL]),
    ("download-rejected-reference-acknowledges", False, [b"rejected encrypted message"], [False]),
    ("download-empty", False, [], []),
    ("download-no-identity", False, 0xF0, []),
    ("download-no-access", False, 0xF1, []),
):
    router = new_router(retain)
    observed = iter(results)
    router.lxmf_propagation = lambda data, **kwargs: next(observed)
    link = router.outbound_propagation_link
    router.message_get_response(SimpleNamespace(response=response, link=link))
    rows.append(dict(label=label, retain=retain, response=json_value(response),
                     state=router.propagation_transfer_state, progress=router.propagation_transfer_progress,
                     last_result=router.propagation_transfer_last_result,
                     duplicates=router.propagation_transfer_last_duplicates,
                     closed=link.closed, requests=link.requests))

router = new_router()
link = router.outbound_propagation_link
router.request_messages_from_propagation_node(object(), max_messages=2)
reuse = dict(same_link=router.outbound_propagation_link is link, identifications=link.identifications,
             state=router.propagation_transfer_state, maximum=router.propagation_transfer_max_messages,
             requests=link.requests)
router.message_get_progress(SimpleNamespace(get_progress=lambda: 0.375, response_size=1234567))
progress = dict(state=router.propagation_transfer_state, progress=router.propagation_transfer_progress,
                size=router.propagation_transfer_size)
router.propagation_transfer_state = router_type.PR_NO_ACCESS
router.acknowledge_sync_completion()
ack_error = dict(state=router.propagation_transfer_state, progress=router.propagation_transfer_progress,
                 size=router.propagation_transfer_size, last_result=router.propagation_transfer_last_result)
router.cancel_propagation_node_requests()
cancellation = dict(closed=link.closed, link_cleared=router.outbound_propagation_link is None,
                    state=router.propagation_transfer_state, progress=router.propagation_transfer_progress,
                    size=router.propagation_transfer_size)
output = dict(lxmf_sha=SHA, rns_sha=rns_sha,
              source_sha256=hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest(),
              cases=rows, reuse=reuse, progress=progress, acknowledge_error=ack_error,
              cancellation=cancellation,
              disclosure="Only link I/O, persistence and delivery callback results are observed/stubbed; callback policy is actual upstream.")
Path(sys.argv[1]).write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
