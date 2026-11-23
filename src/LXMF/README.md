# LXMF

Native .NET library implementing [LXMF](https://github.com/markqvist/LXMF) (Lightweight Extensible Message Format), targeting upstream **LXMF 1.2.1** on top of [DotReticulum](https://github.com/dennispayne/DotReticulum).

- **Kind:** library (`LXMF`, namespace `LXMF`); the daemon lives in [`src/lxmd`](../lxmd).
- **Status:** inventoried core software implemented and Windows/Linux live-verified; optional omissions, upstream defects and platform boundaries remain explicit. Not an exhaustive-parity claim.

**Full reference, contract inventory and evidence: [docs/lxmf.md](../../docs/lxmf.md)**

```sh
dotnet build src/LXMF/LXMF.csproj -c Release
dotnet test tests/LXMF.Tests -c Release --filter "Category=Unit"
```

> Experimental, LLM-built and unofficial; not the work of the Reticulum project. See the [root README](../../README.md).
