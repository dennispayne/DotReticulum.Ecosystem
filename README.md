# DotReticulum.Ecosystem

Native C# / .NET 10 implementations of the applications and protocols layered on top of [Reticulum](https://reticulum.network): partial LXMF and LXST implementations and a .NET-specific telemetry tool. Everything here is built on [DotReticulum](https://github.com/dennispayne/DotReticulum), the .NET implementation of the Reticulum Network Stack itself.

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
| [`LXMF`](src/LXMF) | [LXMF](https://github.com/markqvist/LXMF) 1.2.1 | Required software contracts implemented and Windows/Linux live-verified, including NativeAOT `lxmd`, mixed-node propagation and recovery. Optional omissions, safety differences and unverified physical/platform boundaries remain. See [docs/lxmf.md](docs/lxmf.md) |
| [`Telemetry`](src/Telemetry) | None: a DotReticulum-specific format, not part of upstream Reticulum or LXMF | Implemented: `telemetry` daemon and viewer. No Python interoperability target. See [docs/telemetry.md](docs/telemetry.md) |
| [`LXST`](src/LXST) | [LXST](https://github.com/markqvist/LXST) 0.4.5 | Partial: framing, native Opus/Codec2, audio processing, stream sessions, telephony and Linux/Windows `rnphone`. Full API/utility/platform parity is not complete. See [docs/lxst.md](docs/lxst.md) |
| DotSideband | [Sideband](https://github.com/markqvist/Sideband) | Not started. Sideband depends on RNS, LXMF and LXST, so it would need LXST for voice calls |
| DotNomad | [NomadNet](https://github.com/markqvist/NomadNet) | Not started |

A project listed as not started is an idea, not a commitment.

Project folders and namespaces use `LXMF`, `LXST` and `Telemetry`; the library assemblies are `LXMF` and `LXST`. Executable names remain `lxmd`, `rnphone` and `telemetry`, including the lowercase `telemetry` assembly name. These names do not imply upstream affiliation.

## Upstream

Reticulum, LXMF, LXST, Sideband and NomadNet are separate projects by Mark Qvist; the Python code of each is its reference implementation. The links above are public GitHub mirrors. RNS, LXMF and LXST source histories were also retrieved with native .NET `rngit` from Reticulum host `7649a50d84610232d1416b41d2896aff`, repositories `reticulum/reticulum`, `reticulum/lxmf` and `reticulum/lxst`. Source-object verification is not publisher authentication; see [CONTRIBUTING.md](CONTRIBUTING.md) for seed disclosure and provenance.

Targets pinned in `tests/Interop/reference_environment.py`: RNS 1.5.7 (`a29bfb51c468945c96a1ee22cae0114e993e2391`), LXMF 1.2.1 (`e8ba02483217af43be284a72f1c059c730b7d047`) and LXST 0.4.5 (`3b596f48ddd252ddc9c315ef8f3020cfd3688223`). Newer observed upstream releases do not change these targets.

LXST's reference source is CC BY-NC-ND 4.0, not Reticulum-licensed. This repository's C# LXST implementation is independently authored from factual protocol contracts and observed reference outputs; upstream source, sounds and native codec libraries are not bundled. Native dependencies require separate installation and retain their own licenses. See [docs/lxst.md](docs/lxst.md).

## Getting the code

DotReticulum is included as a git submodule, pinned to a specific commit:

```sh
git clone --recurse-submodules https://github.com/dennispayne/DotReticulum.Ecosystem
cd DotReticulum.Ecosystem
dotnet build DotReticulum.Ecosystem.slnx -c Release
dotnet test DotReticulum.Ecosystem.slnx -c Release
```

If you cloned without `--recurse-submodules`, run `git submodule update --init`. Building requires the .NET 10 SDK (pinned in `global.json`).

Live interoperability tests are opt-in and need the pinned Python reference: set `DOTRETICULUM_LIVE=1`, `DOTRETICULUM_PYTHON` (a venv with `lxmf` 1.2.1) and `DOTRETICULUM_UPSTREAM` (a checkout of the pinned RNS revision). Without them those tests are skipped. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Compatibility

"Live" means a real, unmodified Python reference node in the pinned environment; vectors and loopback tests are not live interoperability.

| Area | Status |
| --- | --- |
| LXMF message format, signing, hashing, stamps | Reference-generated vectors; messages opened by upstream LXMF both ways |
| Opportunistic and direct delivery, large Resources | Live against Python `LXMRouter` in both directions |
| Delivery to `lxmd` | Live delivery to a real `lxmd` destination; this does not establish propagation support |
| Tickets and delivery ratchets | Live reply-ticket exchange, signed ratchet-file handoffs and old-key delivery after restart; exact boundaries in [docs/lxmf.md](docs/lxmf.md) |
| Propagation client and peers | Live bidirectional 2.2 MB synchronization, retrieval progress/cancellation/cache recovery and source-executed admission/reload/maintenance policies; documented safety differences and unverified hardware boundaries remain |
| Native `lxmd` | Final Windows/Linux NativeAOT acceptance: 6/6 cases per host, including delivery/control/persistence, timed announcements, admission/throttle recovery and autonomous mixed-node restart; not exhaustive platform/hardware compatibility |
| LXST framing and codecs | Executed upstream vectors, arbitrary signal maps and rate conversion; native Opus/Codec2 bitstreams verified in WSL. Extended raw precision is host-format-specific |
| LXST stream sessions and telephone calls | Live Python in both call directions, direct and through Python/.NET transport hops; all eight initial telephone profiles, policy rejection and timeout/recovery cases use virtual audio. Remaining gaps are in [docs/lxst.md](docs/lxst.md) |
| LXST headless audio processing | Reference-executed resampling, filters/AGC, mutable tones, bounded mixing/playback, encoded pipelines and file controls; no physical audio-quality claim |
| Windows LXST device adapter | Original PCM16 waveform backend: endpoint enumeration and native synthetic-silence playback/cancellation/reuse verified, including NativeAOT; physical capture, quality and reference WASAPI/loopback behavior are not verified |
| `rnphone` | Linux NativeAOT calls with Python, virtual audio and supplied ringtone; Windows source-free two-native-process Opus calls, profile changes and identity restart. Physical hardware and remaining platform boundaries are unverified |
| Telemetry (`telemetry`) | .NET-only: nodes publish and view reports through direct links and a .NET `rnsd` hop; no upstream counterpart |

The Reticulum stack's own compatibility status and platform support are documented in the [DotReticulum README](https://github.com/dennispayne/DotReticulum#compatibility). Ecosystem managed and native checks have run on Windows and WSL/Linux; these do not establish macOS, physical-audio or RF compatibility. COM8 RAK4631/RNode identification was read-only; one radio does not establish an RF topology.

## Versioning

Each project carries the **upstream release it targets, plus a build number**, like DotReticulum does. `LXMF` and `lxmd` target `1.2.1.<height>`; `LXST` and `rnphone` target `0.4.5.<height>`. The first three components are the upstream release, entered by hand in each project's `version.json`; the last is the git height from [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning), which resets when the upstream number changes. A version names the target, not completed parity. `Telemetry` has no upstream, so it uses its own `0.1.<height>` number.

### Releases

Releases are tag-driven NativeAOT executable packages with SHA-256 checksums. Use `pwsh tools/new-release-tag.ps1 -Application lxmd`, choosing `telemetry`, `lxmd` or `rnphone`. Tags are `telemetry-v0.1.<height>`, `lxmd-v1.2.1.<height>` or `rnphone-v0.4.5.<height>` on `main`; pushing one invokes the [Release workflow](.github/workflows/release.yml). A manual run packages without publishing. No release is implied by local validation.

`telemetry` and `lxmd` package `linux-x64`, `win-x64` and `osx-arm64`; macOS ecosystem feature acceptance remains unverified. `rnphone` packages `linux-x64` and `win-x64`; install its separately distributed native libraries as described in [docs/lxst.md](docs/lxst.md). Windows acceptance covers headless Opus calls, not every codec/file backend. Native dependencies are not bundled. The libraries have no separate release artifact.

## Contributing

Contributions are welcome; read [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Released under the [Reticulum License](LICENSE), copied verbatim from upstream. It adds restrictions on harmful systems and AI-training datasets that are not part of MIT, so this project is not MIT-licensed.
