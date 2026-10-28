# NomadNet and Sideband inventory

## Present state

This is the present-state source, licensing, platform, and architecture
reference for NomadNet and Sideband. **NomadNet has a scoped native .NET
terminal client; it is not a full port or a parity implementation.** Sideband
has no .NET implementation in this repository. Existing `LXMF`, `LXST`, and
DotReticulum libraries are building blocks, not evidence that an application
contract is implemented.

The intended application names are **NomadNet** and **Sideband** (without a
`Dot` prefix). NomadNet's first implementation is a simple command loop with
no third-party terminal UI toolkit. It targets Windows and Linux; platform
and NativeAOT claims are limited to validation recorded below.

## Primary-source provenance

The project READMEs identify the GitHub repositories as public mirrors and say
development happens elsewhere. The Reticulum community's rngit locator index
lists these primary repositories:

- NomadNet: `rns://7649a50d84610232d1416b41d2896aff/reticulum/nomadnet`
- Sideband: `rns://7649a50d84610232d1416b41d2896aff/reticulum/sideband`

Both repositories were cloned directly over Reticulum with the native .NET
`rngit` in the pinned DotReticulum checkout (`3cdf5bee5adbd1da903a163882a31e53c3057aec`).
The configured TCP bootstrap was used as the transport path; it routed to the
repository node over multiple Reticulum hops. The isolated client configuration
and cloned repositories are ignored artifacts under `artifacts\nomadnet`; no
private key, network traffic, or upstream source is tracked or committed.
GitHub mirror README/license/package metadata was consulted during locator
discovery and then checked against the primary clones; **neither clone was
seeded from GitHub**.

The executed fetches were `rngit clone` of each locator into a new bare
repository (Sideband used `--single-branch`), followed by strict Git fsck.
The transient isolated gateway configuration was removed after retrieval.

| Repository | Primary source revision | Root tree | Retrieved ref scope |
|---|---|---|---|
| NomadNet | `f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2` | `223037b20cb53ec1d9e73ba9e4a66886de459ebf` | Default branch plus advertised refs; `rngit` reported 96 refs and 6,727,500 Git-bundle bytes |
| Sideband | `d069122af14880e43607aed0b71add4c7806fbe0` | `b1779ea9fc35ca22abbb1de5f83a9abe4242c33c` | Default `master` branch only (`--single-branch`); other branches and tags were not fetched |

Both clones passed `git fsck --full --strict`; their worktrees were clean.
These checks establish Git-object consistency, not publisher authentication or
release-signature verification. NomadNet's source declares version **1.4.4**
(`nomadnet/_version.py`), with a 1.4.4 tag present in the fetched refs. Sideband
declares version **2.2.0** (`sbapp/main.py`); that version is from the fetched
primary branch, not an independently verified release artifact or fetched tag.

SHA-256 digests below are calculated from the fetched Git blob bytes, so they
are independent of Windows worktree line-ending conversion.

| Primary source file | SHA-256 |
|---|---|
| NomadNet `LICENSE` | `3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986` |
| NomadNet `setup.py` | `0378491e83e0504ce5f066520782942e81db975e47e95c033595a325256d49a2` |
| NomadNet `nomadnet/_version.py` | `eacb73fd814144ffe582ec9d57296c4e04a68f75536fa585abc4a9e395984326` |
| NomadNet `nomadnet/NomadNetworkApp.py` | `c36ce4c537fe5af534910d1f5200d2a2ea5dd50f74bf9e4157222eff15614eb2` |
| NomadNet `nomadnet/Node.py` | `1e2ba96f6dc040fcbe19e55bd2eb33961d9fb9443f7e590e02aeb32894aa695c` |
| NomadNet `nomadnet/Conversation.py` | `83bbfc71a7fc2b0953a2aac283a609c068dbbe38daea3abc788e4266ca4a98cd` |
| NomadNet `nomadnet/RRC.py` | `195e3dd62ab6678f5192b8b9c5a3c3e399e519018f3fb2216a16315c32895925` |
| Sideband `LICENSE` | `1349a4b6148492b44f629e64eed676612e234fe9a839e4f3b277c1482c8849f1` |
| Sideband `setup.py` | `9905d709976990b1a0b4478c4d5ab51ce2b5f08da9ea4693eb6398dda479069e` |
| Sideband `sbapp/main.py` | `5b962d9ed2ccbc066bb19865d78c77da9ecf4008900f6f0e7e9dd1eb1c4767aa` |
| Sideband `sbapp/sideband/core.py` | `8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25` |
| Sideband `sbapp/sideband/plugins.py` | `47a051a26a21d53aa8d6232cfc438dfac5e9396814e4bbc6c0149fb74643b6ac` |

