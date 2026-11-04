"""Actual pinned NomadNet utility controllers with a disclosed headless UI owner.

This is not NomadNetworkApp startup, a rendered terminal, or a protocol emulator.
Original Urwid widgets and unchanged controller methods execute against a real
isolated RNS node. Only UI scheduling/selection and callback observations are
provided here. All stdout machine events are framed by nomadnet_live_node.emit.
"""
import argparse
import hashlib
import heapq
import json
import os
from pathlib import Path
import sys
import threading
import time
import traceback
from types import SimpleNamespace

from GenerateNomadNetClientRuntimeVectors import reference, REVISION
from nomadnet_live_node import command_lines, emit


UTILITIES_SHA256 = "61a75d64a244d13e543d7c915d1611bbde87cf28db8450def4424a5634c7c1b1"
MAX_BYTES = 2000 * 1024 * 1024


def digest_stream(stream):
    position = stream.tell()
    stream.seek(0)
    digest, size = hashlib.sha256(), 0
    try:
        while chunk := stream.read(65536):
            size += len(chunk)
            if size > MAX_BYTES:
                raise ValueError("observation exceeds the largest primary speed-test size")
            digest.update(chunk)
    finally:
        stream.seek(position)
    return {"bytes": size, "sha256": digest.hexdigest()}


def digest_path(path):
    with open(path, "rb") as stream:
        return digest_stream(stream)


class HeadlessLoop:
    """Single serialized UI owner; at most 1024 pending callback/command alarms."""

    def __init__(self):
        self.pending = []
        self.condition = threading.Condition()
        self.sequence = 0
        self.stopping = False

    def set_alarm_in(self, seconds, callback, user_data=None):
        with self.condition:
            if self.stopping:
                return None
            if len(self.pending) >= 1024:
                emit("failure", operation="scheduler", error="1024 pending callbacks exceeded")
                os._exit(2)
            self.sequence += 1
            token = (time.monotonic() + seconds, self.sequence, callback, user_data)
            heapq.heappush(self.pending, token)
            self.condition.notify()
            return token

    def draw_screen(self):
        pass

    def remove_owner_alarms(self, owner):
        with self.condition:
            self.pending = [item for item in self.pending
                            if getattr(item[2], "__self__", None) is not owner]
            heapq.heapify(self.pending)

    def run(self):
        while not self.stopping:
            with self.condition:
                if not self.pending:
                    self.condition.wait(0.5)
                    continue
                wait = self.pending[0][0] - time.monotonic()
                if wait > 0:
                    self.condition.wait(min(wait, 0.5))
                    continue
                _, _, callback, data = heapq.heappop(self.pending)
            try:
                callback(self, data)
            except Exception as error:
                emit("failure", operation="callback", error=str(error),
                     traceback=traceback.format_exc())


