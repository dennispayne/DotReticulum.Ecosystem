# NomadNet

Native .NET terminal/daemon application modelled on [NomadNet](https://github.com/markqvist/NomadNet), targeting upstream **NomadNet 1.4.4**, built on the [`LXMF`](../LXMF) library and DotReticulum.

- **Kind:** NativeAOT executable `nomadnet`.
- **Status:** partial: hosting/propagation, conversations, Micron rendering, forms and RRC groups exist with recorded evidence; complete migration/configuration parity and some platform boundaries remain open. Not a full application-parity claim.

**Full reference, contract inventory and evidence: [docs/nomadnet.md](../../docs/nomadnet.md)**

```sh
dotnet build src/NomadNet/NomadNet.csproj -c Release
dotnet publish src/NomadNet/NomadNet.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
nomadnet --help
```

Use `linux-x64` for Linux. The C# application uses original code and UI, not copied upstream assets.

> Experimental, LLM-built and unofficial; not the work of the Reticulum project. See the [root README](../../README.md).
