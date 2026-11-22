# Testing and compatibility evidence

The ecosystem uses the stack's testing standard: independently executed reference
vectors, real pinned Python peers, explicit compatibility boundaries, process and
NativeAOT acceptance, and unioned production-code coverage. A successful command
means its selected checks passed, **not** that the ecosystem has full upstream
parity. Missing features, skipped cases and untested hardware remain visible.

## Baseline provenance

With DotReticulum pinned at `42d965af7d4152d0ad7c5faf16416b5d6337958c`,
the Windows Release solution build passed without warnings and the ordinary
test run passed 3,184 cases with 197 skips. WSL-native Linux x64 passed 2,008
Unit cases and 1,188 Integration cases with 193 Integration skips using the
solution's `Category=Unit` and `Category!=Unit` filters. Strict Windows and Linux x64
NativeAOT Sideband builds ran their help and selected software-printing
failure, recovery and descendant-ownership checks. These are local results,
not new hosted or live-peer acceptance.

The integrated authorization/endurance and repository-lifecycle candidate
builds with zero warnings/errors and passes3180 managed cases/
197 explicit skips on Windows:

```powershell
dotnet build DotReticulum.Ecosystem.slnx -c Release -warnaserror --nologo -v:q
dotnet test DotReticulum.Ecosystem.slnx -c Release --no-build --nologo -v:q --logger trx --results-directory artifacts\tests\authorization-endurance-current-final
```

The final unified suites pass LXMF163/28, LXST141/31, Nomad492/43,
Sideband2381/95 and Telemetry3/0. The current unified ordinary gate uses:

```powershell
pwsh -NoProfile -File tests\run-tests.ps1 -Coverage -Pester -OutputDirectory artifacts\tests\fleet-authorization-format-final
```

It passes with111 scoped claims/700 selectors/five pins validated. The ten-report
source-identity union is25031/33190 production lines and17393/27236 branches.
Ordinary skipped native/live cases and unmapped records remain explicitly
unavailable evidence, not passing compatibility.

Separately executed parent-independent Windows NativeAOT closures are:

| Claim | Current candidate | Scoped result |
| --- | --- | --- |
| C102 guide | `nomad-guide\parent-final\candidate.json` |13 focused,493 full/33 explicit skips,4 Pester; exact primary vectors regenerate |
| C103 message telemetry | `sideband-message-telemetry\parent-current\candidate.json` |84 selected,4 Pester; actual primary messages both ways, clean/abrupt native restart, import and private clipboard windows |
| C104 Micron/C109 peer controls | `nomad-micron-contracts\final-candidate.json` |15 focused,507 full/19 explicit skips,4 Pester; existing editor regressions and T1052 native persisted removal pass |
| C105 propagation telemetry | `sideband-propagation-telemetry\parent-current\candidate.json` |30 selected without skips; actual primary lxmd and published application, configuration, identity failure and recovery |
| C107 hosted statistics | `nomad-host-statistics\final2\candidate.json` |23 selected,3 Pester without skips; actual primary requests native host, reset/restart and strict import |
| C108 printing | `sideband-printing\format-integration\candidate.json` |16 selected without skips,34 shared/1 Linux skip,2381 Sideband/95 skips,492 Nomad/43 skips,5 Pester; final preview test included |
| C110 authorization | `nomad-windows-authorization\candidate-current.json` |497 ordinary/38 skips with converter enabled,8 focused,1 actual-primary/native,7 same-binary guide/Micron/peer regressions; unchanged native, fresh strict-built test assembly |
| C111 telephone endurance | `lxst-python-endurance\20261105T111153520Z-160d6542\receipt.json` |600.143 steady seconds/40 positive real-codec intervals, Codec2/reverse call/peer restart; Pester1/0/0 and independent verifier; historical6eafd native only |
| C112 stale retention | `sideband-propagation-telemetry\retention-c112-third\workload\result.json` |342.69s Pester1/0/0; actual306.141s retry, Timeout/retention/policy recovery and continued signed delivery; historicale7fd native; parent raw-artifact audit, not a second live run |

Candidate paths are relative to `artifacts\tests`. Their corresponding checked-in
`tests\Pester\Invoke-*Acceptance.ps1` operators require fresh replay output
directories and guard exact production, acceptance and compiled inputs before/
after execution. C102 additionally freezes acceptance sources **before build**
and identifies every ordinary Windows skip by name/reason; C108 counts individual
TRX outcomes rather than the adapter's misleading aggregate `notExecuted=0`.
No scoped receipt accepts another candidate, later source changes or skipped
live/platform cases. Native hashes and replay commands are in each project's
reference. C106's ten-minute workload retains its earlier exact executable;
it is not silently attributed to these new binaries.

