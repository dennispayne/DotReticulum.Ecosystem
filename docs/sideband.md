# Sideband source and scope inventory

## Status

**Initial Windows/Linux desktop client implemented; upstream application parity
is not claimed.** The current native C# application provides a small contacts
and text-conversation workflow over the repository's .NET Reticulum and LXMF
libraries, with bounded local JSON persistence. Windows NativeAOT publishing,
CLI maintenance modes, a headless UI workflow, and native Pester acceptance
have been exercised. Actual unmodified primary Python Sideband daemon-core
messaging passes in both directions, directly and through a Python
transport/propagation node. Separate RNS/LXMF peer cases exercise larger file fields.
Manual/automatic propagation-node selection, one-shot failed-delivery retry, inbox retrieval,
restart persistence and upstream-shaped JSON settings exchange are implemented.
Periodic inbox synchronization and its retry schedule persist over restart.
Bounded file attachments can be sent, received, persisted and explicitly saved.
Image/audio composition and playback, voice, telemetry,
plugins, mobile operation, broad upstream configuration, and upstream database
migration of all profile semantics remain absent. A bounded offline importer
activates identity, supported settings, LXMF conversations, text/file history
and delivery methods from upstream profiles.

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
| App shell and interaction | partial | Original Avalonia UI uses an upstream-inspired dark/teal top bar, collapsible navigation, and Conversations, Contacts, Connectivity, Backup & Keys, Preferences, Utilities, and Guide screens. Supported screens expose real local contacts/history, runtime connection state, effective config path, public identity hashes, and persisted display/theme settings. The announce stream, objects/devices, map, voice, telemetry, hardware management, plugins, and other upstream screens are visibly unavailable rather than simulated; upstream layouts/assets are not copied; `src/Sideband.Desktop/MainWindow.axaml`, `src/Sideband.Desktop/MainWindow.axaml.cs`, `sbapp/main.py`, `sbapp/ui/*.py`, `sbapp/*.kv`. |
| Contacts, discovery and conversation types | partial | The app accepts an LXMF delivery hash and display name, records observed delivery announces, and displays a contact list. Trust controls, peer appearance/settings, groups, broadcasts and voice conversations are absent; `src/Sideband/SidebandClient.cs`, `src/Sideband/SidebandContact.cs`, `sbapp/sideband/core.py`, `sbapp/ui/announces.py`, `sbapp/ui/conversations.py`. |
| LXMF messaging and propagation UI | partial | Text send/receive, automatic/manual node selection, persisted periodic fetches, cancellation, one-shot failed-delivery propagation retry and restart retrieval reuse `LxmRouter`. Actual primary Sideband daemon-core direct/propagated round trips pass separately from Python LXMF endpoint tests. Trusted-conversation ticket issuance and full message presentation/settings remain missing. Sent via propagation means node storage, not recipient delivery; `src/Sideband/SidebandClient.cs`, `tests/Sideband.Tests/SidebandLiveInteropTests.cs`, `sbapp/sideband/core.py:48–93,3966–4001,4811–4828,5156–5227,5634–5644`, `docs/lxmf.md`. |
| Text, image/file and audio messages | partial | Text and file composition, receipt, content-addressed persistence and explicit file export exist. Two-way 2 MiB file fields execute actual pinned Python LXMF and survive native-client restart. The UI displays filenames/sizes and can save the selected message's first file; no received content opens automatically. Image/audio composition, rendering/playback and recording remain missing; `src/Sideband/SidebandAttachmentStore.cs`, `src/Sideband.Desktop/MainWindow.axaml.cs`, `tests/Interop/sideband_attachment_node.py`, `sbapp/sideband/core.py:5189–5196`. |
| Paper messages and `lxm://` imports | missing | No paper-message/QR creation or import flow, camera/QR, export or URI handling. Offline profile migration imports its identity separately; `README.md`, `sbapp/main.py`, `sbapp/ui/keys.py`. |
| LXST calls and push-to-talk | missing | A disabled UI button explicitly reports voice unavailability. No Sideband audio devices, call authorization, duplex controls or call history are implemented. The LXST library now targets Sideband's required 0.5.4, but that does not implement application-level voice integration; `src/Sideband.Desktop/MainWindow.axaml`, `sbapp/sideband/voice.py`, `sbapp/ui/voice.py`, `docs/lxst.md`. |
| Telemetry, location and situational awareness | missing | Sensor streams include time, identity/information, battery, location, physical link, environment, motion, resource, Reticulum transport, LXMF propagation, connections and interfaces; peer telemetry, tracking, live/offline maps and geospatial calculations are app behavior; `sbapp/sideband/sense.py`, `sbapp/sideband/geo.py`, `sbapp/ui/telemetry.py`, `README.md`. |
| Remote commands, queries and plugins | missing | Command, service and telemetry plugin base types; built-in ping/echo/signal/telemetry request behavior; custom telemetry sensors; configurable plugin loading and example plugins. This is an application extension contract, not part of the existing .NET `Telemetry` tool; `sbapp/sideband/plugins.py`, `sbapp/sideband/core.py`, `docs/example_plugins/*`, `README.md`. |
| Reticulum setup and interfaces | partial | The user explicitly connects through `ReticulumNode.Open` using a saved/default config or `--rnsconfig`; the Connectivity page displays actual shared-role and effective config file only after the node starts, and gives `rnstatus` guidance. Offline, it reports that ownership/health is unknown. It never rewrites the global config or checks whether an `rnsd` executable happens to be installed. Shared-instance election is inherited from DotReticulum; compatible reachable local owners may be reused, otherwise the stack may fall back to ownership. Interface editing/discovery and Android interface loading are absent; `src/Sideband/SidebandClient.cs`, `src/Sideband.Desktop/MainWindow.axaml`, `external/DotReticulum/src/DotReticulum.Applications/ReticulumNode.cs`, `sbapp/sideband/core.py`, `sbapp/ui/hardware.py`, `sbapp/buildozer.spec`. |
| Long-lived service and OS integration | missing | The desktop app owns a live client while its window runs. Android foreground services, notifications, location/permission APIs, wake/multicast locks, Bluetooth/RNode discovery and background-power behavior are not implemented or validated; `sbapp/services/sidebandservice.py`, `sbapp/main.py`, `sbapp/sideband/core.py`. |
| Persistence, restart and migration | partial | Atomic native JSON/settings/history persist over restart. Offline profile import reads actual `primary_identity`, binary `sideband_config` and bounded SQLite/WAL snapshots into an absent destination; activates contacts/text/files/methods, archives unimplemented semantics and never modifies source or merges existing state. Trust/unread, voice objects, non-file fields, telemetry and persistent DB semantics remain archive-only; interfaces and router storage are not migrated. See migration limits below; `src/Sideband/SidebandProfileImporter.cs`, `tests/Sideband.Tests/SidebandProfileImportTests.cs`, `sbapp/main.py:209–273`. |
| CLI and headless operation | partial | Help/version, init/check, data/config selection, JSON settings exchange and the native extension `--import-profile SOURCE --data-dir NEW_DIRECTORY` exist. Profile import is offline and supports cancellation; conflicting maintenance/network options fail before opening state/UI. Unsupported keys are reported without values. Upstream verbose, daemon and interactive modes remain missing; `src/Sideband.Desktop/Program.cs`, `tests/Pester/SidebandProfileImport.Tests.ps1`, `sbapp/main.py`. |
| Repository server | missing | Upstream's optional "repository server" is a local-network HTTPS mirror for static Sideband/RNode software and guide downloads plus a package list (default port 4444); it is not an RNS/rngit Git hosting server. The native Sideband app does not host this service. The ecosystem's `rngit` service is separate and is not exposed here; the Utilities page labels the mirror unavailable; `sbapp/sideband/core.py`, `sbapp/ui/layouts.py`, `src/Sideband.Desktop/MainWindow.axaml`. |
| Native packaging and upgrades | partial | Windows and Linux NativeAOT publishes succeed with trimming/linker warnings treated as errors. Package the full publish directory, including Windows `sideband.exe`, `av_libglesv2.dll`, `libHarfBuzzSharp.dll`, `libSkiaSharp.dll` and the three included third-party notice files; Linux `sideband`, `libHarfBuzzSharp.so`, `libSkiaSharp.so`, `libSystem.IO.Ports.Native.so` and the same notices. The notices include Avalonia/Skia/HarfBuzz MIT texts, ANGLE's BSD license, and .NET serial runtime notices. Installer/signing, self-upgrade and other upstream package formats remain absent; `src/Sideband.Desktop/Sideband.Desktop.csproj`, `src/Sideband.Desktop/THIRD-PARTY-NOTICES*.txt`, `README.md`, `setup.py`, `build_appimage.sh`, `winbuild.bat`, `sbapp/buildozer.spec`. |

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
drop-in Sideband application. The selected initial scope is Windows/Linux
desktop with native C# and Avalonia 12.1.4; the app references `LXMF` and
`DotReticulum.Applications`, and intentionally does not reference LXST. RNS is
pinned to 1.5.7, matching Sideband 2.2.0's declared minimum; LXMF 1.2.1 exceeds
its declared minimum. LXST targets the required 0.5.4, but this application does
not yet integrate its call/device APIs. Voice therefore remains disabled.

