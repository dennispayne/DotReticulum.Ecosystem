# Contributing

DotReticulum.Ecosystem is an experimental project written predominantly by LLM coding agents under human direction. Discuss substantial protocol or architecture changes in an issue first. Keep pull requests focused and follow the PR checklist and Code of Conduct.

## Development

Install the .NET 10 SDK and use C# 13, nullable reference types, file-scoped namespaces, and the repository `.editorconfig`. Nullable diagnostics are errors. The solution file is `DotReticulum.Ecosystem.slnx`.

```sh
git submodule update --init
dotnet build DotReticulum.Ecosystem.slnx -c Release
dotnet test DotReticulum.Ecosystem.slnx -c Release
```

Every behavior change needs tests, including malformed inputs, boundary lengths and failure paths. Protocol changes require independently generated reference vectors produced by executing the upstream Python implementation, with the exact upstream revision and generation method recorded in the test (see `tests/DotLXMF.Tests/GenerateLxmfVectors.py`). Round-trip tests alone are not interoperability evidence. Do not commit real private keys, captured private traffic, or credentials.

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

`tests/Interop/reference_environment.py` holds the pins (RNS commit and LXMF version). It is a copy of the DotReticulum file of the same name; keep the RNS pin identical to the one in the submodule. Obtain RNS updates with DotReticulum's `rngit fetch` and verify the objects; the GitHub mirror is only a secondary comparison source. Disclose reference-side shims and non-default peer limits (for example an `lxmd` transfer-size limit). Unsupported test modes must explicitly skip rather than return a false pass.

## Language ownership

C# implements and verifies our solution. PowerShell 7 builds and operates the development workflow. Python only bridges to the upstream Python reference: small adapters for actual RNS/LXMF API calls, callback observations and independent vector generation. Do not reimplement reference behavior in our languages.

## Moving to a new upstream release

Each project targets one upstream release, recorded in its own `version.json` (`src/DotLXMF/version.json` is the LXMF version). To move `DotLXMF` to a newer LXMF release:

1. Update the LXMF version pin (and the RNS pin, if it changed) in `tests/Interop/reference_environment.py`.
2. Regenerate the vectors by executing the new upstream and record the revision. Do not relabel older vectors.
3. Review upstream changes since the old release, update the code and `docs/lxmf.md`, and run the live tests.
4. Edit `src/DotLXMF/version.json` to the new upstream number; the build height resets.

## Licensing

This project is a derivative of Reticulum and is released under the Reticulum License, which adds conditions MIT lacks. Do not relicense, dual-license or remove copyright notices. Contributions are accepted under the same license.
