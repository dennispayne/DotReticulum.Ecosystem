# LXST

Native .NET library implementing [LXST](https://github.com/markqvist/LXST), the Reticulum signal and voice transport, targeting upstream **LXST 0.5.4** (pinned in `tests/Interop/reference_environment.py`).

- **Kind:** library (`LXST`): signalling, framing, codecs, pipelines and telephony; audio devices and native codecs sit behind fakeable interfaces. The telephone utility lives in [`src/rnphone`](../rnphone).
- **Status:** inventoried Windows/Linux software contracts implemented and verified against actual Python calls; physical audio/RF/board behavior, other platforms and exhaustive parity remain unverified.

**Full reference, contract map and evidence: [docs/lxst.md](../../docs/lxst.md)**

```sh
dotnet build src/LXST/LXST.csproj -c Release
dotnet test tests/LXST.Tests -c Release --filter "Category=Unit"
```

Opus and Codec2 use native libraries; see the [native codec and platform matrix](../../docs/lxst.md#native-codec-and-platform-matrix).

> Experimental, LLM-built and unofficial; not the work of the Reticulum project. See the [root README](../../README.md).