The app project is `src/Sideband.Desktop/Sideband.Desktop.csproj` (assembly and
executable name `sideband`); its protocol/application library is `src/Sideband`
(assembly `Sideband.Core`, namespace `Sideband`). The library uses source-
generated JSON rather than reflection serialization. Native plugins, upstream
full SQLite migration, mobile services and platform-specific sensors/audio are not
implemented. The bounded importer below is not full upstream profile parity.
The current native client is an initial desktop app, not an
upstream replacement.

## Settings, propagation and lifecycle acceptance

The Connectivity screen persists a manual `lxmf.propagation` override or leaves
it empty for automatic selection, displays the selected/remembered node, fetches
the local identity's inbox, and exposes cancellation and contextual failures.
Manual and last-selected addresses are stored/exported separately. Selection
preserves upstream's equal-or-fewer-hop replacement and rejects timestamps more
than 300 seconds in the future. Valid older announcements remain eligible.
The inspected reference ignores its decoded `node_active` flag; vectors record
that upstream defect and native selection preserves it. A selected inactive
node may reject transfers; selection is not a storage/delivery guarantee.
Receive, announce and delivery-state persistence share a
bounded serial worker (128 updates), with explicit overload reporting; shutdown
drains accepted updates. Disconnect/disposal cancel pending path lookup and
retrieval before releasing the node/identity. Concurrent disposers await the
same completion, and observer exceptions cannot disable other observers.

