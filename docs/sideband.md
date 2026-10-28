# Sideband source and scope inventory

## Status

**Not implemented in DotReticulum.Ecosystem.** This document records an upstream
source inventory and integration boundaries; it is not an implementation or a
claim of Sideband compatibility. No Sideband project, application tests, UI,
platform adapters, packaging or live application interoperability exist here.
All application-level contracts below are therefore `missing`; a partial status
means only that a lower-level protocol library exists, not that Sideband works.

## Reference provenance

The primary source is hosted on Reticulum at
`rns://7649a50d84610232d1416b41d2896aff/reticulum/sideband`. The source used
for this inventory was fetched directly from that address by the native
.NET `rngit clone` client, without a GitHub object seed. The bare repository is
local, ignored investigation data under `artifacts\sideband`; no upstream source
or assets are committed.

| Reference | Revision |
|---|---|
| Primary default branch at inspection | `d069122af14880e43607aed0b71add4c7806fbe0`, tree `b1779ea9fc35ca22abbb1de5f83a9abe4242c33c`; commit date 2026-10-05 |
| Primary `2.2.0` tag | `cba73130287f0d8b9deb7da4d20ceec09b7fe394`, tree `26721b6a102a28f344eb0e8d14a2e4868ee17a48` |
| `sbapp/main.py` version at primary HEAD | `2.2.0` |
| GitHub mirror's observed `2.2.0` commit | `ef572b58e88ff4e4326d51778336922a53b2a03f`, tree `4414790d02a9a3aca10da002f006ca1e66748f19` |

The primary HEAD is three commits beyond its `2.2.0` tag and still reports
version `2.2.0`. The mirror's release commit/tree differs from the primary
release tag/tree; it is not the authority used for the source inventory.
`rngit clone` completed successfully and `git fsck --full --strict` passed on
the fetched bare repository (154.18 MiB of local packed Git objects). These
checks establish Git object consistency, not publisher authentication.

SHA-256 digests below are over the exact Git blob bytes at primary HEAD.

| Primary source path | SHA-256 |
|---|---|
| `README.md` | `6fe7b1f6f9b2db73201b2c6524bc66c90873badc4b7b24fba03189bca40fae42` |
| `LICENSE` | `1349a4b6148492b44f629e64eed676612e234fe9a839e4f3b277c1482c8849f1` |
| `setup.py` | `9905d709976990b1a0b4478c4d5ab51ce2b5f08da9ea4693eb6398dda479069e` |
| `sbapp/main.py` | `5b962d9ed2ccbc066bb19865d78c77da9ecf4008900f6f0e7e9dd1eb1c4767aa` |
| `sbapp/sideband/core.py` | `8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25` |
| `sbapp/sideband/plugins.py` | `47a051a26a21d53aa8d6232cfc438dfac5e9396814e4bbc6c0149fb74643b6ac` |
| `sbapp/sideband/sense.py` | `80418b1aa4de638f2c65f1bd64e5ea0466378a6f0bc682781672ef89b93a7dab` |
| `sbapp/sideband/voice.py` | `51e61510ea5eee832e7b9ad452459998c76561d970e0e773a253fe17cc7f2ab8` |
| `sbapp/services/sidebandservice.py` | `3b6a481ffe8130dc12340260dba4e73994ed7af467d8ba4b310620d23744c011` |
| `sbapp/buildozer.spec` | `6d4a83fb5466310e0f2b2071e5ed627cdb45ba4b4857592aa52dce3590527441` |

## Licensing boundary

The primary `LICENSE` identifies **Creative Commons
Attribution-NonCommercial-ShareAlike 4.0 International (CC BY-NC-SA 4.0)**.
This is not the Reticulum License in this repository and is distinct from the
CC BY-NC-ND license recorded for LXST. The upstream README also explicitly
warns against unlicensed machine-generated copies and derivative projects.
Keep this repository's Reticulum License and notice unchanged; do not copy,
translate, bundle or relicense Sideband source, UI, documentation, fonts, maps,
sounds, icons or other assets. The embedded `kivymd`, `plyer`, `mapview` and
`libs/able` trees and package assets have their own licensing/provenance
requirements. This is an engineering inventory, not legal advice; distribution
scope and rights require review before implementation ships.