The final ordinary rerun first exposed T603's fixed10,000-yield polling budget
under concurrent workloads. That test now waits for explicit asynchronous
failure/recovery observer completions and uses concurrent collections; its exact
error-count, rendering, recovery and observer-isolation assertions remain.
Ten focused repetitions and the final full gate pass. No editor production
behavior changes. The repository server's independently identified two-state
admission race and unchanged400ms deadline acceptance are documented with its
native TLS/restart replay in [Sideband](sideband.md#explicit-local-selected-paper-printing-c108).

The Windows actual-Python stream replay separately passes3 supported-family
cases across direct/.NET-hop/Python-hop paths, including exact two-packet loss
each way and recovery. The reference itself reports NumPy `float128` unsupported;
the separate extended-Raw theory explicitly skips, rather than passing or
removing its equality assertions. Initial unsplit3-case failures are preserved
in `lxst-python-endurance\parent-stream-regression`; the passing supported-family
receipt is `lxst-python-endurance\parent-format-regression\stream-formats.trx`.
The C111 shell wrapper accidentally redirected away its PassThru object and
exited1 while checking null counts; its actual Pester XML proves1/0/0 and the
independent receipt verifier accepts249 unchanged inputs. No command-success
claim is made for that erroneous wrapper.

The refreshed C108 strict NativeAOT build initially exhausted disk while linking
its PDB. Transparent NTFS compression of explicitly identified completed caches
preserved their bytes and frozen receipts; retry, freeze and independent replay
then passed. Native6eb8 and candidate917d fingerprints, the separate actual
repository-window1/0/0 and exact guard sizes are in the Sideband reference.
This refresh supersedes the earlier accepted49fb printing closure without
rewriting that receipt.

Hosted evidence remains older and separate: successful Build41502113530 targets
`89e948a90e9606bed331b71da6b787cace0bbd81`; successful Test41643993895 runs at
`0e49bee87c932fdf404949812a8420f83cb49e4a`. Current ordinary execution uses
the repository's xUnit2.9.3/runner4.0.1 compatibility where declared; platform
conditions use discovery-time skip attributes, not unavailable v3-only asserts.
Neither hosted run validates these later local commits, and no workflow was
dispatched.

The first current unified attempt passed all selected checks but failed report
aggregation on the old T312 method name after its native-extension assertion was
renamed. T312 now retains its original stable catalog ID and selects the renamed
case, which is also the C105/T919 regression. Earlier failed native freeze/build
attempts retain their evidence: an explicitly selected LXMF checkout disagreed
with the installed reference import, and local cache copies exhausted disk.
Correct reference selection and transparent compression/targeted removal of
completed linker intermediates preserve frozen binaries, references and receipts.
The first C102 replay also refused its stale full-suite skip ceiling; the current
operator requires the exact inventoried Windows outcomes rather than raising a
generic allowance.

The printing and hosted-statistics oracle writers explicitly emit LF, matching
their checked-in `.gitattributes` rules on Windows as well as Linux. Executing
both pinned references on Windows regenerates Git-canonical fixtures unchanged;
only the former working-copy CRLF translation is removed. Focused statistics
17/0 and printing13/0 regression checks pass. This fixture-serialization
correction changes neither production behavior nor reference observations, and
does not relabel the earlier native receipts' exact source hashes.

Earlier MQTT ordinary coverage/Pester/catalog gate
also passes with97 scoped claims/545 selectors/five pins:

```powershell
pwsh -NoProfile -File tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath artifacts\builds\fleet-mqtt-unified -OutputDirectory artifacts\tests\fleet-mqtt-unified
```

Its source-identity union covers23067/30760 production lines and
16017/25289 branches across10 reports. That original catalog snapshot precedes
C99 integration. Reaggregation of the unchanged executed records against the
accepted editor catalog validates98 claims/558 selectors in
`artifacts\tests\fleet-mqtt-editor-catalog`; it does not claim another execution.
Skipped native/live and
unmapped cases are not passing compatibility evidence.
Selected MQTT/external-geoid acceptance
separately passes124/0 without skips plus2/0/0 Pester, including actual primary
telemetry/collector exchange, clean/abrupt native restart, primary-profile import,
observed repeated broker failures and minute cadence, and four offline native
settings windows. The fresh-child frozen replay guards1260 exact inputs and
runs native help on Sideband SHA-256
`a4c1a3bdd23e8766138b94ef7ff3bb55967fc831584a407ae0d7536bd08d7c32`.
Reference publishers use genuine isolated Mosquitto separately from the native
MQTTnet observer; this is not same-broker or transport-hop acceptance. Earlier
broker-policy/reference-drain and cadence-harness failures remain recorded in
[Sideband's scoped evidence](sideband.md#explicit-mqtt-telemetry-forwarding-and-external-geoid-c98).

```powershell
pwsh -NoProfile -File artifacts\tests\sideband-mqtt\Replay-Candidate.ps1 -OutputDirectory artifacts\tests\sideband-mqtt\parent-frozen-retry-observed
```

Parent-independent frozen C99 replay separately passes426/15 managed and
3/0/0 Pester, including three new scrolling ConPTY, existing editor/terminal
ConPTY and actual Vim checks on native SHA-256
`1d6d99096f45b20e920371a0e1b66ca0394f5f78fd8152093c6a996af1aa5548`:

```powershell
pwsh -NoProfile -File artifacts\tests\nomad-editor-scrolling\Replay-Candidate.ps1 -OutputDirectory artifacts\tests\nomad-editor-scrolling\parent-independent
```

Independent session positions survive native preview replacement; primary's
observed replacement resets. C97's actual-primary callbacks had rendered an
error panel without the singleton. C99 separately verifies synthetic Micron
with a disclosed singleton/palette shim; that is not full unmodified-primary
application evidence. Bounded primary editor-state/document migration is now
separately accepted below. Full terminal interaction/columns remain required
software, not physical-hardware deferrals.

Parent-independent frozen C100 replay passes441/26 managed and5/0/0 Pester:

```powershell
pwsh -NoProfile -File tests\Pester\Invoke-NomadNetEditorStateImportAcceptance.ps1 -Candidate artifacts\tests\nomad-editor-state-import\candidate.json -OutputDirectory artifacts\tests\nomad-editor-state-import\parent-independent
```

Native SHA-256 is
`57de210c177c2f4dd9aa010efb1b122a869137b6d832b53d14eb4e7859427e69`.
The actual primary constructor/start/load/save observations, selected native
ConPTY edit/save/restart and strict atomic absent-target import are separate
from primary GUI, new wire interoperability or current Linux acceptance.
External/custom roots and unknown preferences are not guessed.

The C100 integrated coverage/Pester/catalog gate also passes:

```powershell
pwsh -NoProfile -File tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath artifacts\builds\fleet-editor-state-import -OutputDirectory artifacts\tests\fleet-editor-state-import
```

Its full managed execution passes3000 cases/176 explicit skips, including the
separately accepted battery candidate;99 claims/577 selectors/five pins validate.
The ten-report source-identity union is23267/30973 lines and16196/25491 branches.
Neither coverage nor ordinary skipped native/live cases implies full parity.

Hosted evidence was rechecked without dispatching workflows: latest
Build41502113530 succeeds at `89e948a90e9606bed331b71da6b787cace0bbd81`;
Test41643993895 succeeds at `0e49bee87c932fdf404949812a8420f83cb49e4a`,
including its Windows/Linux Unit, Integration and Compatibility jobs.
These earlier commits do not accept the current local integrations.
The xUnit3.2.2 Avalonia-compatible pin remains unchanged.

Parent-independent frozen C103 acceptance passes84 selected managed cases
without skips and4/0/0 Pester, including actual primary normal messages both
ways, newer reports, clean/abrupt native restart, malformed-stream isolation
and native import of actual primary SQLite backups. Six offline Windows
windows verify private-provider telemetry/raw copying separately:

```powershell
pwsh -NoProfile -File tests\Pester\Invoke-SidebandMessageTelemetryAcceptance.ps1 -Candidate artifacts\tests\sideband-message-telemetry\parent-current\candidate.json -OutputDirectory artifacts\tests\sideband-message-telemetry\parent-current\independent
```

Exact native SHA-256 is
`df44e6a9cef21ac012deeda2f51b9163dcd721e4fcca2a336a7f44d3347e2e97`,
SDK10.0.403/ILCompiler10.0.14 at versioning HEAD `10e890f`.
The receipt guards exact production/toolchain and acceptance inputs,
1179 reference inputs and91 managed artifacts. Seventy actual-primary
callback/readings/link-rounding observations regenerate byte-identically.
Full Sideband separately passes2380/95 explicit skips. Primary same-second
send suppression is respected by a source-timebase wait; earlier failed
harness/limit-type/Unicode Git-path attempts remain failures.
No OS clipboard, primary GUI, physical sensor, new hop/loss or current Linux
acceptance is inferred.

Additional opt-in C106 service endurance on the earlier C103 native hash
`f94d8847230f7a4a86d842abcaae93cdc49860760de71dfa43d51adc8a36f2bb` passes
1/0/0 in10m53s:600.027 steady seconds, ten same-process minute-spaced
bidirectional actual-primary exchanges, terminal failed delivery after peer
exit, different-primary recovery and identity/history/snapshot-preserving
native restart. Its direct-loopback, ten-minute boundary and exact command
are recorded in [Sideband's reference](sideband.md#measured-native-messaging-service-endurance-c106).
This later workload is not retroactively part of C103's earlier frozen
managed-input closure; no days-long, loss/hop, physical or Linux claim follows.

Parent-independent C102 frozen guide replay passes13 focused cases,
493 full Nomad cases/33 explicit skips and4/0/0 Pester, with exact-primary
observations independently byte-identical:

```powershell
pwsh -NoProfile -File tests\Pester\Invoke-NomadNetGuideAcceptance.ps1 -Candidate artifacts\tests\nomad-guide\parent-final\candidate.json -OutputDirectory artifacts\tests\nomad-guide\parent-final\independent
```

Native SHA-256 is
`00b16603d1969db1d292502e8446c7a8da00c335a0872e05a910ef755afc69d0`,
SDK10.0.403/ILCompiler10.0.14, versioning HEAD `10e890f`.
The guard covers354 scoped production/toolchain inputs,113 acceptance inputs
and44 managed artifacts. Native ordinary and actual ConPTY navigation,
guide-driven authoring/access/handler registration and identity-preserving
restart are not full Urwid/keyboard/mouse, remote-topology, current Linux or
physical-hardware acceptance. Thirteen actual primary topic bodies are parsed
with disclosed singleton/screen/palette/alarm and unresolved-demo-image seams;
the18-topic native guide is original, not copied primary content.

Parent-frozen C101 acceptance independently passes150 selected managed cases
without skips and4/0/0 Pester, including previous actual-primary MQTT flows and
eight native windows on SHA-256
`5b81b42c155ac36bccb55e1bcd75c12a88931e8c3ea8a12506dfa6ab3b18936d`:

```powershell
pwsh -NoProfile -File artifacts\tests\sideband-battery\Invoke-Acceptance.ps1 -OutputDirectory artifacts\tests\sideband-battery\parent-frozen
```

Its1264-input guard and native help pass.
Reaggregation of the unchanged full-gate records validates100 claims/
588 selectors in `artifacts\tests\fleet-editor-battery-catalog-corrected`.
The first catalog-only attempt exposed and retained an incorrect UI class
selector; correcting that selector maps the already-passing case, not a new
execution or weakened assertion.

Battery evidence is actual-primary
synthetic sysfs, real private test files, injected device-free lifecycle/cache
tests and Windows unavailable-provider/settings windows, **not physical battery
or current Linux execution**. Native re-probing and stricter invalid-input
handling differ from the primary. Android-only acquisition and plugin-driven
providers keep separate dispositions; no automatic sensor is invented.

The integrated terminal-controls/rich-message candidate builds with zero
warnings/errors and passes2812 managed cases/156 explicit skips on Windows:

```powershell
dotnet build DotReticulum.Ecosystem.slnx -c Release -warnaserror --nologo -v:q --artifacts-path artifacts\builds\root-terminal-markup-strict
& tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath artifacts\builds\fleet-terminal-markup-unified -OutputDirectory artifacts\tests\fleet-terminal-markup-unified
```

Ordinary Pester/tooling and strict compatibility aggregation pass with95
claims/494 selectors/five pins. Coverage unions10 reports:
21977/29568 production lines and15014/24087 branches. Full suites pass
LXMF163/28, LXST141/30, Nomad387/20, Sideband2118/78 and Telemetry3/0.
Opt-in native/live checks remain separate, not converted from skips.
Changed operators pass repository-configured PowerShell analysis; changed Python
adapters compile. These are local results, not new hosted acceptance.

The selected stack is `4989f050aed865d16ed9f1460071c1a63cd4c7e9`.
Separately committed stack changes `64b76f6df574406f85b5ab7666b1a91dab9674fa`
and `4989f05` provide public probe-response and implicit-packet-proof policies,
with unchanged explicit link proofs and fixed interface-failure ownership.
Their own1855/84 suite, strict native acceptance, actual Python proof/probe
exchange through a native hop/restart and2,200,017-byte Resource round trip
are separate from application integration. Neither repository was pushed.

Parent-independent frozen Nomad Windows NativeAOT/reference replay passes
390/17 managed and5/0/0 Pester on
`b2117d8ae472172bbd97835c84428533645123fe1b0e5ef0d21ee6241b195665`.
Three actual native ConPTY checks replace three ordinary skips. Nine formerly
stored-only TextUI controls and utility visibility have bounded functional
effects; the separate direct TCP primary utility consumer observes actual probe
responses. Agent-selected actual Vim regressions pass2/0 separately; these are
not physical terminal, full Urwid/editor/grammar or current Linux acceptance.
The panic toggle remains an intentional safety refusal, not an ineffective
stored setting or missing stack implementation.

Sideband frozen Windows NativeAOT/reference replay passes2/0/0 Pester and2/0
actual-primary cases on
`8b19a4bb879d3e2a7df7e1af6836a312927576d7310e1e171a947d531d8d0119`.
Independent byte-identical actual-primary regeneration precedes eight native
formatted/trust-policy windows and clean/abrupt native identity restarts with
both renderer fields exchanged both ways. Direct TCP is the only new network
topology. Raw copy uses a private port, not the shared OS clipboard. Exact
pre-font primary statements and headless controls are not primary GUI,
exhaustive Markdown/Kivy, physical audio/display or current Linux evidence.

```powershell
& artifacts\tests\nomad-terminal-controls\Replay-Candidate.ps1 -FullSuite -OutputDirectory artifacts\tests\nomad-terminal-controls\parent-independent-replay
& artifacts\tests\sideband-message-markup\Replay-Candidate.ps1 -OutputDirectory artifacts\tests\sideband-message-markup\parent-guarded-replay-clean-json
```

Fresh-child receipts guard production, dependency/stack, acceptance sources and
exact native hashes before/after. The rich-message replay additionally guards
481 inputs. Its earlier receipt serialization attempt retained unchanged passing
checks but failed to separate upstream import diagnostics from JSON; it is not
counted as a successful replay. The corrected operator preserves both outputs.
Unchanged primary LXST selects its own Python filter fallback in that reference
import; rich-message checks do not establish filter or audio acceptance.

An earlier integrated collection in `fleet-rns-conversation-actions-final`
failed one shared-owner telemetry assertion after reconnect. The test now
also requires the reconnected shared-client role and supplies diagnostic
context; no assertion was relaxed and no production fix is claimed.
The two focused cases pass without coverage, all three class cases pass with
coverage, and20 instrumented repetitions pass both connection modes (40 cases).
The fresh whole gate above also passes. An earlier rich-message full application
run separately failed existing voice setup readiness. Diagnostic context was
strengthened without changing voice production logic or relaxing the deadline;
12 focused repetitions and fresh full runs pass. Both intermittent causes
remain unestablished; green replays do not demonstrate fixes.
See [NomadNet](nomadnet.md#original-terminal-controls-and-utility-visibility-c95) and
[Sideband](sideband.md#bounded-rich-message-formatting) for scoped commands,
source hashes and unavailable-platform boundaries.

The preceding integrated configuration/appearance candidate builds with zero warnings
and errors and passes2768 managed cases/151 explicit skips on Windows:

```powershell
dotnet build DotReticulum.Ecosystem.slnx -c Release -warnaserror --nologo -v:q
& tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath artifacts\builds\fleet-configuration-appearance-aliases-unified -OutputDirectory artifacts\tests\fleet-configuration-appearance-aliases-unified
```

Ordinary Pester/tooling and strict compatibility aggregation pass with90
claims/439 selectors/five reference pins. Coverage unions10 reports by source
identity:21204/28558 production lines and14173/22926 branches. GUI/live/native
opt-ins remain distinct, not converted from skips into passes. Changed operators
pass the repository-configured PowerShell analysis rules. Generated fixtures
used by byte-identical regeneration retain LF explicitly on Windows checkout.

Current C89/C91 Windows candidates have independent source/native acceptance.
Nomad full360/17, strict NativeAOT/help, native18/0 including actual Vim2/0
and broader29/0 pass on refreshed executable
`5abcbf19347b222db09ee7ee616b0cba5c2dc056d211e57d8d11f907c79d1bd8`.
Sideband full2101/76, actual-primary console4/0, strict NativeAOT/help,
native console5/0, appearance/reference2/0, raw-copy/reference2/0 and
cancellation/retry/deletion5/0 pass on executable
`74584c124b45e943e8d435923c7d3a51b8cf3f0727c933a392caccd68d52e196`.
Both include the final LXMF nonmutating cancellation-readiness API. The
deterministic pre-admission regression corrects a reproduced full-gate failure,
not a relaxed cancellation assertion. Detailed commands, hashes and boundaries
are in [NomadNet](nomadnet.md#current-configuration-acceptance) and
[Sideband](sideband.md#conversation-appearance-policies).

The Nomad75-field forms retain explicit stored-only UI/history and unsupported
public-stack dispositions; their broader29-case replay includes actual primary
multi-megabyte node transfer/restart but no new loss or endurance topology.
Sideband appearance windows use original text badges, and raw-copy smoke uses a
private provider, never primary assets or the shared OS clipboard. Neither
establishes primary GUI, physical hardware or current Linux source replay.

The preceding copy/configuration development worktree passes2763 managed cases/151
explicit skips, ordinary Pester/tooling, strict catalog aggregation and merged
coverage with88 claims/419 selectors/five reference pins:

```powershell
& tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath artifacts\builds\fleet-message-copy-unified -OutputDirectory artifacts\tests\fleet-message-copy-unified
```

This dirty-source run includes in-progress Nomad configuration contracts;
their presence in a green whole-suite run does not accept or complete C89.
Coverage unions10 reports by source identity:21091/28423 production lines
and14124/22846 branches. Native/live/GUI opt-ins remain separate below.

The C90 Sideband raw-copy candidate independently passes full2097/76,
primary console4/0, strict NativeAOT/help and native console5/0 on Windows.
Selected copy/reference Pester2/0 exercises four real native windows through a
private clipboard observation provider, never the shared OS clipboard.
Additional selected cancellation/retry/deletion Pester5/0 includes both
actual-primary native/restart flows, both regenerated fixtures and real
desktop deletion. Exact binary SHA-256:
`0c746481287d636b11f9fa257cf25773d8bf69f2ba07ffa515d33e54d67c2e33`.
Raw/empty/control/maximum content, one pending operation, five-second wait,
failure recovery, close cancellation and state preservation have separate
headless evidence. OS-provider round trips, primary Kivy and current Linux
replay remain unverified; see [Sideband's reference](sideband.md#selected-message-raw-text-copy).

```powershell
& artifacts\tests\sideband-console\Parent-CurrentConsole.ps1 -Managed -Run message-copy-final
& artifacts\tests\sideband-message-copy\Accept-SelectedNative.ps1
& artifacts\tests\sideband-message-cancellation\Accept-SelectedNative.ps1 -CandidateRun message-copy-final -Run parent-copy-final
```

The preceding folder-permissions/pending-cancellation solution builds with zero warnings/errors and
passes2748 managed cases/151 explicit skips on Windows:

```powershell
dotnet build DotReticulum.Ecosystem.slnx -c Release --nologo -v:q
& tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath artifacts\builds\fleet-permissions-cancellation-unified -OutputDirectory artifacts\tests\fleet-permissions-cancellation-unified
```

Sideband's preceding C85 guarded candidate passes full2088/75, separately enabled
primary console4/0, strict NativeAOT/help and existing native console5/0.
The same selected binary independently passes actual-primary text retry,
return messages, repeat rejection and identity-preserving restart; exact
reference regeneration and the unchanged real desktop deletion regression
also pass3/0 Pester, including1/0 nested native xUnit. Native SHA-256:
`c5e9c9a55f181468ef4cdac8d94b69dcc6854ba4456566c154ef6873f83324e5`.
Validation precedes atomic peer/ID/content-matched history replacement; current
delivery policy, eight-request admission, cancellation/disconnect, draft protection,
queue refusal/recovery and same-ID/different-peer isolation are tested.
Reference callback/SQLite observations disclose their synthetic UI boundaries;
no primary GUI retry or new hop/loss claim follows. Retained failed operators
and their corrections remain in [Sideband's reference](sideband.md#selected-failed-message-text-retry).

```powershell
& artifacts\tests\sideband-console\Parent-CurrentConsole.ps1 -Managed -Run message-retry-peer-scope
& artifacts\tests\sideband-message-retry\Accept-SelectedNative.ps1 -CandidateRun message-retry-peer-scope -Run peer-scoped-final
```

Preceding Nomad C86 passes337/17 managed and strict NativeAOT/help. Parent
independently repeats11 reference/native editor/folder cases, including actual
Vim ConPTY2/0, and29 printing/node-migration/clipboard/utility cases with zero
failures/skips. Native SHA-256:
`1e80c6e066387a3d3b6e11b5c94fba8f9133c19d25b613eb3ce717265f574487`.
Folder creation, inspected/confirmed rename/move/removal, dirty/stale/conflict
refusal, bounded traversal/cleanup and current/saved descendant selection
survive ordinary-console restart. Reversible detachment is not a crash-atomic
filesystem-plus-settings transaction; recursive permission acceptance is separate
below, and full forms/grammar/presentation remain required. These local editor flows do not add a new
Python-peer topology; the29-case broader gate separately repeats actual primary
node transfers/restarts. Earlier endurance/extra ownership receipts retain their
original source boundaries.

```powershell
& artifacts\tests\nomad-folders\Invoke-SelectedNative.ps1
& artifacts\tests\nomad-editor\Parent-BroaderAcceptance.ps1 -Candidate artifacts\tests\nomad-folders\candidate.json -OutputDirectory artifacts\tests\nomad-folders\parent-broader-native
```

Exact receipts are under
`artifacts\tests\nomad-folders\acceptance-bf707dd803f44fe0b1dc68aeaa890ff4`,
`parent-broader-native` and
`artifacts\tests\sideband-message-retry\peer-scoped-final`.
Current Linux source replay remains blocked by WSL storage EIO.

Current C87 Nomad passes348/17 managed and strict warning-free NativeAOT/help.
Parent independently repeats14 native/reference permission/editor/folder cases,
including actual Vim ConPTY2/0, and29 printing/node-migration/clipboard/utility
cases with zero failures/skips. Native SHA-256:
`ef0c7a2b8a2bb0ac310d992fc90ebaa175bbba6205dc3be7772a53c17a300863`.
Confirmed recursive permission summaries/replacement/public conversion,
ordinary rollback, explicit committed-cleanup failure and identity-preserving
restart are distinct from network authorization or crash-atomic all-file
visibility. Actual primary confirmation methods execute with disclosed synthetic
UI owners. Current Linux replay, full forms/ConfigObj and terminal presentation
remain unclaimed. Exact guards and fresh parent receipts are retained under
`artifacts\tests\nomad-folder-permissions\parent-final` and `parent-broader-native`.

```powershell
& artifacts\tests\nomad-folder-permissions\Invoke-SelectedNative.ps1 -EvidenceDirectory (Join-Path $PWD 'artifacts\tests\nomad-folder-permissions\parent-final')
& artifacts\tests\nomad-editor\Parent-BroaderAcceptance.ps1 -Candidate artifacts\tests\nomad-folder-permissions\candidate.json -OutputDirectory artifacts\tests\nomad-folder-permissions\parent-broader-native
```

Current C88 Sideband passes full2093/76, separately enabled primary console4/0,
strict NativeAOT/help, ordinary native console5/0 and additional selected
cancellation/retry/deletion5/0. Those5 include two non-skipped nested native
xUnit cases, two independently regenerated fixtures and the real desktop
deletion regression. Exact executable SHA-256:
`d4e8408d6718d4bbedca599c7bf29fbaeeef3e04dbcffe147fd0277cc97c5df7`.
Eight-request cancellation/disposal,257-entry tracking, peer/object isolation,
hashless preparation, no-subscriber persistence, draft preservation and actual
primary direct-TCP healthy delivery in both directions across native restart are
separate evidence. Primary required stamp cost254 is explicit; cancellation
is not recall/nonreceipt, primary-origin cancellation or a new hop/loss claim.
The final candidate adds offline disposal ownership beyond the earlier guarded
`1f9c8ad7...` candidate; see [Sideband's reference](sideband.md#selected-active-message-cancellation).

```powershell
& artifacts\tests\sideband-console\Parent-CurrentConsole.ps1 -Managed -Run message-cancellation-lifetime
& artifacts\tests\sideband-message-cancellation\Accept-SelectedNative.ps1 -CandidateRun message-cancellation-lifetime -Run parent-lifetime-final
```

The preceding Sideband guarded `message-actions-corrected` candidate passes full2083/74,
separately enabled live4/0, strict NativeAOT/help and native5/0. SHA-256:
`d625f2ea5dcff60791ae912fa043c69e5fb8cabf7f5f2140f94c934c7e97cd0a`.
Selected saved-message details and confirmation-bound local deletion also pass
one actual native desktop case, independently replayed with source/binary
guards. Two confirmed deletions and restarts retain shared attachment bytes,
identity and contact policy; empty/malformed explicit test profiles exit2.
The first desktop operator failed because it wrote lowercase keys into the
case-sensitive native JSON schema. Its retained failure is not acceptance;
the corrected operator writes the actual PascalCase schema. Deletion neither
recalls/cancels delivery nor removes shared media. The reference observations
execute exact primary methods on synthetic private SQLite, not a whole live
primary GUI. Reproduction and persistent NUnit receipts:

```powershell
& artifacts\tests\sideband-console\Parent-CurrentConsole.ps1 -Managed -Run message-actions-corrected
& artifacts\tests\sideband-message-actions\Replay-SelectedNative.ps1
```

See `artifacts\tests\sideband-message-actions\parent-final\parent-current.json`
and `native.xml`. The preceding logging-only candidate retains its original
SHA-256 `4407fd0ba2871b1547d75832768fa5da6e66e51a590f7dab0326509dfc70fa37`.
Actual primary handler/RNS/core observations establish runtime filtering and
quiet/verbose defaults, not complete stack diagnostic capture. The retained
preceding native4/1 failure was an assertion waiting for an input error on
stdout instead of stderr; the final unchanged rejection is checked on stderr
after orderly exit. A superseded candidate stopped before the final startup
default correction is not acceptance.

The preceding Nomad external/selected-RNS editor candidate passes327/17 managed
and strict Windows NativeAOT/help. Parent independently replays eight
reference/native cases, including two actual Vim ConPTY cases, and29
printing/node-migration/clipboard/utility cases, zero failures/skips, on the
same source-guarded binary:
`90429dad43a841bdb66b3d79b1e7f476aa63558be73ca12ab9458472fb00cdf3`.
Private staging, invalid-edit rollback, nonzero editor exit, concurrent-writer
conflict, Ctrl+] cancellation/descendant ownership and continued console
service are exercised. Primary EditorTerminal observations disclose a terminal
boundary because Windows Urwid lacks Terminal; they are not unmodified primary
Windows PTY evidence. Native acceptance uses real installed Git Vim.

```powershell
& artifacts\tests\nomad-external-editor\Invoke-SelectedNative.ps1
& artifacts\tests\nomad-editor\Parent-BroaderAcceptance.ps1 -Candidate artifacts\tests\nomad-external-editor\candidate.json -OutputDirectory artifacts\tests\nomad-external-editor\parent-broader-native
```

No new network topology or primary GUI is exercised by the editor cases.
The29-case regression gate includes actual primary multi-megabyte node
transfers/restarts but does not repeat the preceding ten-minute utility hold or
extra descendant-ownership cases. Those retain their earlier source boundary.
Reproduction operators and receipts are under
`artifacts\tests\nomad-external-editor\acceptance-da94bc97697f48f88a0297461c3471e3`,
`parent-broader-native` and
`artifacts\tests\sideband-console\windows-message-actions-corrected`.
The preceding original editor-only candidate
`24f3b7d471e44b60acded6b97a057c7cda0adf6b45e4ad98b518180bd17dd49a`
retains its four editor and29 broader passes; it is not acceptance of external
editing. Bulk-folder and full configuration/
presentation contracts remain required software gaps. Current Linux replay
remains blocked by WSL storage EIO, not waived or inferred from Windows.

The refreshed integrated gate also passes:

```powershell
& tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath artifacts\builds\fleet-retry-folders-unified -OutputDirectory artifacts\tests\fleet-retry-folders-unified
```

Its87 claims/413 selectors aggregate strictly, with explicit missing live/native/
hardware evidence retained. Ten coverage reports are unioned by source identity;
their percentages are not compatibility acceptance. Changed/new PowerShell
passes `tools\PSScriptAnalyzerSettings.psd1`; `git diff --check` is clean.

Nomad node-state migration C74 now has independent parent no-build/no-republish
replay of both immutable candidates. Windows full snapshot Nomad267/9 and
Linux276/0, shared LXMF162/28 each, the separate primary observation and three
native cases pass. Both recipes verify873 compiled-source/179 generated-source
documents and the exact native/compiler/reference inputs. These are retained
pre-printing source snapshots, not current whole-project acceptance.
The retained first Windows printing `childhang` failure exposed unjoined
descendants. Its suspended-start/private-Job correction independently passes
the selected20-case native primary gate and deterministic exited-root/locked-file
cleanup/recovery. A separately reproduced Linux exited-root orphan requires the
new subreaper/pidfd supervisor and fresh acceptance; Windows evidence does not
close that Linux gap.

The independently executed Linux current-source C74 gate subsequently passes
the full LXMF/Nomad suites, strict publish/help and four primary/native cases.
Its receipt is `/home/dennis/fleet-nomad-node-parent-current-results/current-source.json`,
with native SHA-256
`5af5579142e4b6e72fe1d6ed19b41f436b432a64b1ce5106e7c54313f9c0b70d`.
The fixed-stack repeat also passes with selected native SHA-256
`bdd71abb789779e9799987ca23bee83869c051468d1e70341c08a8cebaf8a043`.
Neither is acceptance of subsequently added clipboard/printing behavior.

The later ordinary resumed solution gate builds without warnings but fails
`PropagatedMessage_IsStoredRetrievedAndAcknowledged` (`Sent` expected, `Failed`
observed). The isolated case and subsequent full LXMF162/28 rerun pass; these
do not explain or close the original intermittent failure. Failure-only router
diagnostics now accompany the unchanged state assertion. Evidence remains under
`artifacts\tests\resume-resource-lifetime`. The later ordinary Windows
`parent-resource-unified-windows` gate passes build, all managed projects,
Pester tooling, strict compatibility aggregation and merged coverage against
stack2331aee. That pass is scoped to its retained source snapshot; it does not
explain the original intermittent propagation failure or accept later edits.

Nomad utilities C79 currently passes13 focused managed/reference/TCP cases on
each host. Three separate immutable native process cases pass per host for
owner/cancellation/restart and both-origin4-MiB authorized speed transfers,
refusal and recovery. Fresh guarded fixed-stack native candidates include
the proof measurements and whole-staging file deadline. The actual primary-controller
bridge uses original utility classes/Urwid widgets with a disclosed bounded
headless owner, not a primary application constructor or GUI. Primary/native
testing reproduces a required stack3cdf callback-lifetime race: a successful
primary sender normally closes before an external completed-Resource callback
can open already-disposed data. Passing repetitions do not resolve that gap.
The local independent stack commit2331aee fixes callback ownership, with no
Python mutation or teardown delay. Parent independently replays the selected
Windows/Linux actual-primary T362/T363 gates2/0 each, including sixteen spaced
both-origin4-MiB pairs in unchanged processes over at least600 seconds and
payload cleanup before/after shutdown. Windows's primary rncp unlink defect is
explicitly asserted. New hop/impaired, hours-long and maximum-size acceptance
remain unverified. Exact source/native hashes, commands and retained failures
are in [NomadNet's reference](nomadnet.md).

The preceding Sideband console candidate passes full managed2069/74 explicit skips and
enabled live4/0 on both hosts. Linux strict NativeAOT/help and five native cases
pass with selected SHA-256
`cdb9019e9b46e021f6c2e129e7af6966c48b87ccf50f83db50b7dcdc62a25da4`.
Windows's four existing native flows pass; added T321 profile/map long-path
restart fails the stack's raw Win32 atomic-publication limit. Its retained
`windows-sqlite-final` failure must not be relabelled accepted. SQLite itself
passes canonical extended-path/VFS and escaped immutable-URI managed cases.
Linux VFS512-byte pathname budget remains explicit;600-character cases skip
there. The exact public pending-dial task is now owned/joined on disposal and
voice fixtures bind their actual port0 server rather than releasing/rebinding
a probe socket. These preceding source boundaries are distinct from the current
Windows acceptance below.

A subsequent retained Windows hangup regression fails because hangup returns
before that same public dial task completes. Hangup now joins it, and all call
admission/busy checks retain the pending-dial reservation. All16 Windows voice
lifecycle cases pass, including16 immediate-hangup/healthy-next-call cycles.
The new Linux replay cannot start: WSL reports filesystem I/O errors reading
both ordinary/root account and locale files. Reclaiming79.94GiB of redundant
generated SkiaSharp package-symbol copies leaves the error unchanged. No shared
WSL shutdown, filesystem repair or distribution restart was attempted. This is
a current platform blocker (`/dev/sdd`, ext4 `emergency_ro`), not a Linux pass or a reason to waive the unchanged
assertions; previous Linux evidence retains its original source boundary.

The independent stack path fix is locally committed as
`e52da394658f6f6c723214a9f811552418789cb6` on2331aee, with no wire change or push.
Its Windows whole-stack result1855/77 is explicitly **serialized xUnit collection
acceptance** after two retained default-parallel timing failures; Linux full
default acceptance is1851/76. Parent independently replays the exact Windows
strict-native binary5/0, including four eight-process long-path identity
contention/restart cases (local, extended and authorized loopback UNC) and the
existing encrypted/compressed2.2MB resource. This is not a claim of default-
parallel Windows stack acceptance. The same old raw Win32 path defect
independently fails the retained parent C74 Windows replay2/4 at nested
ordinary paths; paths/assertions remain unchanged.

The preceding e52 integrated Windows solution builds without warnings and
passes2689 managed cases/146 explicit skips. Its guarded Sideband acceptance
passes2070/74, separately enabled live4/0 and strict NativeAOT/help/native5/0,
including unchanged T321 long paths and exact pending-dial hangup ownership.
Selected native SHA-256:
`475dded1fd88aab15f0c4ad7d1a05950c9d8b4b4a05bdbded3753e6603b5d200`.
Fresh guarded Nomad NativeAOT SHA-256
`34d1ff11278cf7679e5c973b71697f90008333cbabafb11c09a0e07de03f4110`
independently passes22 printing/ownership/private-mode checks and nine separate
node-migration/clipboard/utility cases. C74 repeats its real multi-megabyte
primary/node transfers after clean/abrupt native restart at longer nested paths,
not shortened workspaces. This same source-guarded Nomad candidate also repeats
actual-primary T362/T3632/0:601.876 measured endurance seconds, sixteen both-origin
4MiB pairs/128MiB payload, continued responsiveness, exact digests, no payload
leaks before/after shutdown and zero process exit codes. The complete unchanged
two-case run takes613.18seconds. Linux current-source/pin replay remains blocked.

The preceding Windows unified
`pwsh -NoProfile -File tests\run-tests.ps1 -Coverage -Pester
-ArtifactsPath artifacts\builds\fleet-e52-unified
-OutputDirectory artifacts\tests\fleet-e52-unified`
also passes all selected managed/Pester/strict aggregation checks. Coverage
unions ten reports by source line/branch; the ordinary run's explicit missing
live/native/hardware records remain visible rather than being promoted by
separate scoped gates. It does not explain the retained intermittent propagation
failure or establish complete application parity.

The automatic conversation-name correction adds independently executed
primary SQLite write observations and four regressions, including original
desktop controls. That preceding Windows solution builds with zero warnings/
errors and passes2693/146 explicit skips:

```powershell
dotnet build DotReticulum.Ecosystem.slnx -c Release --artifacts-path artifacts\builds\fleet-e52-integration --nologo -v:q
dotnet test DotReticulum.Ecosystem.slnx -c Release --no-build --artifacts-path artifacts\builds\fleet-e52-integration --logger trx --results-directory artifacts\tests\fleet-e52-auto-names --nologo -v:q
```

Fresh source-guarded Sideband managed2074/74, actual-primary live4/0 and
strict NativeAOT/help/native5/0 pass for
`d4d862a6dd4680ece5765b92f5b325cfc87c5e3c87e296c9fdcb5bc0e290190c`.
The native pair now also verifies automatic creation, alias clearing and
continued announce-name updates after restart. Separate Nomad/lxmd/rnphone
accepted candidates above are unaffected by this Sideband-only correction.
Current Linux evidence remains blocked, not inferred.

The refreshed unified gate also passes:

```powershell
& tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath artifacts\builds\fleet-e52-unified-auto-names -OutputDirectory artifacts\tests\fleet-e52-unified-auto-names
```

Its80 claims/345 selectors retain explicit unavailable live/native/hardware
records; ten coverage reports are unioned, not averaged. This ordinary gate
does not substitute for the separately opted-in native/live acceptance.

Fresh strict Windows e52 `lxmd` help/six unchanged native daemon cases also pass
(504.22seconds), including actual primary delivery, authenticated retrieval,
control/expiry timing and two-native/Python propagation with failed-peer restart.
Fresh `rnphone` help and four native cases pass with synthetic real-Opus phones,
CLI rejection and the existing phonebook/reload/identity-restart case now enabled
on Windows. Only the separate SIGTERM case retains its Linux-only skip; four
Linux device/service/virtual-audio cases skip in the Windows gate. Toolchain
assets verify SDK10.0.403 and compiler/runtime10.0.14. Exact native hashes and
topology boundaries are in [LXMF](lxmf.md) and [LXST](lxst.md); this is not physical
audio or a fresh Linux pass.

The unexplained original propagation failure also has a bounded no-build
20-repetition diagnostic replay against the2331aee compiled ecosystem snapshot:
all20 unchanged cases pass. Per-run TRX and test/library hashes remain in
`artifacts\tests\nomad-utilities\propagation-repeat`; this is additional evidence,
not retroactive root-cause attribution.

Sideband standalone startup C76/T290–296 records14 actual passing cases per
host:12 managed/reference/original-UI/actual-primary cases and2 strict native
offline cases. Enabled startup is measured at no less than8 seconds in both
actual primary and native-origin managed applications; disabled and pending
disabled startup, explicit announcements and identity restart pass direct TCP.
The owned delayed task is cancelled/joined on disconnect/disposal. This is not
silent mode and does not implement Android service auto-announcement jobs.
Receipts are `artifacts\tests\sideband-startup-announce\windows\c76.json` and
`/home/dennis/fleet-startup-announce-results/c76.json`; exact pins, fixture/native
digests, source snapshots, commands and remaining boundaries are in
[Sideband's reference](sideband.md#standalone-startup-announcements).
The initial integrated gate failed a remote-UI test relying on immediate startup
discovery. Explicit manual announcements now establish that test's precondition
without changing its five-second command assertions; the focused startup/remote
selection passes11/0 per host. The failed run remains recorded.
The complete post-fix command `tests\run-tests.ps1 -Coverage -Pester
-ArtifactsPath artifacts\builds\fleet-startup-announce-unified-v2
-OutputDirectory artifacts\tests\fleet-startup-announce-unified-v2` passes2,606
managed cases/141 explicit skips,62 Pester tooling cases, strict C76 catalog
aggregation and merged production coverage. Concurrent C74 node sources are
regression inputs, not independent migration acceptance.
The two native candidates retain dirty-source base `5c7b4f1`, embedded
`2.2.0.103+5c7b4f1e53`, SDK10.0.403/runtime and ILC10.0.14; guarded source
snapshots include the concurrent frozen shared LXMF C74 implementation.
Native offline process/profile flows are not native Python-peer elapsed-time
or GUI evidence.

Sideband manual synchronization C75/T280–286 records15 actual passing cases
per host:13 scoped managed cases and2 strict native offline cases.
The focused command passes14/0 each including the existing schedule-policy
regression. Nine independently executed primary UI-method/real-core/router
observations distinguish nullable manual count limits from unlimited scheduled
retrieval; synthetic dialogs/clock do not establish primary GUI/timing.
Actual primary applications and a real Python propagation/transport node
separately verify capped retrieval in both origins, remaining inboxes, native
identity/policy restart and explicit unlimited remainder. Native process/profile
acceptance and original managed controls remain separate evidence.
Receipts are `artifacts\tests\sideband-sync-limits\windows\c75.json` and
`/home/dennis/fleet-sync-limits-results/c75.json`; exact pins, fixture/native
digests, commands and safety/evidence boundaries are in
[Sideband's reference](sideband.md#manual-synchronization-limits).
The integrated command `tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-sync-limits-unified -OutputDirectory
artifacts\tests\fleet-sync-limits-unified` passes2,588 managed cases/138
explicit skips,62 Pester tooling cases, strict C75 catalog aggregation and
merged production coverage. Concurrent Nomad node-migration diagnostics are
regression inputs, not C74 migration acceptance. Both native candidates retain
dirty-source base `718daa8`, embedded `2.2.0.102+718daa84df`, SDK10.0.403
and runtime/ILC10.0.14. Subsequent shared-router changes are not retroactively
assigned to these native binaries.

Parent independently accepts Nomad client-router migration C72/T240–249:
eight owned hashes match, and each frozen replay verifies313 compiled
production/test sources,75 generated receipts, serviced toolchain, converter
and native hashes. Separate current-source managed acceptance passes258/9
explicit Linux-only skips on Windows and267/0 on Linux. Current-harness native
acceptance passes3/0 per host, including actual primary constructor cross-load
and application messages both ways after clean/abrupt native restart.
Each C72 receipt maps ten actual passing cases without omissions or skips:
`artifacts\tests\nomad-router\parent-current-windows\c72.json` and
`/home/dennis/fleet-router-parent-results/c72.json`.
Direct TCP client migration excludes propagation peers/messagestore and does
not establish new hop/loss, full configuration or newer dependency support.
Exact pins, fixture/native hashes, commands and bounded safety differences are
in [NomadNet's reference](nomadnet.md#bounded-primary-client-router-migration).
Frozen native candidates retain their actual dirty-source base `ed8f5de`,
embedded `1.4.4.102+ed8f5deb56`, not integration HEAD.
The integrated command `tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-router-parent-unified -OutputDirectory
artifacts\tests\fleet-router-parent-unified` passes2,573 managed cases/137
explicit skips,62 Pester tooling cases, strict C72 catalog aggregation and
merged production coverage. The ordinary resumed Release build also passes
without warnings and the complete solution test passes2,573/137.

Sideband transfer-limit C73/T250–258 records18 actual passing cases per host:
11 ordinary managed,3 actual-primary direct/Python-hop/.NET-hop,2 exact router
boundary/statistics and2 strict native offline cases. The selected managed
application command passes15/0 each, including separate reacceptance of the
existing C31 two-MiB transfer with explicitly increased limits. Default admission
now matches primary1,000,000 bytes rather than the old fixed8MiB value.
Runtime changes preserve links/identity and affect subsequent admission;
local attachment/storage bounds remain independent. Actual primary
propagation retry is explicitly disabled to isolate size refusal from its
documented callback defect. Fixture/native hashes, commands and remaining
boundaries are in [Sideband's reference](sideband.md#incoming-lxmf-transfer-limits).
Receipts are `artifacts\tests\sideband-delivery-limits\windows\c73.json` and
`/home/dennis/fleet-delivery-limits-results/c73.json`.
The integrated command `tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-delivery-limits-unified -OutputDirectory
artifacts\tests\fleet-delivery-limits-unified` passes2,573 managed cases/137
explicit skips,62 Pester tooling cases, strict C73 catalog aggregation and
merged production coverage. Concurrent Nomad router-import sources are
regression inputs, not independent C72 acceptance. Native candidates retain
their actual dirty-source build base `ed8f5de`, not integration HEAD.

Parent independently accepts Nomad compact-announcement C71/T230–239:
all11 owned hashes match, and each frozen replay verifies311 compiled
production/test sources,75 generated receipts, serviced toolchain, converter
and native hashes. Frozen and separate fresh-source managed runs pass251/9
explicit Linux-only skips on Windows and260/0 on Linux. Current-harness native
acceptance passes3/0 per host; each C71 receipt maps ten actual passing cases
without omissions or skips. Receipts are
`artifacts\tests\nomad-compact\parent-current-windows\c71.json` and
`/home/dennis/fleet-compact-parent-results/c71.json`.
Direct-TCP primary node/delivery announcements and native restart are not
PN-live, new multi-hop/loss or Urwid evidence. Exact pins, fixture/native hashes,
commands and safety/presentation boundaries are in
[NomadNet's reference](nomadnet.md#bounded-compactfull-announce-history-c71).
The integrated command `tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-compact-parent-unified -OutputDirectory
artifacts\tests\fleet-compact-parent-unified` passes2,553 managed cases/134
explicit skips,62 Pester tooling cases, strict C71 catalog aggregation and
merged production coverage. Native candidates retain dirty-source build base
`d59a781` and embedded `1.4.4.99+d59a7815c0`; later parent commits are not
retrospectively assigned to those binaries.

Sideband stamp C70/T209–218 records21 actual passing cases per host:16 ordinary
managed reference/persistence/lifecycle/original-UI cases,3 actual-primary live
cases and2 strict published native cases. Actual primary validates native
32-byte cost8 stamps; both delivery origins, independent invalid-stamp rejection,
disabled-cost retention and identity restart pass direct/Python-hop/.NET-hop.
Stale-cache injection uses the actual public SDK, not a reference replacement or
observed natural loss. Restart requires an actually new whole-second forwarded
announce, not a queued earlier event. Internal worker cancellation no longer
stops the node before graceful LXMF/voice teardown; no stack change is made.
Native offline settings/actual-primary profile restart is separate from original
managed Preferences and actual-primary network evidence. Invalid settings fail
atomically rather than reproducing primary silent setter refusal. Receipts are
`artifacts\tests\sideband-stamp-policy\windows\c70.json` and
`/home/dennis/fleet-stamp-policy-results/c70.json`; exact pins, fixture/native
hashes, commands, retained failures and remaining limits are in
[Sideband's reference](sideband.md#incoming-lxmf-stamp-configuration).
The integrated command `tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-stamp-policy-unified -OutputDirectory
artifacts\tests\fleet-stamp-policy-unified` passes2,552 managed cases/134
explicit skips,62 Pester tooling cases, strict C70 catalog aggregation and
merged production coverage. Concurrent Nomad compact-announcement sources
are regression inputs, not independent C71 acceptance. Native candidates
retain their actual dirty-source build base `d59a781`, not integration HEAD.

The resumed ordinary solution test exposed a playback pacing defect: backend
initialization could consume the first frame's20ms budget. `da8963c` opens the
fixed-format lease before timing writes and adds an80ms delayed-open regression,
without relaxing timing assertions. Focused media/repository cases pass45/1
explicit Unix-permission skip on Windows and46/0 on Linux; the complete resumed
Release solution test then passes2,553/134. An earlier isolated repository
handshake failure and the reproduced pacing failure remain recorded as failed
runs, not passes. This correction postdates the C70 native candidates and
does not relabel their tested source inputs.

Parent independently replays Nomad attachment `final-v2` without republishing:
Windows244/9 explicit managed skips and11/2 explicit native Linux-only skips;
Linux253 managed and13 native passes without skips. Each replay enforces309
compiled production/test and68 generated-source checks plus serviced toolchain,
converter and binary hashes. All11 owned source hashes match the frozen handoff.
Fresh current-source managed acceptance separately passes244/9 and253/0, and
current-harness published `AttachmentPath` passes2/0 on both hosts.
C68/T193–199,T208 records12 actual cases: Windows10 passes/2 explicit Unix-only
skips, Linux12 passes/no skips. Direct-TCP actual-primary attachment exchange,
explicit configured exports, failures and identity-preserving restart are
separate from retained transport-hop RRC/hosting regressions. Native bounded
paths/bytes, private staging and atomic non-overwrite safeguards deliberately
differ from primary check-then-write helpers. Native binaries retain their
actual dirty-source build base `ec06f2d` and embedded version, not integration
HEAD. Receipts are `artifacts\tests\nomad-attachment\parent-windows\c68.json`
and `/home/dennis/fleet-nomad-attachment-parent/c68.json`; exact pins, hashes,
commands and software/platform gaps are in
[NomadNet's reference](nomadnet.md#configured-terminal-attachment-export).
The integrated command `tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-nomad-attachment-unified -OutputDirectory
artifacts\tests\fleet-nomad-attachment-unified` passes2,529 managed cases/131
explicit skips,62 Pester tooling cases, strict C68 catalog aggregation and merged
production coverage. Hosted Build41502113530 and Test41643993895 remain successful
at their earlier commits; neither covers these unpushed changes.

Sideband historical-paper C69/T200–207 records28 actual passing cases per host:
executed-reference envelope/formatter vectors and bounds, atomic import
rollback/retry, durable imported provenance, original UI warnings and two
published process cases. Current paper and shared-contract selections pass28/2
explicit disabled-live skips and16/0 on both hosts. Strict Windows/Linux
NativeAOT binaries import actual primary pairs, export exact URI/PNG, independently
decode PNGs and hand them to unchanged primary recipient daemon cores with an
explicitly preloaded public source key. Recipient signature/content and
duplicate restart pass; separate native desktop restarts preserve message records
and render URI/QR offline. This is not new live Reticulum/topology or physical
camera/printer evidence. Import deliberately does not authenticate opaque
ciphertext or bind it to the separate signed plaintext.
Receipts are `artifacts\tests\sideband-paper-profile\windows\c69.json` and
`/home/dennis/fleet-paper-profile-results/c69.json`; native build inputs are dirty
source over `ec06f2d`. Exact pins, fixture/native hashes, commands and remaining
software/platform boundaries are in
[Sideband's reference](sideband.md#primary-historical-paper-envelope-migration).
The integrated command `tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-paper-profile-unified -OutputDirectory
artifacts\tests\fleet-paper-profile-unified` passes2,529 managed cases/131
explicit skips,62 Pester tooling cases, strict C69 catalog aggregation and merged
production coverage. Concurrent Nomad attachment-path sources are included in
that regression, not independently accepted as part of the Sideband claim.

Parent independently replays Nomad logging `final-v2` with
`Invoke-FrozenLoggingAcceptance.ps1 -Manifest <absolute-manifest>`:
Windows236/7 explicit managed skips and10/2 explicit native Linux-only skips;
Linux243 managed and12 native passes without skips. Each candidate enforces
307 compiled production/test and68 generated-source PDB checks, serviced
compiler/runtime receipts and frozen hashes. All10 owned current files and278
current compiled production sources match before parent documentation integration.
Fresh current-source converter-provisioned managed runs independently pass
236/7 on Windows and243/0 on Linux; the published logging selection passes2/1
explicit Unix-permission skip on Windows and3/0 on Linux.
C67/T190–192 records21 actual cases per host: Windows20 passes/1 explicit skip
with T191 correctly partial, Linux21 passes/no skips. Receipts are
`artifacts\tests\fleet-nomad-logging\parent-windows\c67.json` and
`/home/dennis/fleet-nomad-logging-parent/c67.json`. Direct-TCP actual-primary
messages, routing, failure/recovery and restart are distinct from preserved
RRC/form/hosting regressions. Native fail-closed log errors intentionally correct
a bounded-observed pinned RNS logging-lock deadlock; they are not identical
reference failure behavior. Exact manifests/native hashes, pins and remaining
software boundaries are in [NomadNet's reference](nomadnet.md#application-logging-configuration).
The final integrated command `tests\run-tests.ps1 -Coverage -Pester
-ArtifactsPath artifacts\builds\fleet-nomad-logging-unified -OutputDirectory
artifacts\tests\fleet-nomad-logging-unified-final` passes2,497 managed cases/129
explicit skips,62 Pester tooling cases, strict C67 catalog aggregation and merged
production coverage. The retained failed gate in `fleet-nomad-logging-unified`
exposes a Sideband fixture clock race, not a changed production future policy:
flooring the clock plus241 seconds could become valid after a second boundary.
Commit `ccc9cf3` keeps the240-second policy, asserts its exact fixed-clock cutoff
and next representable rejected timestamp, and uses an unambiguously future
atomic-import fixture. Fifteen focused cases and ten4-case rollback/recovery
repetitions pass independently on each host. No assertions or compatibility
requirements are disabled; the final full gate includes that correction.

Sideband voice-peer C66/T183–189 independently records22 actual passing cases
per host:10 Unit,9 ordinary Integration,2 actual-primary live policy cases and1
published-native profile/GUI case. Receipts are
`artifacts\tests\sideband-voice-peers\windows\c66.json` and
`/home/dennis/fleet-voice-peer-results/c66.json`. The focused managed regression
passes107/3 explicit platform skips on Windows and108/2 on Linux; the separate
actual-primary coordinator/shared-client command passes12 cases per host,
directly and through a .NET transport. Published `VoicePeerImport` passes once
per host without skips, and separate original GUI regression passes9/4 explicit
missing-profile skips each. Offline native rendering is not native Python-peer
calling. Linux uses an owned virtual Pulse server; Windows uses the disclosed
synthetic reference device backend. Exact pins/hashes, commands, safety limits
and remaining metadata/migration boundaries are in
[Sideband's reference](sideband.md#primary-voice-only-identity-migration-and-controls).
The integrated command `tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-voice-peer-unified -OutputDirectory
artifacts\tests\fleet-voice-peer-unified` passes2,497 managed cases/129 explicit
skips,62 Pester tooling cases, strict catalog aggregation and merged production
coverage. It includes concurrent Nomad logging source changes; those are not
independently accepted by this Sideband gate. Latest hosted Build41502113530
and Test41643993895 remain successful at their earlier recorded commits, not
the unpushed local integration.

Parent independently replays Nomad client-runtime `final-v3` with
`Invoke-FrozenClientRuntimeAcceptance.ps1 -Manifest <absolute-manifest>`:
Windows9 passes/1 explicit Linux-only RRC skip; Linux10 passes/no skips.
All ten owned current hashes and277 compiled production inputs match the
accepted manifests; each frozen candidate also verifies61 generated-source PDB
checks. Fresh current-source managed runs with the manifest-checksummed image
converter pass218/7 explicit Linux-only skips on Windows and225/0 on Linux.
C65/T181–182 records all10 new managed cases (8 Unit/2 Integration) and1
published actual-primary runtime case per host, all passing. Receipts are
`artifacts\tests\fleet-nomad-config\parent-windows\c65.json` and
`/home/dennis/fleet-nomad-config-parent-results/c65.json`.
The original native text/headless runtime, notification policy and direct-TCP
application restart flow do not establish Urwid equivalence, new hop/loss
coverage or complete configuration/logging/router/editor compatibility.
Exact frozen manifests, native hashes, source revisions and remaining
software contracts are in [NomadNet's reference](nomadnet.md).
The final integrated `tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-client-config-unified -OutputDirectory
artifacts\tests\fleet-client-config-unified` passes2,461 managed cases/128 explicit
skips,62 Pester tooling cases, strict C65 catalog aggregation and merged
production coverage. That run precedes the independently accepted Sideband
voice-peer evidence above.

The call-history integration gate
`tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-voice-profile-unified -OutputDirectory
artifacts\tests\fleet-voice-profile-unified` passes2,461 managed cases/128 explicit
skips,62 Pester tooling cases, strict catalog aggregation and merged production
coverage. Concurrent Nomad client-configuration work is present but is not
independently accepted by this run. Sideband profile regressions separately
pass83/3 explicit skips on Windows and84/2 on Linux; the actual-primary published
`VoiceImport` case passes once per host without skips, and the published GUI
selection passes8/4 explicit missing-profile skips each. C64/T176–180 records19
passes/1 explicit Linux-only metadata skip on Windows and20 passes on Linux.
Windows T177 remains partial; offline historical assertions do not establish
authentication, audio or live calling. Exact reference/native hashes, safety
differences and private-artifact paths are in
[Sideband's reference](sideband.md#primary-voice-history-migration).

Parent independently replays frozen Nomad RRC v5 on Windows/Linux with
`Invoke-FrozenRrcAcceptance.ps1 -Manifest <absolute-frozen-v5-manifest>`:
Windows7 passes/1 explicit Linux-only skip; Linux8 passes/no skips.
Each new migration case requires five complete unchanged primary startups,
then actual primary messages both ways through one unchanged transport-enabled
rrcd hub across initial, clean-restarted and abruptly terminated native
lifetimes, without overriding imported nick or manually rejoining rooms.
All362 frozen current-source inputs and276 compiled production PDB checks
match; exact manifests/native hashes, reference revisions and retained failures
are in [NomadNet's reference](nomadnet.md). Fresh parent current-source Windows
regression passes204/11 explicit skips without optional converters/live modes;
separate parent current-source runs with the manifest's checksum-verified
`DOTRETICULUM_NOMADNET_IMAGE_CONVERTER` pass208/7 on Windows and215/0 on Linux.
C63/T173–175 independently records23 migration,1 observer-isolation and1
published actual-primary case, all passing on both hosts, under
`artifacts\tests\nomad-rrc-migration\parent-record\c63.json` and
`/home/dennis/fleet-parent-rrc-record/c63.json`.
The final integrated `tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-rrc-unified -OutputDirectory
artifacts\tests\fleet-rrc-unified` passes2,433 managed/127 explicit skips,
62 Pester tooling cases, merged coverage and strict catalog aggregation.
Imported history is unauthenticated; full ConfigObj/router/UI and newer
dependency-minimum gaps remain required work, not passes.

The runtime-telemetry integration gate
`tests\run-tests.ps1 -Coverage -Pester -ArtifactsPath
artifacts\builds\fleet-runtime-unified -OutputDirectory
artifacts\tests\fleet-runtime-unified` passes **2,433 managed cases,
127 explicit skips and62 Pester tooling cases**, with strict catalog aggregation
and merged production coverage. Concurrent Nomad RRC tests are present in this
shared-worktree run, not independently accepted by it. Separate Windows/Linux
runtime acceptance passes26/26, no skips per host, including actual primary
reports both ways direct/Python-hop, rendering and primary sparse repacking.
C62/T168–172 records19 actual passing cases per host; the published native
collection/UI/restart case is separate from managed Python network exchange.
The final published `GUI` selection passes7 with4 explicit missing-profile
skips per host, retaining the offline manual-preview assertions. Exact source,
fixture/native hashes and boundaries are in
[Sideband's reference](sideband.md#automatic-reticulum-runtime-telemetry).

Hosted evidence is unchanged by these local milestones: latest observed
Test41643993895 succeeds at `0e49bee87c932fdf404949812a8420f83cb49e4a`,
and Build41502113530 succeeds at `89e948a90e9606bed331b71da6b787cace0bbd81`.
Neither covers unpushed runtime-telemetry/RRC work. The separate historical
badge-publishing41505064405 failure and subsequent skipped badge run are not
relabelled as compiler/test failures or new local acceptance. No workflow was
dispatched and no commit was pushed.

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
retained. Exact binary hashes/commands, profile bounds and remaining config/
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
been exercised on GitHub. The Test workflow now selects Unit and Integration on
`macos-latest`/`osx-arm64` using the verified managed artifact from the exact
Build. The latest hosted [Integration job](https://github.com/dennispayne/DotReticulum.Ecosystem/actions/runs/44910941829)
failed, including paths whose local file selection explicitly supports only
Windows and Linux; macOS Integration is not accepted. macOS native/tooling
compatibility is not run and macOS production coverage is not collected. The
live/GUI/hardware boundaries above remain unchanged.
