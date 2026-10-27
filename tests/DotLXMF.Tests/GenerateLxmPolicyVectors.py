"""Execute pinned LXMRouter constructor and runtime policy contracts.

Destination registration, announce handlers, process hooks and thread start are
disabled for this filesystem-only oracle. No policy or cryptography is replaced.
"""
import hashlib
import importlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import threading
from unittest.mock import patch

import LXMF
import RNS
import RNS.vendor.umsgpack as msgpack

SHA = "e8ba02483217af43be284a72f1c059c730b7d047"
source_root = Path(os.environ["DOTRETICULUM_LXMF_UPSTREAM"]).resolve(strict=True)
assert Path(LXMF.__file__).resolve().is_relative_to(source_root / "LXMF")
assert subprocess.check_output(["git", "-C", str(source_root), "rev-parse", "HEAD"], text=True).strip() == SHA
module = importlib.import_module("LXMF.LXMRouter")
RNS.Transport.register_destination = staticmethod(lambda destination: None)


class NoReticulum:
    def is_blackholed(self, identity):
        return False

    def _used_destination_data(self, destination_hash):
        pass


RNS.Reticulum.get_instance = staticmethod(lambda: NoReticulum())
directory = Path("artifacts") / "lxmf-policy-reference"
assert not directory.exists(), "Do not replace an existing evidence directory"
try:
    with patch.object(threading.Thread, "start", return_value=None), \
         patch.object(module.signal, "signal", return_value=None), \
         patch.object(module.atexit, "register", return_value=None), \
         patch.object(RNS.Transport, "register_announce_handler", return_value=None):
        router = LXMF.LXMRouter(identity=RNS.Identity(), storagepath=str(directory),
                               max_peers=0, max_inbound_syncs=0, propagation_cost=1, sync_limit=1)
    options = {"stamp_cost": router.propagation_stamp_cost, "sync_limit": router.propagation_per_sync_limit,
               "max_peers": router.max_peers, "max_inbound_syncs": router.propagation_max_inbound_syncs,
               "enforce_stamps": router._enforce_stamps}
    remote = RNS.Identity()
    before = router.identity_allowed(remote)
    router.set_authentication(True)
    denied = router.identity_allowed(remote)
    router.allow(remote.hash)
    router.allow(remote.hash)
    allowed = router.identity_allowed(remote)
    router.allow_control(remote.hash)
    router.allow_control(remote.hash)
    router.prioritise(remote.hash)
    router.prioritise(remote.hash)
    errors = {}
    for name in ("disallow", "disallow_control", "unprioritise"):
        try:
            getattr(router, name)(remote.hash)
            errors[name] = None
        except Exception as error:
            errors[name] = type(error).__name__
    router.enforce_stamps()
    enforced = router._enforce_stamps
    router.ignore_stamps()
    ignored = router._enforce_stamps
    storage = []
    for arguments in ({"kilobytes": 1.5, "megabytes": 0.001}, {}, {"kilobytes": -1}):
        try:
            router.set_message_storage_limit(**arguments)
            result = router.message_storage_limit
        except Exception as error:
            result = type(error).__name__
        storage.append({"arguments": arguments, "result": result})
    router.set_retain_node_lxms(True)
    retained = router.retain_synced_on_node
    router.set_retain_node_lxms(False)
    announced = []
    router.announce_propagation_node = lambda: announced.append(router.propagation_node)
    router.propagation_node = True
    router.disable_propagation()
    disabled = {"enabled": router.propagation_node, "announced": announced,
                "announce_enabled": msgpack.unpackb(router.get_propagation_node_app_data())[2],
                "stats": router.compile_stats()}
    origin = RNS.Destination(router.identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    target = RNS.Destination(remote, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    paper = LXMF.LXMessage(target, origin, "Nonlocal paper", desired_method=LXMF.LXMessage.PAPER)
    paper.pack()
    nonlocal_paper = []
    for hosting in (False, True):
        router.propagation_node = hosting
        router.locally_processed_transient_ids.clear()
        nonlocal_paper.append(router.ingest_lxm_uri(paper.as_uri(finalise=False)))
    output = {"lxmf_sha": SHA,
              "rns_sha": subprocess.check_output(["git", "-C", str(Path(RNS.__file__).parent.parent),
                                                  "rev-parse", "HEAD"], text=True).strip(),
              "source_sha256": hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest(),
              "options": options, "authorization": [before, denied, allowed],
              "allowed_count": len(router.allowed_list),
              "remote_control_count": router.control_allowed_list.count(remote.hash),
              "priority_count": router.prioritised_list.count(remote.hash),
              "defects": errors, "stamp_enforcement": [enforced, ignored],
              "storage_limits": storage, "retain": [retained, router.retain_synced_on_node],
              "disabled": disabled, "nonlocal_paper": nonlocal_paper}
    Path(sys.argv[1]).write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
finally:
    if directory.exists():
        shutil.rmtree(directory)
