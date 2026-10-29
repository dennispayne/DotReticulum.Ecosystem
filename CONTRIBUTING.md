# Contributing

DotReticulum.Ecosystem is an experimental project written predominantly by LLM coding agents under human direction. Discuss substantial protocol or architecture changes in an issue first. Keep pull requests focused and follow the PR checklist and Code of Conduct.

## Development

Install the .NET 10 SDK and use C# 13, nullable reference types, file-scoped namespaces, and the repository `.editorconfig`. Nullable diagnostics are errors. The solution file is `DotReticulum.Ecosystem.slnx`.

```sh
git submodule update --init
dotnet build DotReticulum.Ecosystem.slnx -c Release
dotnet test DotReticulum.Ecosystem.slnx -c Release
```

Every behavior change needs tests, including malformed inputs, boundary lengths and failure paths. Protocol changes require independently generated reference vectors produced by executing the upstream Python implementation, with the exact upstream revision and generation method recorded in the test (see `tests/LXMF.Tests/GenerateLxmfVectors.py`). Round-trip tests alone are not interoperability evidence. Do not commit real private keys, captured private traffic, or credentials.

Use `pwsh -NoProfile -File tests\run-tests.ps1 -Coverage -Pester` for the unified
Release, compatibility-report and production-coverage gate. See
[Testing and compatibility evidence](docs/testing.md) for stable short
`ClaimId`/`TestId` mappings, live/native modes, strict aggregation and evidence
boundaries. Every xUnit class needs a `Unit` or `Integration` category;
live methods additionally carry `Live` and skip explicitly when not enabled.

The ecosystem projects and namespaces are `LXMF`, `LXST` and `Telemetry`, with matching test-project names. Consumers of the former `Dot`-prefixed projects must update project references and namespace imports and rebuild; no compatibility assemblies are supplied. The `telemetry` executable retains its lowercase assembly name. Existing `DOTLXST_*` environment variables, executable names, persisted formats and wire identifiers are unchanged.

The desktop applications add `src/NomadNet` (the `nomadnet` terminal executable), `src/Sideband` (the application core) and `src/Sideband.Desktop` (the Avalonia `sideband` executable), with tests in `tests/NomadNet.Tests` and `tests/Sideband.Tests`. Their upstream inventories, provenance, scope and reproduction commands are in `docs/nomadnet.md` and `docs/sideband.md`. Do not copy upstream UI source or assets; licensing and observed protocol compatibility are separate concerns.

## The DotReticulum dependency

DotReticulum is a git submodule at `external/DotReticulum`, referenced by project. Its commit is the version of the Reticulum stack this repository is built and tested against. To move to a newer DotReticulum:

```sh
git -C external/DotReticulum fetch
git -C external/DotReticulum checkout <commit>
dotnet test DotReticulum.Ecosystem.slnx -c Release
git add external/DotReticulum
```

Commit the new pin once the build and tests pass.

## Live interoperability tests

Live tests compare against the actual Python reference and are skipped unless enabled:

- `DOTRETICULUM_LIVE=1`
- `DOTRETICULUM_PYTHON` - a Python venv containing `lxmf` 1.2.1 (and the RNS it needs)
- `DOTRETICULUM_UPSTREAM` - a working checkout of the pinned RNS revision; the harness verifies the imported source and Git revision

`tests/Interop/reference_environment.py` holds the pins. Keep its RNS commit identical to the one in the submodule. The verifier checks the actual RNS module path, version, Git revision and clean source tree, including untracked files. Use a native Linux checkout when running in WSL: a Windows checkout with CRLF conversion can appear modified to Linux Git.

LXMF 1.2.1 targets primary-host commit `e8ba02483217af43be284a72f1c059c730b7d047`, tree `f5ad44f221707dacf600edcbeeffee57f2c9cc43`. The history was fetched with native .NET `rngit` from `rns://7649a50d84610232d1416b41d2896aff/reticulum/lxmf`: a disclosed GitHub-mirror seed plus 13,622 newly transferred bundle bytes, verified by strict Git fsck. This proves object consistency, not publisher authentication. The fetched HEAD was already 1.2.2; the compatibility target remains 1.2.1.

Every shipped Python source file at that 1.2.1 commit matches the pinned PyPI wheel (SHA-256 `4bd95325b00e5fae81241e28f26a5ed22b38fea941e128d1c63a33c99e931440`). The verifier fingerprints all those files: a matching installed distribution version alone is insufficient. Optionally set `DOTRETICULUM_LXMF_UPSTREAM` and select that checkout in `PYTHONPATH` to additionally verify its Git revision and clean source tree. Without that variable the installed wheel is verified. The stale GitHub mirror is not a 1.2.1 source pin.

LXST targets primary-host revision `3b596f48ddd252ddc9c315ef8f3020cfd3688223` (0.4.5), tree `119ac884a2ff8c1d69b94a3129f59a6bd94b4229`. Native .NET `rngit` fetched its history from `rns://7649a50d84610232d1416b41d2896aff/reticulum/lxst`, using a disclosed mirror seed plus 34,822,229 new bundle bytes and strict Git fsck. The primary HEAD was already 0.5.4; the target remains 0.4.5. The initial mirror snapshot `721b2eee6dd37606e423914a4937a11a3c3a761f` has identical codec/network/telephony source but is not an ancestor of the primary history and is not the active pin.