## Licensing boundary

The NomadNet primary tree contains the GNU GPL version 3 license, and its
README warns that the project was published under GPLv3. However, the same
revision's `setup.py` classifies the package as MIT. This is an upstream
metadata conflict, not a basis for choosing the more permissive interpretation.
Resolve it with the rights holder or qualified legal review before considering
any derivative distribution.

The Sideband primary tree's `LICENSE` is Creative Commons
Attribution-NonCommercial-ShareAlike 4.0. Its README describes a special
permission for binary distribution/use, but that does not establish permission
to adapt or redistribute its source or assets under this repository's license.
The Reticulum License does not automatically replace either application's
license. **No upstream code, translated code, images, sounds, or other assets
were copied into the ecosystem.** Sideband also bundles third-party components
(including KivyMD and Plyer) with their own licenses.

## Upstream contracts and current ecosystem status

`missing` means the application behavior is not present in the .NET ecosystem;
using an underlying protocol library does not make the application contract
implemented. `upstream-placeholder` marks an advertised branch that the
inspected upstream source itself does not implement.

### NomadNet

| Contract | Upstream facts and source references | Ecosystem status |
|---|---|---|
| Executable and lifecycle | Native `nomadnet` executable/assembly; upstream options are `--config`, `--rnsconfig`, `--daemon`, `--console`, `--textui`, `--version`; interactive command loop and Ctrl+C cancellation. Upstream entry point: `setup.py`, `nomadnet/nomadnet.py`. The scoped .NET CLI adds `--help`, `--init`, and `--status`; upstream daemon/console/text-UI modes are not implemented. | partial; Windows/Linux NativeAOT help/version/init/status and native-process tests pass; full upstream CLI modes are missing |
| Identity and delivery | Persistent Reticulum identity, `lxmf.delivery` destination, announce, known-destination listing, direct text send/receive, and delivery-state display via existing .NET `ReticulumNode` and `LxmRouter`. Upstream contracts: `NomadNetworkApp.py`, `Directory.py`, `Conversation.py`. | partial; bidirectional direct-message flows pass against pinned Python LXMF 1.2.1 and the primary NomadNet 1.4.4 app on a one-hop TCP loopback |
| Contacts, conversations, persistence | Display name settings, contacts, bounded durable text history and bookmarks are stored in the selected app data directory; state updates use versioned JSON and atomic replacement. | partial; local persistence has integration tests; attachment, paper message, propagation and upstream database/file formats are missing |
| Peer/node discovery | Displays up to 256 recently announced Reticulum destinations and a decoded display name when available. | partial; the current public known-destination snapshot cannot reliably classify every announce as `lxmf.delivery` because it does not retain destination app/aspect |
| Page browser | Requests `/page/index.mu` or a safe `/page/...` path over a Reticulum link; limits responses to 1 MB; follows textual Micron links and stores bookmarks. Upstream URL/link syntax: `ui/textui/Browser.py`, `MicronParser.py`. | partial; `/page/readme.mu` was live-fetched from primary NomadNet 1.4.4 on a one-hop TCP loopback; headings and bracket links render, while forms, images, dynamic partial refresh, full tables/styles, anchors and page cache are missing |
| File downloads | Requests an upstream `/file/...` resource with a 16 MiB response cap and saves a validated plain filename under `downloads`. Upstream source: `Node.py`, `ui/textui/FileBrowser.py`. | partial; a 20-byte file was live-downloaded from the primary NomadNet 1.4.4 node and byte-checked; larger files and failure/retry cases remain unverified |
| Hosted node | `nomadnetwork.node` announce; `/page`, `/file`, `/media`, `.allowed` authorization; optional executable page handlers and image conversion. `Node.py`. | missing |
| Live group chat | RRC room client and persisted room history. `RRC.py`, `ui/textui/Channels.py`. | missing |
| UI modes and platform | Upstream text/menu UI uses Urwid; `GraphicalUI` and `WebUI` explicitly abort as not implemented. `ui/__init__.py`, `TextUI.py`, `GraphicalUI.py`, `WebUI.py`. | partial: scoped command-loop UI only; upstream placeholders remain out of scope |
| Configuration and platform | Native app data is under `--config` or the platform application-data directory; Reticulum configuration is supplied with `--rnsconfig` or standard discovery. Upstream also stores identity, pages/files, directory, caches and resources. `NomadNetworkApp.py`, `setup.py`. | partial; Windows `win-x64` and WSL Ubuntu `linux-x64` NativeAOT publishes, CLI smoke, and native process flows pass; other operating systems and broader platform coverage are unverified |

