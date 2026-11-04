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
Inbound stamp-cost advertisement and invalid-stamp rejection are independently
configurable, including supported settings/profile migration and original
Preferences controls.
Incoming transfer limits also activate on startup and explicit runtime save:
the primary default is1,000,000 bytes, or128,000,000 when explicitly disabled.
Attachment composition and local storage retain their independent safety bounds.
Periodic inbox synchronization and its retry schedule persist over restart.
Bounded file, image and audio attachments can be sent, received, persisted and explicitly saved.
Paper text creation, duplicate-aware URI import, durable offline URI retrieval
and explicit URI/QR PNG export are implemented with original desktop controls.
Original desktop controls compose/preview images and explicitly record/play audio.
Device ownership/cancellation are verified with fakes, not physical capture or acoustics.
Default-off LXST calling, trust-aware direct PTT, bounded call history and an original
desktop voice screen reuse the same Reticulum identity/router as messaging.
Manual telemetry, privacy-aware collector/report exchange, supported telemetry
migration and an original bounded raster/offline MBTiles map are implemented.
Actual primary application telemetry passes on Windows/Linux, including opted-in
Reticulum runtime collection. Other automatic sensors, plugins, mobile operation,
broad upstream configuration, and upstream database
migration of all profile semantics remain incomplete. A bounded offline importer
activates identity, supported settings, LXMF conversations, text/file/image/audio history,
delivery methods, known client-router files, synchronization state and supported telemetry from upstream profiles. Conversation trust/unread flags
are active native state, including imported values; trusted sends request LXMF
reply tickets. Foreground conversation viewing marks messages read.
The original Announce Stream retains bounded delivery/propagation history,
including unnamed sources, replacement/deletion, cached stamp costs and
available owner-cache signal observations. Explicit conversation/node selection
does not grant trust. Bounded primary SQLite announce-history migration activates
supported delivery/node metadata and historical signal observations without
inventing the absent live delivery-cost cache. Optional primary MessagePack call
history activates as visibly labelled imported assertions, without new contacts,
voice trust, authorization or device activation.
Separate primary voice-only identity objects activate bounded saved names and
explicit user trust, with durable original desktop editing/deletion. Existing
LXMF conversations retain precedence; discovery-only metadata does not become
a conversation or override voice-only authorization.
Historical outgoing paper envelopes also restore exact offline URI/QR exports,
with persisted, visible imported-envelope provenance rather than an assertion
that archived ciphertext was authenticated or matched to the displayed text.

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
| `sbapp/sideband/geo.py` | `af416ae7a1c087cc0c636e994ff58d5c6f7edf9f85379765d7fda9d130e780c7` |
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
repository; `partial` = only the explicitly named subset is implemented.
Underlying-library support does not establish application integration. Source inspection is not
runtime evidence.

| Contract | Status | Upstream contract and source |
|---|---|---|
| Explicit failed-message text retry | implemented bounded subset | Confirmed desktop and native console extension create a new ordinary message with current settings, atomically replacing only a matching failed outgoing text entry after validation. Eight-request admission, cancellation/disconnect, duplicate selection, draft protection, current-policy queue refusal/recovery, actual primary reception/return and native restart are separately tested. No old media/title/hash replay, exactly-once crash guarantee or primary Kivy invocation; see selected retry below; `SidebandClient.MessageActions.cs`, `MainWindow.Messages.cs`, `sbapp/ui/messages.py:gen_retry`. |
| App shell and interaction | partial | Original Avalonia UI provides Conversations, Contacts, Objects, Announce Stream, Connectivity, Backup & Keys, Preferences, Utilities, Guide, Voice, Telemetry, Map and Remote screens. Supported screens expose real local state and explicit errors; hardware/plugin management remain unavailable rather than simulated. Upstream layouts/assets are not copied; `src/Sideband.Desktop/MainWindow.axaml`, `src/Sideband.Desktop/MainWindow.axaml.cs`, `sbapp/main.py`, `sbapp/ui/*.py`, `sbapp/*.kv`. |
| Contacts, discovery and conversation types | partial | The app accepts an LXMF delivery hash and display name, records observed delivery announces, and displays a contact list. Persisted trust/unread flags, explicit trust/unread controls and foreground reading are implemented. Trust/unread survive import, rename, announcements and restart. A bounded persisted delivery/PN stream exposes inspect, converse, copy, node selection and deletion; receipt does not promote a discovered contact to a known conversation. Bounded primary SQLite history activates separately without contact creation. Appearance values and telemetry permissions persist; voice uses the shared client below. Complete peer settings, groups and broadcasts remain absent; `src/Sideband/SidebandClient.cs`, `src/Sideband/SidebandContact.cs`, `src/Sideband/SidebandAnnounce.cs`, `sbapp/sideband/core.py`, `sbapp/ui/announces.py`, `sbapp/ui/conversations.py`. |
| LXMF messaging and propagation UI | partial | Text send/receive, automatic/manual node selection, persisted periodic fetches, cancellation, one-shot failed-delivery propagation retry, trusted-conversation reply tickets and restart retrieval reuse `LxmRouter`. Actual primary Sideband daemon-core direct/propagated round trips pass separately from Python LXMF endpoint tests, with trust enabled and disabled in both directions. Selected saved text/title/time/state/method inspection and confirmed local entry deletion are implemented; deletion does not cancel or recall delivery. Full message presentation/settings remain missing. Sent via propagation means node storage, not recipient delivery; `src/Sideband/SidebandClient.cs`, `MainWindow.Messages.cs`, `tests/Sideband.Tests/SidebandLiveInteropTests.cs`, `sbapp/sideband/core.py:48–93,3966–4001,4811–4828,5156–5227,5634–5644`, `docs/lxmf.md`. |
| Inbound delivery stamp configuration | implemented bounded subset | Independent `lxmf_require_stamps`, `lxmf_inbound_stamp_cost` and `lxmf_ignore_invalid_stamps` activate on startup and explicit runtime save, persist/import/export atomically and have original Preferences controls. Actual primary cost8 announcements, signed stamped delivery, explicit stale-cache rejection/admission and identity-preserving restart pass Windows/Linux direct/Python-hop/.NET-hop. Strict native offline process/profile activation is separate; safety differences, timing and remaining boundaries are below; `SidebandStampConfiguration.cs`, `SidebandClient.Stamps.cs`, `SidebandStateStore.Stamps.cs`, `MainWindow.Stamps.cs`, `sbapp/sideband/core.py:lxmf_announce,update_ignore_invalid_stamps`. |
| Incoming transfer-size configuration | implemented bounded subset | Primary `lxm_limit_1mb` maps decimal1000/128000KB onto startup and mutable router admission/retrieval, atomic settings/profile persistence and original Preferences. Windows/Linux actual primary oversize refusal, larger-policy recovery both ways and native identity restart pass direct/Python-hop/.NET-hop; separate native offline settings/profile flows pass.128MB payload acceptance is not established, and existing attachment/local-storage bounds remain; `SidebandDeliveryConfiguration.cs`, `SidebandClient.Delivery.cs`, `MainWindow.Delivery.cs`, `sbapp/sideband/core.py:start_reticulum,_update_delivery_limits`. |
| Text, image/file and audio messages | implemented bounded subset | Text/files, WebP image preparation/preview, Ogg Opus and Codec2 composition/decoding, explicit 10-second recording/playback/cancellation, content-addressed persistence and explicit exports. Actual Python Sideband core sends/receives images and both audio families directly/through a Python propagation transport; actual Pillow/audioproc decode native output. Published binaries decode imported media and render images. Device tests use fakes; physical capture/acoustics remain unverified. Unknown audio is retained/exportable but playback fails explicitly; no autoplay. See media limits below; `src/Sideband/SidebandMedia*.cs`, `src/Sideband.Desktop/MainWindow.axaml.cs`, `sbapp/main.py:message_send_dispatch`, `sbapp/sideband/audioproc.py`, `sbapp/sideband/core.py:5189–5196`. |
| Paper messages and `lxm://` imports | implemented bounded subset | Connected paper text creation and local duplicate-aware import reuse LXMF; saved URI/QR display and non-overwriting exports work offline and survive restart. Bounded historical outgoing primary envelopes restore exact exports with persisted imported provenance and CLI/UI warnings. Actual primary daemon-core creation/ingestion both ways, trust on/off and process restart pass separately from historical migration/export/recipient-ingestion acceptance. Nonlocal envelopes are unauthenticated and not forwarded. Hardware camera/printer, desktop OS URI registration and other historical paper-field semantics remain unverified/missing; `SidebandClient.Paper.cs`, `SidebandPaperProfile.cs`, `MainWindow.Paper.cs`, `PaperExportCommand.cs`, `sbapp/sideband/core.py:paper_message,lxm_ingest_uri,_db_message`. |
| LXST calls and push-to-talk | implemented bounded subset | Shared LXMF/voice identity and router; explicit device selection, identity/contact dialing, answer/reject/hangup, profile/mode, microphone/speaker mute, loudspeaker and hold-to-talk controls, trusted-only authorization, trusted conversation direct-PTT permission and bounded persisted history. Voice-only names/trust import, editing and deletion are operational independently of contacts; existing LXMF conversation policy wins, but discovery-only metadata does not. Optional primary call-log migration retains labelled historical assertions separately from authenticated call events. Actual primary daemon-core Opus/Codec2 calls both origins, direct and through a .NET transport, pass on Windows/Linux; separate actual calls verify voice-only policy removal/recovery and delivery overrides. Original incoming ringtone replaces licensed assets. Physical audio, upstream GUI and hardware/keypad actions remain unverified/missing; see voice acceptance below; `SidebandVoice*.cs`, `SidebandClient.cs`, `sbapp/sideband/voice.py`, `sbapp/ui/voice.py`. |
| Telemetry, location and situational awareness | partial | All 27 sensor wire formats, exact values, bounded atomic storage and reference-executed geometry exist. Original desktop controls configure manual information/location/sensors, opted-in Reticulum runtime collection, privacy/collector/tracking policies, inspect trusted reports, explicitly delete and show reported locations on the map. Actual primary reports/collector streams, requests, restart, supported profile import, schedules and propagation recovery pass separately on Windows/Linux. Runtime collection/rendering/receive normalization is independently verified below; other automatic sensors, hardware and advanced presentation remain absent/unverified; `SidebandTelemetry*.cs`, `SidebandRuntimeTelemetry.cs`, `SidebandGeo.cs`, `sbapp/sideband/sense.py`, `sbapp/ui/telemetry.py`. |
| Raster and offline maps | implemented bounded subset | Source-executed projection/URL/MBTiles/default/provider/layer, overlay and passive discovery vectors, consent-gated raster cache, immutable imported SQLite snapshots, original pan/zoom viewport, attribution, source zoom limits and persisted view. Reported peers/interfaces/connection points, bounded selected-cache discovery advertisements, clustering, high-zoom scatter, local fixed overrides and marker inspection are integrated. Thirteen provider presets and supported offline-profile activation exist. Broader map migration/presentation remain missing; native limits/privacy differences are below; `SidebandMap*.cs`, `SituationMap.cs`, `sbapp/mapview/{source,downloader,mbtsource}.py`, `sbapp/ui/map.py`. |
| Remote commands, queries and plugins | partial | Authorized telemetry requests and built-in ping/echo/signal requests use the existing bounded client worker. Actual primary daemon cores exchange commands/replies, propagated requests and offline replies, and verify unknown-conversation admission across direct, Python-hop and .NET-hop paths. Explicit PN storage/retrieval, restart/cancellation and controlled direct-failure recovery are distinct evidence. Initially-propagated native-origin failure/recovery passes separately; enabled primary retry remains blocked by the reproduced pinned callback-lock cycle below. Original Remote controls configure shared request permissions, queue/cancel commands and preview persisted replies. Arbitrary Python command/service/telemetry plugins and custom sensors remain missing; native helpers are not a Python-plugin ABI; `SidebandClient.Remote.cs`, `SidebandRemoteCommands.cs`, `sbapp/sideband/plugins.py`, `sbapp/sideband/core.py`, `docs/example_plugins/*`. |
| Reticulum setup and interfaces | partial | The user explicitly connects through `ReticulumNode.Open` using a saved/default config or `--rnsconfig`; the Connectivity page displays actual shared-role and effective config file only after the node starts, and gives `rnstatus` guidance. Offline, it reports that ownership/health is unknown. It never rewrites the global config or checks whether an `rnsd` executable happens to be installed. Shared-instance election is inherited from DotReticulum; compatible reachable local owners may be reused, otherwise the stack may fall back to ownership. The map passively inspects the selected configuration's discovery cache without autoconnect; shared-owner blackhole policy is queried through actual management RPC. Interface editing, remote shared-owner discovery-cache access and Android interface loading remain absent; `SidebandClient.Discovery.cs`, `src/Sideband.Desktop/MainWindow.axaml`, `external/DotReticulum/src/DotReticulum.Applications/ReticulumNode.cs`, `sbapp/sideband/core.py`, `sbapp/ui/hardware.py`, `sbapp/buildozer.spec`. |
| Long-lived service and OS integration | partial | The desktop window and headless daemon own the same cancellable client. Linux published daemon/interactive processes stop on SIGTERM with closed/blocked stdin; native console quit and restart pass on both hosts. Days-long Sideband service operation remains unverified. Android foreground services, notifications, location/permission APIs, wake/multicast locks, Bluetooth/RNode discovery and background-power behavior are deferred mobile contracts; `SidebandDaemon.cs`, `sbapp/services/sidebandservice.py`, `sbapp/main.py`, `sbapp/sideband/core.py`. |
| Persistence, restart and migration | partial | Atomic native JSON/settings/history persist over restart. Offline profile import reads actual identity/config/SQLite-WAL into an absent destination; activates contacts/trust/unread/object classification/text/files/images/audio/methods, known client-router tickets/own ratchets/caches, synchronization anchors, bounded delivery/PN announce history, retained call assertions, voice-only identity/name/trust and supported telemetry/settings/permissions/appearance/cursors/map-overlay settings. Unimplemented semantics remain archived; source files never change. Other voice-object metadata, other fields and unknown persistent properties remain archive-only; interfaces, foreign ratchets and propagation-node stores are excluded. See migration limits below; `src/Sideband/SidebandProfileImporter.cs`, `SidebandRouterProfile.cs`, `SidebandPersistentProfile.cs`, `SidebandTelemetryProfile.cs`, `SidebandAnnounceProfile.cs`, `SidebandVoiceProfile.cs`, `SidebandVoicePeerProfile.cs`, `sbapp/main.py:209–273`. |
| CLI and headless operation | implemented bounded subset | The desktop/headless console has an executed inventory of all56 primary registrations, quoted arguments, longest-prefix resolution, general status, conversation/message/paper/command workflows, propagation/announce/link inspection, active native configuration and telemetry/tracking. Long network operations are owned/cancellable; Unicode pipes, malformed input, persistence and restart are tested. Runtime log-level control filters available structured host reports; log streaming is not complete capture of every stack diagnostic. Arbitrary Python evaluation/plugins fail explicitly; unsupported configuration is never activated by archiving keys. All twelve primary voice handlers are placeholders; functional native voice remains an extension. See the dedicated console section below for exact inventory, limits, evidence and core gaps; `SidebandDaemon.cs`, `SidebandConsole*.cs`, `sbapp/sideband/cli/*.py`. |
| Repository server | implemented bounded subset | Utilities explicitly starts/stops a public unauthenticated HTTPS static mirror with a package list, persistent private certificate, visible fingerprint and joined window shutdown. Default loopback/4444; reachable LAN exposure requires explicit bind and consent. No bundled upstream assets, automatic content downloads or update execution. Original bounded GET-only service, not RNS/rngit Git hosting; safety differences and acceptance are below; `SidebandRepository*.cs`, `MainWindow.Repository.cs`, `sbapp/sideband/core.py`, `sbapp/ui/layouts.py`. |
| Native packaging and upgrades | partial | Windows and Linux NativeAOT publishes succeed with trimming/linker warnings treated as errors. Package the full publish directory, including Windows `sideband.exe`, `av_libglesv2.dll`, `libHarfBuzzSharp.dll`, `libSkiaSharp.dll` and desktop/shared third-party notices; Linux `sideband`, `libHarfBuzzSharp.so`, `libSkiaSharp.so`, `libSystem.IO.Ports.Native.so` and the same notices. These include Avalonia/Skia/HarfBuzz MIT texts, ANGLE's BSD license, .NET serial runtime notices and `ThirdPartyNotices/ZXing.Net.Apache-2.0.txt`. Installer/signing, self-upgrade and other upstream package formats remain absent; `src/Sideband.Desktop/Sideband.Desktop.csproj`, `src/Sideband.Desktop/THIRD-PARTY-NOTICES*.txt`, `README.md`, `setup.py`, `build_appimage.sh`, `winbuild.bat`, `sbapp/buildozer.spec`. |

## Standalone startup announcements

The desktop/headless standalone `start_announce` setting defaults to true.
`SidebandAnnounceConfiguration` replaces the former immediate LXMF announce
with the primary's **eight-second deferred startup**. The client owns one
cancellable task per connection and joins it before router/voice/identity
disposal, including failed startup and rapid disconnect/reconnect. There is no
detached timer or unowned task retaining a disposed node. Startup errors are
reported with context; saves/announcements do not require a subscriber.

Atomic settings/profile import, canonical export and original Connectivity
controls support the flag. Disabling while the startup task is pending also
suppresses that announcement: an intentional privacy improvement over the
primary's flag captured before sleeping. Enabling after a disabled connection
applies on the next connection; it does not invent an immediate startup event.
Strict native boolean validation rejects null/numeric/string truthiness,
including partial-import rollback without replacing the old value.

This is **not silent mode**. Existing explicit delivery announcements and
solicited path responses remain available when startup is disabled. A delayed
startup announcement also reannounces an already enabled telephone as in the
reference; the independent voice initialization/discovery lifecycle is unchanged.
Android service AutoInterface/carrier/periodic auto-announcement jobs are not
implemented by this standalone setting and remain mobile-only deferred work.

`tests/Interop/GenerateSidebandStartupAnnounce.py` executes the exact
`SidebandCore.__start_jobs_deferred` AST at primary2.2.0
`d069122af14880e43607aed0b71add4c7806fbe0`,
`core.py` SHA-256
`8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25`.
Twelve rows use controlled synchronous threads/clock and fake delivery/telephone
observers: they prove selected routing/order, not actual elapsed time, Kivy UI,
periodic workers or audio devices. Independent Windows/Linux executions produce
fixture SHA-256
`e0b4fb6145e9bbba6e3350e70617f2017f739a754b802f19406642fafbcd2fd4`.
The optional profile path runs the complete actual primary constructor/start
with startup disabled and snapshots its real persistence; no algorithm
replacement or reference source edit is used.

Separately, actual primary Sideband and the native managed application pass
three direct-TCP cases on each host: enabled startup (both actual measured
delays are at least8 seconds), initially disabled startup, and pending native
disable with the primary still enabled. Disabled startup emits no unsolicited
delivery announcement during the9-second observation; subsequent explicit
announcements work. Native identity/policy restart and bounded cancellation
of the next delayed task also pass. No packet loss or fresh transport-hop
claim is inferred. Original controls separately reopen offline without devices.

Operator `artifacts\tests\sideband-startup-announce\Acceptance.ps1` runs
`dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release
--artifacts-path <host-build> --filter
"FullyQualifiedName~SidebandStartupAnnounce|FullyQualifiedName~ActualPrimaryStartupDelay"
--logger trx --results-directory <host-results>`:12 passed/0 skipped on
Windows and Linux. `Reference.ps1` records the separately generated actual
primary startup-disabled profile. The opt-in native process harness is
`tests/Pester/SidebandStartupAnnouncement.Tests.ps1`, enabled by
`DOTRETICULUM_SIDEBAND_STARTUP_ANNOUNCE=1`, the actual
`DOTRETICULUM_SIDEBAND_BINARY` and generated
`DOTRETICULUM_SIDEBAND_STARTUP_PROFILE`. Native elapsed network timing,
physical devices and other platforms require separate evidence.

Strict NativeAOT publish with `-p:ILLinkTreatWarningsAsErrors=true
-p:IlcTreatWarningsAsErrors=true -warnaserror`, `sideband --help`, and both
native offline process/profile cases pass on Windows/Linux with SDK10.0.403
and runtime/ILC10.0.14. Windows binary SHA-256:
`4dc9d3d9b5e9cdb1e3f9a3a45542dd511ac2383e885d32711d2a82ed40936e62`;
Linux:
`0c3b680520248c129acbe7a7c1b002d2f113c4d5b7e42c8a5aa2b8dfdef8b5a4`.
`Native.ps1` snapshots referenced Sideband/LXMF/LXST production files and
requires identical hashes before/after publish; source receipts, compiler
packages, runtime assets and ILC-response hashes are retained. The dirty-source
build base is `5c7b4f1e53f407a89b31656e8c1560db331982c5`, embedded
`2.2.0.103+5c7b4f1e53`, including the concurrent frozen C74 shared LXMF
implementation, not a future integration HEAD or C74 node-migration acceptance.

