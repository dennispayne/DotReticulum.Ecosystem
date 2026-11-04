"""Execute pinned primary utility contracts with explicitly instrumented UI/transport owners."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
from types import SimpleNamespace
from unittest.mock import patch

from GenerateNomadNetClientRuntimeVectors import reference, REVISION


def generate(output, observations):
    source, imports = reference()
    import RNS
    from nomadnet.ui.textui import Utilities as utilities
    from nomadnet.ui.textui import Transport as transport

    original_paths = RNS.Transport.path_table
    original_culled = RNS.Transport.tables_last_culled
    try:
        destination = b"\x01" * 16
        RNS.Transport.path_table = {destination: [1234]}
        expiry = [RNS.Transport.expire_path(destination), RNS.Transport.expire_path(destination),
                  RNS.Transport.expire_path(b"\x00" * 16)]
        expired_timestamp = RNS.Transport.path_table[destination][0]
    finally:
        RNS.Transport.path_table = original_paths
        RNS.Transport.tables_last_culled = original_culled

    probes = []
    for size, count in (("", ""), ("junk", "junk"), ("0", "0"), ("-1", "-4"), ("17", "3")):
        owner = utilities.ProbeView.__new__(utilities.ProbeView)
        owner._probing = False
        owner.name_edit = SimpleNamespace(edit_text="rnstransport.probe")
        owner.hash_edit = SimpleNamespace(edit_text="01" * 16)
        owner.size_edit = SimpleNamespace(edit_text=size)
        owner.count_edit = SimpleNamespace(edit_text=count)
        owner.probe_button = SimpleNamespace(original_widget=SimpleNamespace(set_label=lambda value: None))
        owner.update_interface_status = lambda: None
        owner._begin_probe = lambda: None
        owner._log = lambda *values: None
        owner.on_probe()
        probes.append({"size": size, "count": count, "payload": owner._size,
                       "remaining": owner._remaining, "name": owner._app_name,
                       "aspects": owner._aspects})

    timeouts = []
    for first, medium in ((0, 0), (3, 5), (2, 30)):
        owner = SimpleNamespace(_dest_hash=b"\x01" * 16,
                                app=SimpleNamespace(rns=SimpleNamespace(
                                    get_first_hop_timeout=lambda value: first,
                                    get_medium_path_timeout=lambda: medium)))
        timeouts.append({"firstHop": first, "medium": medium,
                         "seconds": utilities.ProbeView._timeout(owner),
                         "speedSeconds": utilities.SpeedTestView._timeout(owner)})

    identity = RNS.Identity()
    expected = RNS.Destination.hash_from_name_and_identity(utilities.SPEEDTEST_FULL_NAME, identity)
    authorization = []
    for label, entered in (("matching", expected), ("different", b"\x00" * 16), ("absent", None)):
        flags = {"closed": False, "resources": False}
        link = SimpleNamespace(teardown=lambda: flags.update(closed=True),
                               set_resource_strategy=lambda value: flags.update(resources=True),
                               set_resource_started_callback=lambda value: None,
                               set_resource_concluded_callback=lambda value: None)
        owner = SimpleNamespace(_entered_peer_hash=lambda: entered, _log=lambda *args: None,
                                _set_progress=lambda value: None, _log_link_mtu=lambda value: None,
                                _on_recv_started=lambda value: None, _on_recv_concluded=lambda value: None)
        utilities.SpeedTestView._verify_incoming(owner, link, identity)
        authorization.append({"name": label, **flags})

    resource_calls = []
    def observe_resource(src, link, **kwargs):
        resource_calls.append({"bytes": len(src), "autoCompress": kwargs["auto_compress"],
                               "metadata": kwargs.get("metadata")})
        return SimpleNamespace()
    owner = SimpleNamespace(_sending=True, _send_seq=1, _send_bytes=25,
                            _client_resource=None, _on_send_done=lambda value: None,
                            _on_send_progress=lambda value: None, _log=lambda *args: None)
    with patch.object(RNS, "Resource", observe_resource):
        utilities.SpeedTestView._begin_resource(owner, SimpleNamespace(), b"public synthetic transfer", None, 1)

    received = {"linkClosed": False}
    link = SimpleNamespace(teardown=lambda: received.update(linkClosed=True))
    owner = SimpleNamespace(_recv_polling=True, _recv_started=1233, _recv_resource=object(),
                            _recv_link=link, _set_progress=lambda value: received.update(progress=value),
                            _log=lambda *args: None, rate_text=SimpleNamespace(set_text=lambda value: None),
                            _update_listen_status=lambda: None)
    owner._resource_size = lambda resource: utilities.SpeedTestView._resource_size(owner, resource)
    resource = SimpleNamespace(status=RNS.Resource.COMPLETE, segment_index=1, total_segments=1,
                               link=link, get_data_size=lambda: 2048)
    with patch.object(utilities.time, "time", return_value=1234):
        utilities.SpeedTestView._recv_concluded(owner, resource)
    received.update(activeCleared=owner._recv_link is None, resourceCleared=owner._recv_resource is None)

    reception = []
    for shared, rssi, snr, quality in ((False, None, None, None),
                                      (False, -80.5, 8.25, 50.5),
                                      (True, -80.5, 8.25, 50.5)):
        proof = SimpleNamespace(packet_hash=b"\x01" * 32, rssi=rssi, snr=snr)
        owner = SimpleNamespace(app=SimpleNamespace(rns=SimpleNamespace(
            is_connected_to_shared_instance=shared,
            get_packet_rssi=lambda value: rssi, get_packet_snr=lambda value: snr,
            get_packet_q=lambda value: quality)))
        reception.append({"shared": shared, "rssi": rssi, "snr": snr,
                          "quality": quality if shared else None,
                          "text": utilities.ProbeView._reception_stats(owner, SimpleNamespace(proof_packet=proof))})

    sizes = [0, 16, 1024, 1048576, 1073741824]
    rates = [0, 1, 125, 125000, 125000000]
    result = {
        "revision": REVISION,
        "method": "Actual imported ProbeView.on_probe/_timeout, SpeedTestView._verify_incoming/_begin_resource/_recv_concluded, Transport formatting and RNS.Transport.expire_path; synthetic UI/transport owners, no wire claim",
        "sources": {name: hashlib.sha256(subprocess.check_output(
            ["git", "-C", str(source), "show", f"{REVISION}:{name}"])).hexdigest()
            for name in ("nomadnet/ui/textui/Utilities.py", "nomadnet/ui/textui/Transport.py")},
        "defaults": {"probeSize": utilities.DEFAULT_PROBE_SIZE,
                     "probeTimeout": utilities.DEFAULT_PROBE_TIMEOUT,
                     "rncpApplication": utilities.RNCP_APP_NAME,
                     "speedName": utilities.SPEEDTEST_FULL_NAME,
                     "speedDefault": utilities.DEFAULT_SPEEDTEST_LABEL,
                     "speedMemoryMaximum": utilities.SPEEDTEST_INMEM_MAX,
                     "speedSizes": utilities.SPEEDTEST_SIZES},
        "probes": probes, "timeouts": timeouts, "authorization": authorization,
        "pathExpiry": {"results": expiry, "timestamp": expired_timestamp,
                       "rnsRevision": subprocess.check_output(
                           ["git", "-C", str(Path(RNS.__file__).resolve().parents[1]),
                            "rev-parse", "HEAD"], text=True).strip(),
                       "rnsTransportSha256": hashlib.sha256(subprocess.check_output(
                           ["git", "-C", str(Path(RNS.__file__).resolve().parents[1]),
                            "show", "HEAD:RNS/Transport.py"])).hexdigest()},
        "resources": resource_calls,
        "received": received,
        "reception": reception,
        "sizes": [{"input": value, "text": utilities._size_str(value)} for value in sizes],
        "rates": [{"input": value, "text": utilities._fmt_bitrate(value)} for value in rates],
        "durations": [{"input": value, "text": transport._fmt_duration(value)}
                      for value in (0, 1, 59, 60, 3599, 3600, 86400)],
    }
    Path(output).write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n",
                            encoding="utf-8", newline="\n")
    Path(observations).write_text(json.dumps({"imports": imports}, indent=2) + "\n",
                                  encoding="utf-8", newline="\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--observations", required=True)
    args = parser.parse_args()
    generate(args.output, args.observations)
