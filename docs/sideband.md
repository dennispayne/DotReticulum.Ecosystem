# Sideband source and scope inventory

## Status

**Initial Windows/Linux desktop client implemented; upstream application parity
is not claimed.** The current native C# application provides a small contacts
and text-conversation workflow over the repository's .NET Reticulum and LXMF
libraries, with bounded local JSON persistence. Windows NativeAOT publishing,
CLI maintenance modes, a headless UI workflow, and native Pester acceptance
have been exercised. Live direct-peer interoperability against the pinned
Python RNS/LXMF node has passed in both messaging directions over a real
Reticulum TCP interface. Attachments, voice, telemetry,
plugins, mobile operation, broad upstream configuration, and upstream database
migration remain absent.

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
| LXMF messaging and propagation UI | partial | C# client wires the app to `LxmRouter` for text send/receive and delivery state; direct-peer interoperability passed both ways against pinned Python LXMF. Propagation-node selection, inbox controls, full peer policy and restart synchronization are not app features; `src/Sideband/SidebandClient.cs`, `tests/Sideband.Tests/SidebandLiveInteropTests.cs`, `sbapp/sideband/core.py`, `docs/lxmf.md`. |
| Text, image/file and audio messages | partial | Text composition, receipt and durable conversation history are implemented. The current UI does not send files; non-text incoming content is represented as a placeholder. Upstream image/file/audio composition and playback remain absent; `src/Sideband.Desktop/MainWindow.axaml.cs`, `src/Sideband/SidebandClient.cs`, `README.md`, `sbapp/ui/messages.py`, `sbapp/sideband/audioproc.py`. |
| Paper messages and `lxm://` imports | missing | No paper-message/QR creation or import flow, identity import, camera/QR, export or URI handling; `README.md`, `sbapp/main.py`, `sbapp/ui/keys.py`. |
| LXST calls and push-to-talk | missing | A disabled UI button explicitly reports voice unavailability. No audio devices, call authorization, duplex modes or call history are implemented. Sideband 2.2.0 requires `lxst>=0.5.4`, while this repository targets LXST 0.4.5; `src/Sideband.Desktop/MainWindow.axaml`, `sbapp/sideband/voice.py`, `sbapp/ui/voice.py`, `docs/lxst.md`. |
| Telemetry, location and situational awareness | missing | Sensor streams include time, identity/information, battery, location, physical link, environment, motion, resource, Reticulum transport, LXMF propagation, connections and interfaces; peer telemetry, tracking, live/offline maps and geospatial calculations are app behavior; `sbapp/sideband/sense.py`, `sbapp/sideband/geo.py`, `sbapp/ui/telemetry.py`, `README.md`. |
| Remote commands, queries and plugins | missing | Command, service and telemetry plugin base types; built-in ping/echo/signal/telemetry request behavior; custom telemetry sensors; configurable plugin loading and example plugins. This is an application extension contract, not part of the existing .NET `Telemetry` tool; `sbapp/sideband/plugins.py`, `sbapp/sideband/core.py`, `docs/example_plugins/*`, `README.md`. |
| Reticulum setup and interfaces | partial | The user explicitly connects through `ReticulumNode.Open` using a saved/default config or `--rnsconfig`; the Connectivity page displays actual shared-role and effective config file only after the node starts, and gives `rnstatus` guidance. Offline, it reports that ownership/health is unknown. It never rewrites the global config or checks whether an `rnsd` executable happens to be installed. Shared-instance election is inherited from DotReticulum; compatible reachable local owners may be reused, otherwise the stack may fall back to ownership. Interface editing/discovery and Android interface loading are absent; `src/Sideband/SidebandClient.cs`, `src/Sideband.Desktop/MainWindow.axaml`, `external/DotReticulum/src/DotReticulum.Applications/ReticulumNode.cs`, `sbapp/sideband/core.py`, `sbapp/ui/hardware.py`, `sbapp/buildozer.spec`. |
| Long-lived service and OS integration | missing | The desktop app owns a live client while its window runs. Android foreground services, notifications, location/permission APIs, wake/multicast locks, Bluetooth/RNode discovery and background-power behavior are not implemented or validated; `sbapp/services/sidebandservice.py`, `sbapp/main.py`, `sbapp/sideband/core.py`. |
| Persistence, restart and migration | partial | Contacts, local delivery hash, Reticulum config path, display name, theme, and bounded chat history are stored in source-generated JSON with atomic replacement. A v1 state file without new optional preference fields loads with defaults. The default native Windows data directory is `%APPDATA%\sideband`; upstream Windows uses `%USERPROFILE%\.config\sideband\app_storage`, so app data/identity stores do not collide. The apps can still select the same user Reticulum config; tests use owned isolated configs and never reset/import global state. There is no import/migration of upstream SQLite, telemetry/announce schemas, export/audio state or full LXMF recovery contract; `src/Sideband/SidebandStateStore.cs`, `src/Sideband/SidebandStateJsonContext.cs`, `tests/Sideband.Tests/SidebandStateStoreTests.cs`, `sbapp/sideband/core.py`, `sbapp/sideband/voice.py`. |
| CLI and headless operation | partial | Native `sideband --help`, `--version`, `--init`, `--check`, `--data-dir` and `--rnsconfig` are implemented and covered by Pester. `--rnsconfig` also selects the UI's Reticulum config at startup. Upstream verbose, daemon, interactive and settings import/export options are absent; `src/Sideband.Desktop/Program.cs`, `tests/Pester/Sideband.Tests.ps1`, `setup.py`, `sbapp/main.py`, `sbapp/sideband/console.py`. |
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
its declared minimum. LXST 0.4.5 is older than Sideband's 0.5.4 minimum, so
voice remains disabled rather than implying compatibility.