### Sideband

| Contract | Upstream facts and source references | Ecosystem status |
|---|---|---|
| Executable and modes | `sideband=sbapp:main.run`; `-v/--verbose`, `-c/--config`, `-r/--rnsconfig`, `-d/--daemon`, `-i/--interactive`, `--export-settings`, `--import-settings`, `--version`. `sbapp/main.py`, `setup.py` | missing |
| Messaging and transport | LXMF conversations, announce/peer and propagation-node discovery, propagation synchronization, file/image/audio attachments, paper QR messages and `lxm://` input. `sbapp/sideband/core.py`, `voice.py`, `sense.py` | missing at application layer |
| Voice | LXST telephony, audio messages and device routing. `sbapp/sideband/voice.py`, `audioproc.py`, `setup.py` | missing at application layer |
| Telemetry and remote control | Location/situational awareness, telemetry collection and queries, offline/online maps, remote command/response and transport-instance controls. `core.py`, `sense.py`, `geo.py`, `mapview/`, `cli/` | missing |
| Extensions | Distinct telemetry, command and service plugin contracts; plugin lifecycle and registration. `plugins.py`, `core.py`, `docs/example_plugins/` | missing |
| UI/service architecture | Kivy/KivyMD GUI; a `SidebandCore` also supports daemon/service/client roles, including Android service integration and local IPC. `sbapp/main.py`, `core.py`, `services/sidebandservice.py` | missing; UI target requires confirmation |
| Configuration and persistence | Default desktop data directory `~/.config/sideband`; `app_storage/sideband_config` (MessagePack), `primary_identity`, SQLite `sideband.db`, LXMF storage, Reticulum config, cache, exports and telemetry state. Android uses its application-private storage. `core.py`, `main.py` | missing |
| Platform and dependencies | Upstream documents Android, Linux, macOS, Windows and Raspberry Pi packages/builds. Uses Kivy, NumPy, Pillow, map/UI dependencies, RNS/LXMF/LXST; voice/audio paths include Codec2/Opus, PyAudio/PortAudio and platform libraries. Android bridges use Android APIs; macOS/Windows have platform-specific packaging/dependency paths. `README.md`, `setup.py`, `build_appimage.sh`, `winbuild.bat`, `sbapp/plyer/` | missing; upstream packaging claims do not establish native .NET support |

