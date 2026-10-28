# NomadNet and Sideband inventory

## Present state

This is a source, licensing, platform, and architecture inventory for two
proposed ecosystem applications. **Neither application has a native .NET
implementation here.** No application interoperability is claimed. Existing
`LXMF`, `LXST`, and DotReticulum libraries are potential building blocks, not
evidence that either application is implemented.

The intended application names are **NomadNet** and **Sideband** (without a
`Dot` prefix). This document does not select a UI framework or authorize
implementation.

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
| Executable and lifecycle | `nomadnet=nomadnet.nomadnet:main`; `--config`, `--rnsconfig`, `--textui`, `--daemon`, `--console`, `--version`. `setup.py`, `nomadnet/nomadnet.py` | missing |
| Network client and identity | Reticulum node and identity, `nomadnetwork.node` destination, peer announces/directory, path requests, and LXMF delivery/propagation settings. `nomadnet/NomadNetworkApp.py`, `Directory.py`, `Conversation.py` | missing at application layer |
| Messaging and persistence | Direct LXMF conversations, status/history, attachments, paper output, propagation-node selection/synchronization and retry behavior. Conversation/message files, indexes and peer/directory settings persist below the configured NomadNet storage directory. `Conversation.py`, `NomadNetworkApp.py` | missing at application layer |
| Hosted pages and files | Node announces, serves `/page`, `/file`, and `/media`, has per-file `.allowed` access rules, supports node-side executable page handlers on non-Windows systems, and converts/caches supported images as WebP. `Node.py` | missing |
| Page/browser contract | Micron markup, node navigation, forms/fields, page cache, terminal image rendering and downloads. `ui/textui/Browser.py`, `MicronParser.py`, `FileBrowser.py`, `images/` | missing |
| Live group chat | RRC client, room state, hub interaction and persisted history. `RRC.py`, `ui/textui/Channels.py` | missing |
| User interface modes | Text and menu UI are implemented upstream; the text UI uses Urwid. The `GraphicalUI` and `WebUI` modules explicitly report “not implemented” and abort. `ui/__init__.py`, `TextUI.py`, `GraphicalUI.py`, `WebUI.py` | upstream-placeholder; not a required parity feature |
| Configuration and platform | Default config location is `~/.nomadnetwork`, with `/etc/nomadnetwork` and `~/.config/nomadnetwork` discovery; state includes identity, pages/files, conversations, directory, peer settings, cache, resources and attachments. Python `>=3.8`; package requirements include RNS `>=1.6.0`, LXMF `>=1.2.2`, Urwid `>=3.0.5`, and qrcode. `NomadNetworkApp.py`, `setup.py` | missing; upstream's “OS Independent” package classifier is not a .NET platform verification |

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

- Current ecosystem targets are Reticulum 1.5.7, LXMF 1.2.1, and LXST 0.4.5;
  the .NET libraries are `LXMF` and `LXST` (no `Dot` prefix). See
  `CONTRIBUTING.md`, `tests/Interop/reference_environment.py`,
  `docs/lxmf.md`, and `docs/lxst.md`.
- The fetched NomadNet `setup.py` requires RNS 1.6.0 and LXMF 1.2.2, newer
  than the current stack/LXMF targets. Sideband's primary source requires
  LXST 0.5.4, newer than the current LXST target 0.4.5. Resolve whether the
  work targets these current upstream revisions or older compatible releases
  before claiming application parity; protocol/app version upgrades are not
  authorized by this inventory.
- NomadNet has a substantial client/node split, local durable state, multiple
  Reticulum request handlers, LXMF conversation workflows, and an Urwid text
  UI. Its graphical and web UI modules are upstream placeholders. The
  implementation must not promise those placeholders as supported features.
- Sideband is a broader mobile/desktop application: a network/service core
  and durable app state behind Kivy UI, with telemetry, plugins, voice and
  device integrations. A headless daemon mode exists, but does not replace
  deciding whether the requested native .NET product includes GUI, Android,
  desktop, or only service/console scope.
- The user-facing scope is therefore still open: confirm NomadNet's initial UI
  target (especially whether a text UI is required) and Sideband's target
  platforms/UI before choosing UI frameworks. This inventory makes no
  terminal-framework selection.
- None of the source retrieval or inventory work is live application
  interoperability, runtime testing, or .NET implementation evidence.

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