The app project is `src/Sideband.Desktop/Sideband.Desktop.csproj` (assembly and
executable name `sideband`); its protocol/application library is `src/Sideband`
(assembly `Sideband.Core`, namespace `Sideband`). The library uses source-
generated JSON rather than reflection serialization. Native plugins, upstream
SQLite migration, mobile services and platform-specific sensors/audio are not
implemented. The current native client is an initial desktop app, not an
upstream replacement.

## Current implementation evidence

The implementation targets the upstream 2.2.0 release tag
`cba73130287f0d8b9deb7da4d20ceec09b7fe394` (the primary default branch inspected
for the inventory remains three commits beyond that release tag). The version
label is a target, not a parity claim. The upstream source is not committed.

Validated commands and outcomes:

| Command | Result |
|---|---|
| `dotnet build src\Sideband\Sideband.csproj -c Release --artifacts-path artifacts\sideband-build` | Passed, zero warnings/errors |
| `dotnet build src\Sideband.Desktop\Sideband.Desktop.csproj -c Release --artifacts-path artifacts\sideband-build` | Passed, zero warnings/errors |
| `dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --artifacts-path artifacts\sideband-build --filter "Category=Unit"` | Passed: 6 unit tests |
| `dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --artifacts-path artifacts\sideband-build` | Passed: unit and headless UI tests; live test is explicitly skipped unless opted in |
| `dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --artifacts-path artifacts\sideband-build` with pinned reference variables and `DOTRETICULUM_LIVE=1` | Passed: 11 tests, including state migration/defaults, UI navigation/preferences/error display, isolated node connect/disconnect, close-time awaiting of startup and pending preferences writes, and live interop; no skips |
| `dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj -c Release -r win-x64 --artifacts-path artifacts\sideband-build -p:RuntimeIdentifiers=win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true` | Passed; strict Windows NativeAOT |
| `wsl bash -lc 'cd /mnt/c/Git/dennispayne/DotReticulum.Ecosystem && /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/nomadnet-linux/sdk/dotnet publish src/Sideband.Desktop/Sideband.Desktop.csproj -c Release -r linux-x64 --artifacts-path artifacts/sideband-linux-build -p:RuntimeIdentifiers=linux-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true'` | Passed; strict Linux NativeAOT |
| Published `sideband.exe --help` and `--version` | Passed |
| Published Linux `sideband --help`, `--version`, `--init`, and `--check` | Passed; created and reloaded the same persisted identity |
| `Invoke-Pester tests\Pester\Sideband.Tests.ps1 -Tag Native,GUI -Output Detailed` with `DOTRETICULUM_SIDEBAND_BINARY` set to the Windows publish | Passed: native CLI/init/check plus actual native Avalonia window and rendered send-validation flow |
| Linux Pester 5.7.1 `Invoke-Pester tests/Pester/Sideband.Tests.ps1 -Tag Native,GUI -Output Detailed` with `DOTRETICULUM_SIDEBAND_BINARY` set to the Linux publish, under WSLg | Passed: native CLI/init/check plus actual window creation, render callback, and rendered send-validation flow |
| `LIBGL_ALWAYS_SOFTWARE=1 DISPLAY=:0 timeout 30s artifacts/sideband-linux-build/publish/Sideband.Desktop/release_linux-x64/sideband --smoke-ui --data-dir artifacts/sideband-linux-gui/data --rnsconfig artifacts/sideband-linux-gui/rns` under WSLg | Passed: observed `SMOKE_UI_RENDERED_INITIAL=1`, native send validation status, and `SMOKE_UI_RENDERED_STATUS=Select a contact before sending.`; no physical audio claim |

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
the expected content digest. Multi-hop routing, propagation, loss/retry and
other peer topologies are not covered by this app-level test. No Android,
macOS, audio-device, voice, telemetry or plugin behavior is claimed.

The native state store persists contacts, chat messages, display name, theme,
and the local configuration/delivery address in `state.json`; it is not wire-
or schema-compatible with upstream's SQLite database. On Windows this lives in
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
non-text content is a placeholder, not an attachment implementation.