C76/T290–296 records14 actual passing cases per host, with frozen-source
managed12/0 and separate native2/0. Receipts:
`artifacts\tests\sideband-startup-announce\windows\c76.json` and
`/home/dennis/fleet-startup-announce-results/c76.json`.
The first integrated run exposed a remote-control UI test relying on immediate
startup discovery. It now explicitly uses the existing manual-announcement
path before commands and retains its exact five-second UI assertions.
Focused startup/remote cases pass11/0 on both hosts. That initial failed gate
is retained, not relabelled; the complete post-fix gate is recorded in
[testing](testing.md#baseline-provenance).

## Manual synchronization limits

`lxmf_sync_limit` defaults to null (unlimited); true uses `lxmf_sync_max`,
whose default is3. False/null retain the saved count without applying it.
These settings affect **manual desktop fetch only**. The pinned periodic job
calls `request_lxmf_sync()` without a limit regardless of this setting.
`SidebandSynchronizationConfiguration`, atomic settings/profile import/export
and original Connectivity controls preserve that distinction. Manual
`SynchronizeConfiguredAsync` snapshots policy under lifecycle ownership and,
once connected with a selected node, records the manual attempt's current
`lxmf.lastsync` and clears `lxmf.syncretrying` before requesting transfer.
Admission/attempt is not successful delivery. Existing explicit
`SynchronizeAsync(maxMessages: ...)` and periodic fetch semantics are unchanged.
Active transfers are not interrupted by policy changes.

Native configuration accepts null/boolean for `lxmf_sync_limit` and positive
Int32 counts, including when inactive. Zero/negative/fractional/string/oversize
counts and numeric/string boolean truthiness fail transactionally instead of
activating the primary's permissive but unsafe configuration. The actual
reference accepts zero as unlimited and passes negative/string counts through
admission; this is deliberately not copied. Invalid settings never replace the
old policy. Queued settings changes cancel on disposal; isolated observer
failures do not disable saves or service, and no observer is required.

`tests/Interop/GenerateSidebandSyncLimits.py` executes nine observations from
primary Sideband2.2.0 `d069122af14880e43607aed0b71add4c7806fbe0`.
It verifies and executes the exact `SidebandApp.lxmf_sync_request` AST with
synthetic dialog/clock/owner dependencies, then the actual `SidebandCore`
constructor and `request_lxmf_sync` using the real pinned LXMF router.
An explicitly unreachable zero-hash propagation address and public cancellation
is used between requests: these rows establish parameter routing/admission,
not transfer completion or Kivy GUI/timing. The initial no-node experiment
reproduced primary `RNS.prettyhexrep(None)` failure and remains a failed receipt,
not a passing reference run; the reference source/algorithms are unchanged.

| Source/evidence | SHA-256 |
|---|---|
| Pinned `sbapp/main.py` | `5b962d9ed2ccbc066bb19865d78c77da9ecf4008900f6f0e7e9dd1eb1c4767aa` |
| Pinned `sbapp/sideband/core.py` | `8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25` |
| Independently executed Windows/Linux fixture | `a691f2d8374c21b7e73265349bdcaff4c3746cbd055d65d2cd4d0b75327e42cf` |
| Windows NativeAOT candidate | `6bc8c4f85d5ca531d6218a2821e5d47b5a52e1f34aad870ce2354c77395b0875` |
| Linux NativeAOT candidate | `a4aa6df1d14d473bb8368f38f5df2186f96e42e269469792736f6ab184a76dc5` |

RNS1.5.7 `a29bfb51c468945c96a1ee22cae0114e993e2391` and LXMF1.2.1
`e8ba02483217af43be284a72f1c059c730b7d047` remain unchanged.
Actual primary Sideband applications and a real Python propagation/transport
node separately exchange five messages per origin on Windows/Linux: manual
two-message native retrieval, same-identity native restart, another two and
explicit unlimited remainder; primary retrieval of two followed by its
unlimited remainder. Empty-inbox completion and disabling the limit retain
service. This is the named Python propagation-transport topology, not a new
direct/.NET-hop/loss or prolonged-operation claim.

Reproduction operators are under ignored
`artifacts\tests\sideband-sync-limits`: `Reference.ps1`, `Acceptance.ps1`,
`Native.ps1` and `Record.ps1`. The selected command
`dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release
--artifacts-path <host-build> --filter
"FullyQualifiedName~SidebandSynchronization|FullyQualifiedName~ActualPrimaryLimitedRetrieval"
--logger trx --results-directory <host-results>` passes14/0 per host:
13 C75 managed cases plus the existing schedule-policy regression.
Strict `dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj -c Release
-r <win-x64|linux-x64> --artifacts-path <native-build> -o <publish>
-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
-warnaserror` and `sideband --help` pass. Native settings/process and actual
primary offline-profile cases in `tests/Pester/SidebandSynchronization.Tests.ps1`
pass2/0 per host with `DOTRETICULUM_SIDEBAND_SYNC_LIMITS=1`,
`DOTRETICULUM_SIDEBAND_BINARY` and the generated
`DOTRETICULUM_SIDEBAND_SYNC_PROFILE`. They do not prove native Python-peer
network or GUI behavior. Original managed controls are tested separately
without opening devices.

C75/T280–286 maps15 actual passing cases per host; each native candidate retains
dirty-source base `718daa84dfaac560d32c8ea6b65deabb14521514`, embedded
`2.2.0.102+718daa84df`, SDK10.0.403/runtime and ILC10.0.14, not a later
integration HEAD. Receipts are
`artifacts\tests\sideband-sync-limits\windows\c75.json` and
`/home/dennis/fleet-sync-limits-results/c75.json`.
Hardware, arbitrary impairment, full configuration/plugin migration and other
platforms remain outside this evidence.

## Incoming LXMF stamp configuration

The three primary keys have independent meanings:

| Key | Default | Active behavior |
|---|---|---|
| `lxmf_require_stamps` | false | Advertise/apply the configured inbound delivery cost when enabled. Disabling it retains the configured value but clears the effective router cost. |
| `lxmf_inbound_stamp_cost` | null | Null or an integer below1 clears the effective cost;1–254 sets it when enabled. Native accepts signed32-bit integers only. |
| `lxmf_ignore_invalid_stamps` | true | Discard messages failing a required stamp. False still advertises the cost but admits failed stamps. This is not sender authorization. |

`GenerateSidebandStampPolicy.py` executes the unchanged pinned Sideband
constructor, `update_ignore_invalid_stamps` and `lxmf_announce`, reading actual
router/provider outputs. Its13 successive observations cover defaults,
advertisement, independent rejection, disabled-cost retention/re-enable, null,
zero, negative,254 and invalid updates. Reading `_enforce_stamps` is an explicit
read-only observation, not a replacement implementation. Independent
Windows/Linux fixture SHA-256 is
`17bcaf972f53551659aca873def80c07d973b9a0e360686bc7abdf7b87ea3de6`;
pins are Sideband `d069122af14880e43607aed0b71add4c7806fbe0`,
RNS `a29bfb51c468945c96a1ee22cae0114e993e2391` and
LXMF `e8ba02483217af43be284a72f1c059c730b7d047`.
The fixture records core SHA-256
`8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25`.

Native settings/profile import, partial settings updates and export use the same
validated policy. Absent keys retain the previous native policy. Invalid flags,
non-integer/out-of-range costs and malformed saved state fail explicitly without
replacing healthy state; a healthy retry succeeds. This deliberately differs
from primary's setter silently refusing a bad cost while retaining its previous
effective cost. Even an inactive invalid value is refused. Original Preferences
save stamps separately from unsaved display/appearance edits. Active saves
serialize against connection/disposal, update the existing router and explicitly
announce; an announcement error reports that persistence/application already
succeeded. No identity replacement or background proof search is started by
saving the policy.

Windows/Linux actual-primary application acceptance verifies native generated
32-byte stamps with the primary's actual `validate_stamp(8)`, both message
origins, advertised costs, missing-stamp rejection, explicit rejection-off
admission, clearing the effective cost and service after native identity restart.
Direct TCP, a Python transport and a .NET transport are exercised separately.
Missing stamps are generated by explicitly clearing the primary outbound cost
cache through its public SDK before its unchanged `send_message`; this is
**disclosed stale-cache fault injection**, not observed natural packet loss.
Restart requires an actually fresh forwarded announcement: the driver deletes
the primary's application announce record, respects RNS whole-second emission
freshness, then waits for a new record before sending. A queued earlier announce
is not restart evidence. Internal application-worker cancellation no longer
stops the owned node before LXMF/voice link teardown; the stack is unchanged.

C70/T209–218 records21 actual passing cases per host:16 ordinary managed
reference/persistence/lifecycle/original-UI cases,3 actual-primary live cases and2
published native cases. Queued policy cancellation during disposal, failing
observers, malformed-tail rollback/retry and no audio-device activation are
explicit. Windows affected-client lifecycle regression separately passes32
cases. Native CLI tests exercise all13 reference transitions, atomic refusal,
actual primary saved-profile import, source immutability and process restart.
Native cases are offline, not published-native Python traffic or native GUI
rendering.

Reproduction operators, run from the repository, are
`artifacts\tests\sideband-runtime-telemetry\StampPolicyReference.ps1`,
`StampPolicyAcceptance.ps1`, `StampPolicyNative.ps1` and `StampPolicyRecord.ps1`.
Managed selection is `dotnet test tests\Sideband.Tests\Sideband.Tests.csproj
-c Release --filter "FullyQualifiedName~SidebandStamp|FullyQualifiedName~ActualPrimaryStamp"`
with the documented actual-primary environment. Native selection is
`tests\run-pester.ps1 -Path tests\Pester\SidebandStamps.Tests.ps1 -Tag StampPolicy`,
requiring `DOTRETICULUM_SIDEBAND_STAMPS=1`,
`DOTRETICULUM_SIDEBAND_BINARY` and the generated
`DOTRETICULUM_SIDEBAND_STAMP_PROFILE`.
Both strict `dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj
-c Release -r <rid> -p:ILLinkTreatWarningsAsErrors=true
-p:IlcTreatWarningsAsErrors=true -warnaserror` builds use
SDK10.0.403/runtime and NativeAOT compiler10.0.14; help and the two feature flows
run on the published executables. Exact binary SHA-256 values are Windows
`8d93203591ef2d9ae606de16d9063d30088a159f8a65af77b2fafa868f6c4453`
and Linux
`eb0908bf60798bddf4c6522bbaa20c66126c27f9d165a7420aa6cbe41f2f81d6`.
Build inputs are dirty source over `d59a781`, not a later integration commit.
Receipts, actual-case mappings, production input hashes and serviced compiler
response checks are `artifacts\tests\sideband-stamp-policy\windows\c70.json`
and `/home/dennis/fleet-stamp-policy-results/c70.json`.
Failed observer/operator/early restart receipts remain separate; no admission
assertions are disabled. Long-running/high-cost proof search, new propagation
topologies, physical devices, other platforms and full configuration parity are
not established by this slice.

## Incoming LXMF transfer limits

Pinned primary `core.py:181,543,719,3744–3754,4765–4770` defines
`lxm_limit_1mb`: true/default selects1000 decimal kilobytes, false selects the
primary's128000KB default. `LXMF/LXMRouter.py:delivery_resource_advertised`
accepts a Resource exactly at the byte limit and refuses larger advertisements.
`GenerateSidebandDeliveryLimits.py` executes the unchanged actual daemon core's
constructor/startup and update method, not a reconstructed policy. Eight
Windows/Linux observations produce identical fixture bytes, SHA-256
`2f5ee3ad86ab87a5dd1db3114244fb3e00321b2c06df614f98bf07702125df35`.
The Sideband revision/core digest and RNS/LXMF pins are those recorded above.
Actual primary truthiness accepts malformed null/numeric/string settings;
native activation deliberately requires a boolean and rejects the whole import.

`SidebandDeliveryConfiguration` stores the supported key in existing settings,
preserves it across partial imports and exports it canonically. Startup and
explicit lifecycle-gated saves apply the policy to the existing router, without
replacing identity/links, announcing or reconnecting. Preferences saves this
setting separately from unsaved appearance/stamp edits, and displays the decimal
limits and independent attachment/storage bounds. Missing settings, including
legacy native profiles, now receive the primary1MB default instead of the earlier
fixed8MiB value; users requiring larger transfers explicitly disable the limit.
Disabling does not enlarge the existing2MiB file/4MiB per-message storage bounds.
Already admitted Resources and an active retrieval retain their admission;
subsequent Resource advertisements/retrievals use the new limit. Router statistics
remain consistent for an already hosted propagation node.

C73/T250–258 records18 actual passing cases per host:11 ordinary
reference/persistence/lifecycle/original-UI,3 actual-primary topologies,2 exact
router/statistics cases and2 strict native offline cases. Managed primary
acceptance uses `dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release
--artifacts-path <host-specific-build> --filter
"FullyQualifiedName~SidebandDeliveryConfigurationTests|FullyQualifiedName~SidebandDeliveryPersistenceTests|FullyQualifiedName~SidebandDeliveryUiTests|FullyQualifiedName~ActualPrimaryDeliveryLimits|FullyQualifiedName~FilesExchangeWithActualPythonAndRemainExportableAfterRestart"`
with the documented actual-primary opt-ins and paths. The selected15 cases also
reaccept the pre-existing2MiB file case with explicit larger-policy configuration;
that case retains its separate C31 mapping. Router selection
`FullyQualifiedName~MutableDeliveryLimit` covers the inclusive exact byte boundary,
one-byte excess refusal, retained-link recovery, malformed/disposed updates and
existing propagation statistics.

Actual primary applications and native managed clients refuse1,050,000-byte
random file attachments in both directions under the default, then exchange
checksum-verified files after both limits are raised. Native identity/policy
restart and subsequent file/text delivery pass direct, through a Python transport
and through a .NET transport. The primary's
`lxmf_try_propagation_on_fail=False` is explicit peer configuration to isolate size
refusal from its documented callback-lock retry defect; no source patch or
resource-admission replacement is used. This is not natural packet loss, a128MB
payload, full dependency/configuration parity or prolonged operation.

Strict NativeAOT publish uses SDK10.0.403/runtime and compiler10.0.14,
`-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror`.
`tests\run-pester.ps1 -Path tests\Pester\SidebandDeliveryLimits.Tests.ps1
-Tag DeliveryLimits` requires `DOTRETICULUM_SIDEBAND_DELIVERY_LIMITS=1`,
the selected `DOTRETICULUM_SIDEBAND_BINARY` and an actual primary saved profile at
`DOTRETICULUM_SIDEBAND_DELIVERY_PROFILE`. Both cases pass on Windows/Linux,
including malformed atomic rejection, all reference boolean transitions,
actual-primary offline profile activation, immutable source bytes and native
process restarts. These are not published-native network or GUI cases.
Windows binary SHA-256 is
`3f79122987de72dce6f0946f6c57416b10e80510bb88a1addeea05668768774a`;
Linux is
`fe1814750c2f6f9cdedc14bc58528b6a3cb32b6139525d3e840aab48212f9e3d`.
Both retain actual dirty-source build base `ed8f5de`, not integration HEAD.
Receipts are `artifacts\tests\sideband-delivery-limits\windows\c73.json` and
`/home/dennis/fleet-delivery-limits-results/c73.json`.

## Announce stream

The original desktop stream retains the newest **48 unique sources** across
delivery and propagation announcements, ordered by receipt and persisted
atomically. A fresh observation replaces the source and moves it in the stream;
unnamed/legacy delivery announcements remain visible. Delivery names and current
cached stamp costs reuse LXMF; propagation metadata uses its validated seven-item
format and metadata keys name1, implementation254 and version0. Connected
refresh consults the current local LXMF cost cache, not a network request;
offline history shows the saved observation.

Inspect shows source, type, time, cost, optional node version/active flag and
available RSSI/SNR/quality. Measurements come from the selected Reticulum
owner's public packet-hash management cache, including authenticated shared-owner
RPC; missing measurements remain unavailable, not invented. Lookup failure is
reported separately without dropping the announcement. TCP acceptance does not
prove physical RF measurement support. Announce records are discovery metadata,
not authenticated conversation messages or evidence of current reachability.
Receipt does not grant trust, unread state, request permission or known-conversation
status. Unsupported contact names remain in history rather than disabling core
processing; control characters are flattened only for original UI display.

Explicit actions converse, copy the address, select a propagation node or delete
only that announcement. A local delivery announcement can be sent while connected.
Node selection does not enable propagation-by-default, and deleting history does
not remove conversations, permissions or the selected node. The existing bounded
128-item client worker owns ingress; observer exceptions are isolated and reported,
and signal lookups use run cancellation during joined disconnect. Native JSON
supports existing profiles without an announce collection. Primary SQLite import
has separate bounded migration evidence below; it is not live-network evidence.

`GenerateSidebandAnnounceVectors.py` executes unchanged primary SQLite/log/list/
delete methods and actual pinned LXMF providers. Exact primary core SHA-256 is
`8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25`;
LXMF router SHA-256 is
`56b6a8e6b9f79256c4ae297340791eebcc1b6cda3cec04ab2dbe2f1efb53caa9`.
Controlled clock/router-cost observers and an offline SQLite connection are
explicit seams, not live application/GUI evidence. Windows/Linux independently
generate identical canonical LF fixture SHA-256
`da57b40e6939abbe90f2a5761255e1153e6fc994f5288c4c2810bdcdc5e9ea57`.
The observed primary prunes before insertion and reaches49; native deliberately
enforces48 after every insertion. Four-KiB app-data/one-KiB text limits, finite
measurements and quality0–100 are native safety bounds, not invented wire fields.

On each host `dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release
-warnaserror --filter "FullyQualifiedName~SidebandAnnounce|FullyQualifiedName~ActualPrimaryAnnouncementStreams"`
passes **14 cases with no failures/skips**, with the documented live/reference
environment and isolated build/result directories. Three actual primary daemon-core
rows exercise delivery both ways, unnamed replacement, explicit actual-router PN
metadata, delete/reannounce, no initial observer, failing later observers and native
identity/history restart: direct, one Python transport and one .NET transport.
The primary router PN role and explicit empty-data RNS emission are disclosed
public API seams, not claims that Sideband's GUI hosts a PN. Successive source
emissions are separated by1.1seconds because RNS path freshness uses whole-second
emission timestamps; same-second replacement dispatch is not asserted. No packet
loss, propagation synchronization or prolonged-service claim comes from these rows.

Strict NativeAOT publishes use SDK10.0.403/compiler10.0.14,
`-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror`.
The selected Windows binary is
`artifacts\builds\fleet-announces-native-win\publish\Sideband.Desktop\release_win-x64\sideband.exe`,
SHA-256 `d0d060f7530ad8c1ba40700b93f3b289320a1b31d88ad0680700dd2006ccefe5`.
The Linux binary is
`/home/dennis/fleet-announces-native-linux/publish/Sideband.Desktop/release_linux-x64/sideband`,
SHA-256 `feee5eeb2ad92d1e504058bbe1cdf956e5952d7fbae33349327d80aaba3a4495`.
On both hosts `tests\run-pester.ps1 -Path tests\Pester\Sideband.Tests.ps1
-Tag @('Native','Announces')` passes six cases with one unrelated
missing-map-profile skip. The new GUI case itself passes, rendering four
actual-reference synthetic records across two real native process starts, checking
unchanged state bytes and rejecting empty history with exit2. This is separate
from managed live-network acceptance and physical/hardware evidence.

## Announce profile migration

The absent-target offline importer activates the newest48 unique supported
delivery/propagation sources from the retained SQLite snapshot. Both the primary
five-column schema and its actual six-column `extra` upgrade are accepted.
At most65,536 input rows are scanned; SQLite cell/step/cancellation limits remain
in force. Selected source/data/extras are bounded before managed allocation,
and bounded MessagePack decoding validates historical signal observations.
Malformed selected rows/schema, oversized input and invalid/nonfinite metadata
reject the staged import atomically, leaving the source and destination unchanged.
Older/duplicate/other destination types and unrecognised extras remain archived
with explicit notices. Import creates no contacts and grants no trust.

The primary receiver normalises modern delivery data before persistence, and
its list method reads delivery costs from the current LXMF router, not SQLite.
Imported offline delivery costs therefore remain unknown; a connected refresh
uses the native live cache. PN name/version/cost/active metadata and optional
historical RSSI/SNR/quality activate, not physical measurements of the new host.
Strict48 retention and all-or-nothing malformed-input handling are deliberate
native safety differences from primary's prune-before-insert49/logged row skipping.

`GenerateSidebandAnnounceProfileVectors.py` executes unchanged primary receiver,
log/list/database-upgrade methods in the upstream-created schema, with disclosed
synthetic clock/cost/PHY observers. The core revision/digest is the provenance
pin above. Windows/Linux produce identical canonical fixture bytes, SHA-256
`8ef0e3f8f343ea73dbf0dc7d307c6163bd082f2f71876b3a4616be004266c302`.
Separately, `tests\Interop\GenerateSidebandProfile.py --sideband <primary>
--rns <pinned-rns> --output <new-private-directory> --announce-profile` executes
the actual imported core's configuration, identity, conversation, message,
announcement and SQLite-WAL persistence with inert unused import stubs.
It retains generated private material only in ignored local profiles.

With `DOTRETICULUM_SIDEBAND_ANNOUNCE_PROFILE` selecting that actual profile,
`dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release -warnaserror
--filter FullyQualifiedName~AnnouncementProfile` passes **15/15 with no skips**
on each host. Cases cover actual WAL, historical schema, reference metadata,
bounded deduplication, unknown types, malformed/oversized/nonfinite input,
atomic rollback, no contact/trust promotion and immutable source files.
Strict warning-as-error NativeAOT publication and
`Invoke-Pester -Path tests\Pester\Sideband.Tests.ps1
-Tag @('Native','Announces','AnnounceImport')` pass **seven cases** with one
unrelated missing-map-profile skip on each host. The migration case itself
imports the actual private profile, rejects an existing target, renders its four
records in two real native process starts, and preserves all imported state and
source bytes. Announcement smoke starts directly on its intended page: briefly
displaying the default conversation page legitimately marked imported unread
messages read on Windows and was corrected rather than weakening the assertion.
The exact accepted Windows binary is
`artifacts\builds\fleet-announce-import-native-win\publish\Sideband.Desktop\release_win-x64\sideband.exe`,
SHA-256 `bae536aeeb59880fda9c19641ede65f45d6fcac1b8483b46e1459463ab9f0cc0`;
Linux is
`/home/dennis/fleet-announce-import-native-linux/publish/Sideband.Desktop/release_linux-x64/sideband`,
SHA-256 `7b304444491459047d0e641f08048f90cb95a23a8fa8334ee4e52261eb021a7c`.
Both select SDK10.0.403/compiler10.0.14. Guarded operator/receipts are in
`artifacts\tests\sideband-announce-import`; selected native and managed results
remain separate from the shared-worktree default gate.
No new network topology, upstream GUI, physical-signal or complete migration
claim follows from this offline acceptance.

## Reported overlays, objects and local fixed locations

The original Map screen projects the newest admitted report per source inside
the configured map window, default **604800 seconds (seven days)**. Up to 256
markers are retained, with omitted/invalid overlays reported explicitly. Peer
markers precede extras; connection keys are namespaced by reporting peer, so two
reporters can describe the same point independently. These are reported points,
not inferred edges, authenticated positions or automatically discovered interfaces.
Source-trust display policy still applies. Map-visible refresh runs every 60
seconds and stops on navigation/window close. Clustering uses the observed
100-pixel radius/512 extent through zoom18; zoom19 scatter separates overlaps.
Cluster activation zooms; marker activation inspects the reporting peer.

Cluster/interface/connection/hidden-blocked switches and whole-second history
1–604800 persist; only-blocked is session-local. Saving is visibly pending until
the existing bounded viewport save completes. `map_cluster`, `map_interfaces`,
`map_connection_maps`, `map_hide_blocked` and `map_history_limit` activate during
atomic offline profile import. No online consent or external path is imported.
Zero coordinates remain valid; disabled/stale overlays are removed rather than
retained by primary display defects.

Contacts explicitly classifies an existing peer as an object; Objects inspects
its visible telemetry or restores an ordinary conversation. Objects are excluded
from the ordinary conversation selector without deleting history, trust,
unread state or permissions. Legacy contacts default to ordinary; actual primary
boolean `is_object` activates during import. Malformed classification rolls back
the entire staged profile. Primary's separate map-object toolbar placeholder is
not invented as a new network protocol.

Map explicitly replaces/clears local `fixed_locations` overrides. The input is
strict UTF-8, at most256KiB/256 coordinates, using `LXMF hash,latitude,longitude`.
Overrides persist atomically and never replace transmitted/stored telemetry,
create conversations, read a plugin directory automatically or override own
reported location. Unlike primary's logged per-line skipping and NaN acceptance,
native malformed/nonfinite input rejects the whole replacement and preserves the
last good snapshot. An override needs an otherwise visible recent source report.

`GenerateSidebandMapOverlayVectors.py` executes 102 observations from actual
primary map methods; `GenerateSidebandObjectVectors.py` executes five SQLite
classification/list observations and eleven fixed-file loader/getter cases.
Both use revision `d069122af14880e43607aed0b71add4c7806fbe0`; exact core/main
digests are in the provenance table, and map SHA-256 is
`cfd65489d5f94ddac3990ebd50c6b346e3cd6d0e0c3bfc1e30880ca983e5bf11`.
Windows/Linux independent output is byte-identical:
overlay fixture `347c42b956ff5894d7b3c2667419b18a1879ed9263fe0a6ef1b5c449dd95008b`,
object/fixed fixture `7e110d6bade6c7bb887a546de236e39e19723ef7d5bbfea0c1205973629bfda5`.
These execute actual selected methods with disclosed synthetic owners/SQLite/files;
they are neither upstream GUI nor live-network/device evidence.

Parent Windows/Linux exact-source focused acceptance selects
`SidebandObject|SidebandFixedLocation|ActualPrimaryObjectDataActivatesAndPreservesTelemetryPermissions|MalformedObjectClassificationRollsBackTheWholeProfile|SidebandMap|SidebandTelemetryUiTests`
and passes130 cases per host. It includes actual pointer cluster/scatter
activation, isolated failing observers, shared-point namespacing, blocked filters,
zero-coordinate overrides, malformed preservation, restart and timer lifecycle.
The sensor-shape integration rebases only the historical timestamp natively;
it is not a freshly generated primary telemetry report.
`DOTRETICULUM_SIDEBAND_MAP_OBJECT_PROFILE` optionally exports an absent synthetic
acceptance directory from the tested UI flow. Native Pester consumes a private
copy, renders one persisted object across two processes, then seven markers in
one cluster over owned offline tiles, without Reticulum/audio/online requests.
This complements managed workflow tests, not native input automation or new
application-network interoperability.
`sideband --smoke-ui --smoke-objects --data-dir <synthetic-profile>` selects
object rendering explicitly; ordinary/media smoke behavior is unchanged.
Conflicting modes reject before state creation, and an empty object profile
reports its error and exits2 rather than claiming a rendered object.

Final strict publishes use
`dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj -c Release -r <rid>
--artifacts-path <host-output> -p:ILLinkTreatWarningsAsErrors=true
-p:IlcTreatWarningsAsErrors=true -v quiet`; both pass with zero warnings/errors.
`tests\run-pester.ps1 -Path tests\Pester\Sideband.Tests.ps1 -Tag Native,GUI`
with the explicit exported synthetic profile passes **11 cases/two explicit
unrelated profile skips on each host**. Linux invokes the script through
PowerShell `-Command`, preserving the tag array rather than passing one
`Native,GUI` string to `-File`. The latter selects zero cases and fails.
Eleven additional focused profile-import cases per host verify every supported
overlay option and whole-profile rollback for malformed values.

| Accepted overlay/object executable | SHA-256 |
|---|---|
| `artifacts\builds\fleet-map-objects-native-win-final2\publish\Sideband.Desktop\release_win-x64\sideband.exe` | `1050b61f9ee3fb519588f50e2764d6a61ae1f21f89b0fad66de183379047575d` |
| `/home/dennis/fleet-map-objects-native-linux-final2/publish/Sideband.Desktop/release_linux-x64/sideband` | `f92dc36b977803ef3d0f61e50ee6848b22fde00ca410149a3d020600708c3ae9` |

An initial history test exposed stale successful map status while saving was
still pending; explicit pending status now precedes the asynchronous save.
The first native overlay recipe selected zoom0 where the synthetic MBTiles
contains no tile; it failed rather than claiming rendering. Zoom1 exercises its
real tiles with the same exact marker/cluster assertions. A mistaken Linux
invocation targeted an older CI checkout and selected zero new tests; that
publish/run is discarded, not evidence. Accepted Linux commands explicitly name
the current mounted project and native-Linux output directories.

## Paper application and desktop acceptance

`CreatePaperMessageAsync` requires an explicitly connected client and a resolvable
recipient. It uses the current authorized telemetry/appearance fields and the
existing LXMF encryption/signing/ratchet path; it does not transmit paper data.
Empty text rejects; whitespace is preserved, as the actual primary does.
Primary trusted paper sets `IncludeTicket` but bypasses outbound generation, so
no ticket field is invented. Native history explicitly says `Paper`, not
`Delivered`; primary stores its prepack state 0/method 5. Inbound paper uses the
ordinary persisted receive worker and upstream propagated method.

Eight combined Create/Ingest operations may be active/waiting; the ninth rejects
immediately. Pending work links runtime cancellation before the lifecycle gate;
disconnect/disposal releases all slots. Existing duplicate suppression operates
without subscribers and survives failing observers and restart. Nonlocal
classification checks only the bounded destination envelope, not authenticity,
and never forwards it. Local acceptance does not promise that cancellation
retracts an already queued delivery.

Conversations creates **text-only** paper explicitly, displays its saved URI and
original shared-LXMF QR preview, and exports either to the explicit path without
overwriting. Pending attachment/recording input is rejected rather than silently
lost. Utilities accepts an explicitly pasted URI and displays local/duplicate/
nonlocal/rejected outcomes; no clipboard/camera access occurs automatically.
Selection changes and window close release QR bitmaps. Native extensions:

```powershell
sideband --export-paper <message-uuid> --paper-output <new-file> --data-dir <existing-profile>
sideband --export-paper <message-uuid> --paper-output <new-png> --paper-qr --data-dir <existing-profile>
sideband --smoke-ui --smoke-paper --data-dir <existing-profile>
```

Export commands are offline and reject conflicting modes before profile/RNS
access. URI output has no BOM/newline. QR defaults/bounds and the transitive
Apache-2.0 dependency notice are documented in [LXMF](lxmf.md); retain that notice
in full published packages.

Actual primary revision `d069122af14880e43607aed0b71add4c7806fbe0`,
core SHA-256 `8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25`,
is unchanged. `GenerateSidebandPaperVectors.py` executes actual methods with
disclosed field/trust/persistence seams; vectors alone are not live evidence.
Separate unmodified primary daemon cores and native applications exchange paper
both ways with trust enabled/disabled, real Python/native restart, exact saved
URI and persistent duplicate suppression. The thin adapter explicitly calls
real `LXMRouter.exit_handler` before process exit to flush upstream persistence.
Direct TCP establishes identities/ratchets; paper data itself travels offline.
No paper-specific multi-hop/loss or physical scanning claim is made.

Parent independent Windows command
`dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release
--artifacts-path artifacts\builds\fleet-paper-accept-win
--filter FullyQualifiedName~SidebandPaper` passes all eight cases with live
reference flags. Linux uses native output `/home/dennis/fleet-paper-tests-linux`;
the combined paper/headless filter passes 17 cases/one explicit audio-device
opt-in skip. Separate shared QR/name filters pass 19 cases per host.
The paper adapter is copied into test output so independent native-filesystem
artifact paths do not depend on locating the source checkout above `bin`.

Strict Windows/Linux NativeAOT publishes use
`-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true`.
`tests\run-pester.ps1 -Path tests\Pester\SidebandPaper.Tests.ps1 -Tag Native,GUI`
passes four cases per host with the published binary and isolated
`DOTRETICULUM_QR_PYTHON` selected. It executes real URI/PNG exports across
processes, independent Pillow/zxing-cpp 2.3.0 decoding, overwrite/argument
rejection and an actual Avalonia paper-render flow. Its seeded synthetic
reference history is not evidence the running client authored that history.
This is separate from actual daemon-core interoperability and from physical
microphones, cameras, printers, upstream GUI or complete paper-profile import.

The selected final strict-publish directories are
`artifacts\builds\fleet-paper-native-v4-win\publish\Sideband.Desktop\release_win-x64`
and `/home/dennis/fleet-paper-native-v4-linux/publish/Sideband.Desktop/release_linux-x64`.
Executable SHA-256s are
`3fcffbc28f8ea84f80835a7375f047ffd393065b4e64faccff8b718c86bc2490`
and `270a4be323ba55054e479f5931f92982cd9085e838f4620d70cffb9bccd9d1c8`.
The shared Apache notice is
`cfae7bd1153b81c2fe81517e5122a0a67e9c2478c16cd7b189793a6072fcf6d4`.
The final Linux three-suite Native/GUI run passes 17/two explicit skips; the
Windows selected native catalog also passes with installed codecs and the
independent decoder selected. The actual QR drawing call is observed after
scrolling its preview into view. Contact-only trust/read changes preserve the
unchanged selected message/preview; a genuine message selection change clears it.
These frozen executables precede the guide-only wording clarification that
distinguishes URI import from QR export; the final managed paper cases also pass.

## Primary historical paper-envelope migration

The offline importer activates an outgoing primary `lxm` Paper record's exact
MessagePack pair `[signed_plaintext_wire, encrypted_paper_envelope]`. It verifies
the ordinary row/hash/source/destination/signature rules, strict pair consumption,
binary types and nonempty wire, LXMF envelope bounds and matching destination.
Malformed pairs, extra/trailing values, missing/oversized envelopes and different
destinations reject the entire staged import; a healthy retry succeeds without
changing source files. Incoming Paper envelopes remain archive-only while their
plaintext history is retained; ordinary primary paper reception uses Propagated
history, as the existing application tests observe.

Opaque ciphertext cannot be authenticated or bound to the separate stored
plaintext without the recipient's key. Restoration explicitly does **not** make
that assertion: `ImportedPaperEnvelope` persists on outgoing messages, and the
original Show/Export controls and CLI display a warning. A test deliberately
alters ciphertext while retaining signed plaintext to enforce that boundary.
Imported envelopes grant no trust or delivery confirmation. The shared
`LxMessage.PaperUriFromBytes` formatter avoids a second Sideband URI codec.

`GenerateSidebandProfile.py --paper-profile` executes actual pinned
`LXMessage.pack/as_uri`, `SidebandCore.lxm_ingest/_db_message` and recipient
decryption. Its synthetic public fixture
`tests\Sideband.Tests\Fixtures\sideband-paper-profile.json` has SHA-256
`f34851b414c865da62d98908da97adbaac474f5e267bab2a973706ed32456ef5`.
It records the primary/core/LXMF revisions above and contains no private
identities. The generator deliberately finalises the URI before storing state5;
this is not relabelled as the normal primary `paper_message` prepack-state0
observation. Both Windows and Linux independently execute persistence/readback;
Linux also verifies the Windows fixture using unchanged LXMF APIs. Unused
subsystem import stubs apply to generation only, not the recipient daemon core.

Current scoped commands on each host are:

```powershell
dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --filter FullyQualifiedName~Paper
dotnet test tests\LXMF.Tests\LXMF.Tests.csproj -c Release --filter FullyQualifiedName~LxmfContractTests
dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj -c Release -r <rid> -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
tests\run-pester.ps1 -Path tests\Pester\SidebandPaper.Tests.ps1 -Tag PaperProfileImport
```

The managed selections pass28/2 explicit disabled-live skips and16/0 each;
the published selection passes2/0 on Windows/Linux. With an actual generated
`DOTRETICULUM_SIDEBAND_PAPER_PROFILE`, complete `DOTRETICULUM_SIDEBAND_BINARY`,
pinned Python/RNS/Sideband/LXST paths and `DOTRETICULUM_QR_PYTHON`, it imports the
actual pair, exports URI and PNG across native process lifetimes, independently
decodes PNGs with Pillow/zxing-cpp2.3.0, and opens the URI in an unchanged primary
recipient daemon core. Its synthetic private recipient identity stays in ignored
artifacts; its public source key is explicitly preloaded through actual
`RNS.Identity.remember`, not a network announce. The recipient validates the
signature/content and preserves duplicate suppression through process restart.
Separate native Avalonia lifetimes render the imported URI/QR and preserve all
message records and the identity; ordinary foreground unread changes are allowed.
This is **offline application handoff**, not new live Reticulum/topology,
physical camera/printer or upstream-GUI evidence.

The complete current paper `-Tag Native,GUI` regression passes6/0 on each host,
including the original export/overwrite/argument and synthetic-history rendering
cases. Current strict-published executables:

| Host | Publish directory | Executable SHA-256 |
|---|---|---|
| Windows x64 | `artifacts\builds\fleet-paper-profile-win\publish` | `73f89be8a9ec9acd294800faa74c51139ead3a442f535b6dc3052eee02043e38` |
| Linux x64 | `/home/dennis/fleet-paper-profile-build/publish` | `9b19438b21ad2e2efff5dd98de251fd2d22798199e3db6af70fcd7c41b88555b` |

C69/T200–207 records28 actual passing cases per host. Local receipts are
`artifacts\tests\sideband-paper-profile\windows\c69.json` and
`/home/dennis/fleet-paper-profile-results/c69.json`; native build inputs are dirty
source over `ec06f2d`, not a claim that the subsequent documentation commit
produced the binaries. Exact executable hashes are recorded in those receipts.
Full profile semantics and hardware/platform boundaries remain incomplete.

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
  GUI flows or confirm platform packaging. The separate daemon-core application
  cases below execute selected messaging contracts.

## DotReticulum.Ecosystem integration boundary

`DotReticulum`, `LXMF` and `LXST` are reusable protocol-layer candidates, not a
drop-in Sideband application. The selected initial scope is Windows/Linux
desktop with native C# and Avalonia 12.1.4; the app references `LXMF` and
`DotReticulum.Applications`, and now references LXST for audio-message codecs/devices. RNS is
pinned to 1.5.7, matching Sideband 2.2.0's declared minimum; LXMF 1.2.1 exceeds
its declared minimum. LXST targets the required 0.5.4 and supplies the application's
call/device APIs. Voice remains default-off until explicitly enabled.

The app project is `src/Sideband.Desktop/Sideband.Desktop.csproj` (assembly and
executable name `sideband`); its protocol/application library is `src/Sideband`
(assembly `Sideband.Core`, namespace `Sideband`). The library uses source-
generated JSON rather than reflection serialization. Native plugins, upstream
full SQLite migration, mobile services and platform-specific sensors are not
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
Last-attempt/retry state persists in native JSON and activates from upstream's
SQLite `persistent` table during offline migration. Disposal cancels and awaits the owned periodic task.

Windows validation uses `--artifacts-path artifacts\builds\fleet-sideband`:
`dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release` with
`--filter "Category!=Live"` passed the settings/UI/lifecycle cases.
Strict `dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj -c Release
-r win-x64 --artifacts-path artifacts\builds\fleet-sideband-native
-p:ILLinkTreatWarningsAsErrors=true
-p:IlcTreatWarningsAsErrors=true` passed. Published Native and GUI Pester gates
passed 4/4 on Windows and WSL/Linux: identity restart, settings exchange/error
transactionality and actual rendered validation/image flows. The analogous Linux
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
`--artifacts-path /home/dennis/fleet-sideband-media-tests` and the isolated Linux
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

Conversation trust defaults to false. Trusted sends request reply tickets through
the existing LXMF router; untrusted sends do not. This does not bypass signing,
stamp enforcement or peer policy. `GenerateConversationPolicyVectors.py` executes
the actual `is_trusted` and `send_message` methods with injected database,
destination, message-factory and router APIs at the recorded revision/digest.
Five cases establish that only the database trust value `1` enables tickets.
The actual application cases independently verify signed ticket fields and
usable reply-ticket stores in both directions, for direct and propagated sends
with trust enabled/disabled. Native conversation flags survive restart; renames
and incoming announcements never grant or revoke trust. The original desktop
shows unread/trusted labels, saves explicit trust, marks the active foreground
conversation read and offers an explicit mark-unread action. These controls do
do not themselves grant voice authorization, telemetry permissions or Python-plugin access.

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
Other voice-object metadata, unsupported message fields and unknown persistent
properties are not activated. Bounded voice-only names/trust and optional
labelled call assertions activate separately as described below.
Supported telemetry settings, conversation permissions/appearance,
up to 4,096 report rows and 4,000 known cursor rows activate through the bounded
telemetry importer. Invalid/future or mismatched report/cursor timestamps reject
the staged import; this is not silent data repair. Known client-router files and synchronization state are activated
as described below; other app-storage files, propagation-node state and interfaces
are not migrated. Upstream queues are memory-only, not omitted persisted files.
Remote historical signatures cannot be verified
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
It uses Python `delivery_limit=8192` KB, explicitly above its default; the
reaccepted native2MiB file case explicitly disables `lxm_limit_1mb`.
Native incoming Resources otherwise default to1,000,000 bytes, matching primary
Sideband, rather than the earlier fixed8MiB value. Native safety ceilings are 2 MiB per file,
4 MiB/eight files per received message and 128 MiB/4,096 content-addressed files
on disk. These are explicit bounded native policies, not upstream limits.
Hash verification precedes export; existing export destinations are never
overwritten (including JSON settings exports). Malformed/unsafe names, corruption, quota exhaustion and
persistence failures are reported. Orphaned content after a failed history
write is bounded but is not automatically garbage-collected. There is no automatic
opening, physical recording evidence or published-native network file-flow claim.

### Images and audio messages

Images use field `0x06` as `[format, binary_data]`; audio uses `0x07` as
`[mode, binary_data]`. Both reuse the existing signed LXMF field serializer.
Native image input/preview accepts PNG, JPEG and WebP, with at most 2 MiB encoded,
8,192 pixels per dimension and 16 Mi pixels total. Composition produces WebP
without upscaling: low/medium/high policies use 320/640/1,280-pixel bounds and
qualities 22/66/75. Skia and Pillow encoding/resampling are not byte-identical.
Other Pillow formats and general animation are not supported by this bounded
native policy. Incoming invalid images report a contextual error without losing
the message's valid text. Content-addressed files are verified again before
preview/export; stale asynchronous previews cannot replace a newer selection.

Audio mode `0x10` is Ogg Opus; Codec2 modes `3` through `9` carry concatenated
codec frames, **not** LXST's leading codec-mode byte. Native composition,
decoding and explicit recording/playback are limited to ten seconds and 2 MiB.
This is a native resource/safety limit, not a reference duration limit.
Unknown byte-valued modes remain stored/exportable and fail playback explicitly.
No receipt, selection or construction opens a microphone/speaker or autoplays.
The desktop offers explicit image/audio-file composition, a ten-second recording,
play/stop/discard and save operations; close cancels and joins owned work.
Device leases, stalled-provider deadlines, cancellation, failure recovery and
no-device paths use fake backends, not physical capture or acoustic evidence.
The desktop selects the existing Windows waveform or Linux PulseAudio provider;
neither has physical-audio acceptance in this application.

`tests/Sideband.Tests/GenerateMediaVectors.py` executes unchanged image branches
extracted from actual `main.py` and actual `audioproc` functions with synthetic
PNG/PCM. The fixture records the exact revisions above, Pillow version, native
library observations and source hashes; `audioproc.py` SHA-256 is
`5e2570ece2a08f3f4a09e19e8203b553f5ef13c49995275862f61b2fb9d2ebce`.
Codec2 encoded frames match all seven supported reference modes; decoder
excitation is nondeterministic, so PCM byte identity is not asserted. Eleven
mono 12 kHz/60 ms Opus packets match independently executed reference output.
The original native Ogg writer uses standard 48 kHz granules/preskip; the
reference helper writes unscaled 12 kHz counters and quarters decoded duration.
Native output deliberately fixes that container-timing defect while preserving
the codec contract. Reference Ogg input still decodes with its observed duration.

Two actual Sideband daemon-core cases per host exchange images and Opus/Codec2
audio in both directions, directly and through a Python transport/propagation
node. Actual Pillow and Sideband `audioproc` decode native output; received
combined media survives export/restart and malformed-media recovery. These use
synthetic, small media, not maximum-sized media or physical devices. Published
binaries separately import/decode media and render images; they do not establish
published-native network media-transfer acceptance.

Images use the packaged Skia native dependency. Audio uses LXST's original
adapters to independently installed Opus, Opusfile and Codec2 libraries; no
reference source, sounds or codec binaries are bundled. Windows acceptance used
MSYS2 Opus 1.6.1, Opusfile 0.12 and Codec2 1.2.0 plus their runtime dependencies;
Linux used its separately installed libraries. Missing libraries fail with
operation context. Refer to [lxst.md](lxst.md) for native-library licenses and
platform boundaries; neither trimming/AOT success nor fake devices establish
macOS or physical audio support.

`sideband --check-media --data-dir DIRECTORY` is an offline native extension.
It validates stored payloads and decodes each supported image/audio item, reports
counts, honors cancellation and fails on corruption/unsupported modes/missing
codecs. It never opens Reticulum or audio devices. The accepted imported fixture
reports one image and two audio items.

### Activated router and synchronization migration

The importer activates available `lxmf/available_tickets`,
`outbound_stamp_costs`, `local_deliveries`, `locally_processed` and only the
imported identity's signed `ratchets/<delivery-hash>.ratchets`.
Actual public LXMF stores validate ownership, signatures, schemas and normal
expiry before installing these files inside the private staging directory.
Secret copies are zeroed. Bounds are 4 MiB tickets/ratchets, 8 MiB per cache,
4,096 ticket destinations per map, 16 inbound tickets per destination,
65,536 duplicate/cache entries and 512 own ratchet keys. Foreign ratchets,
RNS caches/interfaces, propagation-node peers/message stores and `node_stats`
are excluded; client queues are memory-only upstream.
`GenerateSidebandRouterProfile.py` executes actual pinned LXMF/RNS file writers
and reverse-loads the resulting native files through the actual reference APIs.
Seventeen tests cover migration and failures, including the generated profile.
Published acceptance independently reverse-loads native tickets/ratchets/caches
on both Windows and Linux; it does not prove a live post-import ratchet exchange.

Only the actual binary SQLite `persistent` keys `lxmf.lastsync` and
`lxmf.syncretrying` activate the native schedule. Other properties stay archived
without reading their private values. Finite numeric/bool/nil validation and
schema/row/scalar/file bounds reject malformed input transactionally.
`GenerateSidebandPersistentProfile.py` executes actual database and periodic-job
methods, with disclosed controlled dependencies for policy observations.
Forty-four tests cover actual persistence, strict boundaries, WAL snapshots,
restart and failures. The full import fixture stores its schedule only in
committed WAL; published import activates the exact anchor/retry state alongside
history, media and known router state. This is offline file/policy evidence,
not an upstream GUI or all-properties migration claim.

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

## Voice and headless workflows

The original Voice page enables/disables the telephone, enumerates/selects devices,
dials an identity hash or a selected LXMF contact, answers/rejects/hangs up,
changes profiles/modes and controls microphone, playback, loudspeaker and PTT.
Contact dialing resolves the actual announced identity, not a second application
identity. A single `SidebandClient` owns both protocols over the same node;
disconnect cancels lookups/calls before releasing the router or identity.
Windows voice selects WASAPI, Linux voice selects PulseAudio. The separate
audio-message recorder still uses the Windows waveform provider.

Outgoing identity/contact dialing admits one pending operation, including path
lookup, before acquiring the lifecycle semaphore. Concurrent attempts fail
immediately instead of queueing behind the lookup. The accepted operation owns
its caller/run cancellation from admission, including disposal before setup
starts. Hangup cancels and joins that same caller-visible task even when no
telephone link exists yet. Deterministic regressions exercise twelve consecutive
hangups and four iterations each of caller cancellation, disconnect and disposal,
then verify fresh admission, no history and no device acquisition. Cancellation
and hangup deadlines remain two seconds; the original desktop assertion is not
relaxed.

The combined client-call, original voice UI, map and profile corpus passes
70 cases/one generated-profile skip on Windows and Linux. Separate
`SidebandClientCallInteropTests` passes all four actual-primary cases per host:
both call origins, direct and through a .NET transport, Opus/Codec2/PTT,
trust/history and identity restart. Windows uses the disclosed synthetic device
factories; Linux uses an explicitly isolated virtual PulseAudio server, not
physical audio. The first Linux invocation omitted the required
`DOTRETICULUM_SIDEBAND_VOICE_PULSE` and failed all four startups; supplying a
responsive owned virtual server restored the unchanged assertions. Retained
results are `fleet-pending-dial-owned-final`, `fleet-pending-dial-live-win-final`
and Linux `/home/dennis/fleet-pending-dial-live-pulse-results`. This fixes
reproduced pending-dial admission/disposal defects; it does not prove the cause
of the separately recorded intermittent full-suite desktop timeout.

Reference-executed policy preserves LXMF-conversation trust precedence, including
an explicit untrusted conversation overriding voice-only trust. Trusted-only
authorization defaults false; enabling voice alone does not restrict callers to
trusted contacts. Direct PTT requires global opt-in, a trusted LXMF conversation,
its separate per-contact permission and a half-duplex call. Only this explicitly
enabled policy auto-answers; otherwise the user must answer. Capture opens only
after acceptance. Enumeration never captures; incoming ringing may open the
selected **output** after 500 ms. Hold-to-talk releases on pointer/key release,
lost capture, deactivation, navigation and close. A call excludes concurrent
audio-message device ownership.

The notification is an original low-gain 660 Hz pulse, not Sideband's licensed
`soft1.opus` or a bundled asset. Answer, rejection, teardown, configuration and
disposal cancel/join it; output failures are reported without disabling later
calls. Caller failures and observer exceptions likewise do not disable service.
Ringing, waiting and path deadlines are 30, 60 and 10 seconds. Native call history
retains at most 1,000 events for 14 days and supports explicit durable clearing;
clear runs in the same bounded persistence worker so accepted earlier events
cannot reappear afterward. Optional migration of the primary's separate
MessagePack file is described below. Device identifiers, history and
policy survive restart. `voice_output` and `voice_low_latency` remain preserved
unsupported settings, not silently active controls. The inspected primary's
low-latency activation is Android-only; its desktop UI disables that control.
Removing the native jitter buffer would not reproduce that hardware API.

### Primary voice-only identity migration and controls

Primary `core.py:_db_create_voice_object` (`3125–3144`) persists type4
conversation rows keyed by a16-byte **identity hash**, not an LXMF destination.
`GenerateSidebandProfile.py --voice-peers-profile
--public-voice-peers-fixture OUTPUT` executes that unchanged method, actual
name/trust setters, SQLite readback, `voice_is_trusted` and `voice_display_name`.
Independent Windows/Linux observations match fixture SHA-256
`eb6255902597efe60374b3df28f9415ea834e41a0cef3d276054e9ece9a0ca9f`.
The fixture records primary revision
`d069122af14880e43607aed0b71add4c7806fbe0` and `core.py` SHA-256
`8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25`.
Existing inert unused-subsystem import stubs do not replace these persistence
or policy methods; this generation opens no audio devices and is not live
calling or upstream-GUI acceptance.

The existing atomic offline importer activates these objects as separate native
voice peers. Explicit saved trust is user configuration, unlike untrusted
call-history assertions; importing a call log alone still grants no trust.
Existing LXMF conversations, including an explicit false trust value, take
precedence over voice-only policy and names. Mere discovery does not count as
an existing conversation. Saved trusted names render alone; untrusted names
include their hash, unnamed objects show a hash and missing objects show
`Anonymous Peer <hash>`. This slice does not add dynamic RNS app-data naming.
Voice-only trust does not grant conversation direct-PTT permission.

The original Voice page lists saved peers and supports bounded name/trust edits
and explicit deletion offline or while connected. Policy refreshes on these
mutations, unrelated state updates preserve drafts and selection, and updates
survive restart without opening devices or creating LXMF contacts. Effective
names also appear in call status/history; they are labels, not authenticated
identity assertions. Native names allow at most80 UTF-16 characters without
control characters, and imported binary names at most320 bytes with strict
UTF-8 decoding. Invalid identity/type/trust/name values reject the whole
import; malformed-profile rollback followed by healthy retry is exercised.
The shared1,000-row conversation limit includes voice objects. Other voice
metadata, including activity/unread/extras, remains archived. Group/broadcast
types2/3 are still archive-only and do not become voice peers or contacts.

The focused command is:

```powershell
dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release -warnaserror --filter "FullyQualifiedName~SidebandVoicePeer|FullyQualifiedName~SidebandVoicePolicyTests|FullyQualifiedName~SidebandProfileImportTests|FullyQualifiedName~ImportedVoiceHistory|FullyQualifiedName~VoiceOnlyNames|FullyQualifiedName~SidebandVoiceProfileTests"
```

Windows passes107/3 explicit platform skips; Linux108/2. With the pinned
reference paths and `DOTRETICULUM_SIDEBAND_VOICE_LIVE=1`,
`--filter "FullyQualifiedName~SidebandClientCallInteropTests|FullyQualifiedName~SidebandVoiceLiveTests"`
passes12 actual-primary cases per host. The two new cases each establish
three incoming Opus calls and reject two: saved voice-only trust, deletion,
restored trust, explicit untrusted delivery override and trusted delivery
recovery. They verify discovery-only state first, policy refresh without
observers, codec traffic, teardown and identity/history restart, directly and
through one .NET transport. The other ten cases retain both call origins,
Opus/Codec2 and PTT. Linux uses owned virtual PulseAudio with the unchanged
reference audio backend; Windows uses the disclosed synthetic device factory
shim. Neither establishes physical audio, Python transport, impairment or soak.

Serviced10.0.403 SDK/10.0.14 compiler strict NativeAOT publication passes with
both trim/AOT warning-as-error properties. Exact accepted binary SHA-256 values
are Windows
`c59f1439fa7435046f04febd0297668be2509f6faeda25ba9b42e47d8b3bc3f9`
and Linux
`d6fb655c3f0f784207c798ba9fbf736a2673a67c4fe52c96848e274afdf7c09e`.
These candidates were built from dirty source over `aab67c0`, not the later
integration commit. Published `VoicePeerImport` passes once per host without
skips, including actual primary import, invalid-name rollback/retry, unchanged
source/state, non-overwriting conflict, two fresh offline renders, empty-list
failure and invalid-mode rejection. `--smoke-ui --smoke-voice-peers --data-dir
PROFILE` is offline and mutually exclusive with other modes; acceptance
scrolls the actual peer list into the viewport. The strengthened history smoke
does the same. Separate published GUI regression passes9/4 explicit
missing-profile skips per host.

C66/T183–189 records22 actual passing cases per host:10 Unit,9 ordinary
Integration,2 live and1 published-native case. Exact native/live receipts,
source hashes and mappings are
`artifacts\tests\sideband-voice-peers\windows\c66.json` and
`/home/dennis/fleet-voice-peer-results/c66.json`. Initial rejected fixture/test
diagnostic assumptions and their failure artifacts remain recorded privately;
the accepted native diagnostic stays privacy-safe rather than leaking imported
names. Native offline GUI/profile acceptance is separate from managed
actual-primary network acceptance and does not establish full migration.

### Primary voice-history migration

The optional `app_storage\lxst_call_log` belongs to Sideband's
`ReticulumTelephone`, not LXST Telephony. The unchanged primary
`voice.py:170–219` saves a MessagePack array of maps containing `time`, `event`
and a **16-byte identity hash**, not an LXMF destination. Its retention rule is
strictly `now - time < 14 days`; ordering and duplicate events survive a fresh
instance reading the file. The native importer preserves those semantics and
archives the original bytes privately within the existing atomic, absent-target
profile transaction. Missing files remain optional; malformed present files
fail the entire transaction and leave a healthy retry possible.

Imported events persist `Imported=true` and display `[IMPORTED ASSERTION]` in
the original Voice screen across restart. They do not authenticate the recorded
identity or create contacts, voice peers, trust, unread state, call authorization
or audio sessions. Ordinary native call events default to non-imported. Explicit
durable history clearing remains available offline.

Native safety differences are explicit: at most256KiB,1,000 entries,64-byte
scalar values,8,001 total values and depth4; excessive inputs fail rather than
silently truncate. Non-finite/invalid times and times more than240 seconds ahead
fail, although the primary retains arbitrary future timestamps. Primary file
errors can become empty history; native errors remain explicit and transactional.
The exact native future cutoff uses a fixed-clock test:240 seconds is accepted
and the next representable timestamp is rejected. The atomic-import malformed
fixture is one day ahead, rather than a floored clock plus241 seconds, so
crossing a second boundary during copying cannot make the fixture valid.
Windows/Linux require regular files without linked/reparse ancestors; Linux
`statx` additionally rejects FIFO/socket/device types before opening. This
validates a **stopped snapshot**, not an adversarial TOCTOU-proof file jail.
An existing voice log on another OS fails explicitly; no macOS claim is inferred.

`GenerateSidebandVoiceProfileVectors.py` executes actual clear/log/load methods
with owned files, fresh `__new__` instances, a fixed clock and a hash-only
synthetic identity. Seven observations cover empty/all five events, expiry,
exact/inside retention boundaries, future time and duplicates. Independently
executed Windows/Linux fixture bytes match SHA-256
`f21b46d53bbba36d8059b735a9650798eba5f3e5deb7e749f1d10969e3adfbb8`;
the exact pinned `voice.py` SHA-256 is
`51e61510ea5eee832e7b9ad452459998c76561d970e0e773a253fe17cc7f2ab8`.
Windows imports report unavailable native filters and the primary's Python
fallback; log methods execute neither DSP nor device construction. These
observations are persistence vectors, not codec/audio/network acceptance.

`tests\Interop\GenerateSidebandProfile.py --sideband SOURCE --rns RNS
--output NEW_PRIVATE_PROFILE --voice-profile` separately executes actual primary
core persistence and call-log methods with current time and a generated peer
identity. Existing disclosed unused core import stubs remain; actual LXST
imports/pins are verified and no voice persistence method is replaced.
`DOTRETICULUM_SIDEBAND_VOICE_PROFILE` selects that profile for the published
`VoiceImport` Pester case. It compares all six events including a duplicate,
source and state hashes, non-overwriting conflict rejection, two fresh native
Voice renders, empty-history failure and invalid-mode rejection.
`--smoke-ui --smoke-voice-history --data-dir PROFILE` is offline and rejects
Reticulum/other-mode combinations. It selects Voice before loading contacts,
so inspection does not accidentally mark an imported conversation read.

The focused command is:

```powershell
dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release -warnaserror --filter "FullyQualifiedName~SidebandVoiceProfileTests|FullyQualifiedName~SidebandProfileImportTests|FullyQualifiedName~ImportedVoiceHistory"
```

Windows passes83 with3 explicit platform skips; Linux passes84 with2.
This includes wider existing profile regressions; C64/T176–180 separately
identifies the call-history cases. Native publication uses the serviced
10.0.403 SDK/10.0.14 compiler with both trim/AOT warning-as-error properties.
The published `VoiceImport` case passes once per host with no skips. Binary
SHA-256 values are Windows
`635a198e148a6b43dedae201824dbb7d6c41378b5ae0c3bf9449c17f1529891a`
and Linux
`919e8e377a9ab82efbf3ba0a6dd30828a41afaf56def1692f6ea4a4c97ec01c7`.
They were built from dirty source over `aa2e3ed`, not their later integration
commit. Actual C64 receipts are
`artifacts\tests\sideband-voice-profile\windows\c64.json` and
`/home/dennis/fleet-voice-profile-results/c64.json`: Windows19 passes/1 explicit
Linux-only FIFO/device skip, Linux20 passes/no skips. Windows T177 is correctly
partial, not relabelled as complete metadata-platform acceptance.
Private profiles and retained initial failures stay in ignored acceptance
artifacts, never committed keys or captured private traffic.

`SidebandVoiceGenerateVectors.py` executes actual primary policy/callback/log
methods and the unchanged `voice_is_trusted` method with disclosed fake
owners/telephone/device objects; its fixture records source hashes. These are
not live calls. `sideband_voice_node.py` separately starts the actual
`SidebandCore`/`ReticulumTelephone`. Six coordinator cases exercise both origins,
full/half duplex, direct PTT, Opus-to-Codec2 progress, teardown and a fresh call;
four shared-application cases exercise both origins/direct/.NET transport,
contact-derived trust/PTT, actual codec traffic, history and identity restart.
Both peers must report established before PTT is pressed.

Windows replaces only the reference device factories with explicit
synthetic/discard backends; signalling, codecs, policy and persistence remain
actual primary code. Linux uses the unmodified reference audio backend against
an explicitly isolated Pulse null sink/source. Neither is physical audio,
acoustic quality or upstream-GUI acceptance. These application-specific cases
do not establish a Python transport hop, arbitrary loss or prolonged call soak;
the separate LXST cases have their own topology/soak boundaries.

Daemon use:

```text
sideband --daemon --config PROFILE --rnsconfig RNS_DIRECTORY
sideband --daemon --interactive --verbose --synthetic-audio --config TEST_PROFILE --rnsconfig TEST_RNS
```

Noninteractive mode ignores closed stdin and serves until cancellation.
The process isolates one borrowed console read because `Console.In` can block
synchronously before returning an async result; cancellation releases the
client without waiting for another keystroke. Process exit ends any blocked
borrowed stdin read; it is not an owned application/device worker.
Interactive EOF/`quit` shuts down. Its voice extension supports `voice enable`,
`voice announce`, `voice dial IDENTITY`, `voice answer`, `voice reject`,
`voice hangup`, `voice status`, `voice profile VALUE`, `voice mode VALUE`,
mute/unmute mic/spk, PTT on/off, `voice log` and `voice log clear`.
Profiles/modes accept the native enum names or defined numeric values.
Pending dialing remains cancellable by hangup, disable or shutdown, and later
dials use fresh cancellation ownership. Commands are bounded at 8,192 characters;
oversize lines are drained/reported, never truncated and executed.
Maintenance and daemon modes cannot be combined. Native interactive, verbose
and synthetic switches require daemon mode; this stricter option boundary is
an intentional difference from primary GUI option handling.

`GenerateSidebandConsoleVectors.py` actually imports/registers/validates/executes
all twelve primary voice commands: every handler returns `Not yet implemented`.
Functional native commands are explicitly native extensions, not copied Python
console behavior or evidence that these upstream placeholders work.

The broader desktop/headless command inventory, active configuration subset,
strict argument parsing and independent console acceptance are documented in
[Desktop/headless interactive console](#desktopheadless-interactive-console).

Focused voice acceptance uses the pinned Python/RNS/LXST paths plus
`DOTRETICULUM_SIDEBAND_SOURCE` and `DOTRETICULUM_SIDEBAND_VOICE_LIVE=1`.
Windows additionally sets `DOTRETICULUM_SIDEBAND_VOICE_SYNTHETIC=1`;
Linux instead sets `DOTRETICULUM_SIDEBAND_VOICE_PULSE` to an isolated server,
never a default physical source. With separate artifacts directories:

```powershell
dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --filter "FullyQualifiedName~SidebandVoice|FullyQualifiedName~SidebandClientCall|FullyQualifiedName~SidebandDaemonTests|FullyQualifiedName~SidebandConsoleReferenceTests|FullyQualifiedName~VoiceSettingsDevicesTrust"
```

Result: **47/47 on Windows and Linux, no skips**. Linux acceptance explicitly
probes the owned null sink/source before tests and joins only its owned Pulse
PID afterward. Separately, strict `dotnet publish` with
`ILLinkTreatWarningsAsErrors=true` and `IlcTreatWarningsAsErrors=true` succeeds
for `win-x64` and `linux-x64`. With each published binary selected:

```powershell
& .\tests\run-pester.ps1 -Path tests\Pester\SidebandVoice.Tests.ps1 -Tag Native
```

Result: **3 passed, 2 explicit POSIX skips on Windows; 5/5 on Linux**.
Two real native processes exercise both call
origins, Opus traffic, additional frame progress after Codec2 switching, mute,
graceful exit and persisted history/identity restart. These use explicit native
synthetic/discard audio, not physical devices or a Python peer. A separate case
rejects conflicting options before state creation. The two Linux signal cases
stop an interactive process with blocked stdin and a daemon with closed stdin
within three seconds; both now pass against the published binary.

### One-hour native call endurance

The explicit `VoiceSoak` Pester case requires
`DOTRETICULUM_SIDEBAND_SOAK=1`, a selected frozen executable, its expected
`DOTRETICULUM_SIDEBAND_SOAK_SHA256` and a retained
`DOTRETICULUM_SIDEBAND_SOAK_EVIDENCE` JSON path. It cannot silently shorten
the 3,600-second in-call hold. Both peers must remain in-call, with increasing
RX/TX at every 30-second sample and no observed teardown; the case switches from
Opus to Codec2 halfway, kills one owned peer, establishes a reverse-origin
recovery call, exits gracefully and reloads both identities/history.
The sampled bounds are 512 MiB working set and 128 MiB growth after warmup,
not proof that no transient allocation spike occurs.

Frozen voice executables in the table below passed concurrent one-hour runs:

| Host | Actual hold | Samples | Final server RX/TX; client RX/TX | Maximum sampled working set |
|---|---:|---:|---|---:|
| Windows | 3,614.396 seconds | 121 | 37,603/37,559; 37,559/37,604 | 48.738 MiB |
| Linux/WSL | 3,609.872 seconds | 121 | 38,782/38,783; 38,783/38,782 | 69.402 MiB |

Each result is 1 passed, zero skips. Raw samples, monotonic progress, UTC versus
stopwatch durations, XML results and unchanged binary hashes were independently
checked after execution. Local operator recipes are
`pwsh -NoProfile -File artifacts\tests\sideband-voice-soak\Run-Windows.ps1`
and `Run-Linux.ps1`; retained JSON/XML/logs are in that ignored directory.
The reproducible test is committed in `tests\Pester\SidebandVoice.Tests.ps1`:
select `-Tag VoiceSoak` with the explicit variables and native codec libraries.
Private temporary profiles were removed. This is direct-loopback native-pair
synthetic capture/discard output, not prolonged Python-peer, transport-hop,
WAN, physical microphone/speaker or days-long service evidence.

## Telemetry foundations

These platform-independent building blocks now support the separate original
desktop controls below. Their vector/storage acceptance alone does **not** prove
live application telemetry and is unrelated to the .NET-only `telemetry` executable.
`SidebandTelemetryCodec` represents all 27 primary sensor IDs, including
self-describing transport/propagation/interface data. Wire bytes remain
authoritative: unknown IDs survive, malformed known readings are explicitly
classified, and exact integer timestamps are retained separately from convenient
double projections. Manual/synthetic construction opens no devices or services.
Encoding/decoding is bounded to 256 KiB, 16 levels and 16,384 values.

`SidebandTelemetryStore` is an original atomic store, not primary SQLite:
one exclusive writer, default 4,096 records/16 MiB, globally newest-first queries
limited to 256 records, explicit prune/clear and cancellation/disposal ownership.
Duplicate identity is source plus exact numeric source timestamp; the first
payload wins. An outer stream timestamp never substitutes for the sensor time.
Invalid/missing or more-than-240-seconds-future timebases are explicitly rejected.
Reported source hashes alone are not authenticated origin/provenance.

Actual primary SQLite observations expose two defects: an unfiltered query
ignores order/limit, and cleanup retains a maximum of **now minus 240 seconds**,
deleting valid recent reports. Native queries enforce their global bound;
`FromPrimary` records the cleanup defect explicitly. The application does not
invoke that defective policy: while connected and enabled, hourly cleanup retains
the primary seven-day window with a corrected **now plus 240 seconds** upper bound.
`SidebandGeo` covers source-observed distances, azimuth/elevation and radio
horizon; a nonconvergent primary inverse calculation returns no result instead
of an invented fallback. No geoid/maps/assets or location collection are bundled.

Independent acceptance:

```powershell
dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --filter "FullyQualifiedName~SidebandTelemetryTests|FullyQualifiedName~SidebandTelemetryReferenceTests|FullyQualifiedName~SidebandTelemetryStore|FullyQualifiedName~SidebandGeoTests"
# Set SIDEBAND_TELEMETRY_VECTOR_OUTPUT before the tests, then use the pinned venv:
python tests\Sideband.Tests\GenerateTelemetryVectors.py --source <primary-checkout> --verify-csharp <exported-json>
```

Result: **184/184 passed, no skips**; actual pinned `Telemeter` independently
decoded **63 C#-encoded reports** and matched the reference readings.
`GenerateTelemetryVectors.py` and `SidebandGeoGenerateVectors.py` execute the
exact primary sensor/geo methods and extracted SQLite methods with disclosed
synthetic clocks/dependencies. This is vector/storage evidence, not live
application telemetry, maps or physical sensor acceptance.

## Telemetry application exchange, privacy and migration

The client uses existing LXMF fields and delivery machinery for ordinary-message
telemetry/appearance, empty-content report messages, collector streams and
authorized telemetry commands. Its bounded 64-item worker owns cancellation and
deduplicates in-flight work per peer/type. Queued means admission, not delivery;
propagation-node storage does not advance recipient-delivery success cursors.
One failed peer or observer does not disable subsequent service. Telemetry-only
messages do not become empty chat-history entries.

Collection supports manual information, fixed location and bounded supplied
wire readings, plus separately opted-in connected Reticulum runtime statistics.
No telemetry option opens GPS, microphone, physical sensors or external
location services. Ordinary sends respect per-conversation permission/trust;
requests have separate authorization. Collector forwarding excludes the recipient
and configured source exclusions. The signed LXMF outer sender is authenticated;
an inner collector source is only that sender's assertion. Received/forwarded
provenance and available packet statistics are enriched separately, preserving
reported original receiver/distance where required. TCP synthetic locations are
not physical location or RF evidence.

Native safety differences are deliberate: unverified sensitive metadata is ignored
even where primary ordinary ingestion accepts it; future/mismatched stream and
request cursors cannot poison persisted progress; safe unknown readings survive;
and retention corrects the primary cleanup defect above. Display-trust filtering
precedes the requested result limit, so newer untrusted reports cannot hide older
trusted reports. Application queries return at most 256 records from the bounded
4,096-record/16-MiB store. Explicit local refresh updates the timebase rather than
the primary's change-only cached report behavior.

Collector schedules preserve the primary strict boundaries, two send attempts
and blocked slot; native attempt/block/tracking progress also survives restart
where primary counters are RAM-only. A one-second worker, rather than primary's
60-second polling, supports explicit positive whole-second intervals. Unknown
hardware/plugin settings remain reported/preserved, never activated by import.
Supported JSON settings and actual primary binary/SQLite profile migration
activate supported manual/runtime collection/privacy settings, contact flags/appearance, reports
and the four named telemetry cursor suffixes. Broader sensor/provider, MQTT,
Python-plugin and complete upstream presentation/configuration parity is absent.

`GenerateSidebandTelemetryPolicyVectors.py`,
`GenerateSidebandTelemetryObservationsVectors.py` and
`GenerateSidebandTelemetryScheduleVectors.py` execute the actual pinned core,
sensor/geo and persistence methods with disclosed synthetic clocks/dependencies.
Their fixtures record the exact revision and source SHA-256 values. The thin
application adapter executes the real daemon core; collector retrieval explicitly
passes primary's `is_collector_request=True`, because configuring a collector
alone does not change that method's default. No production transport workaround
was needed for the earlier restart failure.

Independent parent acceptance ran:

```powershell
& tests\Interop\Invoke-SidebandTelemetryAcceptance.ps1 -Python "$PWD\artifacts\builds\lxst-win-reference\Scripts\python.exe" -ReticulumSource 'C:\Users\dennis\.copilot\session-state\acfd3d43-fbd3-41f2-b4d5-f670c527895d\files\reference\rns' -SidebandSource "$PWD\artifacts\builds\fleet-sideband-reference" -LxstSource "$PWD\artifacts\builds\lxst-primary-win-054" -ArtifactsDirectory artifacts\builds\sbp
# Linux pwsh, from /home/dennis/dotreticulum-ecosystem-acfd3d43 with DOTNET_ROOT/PATH configured:
& '/mnt/c/Git/dennispayne/DotReticulum.Ecosystem/tests/Interop/Invoke-SidebandTelemetryAcceptance.ps1' -Python '/home/dennis/dotreticulum-ecosystem-acfd3d43/reference-venv/bin/python' -ReticulumSource '/home/dennis/dotreticulum-ecosystem-acfd3d43/rns' -SidebandSource '/mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/builds/fleet-sideband-reference' -LxstSource '/mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/builds/lxst-primary-054' -ArtifactsDirectory artifacts/builds/sblp -NativeRuntimeDirectory artifacts/tests/sbp
```

Result: **1,175 passed, zero skipped per host**; Windows 3m12s and Linux 3m18s.
Four actual-primary cases independently exercise reports/requests/collector streams
both ways, direct and through a Python transport, application restart, exclusions,
privacy/signature/cursor rejection, schedules/tracking, actual primary profile
import, propagation fallback enabled/disabled, propagation-only requests and
direct-service recovery. Failure injection explicitly disables the reference
public destination's packet callback/proofs and refuses delivery links; primary
source remains unmodified. It is not arbitrary packet-loss impairment.
Reports, profiles and locations are synthetic. Neither this run nor the offline
preview establishes published-native network telemetry, physical sensors,
upstream GUI, .NET-hop telemetry or prolonged telemetry-service endurance.
Linux builds are copied unchanged to a native Linux filesystem to enforce Unix
private-file permissions; mounted Windows filesystem checks are not substituted.
The frozen pre-appearance-correction manifest is retained under
`artifacts\builds\sideband-telemetry-contracts\frozen-production-source.json`.

`GenerateSidebandTelemetryAppearanceVectors.py` separately executes actual
`get_message_fields` with validated synthetic RGBA values. Native settings import
now uses the observed truncating division by `1.0/255`, not nearest rounding or
an assumed equivalent multiplication. All 261 reference cases pass independently
on Windows/Linux; this corrects the earlier `0.1` import discrepancy. The change
is confined to `SidebandTelemetrySettings.cs`; other frozen production hashes
remain unchanged. A focused rerun combines those cases with both actual-primary
direct/hop profile-import cases using the operator's
`-Filter 'FullyQualifiedName~SidebandTelemetryAppearanceTests|FullyQualifiedName~ActualPrimaryTelemetryRequestsCollectorProvenancePrivacyAndRestart'`.
Independent parent reruns passed **263 cases, zero skips on each host** after the
correction (Windows 1m04s, Linux 1m03s), including both actual-primary migration
cases, not only the arithmetic vectors. Results are retained under
`artifacts\builds\sba\results` and native Linux
`/home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/tests/sba/results`.

## Automatic Reticulum runtime telemetry

`SidebandRuntimeTelemetry` implements the factual `RNSTransport` contract from
primary `sense.py:2494–2716` and `Sensor:189–305` at the revision/digest above.
`telemetry_s_rns_transport` is imported/exported and remains off by default.
Connected collection queries the selected node's actual public management API,
including authenticated shared-owner statistics, paths through127 hops and link
count. Transport identity is the **local runtime's** identity, not the owner's
reported identity. When transport is disabled the stack may generate a fresh
runtime identity; the shared-owner acceptance explicitly selects
`static_transport_identity=yes` to compare independently with the persisted key.

The60-second sensor cache preserves ordinary-read `>60` versus explicit-update
`>=60` behavior, first instantaneous speeds, subsequent counter deltas, path
ordering, transport-enabled detection and extraction of `rss` into memory.
Integral counters are subtracted before floating conversion, preserving a
one-byte delta above2^63 and the primary's negative rate after counter reset.
Unknown counters remain nil, not invented zeros. Collection is serialized,
cancellable and disposed with the connection; failed queries explicitly fail
without committing a partial sample. Unlike primary's attempt timestamp, the
native cache advances only after a successful complete sample.

Sparse output is the primary default: `ifstats` and `path_table` are **nil**, not
omitted. A separate native privacy opt-in includes dense tables, which may
disclose interface names and routes. Its native JSON setting is
`rnsTransportIncludeTables`; upstream-shaped settings exchange preserves it as
the explicitly native extension `dotreticulum_runtime_telemetry_tables`, not
an invented upstream key. Simultaneous manual/automatic sensor25 configuration
is rejected before mutation. The existing connected scheduler updates opted-in
reports; disconnected preview explicitly reports unavailable and never opens a
node. Imported hardware/plugin keys do not activate other collectors.

Primary receiving behavior has a distinct limit: `core.py:_db_save_telemetry`
reconstructs the sensor with default `sparse=True` and repacks it while adding
received metadata, dropping received dense tables from its saved report.
Native storage preserves original dense bytes. Live acceptance separately
asserts raw callback bytes, actual primary rendering and exact primary
repacking, rather than mislabelling sparse persistence as lost wire data.
The adapter's opt-in public callback observer retains only the latest bounded
report for one selected source and **always invokes unchanged primary delivery
processing**; no primary source, scheduling or persistence is patched.

`GenerateSidebandRuntimeTelemetryVectors.py` executes actual constructors,
start/getter/update/pack/stop and receiver reconstruction with disclosed
synthetic clock/runtime observations. Nine collection observations plus the
receiver observation independently regenerate byte-identically on Windows/Linux:
SHA-256 `a887155929bb62e64063b8f569c844c13de28d6b60e2529d65314c1002f5a0bc`.
Focused acceptance passes26/26, no skips per host:

```powershell
dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release -warnaserror --filter "FullyQualifiedName~SidebandRuntimeTelemetry|FullyQualifiedName~ActualPrimaryRuntimeTelemetry|FullyQualifiedName~SidebandTelemetryApplicationTests|FullyQualifiedName~SidebandTelemetryUiTests"
```

Set the normal live/Python/RNS/Sideband reference environment first.
The four full-primary application cases cover sparse/dense native output,
both directions, direct TCP/Python transport, actual rendering, normalization,
settings activation and native history restart. Standalone/shared-owner tests,
cache/concurrency, malformed input, cancellation/disposal, conflict refusal and
offline UI/persistence are separate evidence. No .NET-hop runtime-telemetry,
impairment, endurance, physical sensor or primary GUI claim is made.

Strict NativeAOT publishing with SDK10.0.403/compiler10.0.14 and
`ILLinkTreatWarningsAsErrors=true`, `IlcTreatWarningsAsErrors=true` passes on both
hosts. The distinct `RuntimeTelemetry` Pester selection runs published `--help`,
settings import/export, unavailable offline rendering without autoconnect,
explicit-config actual runtime collection and two connected native process
starts with stable delivery identity/settings and saved reports. This is native
collection/UI evidence, **not published-native Python network exchange**.
The separate `GUI` regression passes7 cases with4 explicit missing-profile
skips per host, including the unchanged offline manual preview.

Accepted published SHA-256 values are Windows
`375b054e2dbc36f17675f637455736b030b1242066785bf6a8648aaba62592b9`
and Linux
`294929c28a0d025e840d8001e77f0fe99d87c95da7fc9af8781d95afa23c4ba0`;
these dirty-source milestone binaries embed parent `1adf98c`, not the later
integration commit. Actual receipts/source hashes remain under
`artifacts\tests\sideband-runtime-telemetry\native-windows\c62.json`
and `/home/dennis/fleet-runtime-native-results/c62.json`. C62/T168–172 maps
10 unit,3 application,4 actual-primary,1 managed UI and1 native process case,
all passing per host. Reproduce the distinct published selection with:

```powershell
dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
# Select linux-x64 on Linux; set DOTRETICULUM_SIDEBAND_BINARY to that publish:
pwsh -NoProfile -File tests\run-pester.ps1 -Path tests\Pester\Sideband.Tests.ps1 -Tag RuntimeTelemetry
```

Other automatic sensors, LXMF-propagation collection and complete sensor/plugin
configuration remain required gaps; Processor's primary automatic collector is
itself a placeholder, not a license to invent CPU collection.

## Raster maps and manual telemetry desktop

The original Map screen supports drag panning, discrete zoom, explicit coordinate
entry, a configurable HTTPS raster provider, attribution, session-only online
consent, cache removal and offline raster MBTiles import. Launching the application
or viewing stored telemetry never authorizes online tile downloads. The primary
default is online-enabled; native default-off consent is an intentional privacy
difference. No map tiles, fonts, icons or geographic datasets are bundled.
The checked-in four-tile MBTiles fixture is original synthetic colored raster data.

`GenerateSidebandMapVectors.py` executes AST-selected methods from the pinned
primary, including real SQLite tile lookup and original core defaults. Its
105 projection, ten URL, four metadata and five tile observations record exact
source SHA-256 values in `Fixtures/sideband-maps.json`. A recorded raw-image seam
observes the primary SQL lookup without pretending to execute Kivy rendering;
HTTP uses a controlled response, not a public provider. Reference geometry uses
bottom-origin world Y and TMS rows; XYZ/quadkey requests invert the row.
The primary north-pole projection fails and its south pole is out of world;
the original viewport explicitly clips to Mercator's display limit instead.

The owned `maps` directory holds a private exclusive lease, atomic `view.json`,
downloaded content-addressed tiles and an imported `offline.mbtiles` snapshot.
Defaults are 1,024 downloaded tiles/64 MiB, 2 MiB per encoded tile and 1 GiB per
MBTiles source. Raster decoding permits PNG/JPEG/WebP up to 2,048 pixels per side.
Admission is fixed at 64 operations; disposal cancels and drains accepted work.
Online requests use an original User-Agent, a ten-second deadline and no redirects.
Custom `{s}` templates deterministically select the first configured subdomain,
rather than the primary's randomized server selection; no automatic server
failover or provider load-balancing claim is made.
Cache/source errors are visible; missing offline tiles are legitimate empty areas.
Import rejects a live nonempty WAL, checks copied/source hashes and database
integrity, and preserves the last good snapshot on failure. Imported files are
never edited. Dataset attribution is retained rather than the primary's generic
label. The viewport draws at most 64 tiles and 256 validated markers.

The Telemetry screen offers explicit manual preview, persisted privacy/collector
policies, per-conversation permission controls, bounded tracking schedules and
send/request admission results. Admission is not delivery confirmation. Viewing
reports starts neither collection nor Reticulum; no hardware is opened.
Details label the authenticated LXMF outer sender separately from a collector's
asserted inner source. Trust filtering applies before display; explicit deletion
requires confirmation. The map shows the latest viewable location per source
within seven days from the newest-256 report query after source-trust filtering, using validated contact/own
colors and original labels, not licensed symbols. No reported location is proof
of an authenticated physical position.

Clustering and reported interface/connection overlays are implemented in the
separate acceptance slice above. Cached discovery overlays are described below; broader map-setting migration and
full primary map presentation remain required software work. Explicit report
recentring exists; persistent peer-following is not claimed, and no such API was
identified in the inspected primary `Map` implementation. Arbitrary primary
Python plugins are not supported by these APIs.
Manual preview and synthetic maps are not physical sensor or live telemetry evidence.

### Cached interface discovery

The primary `main.py:list_discovered_interfaces` lazily reuses
`RNS.Discovery.InterfaceDiscovery(discover_interfaces=False)`; `ui/map.py`
projects transport advertisements with coordinates on its60-second update.
`GenerateSidebandDiscoveryVectors.py` executes those actual methods, actual
interface appearance methods and the pinned RNS passive constructor/cache
listing. Clocks, isolated cache metadata, active-owner lists and inert map
widgets/triggers are disclosed seams, not a substitute discovery protocol.
Eight input entries produce seven cached/listed records after real expiry
pruning and three primary map markers. Distinct synthetic ordering inputs
avoid filesystem tie-order differences; Windows/Linux independently generated
fixture bytes match SHA-256
`01ce009628a837074cbbd4717c63376aa285859ea5419666944658a72a2d57c7`.
The fixture records exact primary revision/source digests and canonical-LF
RNS discovery SHA-256
`3c7833ccd1a682c7313208874e1a66ff9495280d01827c67b0405d7402d4a89d`;
the CRLF checkout digest differs, not the executed Git blob.

`SidebandClient.GetMapDiscoveryAsync` requires a connected node and inspects
that selected configuration's `storage\discovery`, not an unrelated remote
shared owner's cache. Its actual management query applies the current owner's
blackhole policy, including when connected as a shared client. The public
stack reader prunes expired, unauthorized and blackholed cache files; this is
**not a read-only filesystem operation**. No listener or autoconnect is started
by this inspection. At most512 candidate files and256 retained records are
examined; an over-cap cache can omit records. Admission permits one refresh
and no queued refresh backlog. Cancellation/disposal release lifecycle
ownership, but the public synchronous cache constructor cannot be interrupted
mid-parse; checks surround its bounded scan. Queued lifecycle cancellation
within two seconds does not prove that bound during parsing.

Only public metadata is detached into map DTOs; passphrases, network names,
raw packed payloads and operator credentials are omitted. Raw detached packed
buffers are zeroed. Discovery IDs are32-byte/64-hex identifiers, not
16-byte LXMF delivery hashes. Their separate marker kind/inspection never
creates a contact/conversation or copies the ID into an LXMF peer input.
Out-of-range locations report a diagnostic and omit the marker without
discarding other metadata. Native accepts legitimate zero coordinates
(five markers in the fixture) and replaces each snapshot so moved/deleted
records do not retain stale markers, deliberately fixing primary truthiness/
retention behavior rather than claiming exact reproduction of those defects.

The interface overlay refreshes at most once per60seconds while Map is visible;
its checkbox forces a fresh snapshot. Disabling/disconnecting clears it.
Failure clears stale markers and reports an error; restoring a valid cache
recovers service. Reported telemetry retains priority within the shared
256-marker display limit. Six focused reference/lifecycle/headless cases pass
on each host, including real shared-owner management RPC, blackhole pruning,
missing-directory recovery, admission/cancellation/disposal and restored-view
smoke overrides. Public RPC deserializes maps differently from local management;
the reader accepts both typed pair collections rather than dropping shared
blackhole policy.

Two actual-primary live cases pass on each host: direct Python TCP hub and a
separate .NET transport. Actual RNS signed discovery announcements flow both
ways; primary's real passive facade lists the native advertisement and the
native application projects the Python advertisement at0,0 across restart.
Required stamp8 is an explicit non-default setting. Advertised endpoints are
synthetic: this proves signed announcement/cache/application listing, not
successful connectivity, autoconnect or physical position.

Published `--smoke-ui --smoke-discovery --data-dir <private-data> --rnsconfig
<isolated-rns>` requires explicit configuration and data, renders the real
cached marker and fails empty cache with exit2. The `Discovery` Pester case
copies an isolated synthetic profile to private native temporary storage,
renders across two process starts, then deletes the owned cache and requires
the explicit failure. This is native renderer/restart evidence, separate from
the actual-primary network cases.

Final focused acceptance is6/6 per host; final actual-primary live acceptance
is2/2 per host. Strict NativeAOT publishing passes with zero warnings/errors
using `dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj -c Release
-r <rid> --artifacts-path <root> -p:ILLinkTreatWarningsAsErrors=true
-p:IlcTreatWarningsAsErrors=true -warnaserror -v quiet`.
The final `Native,Discovery` Pester selection passes6 cases per host with one
explicit unrelated generated-primary-map-profile skip. Discovery itself
passes1/1 on both hosts, including the initially disabled/off-centre saved
view, two renders/restarts and empty-cache exit2. The final complete Sideband
regression passes1,894 cases with50 explicit live/prerequisite skips.
The separate solution/strict-catalog gates are recorded in
[testing provenance](testing.md#baseline-provenance).

Selected final native executables:

- Windows `artifacts\builds\fleet-discovery-native-win\publish\Sideband.Desktop\release_win-x64\sideband.exe`,
  SHA-256 `2f64a131a635c2e11aa8ceb60daca7e8411b9e4a1eb40520205f76df318e2b84`.
- Linux `/home/dennis/fleet-discovery-native-linux-serviced/publish/Sideband.Desktop/release_linux-x64/sideband`,
  SHA-256 `7a695759338eec8145349d824b29225fb3ae41e5e07dfdb3d541bc65f103a8f6`.

The selected Linux executable is freshly rebuilt with SDK10.0.403 and
NativeAOT compiler10.0.14 after the compiler10.0.13 advisory; its assets record
both `Microsoft.DotNet.ILCompiler/10.0.14` and the Linux compiler runtime pack.
The unchanged `Native,Discovery` Pester selection is independently repeated:
6passed/one unrelated profile skip, including two discovery renders/restarts
and explicit empty-cache failure. Windows already selected SDK403. The earlier
Linux compiler13 executable, SHA-256
`b88629353ffbf42fac21dc954dae942676572cbc8ed6f39b4141933f4447aecf`,
retains its earlier behavioral evidence but is not the selected serviced binary.
Other previously recorded native executables are not silently relabelled.

The synthetic profile is exported by the headless case to
`artifacts\tests\fleet-discovery-native-race-profile`; export refuses to
overwrite an existing profile. Earlier smoke acceptance failed: the awaited
close task depended on its own tracked operation completing, and saved map
restoration could overwrite the smoke overlay/centre. Close is now scheduled
outside that tracked task, restoration completes before explicit positioning,
and both the headless off-centre/disabled-view regression and native empty-cache
failure execute the unchanged real renderer/exit assertions.

```powershell
# With the pinned actual-import reference environment selected:
& $env:DOTRETICULUM_PYTHON tests\Sideband.Tests\GenerateSidebandDiscoveryVectors.py --source $env:DOTRETICULUM_SIDEBAND_UPSTREAM --output <reference-json>
dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --filter "FullyQualifiedName~SidebandMapDiscovery|FullyQualifiedName~SidebandDiscovery"
dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --filter "FullyQualifiedName~ActualPrimaryPassiveDiscoveryAndNativeMapExchangeAdvertisementsAcrossRestart"
# Select DOTRETICULUM_SIDEBAND_BINARY and the exported synthetic
# DOTRETICULUM_SIDEBAND_DISCOVERY_PROFILE:
& tests\run-pester.ps1 -Path tests\Pester\Sideband.Tests.ps1 -Tag Native,Discovery
```

Focused acceptance:

```powershell
dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --artifacts-path artifacts\builds\sideband-maps-parent --filter "FullyQualifiedName~SidebandMap|FullyQualifiedName~SidebandTelemetryUi"
dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj -c Release -r win-x64 --artifacts-path artifacts\builds\sideband-telemetry-maps-native-win -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
# Select DOTRETICULUM_SIDEBAND_BINARY from the complete publish directory:
& .\tests\run-pester.ps1 -Path tests\Pester\Sideband.Tests.ps1 -Tag Native,GUI
```

The focused Windows/Linux corpus exercises reference vectors, actual loopback
HTTP/XYZ/User-Agent/no-redirect behavior, failed-peer recovery, quotas, cancellation,
admission/disposal, real SQLite import/TMS/WAL/restart, failing view observers and
original headless map/report workflows. Separate strict NativeAOT publishes and
Pester runs exercise actual window rendering of raster tiles and configured manual
report preview on Windows and Linux/WSLg. Online provider access, upstream Kivy UI,
physical location/audio and the later reported-overlay slice were not exercised
by these earlier binaries.

Focused result: **17/17 on Windows and Linux, zero skips**. Strict `win-x64`
and `linux-x64` NativeAOT publishing passed with both linker/compiler warning
flags above. The selected native/GUI Pester suite passed **7 cases per host**;
one unrelated generated-media-profile rendering case explicitly skipped.
It did not convert that absent profile into a pass.

| Accepted map/manual-preview executable | SHA-256 |
|---|---|
| `artifacts\builds\sideband-telemetry-maps-native-win\publish\Sideband.Desktop\release_win-x64\sideband.exe` | `31b425cc1be2eab01934d54be74a44c859b213d3259a5974cf3e0d5a0789d368` |
| `/home/dennis/fleet-sideband-telemetry-maps-native/publish/Sideband.Desktop/release_linux-x64/sideband` | `a8260ec653f6019d55ee39718a15dda1b368489ae40a0dbdd4e5df88ad6e750a` |

`sideband --import-map FILE --data-dir PROFILE` validates/imports an offline
snapshot without Reticulum or audio. `--smoke-ui --smoke-map` and
`--smoke-ui --smoke-telemetry` are explicit acceptance modes, not normal startup
behavior; the latter needs enabled telemetry settings. Runtime smoke connects
only when runtime collection is opted in and `--rnsconfig` is explicitly supplied.

### Provider presets and supported profile view activation

The original Map screen exposes all thirteen observed provider identifiers,
including custom HTTPS templates and quadkey VirtualEarth requests. MapTiler
and Stadia require the user's own key; no upstream bundled keys are copied.
Applying a preset explicitly revokes online consent and clears the key-entry
control. The private view file retains the configured template (including a
user-supplied key), attribution and source zoom range over restart. Protect that
profile; exporting/copying it can disclose provider credentials. Public provider
availability, terms, quotas and account authorization have not been tested.
Native presets upgrade the primary's HTTP URLs to HTTPS and accept only bounded
query-safe key characters; custom insecure HTTP remains rejected in this UI.
These are intentional safety differences, not identical primary defaults.

Offline `--import-profile` activates finite coordinates, whole-number zoom and
the selected enabled provider. Layer selection/fallback and initial online-source
coordinate clamps execute the primary contracts. Missing private provider keys
produce an explicit notice and safe OpenStreetMap fallback, without importing
online consent. Offline/external file paths are never followed; explicitly
import the owned raster MBTiles snapshot instead. All remaining config keys stay
in the private archive. This is not complete map-setting migration or activation
by the generic JSON-settings import command. Malformed supported settings fail
the entire staged profile transaction, preserving source and absent destination.
Legacy view files default to the prior 0-24 zoom range; presets use the observed
0-19 range. Offline import starts within stored raster zooms and permits the
primary metadata maximum thereafter. Rendering still clips at Mercator's finite
latitude limit rather than reproducing the primary pole defect.

`GenerateSidebandMapProfileVectors.py` executes actual AST-selected methods at
`d069122af14880e43607aed0b71add4c7806fbe0`: 13 providers, six layer decisions
and three constructor-clamp observations. The fixture records source SHA-256:
`source.py` `d3162e1acd36c02617842f93e2e7b67f96a582136819c25903c7d631c7790986`,
`ui/map.py` `cfd65489d5f94ddac3990ebd50c6b346e3cd6d0e0c3bfc1e30880ca983e5bf11`.
Kivy density is fixed to identity; the full UI constructor, bundled keys,
HTTP and rendering are not executed. Separate `GenerateSidebandProfile.py
--map-profile` executes actual primary persistence with synthetic map settings
and the existing disclosed unused-import stubs, not a live map peer.

Focused Windows/Linux command:
`dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release
--filter "FullyQualifiedName~SidebandMap|FullyQualifiedName~SidebandProfileImportTests"`
passes 64 cases with one explicit unavailable generated-profile skip per host.
It includes original desktop preset/rejection/restart and actual imported-view
display, bounded source zooms, atomic failure and source preservation. Strict
warning-as-error NativeAOT publication and selected native/GUI acceptance are
separate from these managed cases and from public-provider or physical location
evidence. The new native profile case requires
`DOTRETICULUM_SIDEBAND_MAP_PROFILE` pointing to the generated primary profile;
without it the case explicitly skips.

Selected `tests\run-pester.ps1 -Path tests\Pester\Sideband.Tests.ps1 -Tag Native,GUI`
passes 9 cases/one unrelated generated-media rendering skip on each host,
including actual primary map-profile activation, source preservation, native
restart and original raster drawing. Windows used Pester 6.2.0; Linux used 5.7.1.
The first Linux operator invocation failed before testing because `DOTNET_ROOT`
was not supplied to the PowerShell launcher; setting the documented runtime
environment before launch restored the unchanged suite. No dependency was
installed and no assertion was weakened.

| Frozen provider/profile acceptance executable | SHA-256 |
|---|---|
| `artifacts\builds\fleet-map-profile-native-win\publish\Sideband.Desktop\release_win-x64\sideband.exe` | `961eabdd288aacd6d3b553449a0272d5fc69a398228a35576c5bc12fb08536d7` |
| `/home/dennis/fleet-map-profile-native-linux/publish/Sideband.Desktop/release_linux-x64/sideband` | `ac7e5bba4702316b16d438a9e82c106434667f726f1f4fe40a4596fe13b99092` |

These binaries precede unrelated subsequent LXST/remote-command development;
their selected map acceptance does not validate those mutable workstreams.

After the bounded pending-dial lifecycle fix, strict NativeAOT publishing with
`-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true` passes
again on Windows/Linux. Selecting the binaries below and
`DOTRETICULUM_SIDEBAND_MAP_PROFILE`, then running
`tests\run-pester.ps1 -Path tests\Pester\Sideband.Tests.ps1,tests\Pester\SidebandVoice.Tests.ps1 -Tag Native,GUI`
passes 12 cases/four skips on Windows and 14 cases/two skips on Linux.
These exercise actual persisted primary map settings, native raster/manual
telemetry/HTTPS rendering, synthetic Opus/Codec2 calls both origins and restart.
Linux additionally exercises SIGTERM with blocked/closed stdin. Generated-media
rendering and the separate one-hour mode remain explicit skips; earlier retained
one-hour evidence is not relabelled as a rerun. `--help` also passes.

| Frozen map/dial lifecycle acceptance executable | SHA-256 |
|---|---|
| `artifacts\builds\fleet-map-dial-native-win\publish\Sideband.Desktop\release_win-x64\sideband.exe` | `dc8cdbb1f61a9d68f8b796368b311fe6a40e2674c864f778478ecc0b0b1a7954` |
| `/home/dennis/fleet-map-dial-native-linux/publish/Sideband.Desktop/release_linux-x64/sideband` | `c5cbf7a57ac185091471a30f358c1f83ab95fd04369425ad1a42516ea04f5bb8` |

These binaries include the then-frozen LXST file-drain and remote backend
sources. Selected map/voice acceptance does not establish remote-command UI,
published remote execution or later AGC changes.

## Python-plugin and remote-command boundary

The pinned primary loads arbitrary Python command, service and telemetry plugin
modules and passes its actual `SidebandCore` owner to them. That exposes far more
than a language-neutral callback ABI: plugins may import Python modules, access
core state/methods, create threads and perform unrestricted OS/network actions.
Executed loader observations also show command loading depends on the service
plugin enablement setting and a disabled command instance's `start` is still
called. Those observations are not an endorsement of executing untrusted code.

`Fixtures/sideband-extensions.json` records actual send/authorization/reply/loader
execution with the pinned core hash. It includes command fields, trusted-setting
precedence, signature gating and replies; it is reference evidence only.
Built-in ping/echo/signal requests and telemetry requests are implemented as
described below. Arbitrary Python-plugin execution is not implemented; these
built-ins do not establish that ABI or general remote execution.

There is no production Python interpreter, Python-to-Core bridge or compatible
Python plugin ABI here. A different .NET extension interface would not supply
that compatibility. Full arbitrary Python-plugin support requires a consequential
runtime/architecture decision beyond the current native-only, Python-as-reference
boundary; it remains a required unresolved contract, not a passing native API
substitution or a hardware deferral. No example plugin source is copied.

### Built-in commands and incoming admission

`SendCommandAsync` returns explicit empty/address/offline/capacity outcomes or a
local operation ID. Admission is not transmission, delivery or remote execution.
The Remote page configures global anyone/trusted request permissions shared with
telemetry, independent per-conversation request permission and
`lxmf_ignore_unknown`. Saving individual permission preserves trust and telemetry
sending. It supports explicit propagated selection, cancellation and a newest-50
conversation/reply preview; full history remains in Conversations. No Python,
shell command, audio device or sensor is started by these controls.
Concurrent page loads join one settings read, disable policy edits while it is
pending and preserve peer text edited during that read. Completed loads replace
the pending status explicitly.

Actual primary parsing removes every `echo ` occurrence after the initial
prefix, rejects an empty echo, recognises `sig` and `ping` prefixes and treats
other text as a plugin request. Native built-ins preserve that parsing and reply
text. Unsupported plugin/unknown command IDs are explicitly reported, never
executed locally. Signal replies use received link observations or state that no
reception information exists; TCP tests are not physical signal measurements.

Unknown-conversation filtering applies before ordinary content/media/telemetry
ingestion as well as command authorization. An announce or telemetry appearance
alone does not create a known conversation. Explicit contact/conversation edits
and ordinary message history promote membership; command and telemetry-only
traffic do not silently create it. `lxmf_ignore_unknown` rejects unknown sources
even when anyone requests are otherwise allowed. For known sources,
anyone permission wins; otherwise trusted-only overrides individual request
permission. Queued replies recheck authorization before sending.

Native schema-1 contacts without `ConversationKnown` default to known, preserving
legacy explicit/imported contacts. Their historical creation origins were not
stored, so old announce-only contacts cannot be reconstructed. New discoveries
are distinguished correctly. This is an explicit migration limitation, not
evidence of perfect legacy filtering. Native continues suppressing empty ordinary
history entries while promoting the corresponding conversation; primary saves
them. Unverified command-field messages are strictly dropped rather than falling
through to primary's ordinary ingestion.

Commands share the existing 64-slot telemetry worker, with 64 bounded remote
admissions, at most 64 maps per incoming batch and 4096 strict UTF-8 bytes per
command/echo payload. Bad signatures, malformed text/maps and unauthorized
requests fail explicitly without disabling later work. Outgoing history records
the operation, state and actual method; replies are ordinary inbound text.
Cancellation releases admission and an unreachable peer does not stop healthy
commands. Unstarted queued work is neither durable nor replayed after restart.
Settings imports validate the active boolean transactionally, preserve it across
unrelated imports and export its actual value/default. Invalid persisted policy
is reported rather than silently defaulted.

`GenerateSidebandRemoteVectors.py` executes 120 primary observations;
`GenerateSidebandAdmissionVectors.py` executes 123 more: 112 delivery dispatches,
10 conversation-creation observations and one announce trace. Both verify the
exact primary revision and core hash recorded above. Windows/Linux independent
execution produces byte-identical fixtures. Synthetic owners/observers replace
constructor, storage and GUI boundaries, not the executed methods; vectors alone
are not cryptographic or live interoperability evidence.

Independent actual-primary acceptance on each host passes 1,484 cases with no
skips, including parsing/policy regressions, command origins/replies, ordinary
unknown filtering, restart, failed-peer recovery, malformed UTF-8/oversized
batches and bad-signature injection. Direct, Python transport and .NET transport
topologies use a transparent TCP proxy delaying each read 100 ms in each
direction; it neither parses nor modifies RNS traffic. Undelayed Linux primary
can return false after transmission because of a SQLite `UNIQUE lxm.lxm_hash`
failure. Delayed acceptance is not undelayed API-stability evidence. Windows
primary optional filter acceleration falls back to actual Python filters;
these cases are not codec/filter acceptance.

With the reference environment from CONTRIBUTING configured:

```powershell
& .\tests\Interop\Invoke-SidebandRemoteAcceptance.ps1 -ArtifactsPath artifacts\builds\fleet-remote-parent-full-win -OutputDirectory artifacts\tests\fleet-remote-parent-full-win -IncludeRegression
```

The exact-source Linux run uses `/home/dennis/fleet-remote-parent-full-linux`
build output and
`/home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/tests/fleet-remote-parent-full-linux`
evidence. The Windows/Linux operators complete in 10m33s/10m11s respectively.
They verify actual imported modules/pins, regenerate/compare all three fixtures,
and require each of the thirteen named application cases to pass without skips.
The additional `GenerateSidebandRemoteTransportVectors.py` executes 32 actual
command-selection/packing and twelve reply-send observations. Fixture SHA-256
is `ef5911e349f9c7dc37521e455cd61f4d937d974195bd0975414ef489f1253713`.
These use the same primary revision/core hash above, real `LXMessage.pack` and
disclosed offline storage/router seams, not a live ratchet exchange.

Propagated commands are separately verified with the Python PN also hosting the
TCP hub, a separate Python transport/PN, and a separate .NET transport/Python PN.
Sender `Sent` and durable PN storage are asserted before recipient retrieval;
neither means execution or endpoint delivery. PN/native/primary restarts and
active retrieval cancellation are exercised. The Python PN disables autopeering
and uses an 8 MB store, default stamp 16, transfer 256 KB and sync 10240 KB limits.
Both endpoints explicitly allow requests and know the corresponding conversation;
automatic inbox synchronization is off.

Controlled direct failure completes the public RNS handshake and immediately
tears down the link, separately suppressing packet/proof callbacks and restoring
them for recovery. Default native timeouts/retries are unchanged. Enabled/disabled
fallback and healthy-peer recovery pass. A separate .NET-hop case takes each
reply recipient offline, observes actual primary/native automatic-reply
propagation, restarts it and explicitly retrieves the reply. Cancellation fills
all 64 admissions, rejects the extra request, cancels/restarts, then completes 32
commands from each origin with no native observers. This is bounded operation,
not an hours-long command-service soak.

Outgoing history records actual packed `Method`, including opportunistic
promotion, rather than `DesiredMethod`. Configured one-shot propagation retry
also applies to initially propagated commands, as the executed primary policy
does; native-origin second-group failure and recovery are now exercised below. Silent
blackholing is not the accepted controlled-teardown case. A separate investigative
two-hop default-timing probe observes native/primary terminal failure at
102.516s/121.991s, six/four link attempts and an 18.0004s outgoing link deadline.
These are preserved pre-correction samples, not universal elapsed limits or
final-source suite acceptance.
The earlier alleged five-minute exhaustion was suite elapsed time: the original
case actually reached failure in 107.277s before failing its history-method
assertion. No unbounded stack bug was demonstrated. The native attempt/CLOSED
rescheduling error is now corrected and independently accepted on Windows/Linux
with executed vectors, the full LXMF live suite and native refusal/recovery
probes; see [LXMF retry accounting](lxmf.md#outbound-link-retry-accounting).
That library acceptance does not relabel the earlier application observations or
resolve the separate primary callback-lock defect.

Arbitrary random loss, RF measurements, arbitrary Python plugins and
prolonged command-service operation remain
unverified or missing. These managed application cases do not establish
published-native propagation execution or upstream GUI behavior.

Original headless desktop/settings/profile-import acceptance passes 55 cases
and one explicit generated-profile skip per host. The final Windows focused
run additionally includes the deterministic paper regression: 56 passes and
one explicit skip. Full Linux managed acceptance
passes 1,775 cases with 34 explicit skips. Earlier full Linux batches twice failed
paper import after a stale preceding error satisfied a completion wait. A
deterministically held store gate reproduces that stale status before the fix:
expected pending, observed the previous offline-creation failure. Accepted paper
operations now expose pending status before awaiting; original completion
assertions and five-second deadlines remain. Three focused paper/remote UI cases
pass after the fix; earlier failed TRX is retained at
`artifacts\tests\fleet-paper-pending-before`.

Strict NativeAOT publication uses:

```powershell
dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj -c Release -r win-x64 --artifacts-path artifacts\builds\fleet-remote-ui-final2-win -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -v quiet
```

Linux uses `-r linux-x64 --artifacts-path
/home/dennis/fleet-remote-ui-final2-linux`. Both pass; Windows requires the
documented MSVC discovery path. Published feature acceptance:

```powershell
& .\tests\run-pester.ps1 -Path tests\Pester\Sideband.Tests.ps1,tests\Pester\SidebandPaper.Tests.ps1,tests\Pester\SidebandVoice.Tests.ps1 -Tag Native,GUI
```

With the selected binary, generated map profile and documented codec libraries,
Windows passes 17 cases/five skips; Linux passes 19/three skips. These include the
real Remote window sending ping/echo to another native daemon, cancellation of an
unreachable lookup, a subsequent healthy reply, policy export after restart,
paper URI/QR rendering/export, map/manual preview/TLS and synthetic voice
recovery. Windows additionally skips two POSIX cases. Both skip unselected media
profile/oracle and one-hour modes; prior evidence is not relabelled as a rerun.
An initial Windows invocation omitted codec-library paths and failed both voice
origins; unchanged binaries/assertions pass with those prerequisites supplied.
This is not physical audio or published-native Python-command interoperability.

| Frozen remote/paper acceptance executable | SHA-256 |
|---|---|
| `artifacts\builds\fleet-remote-ui-final2-win\publish\Sideband.Desktop\release_win-x64\sideband.exe` | `8af548182fad39389325c5b1692c658138f51ddf3c25347ee354125463121f34` |
| `/home/dennis/fleet-remote-ui-final2-linux/publish/Sideband.Desktop/release_linux-x64/sideband` | `384ab037c660568128cbb830f8f825d8a52f857232a156574da29599ebda6723` |

`--smoke-ui --smoke-remote <lxmf-address>` is an explicit native acceptance
extension requiring a test data directory and isolated RNS configuration. It
changes test-profile request settings and performs real network requests; it
does not run implicitly in normal desktop operation.

### Initially propagated retry and pinned callback-lock defect

`GenerateSidebandPropagationRetryVectors.py` executes the actual imported
primary `message_notification` at `d069122af14880e43607aed0b71add4c7806fbe0`,
core SHA-256 `8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25`.
Its24 combinations distinguish Failed/Rejected/Cancelled, eligibility, stamp
failure and no-display policy; an actual second notification observes consumed
one-shot eligibility. These are explicit offline router/DB observers, not a
runtime queue substitute. Windows/Linux independent execution byte-matches
fixture SHA-256 `509bc2bc06f0e27fab57baa2b0803c68f84df5f4697691af951299762ce8d7e9`.

The strict operator
`tests\Interop\Invoke-SidebandPropagationRetryAcceptance.ps1 -ArtifactsPath
<build> -OutputDirectory <results>` requires **eight passes/no skips**:
two reference cases and six unique policy/origin rows. Parent independently
executes it on SDK403/runtime14 on Windows and Linux: **six pass/two fail/no
skips on each host**. This is a **failed both-origin acceptance gate**, not
completed primary retry interoperability. The successful cases are both reference
cases, all three native-origin rows and primary-origin retry-disabled. Cases
are individually reported; native cases are not duplicated by another wrapper.

Native-origin means the managed C# application using real pinned Python
recipients/PNs, not the published NativeAOT desktop. With retry disabled, one
Failed group occurs. With retry enabled, restoring the PN after the first failed
group permits storage/retrieval/exact echo; continuous PN failure produces
exactly two Failed groups followed by an eight-second no-third observation.
Every passing endpoint scenario restores service and independently sends,
stores, retrieves and authenticates a **new** healthy command/echo.
PN storage and sender Sent are asserted before explicit recipient retrieval:
neither is called recipient execution. Throwing native observers do not stop
processing. Copied/default adapter and evidence paths also execute successfully
without operator-specific bridge overrides.

The Python PN is a separate real LXMF node and TCP transport hub. Both endpoints
use disclosed transparent100ms-per-read latency each way, avoiding the separately
observed primary callback/SQLite-insertion race. Fault injection uses the public
PN link-established callback to tear down links; recovery delegates its original
callback. No protocol replacement, reference patch, manual message-state reset,
retry-count override or deadline relaxation is used. PN autopeering is off,
storage8MB; stamp16, transfer256KB and sync10240KB remain defaults. Endpoints
explicitly know/authorize their conversation and do not automatically fetch.
The `-NativeOnly` operator selects exactly the three existing native-origin rows;
it never accepts the required both-origin gate.

The two enabled-primary rows fail for a source-backed lock cycle, independently
reproduced on each host with `sideband_retry_diagnostic.py`:

- Pinned LXMF `LXMRouter.py` SHA-256
  `56b6a8e6b9f79256c4ae297340791eebcc1b6cda3cec04ab2dbe2f1efb53caa9`
  creates `threading.Lock` at184. Real propagation-stamp completion clears
  deferral at2766. `process_outbound`2799–2802 holds the lock when exhausted
  attempts at3047 call `fail_message`.
- `fail_message`2688–2694 synchronously invokes the failed callback. Actual
  Sideband4811–4826 consumes retry eligibility, resets the message and
  synchronously re-submits it. With both deferral flags false, actual
  `handle_outbound`1906–1907 waits while that same held lock is locked.
- Read-only public observations find attempts5, lock held, no pending/deferred
  entries and no callback return; restoring the PN does not store the request.
  Restart preserves identity and permits a **fresh** propagated request,
  retrieval and authenticated exact echo. The eight-second observation alone
  does not prove indefinite waiting; unchanged source establishes the re-entry
  cycle. Replacing the lock with an RLock alone would not fix the explicit
  `while locked()` wait.

An upstream correction must avoid synchronous re-submission under that lock;
the reference is not patched here. Native bounded-worker retry already avoids
this cycle. This defect is distinct from the corrected native six/four
link-attempt scheduling error above and does not justify a DotReticulum stack change.
Failed primary rows retain their assertions and deadlines; no skip or
success-shaped fallback hides them. Direct-failure fallback evidence is not
relabeled as this initially-propagated retry case.

Parent full-gate TRX/scenarios are
`artifacts\tests\fleet-remote-retry-parent-final-win` and
`/home/dennis/fleet-remote-retry-parent-final-linux-results`; separate lock/restart
probes are under `artifacts\tests\remote-retry\primary-lock`.
These are local managed application observations, not new hosted, primary GUI,
published-native propagation, additional PN/hop variants or prolonged-service
acceptance.

## Public HTTPS software mirror

The original Utilities controls start/stop an optional local HTTPS file mirror,
not an RNS/rngit service. Select a dedicated administrator-controlled public
directory, a listen address and port, then explicitly authorize unauthenticated
access for that session. Default bind is loopback, port 4444. Root, address, port
and consent are not persisted or automatically restarted. Certificates persist
separately under the private profile's `repository-certificate` directory;
the public root cannot contain that private storage or the profile itself.
Never permit untrusted concurrent filesystem writers: portable managed path
checks are not OS-level `openat`/no-follow filesystem isolation.

The service provides the observed root/index, package-list and static-file routes
using original code and files supplied by the operator. GET-only requests,
headers, target length, file/list sizes, clients and deadlines are bounded.
Uploads, request bodies, pipelining, keepalive, directory listing and inherited
HEAD behavior are not supported. Traversal/reparse paths are rejected; missing
files return 404 rather than the primary handler's generic 500. The package list
contains sorted regular filenames, not symlinks/subdirectories. Responses carry
explicit lengths/MIME types, `Connection: close` and `nosniff`.

The self-signed P-256 certificate is not a public trust anchor; independently
verify the displayed SHA-256 fingerprint. The native service intentionally
persists its certificate rather than regenerating it on every start. Linux uses
ephemeral imported keys and mode-600 private storage. Windows Schannel rejects
the tested ephemeral-key mode, so the verified provider uses temporary UserKeySet
ownership and inherited directory ACLs; restrict the directory to its owner.
Cancellation/stop/window shutdown joins owned clients and disposes the certificate.
One failing peer or error observer does not disable later service.

`GenerateSidebandExtensionObservations.py` executes the pinned primary handler
and certificate methods with disclosed synthetic owners/files; it records exact
source hashes and five routes in `Fixtures/sideband-extensions.json`.
No upstream pages, packages, sounds or other restricted assets are copied.
Automatic upstream-content downloading, self-updating, trust-store installation,
physical LAN/firewall acceptance and live primary HTTPS/GUI peers are unverified
or absent; neither the reference observations nor loopback TLS prove them.
These APIs do not load arbitrary Python plugins or provide their ABI.

Independent backend acceptance passed 39 Windows cases with one explicit Unix-mode
skip, and 40/40 Linux cases. Frozen strict NativeAOT probes on both hosts executed
fingerprint-pinned HTTPS index/package/file/404 requests, certificate restart and
joined disposal. The actual desktop integration additionally verifies consent,
invalid input, root/profile separation, TLS routes, restart, peer errors and
start/stop/close races through original headless UI controls.

```powershell
dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --artifacts-path artifacts\builds\sideband-repository-parent --filter "FullyQualifiedName~SidebandRepository|FullyQualifiedName~SidebandMap|FullyQualifiedName~SidebandTelemetryUi"
dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj -c Release -r win-x64 --artifacts-path artifacts\builds\sideband-repository-app-native-win -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
```

The combined focused corpus passed **69 cases/one Unix-mode skip on Windows,
70/70 on Linux**. Published acceptance uses the ordinary Utilities start handler
only in explicit `--smoke-ui --smoke-repository PUBLIC_ROOT --data-dir PROFILE`
mode: real fingerprint-pinned TLS fetches succeed before rendering is accepted,
and two native process runs retain the same certificate. This mode never opens
Reticulum or audio devices and does not affect normal startup.

Strict complete-application NativeAOT publishing passed with zero warnings/errors
on both hosts. The selected combined `Sideband.Tests.ps1` and
`SidebandVoice.Tests.ps1` published suite passed **11 cases/four explicit skips on
Windows and 13 cases/two skips on Linux**, including the TLS/render/restart flow,
map/manual preview, both synthetic codec-call origins and Linux SIGTERM.
The one-hour frozen call is separate, not replayed by this short suite.

| Accepted repository-integrated executable | SHA-256 |
|---|---|
| `artifacts\builds\sideband-repository-app-native-win\publish\Sideband.Desktop\release_win-x64\sideband.exe` | `8c37cf3d24fe34d3a874315b44d6afd25a5415418b6752587e3354516ea4ed8f` |
| `/home/dennis/fleet-sideband-repository-app-native/publish/Sideband.Desktop/release_linux-x64/sideband` | `e7550581a36680cb90537bb2508f8772711920ff0e2015cb07af005c546cec2d` |

## Integrated source and native acceptance

The final integrated source, including the appearance arithmetic correction and
shared LXMF paper-envelope helper, passes the unified coverage/Pester gate:

```powershell
& .\tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath artifacts\builds\fleet-sideband-integrated-unified -OutputDirectory artifacts\tests\fleet-sideband-integrated-unified
dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj -c Release -r win-x64 --artifacts-path artifacts\builds\sideband-integrated-native-win -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -v quiet
# Same publish options with linux-x64 and /home/dennis/fleet-sideband-integrated-native:
& .\tests\run-pester.ps1 -Path tests\Pester\Sideband.Tests.ps1,tests\Pester\SidebandVoice.Tests.ps1 -Tag Native,GUI
& .\tests\run-compatibility.ps1 -SkipManaged -Pester -Gui -OutputDirectory artifacts\tests\fleet-sideband-integrated-native-catalog
```

The unified gate passed **2,082 managed cases, 87 explicit skips**, Pester tooling
**61 cases**, and merged coverage of all eight production assemblies. Strict
Windows/Linux publishing passed with zero warnings/errors. Selecting each complete
publish directory through `DOTRETICULUM_SIDEBAND_BINARY` independently passed
**11/four skips on Windows and 13/two skips on Linux** for real HTTPS/render/restart,
maps/manual preview, synthetic codec calls/recovery and Linux SIGTERM. Windows
codec DLLs were explicitly present on `PATH`. The selected Windows native/GUI
catalog also passed; missing other binaries/live references and the deliberately
unselected hour-long mode remain skips/availability records. This is not hosted
evidence or a new one-hour/physical/Python-peer native voice claim.

| Final integrated executable | SHA-256 |
|---|---|
| `artifacts\builds\sideband-integrated-native-win\publish\Sideband.Desktop\release_win-x64\sideband.exe` | `2f92080d4039ae7f63fb7aa02dc60f4be9582b2b77f9c91515b19dc9af7191bd` |
| `/home/dennis/fleet-sideband-integrated-native/publish/Sideband.Desktop/release_linux-x64/sideband` | `b218409dfe4d921a1efb26fe68c94632c398f5993cdc0643d2ae96881a1e2797` |

## Current implementation evidence

The implementation targets reported version 2.2.0 at pinned primary source
`d069122af14880e43607aed0b71add4c7806fbe0`, three commits beyond release tag
`cba73130287f0d8b9deb7da4d20ceec09b7fe394`. Reference execution uses that
primary source, not a relabelled release-tag or mirror run. The version label
is a target, not a parity claim. The upstream source is not committed.

Validated commands and outcomes:

| Command | Result |
|---|---|
| `dotnet build DotReticulum.Ecosystem.slnx -c Release --artifacts-path artifacts\builds\fleet-resume` | Passed, zero warnings/errors |
| Full `dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release` with separate Windows/Linux artifacts paths, all live/application/profile/voice prerequisites enabled and `DOTLXST_NATIVE=1` | Frozen voice/media acceptance corpus: 398/398 on each host, no skips. Actual application/media/calls, refusal/recovery, scheduled fetch, file, codec, fake-device, router and WAL/schedule migration cases pass. This also includes isolated telemetry foundations, not application telemetry/maps. Later extension-reference cases are separately scoped |
| `dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --artifacts-path artifacts\builds\fleet-sideband --filter FullyQualifiedName~FailedDirectDelivery` with pinned live paths | Windows 2/2; Linux equivalent 2/2: actual refusal, one-shot propagation policy and direct recovery |
| Same command with `--filter FullyQualifiedName~ActualPrimarySidebandDaemon`, `DOTSIDEBAND_LIVE=1` and the pinned Sideband source | 4/4 on each host: actual unmodified application-core direct/propagated round trips, trust on/off, signed ticket inclusion and persisted reply tickets |
| Same command with `--filter FullyQualifiedName~ConversationTrustUnread` | 1/1 on each host: original desktop trust/read/unread controls and restart persistence |
| Same command with `--filter "FullyQualifiedName~ConversationStateTests\|FullyQualifiedName~SidebandProfileImportTests"` and `DOTRETICULUM_SIDEBAND_PROFILE` pointing to generated observations/profile | 51/51 on each host; ten actual-method trust/read/ingest observations, legacy defaults, immutable trust on rename/announce, flag validation, concurrency/cancellation and WAL-only trust/unread activation |
| `dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj -c Release -r win-x64 --artifacts-path artifacts\builds\sideband-voice-native-win -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true` | Passed; strict Windows NativeAOT |
| Linux `dotnet publish src/Sideband.Desktop/Sideband.Desktop.csproj -c Release -r linux-x64 --artifacts-path /home/dennis/fleet-sideband-voice-native -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true` | Passed; strict Linux NativeAOT |
| `& .\tests\run-pester.ps1 -Path tests\Pester\Sideband.Tests.ps1 -Tag @('Native','GUI')` with `DOTRETICULUM_SIDEBAND_BINARY` and the generated media profile | 4/4 passed: help/version, identity restart, transactional settings exchange/errors and rendered validation/image flows |
| Linux PowerShell `& ./tests/run-pester.ps1 -Path tests/Pester/Sideband.Tests.ps1 -Tag Native,GUI` with the Linux binary and WSLg/software graphics | 4/4 passed; same native maintenance and rendered validation/image flows |
| `& .\tests\run-pester.ps1 -Path tests\Pester\SidebandProfileImport.Tests.ps1` with published binary, native libraries and generated profile/router/persistent observations | 6/6 on each host: identity/config/SQLite-WAL/history/files/trust/media/router/schedule import, unchanged source, actual reverse router load, offline media decode, option/target errors, corruption recovery and transactional rollback |

SHA-256 of the accepted published executables:

| Executable | SHA-256 |
|---|---|
| Windows `artifacts\builds\sideband-voice-native-win\publish\Sideband.Desktop\release_win-x64\sideband.exe` | `877366b5d6294fbd5be9d00dc73f52f797267c8bb50b9c0851040ab302773b36` |
| Linux `/home/dennis/fleet-sideband-voice-native/publish/Sideband.Desktop/release_linux-x64/sideband` | `209a0fd7824ad7f8ba48b2a756e0ad6fbdd937c1da5ae128eabe4949a277a6f2` |

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
topologies are not covered. Voice acceptance has the separate boundaries above.
No Android, macOS, physical-audio or Python-plugin behavior is claimed.
Telemetry network application acceptance remains distinct from the manual UI and map cases above.

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
file fields display metadata and can be explicitly saved. Image/audio composition,
preview/playback and persistence have the bounded, separately evidenced behavior
above. Voice calls have the separately evidenced subset above; remaining
application inventory entries are still required work, not passes or hardware deferrals.

## Desktop/headless interactive console

The original native `sideband --daemon --interactive` console executes the
supported application core; it does not embed Python, evaluate shell text or
duplicate LXMF. `status` reports the application/router, identity/delivery,
propagation, transport, telemetry/voice, conversation/announce and pending-work
state. Voice state remains explicitly available through `voice status`.
The utility name is still `sideband`.

### Executed primary inventory

Reference: primary Sideband2.2.0
`d069122af14880e43607aed0b71add4c7806fbe0`. Sources below are under
`sbapp/sideband/cli/`. `GenerateSidebandCliVectors.py` imports the actual
registry and every command module, registers all56 commands, executes their
argument checks and handlers, observes parser/longest-prefix results, and
executes configuration conversion cases. Arbitrary `raw` evaluation is not
executed. Handler vectors use explicitly disclosed recording core/context
boundary doubles, **not** a substitute application core or live evidence.
`Fixtures/sideband-cli.json` records every module SHA-256, all166 configuration
aliases/types and each handler's actual result/API calls. Its canonical UTF-8/LF
SHA-256 is `c930d2b20c54a6482480fe89551943447e2cf1492829d57e65aab89f4541033a`;
independent Windows/Linux executions produce identical bytes.

Every primary registration has **no aliases**. `*` below means an unbounded
primary argument remainder, bounded by the native command/token limits.
Status `native` means functionality beyond a primary placeholder; it does
not assert that Python implements that function.

| Registration | Arguments min..max | Source | Native status |
|---|---:|---|---|
| `help` | 0..10 | `cmd_system.py` | implemented, generated bounds/source/status help |
| `raw` | 1..* | `cmd_system.py` | unsupported; visible error, never evaluated |
| `log` | 0..1 | `cmd_system.py` | implemented for available numeric host reports, with primary labels/filtering and explicit malformed-input errors |
| `log stream` | 0..0 | `cmd_system.py` | partial: filtered host reports and voice events, Enter ends stream; not every independent stack Trace diagnostic |
| `clear` | 0..* | `cmd_system.py` | implemented, explicit local ANSI clear |
| `quit` | 0..* | `cmd_system.py` | implemented, cancels owned work/disposes core |
| `status` | 0..0 | `cmd_system.py` | implemented, general application status |
| `conv` | 0..0 | `cmd_conv.py` | implemented, known conversations including objects |
| `conv send` | 2..* | `cmd_conv.py` | implemented, core text delivery; submission is not delivery |
| `conv command` | 2..* | `cmd_conv.py` | implemented, existing bounded remote-command core; no local evaluation |
| `conv announce` | 0..0 | `cmd_conv.py` | implemented, actual LXMF announce |
| `conv sync` | 0..1 | `cmd_conv.py` | implemented, configured or explicit nonnegative limit |
| `conv sync cancel` | 0..0 | `cmd_conv.py` | implemented, cancels pending path lookup and transfer |
| `conv sync status` | 0..0 | `cmd_conv.py` | implemented, actual core state/progress |
| `conv msgs` | 1..2 | `cmd_conv.py` | implemented, latest12 by default, bounded1..1000 |
| `conv create` | 1..2 | `cmd_conv.py` | implemented, explicit duplicate/self refusal |
| `conv delete` | 1..1 | `cmd_conv.py` | implemented, selected conversation/messages/telemetry removed |
| `conv clear` | 1..1 | `cmd_conv.py` | implemented, messages removed, conversation retained |
| `conv trust` | 1..1 | `cmd_conv.py` | implemented, actual persisted trust/voice policy |
| `conv untrust` | 1..1 | `cmd_conv.py` | implemented, actual persisted trust/voice policy |
| `conv name` | 2..* | `cmd_conv.py` | implemented, explicit bounded saved name takes precedence; empty string restores automatic announce naming |
| `conv paper` | 2..* | `cmd_conv.py` | implemented, existing signed/encrypted paper core, durable URI |
| `net propagation` | 0..1 | `cmd_net.py` | implemented, configured/effective node inspection |
| `net propagation set` | 1..1 | `cmd_net.py` | implemented, actual persisted selection |
| `net announces` | 0..0 | `cmd_net.py` | implemented, latest32 of bounded persisted stream |
| `net identity` | 0..0 | `cmd_net.py` | implemented, actual public identity/delivery/name |
| `net link` | 1..1 | `cmd_net.py` | implemented, outbound link state/MTU/RTT; not inbound-link inventory |
| `config` | 0..0 | `cmd_config.py` | implemented active-native category listing |
| `config list` | 0..1 | `cmd_config.py` | partial, active aliases only, never archive keys |
| `config get` | 2..2 | `cmd_config.py` | partial, active aliases only |
| `config set` | 2..* | `cmd_config.py` | partial, typed validated active changes, atomically saved |
| `config save` | 0..0 | `cmd_config.py` | implemented, successful changes already atomically persisted |
| `config reload` | 0..0 | `cmd_config.py` | implemented, validated reload/runtime policy refresh; interfaces require restart |
| `telemetry` | 0..1 | `cmd_telemetry.py` | implemented, actual own/latest peer readings |
| `telemetry request` | 1..1 | `cmd_telemetry.py` | implemented, actual bounded queue/policy/status |
| `telemetry send` | 1..1 | `cmd_telemetry.py` | implemented, actual bounded queue/policy/status |
| `telemetry track` | 3..3 | `cmd_telemetry.py` | implemented, bounded core tracking schedule |
| `telemetry untrack` | 1..1 | `cmd_telemetry.py` | implemented, removes actual schedule |
| `telemetry list` | 0..2 | `cmd_telemetry.py` | implemented, bounded1..256 reports, default20 |
| `telemetry on` | 0..0 | `cmd_telemetry.py` | implemented, actual configured collection/refresh |
| `telemetry off` | 0..0 | `cmd_telemetry.py` | implemented, actual collection/cache stop |
| `telemetry sensors` | 0..0 | `cmd_telemetry.py` | partial, active native information/fixed-location/runtime sensors; hardware explicitly unsupported |
| `voice status` | 0..0 | `cmd_voice.py` | primary placeholder; native functional extension |
| `voice announce` | 0..0 | `cmd_voice.py` | primary placeholder; native functional extension |
| `voice dial` | 1..2 | `cmd_voice.py` | primary placeholder; native identity plus optional defined profile |
| `voice hangup` | 0..0 | `cmd_voice.py` | primary placeholder; native functional extension |
| `voice answer` | 0..0 | `cmd_voice.py` | primary placeholder; native functional extension |
| `voice log` | 0..0 | `cmd_voice.py` | primary placeholder; native functional extension |
| `voice mute mic` | 0..0 | `cmd_voice.py` | primary placeholder; native functional extension |
| `voice mute spk` | 0..0 | `cmd_voice.py` | primary placeholder; native functional extension |
| `voice unmute mic` | 0..0 | `cmd_voice.py` | primary placeholder; native functional extension |
| `voice unmute spk` | 0..0 | `cmd_voice.py` | primary placeholder; native functional extension |
| `voice profile` | 1..1 | `cmd_voice.py` | primary placeholder; native functional extension |
| `voice mode` | 1..1 | `cmd_voice.py` | primary placeholder; native functional extension |
| `plugins` | 0..0 | `cmd_plugins.py` | unsupported Python plugin ABI; visible error |
| `plugins reload` | 0..0 | `cmd_plugins.py` | unsupported Python plugin ABI; visible error |

Additional native commands are `exit`, `cancel`, `voice enable`,
`voice disable`, `voice reject`, `voice log clear`, `voice ptt on` and
`voice ptt off`; all take zero extra arguments. Existing native voice
behavior is retained, with a two-minute dialing deadline.

### Active configuration and intentional boundaries

`config list` lists only native-active settings, with their current values and
types. Unsupported aliases fail before mutation, including plugin/MQTT/mobile/
hardware/connectivity templates, archive-only values and the primary
`voice output` alias (the actual supported native setting is separately
named `voice_speaker` in settings exchange). There is no success-shaped
placeholder or Python plugin ABI claim.

Active aliases:

* `general`: `display_name`, `dark_ui`, `start_announce`, `lxm_limit_1mb`.
* `lxmf`: `propagation_node`, `propagation_by_default`, `periodic_sync`,
  `sync_interval`, `try_propagation_on_fail`, `ignore_unknown`,
  `require_stamps`, `ignore_invalid_stamps`, `inbound_stamp_cost`, `sync_max`,
  and nullable `sync_limit`.
* `voice`: `enabled`, `trusted_only`, `input`, `ringer`.
* `telemetry`: `enabled`, `send_to_trusted`, `send_to_collector`,
  `request_from_collector`, `collector_enabled`, `send_all_to_collector`,
  `allow_requests_from_anyone`, `allow_requests_from_trusted`,
  `receive_trusted_only`, `display_trusted_only`, `send_appearance`,
  `use_propagation_only`, `try_propagation_on_fail`,
  `requests_only_send_latest`, `collector`, `send_interval`,
  `request_interval`, `icon`, `fg`, `bg`.
* `sensors`: `rns_transport`, `information`, `information_text`,
  `fixed_location`, `fixed_latlon`, `fixed_altitude`. These do not open devices.
* `map`: `history_limit`, `lat`, `lon`, `zoom`, `use_offline`, `layer`,
  `cluster`, `interfaces`, using the actual map view store. No external
  paths are followed and no online consent is granted. Keyed/custom providers
  require their separately supported explicit configuration, not bundled keys.

The primary CLI metadata incorrectly declares `lxmf sync_limit` as integer,
although the actual core/persisted contract is boolean-or-null. Executed
conversion vectors preserve that mismatch. Native `config set lxmf sync_limit
none` clears the optional limit; integer writes fail visibly instead of
archiving an integer as active behavior. The separately supported Preferences/
settings synchronization workflow activates boolean limits and maximum counts.
Primary `sync_max` metadata calls it retries, but the actual core uses it as
the manual maximum message count; native behavior follows the actual core.

Single/double quotes, empty quoted arguments and POSIX-style backslash grouping
match executed primary parser cases. Resolution tries the longest path first
(up to four tokens). Native rejects unbalanced quotes, trailing escapes,
control characters, more than256 tokens or more than8,192 input characters;
the primary instead falls back to whitespace splitting malformed quotes.
No shell expansion, command substitution or expression evaluation occurs.
Tabs group arguments as whitespace; standard line endings delimit commands.
Windows/Linux native stdin/stdout use UTF-8. Output removes remote terminal
controls/bidirectional overrides; only the explicit local `clear` command
emits a terminal control sequence. Names resolve case-insensitively; ambiguous
names require hashes instead of silently choosing a peer. Unlike the primary,
native resolution also accepts an untrusted conversation's bare saved name;
the primary resolves its displayed name including the appended hash.

Network sends/paper/announces/synchronization run as at most four owned tasks,
each bounded to two minutes, with visible queued/completed/error/cancelled
results. A second concurrent sync fails. `cancel` stops all console network
work; `conv sync cancel` stops that sync; quit/EOF cancels and joins all work
before core disposal. Quick foreground commands have a five-second budget,
including waiting behind a busy core lifecycle gate. Telemetry/command
admission uses the core's existing bounded workers, not an unowned console
task. Counts, tracking intervals/durations and rendering are bounded; rendered
telemetry is explicitly truncated at display budgets without changing stored
wire data. Queued/submitted is never reported as recipient delivery/execution.
There is no days-long service, exhaustive packet-loss or physical audio claim.

Pending voice dialing owns the exact public task returned to its caller.
Disposal and hangup cancel and join that task, rather than a completion signal
raised before the async method itself finished. Outgoing admission, incoming
authorization and busy reporting also retain the reservation until the exact
task finishes, preventing an old cancelled dial's cleanup from hanging up a
new call. Dial failures remain on the caller's task; joining does not rethrow
an already owned call failure. The retained Windows hangup negative control
fails its exact-task completion assertion before this correction. All16
lifecycle cases then pass, including16 fresh pending-dial/hangup/healthy-next-call
cycles and the separate16-service/four-concurrent-disposer regression.
Linux replay of this later correction is blocked by WSL filesystem I/O errors
before process startup; the preceding24-case lifecycle/ringer result on each
host does not validate it. Native acceptance remains distinct from those earlier
console candidates. The TCP voice fixture also binds port0 directly and uses
the actual bound endpoint, removing the observed Linux release/rebind
port-ownership race.

Conversation clear/delete are atomic for native conversation/message state;
delete also clears the peer's telemetry store. These are separate stores, not
a cross-store transaction; a concurrent newly arriving message can re-create
activity, as with the primary's separate operations. Content-addressed media
blobs are not removed by these commands. Unknown-conversation mutations fail
visibly rather than returning an upstream-shaped success for a no-op.
Explicitly saved conversation names take precedence over later delivery
announcements, matching eight observations from the pinned primary's actual
`raw_display_name` and `peer_display_name` methods. Announcement-only and
unnamed conversations continue following announcements. The nullable persisted
`NameIsUserDefined` marker distinguishes these cases; legacy known conversations
with non-address names are conservatively treated as saved, while discovery-only
and address-placeholder names remain automatic. Explicit edits and supported
primary imports set the marker. Creating an unnamed conversation no longer
incorrectly saves its address as an explicit alias. Empty-string rename, also
available as the original desktop **Use announced name** control, atomically
clears the marker and resolves the bounded persisted delivery-announce cache
or falls back to the address. It preserves trust/unread and other peer policy.
Cache eviction and unusable names retain the native safety boundary: no
unbounded reference identity-cache lookup or name longer than80 characters.
The executed primary public `named_conversation` wrapper and actual SQLite
setter independently establish empty-string clearing, explicit replacement
and null rejection; null raises primary `AttributeError`, while the typed
native API rejects it with `ArgumentNullException`. The private SQLite read/
connection boundary is injected, not the primary constructor or full database.
Generator output SHA-256:
`4c4365c4235573f593a7bba55f9c25211bca75b6eaf544ec56a95a803ed025d5`.
T319–320 and T369–372 verify reference outputs, restart persistence, rejected/
cancelled write immutability, automatic creation, console and desktop controls.
The strengthened native-pair case retains default startup
announcements and requires an actual changed announcement before checking the
saved alias; current-source native acceptance remains separate from the older
candidates below.

Private SQLite snapshots now use canonical extended Windows local/UNC paths,
the native `win32-longpath` VFS and correctly escaped immutable map URIs, including
spaces, Unicode, `#` and `%`. The actual Linux SQLite3.46.1 VFS has a512-byte
pathname budget;600-character cases explicitly skip there, while300-character
snapshot paths run on both hosts and600-character managed cases run on Windows.
Source profile/map files remain unchanged. T321 exercises actual native profile
and map maintenance plus imported-identity restart. The preceding Linux
candidate passes. Its retained Windows failure exposed the stack's raw Win32
atomic-publication limit, not a SQLite failure. The selected e52 stack path fix
and fresh Windows strict NativeAOT replay now pass T321 without shortening paths
or changing the assertions. The Linux current-source/pin replay is blocked by
WSL storage failure.

`net propagation set none` clears the configured node but retains the existing
core's automatic/last-node selection; its output says so. The primary's
`set_active_propagation_node(None)` also does not clear an already active node,
despite its CLI response saying “cleared”.

`log [0..7]` now controls available numeric runtime reports through the existing
`ReticulumNode.Open(config, Action<int,string>)` callback, without a stack
change. The prior application adapter discarded the callback's level; the
stack did not. The runtime-only threshold is2 on a fresh client; daemon
interactive quiet mode uses0 and explicit verbose mode uses6. These follow
the inspected primary startup and four executed `_reticulum_log_debug`
quiet/verbose observations, not the generic RNS module's initial3. The level
is not persisted by configuration reload and filters with the actual RNS numeric
comparison. Fifteen actual primary `cmd_system._cmd_log` observations and72
actual pinned `RNS.log` stdout observations cover labels, accepted/rejected
arguments and all0..7 thresholds against0..8 source levels.

The primary CLI labels are retained verbatim but do not match RNS constant
names: RNS0 is critical, not squelch; RNS5 is verbose, and extreme is8.
Consequently, as in the actual primary, CLI0 still admits critical reports
and CLI7 does not admit RNS8. No severity offset is invented. Legacy string-
only stack reports are forwarded as5 by the existing host: that classification
does not recover their original severity or exception objects. Independent
stack `Trace` diagnostics are not all routed through this callback.

`log stream` displays the filtered, terminal-sanitized runtime reports and
voice events until Enter, with commands still available. Runtime critical/
error reports retain the contextual error channel; application errors and
observer-failure diagnostics remain separate from this optional stream.
Failing runtime observers are isolated and reported; absent observers do not
disable core processing. Verbose daemon mode also shows available non-error
runtime reports outside streaming at the selected threshold. None of this is
a claim of complete Python/global logger capture. Current logging acceptance
is recorded separately from preceding native candidates below.

### Console evidence and replay

`SidebandConsoleSyntaxTests`, `SidebandConsoleApplicationTests` and
`SidebandDaemonTests` cover the primary registration/parser vectors, Unicode,
quotes/escapes/control/huge arguments, active versus unsupported configuration,
map persistence, trust/name/history mutations, no-subscriber operation,
bounded cancellation, failed/corrupt reload and continued service.
`SidebandConsoleLiveTests` executes the actual primary CLI registry/handlers
over an actual primary Sideband core, not the recording boundary double:
messages pass both directions directly, through a Python transport and through
a .NET transport, including identity-preserving native-core restart. A separate
Python transport/propagation node verifies both propagation directions and
explicit native retrieval limits. No reference source or callback is replaced.

`SidebandConsole.Tests.ps1` exercises selected strict Windows/Linux NativeAOT
processes: offline conversation/configuration/telemetry/maps and persistence,
malformed/unsupported/huge input plus pending-send cancellation/EOF, native
messages both ways after clean/abrupt restart, and actual primary CLI/core
messages both ways after clean/abrupt native restart. The native pair separately
exchanges at least five Opus64 frames in each direction using synthetic capture
and discard output. That is software/audio-codec evidence, not physical audio.
All network configurations are isolated, with shared instances disabled.
The primary observation adapter explicitly disables automatic startup announces
and uses actual manual announces. Its propagation/transport peer uses the
shared harness settings: propagation stamp cost13, delivery limit1,000KB,
propagation message limit256KB and propagation synchronization limit10,240KB.
These are reference-node test parameters, not a claim of unchanged defaults.
Live commands use the actual pinned imported primary source, RNS1.5.7,
LXMF1.2.1 and LXST0.5.4. The thin JSON/base64 process envelope is harness-only.

Focused commands:

```powershell
dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --filter "FullyQualifiedName~SidebandConsole|FullyQualifiedName~SidebandDaemonTests"
dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release
# Separate explicit live gate:
# DOTRETICULUM_LIVE=1; DOTRETICULUM_SIDEBAND_LIVE=1
# DOTRETICULUM_PYTHON, DOTRETICULUM_UPSTREAM,
# DOTRETICULUM_LXST_UPSTREAM, DOTRETICULUM_SIDEBAND_UPSTREAM point to actual pins.
dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --filter "FullyQualifiedName~SidebandConsoleLiveTests"
```

Native Pester additionally requires `DOTRETICULUM_SIDEBAND_CONSOLE=1`,
`DOTRETICULUM_SIDEBAND_BINARY`, its exact
`DOTRETICULUM_SIDEBAND_CONSOLE_SHA256` and optional
`DOTRETICULUM_SIDEBAND_CONSOLE_RESULTS`. Missing native/live opt-ins explicitly
skip rather than passing. Strict publishing uses both trim/ILC warnings as
errors and executes the resulting binary's help and feature flows.
Frozen candidate manifests, PDB/source/generated-source checks, actual
compiler/runtime/ILC response receipts, failures and no-republish replay
operators are retained separately under ignored
`artifacts/tests/sideband-console`. Independent parent replay accepted the older
Windows full/focused/live/native candidate and the Linux focused/live/native
candidate. The Linux full replay failed the heavy-profile window-readiness wait;
that failure remains retained. Its test now awaits the window's actual owned
initialization task before asserting readiness and the unchanged save/close
contract. Current-source integrated/native acceptance is separate, and neither
candidate acceptance nor the readiness correction establishes overall application
completion.

Retained older console candidate runs: focused selectors **25 passed /4 explicit live
skips** on each host; separately enabled live selectors **4 passed /0 skipped**
on each host; full Sideband managed suite **2,060 passed /74 skipped** on Windows
and **2,062 passed /72 skipped** on Linux; native Pester **4 passed /0 skipped**
on each host. Full-suite skip receipts retain their individual opt-in/platform
reasons. Both strict NativeAOT publishes pass with no warnings, and repeated
publishes preserve the selected native digest. Earlier failed live/name-lookup,
Unicode and native observer-race receipts remain retained; the final observers
assert the actual message content rather than dropping output between polls.
Windows primary LXST reports its own normal Python filter fallback during CLI
vector import; CLI vectors do not exercise physical audio or filter acceleration.

The preceding guarded `sqlite-final` source repeats the full managed suite on both
hosts: **2069 passed /74 explicit skips** each; separately enabled live selectors
pass **4/0** each. Linux strict NativeAOT/help and all **five native cases pass**,
including T321 source-preserving profile/map maintenance and identity restart.
Selected Linux SHA-256:
`cdb9019e9b46e021f6c2e129e7af6966c48b87ccf50f83db50b7dcdc62a25da4`.
The guarded receipt and per-mode logs are at
`/home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/tests/sideband-console/linux-sqlite-final`.
Windows's four previous native flows pass, but T321 fails transport-identity
atomic publication at the long imported path. Retained
`artifacts\tests\sideband-console\windows-sqlite-final\Native.log` is a failure,
not full native acceptance. This preceding run includes saved-name/default-startup assertions, exact owned
pending-dial completion, actual bound-port voice fixture and SQLite path fixes;
it does not establish missing configuration/plugin/hardware contracts.

The selected Windows `atomic-hangup-final` candidate includes the exact public
hangup-task correction and committed e52 stack path fix. Full Sideband managed
acceptance passes2070/74 explicit skips; separately enabled actual-primary live
cases pass4/0. Strict NativeAOT/help and all five native cases pass, including
the unchanged long-path profile/map and imported-identity restart assertions.
The source-before/after guard remains unchanged across every mode. Selected
native SHA-256:
`475dded1fd88aab15f0c4ad7d1a05950c9d8b4b4a05bdbded3753e6603b5d200`.
The receipt, commands and per-mode logs are under
`artifacts\tests\sideband-console\windows-atomic-hangup-final`.

```powershell
& artifacts\tests\sideband-console\Parent-CurrentConsole.ps1 -Managed -Run atomic-hangup-final
```

Current Linux replay cannot start because its WSL filesystem reports storage
I/O errors and is mounted emergency read-only. No current-source/pin Linux pass
is inferred from the preceding candidates. No shared distribution restart or
filesystem repair was performed.

The preceding `automatic-names-final` Windows candidate also fixes unnamed
creation and alias clearing. Fresh managed acceptance is2074/74 explicit
skips, actual-primary live4/0, and strict NativeAOT/help/native5/0. The native
pair requires empty-name creation, saved alias precedence over an actual
changed announcement, empty-string clearing, and a later actual announcement
after identity-preserving restart, without losing trust. All production
source hashes remain unchanged through the four gates. Selected native
SHA-256:
`d4d862a6dd4680ece5765b92f5b325cfc87c5e3c87e296c9fdcb5bc0e290190c`.

```powershell
& artifacts\tests\sideband-console\Parent-CurrentConsole.ps1 -Managed -Run automatic-names-final
```

Receipts/logs are under
`artifacts\tests\sideband-console\windows-automatic-names-final`.
The preceding candidate does not include this name-policy correction; Linux
acceptance of it remains blocked by the same unavailable WSL filesystem.

The current guarded `runtime-log-startup-final` Windows candidate passes full
managed2080/74 explicit skips, actual-primary live4/0 and strict
NativeAOT/help/native5/0. Selected native SHA-256:
`4407fd0ba2871b1547d75832768fa5da6e66e51a590f7dab0326509dfc70fa37`.
Production source guards remain unchanged throughout every mode. Reproduce:

```powershell
& artifacts\tests\sideband-console\Parent-CurrentConsole.ps1 -Managed -Run runtime-log-startup-final
```

Receipts/logs are under
`artifacts\tests\sideband-console\windows-runtime-log-startup-final`.
The generated CLI fixture SHA-256 is
`aea4430e8736eaa54c605b16c0b3499245819795c2fd30433fe3d9e6b1eb28fd`.
Its actual imported core helper observes quiet/verbose selection on a minimal
owner; these four rows are not full application startup or audio evidence.
The72 numeric-filter rows execute pinned RNS stdout logging, source SHA-256
`217d8cd060844475acb692f869c9eb1e051ce5b800d3f23479d562e37f048f33`.
Upstream filterlib compilation/fallback warnings during actual core import
remain visible; no reference behavior shim is used for these log observations.

T373–T377 cover command results, numeric filtering, failing/concurrent observers,
terminal sanitization, Enter/disposal detachment, actual owned-node management
errors with and without subscribers, and real daemon quiet/verbose startup.
Healthy TCP startup and connection refusal did not emit runtime callbacks in
the initial attempts; they are not claimed as logging evidence. The deterministic
real stimulus is a missing-interface management attachment on the same owned
node, which reports numeric error1. The published native stream separately
observes actual routing reports at host-forwarded level5 while the native pair
continues messaging and synthetic real-Opus calling. It is not a voice-event
proxy or proof of complete stack logger capture.

The retained earlier `runtime-log-final` native4/1 failure waited for a malformed
input error on stdout, but errors use stderr. The final gate asserts stderr
after orderly exit, unchanged threshold and continued service, without weakening
the rejection. The superseded `runtime-log-accepted` run was stopped before the
startup-default correction and is not acceptance. Linux current-source replay
remains blocked by WSL storage EIO.

## Saved-message details and local deletion

The original Conversations screen exposes selected saved message IDs, peer,
direction, title, UTC-offset timestamp, delivery state/method and plain text in
a read-only selectable field. These are local saved metadata, not an assertion
that a historical sender/signature was authenticated. Changing selection clears
the deletion confirmation. Explicit confirmation removes exactly the selected
peer/local-ID entry atomically and persists across restart, retaining contacts,
trust/unread policies, unrelated messages and content-addressed media files.
It does not recall a message, cancel an in-flight router operation or erase
shared attachment contents. Later delivery-state callbacks do not recreate a
removed entry. Media-store garbage collection remains separate work.

`GenerateConversationPolicyVectors.py` executes actual primary
`delete_message` and `_db_delete_message` at the pinned core revision/hash above
on real private SQLite with a disclosed minimal owner/connection. Four sequential
observations cover incoming/outbound deletion, an unknown hash and a repeat,
while another peer's identical text remains. The reference returns no error for
missing hashes; native ownership validation explicitly rejects an absent
peer/local-ID match rather than reporting false deletion. This is a deliberate
error-reporting difference, not a wire change. The regenerated fixture SHA-256
is `cdd1ddcb255b824421853961ace6139137403a09fc6551addff110ad06f33c6c`.

T378–T380 cover those observations, unchanged rejected/cancelled writes,
concurrent/late state callbacks, no-subscriber operation, continued service,
selection/confirmation and headless desktop restart. T381 selects an exact
published binary and real desktop to check rendering, two successive confirmed
deletions/restarts, stable identity/conversation policy and shared-file retention.
Without its native/desktop opt-in it explicitly skips. The destructive
`--smoke-ui --smoke-message-actions --data-dir <test-profile>` acceptance mode
removes one saved entry through the ordinary selected-message UI routine and
must only be used with synthetic test profiles. It cannot combine with network,
maintenance or other smoke modes. This is not new Python-peer, impaired-network,
full message-menu, rich-renderer, retry or platform acceptance.

Windows acceptance passes full Sideband2083/74 explicit skips, actual-primary
console live4/0, strict NativeAOT/help, the five existing native console flows
and T381 real desktop1/0. Selected native SHA-256:
`d625f2ea5dcff60791ae912fa043c69e5fb8cabf7f5f2140f94c934c7e97cd0a`.
T381 independently repeats two confirmed deletions through normal UI routines,
window restarts, unchanged identity/conversation policy and shared attachment
bytes. Empty/malformed test profiles exit2 explicitly; invalid combinations
leave state unchanged. Production and binary guards match before/after:

```powershell
& artifacts\tests\sideband-console\Parent-CurrentConsole.ps1 -Managed -Run message-actions-corrected
& artifacts\tests\sideband-message-actions\Replay-SelectedNative.ps1
```

Receipts are under
`artifacts\tests\sideband-console\windows-message-actions-corrected` and
`artifacts\tests\sideband-message-actions\parent-final`. The initial T381 run
failed because the operator wrote lowercase keys into the case-sensitive native
JSON schema. The diagnostic profile/output remain under `native-diagnostic`;
the operator now writes actual PascalCase keys, without relaxing validation.
The new smoke mode also now terminates visibly if profile initialization fails,
and compares shared attachment metadata structurally rather than by list-object
identity. The earlier candidate's successful console gate alone is not acceptance
of these corrections. Current Linux desktop/source replay remains blocked by
WSL storage EIO.

## Selected failed-message text retry

The original Conversations screen provides an explicit, selection-bound
confirmation for retrying an outgoing failed non-paper message's text.
`conv retry-text <peer-or-name> <saved-uuid>` exposes the same operation as a
native console extension, not an invented primary CLI registration. It is an
owned cancellable console operation. Retry creates a fresh LXMF message with
current direct/propagated, telemetry and reply-ticket policies; it does not
replay the saved title, attachments, image, audio, timestamp or LXMF hash.
The UI refuses to overwrite an existing text/media draft or silently reattach
its content. Copy/cancel and complete rich-message/menu presentation remain
separate required contracts.

At primary `d069122af14880e43607aed0b71add4c7806fbe0`,
`sbapp/ui/messages.py:895-906` copies UTF-8 content into the current composer,
deletes the old entry, invokes ordinary send and then dismisses/reloads the
menu. Its menu offers retry for `FAILED`, not for ordinary pending/delivered
messages or paper. `GenerateMessageRetryVectors.py` executes that exact
AST-extracted factory and actual core deletion methods on private SQLite.
Synthetic composer/dispatcher/menu and immediate Clock boundaries are disclosed;
this is not a primary Kivy GUI or live retry invocation. Source SHA-256 values
are core `8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25`
and messages `9b2580f76310356d2e1a30b038857fbad4ee346aad9e2a25ab237515fff39928`;
generated fixture SHA-256 is
`5fac8b943eb270f088433faa68ad19a96ffd605bb379be598205448a75e56de2`.
Four observations include Unicode, empty content, dispatcher failure and
invalid UTF-8. A dispatcher failure leaves the primary entry deleted.

Native validation/path failures deliberately retain the original failure.
After identity resolution and message construction, one atomic state write
replaces the still-matching failed peer/ID/content with the new queued entry,
before queue submission. Submission failure/cancellation records the replacement
as failed/cancelled through the existing state worker. This avoids an empty
history window; it is not an exactly-once network transaction or crash-replay
guarantee. Late old callbacks cannot resurrect the replaced entry. Shared media
bytes remain untouched. Empty/whitespace/oversized text, incoming or absent IDs,
paper and nonfailed state are refused explicitly. These bounded safety
differences do not assert identical primary empty-message or draft behavior.

At most eight retry requests may be active/waiting. Admission fails immediately
at capacity; runtime cancellation, disconnect and disposal cancel owned waits,
and lifecycle serialization prevents concurrent retries of one failed ID from
both committing. T412-T416 cover executed observations, malformed/cancelled
and disconnected requests, atomic conflict refusal, late state updates,
capacity/recovery, actual native TCP delivery, identity/history restart and
the connected headless desktop confirmation/draft workflow. T417/T418 select an
exact published executable for actual primary application reception, return
messages, repeat rejection and identity-preserving native restart. T419
independently regenerates the actual-primary fixture. Native/live modes skip
explicitly without their opt-ins. New hop/loss, primary GUI, Linux replay and
hardware acceptance are not implied.

Windows full Sideband2088/75 explicit skips passes with `-warnaserror`.
Separately enabled existing primary console4/0, strict NativeAOT/help and
native console5/0 pass. The same source-guarded published executable passes
T417 actual-primary retry/reply/restart, T419 reference regeneration and the
unchanged T381 real desktop deletion regression:3/0 Pester, including1/0 nested
native xUnit. Selected SHA-256:
`c5e9c9a55f181468ef4cdac8d94b69dcc6854ba4456566c154ef6873f83324e5`.

```powershell
& artifacts\tests\sideband-console\Parent-CurrentConsole.ps1 -Managed -Run message-retry-peer-scope
& artifacts\tests\sideband-message-retry\Accept-SelectedNative.ps1 -CandidateRun message-retry-peer-scope -Run peer-scoped-final
```

Final receipts are under `windows-message-retry-peer-scope` and
`artifacts\tests\sideband-message-retry\peer-scoped-final`.
An added exact-ID/different-peer test verifies replacement cannot remove the
other peer's history. The preceding accepted
`749eb8cffb0dd57b9ade3676cdd1a253c22910cf0d9fbc8f4817553a03f39a1b`
candidate does not establish that final peer-isolation correction.
The retained first native return-flow timeout and diagnostic failure occurred
before the test explicitly announced/observed the native delivery destination;
the actual primary refused to originate a reply without those announced keys.
The corrected operator observes that announce, checks primary command results
as well as error fields and preserves the original message-delivery assertions.
Another retained test setup attempted forbidden propagated mode without a
selected/discovered node. The final failure-path case instead selects a real
delivery-only destination, asserts its absent PN-limit refusal, then verifies a
current-policy direct retry. No configuration guard or delivery assertion was
relaxed. Current Linux replay remains blocked by WSL storage EIO.
