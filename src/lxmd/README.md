# lxmd

Native .NET counterpart of upstream `lxmd`, the LXMF router and propagation-node daemon from [LXMF](https://github.com/markqvist/LXMF) **1.2.1**, built on the [`LXMF`](../LXMF) library.

- **Kind:** NativeAOT executable `lxmd`.
- **Status:** implemented software surface with bounded evidence: published-binary process cases pass on Windows and Linux; status rendering deliberately differs from upstream's terminal layout.

**Full reference: [docs/lxmf.md, daemon and CLI inventory](../../docs/lxmf.md#daemon-and-cli-inventory-utilitieslxmdpy)**

```sh
dotnet build src/lxmd/lxmd.csproj -c Release
dotnet publish src/lxmd/lxmd.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
lxmd --help
```

Use `linux-x64` or `osx-arm64` for other platforms. On Windows, NativeAOT needs the MSVC tools and `vswhere.exe` on `PATH`.

> Experimental, LLM-built and unofficial; not the work of the Reticulum project. See the [root README](../../README.md).