The primary Sideband `setup.py` requires RNS `>=1.5.7`, LXMF `>=1.2.0`, and
LXST `>=0.5.4`, plus Kivy `>=2.3.0`, NumPy, Pillow, Mistune, qrcode,
Material You color, BeautifulSoup, and pycodec2. It also declares platform
conditional pyobjus/pyogg and Python-version-specific audio dependencies.
These are source package requirements, not proof that every feature works on
every listed operating system. In particular, the README notes Python/Kivy
version constraints for some source installs.

## Architecture and compatibility findings

- The native NomadNet project is `src\NomadNet\NomadNet.csproj`, with
  assembly/executable `nomadnet`, namespace `NomadNet`, and target version
  `1.4.4.<height>` for the inspected primary source revision. It uses
  `ReticulumNode`, `LxmRouter`, `ClientLinks`, and the existing public link
  request API; no stack or LXMF source was changed.
- The initial user-visible scope is text LXMF, announced-destination listing,
  bounded page requests/navigation/bookmarks, and bounded file downloads.
  Incoming binary content is persisted as a non-rendered message notice.
  Attachments, paper messages, propagation, node hosting, RRC, and full Micron
  functionality are not claimed.
- The app target is primary NomadNet 1.4.4 at
  `f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2` (root tree
  `223037b20cb53ec1d9e73ba9e4a66886de459ebf`). Its Python package declares
  minimum RNS 1.6.0 and LXMF 1.2.2, while this ecosystem pins RNS 1.5.7 and
  LXMF 1.2.1. The actual primary NomadNet app class and node handlers were
  imported and exercised against those pinned older versions for direct LXMF,
  one page, and one file. This proves only those tested contracts; it does not
  override the package's declared minimums or establish full 1.4.4
  compatibility.
- Licensing remains unresolved upstream: `LICENSE` is GPLv3 while `setup.py`
  declares an MIT classifier. No source, translated source, or assets were
  copied. Resolve the upstream conflict before making distribution decisions
  that depend on the upstream license.
- Current ecosystem targets are Reticulum 1.5.7, LXMF 1.2.1, and LXST 0.4.5;
  the .NET libraries are `LXMF` and `LXST` (no `Dot` prefix). See
  `CONTRIBUTING.md`, `tests/Interop/reference_environment.py`,
  `docs/lxmf.md`, and `docs/lxst.md`.
- Sideband's primary source requires LXST 0.5.4, newer than the current LXST
  target 0.4.5. NomadNet requires RNS 1.6.0 and LXMF 1.2.2, newer than current
  stack targets. These mismatches limit claims; they do not justify changing
  protocol pins or claiming full application parity.
- NomadNet has a substantial client/node split, local durable state, multiple
  Reticulum request handlers, LXMF conversation workflows, and an Urwid text
  UI. Its graphical and web UI modules are upstream placeholders. The scoped
  client does not promise those placeholders as supported features.
- Sideband is a broader mobile/desktop application: a network/service core
  and durable app state behind Kivy UI, with telemetry, plugins, voice and
  device integrations. A headless daemon mode exists, but does not replace
  deciding whether the requested native .NET product includes GUI, Android,
  desktop, or only service/console scope.
- The scoped client uses the standard console and is explicitly not full
  upstream UI parity. Native Pester checks cover CLI help, version, local
  initialization, terminal status/shutdown, and delivery-identity persistence
  across restart on Windows and Linux.
- Live evidence is direct TCP loopback at one Reticulum hop, not a public
  network or multi-hop topology. The pinned Python LXMF peer and the actual
  primary NomadNet 1.4.4 app exchanged direct messages in both directions.
  Native-to-upstream page and file requests passed, and the .NET client
  retained an upstream-originated message across process restart. The
  upstream app was configured with `enable_node = yes` and a local
  `TCPServerInterface`; its Python adapter invokes the unmodified
  `NomadNetworkApp` and `LXMRouter` APIs and does not shim protocol behavior.