def checked_hash(value):
    if not isinstance(value, str) or len(value) != 32:
        raise ValueError("expected a 32-character destination or identity hash")
    bytes.fromhex(value)
    return value.lower()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True, help="isolated synthetic owner profile")
    parser.add_argument("--rnsconfig", required=True, help="isolated actual RNS configuration")
    args = parser.parse_args()
    source, imports = reference()
    utilities_path = source / "nomadnet" / "ui" / "textui" / "Utilities.py"
    source_bytes = utilities_path.read_bytes()
    disk_digest = hashlib.sha256(source_bytes).hexdigest()
    if hashlib.sha256(source_bytes.replace(b"\r\n", b"\n")).hexdigest() != UTILITIES_SHA256:
        raise RuntimeError("Unexpected primary Utilities.py source")
    import RNS
    from RNS.Utilities import rncp
    from nomadnet.ui.textui.Utilities import (
        SpeedTestView, ProbeView, SendFileView, SPEEDTEST_FULL_NAME, SPEEDTEST_SIZES,
    )

    rns_config = Path(args.rnsconfig).resolve()
    profile = Path(args.config).resolve()
    profile.mkdir(parents=True, exist_ok=True)
    received = profile / "received"
    received.mkdir(exist_ok=True)
    # Actual rncp's Windows save_path guard mixes slash styles. Its unchanged
    # default relative-filename receive mode instead uses this isolated cwd.
    os.chdir(received)
    callback_logs = threading.local()

    def reference_log(message):
        message = str(message)[:4096]
        errors = getattr(callback_logs, "errors", None)
        if errors is not None and "[Error]" in message and len(errors) < 16:
            errors.append(message)
        emit("reference-log", message=message)
    rns = RNS.Reticulum(configdir=str(rns_config), loglevel=2, logdest=reference_log)
    identity_path = profile / "identity"
    if identity_path.exists():
        identity = RNS.Identity.from_file(str(identity_path))
        if identity is None:
            raise RuntimeError("Invalid persisted adapter identity")
    else:
        identity = RNS.Identity()
        fd = os.open(identity_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        os.close(fd)
        if not identity.to_file(str(identity_path)):
            raise RuntimeError("Could not save adapter identity")
    loop = HeadlessLoop()
    parent = SimpleNamespace(active="speedtest")
    ui = SimpleNamespace(glyphs={}, loop=loop, main_display=SimpleNamespace(
        sub_displays=SimpleNamespace(active_display=parent)))
    app = SimpleNamespace(identity=identity, rns=rns, config={"textui": {"glyphs": "unicode"}}, ui=ui)
    speed = SpeedTestView(app, parent)
    probe = ProbeView(app, parent)
    sender = SendFileView(app, parent)
    speed_address = RNS.Destination.hash_from_name_and_identity(SPEEDTEST_FULL_NAME, identity).hex()
    probe_destination = RNS.Destination(identity, RNS.Destination.IN, RNS.Destination.SINGLE,
                                        "nomadnetwork", "utilities", "probe")
    probe_destination.set_proof_strategy(RNS.Destination.PROVE_ALL)
    probe_count = [0]
    rncp_destination = [None]
    send_digest = [{}]
    file_digest = [{}]
    speed_operation = [False]
    file_operation = [False]
    probe_operation = [False]

    def guard(operation, callback):
        def guarded(*values, **keywords):
            try:
                return callback(*values, **keywords)
            except Exception as error:
                emit("failure", operation=operation, error=str(error),
                     traceback=traceback.format_exc())
                # Surface instrumentation failures before upstream broad catches.
                return None
        return guarded

    def observe_output(view, name):
        original = view.output.log

        def log(message, attr="body_text"):
            original(message, attr)
            if len(view.output.walker) > 128:
                del view.output.walker[:-128]
            emit("utility-log", utility=name, message=str(message)[:2048], attribute=attr)
            if "unauthorized peer" in str(message):
                emit("rejection", operation="speed-receive", reason=str(message))
        view.output.log = guard(name + "-log", log)

    for view, name in ((speed, "speed"), (probe, "probe"), (sender, "file")):
        observe_output(view, name)

    original_begin = speed._begin_resource

    def begin_resource(link, data_bytes, tmppath, seq):
        if data_bytes is not None:
            digest = hashlib.sha256()
            for offset in range(0, len(data_bytes), 65536):
                digest.update(memoryview(data_bytes)[offset:offset + 65536])
            send_digest[0] = {"bytes": len(data_bytes), "sha256": digest.hexdigest()}
        else:
            send_digest[0] = digest_path(tmppath)
        emit("speed-send", peer=speed.hash_edit.edit_text, **send_digest[0])
        return original_begin(link, data_bytes, tmppath, seq)
    speed._begin_resource = guard("speed-begin-observation", begin_resource)
    original_send_done = speed._send_done

    def send_done(resource):
        active = speed._sending
        complete = resource.status == RNS.Resource.COMPLETE
        reporting = active and speed_operation[0]
        if reporting:
            speed_operation[0] = False
        original_send_done(resource)
        if reporting:
            emit("speed-result", status="complete" if complete else "failed",
                 resource_status=resource.status, **send_digest[0])
    speed._send_done = guard("speed-send-observation", send_done)
    original_speed_finish = speed._finish

    def speed_finish():
        original_speed_finish()
        if speed_operation[0]:
            speed_operation[0] = False
            emit("speed-result", status="failed", **send_digest[0])
    speed._finish = guard("speed-finish-observation", speed_finish)
    original_received = speed._on_recv_concluded

    def received_resource(resource):
        # RNS closes/unlinks its actual stream immediately after this callback.
        # Observe it here, before scheduling the original controller callback.
        final = (resource.segment_index or 1) >= (resource.total_segments or 1)
        complete = resource.status == RNS.Resource.COMPLETE
        observed = {}
        try:
            if complete and final:
                observed = digest_stream(resource.data)
        except Exception as error:
            emit("failure", operation="speed-receive-digest", error=str(error),
                 traceback=traceback.format_exc())
        finally:
            original_received(resource)
        if final:
            loop.set_alarm_in(0, lambda l, u: emit(
                "speed-received", status="complete" if complete else "failed",
                resource_status=resource.status, **observed))
    speed._on_recv_concluded = guard("speed-receive-observation", received_resource)
    original_probe_finish = probe._finish

    def probe_finish():
        original_probe_finish()
        if probe_operation[0]:
            probe_operation[0] = False
            emit("probe-result", sent=probe._sent, received=probe._replies,
                 status="complete" if probe._sent == probe._replies and probe._sent else "failed")
    probe._finish = guard("probe-finish-observation", probe_finish)
    original_file_done = sender._resource_done

    def file_done(resource):
        active = sender._sending
        complete = resource.status == RNS.Resource.COMPLETE
        # Clear before upstream _finish is called by the original completion.
        if active and file_operation[0]:
            file_operation[0] = False
            original_file_done(resource)
            emit("file-result", status="complete" if complete else "failed",
                 resource_status=resource.status, **file_digest[0])
        else:
            original_file_done(resource)
    sender._resource_done = guard("file-send-observation", file_done)
    original_file_finish = sender._finish

    def file_finish():
        original_file_finish()
        if file_operation[0]:
            file_operation[0] = False
            emit("file-result", status="failed", **file_digest[0])
    sender._finish = guard("file-finish-observation", file_finish)

    def probe_received(data, packet):
        probe_count[0] += 1
        if probe_count[0] <= 1024:
            emit("probe-received", bytes=len(data), count=probe_count[0])
    probe_destination.set_packet_callback(guard("probe-receive-observation", probe_received))
    original_rncp_concluded = rncp.receive_resource_concluded
    rncp_observations = set()
    rncp_observation_lock = threading.Lock()

    def file_received(resource):
        # Do not hash before the actual handler: sender teardown can re-enter
        # this callback while a slow observation is still reading the stream.
        with rncp_observation_lock:
            if resource.hash in rncp_observations:
                original_rncp_concluded(resource)
                emit("rncp-callback-repeat", resource_status=resource.status)
                return
            if len(rncp_observations) >= 128:
                raise RuntimeError("128 rncp observations exceeded")
            rncp_observations.add(resource.hash)
        emit("rncp-callback", resource_hash=resource.hash.hex(), resource_status=resource.status,
             metadata=resource.metadata is not None, data_path=getattr(resource.data, "name", None))
        name, target = None, None
        if resource.status == RNS.Resource.COMPLETE and resource.metadata:
            name = os.path.basename(resource.metadata["name"].decode("utf-8"))
            target = received / name
            counter = 0
            while target.exists():
                counter += 1
                if counter > 128:
                    raise RuntimeError("128 existing rncp filename collisions exceeded")
                target = received / (name + "." + str(counter))
        callback_logs.errors = []
        try:
            original_rncp_concluded(resource)
            reference_errors = callback_logs.errors
        finally:
            del callback_logs.errors
        saved = target is not None and target.is_file()
        observed = digest_path(target) if saved else {}
        emit("file-received", status="complete" if saved else "failed",
             name=name, saved=saved, resource_status=resource.status,
             resource_hash=resource.hash.hex(), target=str(target), cwd=os.getcwd(),
             rncp_save_path=rncp.save_path, reference_errors=reference_errors, **observed)
    rncp.receive_resource_concluded = guard("rncp-receive-observation", file_received)

    def status():
        emit("status", listening=speed._listening, sending=speed._sending,
             receiving=speed._recv_link is not None, probing=probe._probing,
             file_sending=sender._sending, pending=len(loop.pending),
             probe_received=probe_count[0], rncp_listening=rncp_destination[0] is not None)

    def cancel_operations():
        speed_operation[0] = False
        speed.cancel(silent=True)
        if sender._resource is not None and sender._sending:
            sender._resource.cancel()
        if sender._link is not None and sender._sending:
            sender._link.teardown()
        if sender._sending:
            sender._concluded = True
            sender._finish()
        loop.remove_owner_alarms(sender)
        if probe._probing:
            probe_operation[0] = False
            probe._remaining = 0
            probe._cur_receipt = None
            probe._concluded = True
            probe._finish()
        loop.remove_owner_alarms(probe)

    def shutdown(exit_code=0):
        cancel_operations()
        speed.stop_listening()
        for link in list(RNS.Transport.active_links):
            link.teardown()
        # Upstream cleanup replaces stdout/stderr with devnull. Retain only the
        # adapter event channels so stopped is emitted after real cleanup.
        output, error = sys.stdout, sys.stderr
        try:
            RNS.Reticulum.exit_handler()
        finally:
            sys.stdout, sys.stderr = output, error
        emit("stopped", exit_code=exit_code)
        loop.stopping = True
        os._exit(exit_code)

    def command(value):
        operation = value.get("op")
        if operation == "quit":
            shutdown()
        elif operation == "status":
            status()
        elif operation == "announce":
            probe_destination.announce()
            if speed.server_destination is not None:
                speed.server_destination.announce()
            if rncp_destination[0] is not None:
                rncp_destination[0].announce()
            emit("announced")
        elif operation == "speed-listen":
            if speed._sending or speed._recv_link is not None:
                raise ValueError("speed operation already active")
            peer = checked_hash(value["peer"])
            speed.hash_edit.set_edit_text(peer)
            parent.active = "speedtest"
            speed.start_listening()
            if speed.server_destination is None:
                raise RuntimeError("Primary listener failed to start")
            speed.server_destination.announce()
            emit("speed-listen", peer=peer, destination=speed.server_destination.hash.hex())
        elif operation == "speed-off":
            speed.cancel(silent=True)
            speed_operation[0] = False
            speed.stop_listening()
            emit("speed-off")
        elif operation == "speed-send":
            if speed._sending or speed._recv_link is not None:
                raise ValueError("speed operation already active")
            peer = checked_hash(value["peer"])
            size = int(value.get("size", 4 * 1024 * 1024))
            label = next((label for label, count in SPEEDTEST_SIZES if count == size), None)
            if label is None:
                raise ValueError("size must be one of the twelve actual primary sizes")
            speed.hash_edit.set_edit_text(peer)
            speed.size_dropdown.selected = label
            send_digest[0] = {}
            speed_operation[0] = True
            speed.on_start()
            if not speed._sending and speed_operation[0]:
                speed_operation[0] = False
                raise RuntimeError("Primary did not start speed operation")
        elif operation == "probe":
            if probe._probing:
                raise ValueError("probe already active")
            size, count = int(value.get("size", 16)), int(value.get("count", 1))
            if not 1 <= size <= 65536 or not 1 <= count <= 64:
                raise ValueError("probe size/count outside adapter bounds")
            probe.hash_edit.set_edit_text(checked_hash(value["destination"]))
            probe.name_edit.set_edit_text(str(value["name"]))
            probe.size_edit.set_edit_text(str(size))
            probe.count_edit.set_edit_text(str(count))
            probe_operation[0] = True
            probe.on_probe()
            if not probe._probing and probe_operation[0]:
                probe_operation[0] = False
                raise RuntimeError("Primary did not start probe")
        elif operation == "file-send":
            if sender._sending:
                raise ValueError("file send already active")
            sender.hash_edit.set_edit_text(checked_hash(value["destination"]))
            sender.file_path = str(Path(value["path"]).resolve(strict=True))
            file_digest[0] = digest_path(sender.file_path)
            file_operation[0] = True
            sender.on_send()
            if not sender._sending and file_operation[0]:
                file_operation[0] = False
                raise RuntimeError("Primary did not start file send")
        elif operation == "rncp-listen":
            peer = bytes.fromhex(checked_hash(value["identity"]))
            rncp.allowed_identity_hashes = [peer]
            rncp.allow_all = False
            rncp.save_path = None
            if rncp_destination[0] is None:
                rncp_destination[0] = RNS.Destination(
                    identity, RNS.Destination.IN, RNS.Destination.SINGLE, "rncp", "receive")
                rncp_destination[0].set_link_established_callback(rncp.client_link_established)
            rncp_destination[0].announce()
            emit("rncp-listen", identity=peer.hex(), destination=rncp_destination[0].hash.hex())
        elif operation == "cancel":
            cancel_operations()
            emit("cancelled")
        else:
            raise ValueError("unsupported operation: " + str(operation))

    def read_commands():
        try:
            for raw in command_lines():
                value = json.loads(raw)
                if not isinstance(value, dict):
                    raise ValueError("commands must be JSON objects")
                loop.set_alarm_in(0, lambda l, u, v=value: guard(
                    str(v.get("op")), command)(v))
            loop.set_alarm_in(0, lambda l, u: shutdown())
        except Exception as error:
            emit("failure", operation="input", error=str(error))
            loop.set_alarm_in(0, lambda l, u: shutdown(2))

    emit("ready", revision=REVISION, imports=imports, utilities_sha256=UTILITIES_SHA256,
         utilities_disk_sha256=disk_digest,
         identity=identity.hash.hex(), speed=speed_address, probe=probe_destination.hash.hex(),
         probe_name="nomadnetwork.utilities.probe",
         rncp=RNS.Destination.hash_from_name_and_identity("rncp.receive", identity).hex(),
         sizes=[{"label": label, "bytes": count} for label, count in SPEEDTEST_SIZES],
         owner="synthetic minimal app; actual RNS node and unchanged primary controllers/widgets; "
               "bounded serialized headless UI scheduler, no application startup or terminal rendering",
         rncp_receiver="actual RNS.Utilities.rncp listener callbacks; not a NomadNet receiver")
    threading.Thread(target=read_commands, daemon=True).start()
    loop.run()


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit("failure", operation="startup", error=str(error), traceback=traceback.format_exc())
        sys.exit(1)
