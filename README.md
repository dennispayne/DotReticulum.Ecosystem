# DotReticulum.Ecosystem

Native C# / .NET 10 implementations of the applications and protocols layered on top of [Reticulum](https://reticulum.network): LXMF and a .NET-specific telemetry tool today, with LXST, Sideband, NomadNet and others as possible future projects. Everything here is built on [DotReticulum](https://github.com/dennispayne/DotReticulum), the .NET implementation of the Reticulum Network Stack itself.

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
| [`DotLXMF`](src/DotLXMF) | [LXMF](https://github.com/markqvist/LXMF) 1.2.1 | Implemented in part: message format, signing, stamps, opportunistic and direct delivery. No propagation nodes, tickets or delivery-destination ratchets. See [docs/lxmf.md](docs/lxmf.md) |
| [`DotTelemetry`](src/DotTelemetry) | None: a DotReticulum-specific format, not part of upstream Reticulum or LXMF | Implemented: `dottelemetry` daemon and viewer. No Python interoperability target. See [docs/telemetry.md](docs/telemetry.md) |
| DotLXST | [LXST](https://github.com/markqvist/LXST) | Not started |
| DotSideband | [Sideband](https://github.com/markqvist/Sideband) | Not started. Sideband depends on RNS, LXMF and LXST, so it would need DotLXST for voice calls |
| DotNomad | [NomadNet](https://github.com/markqvist/NomadNet) | Not started |

A project listed as not started is an idea, not a commitment.

## Upstream

Reticulum, LXMF, LXST, Sideband and NomadNet are separate projects by Mark Qvist; the Python code of each is its reference implementation. The links above are the public GitHub repositories, the easiest place to browse the upstream code. The Reticulum stack's own primary home is hosted on the Reticulum network (`rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum`, see [DotReticulum](https://github.com/dennispayne/DotReticulum)); this repository has not verified where development of the other projects takes place, so check each repository's README.

Targeted: LXMF 1.2.1 on RNS 1.5.7 (upstream commit `a29bfb51c468945c96a1ee22cae0114e993e2391`), as pinned in `tests/Interop/reference_environment.py`.

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
| Propagation, tickets, ratchets, an `lxmd` equivalent | Not implemented |
| Telemetry (`dottelemetry`) | .NET-only: nodes publish and view reports through direct links and a .NET `rnsd` hop; no upstream counterpart |

The Reticulum stack's own compatibility status and platform support are documented in the [DotReticulum README](https://github.com/dennispayne/DotReticulum#compatibility). Platform behavior here follows from it; nothing in this repository has been verified separately on other platforms.

## Versioning

Each project carries the **upstream release it targets, plus a build number**, like DotReticulum does. `DotLXMF` is `1.2.1.<height>`: the first three components are the LXMF release, entered by hand in [`src/DotLXMF/version.json`](src/DotLXMF/version.json); the last is the git height from [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning), which resets when the upstream number changes. A version names the target, not completed parity. Every project in this repository gets its own `version.json` for its own upstream. `DotTelemetry` has no upstream, so it uses its own `0.1.<height>` number.

## Contributing

Contributions are welcome; read [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Released under the [Reticulum License](LICENSE), copied verbatim from upstream. It adds restrictions on harmful systems and AI-training datasets that are not part of MIT, so this project is not MIT-licensed.
