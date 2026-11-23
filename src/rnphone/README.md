# rnphone

Native .NET counterpart of upstream `rnphone`, the Reticulum telephone utility from [LXST](https://github.com/markqvist/LXST) **0.5.4**, built on the [`LXST`](../LXST) library.

- **Kind:** NativeAOT executable `rnphone`.
- **Status:** console entry point and controls implemented in software; physical audio, keypad/LCD hardware and presentation limits mean this is not full CLI/daemon compatibility.

**Full reference: [docs/lxst.md, utilities, configuration and persistence](../../docs/lxst.md#utilities-configuration-and-persistence)**

```sh
dotnet build src/rnphone/rnphone.csproj -c Release
dotnet publish src/rnphone/rnphone.csproj -c Release -r win-x64 -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
rnphone --help
```

Use `linux-x64` for Linux. Real Opus/Codec2 audio needs the native libraries described in the [native codec and platform matrix](../../docs/lxst.md#native-codec-and-platform-matrix).

> Experimental, LLM-built and unofficial; not the work of the Reticulum project. See the [root README](../../README.md).