Set `DOTRETICULUM_LXST_UPSTREAM` to the clean primary checkout and install its declared dependencies into the isolated reference venv. `--require-lxst` verifies the actual imported checkout, revision and version. The verifier allows upstream's generated `filterlib` extension and CFFI cache C file, but no tracked modifications or additional source modules. These are native build outputs, not reference-side behavior shims. Object consistency is not publisher authentication.

The LXST source has a different license (CC BY-NC-ND 4.0); do not copy, translate, redistribute or assume the root Reticulum License applies to it. LXST must remain independently authored from factual protocol contracts and observed reference behavior. Native codec dependencies have their own licenses; see `docs/lxst.md`.

Obtain RNS updates with DotReticulum's `rngit fetch` and verify the objects; the GitHub mirror is only a secondary comparison source. Disclose reference-side shims and non-default peer limits (for example an `lxmd` transfer-size limit). Unsupported test modes must explicitly skip rather than return a false pass.

Run provenance acceptance and rejection tests with the reference environment variables set:

```powershell
& $env:DOTRETICULUM_PYTHON tests\Interop\reference_environment.py --require-lxmf
& $env:DOTRETICULUM_PYTHON tests\Interop\reference_environment.py --require-lxmf --require-lxst
pwsh -NoProfile -File tests\run-pester.ps1
```

The Pester suite requires Pester 5.7.1 or later. Reference-dependent cases explicitly skip when the environment variables are absent; with them set, a missing or incorrect reference fails.

LXST live session/telephone cases have their own opt-in, `DOTLXST_LIVE=1`, in addition to the Python/RNS/LXST source paths above. Enabling LXMF live mode alone does not enable them.

Headless native codec cases require `DOTLXST_NATIVE=1` with compatible Opus and Codec2 libraries installed. Without that opt-in they skip explicitly; these are native bitstream tests, not live Reticulum call or audio-device evidence.

Telephone audio cases additionally require `DOTLXST_VIRTUAL_AUDIO=1` on Linux with PulseAudio and its `pactl`, `pacat` and `parec` tools. They use virtual audio, not physical microphone/speaker or audio-quality acceptance.

The original Windows waveform device adapter has deterministic lifecycle tests
on both operating systems and a Windows-only endpoint enumeration case.
`DOTLXST_WINDOWS_AUDIO=1` additionally opens the default Windows output to verify
synthetic-silence playback, cancellation and buffer reuse. It never opens a
microphone. This is not reference WASAPI/loopback or physical audio-quality
acceptance; physical capture remains unverified.

With the reference paths configured, enable all three LXST modes to run its
complete provisioned Linux suite:

```powershell
$env:DOTLXST_LIVE = '1'
$env:DOTLXST_NATIVE = '1'
$env:DOTLXST_VIRTUAL_AUDIO = '1'
dotnet test tests\LXST.Tests\LXST.Tests.csproj -c Release --artifacts-path <linux-output-directory>
```

Use distinct `--artifacts-path` directories for Windows, Linux and concurrent
validation processes. Shared `obj` outputs from Windows and WSL are not
interchangeable; a successful restore on one OS does not prepare the other.

Published executable acceptance selects a binary explicitly:
`DOTRETICULUM_LXMD_BINARY` for `Lxmd.Tests.ps1`, or
`DOTLXST_RNPHONE_BINARY` for `Rnphone.Tests.ps1`. Run the chosen script through
`tests\run-pester.ps1 -Path <test-script> -Tag Native`. The phone's Python case
also requires `DOTLXST_LIVE=1`; `-Tag NativeStandalone` selects its source-free,
two-native-process virtual-audio flow instead. Run phone acceptance from a
native Linux working directory, not the Windows-mounted checkout: Pester's
temporary identity files must support owner-only Unix permissions. The test
script and binary may be supplied by absolute path.

## Language ownership

C# implements and verifies our solution. PowerShell 7 builds and operates the development workflow. Python only bridges to the upstream Python reference: small adapters for actual RNS/LXMF API calls, callback observations and independent vector generation. Do not reimplement reference behavior in our languages.

## Moving to a new upstream release

Each project targets one upstream release, recorded in its own `version.json` (`src/LXMF/version.json` is the LXMF version). To move `LXMF` to a newer LXMF release:

1. Update the LXMF version pin (and the RNS pin, if it changed) in `tests/Interop/reference_environment.py`.
2. Regenerate the vectors by executing the new upstream and record the revision. Do not relabel older vectors.
3. Review upstream changes since the old release, update the code and `docs/lxmf.md`, and run the live tests.
4. Edit `src/LXMF/version.json` to the new upstream number; the build height resets.

## Licensing

This project is a derivative of Reticulum and is released under the Reticulum License, which adds conditions MIT lacks. Do not relicense, dual-license or remove copyright notices. Contributions are accepted under the same license.