- NativeAOT publish and `--help`, `--version`, `--init`, and `--status` were
  exercised on Windows `win-x64` and WSL Ubuntu 26.04 `linux-x64`. The Linux
  binary currently links only standard glibc/libm/loader dynamic libraries
  (`ldd`); platform-wide Windows/Linux support beyond these hosts is unverified.

## NomadNet validation evidence

- `dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release
  --artifacts-path artifacts\nomadnet-build` — 12 passed, 0 failed.
- `Invoke-Pester -Path tests\Pester\NomadNet.Tests.ps1 -Tag Native` — 3 passed
  against the published Windows `nomadnet.exe` and 3 passed against the Linux
  `nomadnet` binary, including clean terminal shutdown and persistence of the
  same LXMF delivery identity across restart.
- `dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64
  --artifacts-path artifacts\nomadnet-aot
  -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true` —
  NativeAOT publish passed.
- WSL Ubuntu 26.04 using local .NET SDK 10.0.403:
  `dotnet publish src/NomadNet/NomadNet.csproj -c Release -r linux-x64
  --artifacts-path artifacts/nomadnet-linux/build
  -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true` —
  NativeAOT publish passed. Linux `--help`, `--version`, `--init`, and
  `--status` succeeded; `ldd` listed libc, libm and the ELF loader.
- Reference verification in WSL:
  `PYTHONPATH=$root/rns:$root/lxmf DOTRETICULUM_UPSTREAM=$root/rns
  DOTRETICULUM_LXMF_UPSTREAM=$root/lxmf
  $root/reference-venv/bin/python tests/Interop/reference_environment.py
  --require-lxmf` — passed; imports resolved to the pinned RNS commit
  `a29bfb51c468945c96a1ee22cae0114e993e2391` (1.5.7) and LXMF commit
  `e8ba02483217af43be284a72f1c059c730b7d047` (1.2.1).
- `Invoke-Pester -Path tests\Pester\NomadNet.Tests.ps1 -Tag Live` in WSL,
  with those reference paths, primary source
  `f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`, and the Linux NativeAOT
  executable — 2 passed, 0 failed. It verifies direct LXMF both ways against
  Python LXMF, then against the actual upstream NomadNet app: `/page/readme.mu`
  and `/file/live.txt` requests, .NET-to-upstream message persistence,
  upstream-to-.NET direct delivery, and message history after restarting the
  .NET process. The test topology is one TCP hop with no Reticulum transport
  node, packet loss, or multi-hop path.
- `Invoke-Pester -Path tests\Pester\NomadNet.Tests.ps1 -Tag Live` on Windows
  with the same pinned Python reference and the Windows NativeAOT executable —
  1 passed: direct LXMF both ways against the pinned Python peer.
- The isolated Linux reference venv initially lacked `urwid` and `qrcode`;
  installed declared NomadNet dependencies there (`urwid 4.2.5`, `qrcode
  8.2`). The live test work directory is placed on the WSL Linux filesystem:
  NomadNet executes `.mu` pages when the filesystem reports the executable
  bit, which WSL's default `/mnt/c` mount reports for ordinary files.

## References

- Primary locators: `rns://7649a50d84610232d1416b41d2896aff/reticulum/nomadnet`
  and `rns://7649a50d84610232d1416b41d2896aff/reticulum/sideband`.
- Public mirror locators used for identification/comparison only:
  [NomadNet](https://github.com/markqvist/NomadNet) and
  [Sideband](https://github.com/markqvist/Sideband).
- The [Reticulum community rngit index](https://reticulum.miraheze.org/wiki/Rngit)
  lists the primary locators. The [Reticulum Git-over-Reticulum manual](https://reticulum.network/manual/git.html)
  documents the repository transport.
- The fetched source snapshots and isolated retrieval configuration are local,
  ignored artifacts under `artifacts\nomadnet`; they are not part of this
  documentation commit.
