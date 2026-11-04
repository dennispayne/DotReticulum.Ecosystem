"""Thin process adapter for the actual pinned Sideband desktop daemon core."""
import argparse
import base64
import hashlib
import inspect
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import tempfile

import RNS
import LXMF

from live_lxmf_node import Handler, command_lines, emit, write_config
from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
DIGEST = "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25"

def media_summary(message):
    fields = message["lxm"].fields
    result = {}
    result["telemetry_included"] = LXMF.FIELD_TELEMETRY in fields
    result["appearance_included"] = LXMF.FIELD_ICON_APPEARANCE in fields
    if LXMF.FIELD_IMAGE in fields:
        fmt, data = fields[LXMF.FIELD_IMAGE]
        result["image"] = {"format": fmt, "length": len(data), "sha256": hashlib.sha256(data).hexdigest()}
    if LXMF.FIELD_AUDIO in fields:
        mode, data = fields[LXMF.FIELD_AUDIO]
        result["audio"] = {"mode": mode, "length": len(data), "sha256": hashlib.sha256(data).hexdigest()}
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--tcp-server-port", type=int, default=0)
    parser.add_argument("--rnsd-port", type=int, default=0)
    parser.add_argument("--source", type=Path, required=True)
    args = parser.parse_args()
    lxst_source = os.environ.get("DOTRETICULUM_LXST_UPSTREAM")
    if lxst_source:
        sys.path.insert(0, str(Path(lxst_source).resolve()))
    verify_reference(require_lxmf=True)
    source = args.source.resolve()
    revision = subprocess.check_output(
        ["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != PIN:
        raise RuntimeError(f"Expected Sideband {PIN}, got {revision}")
    dirty = subprocess.check_output(
        ["git", "-C", str(source), "status", "--porcelain", "--untracked-files=normal"], text=True)
    if dirty:
        raise RuntimeError("The Sideband reference checkout is not clean")
    core_path = source / "sbapp" / "sideband" / "core.py"
    if hashlib.sha256(core_path.read_bytes().replace(b"\r\n", b"\n")).hexdigest() != DIGEST:
        raise RuntimeError("Unexpected Sideband core.py digest")
    sys.path.insert(0, str(source))
    from sbapp.sideband.core import SidebandCore
    if Path(inspect.getfile(SidebandCore)).resolve() != core_path:
        raise RuntimeError("Imported Sideband from an unexpected location")
    rns = args.config / "rns"
    write_config(str(rns), args.tcp_server_port, args.rnsd_port)
    core = SidebandCore(None, config_path=str(args.config / "profile"),
                        rns_config_path=str(rns), is_daemon=True, quiet=True)
    core.start()
    delivery_packet_callback = core.lxmf_destination.callbacks.packet
    delivery_proof_strategy = core.lxmf_destination.proof_strategy
    observed_runtime = [None]
    if core.message_router is None:
        raise RuntimeError("The actual Sideband application did not start its LXMF router")
    RNS.Transport.register_announce_handler(Handler())
    emit({"event": "ready", "dest": core.lxmf_destination.hash.hex(),
          "identity_hash": core.identity.hash.hex(), "revision": revision,
          "core_path": str(core_path), "source_sha256": DIGEST})
    for line in command_lines():
        parts = line.strip().split()
        if not parts:
            continue
        if parts[0] == "quit":
            core.save_configuration(no_thread=True)
            core.cleanup()
            RNS.Reticulum.exit_handler()
            os._exit(0)
        if parts[0] == "announce":
            core.lxmf_announce()
            emit({"event": "announced", "dest": core.lxmf_destination.hash.hex()})
        elif parts[0] == "announce-empty":
            # Explicit public RNS emission seam; does not replace Sideband's received handlers.
            core.lxmf_destination.announce(app_data=b"")
            emit({"event": "announced-empty"})
        elif parts[0] == "announces":
            emit({"event": "announces", "records": [
                {**entry, "dest": entry["dest"].hex()} for entry in core.list_announces()]})
        elif parts[0] == "delete-announce" and len(parts) == 2:
            core.delete_announce(bytes.fromhex(parts[1]))
            emit({"event": "announce-deleted"})
        elif parts[0] == "enable-announcement-node":
            # Explicit actual LXMF router role, not a claim that Sideband's UI hosts a PN.
            core.message_router.enable_propagation()
            core.message_router.propagation_destination.announce(
                app_data=core.message_router.get_propagation_node_app_data())
            emit({"event": "announce-node", "dest": core.message_router.propagation_destination.hash.hex()})
        elif parts[0] == "use-pn" and len(parts) == 2:
            core.config["lxmf_propagation_node"] = bytes.fromhex(parts[1])
            core.update_active_lxmf_propagation_node()
            core.save_configuration(no_thread=True)
            emit({"event": "pn-selected"})
        elif parts[0] == "delivery-links" and len(parts) == 2 and parts[1] in ("on", "off"):
            core.lxmf_destination.accepts_links(parts[1] == "on")
            emit({"event": "delivery-links", "enabled": core.lxmf_destination.accepts_links()})
        elif parts[0] == "delivery-packets" and len(parts) == 2 and parts[1] in ("on", "off"):
            # Disclosed packet-loss injection through public destination callbacks, not modified primary source.
            enabled = parts[1] == "on"
            core.lxmf_destination.set_packet_callback(delivery_packet_callback if enabled else None)
            core.lxmf_destination.set_proof_strategy(delivery_proof_strategy if enabled else RNS.Destination.PROVE_NONE)
            emit({"event": "delivery-packets", "enabled": enabled})
        elif parts[0] == "retrieve":
            if not core.request_lxmf_sync():
                raise RuntimeError("The actual application rejected propagation retrieval as busy")
            deadline = time.time() + 60
            while core.message_router.propagation_transfer_state < LXMF.LXMRouter.PR_COMPLETE and time.time() < deadline:
                time.sleep(0.05)
            state = core.message_router.propagation_transfer_state
            if state != LXMF.LXMRouter.PR_COMPLETE:
                raise RuntimeError(f"Actual application retrieval did not complete: {state}")
            emit({"event": "retrieved"})
        elif parts[0] == "trust" and len(parts) == 3 and parts[2] in ("yes", "no"):
            destination = bytes.fromhex(parts[1])
            if not core.has_conversation(destination):
                core.create_conversation(destination)
            if parts[2] == "yes":
                core.trusted_conversation(destination)
            else:
                core.untrusted_conversation(destination)
            emit({"event": "trust", "trusted": core.is_trusted(destination)})
        elif parts[0] == "telemetry-config" and len(parts) == 2:
            values = json.loads(base64.b64decode(parts[1], validate=True))
            was_enabled = core.config["telemetry_enabled"]
            hardware = ("location", "battery", "pressure", "temperature", "humidity", "magnetic_field",
                        "ambient_light", "gravity", "angular_velocity", "acceleration", "proximity")
            for key, value in values.items():
                if not key.startswith("telemetry_") or key not in core.config:
                    raise ValueError(f"Unknown telemetry configuration key {key}")
                if key in ("telemetry_to_mqtt",) and value:
                    raise ValueError("Reference bridge does not permit external services")
                if key in ("telemetry_s_" + sensor for sensor in hardware) and value:
                    raise ValueError("Reference bridge does not permit hardware sensors")
                if key == "telemetry_collector" and value is not None:
                    value = bytes.fromhex(value)
                core.config[key] = value
            if not was_enabled and core.config["telemetry_enabled"]:
                # Exercise the actual cleanup on the initially empty reference store, before test reports arrive.
                core._db_clean_telemetry()
            core.update_telemeter_config()
            core.update_telemetry()
            core.save_configuration(no_thread=True)
            emit({"event": "telemetry-configured", "enabled": core.config["telemetry_enabled"],
                  "packed": base64.b64encode(core.latest_packed_telemetry).decode() if core.latest_packed_telemetry else None})
        elif parts[0] == "telemetry-runtime-snapshot":
            sensor = core.telemeter.sensors.get("rns_transport") if core.telemeter else None
            if sensor is None or not sensor.active:
                raise RuntimeError("The actual runtime sensor is not enabled")
            emit({"event": "telemetry-runtime-snapshot",
                  "transport_identity": RNS.Transport.identity.hash.hex(),
                  "packed": base64.b64encode(sensor.packb()).decode()})
        elif parts[0] == "telemetry-runtime-observe" and len(parts) == 2:
            observed_source = bytes.fromhex(parts[1])
            if len(observed_source) != 16:
                raise ValueError("A delivery source must be 16 bytes")

            def observe_runtime(message):
                try:
                    if message.source_hash == observed_source and LXMF.FIELD_TELEMETRY in message.fields:
                        raw = message.fields[LXMF.FIELD_TELEMETRY]
                        if not isinstance(raw, bytes) or len(raw) > 262144:
                            raise ValueError("Observed runtime report exceeds the bridge bound")
                        observed_runtime[0] = raw
                finally:
                    core.lxmf_delivery(message)

            # Public observational callback; the actual application handler always runs unchanged.
            core.message_router.register_delivery_callback(observe_runtime)
            emit({"event": "telemetry-runtime-observing"})
        elif parts[0] == "telemetry-runtime-received":
            emit({"event": "telemetry-runtime-received",
                  "packed": base64.b64encode(observed_runtime[0]).decode() if observed_runtime[0] is not None else None})
        elif parts[0] == "telemetry-runtime-render" and len(parts) == 2:
            from sbapp.sideband.sense import Telemeter
            telemeter = Telemeter.from_packed(base64.b64decode(parts[1], validate=True))
            if telemeter is None or "rns_transport" not in telemeter.sensors:
                raise RuntimeError("The actual primary did not decode the runtime sensor")
            rendered = telemeter.sensors["rns_transport"].render()
            if rendered is None:
                raise RuntimeError("The actual primary did not render the runtime sensor")
            values = rendered["values"]
            emit({"event": "telemetry-runtime-render",
                  "transport_identity": values["transport_identity"].hex(),
                  "interface_names": list(values["interfaces"]),
                  "interface_count": values["interface_count"], "path_count": values["path_count"],
                  "link_count": values["link_count"],
                  "repacked": base64.b64encode(telemeter.sensors["rns_transport"].packb()).decode()})
        elif parts[0] == "telemetry-permission" and len(parts) == 4:
            destination = bytes.fromhex(parts[1])
            if not core.has_conversation(destination):
                core.create_conversation(destination)
            core._db_conversation_set_telemetry(destination, send_telemetry=parts[2] == "yes")
            core._db_conversation_set_requests(destination, allow_requests=parts[3] == "yes")
            emit({"event": "telemetry-permission", "send": core.should_send_telemetry(destination),
                  "request": core.allow_request_from(destination)})
        elif parts[0] in ("telemetry-send", "telemetry-request", "telemetry-request-collector") and len(parts) == 2:
            destination = bytes.fromhex(parts[1])
            result = core.send_latest_telemetry(destination) if parts[0] == "telemetry-send" else core.request_latest_telemetry(
                destination, is_collector_request=parts[0] == "telemetry-request-collector")
            emit({"event": "telemetry-queued", "status": result})
        elif parts[0] == "telemetry-snapshot" and len(parts) == 2:
            destination = bytes.fromhex(parts[1])
            reports = core.list_telemetry() or {}
            emit({"event": "telemetry-snapshot", "cursor": core._telemetry_timebase(destination),
                  "last_send": core._last_telemetry_send_success_timebase(destination),
                  "last_request_success": core.getpersistent(f"telemetry.{destination.hex()}.last_request_success_timebase"),
                  "records": [{"source": source.hex(), "timestamp": entry[0], "packed": base64.b64encode(entry[1]).decode()}
                              for source, entries in reports.items() for entry in entries]})
        elif parts[0] == "telemetry-seed" and len(parts) == 3:
            result = core._db_save_telemetry(bytes.fromhex(parts[1]), base64.b64decode(parts[2], validate=True))
            emit({"event": "telemetry-seeded", "saved": result is not None})
        elif parts[0] == "telemetry-exclude" and len(parts) == 2:
            core.telemetry_response_excluded = [bytes.fromhex(parts[1])]
            emit({"event": "telemetry-excluded"})
        elif parts[0] == "telemetry-stream" and len(parts) == 3:
            from RNS.vendor import umsgpack
            stream = umsgpack.unpackb(base64.b64decode(parts[2], validate=True))
            result = core.send_latest_telemetry(bytes.fromhex(parts[1]), stream=stream, is_authorized_telemetry_request=True)
            emit({"event": "telemetry-queued", "status": result})
        elif parts[0] in ("telemetry-fields", "telemetry-fields-invalid-signature") and len(parts) == 3:
            from RNS.vendor import umsgpack
            destination = bytes.fromhex(parts[1])
            fields = umsgpack.unpackb(base64.b64decode(parts[2], validate=True))
            peer = RNS.Identity.recall(destination)
            if peer is None:
                raise ValueError("Reference must know destination before raw-field test")
            message = LXMF.LXMessage(
                RNS.Destination(peer, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery"),
                core.lxmf_destination, "metadata isolation test", fields=fields, desired_method=LXMF.LXMessage.DIRECT)
            if parts[0] == "telemetry-fields-invalid-signature":
                message.pack()
                message.packed = message.packed[:32] + bytes(64) + message.packed[96:]
            core.message_router.handle_outbound(message)
            emit({"event": "telemetry-fields-queued"})
        elif parts[0] == "telemetry-profile-snapshot" and len(parts) == 2:
            import shutil
            import sqlite3
            from contextlib import closing
            destination = Path(base64.b64decode(parts[1], validate=True).decode())
            destination.mkdir(mode=0o700, parents=True, exist_ok=False)
            core.save_configuration(no_thread=True)
            for name in ("primary_identity", "sideband_config", "collector_response_excluded"):
                shutil.copyfile(Path(core.identity_path).parent / name, destination / name)
                if os.name != "nt":
                    (destination / name).chmod(0o600)
            # Consistent SQLite backup of actual primary state, not a recreated schema.
            with core.db_lock:
                with closing(sqlite3.connect(destination / "sideband.db")) as snapshot:
                    core.db.backup(snapshot)
            if os.name != "nt":
                (destination / "sideband.db").chmod(0o600)
            emit({"event": "telemetry-profile-snapshotted"})
        elif parts[0] in ("send", "send-propagated", "send-media", "send-media-propagated") and len(parts) == 3:
            destination = bytes.fromhex(parts[1])
            media = {}
            content = base64.b64decode(parts[2], validate=True).decode("utf-8")
            if parts[0].startswith("send-media"):
                media = json.loads(content)
                content = media["content"]
            image = [media["image"]["format"], base64.b64decode(media["image"]["data"], validate=True)] if "image" in media else None
            audio = [media["audio"]["mode"], base64.b64decode(media["audio"]["data"], validate=True)] if "audio" in media else None
            if RNS.Identity.recall(destination) is None:
                RNS.Transport.request_path(destination)
                deadline = time.time() + 20
                while RNS.Identity.recall(destination) is None and time.time() < deadline:
                    time.sleep(0.05)
            if not core.send_message(content, destination, parts[0].endswith("propagated"), image=image, audio=audio):
                raise RuntimeError("Actual Sideband send_message rejected the synthetic test message")
            emit({"event": "sent_queued"})
        elif parts[0] == "inspect-media" and len(parts) == 3:
            messages = core.list_messages(bytes.fromhex(parts[1]), limit=32) or []
            message = next(item for item in messages if item["hash"].hex() == parts[2])
            fields = message["lxm"].fields
            result = media_summary(message)
            if LXMF.FIELD_IMAGE in fields:
                from PIL import Image
                with Image.open(io.BytesIO(fields[LXMF.FIELD_IMAGE][1])) as image:
                    image.load()
                    result["image"].update({"width": image.width, "height": image.height})
            if LXMF.FIELD_AUDIO in fields:
                verify_reference(require_lxmf=True, require_lxst=True)
                from sbapp.sideband import audioproc
                mode, data = fields[LXMF.FIELD_AUDIO]
                if mode == LXMF.AM_OPUS_OGG:
                    descriptor, filename = tempfile.mkstemp(suffix=".ogg", dir=args.config)
                    try:
                        with os.fdopen(descriptor, "wb") as output:
                            output.write(data)
                        decoded = audioproc.samples_from_ogg(filename)
                    finally:
                        Path(filename).unlink()
                    result["audio"]["decoded_samples"] = len(decoded)
                else:
                    result["audio"]["decoded_samples"] = len(audioproc.decode_codec2(data, mode)) // 2
            emit({"event": "media-inspected", **result})
        elif parts[0] == "snapshot" and len(parts) == 2:
            messages = core.list_messages(bytes.fromhex(parts[1]), limit=32)
            if messages is None:
                messages = []
            emit({"event": "snapshot",
                  "has_ticket": core.message_router.get_outbound_ticket(bytes.fromhex(parts[1])) is not None,
                  "messages": [
                {"hash": message["hash"].hex(), "source": message["source"].hex(),
                 "state": message["state"], "method": message["method"],
                 "incoming": message["dest"] == core.lxmf_destination.hash,
                 "includes_ticket": LXMF.FIELD_TICKET in message["lxm"].fields,
                 **media_summary(message),
                 "sha256": hashlib.sha256(message["content"]).hexdigest()}
                for message in messages]})
        else:
            raise ValueError("Unsupported Sideband adapter command")


if __name__ == "__main__":
    main()
