# Sideband

Native .NET desktop client modelled on [Sideband](https://github.com/markqvist/Sideband), targeting upstream **Sideband 2.2.0**, built on the `LXMF` and `LXST` libraries and DotReticulum.

- **[`Sideband.Core`](Sideband.Core):** UI-free library with the protocol and application logic, testable without a GUI.
- **[`Sideband.Desktop`](Sideband.Desktop):** Avalonia NativeAOT GUI producing the `sideband` executable.
- **Status:** partial Windows/Linux desktop client (messaging, propagation, media/paper, telemetry, maps, LXST calls); complete migration, plugin contracts and general Python execution remain unresolved, and Android/mobile is deferred.

**Full reference, contract inventory and evidence: [docs/sideband.md](../../docs/sideband.md)**

```sh
dotnet build src/Sideband/Sideband.Desktop -c Release
dotnet publish src/Sideband/Sideband.Desktop -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
```

Use `linux-x64` for Linux. The C# application uses original code and UI, not copied upstream assets.

> Experimental, LLM-built and unofficial; not the work of the Reticulum project. See the [root README](../../README.md).