## Contract inventory

Status definitions: `missing` = no Sideband application contract in this
repository; `partial` = a lower-level DotReticulum library exists, but the
Sideband application behavior/integration does not. Source inspection is not
runtime evidence.

| Contract | Status | Upstream contract and source |
|---|---|---|
| App shell and interaction | missing | One Kivy/KivyMD application with conversation, message, announce, telemetry, voice, hardware, keys, object-detail, guide, layout and utility screens; `sbapp/main.py`, `sbapp/ui/*.py`, `sbapp/*.kv`, `README.md`. |
| Contacts, discovery and conversation types | missing | Delivery/peer announces, names and trust, per-peer appearance and controls; conversation types include P2P, group, broadcast and voice; `sbapp/sideband/core.py`, `sbapp/ui/announces.py`, `sbapp/ui/conversations.py`. |
| LXMF messaging and propagation UI | partial | The existing `LXMF` library implements protocol-level messaging/propagation separately, but Sideband's inbox, conversation lifecycle, announce directory, peer policy and user controls are absent; `sbapp/sideband/core.py`, `sbapp/ui/messages.py`, `docs/lxmf.md`. |
| Text, image/file and audio messages | partial | Upstream offers LXMF text, image/file attachment flows and Codec2/Opus audio messages for constrained links; compose/receive, file selection, persistence and playback UI are absent; `README.md`, `sbapp/ui/messages.py`, `sbapp/sideband/core.py`, `sbapp/sideband/audioproc.py`. |
| Paper messages and `lxm://` imports | partial | Encrypted QR/paper message and message URI flows are advertised; only lower-level LXMF formats exist here, not Sideband's identity import, camera/QR, export or link handling; `README.md`, `sbapp/main.py`, `sbapp/ui/keys.py`. |
| LXST calls and push-to-talk | partial | Sideband adds contact authorization, call UI, half/full-duplex modes, audio device selection and call history around LXST; no Sideband telephony integration exists. `setup.py` requires `lxst>=0.5.4`, while this repository's LXST target is 0.4.5, so a source/API compatibility audit and upstream-version decision are required before reuse; `sbapp/sideband/voice.py`, `sbapp/ui/voice.py`, `docs/lxst.md`. |
| Telemetry, location and situational awareness | missing | Sensor streams include time, identity/information, battery, location, physical link, environment, motion, resource, Reticulum transport, LXMF propagation, connections and interfaces; peer telemetry, tracking, live/offline maps and geospatial calculations are app behavior; `sbapp/sideband/sense.py`, `sbapp/sideband/geo.py`, `sbapp/ui/telemetry.py`, `README.md`. |
| Remote commands, queries and plugins | missing | Command, service and telemetry plugin base types; built-in ping/echo/signal/telemetry request behavior; custom telemetry sensors; configurable plugin loading and example plugins. This is an application extension contract, not part of the existing .NET `Telemetry` tool; `sbapp/sideband/plugins.py`, `sbapp/sideband/core.py`, `docs/example_plugins/*`, `README.md`. |
| Reticulum setup and interfaces | partial | DotReticulum supplies the lower-level RNS stack, but Sideband's settings, custom interface configuration, discovery/management UI and Android custom-interface loading are absent; `sbapp/sideband/core.py`, `sbapp/ui/hardware.py`, `sbapp/buildozer.spec`. |
| Long-lived service and OS integration | missing | Android uses a foreground Python service, Android notification/location/permission APIs, wake/multicast locks, Bluetooth/RNode discovery and background-power handling; the app and service share state and RPC. No mobile service adapter or verified Android support exists here; `sbapp/services/sidebandservice.py`, `sbapp/main.py`, `sbapp/sideband/core.py`. |
| Persistence, restart and migration | missing | App config, identity, SQLite message/conversation/announce/telemetry/state tables, LXMF storage, logs, caches, exports, audio and web-share state; no Sideband schema, migration or restart behavior exists in this repository; `sbapp/sideband/core.py`, `sbapp/sideband/voice.py`. |
| CLI and headless operation | missing | `sideband` is primarily a GUI entry point; observed options are `-v/--verbose`, `-c/--config`, `-r/--rnsconfig`, `-d/--daemon`, `-i/--interactive`, `--export-settings`, `--import-settings`, and `--version`. Daemon/interactive modes share the application core; `setup.py`, `sbapp/main.py`, `sbapp/sideband/console.py`. |
| Native packaging and upgrades | missing | Upstream documents Android APK, Linux AppImage/PyPI, macOS DMG and Windows ZIP, plus in-app repository/update flows. No Sideband package, signing, migration or native dependency matrix is implemented or verified here; `README.md`, `setup.py`, `build_appimage.sh`, `winbuild.bat`, `sbapp/buildozer.spec`. |

