# Testing and compatibility evidence

The ecosystem uses the stack's testing standard: independently executed reference
vectors, real pinned Python peers, explicit compatibility boundaries, process and
NativeAOT acceptance, and unioned production-code coverage. A successful command
means its selected checks passed, **not** that the ecosystem has full upstream
parity. Missing features, skipped cases and untested hardware remain visible.

## Baseline provenance

Parent independently replays `artifacts\tests\lxmf-retry-fidelity\ParentAccept.ps1`
on Windows/Linux: byte-identical regenerated reference vectors, all 183
live-enabled LXMF cases, all six published `lxmd` cases and the published
default-refusal/recovery probes pass per host. The direct and Python-hop native
probes assert five charged attempts, four closed links and zero pending links
before fresh healthy delivery in both directions. The managed live matrix also
includes a .NET transport hop. Exact source/native hashes and boundaries are in
[LXMF's reference](lxmf.md#outbound-link-retry-accounting); the accepted native
binaries embed `63c3710`, not a later integration HEAD. Propagated refusal has
branch-vector coverage only. The separate pinned-primary Sideband callback-lock
failure remains unresolved; no stack change or patched reference masks it.

Parent independently accepts frozen Nomad profile v4 on Windows/Linux with
`Invoke-FrozenAcceptance.ps1 -Manifest <frozen-win/linux-191-v4>\manifest.json`:
five successive mandatory native runs and a separate recorded-case run pass per
host, including messages both ways with the
unchanged full primary **after** clean/abrupt restart of the imported identity.
The 29-byte direct-TCP application exchange is distinct from v2's 25-byte
pre-restart Python LXMF endpoint flow. Current-source Nomad regression passes
180/11 explicit skips on Windows and 187/4 on Linux with isolated outputs;
all ten frozen owned hashes match and 36 native PDB source checks per host are
retained. Exact binary hashes/commands, profile bounds and remaining RRC/config/
router gaps are in [NomadNet's reference](nomadnet.md). The preserved v3 failure
exposed timestamped primary diagnostics before JSON events; v4 retains those
diagnostics with bounded event waits while malformed JSON/schema errors still
fail. No production binary or message assertion changed for that reader fix.

The announce-profile migration gate
`tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-announce-import-unified -OutputDirectory
artifacts\tests\fleet-announce-import-unified` passes **2,395 managed cases,
123 explicit skips and62 Pester tooling cases**, with strict catalog aggregation
and merged coverage. Separate Windows/Linux actual-primary SQLite-WAL migration
passes15/15 each; published native CLI/import/restart/render selections pass7
with one unrelated missing-map-profile skip each. Imported delivery costs remain
unknown without the router cache; offline migration is not live interoperability.
Concurrent LXMF/Nomad changes require their own frozen-slice acceptance.

The announce-stream integration gate
`tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-announces-unified -OutputDirectory
artifacts\tests\fleet-announces-unified` passes **2,380 managed cases,
122 explicit skips and62 Pester tooling cases**, with strict catalog aggregation
and merged coverage. This shared-worktree run includes concurrently developed
LXMF retry and Nomad profile tests; it does not independently accept those
slices. Separate actual-primary announcement acceptance passes14/14 on each
host, direct/Python-hop/.NET-hop, and fresh Windows/Linux strict NativeAOT
announcement rendering passes independently; exact boundaries are in
[Sideband's reference](sideband.md#announce-stream).

Parallel builds use distinct `-ArtifactsPath` directories. A normal-output
solution build encountered `MSB3026` while another owner's live `testhost` held
`LXMF.Tests\bin` DLLs; the isolated gate above succeeds without killing that
process, disabling tests or suppressing warning-as-error policy.

The integrated directory-import/retry gate
`& .\tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-directory-retry-unified -OutputDirectory
artifacts\tests\fleet-directory-retry-unified` passes **2,341 managed cases,
117 explicit skips and62 Pester tooling cases**, including strict catalog
aggregation and merged production coverage. Parent independently replays both
frozen Nomad directory recipes: Windows166/10 managed skips, Linux172/4,
Native4/4 and actual-primary offline DirectoryImport1/1 on each host. This
accepts atomic saved-directory/ignore-policy import, ownership/conflicts and
restart, not other profile migration or a new network topology.

Directory-import regeneration now writes canonical LF and verifies normalized
source bytes against the exact pinned Git blobs; separate receipts retain each
host's raw working hashes and imported paths. Actual Windows/Linux reference
outputs are byte-identical (5,929 bytes, SHA-256
`ebc92a10c3bda11971f79aa048ca322a7fea5725815d142865b7763791a4a3ee`).
`dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --filter
FullyQualifiedName~NomadNetDirectoryImport` passes seven cases per host.
The original frozen CRLF snapshot and its native evidence remain unchanged;
canonical regeneration does not relabel those original bytes.

The separate strict initially-propagated retry operator remains **failed** on
both hosts: six passes/two enabled-primary failures/no skips out of eight
required unique cases. All three native-origin rows and the primary disabled
row pass. Independent lock/restart probes reproduce the pinned LXMF/Sideband
callback-lock cycle; the tests retain their failure assertions and deadlines.
Disabled live cases in the default gate are not accepted retry interoperability.
Exact topology, reference digests, native-origin versus published-native limits
are in
[Sideband's reference](sideband.md#initially-propagated-retry-and-pinned-callback-lock-defect).
The separate native attempt-accounting discrepancy is corrected and independently
accepted in the LXMF gate above.

The serviced baseline selects SDK **10.0.403**, runtime and NativeAOT compiler
**10.0.14**. A fresh Linux retry restore exposed `NU1903` on compiler10.0.13
([GHSA-p3jp-9v83-76w9](https://github.com/advisories/GHSA-p3jp-9v83-76w9),
CVE-2026-111497). `global.json` now requires403 instead of relying on Windows'
already-installed newer SDK; Linux was updated side-by-side from the official
SHA-512-verified archive. No audit suppression, package downgrade or submodule
edit was used. Existing native executable evidence retains its actual compiler
version and is not relabelled as rebuilt.

`dotnet build DotReticulum.Ecosystem.slnx -c Release -warnaserror --nologo
-v quiet` passes with zero warnings/errors on Windows and Linux (Linux adds
`--artifacts-path /home/dennis/fleet-servicing-baseline`). Full non-opt-in tests
on these serviced builds pass **2,341/117 explicit skips on Windows** and
**2,346/112 on Linux**: `dotnet test DotReticulum.Ecosystem.slnx -c Release
--no-build --logger trx --results-directory <results> --nologo -v quiet`,
with the same Linux artifacts path. The default corpus includes concurrently
developed Nomad directory-import tests, not independent acceptance of that slice.
Actual platform-specific skips differ; neither count establishes compatibility.
Fresh strict Linux Sideband NativeAOT publication selects compiler10.0.14 and
its `Native,Discovery` Pester selection passes6/one unrelated profile skip;
the executable/hash and exact evidence boundary are in
[Sideband's reference](sideband.md#cached-interface-discovery). Hosted
Build/Test results below are unchanged and do not accept these local changes.

Independent serviced Linux native reacceptance also verifies fresh `lxmd`,
`rnphone` and `telemetry` hashes and compiler10.0.14 assets. From native storage,
`Invoke-Pester -Path <repo>\tests\Pester\Lxmd.Tests.ps1 -Tag Native` passes6/no
skips, and the equivalent `Rnphone.Tests.ps1` passes7/no skips with actual
pinned Python and owned virtual audio. Telemetry's published daemon/get/watch
loopback flow and graceful SIGTERM pass independently; it has no Python
counterpart. Strict publication uses `dotnet publish <project> -c Release -r
linux-x64 --artifacts-path /home/dennis/fleet-servicing-native
-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
-warnaserror -v quiet`. Parent's non-republishing recipe and machine-readable
case/hash results are under `artifacts\tests\fleet-servicing-native`; selected
binary paths/hashes are in the three project reference docs. Old one-hour
phone/native evidence is retained, not relabelled as a serviced rerun.

The integrated discovery/conversation-image gate
`& .\tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-discovery-nomad-final-unified -OutputDirectory
artifacts\tests\fleet-discovery-nomad-final-unified` passes **2,334 managed
cases,117 explicit skips and62 Pester tooling cases**, including strict
catalog aggregation. It includes the shared-owner discovery regression and
unaccepted retry diagnostics; disabled live modes are not accepted
interoperability. Subsequent discovery smoke restoration/centering refinements
have separate focused and published native acceptance. Parent independently
repeats both frozen Nomad conversation-image recipes and Linux mixed
propagation/restart/dropped-Resource acceptance; that slice is committed.
Discovery's actual-primary signed-announcement cases and original published
renderer/restart cases are separate evidence in
[Sideband's reference](sideband.md#cached-interface-discovery).

The preceding `fleet-discovery-nomad-unified` attempt remains a failure: the
existing headless voice case timed out while waiting for persisted peer trust.
Its exact cause is not established; neither its five-second wait nor the
two-second hangup assertion was relaxed. Both the subsequent isolated full
Release run and strict coverage run pass. Shared-client discovery acceptance
also initially failed because management RPC decodes maps differently from
local management; accepting both typed pair collections fixes the actual
boundary, and the unchanged blackhole/pruning assertions pass on both hosts.
No hosted workflow was dispatched. Current hosted Build remains successful
at run `41502113530`; latest Test remains `41643993895`, with the distinct
artifact-producing commit described below. Neither accepts these local commits.

The remote-transport gate
`& .\tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-remote-transport-unified -OutputDirectory
artifacts\tests\fleet-remote-transport-unified` passes **2,326 managed cases,
108 explicit skips and 62 Pester tooling cases**, including strict catalog
aggregation. It includes the frozen remote follow-up and unaccepted Nomad
conversation-image work; it does not accept that work or later discovery changes.
The separate actual-primary remote operator passes 1,484/no skips on each host,
including all thirteen required command/admission/propagation/reply/cancellation
cases. Direct failure uses disclosed public handshake/teardown callbacks, not a
silent-blackhole acceptance claim. Reference execution, propagation-node storage
versus endpoint delivery, exact topologies and remaining retry/timing boundaries
are recorded in [Sideband's reference](sideband.md#built-in-commands-and-incoming-admission).
No hosted workflow was dispatched.

The integrated map/object gate
`& .\tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-map-objects-unified -OutputDirectory
artifacts\tests\fleet-map-objects-unified` passes **2,312 managed cases,
106 explicit skips and 62 Pester tooling cases**, with strict compatibility
aggregation and merged coverage for all eight production assemblies. It includes
the accepted Nomad node-configuration slice and frozen remote-transport follow-up
code; its absent live modes are not application acceptance. Parent separately
reproduces Nomad's exact-source guarded Windows/Linux configured peering/restart
and Linux mixed propagation/loss flows. Windows/Linux exact-source map/object
checks pass130 cases plus11 profile-option/rollback cases per host, and strict
published native/GUI checks pass11/two unrelated profile skips per host.
Reference hashes, discarded wrong-checkout/zero-selection invocations, exact
native hashes and topology boundaries remain in the project reference docs.
This is local evidence, not a new hosted run or full application-parity claim.

The local remote-command, admission and desktop lifecycle gate
`& .\tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-remote-ui-unified-final2 -OutputDirectory
artifacts\tests\fleet-remote-ui-unified-final2` passed **2,200 managed cases,
99 explicit skips and 62 Pester tooling cases**, with merged coverage for all
eight production assemblies. It includes committed LXST drain/AGC and Nomad
inline-media behavior, integrated remote backend/UI and the then-uncommitted
Nomad node-configuration slice. That slice's managed results are not final
application acceptance. Actual-primary remote command/admission acceptance
separately passes on Windows/Linux, and final strict native remote/paper/voice
flows pass 17/five skips and 19/three skips respectively; exact commands,
source revisions, executable hashes and topology boundaries are in
[Sideband's reference](sideband.md#built-in-commands-and-incoming-admission).

The preceding `fleet-remote-ui-unified-final` run passed its managed/tooling
checks but failed strict aggregation: two new catalog selectors used filenames
instead of the actual partial class/method names. Exact-method selectors now
discover the six real live cases; absent prerequisites remain skips, not passes.
No tests or aggregation checks were disabled. A deterministic store-gate test
also reproduced stale paper-operation feedback before its correction; pending
status now precedes asynchronous work without changing completion assertions
or deadlines. Separate current Nomad mixed propagation restart acceptance
passes using observed online/active route readiness, not a timed sleep; prior
offline-route failures are retained and controlled stale-route recovery is
not claimed.

Earlier `fleet-map-profile-unified` failed the original desktop voice test's
post-hangup five-second wait. The subsequent
`nomadnet-inline-integrated-final` gate also failed the new bounded-dial
regression's immediate concurrent-admission requirement. Both failures are
retained, not converted to passes by rerunning. The corrected client owns one
pending dial from admission, links cancellation before lifecycle acquisition,
and lets hangup cancel/join the caller-visible task. An additional regression
reproduced disposal-before-setup as `ObjectDisposedException` rather than
cancellation; the corrected run-token ownership passes caller/disconnect/disposal
cases. Original two-second cancellation assertions remain intact. Failure-only
desktop diagnostics remain; the precise cause of the earlier intermittent UI
timeout is not proven.

Independent map/voice acceptance passes 70 focused managed cases/one explicit
generated-profile skip per host, four actual-primary Sideband shared-client call
cases per host, and strict published Windows/Linux native/GUI flows
(12/four skips and 14/two skips respectively). Linux reference calls use an owned
isolated virtual PulseAudio server; Windows uses the disclosed synthetic device
factories. Exact binaries, commands and unavailable modes are recorded in
[Sideband's reference](sideband.md). These are local results, not new hosted
evidence or physical-audio acceptance.

At `daa304d680d67cf6a8fa330f65f83b2be2b0fc2c`, local
`git submodule update --init`, Release solution build and full non-opt-in test
run passed; the build had zero warnings/errors. The xUnit 3.2.2 pin in
Sideband.Tests is required by Avalonia.Headless.XUnit 12.1.4's discovery API;
upgrading it alone to xUnit 4 is not compatible.

Hosted scheduled Test run `41643993895` completed successfully and its downloaded
`test-summary-1` records **producing commit `daa304d`**, despite workflow head
`0e49bee`. It passed selected Windows/Linux managed/native-tooling checks, not
Python-peer, GUI or physical-hardware gates. Hosted run `41504361342` failed
Windows `IdlePropagationLinks_ExpireStrictlyAfter180SecondsAndReconnect` at
producing merge commit `2f079f0`; a later green run does not erase that failure
or prove absence of a race. Build run `41502113530` built that merge commit,
including macOS build/help-only acceptance. Workflow head SHAs and badge color
alone are not source-provenance evidence. No workflows were dispatched to
validate subsequent unpushed local changes.

The integrated local gate
`& .\tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-voice-unified -OutputDirectory
artifacts\tests\fleet-voice-final-v3` covers Sideband voice/media/router/schedule migration,
NomadNet browser/directory and the synchronized LXST 0.5.4 reference/catalog and release-tag
expectation. It passed: 722 managed cases and 61 Pester cases, with 81 managed
and 23 discovered Pester cases explicitly skipped for absent prerequisites.
Fourteen additional catalog availability records identify unavailable native suites.
That frozen voice-era corpus included isolated telemetry/extension reference
foundations; it did not cover the later bounded raster maps and manual telemetry
desktop controls. Their separate Windows/Linux focused and published-rendering
acceptance, and the explicit one-hour frozen native voice evidence, are recorded
in [Sideband's reference](sideband.md). None establishes arbitrary Python-plugin
compatibility or full telemetry application acceptance.
It retains TRX, per-case
compatibility evidence and merged coverage for all eight production assemblies.
Its absent live/native prerequisites remain explicit skips; the separately
selected published/real-peer application gates are recorded in each project's
reference doc. This local result is not hosted evidence or a complete-parity
claim.

The final native catalog check
`& .\tests\run-compatibility.ps1 -SkipManaged -Pester -OutputDirectory
artifacts\tests\fleet-voice-final-catalog-v2`, with the accepted Windows Sideband
binary selected, passed 66 Pester cases. Managed checks were not selected.
Excluded data-driven GUI cases retain their parameter values and remain explicit
skips, rather than producing a false unmatched-selector failure. Its two POSIX
signal skips are independently exercised on Linux; the Sideband reference doc
records the full published voice, import and rendered-GUI results.

The map/manual-telemetry gate
`& .\tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-maptelemetry-unified -OutputDirectory
artifacts\tests\fleet-maptelemetry-unified-v2` passed all five managed projects:
1,799 passed and 87 explicit skips, with coverage merged across all eight
production assemblies. Pester tooling passed 61 cases; absent selected
reference/native prerequisites remained explicit skips/availability records.
An earlier mutable-tree attempt failed while a new NomadNet test attribute was
temporarily nested incorrectly; the corrected source passed without removing
tests or relaxing assertions.

The separate selected Windows native/GUI catalog command
`& .\tests\run-compatibility.ps1 -SkipManaged -Pester -Gui -OutputDirectory
artifacts\tests\fleet-maptelemetry-native-catalog-v2` passed, using the accepted
map/manual-preview binary and separately installed codec DLLs on `PATH`.
Its initial run omitted those DLLs and failed both codec-call cases; supplying
the documented runtime prerequisites restored both directions without changing
the tests. The one-hour endurance mode remains an explicit skip in this short
catalog invocation; independently checked retained hour-long samples are
separate evidence, not silently replayed passes. These local runs predate the
subsequent repository-mirror desktop integration and are not hosted acceptance.

The integrated telemetry/map/repository source gate
`& .\tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-sideband-integrated-unified -OutputDirectory
artifacts\tests\fleet-sideband-integrated-unified` passed **2,082 managed cases,
87 explicit skips and 61 Pester tooling cases**, with unioned coverage for all
eight production assemblies. This includes the independently source-generated
appearance arithmetic correction and the shared bounded LXMF paper-envelope
helper; it does not include the subsequently staged Sideband paper backend.
The separate actual-primary telemetry operator passed 1,175 cases without skips
on each host, including direct/Python-hop reports, collector/restart/migration
and disclosed propagation-rejection recovery, as recorded in
[Sideband's reference](sideband.md#telemetry-application-exchange-privacy-and-migration).
Neither the managed aggregate nor those synthetic peer cases establishes
automatic/physical sensors, arbitrary impairment or complete upstream parity.

Strict final integrated Sideband NativeAOT publishes and actual selected
`Sideband.Tests.ps1,SidebandVoice.Tests.ps1 -Tag Native,GUI` flows passed on Windows
(11 cases/four skips) and Linux (13 cases/two skips). The Windows command
`& .\tests\run-compatibility.ps1 -SkipManaged -Pester -Gui -OutputDirectory
artifacts\tests\fleet-sideband-integrated-native-catalog` also passed with the
final binary selected and installed codec DLLs on `PATH`. The retained
one-hour frozen voice case is independently verified evidence, not a replayed
short-suite pass. Hosted Build/Test metadata was inspected again and remains at
the exact earlier runs/producing source boundaries above; no workflow was
dispatched and no local commit was pushed.

The paper/name/QR source gate
`& .\tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-paper-unified -OutputDirectory
artifacts\tests\fleet-paper-unified-final-v2` passes **2,117 managed cases,
89 explicit skips and 62 Pester tooling cases**, with all eight production
assemblies represented. Absent published/reference prerequisites are explicit
availability/skipped records; the paper suite is not mistaken for tooling.
Earlier attempts exposed and fixed paper test-output adapter packaging,
case-sensitive synthetic JSON seeding, native-suite prerequisite classification,
an inherited cleanup-variable collision and an invalid sealed Image override.
The cleanup regression proves failed setup preserves an unrelated owned
sentinel directory. Tests/coverage were not disabled to obtain the green gate.

Actual-primary paper cases independently pass on Windows/Linux with trust
enabled/disabled and real native/Python restarts. Shared QR/name filters pass
19 cases per host; primary QR pixels and independent Pillow/zxing-cpp decoding
remain separate from live application evidence. Strict published Sideband
paper URI/PNG export, overwrite/mode rejection and actual original QR drawing
pass on Windows/Linux. Paper rendering is observed at the positive-bounds
`DrawImage` call after scrolling the preview into view, not inferred merely
from a nonnull image source. Exact source/native commands and boundaries are
in [Sideband](sideband.md#paper-application-and-desktop-acceptance) and
[LXMF](lxmf.md). One unchanged headless voice five-second readiness timeout
under simultaneous native publication passed unchanged isolated/combined and
in the full gate; this is retained as intermittent-load evidence, not proof
that exhaustive timing is race-free. Hosted evidence remains the earlier
producing commits, not these unpushed local sources.

## Entry points

```powershell
pwsh -NoProfile -File tests\run-tests.ps1
pwsh -NoProfile -File tests\run-tests.ps1 -Pester
pwsh -NoProfile -File tests\run-tests.ps1 -Coverage -Pester
pwsh -NoProfile -File tests\run-tests.ps1 -Live -Pester
pwsh -NoProfile -File tests\run-tests.ps1 -Live -LxstLive -NomadNetLive -Pester -Gui
```

`run-tests.ps1` delegates to `run-compatibility.ps1`. Both run the complete
Release solution, write TRX, and generate `compatibility.json` and
`compatibility.md` in a fresh `artifacts\tests\compatibility-<unique-id>` directory.
Use `-OutputDirectory <new-path>` to choose another directory; existing
directories are rejected to prevent stale results being mixed into a new run.
`-ArtifactsPath <path>` isolates build outputs, particularly between Windows and
WSL. Put these build outputs under `artifacts\builds`; do not share intermediate
build outputs between operating systems. Language-server caches remain separate
under `artifacts\lsp`. Historical commands below retain their original paths;
old root `TestResults` evidence is now at `artifacts\tests`, and old direct
`artifacts\<run>` outputs are at `artifacts\builds\<run>`.

`-Pester` runs all ecosystem Pester suites, including release-tooling and testing
infrastructure regressions. It requires PowerShell 7 and Pester 5.7.1 or later.
Native suites use the explicitly selected published binaries, not a managed
fallback. Missing native prerequisites produce skipped evidence, not passes.
`-Gui` additionally selects the published Sideband window/renderer flow; without
it GUI evidence is explicitly unselected.

`-RequireComplete` exits nonzero unless every catalogued claim passes. It is
expected to fail while catalogued missing features and hardware evidence remain.
Ordinary runs still fail on build/test/discovery errors, malformed catalogs,
unmatched selectors in an executed runner, provenance failures or incomplete
production coverage. Failure reports are retained.

For quick targeted development checks, keep using the normal runners:

```powershell
dotnet test DotReticulum.Ecosystem.slnx -c Release --filter "Category=Unit"
pwsh -NoProfile -File tests\run-pester.ps1 -Path tests\Pester\TestingStandards.Tests.ps1
```

Every xUnit class carries `Category=Unit` (deterministic, in-process) or
`Category=Integration` (OS resources). Live methods additionally carry
`Category=Live`; their real-peer environment gates must skip explicitly.
Targeted checks do not replace the complete compatibility or coverage run.

## Catalog and aggregation

`tests\Compatibility\catalog.json` contains stable short `ClaimId` values
`C1`, `C2`, ... and `TestId` values `T1`, `T2`, ... . Preserve existing IDs when
adding coverage. A test mapping selects a runner, suite and wildcard-qualified
test name; xUnit theories retain their individual case outcomes. A mapping can
cover several cases, and cases may support more than one mapping without being
counted as additional executions.

The catalog is a scoped evidence index, **not** an exhaustive upstream API
inventory. Those inventories remain in `lxmf.md`, `lxst.md`, `nomadnet.md` and
`sideband.md`. The catalog deliberately includes unsupported Sideband features
and physical RF/audio boundaries without tests. Unmapped executed tests are
retained and counted separately; they do not silently become compatibility
claims.

Each claim records its source, boundary and implementation qualification.
Evidence labels mean:

| Label | Evidence |
|---|---|
| V | Stored vectors generated by executing the pinned reference |
| U | Local managed tests, including fakes and .NET loopback |
| L | An actual pinned Python peer; not merely vectors or native-to-native traffic |
| P | An actual subprocess or CLI flow |
| N | Acceptance using the explicitly supplied published native executable |
| H | Physical hardware acceptance; not synthetic silence or one identified board |

Combined labels name the kinds of evidence required by a mapping, not additional
passes. Native libraries used in-process are labelled separately from published
executables. A supplied binary's path and SHA-256 are recorded; a file hash alone
does not prove how it was built or establish publisher authenticity.

A mapping passes only if **all** its discovered cases pass. A claim passes only
if **all** its mappings pass. Any failure wins; passed plus skipped, unselected
or uncovered evidence is `Partial`, never `Pass`. All-skipped evidence is
`Skipped`; unexecuted runners are `Not run`; an unmapped claim is `Not covered`.
Unavailable-suite records are distinguished from actual discovered cases.
JSON retains individual outcomes, reasons, source artifacts, expected reference
pins, actual-import preflight output, native binary hashes, catalog hash, mode
flags, commands, platform, repository commit and dirty-worktree status.

## Real references and native modes

Use the isolated reference environments described in `.github/CONTRIBUTING.md`. Retrieve
upstream source using native `rngit` from the primary `rns://` locators in the
catalog; retain provenance and any seed disclosure. Local tests do not fetch or
change those checkouts. Git object consistency is not publisher authentication.

`-Live` requires `DOTRETICULUM_PYTHON`, `DOTRETICULUM_UPSTREAM` and
`DOTRETICULUM_LXMF_UPSTREAM`. Unlike the individual legacy tests' optional wheel
mode, the aggregate live runner requires the primary LXMF source checkout. It
constructs an isolated reference import path and executes
`reference_environment.py --require-lxmf` **before** starting live checks.
`-LxstLive` additionally requires `DOTRETICULUM_LXST_UPSTREAM` and verifies it
with `--require-lxst`. `-NomadNetLive` additionally selects the actual
NomadNet-node cases using `DOTRETICULUM_NOMADNET_SOURCE`; that adapter validates
the application revision at runtime. Both switches imply `-Live`.

The runner saves and restores reference import paths and live flags. Ambient
live flags do not enable tests accidentally. Device/library-specific opt-ins
remain explicit and are recorded: `DOTLXST_NATIVE=1` for installed native codec
tests, `DOTLXST_VIRTUAL_AUDIO=1` for provisioned Linux virtual audio, and
`DOTLXST_WINDOWS_AUDIO=1` for synthetic-silence Windows playback. These do not
prove physical audio quality or capture.

| Published application | Environment variable |
|---|---|
| lxmd | `DOTRETICULUM_LXMD_BINARY` |
| rnphone | `DOTLXST_RNPHONE_BINARY` (legacy `DOTRETICULUM_RNPHONE_BINARY` fallback) |
| nomadnet | `DOTRETICULUM_NOMADNET_BINARY` |
| sideband | `DOTRETICULUM_SIDEBAND_BINARY` |

Linux phone acceptance needs native Linux temporary identity directories with
owner-only permissions. Run from a native working copy rather than the mounted
Windows repository for that mode. Reference absence, unsupported platforms and
unselected modes are not interoperability passes.

## Production coverage

`-Coverage` uses Coverlet/OpenCover with serial project collection. It instruments
all eight repository production assemblies: `LXMF`, `LXST`, `telemetry`, `lxmd`,
`rnphone`, `nomadnet`, `Sideband.Core` and `sideband`. It excludes test projects,
submodule code and third-party assemblies. Missing reports or an uninstrumented
production module fail the run rather than disappearing from the denominator.
Assembly names are derived from the production projects, including their
case-sensitive names. Add a new assembly to `coverage.runsettings` when adding a
production project; the missing-module gate detects an omitted filter.

`coverage_summary.ps1` unions hits across reports using source-path/line
identity, and source-path/line/method/offset/path/end-offset branch identity.
A point hit by any suite counts as covered once. Duplicate attachment copies
and common code exercised by several test projects are not double-counted.
Percentages are **not** averaged. Zero branch points are `n/a`, not 100%.

```powershell
pwsh -NoProfile -File tests\coverage_summary.ps1 artifacts\tests\<run-directory>
```

Process-only native acceptance is not instrumented by the managed collector.
Consequently daemon/phone command-line coverage can be low even when their
separate native gates pass. Coverage measures exercised managed production code,
not compatibility or physical acceptance; no arbitrary threshold is used to
conceal these boundaries.

The Windows baseline collected with
`pwsh -NoProfile -File tests\run-tests.ps1 -Coverage -Pester -OutputDirectory TestResults\standards-final`
passed 239 xUnit cases with 45 opt-in skips and 33 Pester tooling cases.
The merged production baseline was 5,409/8,557 lines (63.21%) and
2,872/5,922 branches (48.50%); all eight modules were represented. This is a
non-live coverage baseline, not a new compatibility claim.

## Aggregate acceptance

The reference and binary variables above were explicitly provisioned for these
runs. Linux additionally selected `DOTLXST_NATIVE=1` and
`DOTLXST_VIRTUAL_AUDIO=1`, using the separately provisioned .NET SDK, PowerShell,
codec/audio dependencies and `zip` on `PATH`.

| Host | Exact runner invocation | Result |
|---|---|---|
| Windows | `pwsh -NoProfile -File tests\run-tests.ps1 -Live -Pester -Gui -OutputDirectory TestResults\standards-live-windows-final` | Passed: 268 xUnit cases, 17 skips; 51 Pester cases, 12 skips |
| WSL/Linux | `& .\tests\run-tests.ps1 -LxstLive -Coverage -Pester -OutputDirectory .\TestResults\standards-live-linux-final -ArtifactsPath .\artifacts\standards-linux` | Passed: 311 xUnit cases, 2 skips; 42 Pester cases, 14 skips; production coverage 6,137/8,557 lines (71.72%) and 3,262/5,922 branches (55.08%) |
| Windows | `pwsh -NoProfile -File tests\run-tests.ps1 -Pester -Gui -RequireComplete -OutputDirectory TestResults\standards-native-windows` | Correctly exited 1 solely for incomplete compatibility: all selected checks passed, but skipped/missing claims prevented a complete verdict |

These runs verified the catalog's pinned RNS/LXMF source imports on Windows and
RNS/LXMF/LXST imports on Linux. They exercised actual LXMF delivery, propagation,
tickets/ratchets and impaired Python/.NET transport paths; Linux also exercised
the actual LXST stream/telephone profiles, codecs and virtual-audio failure
recovery. Windows exercised the published daemon's six-case Python
delivery/control/propagation/restart suite and Sideband's native state and
rendered GUI flow. They did **not** select the NomadNet Python-node mode or Linux
published-application gates, and do not establish physical RF/audio acceptance.
The per-project documents retain their separately scoped earlier evidence.

The Windows daemon was rebuilt with
`dotnet publish src\lxmd\lxmd.csproj -c Release -r win-x64 --artifacts-path artifacts\standards-aot -o artifacts\standards-aot\publish -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true --verbosity quiet`
using MSVC in the same process. Publication passed; the resulting executable
passed both native CLI boundary cases and the live six-case suite above. Test
code is granted internal access only to verify CLI argument rejection before
network or identity creation; no production behavior was changed.

## Continuous integration

The Actions layout follows DotReticulum's workflow controls at
`c8d36a8b9f97b4f1bc08f0d1a9b533391d5940e3`, adapted without changing the stack pin.
`.github\workflows\build.yml` runs on relevant pushes and pull requests to `main`,
or manual dispatch. Documentation-only changes do not trigger Build;
submodule-pin and build-configuration changes do.

The Build matrix restores and builds the Release solution, then publishes
NativeAOT with trim/AOT warnings as errors. Windows and Linux publish all five
executables; macOS ARM64 publishes only `telemetry` and `lxmd`, matching their
release matrix. Each executable must run `--help`. A manifest records the source
commit, RID and SHA-256 of every non-debug runtime file; full native runtime
directories are retained, not just executables.

Build additionally packages its actual Release test outputs and restored
coverage collector into `managed.tar`, with a commit/RID/archive-digest manifest.
Trusted main builds attest both managed subjects and the native manifest in a
separate job that never executes downloaded artifacts. Signing the native
manifest binds every declared runtime-file digest; additional runtime files are
rejected. PR builds have no signing permissions.

`.github\workflows\test.yml` follows a successful Build automatically. Manual
dispatch accepts `build_run_id` or selects the newest successful main Build.
Tuesday/Saturday at 06:00 UTC rerun the same bounded gates against that newest
main Build. These scheduled runs are not longer live soak tests. Selection
validates the producing repository/workflow/event, successful conclusion and
complete unexpired native/managed artifact inventory. Main manifests must match
the producer SHA; PR merge checkouts must have the producer head as their second
parent. There is no rebuild or artifact fallback. The shared `use-build` action
verifies signed main provenance before execution and verifies native runtime
digests before exporting executable paths.

Windows/Linux Unit and Integration jobs run the packaged assemblies with
`tests\Invoke-BuiltTests.ps1`, not project restore/build. Unit selects
`Category=Unit`; Integration selects `Category!=Unit`, retaining actual opt-in
skips. The archive verifier rejects traversal, duplicate entries and links, and
requires every expected test assembly and the coverage collector.
Compatibility jobs use `tests\run-compatibility.ps1 -SkipManaged -Pester` for
existing native CLI/state/restart checks and tooling regressions. Tooling tests
may restore or build their own tiny fixtures, not the ecosystem candidate.
Native telemetry additionally runs its identity flow. Pester is pinned to 5.7.1;
Linux provisions phone runtime libraries and packaging `zip`.

The summary job combines both managed suites with native Pester cases using the
existing scoped catalog. Missing reports, failed dependencies and unmatched
selectors fail explicitly; skipped evidence never turns into Pass. Coverage is
unioned per platform across Unit/Integration, not across different platforms.
JSON/Markdown, TRX, Pester XML and coverage are retained for 14 days, including
after failures. Superseded runs are cancelled; actions are SHA-pinned.
The consolidated Actions summary compares Windows/Linux suites, claims, test
mappings and coverage side-by-side instead of repeating whole platform reports.
macOS cells explicitly say not run/not collected. Detailed per-platform
JSON/Markdown remains in the artifacts; test mappings are collapsed in the UI.
Test runners clear and restore GitHub command-file variables during execution,
disable unused version-variable exports without changing cloud version
computation, and suppress child-process ANSI output for XML-safe failure reports,
preventing intentional failure fixtures from publishing misleading job summaries.
The exact solution-derived test report inventory is required. Consolidation
normalizes Windows/Linux TRX assembly paths and accepts only the LF or CRLF
representation of the producing catalog; changed contents or commits fail.
The original native catalog hash, mode flags and dirty-worktree disclosure
remain in each platform's consolidated JSON.

CI does **not** provision actual Python reference checkouts, virtual telephone
audio/calls, physical devices or a desktop display. Corresponding claims remain
skipped/unselected, and `-RequireComplete` is intentionally not used. macOS is a
build/help gate only, not ecosystem feature acceptance. The recorded live and
hardware evidence requirements still apply to protocol changes.

`.github\workflows\codeql.yml` independently analyzes Actions, C# (manual Release
build with the pinned stack) and Python using CodeQL, plus tracked repository
PowerShell using PSScriptAnalyzer 1.24.0. It runs on relevant changes, manual
dispatch and Mondays at 05:00 UTC. PowerShell correctness/security findings fail
after SARIF is written; existing command names are not subjected to a style
rewrite. Reports distinguish analysis completion from findings. Fork PRs retain
SARIF without uploading it to Code Scanning. C# analysis necessarily includes
its compiled stack dependencies; coverage still excludes them.

`.github\workflows\stale.yml` sends 90-day inactivity reminders on Mondays at
05:30 UTC, never closes issues/PRs or deletes branches, and exempts assigned,
milestoned, draft or explicitly protected work. Manual runs default to preview.
Release remains separately tag-driven; none of this dispatches a release.

README badges link to Build, Test and Static analysis (which includes CodeQL);
there is no separate default CodeQL workflow in this repository.
`assets\coverage` contains Shields endpoint data and source-run provenance.
The `coverage-badges.yml` workflow updates these files only from a successful
automatic main Test, after validating the main Build, summary commit, clean
worktree and measured coverage counts. It never executes downloaded artifacts.
Older runs cannot overwrite newer badge data, and the branch update is
non-forced. Badge-only commits do not trigger Build/Test. macOS is explicitly
not collected, not zero percent. Manual badge dispatch accepts an automatic
main Test run ID; scheduled/manual Test reuse does not publish badges.

### Local CI acceptance

The CI working tree based on `c4e2b8c` was exercised locally with Pester 5.7.1
and verified native artifacts selected before the runner. Windows used a
runtime-only ZIP handoff; Linux used a native WSL checkout and removed executable
permissions before verifying their restoration. Run artifact preparation and
test execution in separate PowerShell processes, as the workflow steps do.
Pester 5.7.1 skip handling can inherit a caller's `$File` variable: a manifest-file
loop left in that scope stalled skip reporting. Removing that caller variable
or starting a fresh process restored the expected passes and explicit skips.

| Host | Exact runner invocation | Result |
|---|---|---|
| Windows | `& .\tests\run-tests.ps1 -Coverage -Pester -OutputDirectory TestResults\ci-local-windows-clean` | Passed: 239 xUnit cases, 45 skips; 46 Pester cases, 21 skips |
| WSL/Linux | `& .\tests\run-tests.ps1 -Coverage -Pester -OutputDirectory .\TestResults\ci-local-linux-clean` | Passed: 238 xUnit cases, 46 skips; 49 Pester cases, 18 skips |

Each run also excluded one GUI case. All eight production modules were covered:
Windows 5,400/8,558 lines and 2,866/5,922 branches; Linux 5,404/8,558 lines and
2,869/5,922 branches. NativeAOT publication with trim/AOT warnings as errors and
help checks passed for all five applications on both hosts. The downloaded
applications passed their selected native gates, and
`& $env:DOTRETICULUM_TELEMETRY_BINARY -p --config <isolated-directory>` passed on
each host. `dotnet build DotReticulum.Ecosystem.slnx -c Release --no-restore --verbosity quiet`
passed with zero warnings/errors on both hosts; actionlint accepted the workflow.

The gate exposed and corrected a capture-disposal cancellation race covered by
the existing fake-driver regression; this adds no physical-audio claim. The
runners now preserve an explicitly loaded supported Pester version rather than
trying to load a newer incompatible assembly. The original combined workflow
passed all five hosted jobs at `f7fa7fc` in
[run 40990893298](https://github.com/dennispayne/DotReticulum.Ecosystem/actions/runs/40990893298),
including macOS ARM64 publication/help. That run does not verify the new split
workflows; their hosted evidence is recorded below.

### Split gate acceptance

The split helpers were exercised from a dirty working tree based on
`f7fa7fcc44e70eef1683bd4372f51d1ffbacbfb0`. Both platforms packaged and ran their
actual Release assemblies and restored collector without rebuilding in the
test runner. Native-only checks used the exact runtime artifacts downloaded
from the hosted run above, not newly built substitutes.

| Host | Unit | Integration | Native/tooling |
|---|---:|---:|---:|
| Windows | 157 passed | 82 passed, 45 skipped | 58 passed, 21 skipped |
| WSL/Linux | 157 passed | 81 passed, 46 skipped | 60 passed, 19 skipped |

Each native run also retained one unselected GUI case. Linux's native/tooling
skips include the analyzer-module regression because PSScriptAnalyzer was not
installed there; the separate Static analysis job provisions its pinned version.
The managed commands were `tests\Invoke-BuiltTests.ps1 -Directory <verified-managed-artifact> -Rid <host-rid> -Commit f7fa7fcc44e70eef1683bd4372f51d1ffbacbfb0 -Filter 'Category=Unit' -ResultsDirectory <Unit-directory> -Coverage`
and the same invocation with `-Filter 'Category!=Unit'` and its Integration
directory. Windows used `artifacts\actions-managed-win` and
`TestResults\actions-built-win`; Linux used `artifacts/managed` and
`TestResults/built`.

`tests\run-compatibility.ps1 -SkipManaged -Pester -OutputDirectory TestResults\actions-native-win-complete`
passed on Windows; the Linux invocation used `TestResults/native-complete`.
`tests\Get-CiSummary.ps1 -Directory <actions-platforms-directory> -Commit f7fa7fcc44e70eef1683bd4372f51d1ffbacbfb0 -Attempt 1 -ManagedStatus success -CompatibilityStatus success`
passed on the Linux summary host with both platforms' real inputs: 364 discovered
cases per platform, zero execution/discovery failures, and the dirty flag intact.
All eight production modules were represented; Windows unioned 5,410/8,558 lines
(63.22%) and 2,872/5,922 branches (48.50%), Linux 5,405/8,558 lines (63.16%) and
2,869/5,922 branches (48.45%). Missing evidence and changed commit/catalog
regressions failed explicitly.

`dotnet build DotReticulum.Ecosystem.slnx -c Release --verbosity quiet` passed with
zero warnings/errors; `dotnet test DotReticulum.Ecosystem.slnx -c Release --no-build --verbosity quiet`
passed 239 cases with 45 skips on Windows.
`tests\run-pester.ps1 -Path tests\Pester\BuildArtifacts.Tests.ps1,tests\Pester\NativeBuild.Tests.ps1,tests\Pester\TestingStandards.Tests.ps1`
passed 27 cases on Windows and 26 with one explicit analyzer-module skip on Linux.
`tools\Invoke-PowerShellAnalysis.ps1` analyzed 27 tracked scripts with zero
findings, and actionlint 1.7.7 accepted all workflows.

### Hosted split gate acceptance

At `3ab9cfd`, all jobs passed in
[Build 41243731068](https://github.com/dennispayne/DotReticulum.Ecosystem/actions/runs/41243731068),
the automatically triggered
[Test 41244383455](https://github.com/dennispayne/DotReticulum.Ecosystem/actions/runs/41244383455)
and
[Static analysis 41243730917](https://github.com/dennispayne/DotReticulum.Ecosystem/actions/runs/41243730917).
This verifies all three managed/native build targets, trusted-main artifact
attestation and consumer verification, exact workflow-run handoff, six independent
Windows/Linux test jobs, consolidated evidence/coverage, and CodeQL/PSScriptAnalyzer
execution and report uploads.

Windows passed 157 Unit and 82 Integration cases with 45 Integration skips;
native/tooling passed 61 cases with 23 skipped/unselected records. Linux passed
157 Unit and 81 Integration cases with 46 Integration skips; native/tooling
passed 64 cases with 20 skipped/unselected records. Each consolidated report
retained 368 cases, zero execution/discovery failures and a clean working tree.
Coverage represented all eight modules: Windows 5,398/8,558 lines and 2,865/5,922
branches; Linux 5,404/8,558 lines and 2,867/5,922 branches.

The hosted summary uses shared platform columns for suites, compatibility and
coverage. [Coverage badges 41244624568](https://github.com/dennispayne/DotReticulum.Ecosystem/actions/runs/41244624568)
validated this Test and updated endpoint data plus its source-run metadata.
Windows is 63.08% line coverage, Linux 63.15%, macOS explicitly not collected.
Local `tests\run-pester.ps1 -Path tests\Pester\TestingStandards.Tests.ps1,tests\Pester\BuildArtifacts.Tests.ps1`
passed 25 cases on Windows and 24 with one explicit analyzer-module skip on Linux.
The complete native/tooling runner also passed with simulated GitHub detection,
including a detached Linux checkout; sentinel GitHub command files remained
unchanged and restored. Both passing and failing subprocess regressions verify
that isolation does not hide test failures or alter cloud version semantics.
PSScriptAnalyzer analyzed all 28 tracked scripts with zero findings.

The first hosted attempts exposed concurrent unused version-variable exports
corrupting macOS's GitHub environment file and a successful coverage helper
inheriting an unset exit code. Build disables only the environment exports,
preserving assembly version computation; coverage now explicitly returns
success, with a regression covering a preceding failed command.
PR merge selection, manual/scheduled reuse and inactivity reminders have not
been exercised on GitHub. macOS remains build/help-only, and the live/GUI/hardware
boundaries above remain unchanged.