`GenerateSettingsVectors.py` executes the pinned primary `main.py` export
assignment and bytes-to-hex conversion loop, with supplied test configuration
and actual RNS helpers. Source SHA-256 is
`5b962d9ed2ccbc066bb19865d78c77da9ecf4008900f6f0e7e9dd1eb1c4767aa`.
It does not launch upstream's GUI, initialize its full defaults or establish
SQLite import compatibility. Unsupported JSON settings round-trip without
claiming their corresponding functionality.

The same generator's `--propagation-output` executes the actual primary
`PropagationNodeDetector` with injected clock, path-hop and link-stat dependencies;
it uses the actual pinned LXMF announce validator and MessagePack functions.
The nine cases include manual priority, closer/equal/farther nodes, the exact
future boundary, aged announces and the ignored inactive flag. Canonical
`core.py` SHA-256 is
`8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25`.
These dependency-controlled policy vectors are not live peer evidence.

Periodic synchronization defaults to disabled, with the reference's 43,200-second
interval and 60-second checks while connected. The first enabled tick initializes
the persisted schedule without fetching; later checks use the strict
`now > last_sync + interval` boundary. The first busy admission adds 360 seconds
to the previous schedule base; a second busy admission returns to the normal
interval. Queue acceptance records an attempt, not successful delivery. Actual
transfer failures are reported without terminating the service, and disabling
the schedule does not cancel an already accepted fetch. The native policy
requires positive whole-second intervals; fractional/nonpositive imported
values fail transactionally instead of being silently coerced.

`--synchronization-output` executes the actual `_periodic_jobs` synchronization
branch with injected clock, persistence, request-admission and logging
dependencies, at the same recorded `core.py` revision/digest. Eight vectors
cover initialization, disabled/before/exact/due ticks and both busy-retry states.
Last-attempt/retry state is native JSON persistence, not migration of upstream's
SQLite `persistent` table. Disposal cancels and awaits the owned periodic task.

Windows validation uses `--artifacts-path artifacts\builds\fleet-sideband`:
`dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release` with
`--filter "Category!=Live"` passed the settings/UI/lifecycle cases.
Strict `dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj -c Release
-r win-x64 --artifacts-path artifacts\builds\fleet-sideband-native
-p:RuntimeIdentifiers=win-x64 -p:ILLinkTreatWarningsAsErrors=true
-p:IlcTreatWarningsAsErrors=true` passed. Published Native and GUI Pester gates
passed 3/3 on Windows and WSL/Linux: identity restart, settings exchange/error
transactionality and an actual rendered validation flow. The analogous Linux
publish used `/home/dennis/fleet-sideband-native`, `linux-x64` and provisioned
.NET/MSBuild plus clang; WSLg rendering used software graphics. These gates do
not exercise physical audio or arbitrary desktop environments.