## Upstream architecture and platform facts

- `sbapp/main.py` owns the Kivy/KivyMD UI. `SidebandCore` in
  `sbapp/sideband/core.py` owns configuration, RNS/LXMF integration,
  conversations, announces, SQLite persistence, telemetry and plugin dispatch.
  The core distinguishes standalone, service, client and daemon operation;
  Android's UI/service communicate through shared state and a local RPC loop.
- The default application directory is `~/.config/sideband` on desktop and
  the Android app sandbox under `io.unsigned.sideband/files`. Core state includes
  `app_storage/sideband_config`, `primary_identity`, `sideband.db`, a Reticulum
  config directory, LXMF storage, telemetry-exclusion data, logs and temp files.
  Its SQLite schema includes `lxm`, `conv`, `announce`, `telemetry`, `state` and
  `persistent` tables. This is an observed upstream format, not a proposed
  compatible .NET format.
- `setup.py` declares `rns>=1.5.7`, `lxmf>=1.2.0`, `lxst>=0.5.4`, Kivy, NumPy,
  Pillow, QR, Markdown and image utilities, and `pycodec2`; it also includes
  platform-conditional `pyobjus`, `pyogg` and `audioop-lts`. Android's
  `sbapp/buildozer.spec` targets API 33/minimum API 24 and arm64-v8a, builds a
  foreground service, and requests networking, notification, wake-lock,
  location, Bluetooth, storage, microphone and audio permissions.
- Upstream documents Android, Linux, macOS and Windows. That is an upstream
  packaging/support statement, not validation of a future .NET implementation.
  The current ecosystem repository records Windows/Linux feature testing;
  macOS application-level feature acceptance and Android/mobile support are
  unverified. Linux voice/audio and clipboard behavior also depends on separately
  installed native/runtime components.
- The checked-in upstream `README.md` describes direct peer-to-peer operation
  without a required Internet service, optional Reticulum transports, encrypted
  messaging, local/offline maps, plugins, remote control and Android transport
  use. These are source claims only; this inventory did not execute upstream
  app flows or confirm platform packaging.

## DotReticulum.Ecosystem integration boundary

`DotReticulum`, `LXMF` and `LXST` are reusable protocol-layer candidates, not a
drop-in Sideband application. RNS is pinned to 1.5.7 here, matching the
Sideband package's declared minimum; LXMF 1.2.1 exceeds its declared minimum.
LXST's target is older than Sideband's declared minimum and needs explicit
compatibility work. Sideband's sensor model, peer/conversation state, local
database, service, extension surface, UI, update flow and platform integrations
remain separate work.

A future implementation should keep protocol/domain services testable without
UI or devices, then place persistence and OS-specific services behind explicit
interfaces. Android background execution, permission/location/notification
handling, audio devices and desktop integration need dedicated platform adapters
and platform tests. Upstream's Python-module plugin loading and custom RNS
interface imports are not directly compatible with trimming/NativeAOT-safe
design; whether and how to preserve those extension contracts is unresolved.
No GUI toolkit or supported-platform subset is selected by this inventory.

Before implementation, confirm the required first-release platforms (desktop
only, Android, macOS, or some combination), required background/service behavior,
whether NativeAOT is mandatory for each target, and whether third-party plugin
loading is a required compatibility contract.
