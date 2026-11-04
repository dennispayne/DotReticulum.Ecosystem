[![Build](https://github.com/dennispayne/DotReticulum.Ecosystem/actions/workflows/build.yml/badge.svg)](https://github.com/dennispayne/DotReticulum.Ecosystem/actions/workflows/build.yml)
[![Test](https://github.com/dennispayne/DotReticulum.Ecosystem/actions/workflows/test.yml/badge.svg)](https://github.com/dennispayne/DotReticulum.Ecosystem/actions/workflows/test.yml)
[![Static analysis / CodeQL](https://github.com/dennispayne/DotReticulum.Ecosystem/actions/workflows/codeql.yml/badge.svg)](https://github.com/dennispayne/DotReticulum.Ecosystem/actions/workflows/codeql.yml)
[![Linux coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fdennispayne%2FDotReticulum.Ecosystem%2Fmain%2Fassets%2Fcoverage%2Flinux-x64.json)](docs/testing.md#hosted-split-gate-acceptance)
[![macOS coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fdennispayne%2FDotReticulum.Ecosystem%2Fmain%2Fassets%2Fcoverage%2Fosx-arm64.json)](docs/testing.md#continuous-integration)
[![Windows coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fdennispayne%2FDotReticulum.Ecosystem%2Fmain%2Fassets%2Fcoverage%2Fwin-x64.json)](docs/testing.md#hosted-split-gate-acceptance)

# DotReticulum.Ecosystem

Native C# / .NET 10 implementations of the applications and protocols layered on top of [Reticulum](https://reticulum.network): LXMF and LXST libraries, initial NomadNet and Sideband desktop clients, and a .NET-specific telemetry tool. Everything here is built on [DotReticulum](https://github.com/dennispayne/DotReticulum), the .NET implementation of the Reticulum Network Stack itself.

> [!WARNING]
> DotReticulum.Ecosystem is an **experiment in having an LLM build Reticulum-ecosystem software**, started in the spirit of doing odd things in .NET just to see whether they work. Almost all of its code, tests and documentation were written by AI coding agents under human direction. It is experimental, **not** audited, and **not** for production or security-critical use. Be careful with it.
>
> It is **not** an official, endorsed or community-recognized Reticulum, LXMF, LXST, Sideband or NomadNet implementation, and it is not the work of the Reticulum project or its maintainer. Do not treat it as a known-good implementation; those are listed in the [Reticulum README](https://github.com/markqvist/Reticulum#community-implementations).
>
> The upstream project takes a strict but fair view of LLM-generated implementations, documentation and licensing claims; see the [Reticulum README](https://github.com/markqvist/Reticulum) and manual. This repository acknowledges that position and makes no attempt to influence it, to seek recognition, or to claim anything on upstream's behalf.
>
> In keeping with it, this project keeps the [Reticulum License](LICENSE) and its copyright notice intact and does not relicense or assert its own license over derived work. Compatibility claims here are exactly as strong as the recorded evidence.

## Projects

| Project | Upstream | Status |
| --- | --- | --- |
| [`LXMF`](src/LXMF) | [LXMF](https://github.com/markqvist/LXMF) 1.2.1 | Inventoried core software implemented and Windows/Linux live-verified, including NativeAOT `lxmd`, mixed-node propagation and recovery. Default refusal now matches the primary's five charged attempts/four closed links, with healthy recovery; pinned callback re-entry defects, optional omissions, safety differences and physical/platform boundaries remain explicit. See [docs/lxmf.md](docs/lxmf.md) |
| [`Telemetry`](src/Telemetry) | None: a DotReticulum-specific format, not part of upstream Reticulum or LXMF | Implemented: `telemetry` daemon and viewer. No Python interoperability target. See [docs/telemetry.md](docs/telemetry.md) |
| [`LXST`](src/LXST) | [LXST](https://github.com/markqvist/LXST) 0.5.4 | Inventoried Windows/Linux software contracts implemented, including native Opus/Codec2, bounded file playback, independent AGC/PTT, telephony and `rnphone`; actual Python calls and native utility flows verified. Physical audio/RF/board behavior, other platforms and exhaustive parity remain unverified. Licensing and intentional differences remain explicit. See [docs/lxst.md](docs/lxst.md) |
| [`Sideband`](src/Sideband.Desktop) | [Sideband](https://github.com/markqvist/Sideband) 2.2.0 | Partial Windows/Linux Avalonia desktop client: contacts/objects/trust/unread/history, bounded announce stream and messaging/media/paper, propagation/scheduling, supported offline migration, shared-identity LXST calls/PTT, manual and opted-in Reticulum runtime telemetry/collector exchange, clustered reported and cached-discovery map overlays/local overrides, raster/offline maps, built-in authorized remote commands and explicit HTTPS mirroring. Actual Python core and published native workflows are separate evidence, not physical audio/sensors. Complete migration, other automatic sensors, arbitrary Python plugins/general remote execution and mobile remain missing/unverified/deferred. See [docs/sideband.md](docs/sideband.md) |
| [`NomadNet`](src/NomadNet) | [NomadNet](https://github.com/markqvist/NomadNet) 1.4.4 | Partial terminal/daemon application: configurable hosting/propagation peer policies, conversations, direct/propagated messaging, attachments/paper, cached pages and bounded original page/conversation ASCII images, functional forms/folds/anchors and RRC groups. Atomic primary directory/policy and bounded identity/conversation/media/paper/RRC profile import pass Windows/Linux native restart/conflict acceptance, including actual primary messages both ways after imported-identity restart. Imported RRC identity/nick/rooms retain service through rrcd across clean/abrupt native restart. Actual primary image/RRC/form/configured-peering and separate mixed propagation/loss flows pass. Remaining configuration, router migration and terminal workflows remain required work. See [docs/nomadnet.md](docs/nomadnet.md) |

A project listed as not started is an idea, not a commitment.

Project folders and namespaces use `LXMF`, `LXST`, `Telemetry`, `NomadNet` and `Sideband`; the library assemblies include `LXMF` and `LXST`. Executables are named `lxmd`, `rnphone`, `telemetry`, `nomadnet` and `sideband`, including the lowercase `telemetry` assembly name. These names do not imply upstream affiliation.

## Upstream

Reticulum, LXMF, LXST, Sideband and NomadNet are separate projects by Mark Qvist; the Python code of each is its reference implementation. The links above are public GitHub mirrors. RNS, LXMF and LXST source histories were also retrieved with native .NET `rngit` from Reticulum host `7649a50d84610232d1416b41d2896aff`, repositories `reticulum/reticulum`, `reticulum/lxmf` and `reticulum/lxst`. Source-object verification is not publisher authentication; see [CONTRIBUTING.md](.github/CONTRIBUTING.md) for seed disclosure and provenance.

Targets pinned in `tests/Interop/reference_environment.py`: RNS 1.5.7 (`a29bfb51c468945c96a1ee22cae0114e993e2391`), LXMF 1.2.1 (`e8ba02483217af43be284a72f1c059c730b7d047`) and LXST 0.5.4 (`eba5abc77fef7fef3bcb6c9c091c80b7f7492c9f`). Current LXST vectors execute the actual 0.5.4 checkout; earlier 0.4.5 evidence is not relabelled.

LXST's reference source is CC BY-NC-ND 4.0, not Reticulum-licensed. This repository's C# LXST implementation is independently authored from factual protocol contracts and observed reference outputs; upstream source, sounds and native codec libraries are not bundled. Native dependencies require separate installation and retain their own licenses. See [docs/lxst.md](docs/lxst.md).

NomadNet and Sideband references were also retrieved directly over Reticulum with native `rngit`. Their inventories record exact revisions, upstream licensing and dependency-version differences. The C# applications use original code and UI rather than copied upstream assets; passing selected messaging flows does not establish support for all features of those newer applications.

## Getting the code

DotReticulum is included as a git submodule, pinned to a specific commit:

```sh
git clone --recurse-submodules https://github.com/dennispayne/DotReticulum.Ecosystem
cd DotReticulum.Ecosystem
dotnet build DotReticulum.Ecosystem.slnx -c Release
dotnet test DotReticulum.Ecosystem.slnx -c Release
```

If you cloned without `--recurse-submodules`, run `git submodule update --init`. Building requires the .NET 10 SDK (pinned in `global.json`).

Live interoperability tests are opt-in and need the pinned Python reference: set `DOTRETICULUM_LIVE=1`, `DOTRETICULUM_PYTHON` (a venv with `lxmf` 1.2.1) and `DOTRETICULUM_UPSTREAM` (a checkout of the pinned RNS revision). Without them those tests are skipped. See [CONTRIBUTING.md](.github/CONTRIBUTING.md).

## Compatibility

"Live" means a real, unmodified Python reference node in the pinned environment; vectors and loopback tests are not live interoperability.

The [testing standard](docs/testing.md) provides a scoped compatibility catalog
with short `ClaimId`/`TestId` mappings, per-case JSON/Markdown evidence and merged
coverage of all eight production assemblies. Run
`pwsh -NoProfile -File tests\run-tests.ps1 -Coverage -Pester`; opt-in live/native
checks remain distinct from local coverage. Skipped or uncovered evidence never
becomes a passing compatibility claim.

[Build](.github/workflows/build.yml) produces managed test archives and NativeAOT
release targets once. [Test](.github/workflows/test.yml) reuses those exact
artifacts for independent Windows/Linux Unit, Integration and Compatibility
jobs, with consolidated coverage and evidence. [Static analysis](.github/workflows/codeql.yml)
runs CodeQL and PSScriptAnalyzer. Python-peer, GUI and hardware checks remain
separate opt-in evidence; green Actions do not mean full upstream parity.

| Area | Status |
| --- | --- |
| LXMF message format, signing, hashing, stamps | Reference-generated vectors; messages opened by upstream LXMF both ways |
| Opportunistic and direct delivery, large Resources | Live against Python `LXMRouter` in both directions |
| Delivery to `lxmd` | Live delivery to a real `lxmd` destination; this does not establish propagation support |
| Tickets and delivery ratchets | Live reply-ticket exchange, signed ratchet-file handoffs and old-key delivery after restart; exact boundaries in [docs/lxmf.md](docs/lxmf.md) |
| Propagation client and peers | Live bidirectional 2.2 MB synchronization, retrieval progress/cancellation/cache recovery and source-executed admission/reload/maintenance policies; documented safety differences and unverified hardware boundaries remain |
| Native `lxmd` | Final Windows/Linux NativeAOT acceptance: 6/6 cases per host, including delivery/control/persistence, timed announcements, admission/throttle recovery and autonomous mixed-node restart; not exhaustive platform/hardware compatibility |
| LXST framing and codecs | Executed upstream vectors, arbitrary signal maps and rate conversion; native Opus/Codec2 bitstreams verified on Windows/Linux, with independently executed same-library platform PCM observations. Extended raw precision is host-format-specific |
| LXST stream sessions and telephone calls | Live Python in both call directions, direct and through Python/.NET transport hops; all eight initial telephone profiles, policy rejection and timeout/recovery cases use virtual audio. Remaining gaps are in [docs/lxst.md](docs/lxst.md) |
| LXST headless audio processing | Reference-executed resampling, filters/AGC, mutable tones, bounded mixing/playback, encoded pipelines and file controls; no physical audio-quality claim |
| Windows LXST device adapter | Original waveform and WASAPI providers: shared/exclusive/loopback contracts, channel maps and owned lifecycle implemented. Native enumeration, shared synthetic silence and loopback start/stop exercised; the tested exclusive float32 format is unsupported by both references. No microphone/private loopback frames were read; physical capture/acoustics remain unverified |
| `rnphone` | Linux NativeAOT calls with Python, virtual audio and supplied ringtone; Windows source-free native Opus calls. Separate one-hour Windows/Linux native call/recovery and Linux service/SIGTERM/identity-restart cases pass. Physical hardware, prolonged Python-peer calls and remaining platform boundaries are unverified |
| Telemetry (`telemetry`) | .NET-only: nodes publish and view reports through direct links and a .NET `rnsd` hop; no upstream counterpart |
| NomadNet (`nomadnet`) | Windows/Linux published hosting, caching, attachment/paper, propagation and restart workflows; deliberate dropped-Resource recovery. Actual primary RRC/forms and automatic/forced Micron partial refresh run across a Python transport. Exact cases/topologies and remaining software gaps are in [docs/nomadnet.md](docs/nomadnet.md); no full application-parity claim |
| NomadNet profile migration | Actual primary constructor/persistence, four historical containers with bounded media/paper and supported identity/settings/directory import; Windows/Linux published same-profile clean/abrupt restart and subsequent 29-byte messages both ways with the full primary application pass. Direct TCP for that slice; separate bounded RRC hubs/nicks/joined/parted-history import preserves identity and exchanges actual primary messages through one unchanged rrcd hub after clean/abrupt restart. Historical claims do not authenticate messages or confer trust. Full configuration/router migration remains required |
| NomadNet client runtime configuration | Executed primary configuration/NoneUI/CLI observations; Windows/Linux native configured text/headless selection, persisted notification policy, daemon precedence, actual-primary messaging and imported-identity restart pass. The original console is not Urwid; full configuration/router/editor/presentation and newer upstream dependency minimums remain explicit gaps |
| NomadNet application logging | Executed primary severity/routing/path/rotation and CLI observations; Windows/Linux native configuration, file/stdout, daemon/console precedence, failure/recovery and actual-primary messages across restart pass. Original terminal/status/BEL are separate outputs. Native visible fail-closed sink errors intentionally correct a reproduced pinned RNS logging-lock deadlock. Windows Unix-permission evidence remains explicitly skipped; no complete configuration or exact internal Python log-message claim |
| Sideband (`sideband`) | Windows/Linux actual primary Python daemon-core direct/propagated text, images and Opus/Codec2 audio both ways; separate 2 MiB file, scheduled fetch and fallback/recovery cases. Published offline identity/config/SQLite-WAL/router/schedule import, codec decoding and original Avalonia image rendering pass. No upstream GUI, physical recording/acoustics, arbitrary impairment, full migration or published-native file-transfer claim |
| Sideband voice | Actual primary application-core Opus/Codec2/PTT both call origins, direct and through a .NET transport; separate shared-client identity/trust/history/restart cases. Bounded primary voice-only names/trust import, original editing/deletion and actual call rejection/recovery preserve LXMF-conversation precedence without treating discoveries as conversations. Linux reference uses isolated virtual PulseAudio; Windows reference uses a disclosed synthetic device-backend shim. Windows/Linux published native pairs separately pass a real one-hour in-call hold, codec switch, peer-loss recovery and identity/history restart using synthetic/discard devices. Separate bounded primary call-log import preserves labelled historical assertions through offline native restart without granting trust or enabling devices. Neither establishes physical audio or Python-peer endurance |
| Sideband maps and manual telemetry UI | Executed primary projection/provider/layer/overlay/object/fixed-file/passive-discovery observations; original bounded clustering/scatter, reported interface/connection points, cached discoveries, local overrides and object workflows persist. Actual signed discovery advertisements pass both ways direct/.NET-hop; published cached-map rendering is separate from network transport and manual preview. No map autoconnect, physical position, public-provider, upstream GUI or complete migration claim |
| Sideband telemetry application | Actual primary daemon-core manual reports and collector requests/streams both ways, direct/Python-hop, restart, supported SQLite/profile import, privacy/cursor rejection, scheduling and propagation fallback/recovery on Windows/Linux. Collector inner source is asserted, not authenticated origin. Disclosed fault injection, native safety differences and automatic/physical sensor omissions remain explicit |
| Sideband Reticulum runtime telemetry | Executed collection/cache/rate/sparse and primary receive-normalization vectors; actual local/shared-owner management, primary daemon-core reports both ways direct/Python-hop, rendering and native history restart pass on Windows/Linux. Separate published native collection/UI/settings/restart passes without physical sensors. Dense-table disclosure is explicit; primary persistence sparsifies received dense data. Other automatic sensors, prolonged/impaired operation and published-native Python telemetry exchange remain unverified/missing |
| Sideband public software mirror | Original consent-gated HTTPS server, bounded static files/package list and persistent private certificate; real fingerprint-pinned loopback TLS and published window/restart acceptance on Windows/Linux. No bundled upstream assets, automatic downloads, public trust installation or actual primary HTTPS-peer claim |
| Sideband built-in remote commands | Actual primary parsing/admission, propagated command storage/retrieval, offline automatic-reply fallback and direct-failure recovery on Windows/Linux, direct/Python-hop/.NET-hop with disclosed TCP delay; separate published native Remote window and policy persistence. Native-origin initially-propagated failure/recovery passes; enabled primary retry is blocked by a reproduced pinned callback-lock defect and the both-origin gate remains failing. Unknown-conversation filtering distinguishes new discoveries; legacy origins cannot be reconstructed. Arbitrary Python plugins and general remote execution remain missing |
| Sideband announce stream | Actual primary delivery announcements both ways, unnamed replacement, PN metadata, delete/reannounce and native identity/history restart pass on Windows/Linux direct/Python-hop/.NET-hop. Original bounded desktop actions and separate primary SQLite-WAL history import/published offline restart rendering pass; strict48 corrects primary's pre-insert49 behavior. Offline delivery costs are unknown without the router cache. No physical-signal, impaired-network or upstream-GUI claim |
| Paper QR and Sideband paper | Actual LXMF QR pixels decode in .NET; independent Python decoder reads Windows/Linux native PNGs. Actual primary Sideband daemon cores create/ingest paper both ways with trust/restart/duplicate persistence. Bounded historical outgoing primary envelopes restore exact native offline URI/PNG exports, imported warnings and original desktop rendering; an actual primary recipient opens them and retains duplicate suppression across restart. Import does not authenticate opaque ciphertext or bind it to stored plaintext. No new Reticulum topology, physical camera/printer or complete profile-migration claim |

The Reticulum stack's own compatibility status and platform support are documented in the [DotReticulum README](https://github.com/dennispayne/DotReticulum#compatibility). Ecosystem managed and native checks have run on Windows and WSL/Linux; these do not establish macOS, physical-audio or RF compatibility. COM8 RAK4631/RNode identification was read-only; one radio does not establish an RF topology.

The per-project reference docs record exact current acceptance commands,
reference revisions, native binaries and exercised flows. The hosted baseline
and artifact-producing commit boundaries are in [docs/testing.md](docs/testing.md#baseline-provenance).
Local live-peer and rendered-GUI acceptance are separate from hosted selected
checks; neither test totals nor coverage establish the omitted application
contracts.

## Versioning

Each project carries the **upstream release it targets, plus a build number**, like DotReticulum does. `LXMF` and `lxmd` target `1.2.1.<height>`; `LXST` and `rnphone` target `0.5.4.<height>`; `NomadNet` targets `1.4.4.<height>` and `Sideband` targets `2.2.0.<height>`. The first three components are the upstream release, entered by hand in each project's `version.json`; the last is the git height from [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning), which resets when the upstream number changes. A version names the target, not completed parity. `Telemetry` has no upstream, so it uses its own `0.1.<height>` number.

### Releases

Releases are tag-driven NativeAOT executable packages with SHA-256 checksums. Use `pwsh tools/new-release-tag.ps1 -Application lxmd`, choosing `telemetry`, `lxmd`, `rnphone`, `nomadnet` or `sideband`. Tags are `telemetry-v0.1.<height>`, `lxmd-v1.2.1.<height>`, `rnphone-v0.5.4.<height>`, `nomadnet-v1.4.4.<height>` or `sideband-v2.2.0.<height>` on `main`; pushing one invokes the [Release workflow](.github/workflows/release.yml). A manual run packages without publishing. No release is implied by local validation.

`telemetry` and `lxmd` package `linux-x64`, `win-x64` and `osx-arm64`; macOS ecosystem feature acceptance remains unverified. `rnphone` packages `linux-x64` and `win-x64`; install its separately distributed native libraries as described in [docs/lxst.md](docs/lxst.md). Windows acceptance covers headless Opus calls, not every codec/file backend. Native dependencies are not bundled. The libraries have no separate release artifact.

`nomadnet` and `sideband` target `linux-x64` and `win-x64`. Packages retain their native runtime files alongside the executable, including Linux serial-port support and Sideband's Avalonia dependencies; do not extract just the executable. See the application reference docs for supported flows, runtime dependencies and omitted upstream features.

## Contributing

Contributions are welcome; read [CONTRIBUTING.md](.github/CONTRIBUTING.md).

## License

Released under the [Reticulum License](LICENSE), copied verbatim from upstream. It adds restrictions on harmful systems and AI-training datasets that are not part of MIT, so this project is not MIT-licensed.