With the pinned Windows reference environment, `dotnet test
tests\Sideband.Tests\Sideband.Tests.csproj -c Release --artifacts-path
artifacts\builds\fleet-sideband` selects the peer cases with `DOTRETICULUM_LIVE=1`.
The direct topology is native Sideband to actual Python LXMF over TCP.
The propagation topology is native Sideband and a separate actual Python
client connected to a Python transport/propagation node: messages deposit and
retrieve in both directions, retrieval deduplicates, and local history/identity
and propagation settings survive restart. The Python node's actual announce
automatically selects the node; switching to manual and back retains its
remembered address over restart. Legacy manual-only JSON state can also switch
to automatic selection without losing its known node. These are LXMF-peer tests, not an
actual upstream Sideband application or GUI peer. Separate actual application cases
are described below. The Linux equivalent uses
`--artifacts-path /home/dennis/fleet-sideband-managed` and the isolated Linux
reference environment. A separate message deposited after manual exchange is
retrieved autonomously by the real 60-second scheduler, without calling manual
sync; both messages and the enabled interval survive restart. The case runs
for approximately two minutes on each platform. No random packet-loss impairment,
hours-long soak or published-native scheduled-transfer flow is implied.

Failed delivery captures eligibility when queued: the default
`lxmf_try_propagation_on_fail=True` applies only when a propagation node is known.
The actual `message_notification` oracle records one retry, reset attempt/deadline
state, repacking and suppression after stamp-generation failure. Native messages
have immutable terminal completion, so retry uses a fresh message with identical
timestamp, source, content, title and signed fields/hash, updates the same history
record/method, and never enables a second fallback. Preparation/stamp exceptions
are reported without retry; cancellation is not failure. As upstream, eligibility
also permits one further propagated attempt if the original method was propagated.
The GUI and JSON import/export expose the policy. Real Python link-admission
refusal verifies enabled/disabled behavior, recipient retrieval and continued
direct service after admission recovers on Windows/Linux (two cases per host,
approximately four minutes). This is deliberate refusal, not random packet loss.

`sideband_application_node.py` separately imports the actual pinned Sideband
`SidebandCore`, verifies its path/revision/digest and starts its desktop daemon
mode with isolated profile/RNS directories. There are **no source replacements,
import stubs or callback substitutions** in these application cases. Actual
`send_message`, `request_lxmf_sync` and `list_messages` calls observe two-way text,
directly and with both peers connected to a Python transport/propagation node.
`DOTSIDEBAND_LIVE=1`, `DOTRETICULUM_LIVE=1` and
`DOTRETICULUM_SIDEBAND_UPSTREAM` select these cases, alongside the pinned Python/RNS
environment. This proves daemon-core application flows, not Kivy GUI interaction.

### Offline upstream profile migration

Stop both applications, then run
`sideband --import-profile UPSTREAM_PROFILE --data-dir NEW_DIRECTORY`.
The source may be a profile root or its `app_storage`. The target must not exist;
there is no silent merge/overwrite. An isolated sibling staging directory and
atomic final directory move commit identity/settings/history together. Failures,
cancellation and concurrent competing imports leave an existing destination
untouched. Source fingerprints before/after copying reject concurrent changes;
the original is never opened for writing. Private copies include WAL/SHM so
committed WAL-only messages are not discarded. SQLite opens only the private copy.

Limits are 1 MiB binary config, 128 MiB each DB/WAL, 8 MiB SHM/cell, 64 MiB query
allocation, 1,000 conversations and 10,000 messages, plus existing native
message/file/history limits. Unsupported schemas, malformed records, unrelated
messages, orphan conversations, invalid names and oversize inputs fail atomically.
The native identity is preserved at `reticulum/storage/sideband`; imported
interfaces are intentionally not activated. Configure the new private Reticulum
directory before connecting. Windows needs system `winsqlite3.dll`, Linux
`libsqlite3.so.0`; neither is bundled. macOS resolution is unverified.

Unimplemented data stays in `imported-profile`: original binary config and SQLite
snapshot, but no redundant private-identity copy. Unknown binary configuration
uses hexadecimal JSON projections; the original MessagePack remains authoritative.
Trust/unread, voice, non-file message fields, telemetry and persistent DB semantics
are not activated. Other app-storage files, router tickets/ratchets/queues and
interfaces are not migrated. Remote historical signatures cannot be verified
without sender identities; imported-identity signatures and record hashes are
checked. Protect the Windows destination with account ACLs; Unix staging/key
permissions are owner-only. Temporary key/config buffers are zeroed.

