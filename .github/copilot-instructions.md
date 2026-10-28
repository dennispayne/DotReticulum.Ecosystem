# DotReticulum.Ecosystem agent instructions

You are building the **Reticulum-ecosystem layer** in native C# 13 / .NET 10, on top of [DotReticulum](https://github.com/dennispayne/DotReticulum) (the .NET Reticulum Network Stack, included here as the git submodule `external/DotReticulum`). The two current goals are:

1. **LXMF**: bring the existing partial LXMF implementation to high-fidelity feature and wire parity with upstream LXMF **1.2.1**.
2. **LXST**: implement LXST (Mark Qvist's Reticulum signal/voice transport) from scratch, with the same standard of parity against its pinned upstream.

Work **autonomously and continuously within the requested scope**. Complete the requested project without milestone confirmation prompts, then stop before unrequested work. Ask the user only when blocked on something that cannot be resolved from the repo, the web, or the upstream source (for example, missing credentials or hardware). A request to "do LXMF and LXST" means finish both with evidence, not to hand back after an intermediate milestone.

## Project framing and disclosure

This is an experimental, LLM-built project. It is **not** an official, endorsed or community-recognized LXMF/LXST/Reticulum implementation and must never be presented as the work of the Reticulum project or its maintainer. Keep the README warning banner, the Reticulum License (`LICENSE`) and its copyright notice intact; do not relicense. Compatibility claims are exactly as strong as the recorded evidence: unit vectors and loopback tests are **not** live interoperability. A project listed as "not started" is an idea, not a commitment; do not change its status in the README until work exists, and never claim parity that is not demonstrated.

The target is wire and behavioral parity with the pinned Python reference, not a line-by-line translation. Use idiomatic, trimming/NativeAOT-safe .NET: async I/O, cancellation, bounded resource use, explicit ownership, `Span<T>`, `System.Buffers.Binary`, `Channels`. No reflection-based serialization or dynamic code generation. Do not copy Python's module layout or threading model.

## Start / resume protocol (every session)

1. `git status`, `git log --oneline -20`, read `README.md`, `CONTRIBUTING.md`, `docs\*.md` and the existing code. Finish or fix uncommitted work, then commit it.
2. Run `git submodule update --init`, then `dotnet build DotReticulum.Ecosystem.slnx -c Release` and `dotnet test DotReticulum.Ecosystem.slnx -c Release`. Fix any red baseline before adding features.
3. Never trust a checkbox, doc claim or README table over code and tests. If they disagree, fix the claim.
4. Re-run this protocol after any interruption, context compaction or new session.

## Repository layout and rules

```
src\LXMF            library (AssemblyName/namespace conventions as present)
src\LXST            library (create)
src\Telemetry       .NET-only tool; out of scope here, keep it building and passing
tests\LXMF.Tests    xUnit
tests\LXST.Tests    xUnit (create)
tests\Interop          reference_environment.py (pins), thin Python adapters
docs\<project>.md      one reference doc per project (present-state, not history)
external\DotReticulum  submodule; the Reticulum stack
```

- Solution file is **`DotReticulum.Ecosystem.slnx`**. **Edit it by hand to list only this repo's projects.** `dotnet sln add` pulls the submodule's projects into the file; remove any such entries.
- Each project has its own `version.json` (Nerdbank.GitVersioning): `<upstream release>.<height>` (for example `LXMF` is `1.2.1.<height>`). The first three components are the upstream release, set by hand, and change only when the target upstream changes. Set `LXST` to the upstream release actually targeted. A version names the target, not completed parity.
- `.editorconfig`, nullable warnings as errors, file-scoped namespaces, 4-space indent. `IsAotCompatible`/`IsTrimmable` stay true for non-test projects. Executables set `PublishAot=true`; library projects must still pass the trim/AOT analyzers.
- **Do not edit files under `external\DotReticulum` in place.** If the stack lacks something you need (a transport behavior, a link/resource option, an API), implement it in the DotReticulum repository with its own tests, commit and push it there, then bump the pin here (see CONTRIBUTING). If you can use the existing public API, prefer that. Record any stack gap you hit in the project's doc.
- Language ownership: C# implements and verifies; PowerShell 7 operates the workflow and CLI end-to-end tests; Python only bridges to the upstream reference (thin adapters for actual API calls, callback observations, actual-import checks, independent vector generation, disclosed shims). Never replace the reference with our own implementation, and never embed Python in PowerShell strings.
- Executable names match their upstream names (for example `lxmd`, not `dotlxmd`). The telemetry tool already follows this (`telemetry`).

## Evidence rules (non-negotiable)

- **Never invent a wire format, constant, default or behavior.** Read the upstream Python source at the pinned revision, write down the exact format, then code. If you are unsure, execute the upstream code to produce evidence.
- Generate reference vectors **by executing upstream Python** (never by hand, never from your own C# output). Store the generator next to the tests (`Generate*.py`) and record the upstream revision and method in the test file. Record the actual upstream SHA (and source-file SHA-256 where relevant) for new vectors.
- Verify against a **live unmodified Python node** in both directions, including direct links, multi-hop paths via a Python transport node and a .NET transport node, packet loss, retries, teardown and recovery, with multi-megabyte payloads where the protocol has transfers. Unexercised topologies and failure paths must be recorded as such.
- The live reference environment is pinned in `tests\Interop\reference_environment.py` (RNS `a29bfb51c468945c96a1ee22cae0114e993e2391` = 1.5.7, LXMF 1.2.1). Use an isolated Python venv, verify the actual imported module paths and versions, and never let `PYTHONPATH` or an installed package silently select other source. Add the LXST pin there (and to the harness checks) when you start LXST, with the exact upstream commit you read. Keep the RNS pin identical to the submodule's copy of the same file. Disclose any reference-side shim and which unmodified-upstream claim it prevents.
- Distinguish wire errors from peer configuration limits and authorization/rejection policy (for example `lxmd`'s `delivery_transfer_max_accepted_size`). Record non-default peer settings used in a test. Delivery-endpoint interoperability, interaction with `lxmd`, and propagation-node synchronization are **separate capabilities**; one does not prove another. If a daemon does not originate messages, use a suitable client to verify the reverse direction rather than claiming bidirectional coverage from a one-way test.
- An unsupported test mode must explicitly **skip with a reason or fail**, never return early and appear to pass. Live tests are opt-in (`DOTRETICULUM_LIVE=1`, `DOTRETICULUM_PYTHON`, `DOTRETICULUM_UPSTREAM`; add equivalents for LXST).
- Security: zero secret buffers (`CryptographicOperations.ZeroMemory`), constant-time comparisons, never log or commit private keys, credentials or captured private traffic. Callback exceptions are reported through the logging/error mechanism with context; one failing observer must not disable core processing, and core processing must run with no subscriber registered (test that path).
- Lifecycle: explicit start, asynchronous disposal, bounded queues, cancellation and disposal-race tests, continued service after a peer fails.

## Working loop (per item)

1. **Define and research**: write observable completion criteria. Read the upstream source and docs for the item; separate required behavior from optional extensions and upstream placeholders.
2. **Vectors first**, then **implement** the smallest complete slice with no warnings.
3. **Validate**: unit tests (including malformed input, boundaries, failure paths), then live interop. Iterate until green; if an approach fails, try another rather than weakening a test.
4. **Review and commit**: run the full test suite and the NativeAOT publish check for any executable, update the docs and README status honestly, commit a coherent verified slice (explicit paths; trailers below). Check an item off only when every criterion is met.

Use `/fleet` only for genuinely independent workstreams (for example fixture generation versus a separate project), with explicit file ownership and required evidence per handoff. Keep tightly coupled wire-format work with one agent.

## Test categories

Every test class carries `[Trait("Category", "Unit" | "Integration")]`; individual live methods carry `[Trait("Category", "Live")]` and skip unless live mode is enabled. `Unit` means in-process and deterministic (no sockets, subprocesses or external tools); `Integration` uses real OS resources. Filter with `--filter "Category=Unit"`. Partial classes need the trait on one declaration. Put each case in one place; do not duplicate cases across projects.

## Goal 1: LXMF to LXMF 1.2.1 parity

Current state (see `docs\lxmf.md`): message format, signing, stamps, opportunistic and direct delivery, delivery proofs, retry and duplicate suppression are implemented and live-verified against Python `LXMRouter` and a real `lxmd` delivery destination. **Not implemented**: propagation-node client and synchronization, propagation delivery/peering, tickets, `lxmf.delivery` ratchets, an `lxmd` equivalent, and several public behaviors not yet inventoried.

Do this in order:

1. **Inventory** every public behavior, configuration option, persistence file, message field, stamp/ticket rule, announce data format, delivery method and CLI/daemon option in upstream LXMF 1.2.1 (`LXMF/*.py`, `LXMF/Utilities/lxmd.py`). Write the inventory into `docs\lxmf.md` with each contract marked implemented / partial / missing / unverified / not-applicable / upstream-placeholder, and source file references. Audit the existing implementation against it; do not assume it is complete.
2. Implement the missing contracts, vectors first: `LXMessage` details not yet covered (fields, attachments, image/audio field conventions, paper messages and URIs if present upstream, signature/stamp/ticket validation), delivery-destination ratchets, tickets, propagation (client messaging, sync/retrieval, node announce, peering, stamp costs, limits), `LXMPeer`, persistence and recovery after restart, and `LXMRouter` configuration parity.
3. Implement an **`lxmd`-equivalent executable named `lxmd`** (NativeAOT) with upstream's CLI options, configuration file, and behaviors, as far as the inventory marks required. Verify with Pester against the Python `lxmd` and Python clients, both directions, including a propagation node round trip (.NET node syncing from Python propagation node and the reverse), restarts and persisted state.
4. Add or extend the live tests for each new capability; keep existing LXMF tests passing.

Definition of done: every inventoried contract has a status with evidence or an explicit documented deferral; live interop passes in both directions for delivery, propagation sync and `lxmd`; the README and `docs\lxmf.md` state exactly what is and is not verified.

## Goal 2: LXST from scratch

LXST is **not yet read or inventoried here, and nothing about its wire format, codecs or API may be assumed from memory**. Start by reading upstream (`https://github.com/markqvist/LXST`; also check whether the Reticulum network hosts a primary copy, and say which you used). Pin the exact commit you read.

Do this in order:

1. **Inventory** upstream's public behavior: signalling and call setup over Reticulum, packet/frame formats, codecs and their parameters, mixing/filters/pipelines, sources/sinks, profiles, network transport details (links, packets, resources, quality/latency adaptation), command-line utilities and examples, platform/audio-device assumptions, and external native dependencies. Write it to `docs\lxst.md` with status per contract and source references, and mark upstream placeholders as such rather than inventing behavior.
2. **Architecture**: put signalling, framing, codec contracts and the network session logic in a platform-independent `LXST` library that is testable with no audio hardware. Put audio devices, codecs requiring native libraries, and any capture/playback behind small interfaces so they can be faked in tests. Decide codec strategy from the inventory: prefer a managed or well-maintained AOT-friendly implementation that reproduces the codec's bitstream exactly; if a native library is unavoidable, isolate it, document the platform matrix and its trimming/AOT impact, and do not claim platform support that is not verified. A decoder that does not match the reference bitstream is not parity.
3. Implement vectors first (frames/codec round trips generated by running the upstream codecs), then the library, then the session layer, then any executables upstream ships (keep upstream executable names).
4. Live-verify against the actual Python LXST: call setup and teardown, audio frames in both directions with the real codecs, loss and latency, and behavior across a transport hop. Audio quality or device I/O that cannot be verified without hardware must be documented as unverified, not assumed.

Definition of done: documented inventory with statuses and evidence; a library that interoperates with Python LXST over real Reticulum in both directions for the contracts marked implemented; AOT publish and run of the executables; `README.md` project table updated from "Not started" to the true status.

## Docs, README and versioning

- Keep `README.md` (project table, Compatibility table, Versioning) and `docs\<project>.md` as **present-state reference**, not chronology. Update them in the same commit as the behavior. When later work resolves a limitation, replace the stale statement.
- Contributor docs stay in sync with reality; `CONTRIBUTING.md` already describes the submodule bump and live-test environment.
- Do not maintain duplicate trackers or notes files. Do not create planning markdown unless asked.

## Commands

```sh
git submodule update --init
dotnet restore DotReticulum.Ecosystem.slnx
dotnet build DotReticulum.Ecosystem.slnx -c Release
dotnet test DotReticulum.Ecosystem.slnx -c Release
dotnet test DotReticulum.Ecosystem.slnx -c Release --filter "Category=Unit"
dotnet publish src/<Exe>/<Exe>.csproj -c Release -r <rid> -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
```

`<rid>` is `linux-x64`, `win-x64` or `osx-arm64`. On Windows, NativeAOT needs MSVC tools and `vswhere.exe` on `PATH` (`${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer`); keep toolchain setup and publish in the same process. A published binary must run `--help` and execute a representative feature flow, not just start. Record which features actually ran.

Releases are tag-driven (`tools\new-release-tag.ps1`, `.github\workflows\release.yml`); extend the release workflow when a new executable ships, and **never push a release tag or dispatch the release workflow unless asked**. Do not add heavy workflows that run on every commit; the repo deliberately has no CI beyond release until asked.

## Git hygiene

- Commit coherent verified slices with explicit paths. End every commit message with these trailers:

```
Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
Copilot-Session: <current session id>
```

- Never force-push or rewrite history. LF to CRLF warnings are harmless.
- The submodule pin is part of the commit: bump it only after the build and tests pass against the new DotReticulum commit.
- Never comment `@dependabot rebase` on grouped Dependabot PRs; it spawns a PR per package. Dependabot here is NuGet only.

## Reporting

When a goal completes, summarize concisely: what is implemented, what evidence exists (exact commands and results, upstream SHAs, live topologies exercised), what is deferred or unverified, and any gaps found in DotReticulum. Do not describe work as complete while required capabilities remain missing or unverified.
