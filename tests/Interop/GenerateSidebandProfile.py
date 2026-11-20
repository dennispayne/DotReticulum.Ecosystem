"""Execute pinned Sideband persistence methods, not a reimplementation of its schema.

GUI/audio/telemetry/plugin/resource imports are inert dependency stubs. The actual
SidebandCore database/configuration methods and installed RNS/LXMF run unchanged.
No GUI, networking, telemetry or plugin interoperability is claimed by this adapter.
The output profile contains a fresh private identity: keep it in ignored artifacts.
The optional public fixture contains an empty DB, synthetic config and non-secret conversation observations.
The optional public paper fixture contains generated public identifiers and
synthetic signed/encrypted messages, never private identities.
"""
import argparse
import base64
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import threading
import types
from contextlib import closing


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sideband", required=True, type=Path)
    parser.add_argument("--rns", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--public-fixture", type=Path)
    parser.add_argument("--media-fixture", type=Path)
    parser.add_argument("--map-profile", action="store_true")
    parser.add_argument("--announce-profile", action="store_true")
    parser.add_argument("--voice-profile", action="store_true")
    parser.add_argument("--voice-peers-profile", action="store_true")
    parser.add_argument("--public-voice-peers-fixture", type=Path)
    parser.add_argument("--voice-peer-name")
    parser.add_argument("--paper-profile", action="store_true")
    parser.add_argument("--public-paper-fixture", type=Path)
    parser.add_argument("--verify-paper-fixture", type=Path)
    args = parser.parse_args()
    if args.announce_profile and args.public_fixture:
        parser.error("--announce-profile cannot replace the existing empty public fixture")
    if args.public_voice_peers_fixture and not args.voice_peers_profile:
        parser.error("--public-voice-peers-fixture requires --voice-peers-profile")
    if args.voice_peer_name is not None and not args.voice_peers_profile:
        parser.error("--voice-peer-name requires --voice-peers-profile")
    if args.public_paper_fixture and not args.paper_profile:
        parser.error("--public-paper-fixture requires --paper-profile")
    if args.verify_paper_fixture and not args.paper_profile:
        parser.error("--verify-paper-fixture requires --paper-profile")
    source = args.sideband.resolve()
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if revision != "d069122af14880e43607aed0b71add4c7806fbe0":
        raise RuntimeError("Unexpected Sideband pin")
    core_path = source / "sbapp" / "sideband" / "core.py"
    fingerprint = hashlib.sha256(core_path.read_bytes()).hexdigest()
    if fingerprint != "8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25":
        raise RuntimeError("Unexpected core.py fingerprint")
    main_fingerprint = hashlib.sha256((source / "sbapp" / "main.py").read_bytes()).hexdigest()
    if main_fingerprint != "5b962d9ed2ccbc066bb19865d78c77da9ecf4008900f6f0e7e9dd1eb1c4767aa":
        raise RuntimeError("Unexpected main.py fingerprint")
    sys.path.insert(0, str(args.rns.resolve()))
    import RNS
    import LXMF
    import RNS.vendor.umsgpack as msgpack
    if not Path(RNS.__file__).resolve().is_relative_to(args.rns.resolve()):
        raise RuntimeError("RNS actual-import mismatch")
    if RNS.__version__ != "1.5.7" or LXMF.__version__ != "1.2.1":
        raise RuntimeError("Unexpected RNS/LXMF versions")
    from reference_environment import verify_reference, PINNED_RNS_SHA, PINNED_LXMF_SHA
    os.environ["DOTRETICULUM_UPSTREAM"] = str(args.rns.resolve())
    verify_reference(require_lxmf=True)
    if args.verify_paper_fixture:
        paper_fixture = json.loads(args.verify_paper_fixture.read_text(encoding="utf-8"))
        if paper_fixture["upstream"] != revision or paper_fixture["coreSha256"] != fingerprint or paper_fixture["lxmfRevision"] != PINNED_LXMF_SHA:
            raise RuntimeError("The paper fixture targets another reference")
        for row in paper_fixture["rows"]:
            wire, envelope = msgpack.unpackb(bytes.fromhex(row["storedHex"]))
            observed_paper = LXMF.LXMessage.unpack_from_bytes(wire, original_method=LXMF.LXMessage.PAPER)
            observed_paper.paper_packed = envelope
            if observed_paper.hash.hex() != row["hash"] or observed_paper.as_uri(finalise=False) != row["uri"]:
                raise RuntimeError("Actual LXMF could not reproduce the independently generated paper fixture")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain", "--untracked-files=no", "--", "sbapp"], text=True).strip():
        raise RuntimeError("Sideband reference source has local modifications")
    # Stub imports only; none of these subsystems are called by the persistence methods.
    for package in ["sbapp", "sbapp.sideband"]:
        module = types.ModuleType(package)
        module.__path__ = []
        sys.modules[package] = module
    stubs = {
        "sbapp.sideband.res": {"sideband_fb_data": b""},
        "sbapp.sideband.sense": {"Telemeter": object, "Commands": object,
                                "valid_telemetry_timebase": lambda _: True, "TB_DIFF_GRACE": 0},
        "sbapp.sideband.plugins": {"SidebandCommandPlugin": object, "SidebandServicePlugin": object,
                                  "SidebandTelemetryPlugin": object},
        "sbapp.sideband.mqtt": {"MQTT": object},
        "sbapp.plyer": {},
    }
    for name, members in stubs.items():
        module = types.ModuleType(name)
        module.__dict__.update(members)
        sys.modules[name] = module
    spec = importlib.util.spec_from_file_location("sbapp.sideband.core", core_path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    core = module.SidebandCore.__new__(module.SidebandCore)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    work = output / "generation"
    work.mkdir(mode=0o700)
    core.db = None
    core.db_lock = threading.Lock()
    core.db_path = str(work / "sideband.db")
    core.config_path = str(work / "sideband_config")
    core.app_dir = str(work)
    core.saving_configuration = False
    core.is_client = True
    core.is_service = False
    core.is_daemon = False
    core.state_lock = threading.Lock()
    core.state_db = {}
    core.service_stopped = False
    core.config = {
        "display_name": "Python Profile",
        "dark_ui": False,
        "lxmf_propagation_node": bytes.fromhex("00112233445566778899aabbccddeeff"),
        "last_lxmf_propagation_node": bytes.fromhex("00112233445566778899aabbccddeeff"),
        "propagation_by_default": True,
        "lxmf_periodic_sync": True,
        "lxmf_sync_interval": 1800,
        "lxmf_try_propagation_on_fail": False,
        "telemetry_receive_trusted_only": False,
        "profile_fixture_unknown": {"binary": b"\x00\x01", "enabled": True},
    }
    if args.map_profile:
        core.config.update({
            "map_layer": "atlas", "map_use_atlas": True, "map_lat": 48.8566, "map_lon": 2.3522,
            "map_zoom": 12, "map_use_online": True, "map_use_offline": True,
            "map_storage_file": str(output / "unavailable-external-map.mbtiles"),
        })
    if args.voice_peers_profile:
        core.config.update({"voice_trusted_only": True, "voice_enabled": False})
    core._SidebandCore__db_init()
    if args.announce_profile:
        core._db_upgradetables()
    core._SidebandCore__save_config(no_thread=True)
    if args.public_fixture:
        fixture = {
            "upstream": revision,
            "coreSha256": fingerprint, "mainSha256": main_fingerprint,
            "method": "Actual SidebandCore.__db_init, __save_config, conversation trust/read/name setters and lxm_ingest; inert unused subsystem imports",
            "emptyDatabase": base64.b64encode(Path(core.db_path).read_bytes()).decode(),
            "configuration": base64.b64encode(Path(core.config_path).read_bytes()).decode(),
        }
    own = RNS.Identity()
    peer = RNS.Identity()
    own.to_file(str(work / "primary_identity"))
    own_dest = RNS.Destination(own, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    peer_dest = RNS.Destination(peer, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
    core._SidebandCore__event_conversations_changed = lambda: None
    core._db_create_conversation(peer_dest.hash, name="Reference contact", trust=True)
    conversation_transitions = []

    def observe_conversation(operation):
        conversation = core._db_conversation(peer_dest.hash)
        conversation_transitions.append({
            "operation": operation,
            "trusted": core.is_trusted(peer_dest.hash),
            "unread": bool(conversation["unread"]),
            "name": conversation["name"],
        })

    observe_conversation("created_trusted")
    core.untrusted_conversation(peer_dest.hash)
    observe_conversation("untrusted")
    core.trusted_conversation(peer_dest.hash)
    observe_conversation("trusted")
    core.unread_conversation(peer_dest.hash)
    observe_conversation("marked_unread")
    core._db_conversation_set_name(peer_dest.hash, "Renamed contact")
    observe_conversation("renamed_unread")
    core._db_conversation_set_name(peer_dest.hash, "Reference contact")
    core.read_conversation(peer_dest.hash)
    observe_conversation("read")
    missing_context = bytes.fromhex("ff" * 16)
    core.trusted_conversation(missing_context)
    core.unread_conversation(missing_context)
    core.read_conversation(missing_context)
    missing_conversation_created = core.has_conversation(missing_context)
    core.untrusted_conversation(peer_dest.hash)
    core.db.execute("PRAGMA journal_mode=WAL")
    core.db.execute("PRAGMA wal_autocheckpoint=0")
    core.trusted_conversation(peer_dest.hash)
    core.setpersistent("lxmf.lastsync", 1760000360.25)
    core.setpersistent("lxmf.syncretrying", True)
    media = None
    outgoing_fields = {}
    if args.media_fixture:
        media = json.loads(args.media_fixture.read_text(encoding="utf-8"))
        if media["sideband_sha"] != revision:
            raise RuntimeError("The media fixture targets another Sideband revision")
        encoded_codec2 = next(row for row in media["codec2"] if row["mode"] == 8)
        outgoing_fields[LXMF.FIELD_AUDIO] = [8, bytes.fromhex(encoded_codec2["encoded"])]
    outgoing = LXMF.LXMessage(peer_dest, own_dest, "Native migration outbound", "Outgoing",
                              desired_method=LXMF.LXMessage.PROPAGATED, fields=outgoing_fields)
    outgoing.timestamp = 1760000000.25
    outgoing.state = LXMF.LXMessage.DELIVERED
    outgoing.pack()
    outgoing.method = LXMF.LXMessage.PROPAGATED
    core.setstate("app.displaying", "conversations_screen")
    core.lxm_ingest(outgoing, originator=True)
    observe_conversation("outgoing_unselected")
    core.read_conversation(peer_dest.hash)
    core.setstate("app.displaying", "messages_screen")
    core.setstate("app.active_conversation", peer_dest.hash)
    core.lxm_ingest(outgoing, originator=True)
    observe_conversation("outgoing_selected")
    incoming = LXMF.LXMessage(own_dest, peer_dest, "Native migration inbound", "Incoming",
                              fields={LXMF.FIELD_FILE_ATTACHMENTS: [["reference.txt", b"Python attachment\n"]],
                                      LXMF.FIELD_RENDERER: 2},
                              desired_method=LXMF.LXMessage.DIRECT)
    media_observations = {}
    if media is not None:
        image = media["images"][0]
        image_bytes = base64.b64decode(image["encoded"], validate=True)
        audio = media["opus"]
        audio_bytes = base64.b64decode(audio["encoded"], validate=True)
        incoming.fields[LXMF.FIELD_IMAGE] = [image["format"], image_bytes]
        incoming.fields[LXMF.FIELD_AUDIO] = [audio["mode"], audio_bytes]
        media_observations = {
            "image": {"format": image["format"], "sha256": hashlib.sha256(image_bytes).hexdigest(), "length": len(image_bytes)},
            "audio": {"mode": audio["mode"], "sha256": hashlib.sha256(audio_bytes).hexdigest(), "length": len(audio_bytes)},
        }
    incoming.timestamp = 1760000001.5
    incoming.state = LXMF.LXMessage.DELIVERED
    incoming.pack()
    incoming.method = LXMF.LXMessage.DIRECT
    core.lxm_ingest(incoming)
    observe_conversation("incoming_selected")
    core.setstate("app.displaying", "conversations_screen")
    core.lxm_ingest(incoming)
    observe_conversation("incoming_unselected")
    announce_observations = []
    if args.announce_profile:
        announce_fixture = json.loads((Path(__file__).parent.parent / "Sideband.Tests" /
            "Fixtures" / "sideband-announce-profile.json").read_text(encoding="utf-8"))
        if announce_fixture["revision"] != revision or announce_fixture["core_sha256"] != fingerprint:
            raise RuntimeError("The executed announcement fixture targets another primary revision")
        core.message_router = types.SimpleNamespace(get_outbound_stamp_cost=lambda _: None)
        for row in announce_fixture["rows"]:
            extras = msgpack.unpackb(bytes.fromhex(row["extra"])) if row["extra"] is not None else {}
            core.log_announce(bytes.fromhex(row["source"]), bytes.fromhex(row["data"]),
                              row["type"], link_stats=extras.get("link_stats"))
        announce_observations = [{**item, "dest": item["dest"].hex()} for item in core.list_announces()]
        if len(announce_observations) != 4:
            raise RuntimeError("Actual primary announcement persistence/readback omitted observations")
    voice_peer_observations = None
    if args.voice_peers_profile:
        core.lxmf_destination = own_dest
        identities = [bytes(range(offset, offset + 16)) for offset in (0, 16, 32)]
        for identity_hash, name, trust in zip(identities,
                (args.voice_peer_name if args.voice_peer_name is not None else "Voice-only trusted",
                 "LXMF trust takes precedence", ""), (True, True, False)):
            core._db_create_voice_object(identity_hash, name=name, trust=trust)
        override_destination = RNS.Destination.hash_from_name_and_identity("lxmf.delivery", identities[1])
        core._db_create_conversation(override_destination, name="Untrusted delivery override", trust=False)
        voice_peer_observations = {
            "upstream": revision, "coreSha256": fingerprint,
            "method": "Actual SidebandCore._db_create_voice_object and unchanged name/trust setters, SQLite readback, voice_is_trusted and voice_display_name; synthetic identity hashes, unused core subsystem import stubs.",
            "rows": [],
            "overrideDeliveryHash": override_destination.hex(),
            "overrideDeliveryTrusted": bool(core._db_conversation(override_destination)["trust"]),
            "trustedOnly": core.config["voice_trusted_only"],
        }
        for identity_hash in identities:
            row = core._db_conversation(identity_hash)
            voice_peer_observations["rows"].append({
                "identity": identity_hash.hex(), "type": row["type"], "unread": row["unread"],
                "trust": row["trust"], "name_hex": row["name"].encode("utf-8").hex(),
                "allowed_trusted_only": core.voice_is_trusted(identity_hash),
                "display_name": core.voice_display_name(identity_hash),
            })
        if args.public_voice_peers_fixture:
            args.public_voice_peers_fixture.write_text(
                json.dumps(voice_peer_observations, indent=2) + "\n", encoding="utf-8", newline="\n")
    paper_observations = None
    if args.paper_profile:
        paper = LXMF.LXMessage(peer_dest, own_dest, "Native migration paper", "",
                              desired_method=LXMF.LXMessage.PAPER)
        paper.timestamp = 1760000002.75
        paper.pack()
        uri = paper.as_uri()
        core.lxm_ingest(paper, originator=True)
        restored = core._db_message(paper.hash)
        stored = core.db.execute("SELECT data FROM lxm WHERE lxm_hash=?", (paper.hash,)).fetchone()[0]
        pair = msgpack.unpackb(stored)
        if pair != [paper.packed, paper.paper_packed] or restored["lxm"].as_uri(finalise=False) != uri:
            raise RuntimeError("Actual primary paper persistence/readback did not preserve the envelope")
        if peer_dest.decrypt(paper.paper_packed[16:]) != paper.packed[16:]:
            raise RuntimeError("Actual generated recipient cannot open its paper envelope")
        recipient = output / "paper-recipient"
        recipient.mkdir(mode=0o700)
        peer.to_file(str(recipient / "identity"))
        os.chmod(recipient / "identity", 0o600)
        paper_observations = {
            "upstream": revision, "coreSha256": fingerprint, "lxmfRevision": PINNED_LXMF_SHA,
            "method": "Actual LXMessage.pack/as_uri, SidebandCore.lxm_ingest/_db_message and destination.decrypt; generated identities and synthetic content; unused-subsystem import stubs.",
            "rows": [{
                "hash": paper.hash.hex(), "source": own_dest.hash.hex(), "destination": peer_dest.hash.hex(),
                "sourcePublicKey": own.get_public_key().hex(), "state": restored["state"],
                "method": restored["method"], "content": restored["content"].decode("utf-8"),
                "storedHex": stored.hex(), "wireHex": paper.packed.hex(),
                "paperHex": paper.paper_packed.hex(), "uri": uri,
            }],
        }
        if args.public_paper_fixture:
            args.public_paper_fixture.write_text(
                json.dumps(paper_observations, indent=2) + "\n", encoding="utf-8", newline="\n")
    storage = output / "app_storage"
    storage.mkdir(mode=0o700)
    for name in ["primary_identity", "sideband_config", "sideband.db", "sideband.db-wal", "sideband.db-shm"]:
        if (work / name).exists():
            shutil.copyfile(work / name, storage / name)
            os.chmod(storage / name, 0o600)
    voice_observations = None
    if args.voice_profile:
        verify_reference(require_lxst=True)
        sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Sideband.Tests"))
        from SidebandVoiceGenerateVectors import load_reference
        voice, voice_digests = load_reference(source)
        phone = voice.ReticulumTelephone.__new__(voice.ReticulumTelephone)
        phone.logpath = str(storage / "lxst_call_log")
        phone.call_log = None
        phone.clear_call_log()
        for event in ("outgoing-success", "ongoing-ended", "incoming-success",
                      "outgoing-failure", "incoming-missed", "incoming-missed"):
            phone.log_call(event, peer)
        fresh = voice.ReticulumTelephone.__new__(voice.ReticulumTelephone)
        fresh.logpath = phone.logpath
        fresh.call_log = None
        voice_observations = {
            "voiceSha256": voice_digests["sbapp/sideband/voice.py"],
            "method": "Actual ReticulumTelephone.clear_call_log/log_call/get_call_log with current time and a generated peer identity; fresh __new__ instances avoid audio/network construction.",
            "entries": [{**entry, "identity": entry["identity"].hex()} for entry in fresh.get_call_log()],
            "sha256": hashlib.sha256(Path(phone.logpath).read_bytes()).hexdigest(),
        }
        os.chmod(phone.logpath, 0o600)
    observed = core._db_message(incoming.hash)
    if observed["content"] != incoming.content:
        raise RuntimeError("Upstream persistence readback failed")
    # This diagnostic intentionally excludes WAL, proving both activated flags
    # differ from the main file and must come from the consistent WAL snapshot.
    with closing(sqlite3.connect(Path(core.db_path).as_uri() + "?immutable=1", uri=True)) as main_only:
        main_trust, main_unread = main_only.execute("SELECT trust, unread FROM conv").fetchone()
    metadata = {
        "upstream": revision, "coreSha256": fingerprint, "mainSha256": main_fingerprint,
        "rnsRevision": PINNED_RNS_SHA, "lxmfRevision": PINNED_LXMF_SHA,
        "rnsModule": str(Path(RNS.__file__).resolve()), "rnsVersion": RNS.__version__,
        "lxmfModule": str(Path(LXMF.__file__).resolve()), "lxmfVersion": LXMF.__version__,
        "ownDeliveryHash": own_dest.hash.hex(), "peerDeliveryHash": peer_dest.hash.hex(),
        "incomingHash": incoming.hash.hex(), "outgoingHash": outgoing.hash.hex(),
        "conversationTransitions": conversation_transitions,
        "conversationTrusted": core.is_trusted(peer_dest.hash),
        "conversationUnread": bool(core._db_conversation(peer_dest.hash)["unread"]),
        "mainDatabaseTrusted": bool(main_trust), "mainDatabaseUnread": bool(main_unread),
        "missingConversationCreated": missing_conversation_created,
        "announces": announce_observations,
        "lastSynchronization": core.getpersistent("lxmf.lastsync"),
        "synchronizationRetrying": core.getpersistent("lxmf.syncretrying"),
        "walBytes": (storage / "sideband.db-wal").stat().st_size,
        "disclosure": "Actual unchanged persistence methods with inert unused GUI/telemetry/plugin/resource imports",
        "media": media_observations,
        "mapProfile": {key: value for key, value in core.config.items() if key.startswith("map_")} if args.map_profile else None,
        "voiceLog": voice_observations,
        "voicePeers": voice_peer_observations,
        "paper": paper_observations,
    }
    (output / "observations.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    if args.public_fixture:
        fixture["conversationTransitions"] = conversation_transitions
        fixture["missingConversationCreated"] = missing_conversation_created
        args.public_fixture.write_text(json.dumps(fixture, indent=2) + "\n", encoding="utf-8")
    core.db.close()
    shutil.rmtree(work)
    print(json.dumps({"status": "ok", "observationsFile": "observations.json"}))


if __name__ == "__main__":
    main()