`GenerateSidebandProfile.py` executes actual pinned persistence methods, verifies
RNS/LXMF actual imports and source hashes, and emits ephemeral identity/WAL profiles.
Its unused GUI/telemetry/plugin/resource imports are explicitly inert stubs;
this is independent file-format evidence, **not** the unmodified application
test above. The committed fixture contains only empty upstream-created SQLite
and non-secret synthetic settings; private identities are generated at runtime.

File attachments reuse `LxmField.FileAttachments` and the existing LXMF
message serializer: `[[filename, binary_data], ...]`. The separate thin adapter
executes actual `LXMessage`/`LXMRouter` APIs and hashes the received file data.
It uses Python `delivery_limit=8192` KB, explicitly above its default; native
inbound Resources allow 8 MiB. Native safety ceilings are 2 MiB per file,
4 MiB/eight files per received message and 128 MiB/4,096 content-addressed files
on disk. These are explicit bounded native policies, not upstream limits.
Hash verification precedes export; existing export destinations are never
overwritten (including JSON settings exports). Malformed/unsafe names, corruption, quota exhaustion and
persistence failures are reported. Orphaned content after a failed history
write is bounded but is not automatically garbage-collected. No image/audio
decoder, automatic opening, physical recording or published-native file-flow
acceptance is claimed.

### Python plugin compatibility

The inspected upstream `plugins.py` exposes Python subclass contracts
`SidebandCommandPlugin`, `SidebandServicePlugin` and
`SidebandTelemetryPlugin`. Their constructors retain the actual Python
Sideband core; callbacks receive Python arguments or a telemeter object.
Lifecycle state and `get_sideband()` therefore do not constitute a language-
neutral plugin ABI. This app neither loads those plugins nor substitutes a
.NET extension interface and calls it compatible. Preserved plugin settings
from JSON import do not activate Python code. Python-plugin execution,
isolation and access to the full required core remain missing software work,
not a hardware limitation.

## Current implementation evidence

The implementation targets the upstream 2.2.0 release tag
`cba73130287f0d8b9deb7da4d20ceec09b7fe394` (the primary default branch inspected
for the inventory remains three commits beyond that release tag). The version
label is a target, not a parity claim. The upstream source is not committed.

Validated commands and outcomes:

| Command | Result |
|---|---|
| `dotnet build DotReticulum.Ecosystem.slnx -c Release --artifacts-path artifacts\builds\fleet-resume` | Passed, zero warnings/errors |
| Full `dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release` with separate Windows/Linux artifacts paths above, all live/application/profile prerequisites enabled | 48/48 on each host, no skips; includes actual application, refusal/recovery, scheduled fetch, file and migration cases |
| `dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --artifacts-path artifacts\builds\fleet-sideband --filter FullyQualifiedName~FailedDirectDelivery` with pinned live paths | Windows 2/2; Linux equivalent 2/2: actual refusal, one-shot propagation policy and direct recovery |
| Same command with `--filter FullyQualifiedName~ActualPrimarySidebandDaemon`, `DOTSIDEBAND_LIVE=1` and the pinned Sideband source | 2/2 on each host: actual unmodified application-core direct/propagated round trips |
| Same command with `--filter FullyQualifiedName~SidebandProfileImportTests` and `DOTRETICULUM_SIDEBAND_PROFILE` pointing to generated observations/profile | Windows 18/18; native process checks separately verify actual WAL profile import on both hosts |
| `dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj -c Release -r win-x64 --artifacts-path artifacts\builds\fleet-sideband-native -p:RuntimeIdentifiers=win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true` | Passed; strict Windows NativeAOT |
| Linux `dotnet publish src/Sideband.Desktop/Sideband.Desktop.csproj -c Release -r linux-x64 --artifacts-path /home/dennis/fleet-sideband-native -p:RuntimeIdentifiers=linux-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true` | Passed; strict Linux NativeAOT |
| `& .\tests\run-pester.ps1 -Path tests\Pester\Sideband.Tests.ps1 -Tag @('Native','GUI')` with `DOTRETICULUM_SIDEBAND_BINARY` set to the Windows publish | 3/3 passed: help/version, identity restart, transactional settings exchange/errors and actual rendered send validation |
| Linux PowerShell `& ./tests/run-pester.ps1 -Path tests/Pester/Sideband.Tests.ps1 -Tag Native,GUI` with the Linux binary and WSLg/software graphics | 3/3 passed; same native maintenance and rendered validation flows |
| `& .\tests\run-pester.ps1 -Path tests\Pester\SidebandProfileImport.Tests.ps1` with published binary and `DOTRETICULUM_SIDEBAND_PROFILE` | 4/4 on each host: identity/config/WAL history/file import, unchanged source, occupied-target refusal, conflicting-option rejection and malformed-input rollback/retry |

SHA-256 of the accepted published executables:

| Executable | SHA-256 |
|---|---|
| Windows `artifacts\builds\fleet-sideband-native\publish\Sideband.Desktop\release_win-x64\sideband.exe` | `43813fbea6c7100b81505238faae8353ed6b6fc634b304ee3e8dd0009301c665` |
| Linux `/home/dennis/fleet-sideband-native/publish/Sideband.Desktop/release_linux-x64/sideband` | `8ad843cad8110cf76d66615997635e7fab505564be32f43e6214f5a98be5ecc7` |

The Windows output contains `sideband.exe` and native GUI dependencies
`av_libglesv2.dll`, `libHarfBuzzSharp.dll`, and `libSkiaSharp.dll`. Linux output
contains the native `sideband` executable plus `libHarfBuzzSharp.so`,
`libSkiaSharp.so`, and `libSystem.IO.Ports.Native.so`. Package every publish
output except debug symbols, including `THIRD-PARTY-NOTICES.txt`,
`THIRD-PARTY-NOTICES.Angle.txt`, and the official
`THIRD-PARTY-NOTICES.Runtime.txt`. PDBs and `.dbg` files are debug artifacts.
`av_libglesv2.dll` uses the included ANGLE BSD license notice. Avalonia,
SkiaSharp, HarfBuzzSharp and the listed .NET packages declare MIT licensing;
the .NET native ports notice preserves its additional third-party notices.
`ldd` on the executable reported `libc`, `libm` and the dynamic loader.
`libSkiaSharp.so` additionally loads system `fontconfig`, `freetype`, `expat`,
`zlib`, `bzip2`, `libpng`, and Brotli libraries; `libHarfBuzzSharp.so` uses
`libpthread`, `libm`, `libc` and the loader. Linux window creation/rendering was
exercised on WSLg's X11 display with software rendering; Xvfb was not needed.
This proves the tested Linux GUI path on WSLg, not every distribution/window
manager or physical display setup.

The live app test used reference RNS revision
`a29bfb51c468945c96a1ee22cae0114e993e2391` (1.5.7) and LXMF revision
`e8ba02483217af43be284a72f1c059c730b7d047` (1.2.1), verified by
`tests/Interop/reference_environment.py`. One Python TCP server and one .NET
TCP client formed a direct single-hop topology; a Python-to-.NET incoming
message was persisted, and a .NET-to-Python outgoing message was received with
the expected content digest. Separate propagation and file-field cases are
described above. Deliberate direct-refusal/retry is covered separately;
random packet loss and arbitrary transport-hop
topologies are not covered. No Android,
macOS, audio-device, voice, telemetry or plugin behavior is claimed.

The native state store persists contacts, chat messages, display name, theme,
and the local configuration/delivery address in `state.json`; it is not wire-
or schema-compatible with upstream's SQLite database, though the bounded offline
importer reads that format. On Windows this lives in
`%APPDATA%\sideband\state.json`, separate from upstream's
`%USERPROFILE%\.config\sideband\app_storage`. Tests explicitly use isolated
data/config directories and do not connect to, reset, or migrate the user's
global RNS state. On Windows the default Reticulum path is
`%USERPROFILE%\.reticulum`, which may also be used by upstream Sideband; the
desktop app does not connect on launch or read/change that configuration until
the user requests Connect. The Connectivity screen shows the selected path
while offline, actual resolved config path and shared role after connection,
and rnstatus guidance. Its `--rnsconfig` UI/CLI option can select a config
without silently replacing or modifying the existing global configuration.

The desktop app has original C# Avalonia navigation and supported Preferences,
Connectivity, Contacts and Conversations screens. A local `ReticulumNode.Open`
integration test verifies actual standalone status and config-path display
with an isolated config; shared-client status is reported from the actual
runtime role, not guessed from installed software or a config file. The
user-facing text client supports manually entering a 32-hex LXMF address,
connecting, sending/receiving text and viewing delivery state. Incoming
file fields display metadata and can be explicitly saved; image/audio fields
still have no composition, rendering or playback implementation.
