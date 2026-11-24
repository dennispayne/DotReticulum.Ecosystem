# NomadNet application reference

Experimental, independently authored .NET terminal application targeting primary
NomadNet **1.4.4**, not an official implementation or a full port. The application
reuses `ReticulumNode`, `ClientLinks` and `LxmRouter`; it does not duplicate LXMF,
paper encryption, propagation, Reticulum links or Resource protocols.
Required software and evidence gaps remain individually inventoried, including
complete migration/configuration semantics. Opted-in Windows executable
authorization generators have separate actual-primary and NativeAOT acceptance
below; Windows executable pages remain disabled. A bounded original page/file/
native-configuration editor and local guide are implemented and Windows-native
verified below; they are not the primary editor or copied guide. Scoped primary-peer utilities
and ten-minute transfer endurance now have separate Windows/Linux evidence;
current integrated Linux replay is blocked as recorded below. Missing features are
software gaps, not hardware or optional-platform blockers. Original terminal/RRC/
Kitty presentation differences do not establish full upstream UI parity.
Sideband has its own implementation and
inventory in [sideband.md](sideband.md); this document does not track it.

## Windows executable authorization generators (C110)

Windows `.allowed` companions are classified by `GetBinaryTypeW`, not their
extension. Win32/Win64 images require the existing `EnableExecutablePages`
opt-in; static identity lists do not. This does **not** enable Windows executable
pages. Generators execute directly, without a shell, through the existing
suspended-start private kill-on-close Windows job, limited to64 processes.
Stdout is bounded to64KiB; stderr is drained/discarded. A15-second execution
deadline, request cancellation and asynchronous host disposal join the owned
process tree, including when no observer subscribes.

The existing transport dispatcher bounds four active handlers and256 queued
requests; T1068 exercises actual concurrent TCP/link requests, four PE starts,
queued-link cancellation and continued service. No application semaphore or
second queue is introduced. **The execution deadline excludes prior transport
queue residence.** Its trusted arrival time is not exposed; remote-supplied
`RequestedAt` is not used as a local deadline.

The custom Unicode environment contains only `PATH`, `link_id`,
`remote_identity`, existing bounded `field_*`/`var_*` values and Windows-known-folder
`SystemRoot`. The shared launcher's null environment preserves its original
inherited environment and process flags. Validation rejects malformed,
case-insensitively duplicated and oversized entries. Nonzero-exit output,
CR/LF identity lines and malformed surrounding lines match executed primary
observations. Invalid/start/read/output/deadline failures deny with sanitized
diagnostics; throwing log observers do not disable service.

Actual pinned `Node.request_allowed` executes an independently authored
NativeAOT PE renamed `.allowed`: matching identities succeed, outsiders and
anonymous callers fail. The observer also exercises nonzero exit, malformed
lines, CR delimiters and2MiB stderr. `GetBinaryTypeW` recognizes PE type6 and
rejects plaintext with error193. Primary Windows `os.access(X_OK)` attempts
plaintext execution and raises `WinError193`; retaining native static lists
is an explicit safety difference. The observed `Node.py` SHA-256 is
`d48dc47e5c99378bf9b869c8766891154f06174a51c0d35c7120b5eab07324df`.

The actual primary `NomadNetworkApp`/RNS client separately requests the published
native host over direct loopback TCP: opt-in, lists, valid/malformed/overflow/
outsider recovery and clean native/primary restart preserve identity. An
oversized PE page fails as bounded static content without executing.

```powershell
pwsh -NoProfile -File tests\NomadNet.Tests\RunWindowsAuthorizationAcceptance.ps1 -Manifest artifacts\tests\nomad-windows-authorization\candidate-current.json -Results artifacts\tests\nomad-windows-authorization\parent-current
```

Independent parent replay passes497 ordinary cases/38 individually reason-bearing
skips,8 focused cases,1 actual-primary/native case and7 same-binary C102/C104/C109
regressions. Converter-enabled ordinary outcomes differ from the ordinary root
environment; neither set promotes skips to passes. Strict managed and win-x64
NativeAOT builds and native `--help` pass. Native SHA-256 is
`158917141c7fac80e849ff69829d842ca307ce02a8075f25ab01ec3fac23e53c`;
candidate SHA-256 is
`bd882db2001dbe0a4a816fbef262f9c531129a5072c8bc056b4a6b8ab1020663`.
Final managed acceptance uses the separately strict-built current assembly
after T603's test-only observer synchronization fix. Before creating that fresh
manifest, every original native, production, fixture, reference and other source
input was verified byte-identical; the sole changed test source matches the
recorded before/after build closure. `candidate-final2.json` and its accepted
parent replay remain historical, unmodified evidence.
Initial locked-PDB/linker disk-full attempts remain recorded. No new Linux,
hop/loss, hardware or exhaustive queue-latency claim follows. The primary's
newer declared dependency minima remain disclosed below.

## Functional Micron and hosted-node controls

### Bounded Micron grammar and original rendering (C104)

The original browser, editor preview, conversation and RRC renderers now activate
source-observed font/foreground/background controls, row alignment, headings,
custom folds, buffered actionable tables and per-image width/height/alignment.
Existing fields, submissions, anchors and partial replacement retain their
actionable metadata. Conversation/RRC consumers preserve primary-specific
alignment suppression. Monochrome and redirected output retain safe plain text;
original themes and bounded ASCII images do not copy primary assets or implement
Kitty graphics.

The independently executed corpus contains76 primary grammar inputs (75
successes and an explicit primary empty-heading `TypeError`), six partials, nine
image property/layout observations and three consumer pipelines. Native empty
headings safely omit the invalid fold and continue. Singleton/palette,
image-metadata and terminal-query seams are disclosed in
`GenerateNomadNetMicronGrammarVectors.py`; vectors are not a live-node or primary
fullscreen GUI claim. Native source/table/image/output bounds and Rune-safe
clipping do not reproduce the optional exhaustive `wcwidth` database.
Canonical Git parser SHA-256 is
`995a8fd8883ec57cd173d117b6758447e508ca2ddf96d8521260cc70e18a8e4e`;
the CRLF Windows checkout fingerprint is
`9932db8038b6380742e2586250dc77b2509b72d7ed8a6fdfd954f03602cf4e03`.
Both identify the same pinned source after line-ending normalization.

```powershell
pwsh -NoProfile -File tests\Pester\Invoke-NomadNetMicronGrammarAcceptance.ps1 -Candidate artifacts\tests\nomad-micron-contracts\final-candidate.json -OutputDirectory artifacts\tests\nomad-micron-contracts\parent-independent
```

Parent-independent replay passes15 focused cases,507 full Nomad cases/
19 explicit skips and4 Pester cases. T1052 and the eight previously accepted
editor regressions pass against this actual NativeAOT binary, SHA-256
`72e3b37b6dc2b959df225f5fbc2ef7f0cea5001902eb24475284de2de591860a`.
No required selected case skips; ordinary platform/other opt-in skips retain
individual reasons. The guard covers354 production,117 acceptance,1615
reference,10 toolchain and111 managed inputs.

### Hosted request statistics and profile activation (C107)

`node stats` shows actual hosted connections/page/file/media counters;
`node stats reset` durably resets connections/pages/files and deliberately
retains media, matching primary `NodeInfo`. Default-index handling does not
count as a page-file attempt. Registered page/file handlers count before
authorization/open failures; media counts only after path/key/extension/jail/
length validation, before authorization/open failures. A deleted real index
retains its registered-handler attempt semantics until rescan.

`storage\hosted-statistics.json` is a strict, bounded1KiB schema with four
nonnegative Int64 counters and atomic private publication. Five-second jobs
flush strictly after60seconds; clean disposal flushes even without a started
worker. Failed writes retain dirty state and report/retry; corruption refuses
startup without overwriting evidence. Saturation reports overflow instead of
Python's arbitrary-precision growth. Reset preserves media, including restart.
Offline profile import activates validated primary counters inside its existing
private staging transaction; invalid counters roll back the entire import and
never modify the source. These application counters are distinct from LXMF's
propagation-node counters.

Vectors execute actual primary handler/reset methods with disclosed synthetic
request/owner boundaries. Separate direct-TCP acceptance uses the actual primary
application/RNS client to request the published native host, including rejection,
reset and restart. It is not Urwid Browser, new hop/loss, image-decoder, physical
RF, current Linux or macOS evidence.

```powershell
pwsh -NoProfile -File tests\Pester\Invoke-NomadNetHostedStatisticsAcceptance.ps1 -Candidate artifacts\tests\nomad-host-statistics\final2\candidate.json -OutputDirectory artifacts\tests\nomad-host-statistics\final2\parent-independent
```

Parent-independent replay passes23 selected cases and3 Pester cases without
skips. Native SHA-256 is
`aba658dc1723e50c006da23573ed5bcd61c49f0a6deb707e48c400fc170a5383`.
Strict positive four-counter whole-profile activation and negative-counter
atomic rollback are also part of the full ordinary suite.

### Existing-peer synchronization and removal (C109)

`node-peers` exposes actual peer state, rates, transfer/sync limits, stamp
cost/flexibility, acceptance percentage, timing and retained unresolved count.
`node-peer-sync <hash>` selects only an existing resolved peer, requires strictly
more than10seconds since its last attempt and retains the three-minute
cancellation deadline. It does not create an unknown peer. `node-unpeer <hash>`
uses existing LXMF removal/persistence with the current-time peering guard and
supports a retained unresolved peer. It does not blacklist the destination or
edit static configuration: subsequent discovery/restart can peer again.

`GeneratePeerControlVectors.py` executes nine actual pinned `LXMFPeers`
callbacks; synthetic selection/router/clock/thread boundaries stop before dialog
construction and are not scheduling or wire evidence. The source filesystem
SHA-256 is
`7566c413f1d7b298dec6c764e0e8a14f7f7d6c2ec7825e93c595db2137063949`.
T1050 checks strict before/equal/after timing; T1051 rejects invalid timestamps.
T1052 is an explicit opt-in published-process check using a synthetic imported
unresolved peer with a current-clock timebase, missing-peer refusal, removal,
unchanged static settings and restart. Its development managed-apphost preflight
is not NativeAOT acceptance; the C104 operator separately requires the actual
published case to pass. No new active-peer Python wire/topology claim follows.

### Current configuration acceptance

The C93 selected-RNS policy candidate passes full371/17 Nomad managed tests,
strict Windows NativeAOT/help without warnings, and **3 native/reference
passes/0 failures/0 skips**. The previously red selected-RNS baseline assertion
now verifies actual supported policy activation while retaining malformed-input
and exact native/unrelated-source preservation checks. The exact binary SHA256 is
`0e41b20cedfc9c51947a63a9addf7cf9c3a10f49519f57b0293021fb31e40437`,
built against stack `4989f050aed865d16ed9f1460071c1a63cd4c7e9`.
Frozen production/current-LXMF/native/stack guards, source snapshots, T510–516
selector records and a fresh-child replay operator are in
`artifacts\tests\nomad-rns-policy`. Actual Python RNS observes explicit and
implicit replies from the configured Nomad owner across identity-preserving
restarts, plus disabled/nontransport negative cases after healthy reverse
traffic. The primary form fixture independently regenerates byte-identically.
Parent-independent frozen replay passes3/0/0 in `parent-final`. All four legacy
configuration-form cases also pass on this exact binary in `parent-legacy-forms`,
including supported flag persistence/restart, explicit panic safety refusal,
invalid-port rejection and unchanged unrelated/native settings. Whole-root
acceptance is recorded separately in [testing provenance](testing.md#baseline-provenance).

```powershell
& artifacts\tests\nomad-rns-policy\Invoke-SelectedNative.ps1 -EvidenceDirectory (Join-Path $PWD 'artifacts\tests\nomad-rns-policy\parent-final')
```

The preceding C92 global-RRC/history/style candidate passes full367/17 managed tests,
strict Windows NativeAOT/help, three new native/reference cases and the existing
actual-primary RRC presentation regression: **4 passed/0 failed/0 skipped**.
The exact native SHA256 is
`e359f1b226aae1b24d67278b88f5d3f38894f0751eef6c818d9565fb822a0cca`.
The source/current-LXMF/native guards, source snapshots, nonoverlapping
T490–499 selector records, exact environment and fresh replay operator are in
`artifacts\tests\nomad-rrc-settings`. Ten controls are functional;
`enable_esoterics` is explicitly a primary placeholder, not a fabricated feature.
Parent-independent guarded replay passes4/0/0 under
`artifacts\tests\nomad-rrc-settings\parent-final`, including actual primary
and unchanged rrcd messages both ways, restart and the existing Python-hop
presentation regression. Local rendering observations are not new wire evidence.
The parent whole-solution coverage/Pester/strict-catalog gate passes2775 managed
cases/151 explicit skips with91 claims/449 selectors. Its source-identity union
covers21411/28821 production lines and14420/23264 branches; opt-in native/live
receipts are separate, not converted from skips.

```powershell
& artifacts\tests\nomad-rrc-settings\Invoke-SelectedNative.ps1 -EvidenceDirectory (Join-Path $PWD 'artifacts\tests\nomad-rrc-settings\parent-final')
```

The preceding bounded C89 configuration slice has parent-independent full360/17 managed
acceptance, strict Windows NativeAOT/help, native18/0 including actual installed
Vim ConPTY2/0, and broader printing/node-migration/clipboard/utility29/0.
The refreshed candidate includes the final LXMF cancellation-readiness API;
its exact SHA-256 is
`5abcbf19347b222db09ee7ee616b0cba5c2dc056d211e57d8d11f907c79d1bd8`.
The original `f846045e...` candidate/receipts remain preserved, not relabelled.
Frozen source/native guards, regenerated primary observations and parent
receipts are under `artifacts\tests\nomad-config-forms\parent-lxmf-admission`
and `parent-lxmf-admission-broader`.

```powershell
& artifacts\tests\nomad-config-forms\Invoke-SelectedNative.ps1 -EvidenceDirectory (Join-Path $PWD 'artifacts\tests\nomad-config-forms\parent-lxmf-admission')
& artifacts\tests\nomad-editor\Parent-BroaderAcceptance.ps1 -Candidate artifacts\tests\nomad-config-forms\candidate.json -OutputDirectory artifacts\tests\nomad-config-forms\parent-lxmf-admission-broader
```

All71 implemented form fields (primary printing omitted, C78) have explicit functional editing/reset/persistence
dispositions. TextUI/utility effects have separate bounded C95 acceptance below;
bounded grammar/rendering now has the separate C104 disposition; current Linux replay
remain required/unverified as detailed below. Native form cases use actual
piped processes; separate ConPTY cases are not full Urwid or physical terminal
acceptance. The broader29 repeat actual primary multi-megabyte node
transfers/restarts, not new impaired-network or prolonged-operation evidence.

## Primary reference and licensing

The primary repository was retrieved with native .NET `rngit` from
`rns://7649a50d84610232d1416b41d2896aff/reticulum/nomadnet`, through the configured
Reticulum TCP bootstrap and multiple transport hops. It was not seeded from the
GitHub mirror. The fetched default branch and advertised refs included 96 refs
and 6,727,500 Git-bundle bytes. `git fsck --full --strict` passed. This establishes
Git-object consistency, not publisher authentication or release-signature trust.

- Commit: `f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`.
- Tree: `223037b20cb53ec1d9e73ba9e4a66886de459ebf`.
- Declared version: `nomadnet/_version.py`, 1.4.4.
- Source snapshots are ignored artifacts, not distributed application source.

| Git blob | SHA-256 |
|---|---|
| `LICENSE` | `3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986` |
| `setup.py` | `0378491e83e0504ce5f066520782942e81db975e47e95c033595a325256d49a2` |
| `nomadnet/NomadNetworkApp.py` | `c36ce4c537fe5af534910d1f5200d2a2ea5dd50f74bf9e4157222eff15614eb2` |
| `nomadnet/Node.py` | `1e2ba96f6dc040fcbe19e55bd2eb33961d9fb9443f7e590e02aeb32894aa695c` |
| `nomadnet/Conversation.py` | `83bbfc71a7fc2b0953a2aac283a609c068dbbe38daea3abc788e4266ca4a98cd` |
| `nomadnet/RRC.py` | `195e3dd62ab6678f5192b8b9c5a3c3e399e519018f3fb2216a16315c32895925` |

The primary `LICENSE` is GPLv3, while `setup.py` has an MIT classifier. This is an
unresolved upstream metadata conflict, not permission to select MIT. No upstream
source, translated source, UI assets, fonts, sounds or templates are copied.
Resolve derivative-distribution questions with the rights holder/legal review.
The ecosystem Reticulum License and notices remain unchanged.

Primary NomadNet declares **RNS >=1.6.0 and LXMF >=1.2.2**. The exercised
environment remains RNS 1.5.7
`a29bfb51c468945c96a1ee22cae0114e993e2391` and LXMF 1.2.1
`e8ba02483217af43be284a72f1c059c730b7d047`. Selected working application flows
against these older dependencies do not establish full 1.4.4 compatibility.

## Observable contract inventory

`implemented` is limited to the named behavior; `partial` has explicit omissions.
Underlying-library acceptance is not automatically application acceptance.

| Contract and upstream sources | Current status and boundary |
|---|---|
| Entry point, identity, delivery; `nomadnet.py`, `NomadNetworkApp.py` | **partial**: persistent identity and LXMF destination; `--config`, `--rnsconfig`, `--version`, `--daemon`/`-d`, `--console`/`-c`, `--textui`/`-t` accepted. Imported `client.user_interface=none` selects headless operation; `text` selects the original console, not Urwid. As in the primary, `--textui` cancels `--daemon` regardless of argument order but does not override configured `none`. Daemon ignores terminal input and serves until cancellation; controlled SIGTERM/Ctrl+C cancellation exits successfully after disposal. Native terminal/daemon/init/offline directory import hold an exclusive profile lease. Application logging supports configured levels/file/stdout and the primary's daemon-gated console override, independently of original terminal output/BEL. Extensions: `--help`, `--init`, `--status`, explicit non-overwriting core `--import-config`, additive conflict-refusing `--import-directory`, offline non-overwriting `--import-profile` retaining the primary identity with an explicit selected RNS configuration. |
| Client announcements; `NomadNetworkApp.START_ANNOUNCE_DELAY`, `__jobs`, `announce_now`, `apply_config`, `set_display_name` | **implemented bounded subset**: configurable three-second startup, strict periodic interval after90seconds with five-second checks, manual delivery-only or combined announces, durable successful timestamp, runtime display-name and stamp-cost edits through existing LXMF APIs. Display-name changes save without announcing or replacing the identity/destination. Retains unused `enable_client` metadata rather than inventing delivery-disable semantics. Native safety differences for names, fresh null timestamps, failed background work, serialization and interval bounds are explicit below. |
| Direct/opportunistic conversations; `Conversation.py` | **partial**: direct text, durable inbound/outbound history, displayed delivery state, per-peer direct/propagated/opportunistic preference, unread/failure counters, explicit viewed/read state, latest-activity listing, deletion/failed purge and plain-text export. Bounded outbound supervision retries failed direct delivery once through a selected propagation node, preserving the LXMF hash, timestamp, binary content and fields. Cancellation, rejection and preparation errors do not trigger fallback. Native opportunistic text is received by the actual primary; direct reply/viewed state and both applications' read counters are exercised. Primary conversation/container/attachment import and inert `.index` preservation with honest historical authentication are implemented below; full conversation UI remains missing. |
| File/image/audio attachments; `ConversationMessage` helpers | **partial**: `attach`, `image`, `audio`; bounded extraction of LXMF fields 0x05/0x06/0x07 and safe display names; durable payloads outside history JSON. File attachments live-verified both ways, including 2,200,000 bytes. Typed image references support explicit/automatic original ASCII conversation previews, using the existing converter/cache and never/manual/auto/always controls; this is not primary UI parity. Audio is stored/sent as encoded bytes. The pinned conversation helpers save encoded audio; an upstream recording/playback/transcoding composer was not found there, and no such native codec/device capability is claimed. |
| Paper; `Conversation.paper_output`, `LXMessage` | **partial**: `paper` generates encrypted `lxm://` URI through LXMF; `ingest` uses the router's duplicate-aware ingestion. `paper-qr`/`paper-save` save PNG/URI and outbound history; `qr` exports an existing URI, `qr-show` renders explicit black-on-white half-blocks. All QR encoding uses shared `LxPaperQr`, not a second codec. Executed primary save-QR vectors and terminal module tests exist; selected application acceptance is recorded below. Printing is not implemented (C78); image-import/scanner and physical scanning remain unverified. |
| Printing; `NomadNetworkApp.should_print`, `print_file`, `print_message`, `applyConfig`; `Conversation.paper_output` | **not implemented (deliberately omitted)**: no incoming, explicit or paper-QR printing. An imported primary `[printing]` section is ignored with a warning; see C78. |
| Propagation selection and retrieval; `NomadNetworkApp.py`, `Conversation.py`, `Directory.py` | **partial, live-verified core**: persisted manual/trusted automatic selection, explicit propagated send, limited/manual/scheduled sync, asynchronous status/cancellation and direct-failure fallback; hosted propagation uses `LxmRouter`. Actual primary Python client retrieves from native PN after restart/deletes messages; native client retrieves from actual primary Python PN after restart/deletes messages. Scheduled retrieval and direct-failure fallback after peer exit are exercised. Configured static-peer transfers are application-verified both ways on Windows/Linux through a Python transport, with restart, real loss and2.2MB attachment assertions. Storage/limit/cost/static/priority settings use existing LXMF options. C109 adds actual peer inspection, strict manual-sync grace and durable unpeering without rewriting static settings. Storage-pressure eviction-order acceptance and additional peer failure/topology cases remain unverified. |
| Directory and announces; `Directory.py`, `Conversation.received_announce` | **partial**: bounded known-destination snapshot and decoded names; persisted trusted/untrusted/unknown/warning contact classification, exact primary saved-name collision policy, independent blacklist applied to the existing LXMF router and filtered application peer view, delivery preference and local notes. Saved-node listing/opening, primary rank/trust/name ordering, explicit browser identification consent and existing associated-node trust updates are operational. Explicit trusted node identities and validated active propagation-node classification drive automatic selection separately. Current and legacy primary saved-directory/ignored-list import is operational through offline CLI and terminal, with atomic additive merging, idempotence and conflict rejection. Bounded compact/full announce history, separate explicit transient import, per-type/search/name-or-address terminal presentation and restart persistence are implemented below. Complete Urwid stream/node-directory presentation remains missing. |
| Hosted pages; `Node.register_pages`, `serve_page`, `announce` | **implemented bounded subset**: `nomadnetwork.node`, UTF-8 name app-data, original default index, nested static pages, six-second startup/configured periodic announces, independent page/file rescans, explicit refresh and bounded handler inspection. New/deleted paths update handlers; static `.allowed` identity lists are enforced. Node announces chain existing propagation behavior. C107 implements source-specific attempt counters, reset, persistence and profile activation. Original default content deliberately replaces upstream assets; executable platform boundaries are separately inventoried below. |
| Hosted files; `Node.register_files`, `serve_file` | **implemented bounded subset**: `/file/...` uses the existing metadata-bearing Resource response (`name` UTF-8 bytes), up to 16 MiB. No hidden or `.allowed` files are advertised. Both serving directions verified with 2.2 MB files; streaming beyond that bound is not supported. |
| Executable pages and executable `.allowed`; `Node.serve_page`, `request_allowed` | **implemented bounded platform subsets; form-browser evidence partial**: explicit opt-in, direct executable without shell interpolation, restricted request environment, bounded output and15-second execution deadline. Linux pages retain earlier native/Python-transport form evidence; Python-to-native executable-form browser acceptance remains unverified. Windows executable pages stay disabled, matching primary page handling; PE `.allowed` generators have C110 actual-primary/native, cancellation/descendant/disposal/recovery acceptance above. Existing bounded transport queue residence is outside the execution deadline; static Windows lists deliberately remain supported despite primary WinError193. |
| Media; `Node.serve_media`, `convert_media_to_webp` | **partial, live-verified core**: `/media` dictionary requires `path` and `key`; jailed, authorised native `.webp` responses and PNG/JPEG/BMP/GIF/TIFF conversion through configured external FFmpeg. Primary quality85/dimension1200 and content-hash cache naming, 256-file/192-MiB quota, corruption regeneration and restart/cache-only serving are exercised. `show-media` provides ASCII/explicit ANSI previews; bounded browser inline ASCII uses imported/runtime never/manual/auto/always policy, a separate durable remote cache and joined cancellation. Original conversation previews reuse the conversion cache and attachment storage. Actual primary `ImageData` decodes native-served images and native previews primary-served images on Windows/Linux; exact additional inline acceptance is recorded below. Native conversion explicitly selects one frame; complete animated/multipage, alpha/EXIF behavior and Kitty graphics remain unverified/missing. |
| Browser; `ui/textui/Browser.py`, `FileBrowser.py` | **partial**: bounded page/file/media retrieval with explicit identification consent, links/bookmarks, reload, session navigation history with back/forward and submitted-field replay, durable bounded cache with upstream `#!c=<seconds>` and 12-hour default. Editable forms/variables, anchors/folds, automatic initial/periodic partial retrieval and in-place textual composition with actionable child fields/links/media; forced `p:<id>` updates use current combined fields. Navigation/disposal cancels and joins updates. Submitted and partial requests bypass cache. Automatic/manual inline ASCII previews preserve descriptor/form indices and collapsed visibility, including partial media; a local interactive file picker supports transactional attachment composition. Upstream Kitty widgets and complete layout remain missing. |
| Micron; `ui/textui/MicronParser.py` | **implemented, bounded functional scope**: source-observed line/inline grammar, fonts/foreground/background, row alignment, headings/custom folds, fields/actions/anchors, buffered actionable tables, per-image width/height/alignment and partial metadata composition. Browser/preview/conversation/RRC owners activate applicable controls while retaining source-specific filtering, monochrome and pipe sanitation. Executed independent reference and native-owner evidence remain distinct from live-node or identical Urwid/wcwidth/Kitty behavior; see C104. |
| RRC; `RRC.py`, `ui/textui/Channels.py` | **partial, live-verified core**: identified `rrc.hub` link, CBOR v1 HELLO/WELCOME and negotiated limits; join/part; messages/actions/notices/errors; nicknames, bounded full-hash/prefix membership with promotion and mentions; ping/pong; bounded hashed notice/MOTD resources; `/list`/`/who` and parsed directory state including fragmented notices; persisted automatic list/who policy with silent replies; session-local active/read/unread/mention state; bounded per-room retention; manual reconnect and opt-in exponential reconnect; durable hub nick/rooms/history. Actual primary client and real rrcd exchange messages/actions both ways, including hub/client restarts, automatic roster learning and active/unread transitions. Markdown/Micron switches render live/history bodies against executed primary widget vectors; JSON string composition preserves multiline/raw content. Global retention/load filtering/notice cleanup and original functional nick/mention/timestamp colours, spacing, wrapping and collapsed-pane gutters are implemented below. `enable_esoterics` is a primary placeholder, not a missing invented feature. Complete Channels/Urwid layout remains missing. RRC is its own upstream CBOR-over-Link protocol, not LXMF multicast. |
| UI; `ui/TextUI.py`, `ui/textui/*`, guides/examples | **partial**: original standard-console command loop, local attachment picker, bounded notification policy and opt-in OSC52 content/URL copying. Original bounded page/file/native-config editing, explicit Micron preview and local guide navigation pass separate Windows NativeAOT acceptance below. Complete primary editor/ConfigObj/Urwid/guide-asset workflows remain missing or intentionally different, as individually inventoried below. Current Linux native replay is blocked by WSL storage failure. Utilities have their separate bounded contract below; original presentation is not Urwid layout parity. GraphicalUI/WebUI abort upstream: **upstream-placeholder**, not an implemented or required invented GUI. |
| Network utilities; `ui/textui/Utilities.py`, `ui/textui/Transport.py` | **implemented bounded subset**: current-owner tables, identity/path resolution/expiry, receipt-backed probes, named rncp sending and identified streamed speed tests. Executed reference vectors, managed TCP and native-pair evidence are separate from independently replayed actual-primary Windows/Linux direct-loopback gates, including unchanged-process ten-minute service operation. Stack callback ownership is fixed in the selected local stack commit; see C79 below. No new hop/loss, maximum-size, full primary GUI or physical-RF claim. |
| Persistence/recovery; `NomadNetworkApp.py`, `Directory.py`, `Conversation.py`, `RRC.py` | **partial**: versioned JSON settings/state with atomic replacement, bounded contact policy/ignore state/messages/unread/failure alerts/bookmarks, LXMF storage, attachment byte files, page/conversion caches and separate bounded CBOR RRC hub profiles/history; application identity/history/policies/caches and RRC nick/rooms/history survive restart. Explicit core INI import stages a new native profile and reports every unsupported key. Saved-directory/ignored-policy import preserves existing native history/settings/identity, rejects conflicting contacts atomically and persists through terminal/daemon restarts. Offline profile import retains identity/signed ratchets, bounded client-router tickets/stamp-cost and duplicate histories, supported peer settings with backup fallback, contacts/conversations/attachments/paper and bounded primary RRC hubs/nicks/joined/parted rooms/retained history without silently authenticating cached claims. Separate bounded propagation-node migration has immutable-source and current integrated Windows native acceptance; current Linux pin replay is blocked by WSL storage failure. Complete ConfigObj compatibility and full restart policy remain required gaps. |

### Configuration audit

The running .NET application uses `settings.json`, not an automatically loaded
upstream ConfigObj/INI. Existing schema-1 files without new properties still
load with defaults. The explicit core importer below translates only named
contracts and reports unsupported keys; it is not complete configuration parity.

| Native setting | Default / behavior |
|---|---|
| `displayName` | `NomadNet user`; 1–80 non-control UTF-16 code units; runtime editing also requires strict UTF-8 |
| `enableClient` | true for legacy native profiles; false when absent from imported primary configuration; metadata only, matching the primary's unused runtime flag; does not disable delivery |
| `userInterface`, `notifyOnNewMessage` | text2 /true; explicit ConfigObj `client.user_interface` supports none0/text2, case-insensitively. Missing UI retains the native text default; it does not reproduce an invalid primary configuration's unset UI. `--daemon` forces headless unless `--textui` is also present; configured none stays headless even with `--textui`. Enabled notifications write/flush BEL only after successful inbound persistence in text mode; daemon and muted profiles emit none. Notification failure is contextual and isolated from persistence/subsequent service. Changes to interface selection require restart; this is not Urwid or audible/hardware acceptance. |
| `logLevel`, `logDestination`, `logFile` | Fresh/legacy native defaults4 /file1 /null (`<profile>/logfile`), including daemon. Explicit import clamps Python-style decimal log levels0–7, selects file only for case-insensitive `file` and stdout0 for every other destination. Missing level/destination import as null, retaining the selected RNS level/stdout. A logfile without file destination is inert. Custom paths are literal: relative to process CWD, no home/environment expansion or automatic parent-directory creation. Details and intentional failure-policy differences below. |
| `clientAnnounceAtStart`, `clientAnnounceIntervalMinutes` | true /360; three-second startup announce, periodic checks after90seconds and then every5seconds; interval30–10080minutes |
| `lastClientAnnounce` | absent initially; saved only after successful delivery announce; null suppresses periodic announcements until first manual/startup success |
| `composeInMarkdown`, `rrcRenderMarkdown`, `rrcRenderMicron` | true /true /true, matching actual primary constructor defaults; outgoing ordinary LXMF gets renderer2 only when enabled; paper does not get this field; RRC live/history display uses its independently persisted switches |
| `enableNode`, `nodeName` | false; absent name derives from display name |
| `nodeAnnounceIntervalMinutes` | 360; validated 1–10080 |
| `nodeAnnounceAtStart` | true for legacy native profiles; false when absent from imported primary node configuration; controls the six-second hosted node announce and its additional scheduled propagation announcement |
| `nodePageRefreshIntervalMinutes`, `nodeFileRefreshIntervalMinutes` | native defaults 5/60 minutes preserve earlier five-second scans; imported primary defaults0 disable rescans; independently validated finite0–10080 minutes |
| `enableExecutablePages` | false; explicit Linux execution/authorization-generator opt-in |
| `mediaConverter` | `ffmpeg`; local executable name or absolute path; no shell, 1–1024 printable characters; required only for conversion/preview, not cached or native WebP serving |
| `imageLoading` | numeric never0/manual1/auto2/always3; default2. `images <mode>` persists policy; `images load` explicitly loads, `images reload` refetches media without replaying the page/form |
| `inboundStampCost`, `acceptInvalidStamps` | null / false; stamp cost null or 1–254; existing LXMF enforces required stamps unless explicitly accepting invalid stamps |
| `pagesPath`, `filesPath`, `downloadsPath` | null uses native `storage\pages`, `storage\files`, `downloads`; explicit paths override those roots; remote names still cannot escape selected roots |
| `attachmentSavePath` | null/empty falls back to `downloadsPath` or native `<profile>/downloads`; explicit core/profile import expands the current user's `~`, preserving relative and literal environment-variable paths. Only explicit `attachment <id> <index>` exports create this directory. The existing named-output form still exports to downloads and refuses collisions. |
| `enablePropagation` | false; requires `enableNode`; maps the hosting settings below onto existing `LxmPropagationOptions`, not another propagation implementation |
| `propagationTransferLimitBytes`, `propagationSyncLimitBytes` | 256,000 /10,240,000; effective sync is at least transfer. Native safety bounds: transfer1,000–64MiB, configured sync1,000–256MiB |
| `propagationStorageLimitBytes`, `propagationStampCost` | Fresh/imported profiles2,000,000,000 /16, matching the actual primary node defaults; existing native files without the storage property retain their earlier500MB quota, which is made explicit on next save. Native storage5,000–16GiB and cost13–256 |
| `propagationMaxPeers` | null maps onto existing LXMF default20; explicit0–65,536. Static peers can exist with the dynamic peer cap0 |
| `propagationStaticPeers`, `prioritisedDestinations` | empty; at most128 validated16-byte hashes per list: respectively `lxmf.propagation` peer destinations and `lxmf.delivery` priority destinations, not `nomadnetwork.node` addresses. Import preserves valid static duplicates as upstream does; priorities are deduplicated. New policies are copied across application snapshots/updates |
| `propagationNode`, `usePropagation` | null / true; `propagation <hash|auto|off>` persists manual, trusted automatic or disabled client selection |
| `trustedNodes` | empty; at most 128 `nomadnetwork.node` hashes, not delivery/propagation hashes |
| `periodicLxmfSync`, `lxmfSyncIntervalMinutes`, `lxmfSyncLimit` | true / 360 / 8; interval 1–525600 minutes, limit 0–100000 (0 is unlimited); five-second scheduler after 90-second startup deferral |
| `lastLxmfSync` | absent until first attempt; persisted UTC timestamp before retrieval; failure/cancellation do not erase it |
| `tryPropagationOnSendFail` | true; one propagation attempt after failed direct delivery if a node is selected |
| `deliveryTransferLimitBytes` | 1,000,000; validated 1 KiB–64 MiB. Independent attachment extraction limit is 16 MiB. |

### Configured terminal attachment export

`client.attachment_save_path` is an **implemented bounded contract**, not a
whole-client parity claim. `GenerateNomadNetAttachmentPathVectors.py` executes
the unchanged primary daemon constructor and actual terminal
`_save_attachment_to_disk` / `_copy_attachment_to_dest` helpers. Public vectors
record Git-blob SHA256, including `Conversations.py`
`f1b47c7d93db25e8ddb582ca735631963beab6990e7373e78a27ff9a81ad0d6e`.
Host-specific actual imports and named-user expansion observations are retained
separately and asserted against each host's executed result, rather than
pretending Python's Windows and Unix results agree. Native rejection of that
unsupported expansion is also exercised without creating a target profile.
Primary dependency minimums remain unsatisfied by the pinned RNS/LXMF environment
described above; no primary source or logger-lock shim is used.

The primary starts with `attachment_save_path=None`, applies `os.path.expanduser`
to configured values, and uses the downloads directory for null/empty values.
Relative paths are relative to process CWD, not the profile or imported source;
environment variables remain literal. Default-destination export creates missing
parents only on the explicit user save action. Primary safe filenames strip
controls, path prefixes, colon prefixes and leading dots; file collisions use
`base_1.ext`, `base_2.ext`, with the last extension retained. The unchanged helper
copies stored attachments or writes in-memory bytes, reports failed saves, and
continues after recovery. The native implementation reuses existing LXMF fields,
attachment storage, safe-name logic and checksum verification; it does not add
a field/wire format or a second media decoder.

Terminal usage:

```text
attachment <message-id> <index>
attachment <message-id> <index> <output-name>
```

The first form exports the stored filename to the configured attachment path,
with automatic collision suffixes. The second preserves the existing explicit
name/downloads behavior and never overwrites. Both config-only import and offline
full-profile import persist the setting through restart without granting startup
write authority to an imported external directory. Configured logging, original
terminal/status/BEL, identity, profiles and router state retain their existing
ownership and behavior.

Intentional native safety boundaries:

- Paths are at most2048 UTF-16 code units, without controls or whitespace-only
  values; empty is valid fallback. Only current-user tilde expansion is supported.
  Unknown/named-user tilde paths are rejected rather than guessing another home.
  Python on Windows guesses a sibling home even for a nonexistent username;
  Unix leaves that unknown-user spelling literal.
- Export validates a bounded regular `.name` sidecar and checksum-verified stored
  bytes before creating a destination. Invalid UTF-8/OS filenames, Windows device
  names, oversize/corrupt content and linked/non-regular storage are refused.
  Native safe names retain the existing200-code-unit bound and fallback naming;
  no new historical filename reconstruction is claimed.
- Exports are staged, Unix owner-read/write, then atomically published without
  replacement. Linux uses `renameat2(RENAME_NOREPLACE)`: ordinary `File.Move` was
  independently found to allow duplicate successful destinations under concurrent
  Unix calls. Windows uses its non-replacing move; macOS ARM64 uses
  `renamex_np(RENAME_EXCL)` and fails explicitly if the filesystem does not
  support it. All treat existing directories
  as occupied names, refuse linked destination ancestors, bound suffix attempts
  to1000, and remove staging files on failure/cancellation. These are stricter
  than primary helper check-then-write behavior. No hostile concurrently replaced
  ancestor-directory sandbox guarantee is made.

`T193–198` cover actual-reference configuration/names, malformed and oversize
inputs, inert import/persistence, atomic/concurrent collision exhaustion,
pre-cancelled export, blocked directories, invalid sidecars/checksums,
non-regular files, Unix permissions/symlinks and healthy recovery. The
hosted macOS ARM64 [Integration run 45105470510](https://github.com/dennispayne/DotReticulum.Ecosystem/actions/runs/45105470510)
passed four export cases, including concurrent collisions, recovery and links;
the Linux-only permission/FIFO case skipped. The overall macOS job failed on
other tests. The
`AttachmentPath` Pester selection (`T199` and `T208`) executes the actual user terminal
commands on the selected published binary, primary application file exchange in
both directions, configured and explicit exports and same-identity restart.
Physical GUI/file-picker behavior is not exercised or claimed by this slice.
Utilities and other complete configuration/presentation contracts remain
required elsewhere. Bounded propagation-node migration and the omitted printing contract are described below.

### Printing is not implemented (C78)

Printing is deliberately omitted. Primary `NomadNetworkApp.py:709–791,1235–1282`
(`should_print`, `print_file`, `print_message`, `applyConfig`) and
`Conversation.py:358–409` (paper-QR printing) are not reproduced: there is no
incoming-message printing, explicit message print, `paper-print` command, print
template or print-command execution. Explicit ConfigObj migration tolerates a
primary `[printing]` section (`print_messages`, `print_command`, `print_from`,
`message_template`) by ignoring it with the warning "The primary [printing]
section is ignored: printing is not implemented."; nothing from it is persisted
or exported. The configuration form has no printing section. Paper messages
remain available as URI/PNG/terminal QR output (`paper`, `paper-qr`,
`paper-save`, `qr`, `qr-show`).

### Bounded compact/full announce history (C71)

The actual primary `NomadNetworkApp.py:155,930–932,1382–1386`,
`Directory.py:44–83,116–240` and `ui/textui/Network.py:279–591` at
`f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2` define this contract.
“Compact” does **not** shorten a row: on each new arrival it removes previous
announces from that destination **within that type**, then prepends the new row.
Full mode retains repeats. Each type retains256 rows; combined enumeration is
nodes, peers, propagation nodes, not a globally timestamp-sorted list. Changing
mode does not rewrite existing history; the next compact arrival removes all
previous same-type/destination rows. The primary's raw absent-option default is
false, but its generated default configuration says `yes`. Native new profiles
use true; explicit configuration/profile imports with an absent option use false.
The ConfigObj boolean spellings and malformed boolean rejection are independently
executed by `GenerateNomadNetCompactAnnounceVectors.py`, as are the actual primary
constructor, receive methods, save/reload and30 headless entry-widget observations.
The fixture records exact Git-blob SHA256 values and the unchanged RNS/LXMF pins.

Native settings expose `compactAnnounceStream`; the core/profile configuration
importer supports `[client] compact_announce_stream`. Terminal commands:

```text
announces [nodes|peers|pn] [name|dest] [search]
announces mode compact
announces mode full
announces import <offline-primary-profile>
```

The first command defaults to nodes, reports search-filtered counts across all
three types and shows the chosen type. Search matches case-insensitive **raw
decoded announce data**, not the displayed destination hash or saved contact name,
as in the primary. A destination view does not change search semantics. Rows use
local `HH:mm:ss` for today or `yyyy-MM-dd` otherwise, normalized/sanitized names,
34Unicode-scalar truncation plus ellipsis and address fallback. Original terminal
type markers `N`/`P`/`S` and explicit `trust=<level>` replace theme glyphs/colours.
The native presentation always uses the safe-name mode; arbitrary terminal
controls and symbols are not enabled by importing `sanitize_names=no`. Unicode
normalization/category tables follow the host .NET implementation rather than
claiming identical rendering for every Python/terminal Unicode version.
No Urwid layout, mouse/focus, popup announce-info, per-row delete or pixel
equivalence is claimed by this bounded presentation.

Live validated node/delivery/active-PN announcements are registered through the
existing stack APIs. Delivery wire app-data is converted to the display-name bytes
stored by primary `Conversation.received_announce`; path responses are excluded.
Ignored destinations are filtered by native contact policy. Native history is
atomically stored in `storage/announce-stream.json`, separately from saved trust
and conversation state. `announces import` explicitly reads the primary
`storage/directory` stream; it never creates identities, paths, trust or fresh
network observations. Exact duplicate records import idempotently; merging more
than256 records/type fails without changing existing history, rather than silently
evicting previously imported rows. The older `--import-directory` and profile
archive behavior remain unchanged; transient migration is a separate explicit
operation. Invalid records, legacy boolean type tags, out-of-range/nonfinite
timestamps, app-data over4096bytes and stream files over7MiB are rejected.
Primary directory reads retain the existing5MiB bound. Receive persistence uses a
bounded128-entry queue: overload/failures are reported, not silently labelled
complete capture. Observer failures are reported independently; no subscriber is
required. Disposal deregisters handlers and joins queued/admitted writes.

Evidence and exact catalog selectors (C71/T230–239):

| TestId | Actual selector / cases |
|---|---|
| T230 | `FullyQualifiedName=NomadNet.Tests.NomadNetCompactAnnounceTests.ConfigurationMatchesExecutedPrimary`: generated/absent/yes/no/on/False defaults, malformed booleans and config import. |
| T231 | `FullyQualifiedName=NomadNet.Tests.NomadNetCompactAnnounceTests.PresentationAndRawSearchMatchExecutedPrimary`:30 primary widget suffixes, truncation, safe names, address fallback/toggle, date boundary, raw search and malformed UTF-8. |
| T232 | `FullyQualifiedName=NomadNet.Tests.NomadNetCompactAnnounceTests.MalformedPrimaryAndBoundsAreRejected`: timestamp/type/hash/data/MessagePack/file/per-type bounds. |
| T233 | `FullyQualifiedName=NomadNet.Tests.NomadNetCompactAnnouncePersistenceTests.ReceiveSequencesCapsAndRestartMatchExecutedPrimary`: six constructor policies, every observed receive sequence, same-kind repeats, per-type caps and settings/history restart. |
| T234 | `FullyQualifiedName=NomadNet.Tests.NomadNetCompactAnnouncePersistenceTests.PrimaryImportIsAtomicIdempotentAndDoesNotCompactHistory`: repeated offline records retained until next live compact arrival; idempotence and malformed rollback. |
| T235 | `FullyQualifiedName=NomadNet.Tests.NomadNetCompactAnnouncePersistenceTests.CancellationObserverFaultsDisposalAndFailedWriteRecover`: cancelled write/no publication, blocked storage/recovery, faulty observer/logger isolation, mode switch, concurrent admitted writes/disposal/restart. |
| T236 | Pester `NomadNet bounded compact announce stream.T236 persists configured modes imports primary history and presents terminal views across restart`. |
| T237 | Pester `NomadNet bounded compact announce stream.T237 captures actual primary application announcements in compact and full modes with healthy restart`. |
| T238 | Pester `NomadNet bounded compact announce stream.T238 regenerates independent primary constructor history and widget vectors`. |
| T239 | `FullyQualifiedName=NomadNet.Tests.NomadNetCompactAnnouncePersistenceTests.RegisteredHandlersBoundQueueFilterPathsAndRecoverWithoutObservers`: signed native announce inputs through real handlers;128-queue overload reporting, display-name envelope conversion, path-response/invalid-PN exclusion, joined disposal and healthy service without observers. |

Focused selector: `FullyQualifiedName~NomadNetCompactAnnounce`.
The managed suite is260cases: Windows251passed/9explicit Linux-only skips;
Linux260passed/0skips, with the recorded reference converter selected.
Strict Windows/Linux NativeAOT publish uses SDK10.0.403/runtime and compiler10.0.14,
both trim/AOT warnings-as-errors flags, native `--help` and the three
`CompactAnnounce` Pester cases (all pass on both hosts).
Those Pester tests skip explicitly unless
`DOTRETICULUM_NOMADNET_COMPACT_ANNOUNCE=1` and require the selected binary, Python,
RNS, LXMF and primary source paths when enabled.
Native network evidence is an actual unchanged primary application over direct
loopback TCP: delivery/node repeats into native compact/full history and recovery
after native restart, plus native delivery announces received in the actual
primary compact stream. Test-only interface settings are `announce_cap=100`,
`bitrate=10000000`, `ingress_control=no`; the thin adapter spaces the two independent
primary delivery/node announcements by250ms to avoid its immediate outbound queue.
Earlier unspaced attempts and their failures are retained, not counted as passes.
This does not establish propagation-node traffic, multi-hop/loss topologies,
physical network/hardware, GUI or complete application equivalence.
Primary NomadNet1.4.4 declares newer dependency minimums; these observations use
the unchanged pinned RNS1.5.7/LXMF1.2.1, not a claim that those minimums are met.

Frozen candidates and no-republish operators live under
`artifacts/tests/nomad-compact/frozen-win-final` and the native Linux workspace's
`artifacts/tests/nomad-compact/frozen-linux-final`. Manifests preserve actual
embedded Git build versions, binary/fixture hashes, compiled-source PDB checks,
generated-source receipts, converter/toolchain/reference paths and host skip
reasons. They describe a dirty candidate built on `d59a781`, not a relabelled later
integration commit. Parent independently replays both frozen candidates,
verifying all11 owned hashes,311 compiled production/test PDB checks and75
generated-source receipts per host, plus compiler/runtime/converter/binary hashes.
Fresh current-source managed acceptance separately passes251/9 on Windows and
260/0 on Linux; the current native harness passes all three cases on both hosts.
C71/T230–239 independently maps all10 selected passing cases per host.
Parent operator `artifacts\tests\nomad-compact\Parent-CurrentAcceptance.ps1`
uses the recorded manifest environment with explicit source/build/result roots;
receipts are `artifacts\tests\nomad-compact\parent-current-windows\c71.json`
and `/home/dennis/fleet-compact-parent-results/c71.json`.
Fixture SHA-256 is
`4d8aaf05c33ee5e26e61c9e93b7798b842707af57d98c3b623b035cb63785d17`;
native Windows SHA-256 is
`2dc8a710e1a916477523ad652fa8faa474666b40f19c23eb500c298cf31562d0`;
native Linux SHA-256 is
`976d73d15c0dd57bc1491a954d313953c4d25dcff70c64d0682068c9abb3508e`.
Embedded version remains `1.4.4.99+d59a7815c0`, not parent integration HEAD.

### Configured attachment export acceptance

Scoped Windows/Linux acceptance uses SDK10.0.403 and runtime/NativeAOT
compiler10.0.14, with both `ILLinkTreatWarningsAsErrors=true` and
`IlcTreatWarningsAsErrors=true`. The source base is `ec06f2d`; uncommitted
NomadNet changes and the parent's stable shared LXMF paper formatter are captured
as actual PDB/current-source checks, not relabelled as code from that HEAD.

| Selected verification | Windows | Linux |
|---|---|---|
| Converter-enabled full NomadNet managed suite |244 passed /9 explicit Linux-only skips |253 passed /0 skips |
| Native attachment + logging/runtime/profile/directory/RRC/hosting/paper selection |11 passed /2 explicit Linux-only skips |13 passed /0 skips |
| Primary constructor/helper observations |5 common vectors plus host-specific named-home observation |same common fixture; independently executed Unix named-home result |

Windows skips are nine managed Unix execution/link/permission cases and two
Pester Unix logging-permission/actual executable-form RRC cases. Missing software
is not treated as an optional deferral. Native attachment exchange is direct TCP
with the actual unchanged primary application; it includes both attachment
origins and configured terminal exports across clean native restart. Headless
Windows runtime cases separately use abrupt termination. The retained RRC cases
also exercise a Python transport; this slice makes no new attachment-hop/loss
or physical-input claim.

Final native SHA256:

- Windows: `62b19295f1b7bf50982bfd8ef59f47f8d07da30a066767b6aefeaede12bacaab`.
- Linux: `c0f965f0e7c56e40e2d574528fe03b0141ce93ec804c399784803254947cefb4`.
- Public observation fixture: `4b2cfdbd7e6a0990c67d9d34b8231daefb1f34bcb1124afbc73c1c3d38c02ecd`.

The frozen per-host handoffs are
`artifacts\tests\nomad-attachment\frozen-win-final-v2` and native Linux
`/home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/tests/nomad-attachment/frozen-linux-final-v2`.
Each includes immutable source/test/fixture/operator snapshots, binaries,
managed assemblies, PDB inputs and generated-source receipts, exact toolchain,
actual import observations, retained failure records and a manifest. Run its
`Invoke-FrozenAttachmentAcceptance.ps1 -Manifest <manifest.json>` for checksum-
and-current-source-checked replay without rebuilding or republishing.

Parent independently replays both exact frozen candidates without republishing:
Windows244 managed passes/9 explicit skips and11 native passes/2 explicit skips;
Linux253 managed and13 native passes without skips. All11 owned current hashes
match the handoff; each replay verifies309 production/test portable-PDB source
checks and68 generated-source checks, actual compiler/runtime receipts and
converter/native hashes. The embedded native version remains
`1.4.4.96+ec06f2d600`, not the later integration HEAD.
Separate current-source converter-provisioned managed runs pass244/9 on Windows
and253/0 on Linux. Current-harness `AttachmentPath` passes2/0 on each host using
those same source-verified published binaries. C68/T193–199,T208 records12 actual
cases per host: Windows10 passes/2 explicit Unix-only skips, Linux12 passes.
T208 maps the existing application-runtime case separately because it now also
verifies both actual-primary attachment origins and configured exports; T199
isolates terminal failure/recovery. Receipts are
`artifacts\tests\nomad-attachment\parent-windows\c68.json` and
`/home/dennis/fleet-nomad-attachment-parent/c68.json`. Exact parent operators are
`artifacts\tests\nomad-attachment\ParentAccept.ps1` and the frozen replay command
above; logs retain both independent runs. This bounded acceptance does not
establish whole-application parity.

### Application logging configuration

`logging.loglevel`, `logging.destination`, `logging.logfile` and daemon/console
precedence are implemented as bounded application-owned logging contracts.
Authority is `NomadNetworkApp.__init__`/`applyConfig` and `nomadnet.main` at the
primary revision above, plus `RNS.log` at the pinned RNS1.5.7 revision.
`GenerateNomadNetLoggingVectors.py` executes actual unchanged constructors,
ConfigObj/defaults, configuration application and logging calls at severities0–8.
Its sixteen deterministic cases and nine CLI cases per configuration include
absent settings,0/7 clamps, default/custom/relative/literal-tilde paths, ignored
logfile options, unknown destinations, Unicode/large/underscored integers and
forced stdout. Actual reference writes also establish5MiB rotation/recreation.
Timestamp-disabled
reference output establishes exact labels and severity filtering, not invented
messages. The fixture records primary source Git-blob SHA-256 and RNS logger
SHA-256 `ed6cd970da6e19a0fdc34aae74897f876917879eb02cf6aacc86eb9ae13ebbd9`.
Per-host actual imported paths are retained with generated observations.

The CLI computes force-console from the **original presence of both daemon and
console flags**, before text mode cancels daemon. Consequently `-c` alone does
not override file logging; `-d -c` does; `-d -c -t` still forces stdout even
though it selects the text command loop. Order and short/long aliases do not
change this. Configured `none` UI does not itself enable force-console.
Default file routing applies in both text and daemon modes: the primary
does not contain a separate daemon-only destination default.

Both config-only and full offline profile import persist supported logging;
unrelated settings updates retain it across restart. The native JSON uses
numeric destination0/stdout and1/file. Logging is established before network
startup; Reticulum's instance callback, LXMF and application hosting/announcement/
delivery/image/RRC diagnostics feed the same owned logger. Unlevelled native
library diagnostics receive explicit native severity (LXMF/RRC/propagation
notice3, failed work error1); their text/severity is not claimed identical to
Python's internal logs. Startup/configuration logs are notice3; persisted/queued
message identifiers are info4. Private message bodies and keys are not logged.
Reticulum's configured timestamp switch is retained.

Original command/status output, propagation progress presentation, conversation
rendering and the notification BEL are **not** RNS log records and are not
silenced or redirected by logging configuration. Help/version, init/status,
offline-import reports and fatal CLI errors also remain console outputs.
This is not Urwid's stderr-capture workaround or primary `errors` file parity.
One cannot establish file logging merely by redirecting console output.

Ownership and intentional safety differences:

- The logger never changes a static/global handler or another application's
  logger. Synchronous serialized writes provide backpressure without an
  unbounded queue; each message is limited to16Ki UTF-16 code units and terminal
  control characters are removed. A failing sink records its fault, reports a
  contextual stderr error and cancels this application's lifetime.
- File writes append UTF-8 with native line endings, close each write, and rotate
  to one replaceable `.1` backup after exceeding RNS's5MiB threshold.
  Existing oversized user files are not proactively truncated. Writes racing
  disposal become inert; cancellation is checked before work; disposal is
  explicit and does not own/close the process console.
- Bad/unwritable paths fail visibly with exit1 and no success-shaped fallback.
  The generator also executes upstream's actual file-write failure on a bounded
  daemon observer thread. Pinned RNS1.5.7 sets its emergency-console flag but
  deadlocks recursively acquiring its non-reentrant logging lock; the call does
  not finish or print the intended fallback errors within the one-second join.
  No RLock shim or primary source patch is used. This native correction is
  tested, not concealed as identical failure behavior. Healthy restart recovers.
- Paths must be1–2048 non-control characters; unsupported empty/malformed paths
  are rejected during migration/save, rather than delaying failure to the first
  upstream write. Imported custom paths are explicit operator-selected log
  destinations, not automatically activated hosted/executable/network roots.
- Invalid integer levels fail rather than succeeding with a default. Complete
  ConfigObj grammar, hot reload/configuration editing,
  arbitrary RNS callback destinations and exact Python message wording are not
  claimed by this bounded contract.

Logging acceptance is opt-in with `DOTRETICULUM_NOMADNET_LOGGING=1`,
`DOTRETICULUM_NOMADNET_BINARY` and the existing pinned primary/Python variables:

```powershell
dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --filter "FullyQualifiedName~NomadNetLogging"
pwsh -NoProfile -File tests\run-pester.ps1 -Path tests\Pester\NomadNet.Tests.ps1 -Tag Logging
```

The native cases execute default/daemon routing, both CLI aliases and
text/daemon precedence, low/high clamps, relative paths, absent-setting
inheritance, nonexistent-parent/directory-target failures, runtime sink failure
with nonzero exit and healthy restart. Linux additionally removes actual Unix
write permission and restores it before recovery. The messaging case uses the
actual primary daemon core over direct TCP, not a .NET loopback: four profiles
start twice, receive eight primary messages, and text profiles originate four
replies observed by the primary. File/stdout log records and independent BEL
policy survive restart. Windows headless exit is abrupt; Linux uses clean
SIGTERM. No new transport-hop/loss, hardware, complete terminal or full dependency
parity claim is made.

Selected logging candidates are strict NativeAOT builds with
SDK10.0.403/runtime and ILC10.0.14:

| Validation | Windows | Linux |
|---|---|---|
| Complete current NomadNet managed suite, verified converter enabled |236 passed /7 explicit Linux-only skips |243 passed /0 skips |
| Logging reference/failure/lifecycle cases within that suite |18 passed |18 passed |
| Parent current-source published logging selection |2 passed /1 explicit Unix-permission skip |3 passed /0 skips |
| Selected native logging + client/profile/directory/RRC/hosting regression |10 passed /2 explicit Linux-only skips |12 passed /0 skips |
| Strict trim/ILC publish, help/version and published feature flows |passed |passed |

Native SHA-256: Windows
`19c56b51bfb14e2ad30db9404b6e337f076ad6075e363f9a03d3fa318d91f43a`;
Linux
`c42e95bfeba5c0719c2033913a1b37dac0692a36e68daf3cba679f31d2d300ee`.
Source/binary/managed-test freezes, production/test portable-PDB source and
embedded-generated-source checks, actual-import records, compiler/runtime
receipts, exact commands and bounded no-republish replay operators are retained
under `artifacts\tests\fleet-nomad-logging\frozen-win-final-v2` and native Linux
`/home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/tests/fleet-nomad-logging/frozen-linux-final-v2`.
Build/freeze HEAD is `aab67c01fd901a496075867ff8f775ca35be1fa5`;
compiled-source hashes, not HEAD alone, identify the uncommitted candidate.
Parent integration/commit is separate and no release/push is performed here.

Parent independently replays each exact frozen `Invoke-FrozenLoggingAcceptance.ps1
-Manifest <absolute-final-v2-manifest>` without rebuilding or republishing.
Windows records236 managed passes/7 explicit skips and10 native passes/2 explicit
Linux-only skips; Linux243 managed and12 native passes without skips.
Before integration, all10 owned current files and278 compiled production source
hashes match per host; each replay verifies307 production/test portable-PDB
source checks and68 generated-source checks. Manifest SHA-256 values are Windows
`bae5133d4b27ac3145ab82ce6211314ca65cd3b40993832ea1bf724514cd1d34`
and Linux
`91fbf5f6c19151524b18875e2475eb7b6376f9fd5998534e066c5c5556b79641`.
Serviced runtime/compiler receipts, the converter checksum and all frozen
file hashes are enforced before execution.

Fresh parent current-source managed runs with the manifest-checksummed converter
also record236/7 explicit Linux-only skips on Windows and243/0 on Linux.
C67/T190–192 records21 actual cases per host:18 managed and3 native. Windows
has20 passes/1 explicit Unix-permission skip, so T191 is correctly partial;
Linux has21 passes without skips. The existing actual-primary client-runtime
case is mapped separately as T192 because it now also exercises configured
application logs independently of status/BEL. Parent receipts are
`artifacts\tests\fleet-nomad-logging\parent-windows\c67.json` and
`/home/dennis/fleet-nomad-logging-parent/c67.json`; parent full frozen replay logs
are `artifacts\tests\fleet-nomad-logging\parent-windows-replay.log` and
`parent-linux-replay.log`. Source freeze/build HEAD remains the value above,
not the later parent integration HEAD.

### Client runtime selection and notification acceptance

`GenerateNomadNetClientRuntimeVectors.py` executes the unchanged primary
constructor with `daemon=True`, then reloads the same ConfigObj and executes
unchanged `applyConfig` and `notify_message_recieved`. Captured text/none,
default/enabled/disabled notification observations cover six configurations.
Its separate execution of unchanged `nomadnet.nomadnet.main` observes seven
argument combinations by replacing only the final `program_setup` boundary
with an argument recorder. This proves CLI selection, not primary TextUI startup.
No Urwid screen, microphone, printer or private traffic is opened. The primary
source is not modified; actual module paths are recorded separately so the
fixture is canonical LF and independently identical on Windows/Linux.

The fixture records Git-blob SHA-256s at the pin above:
`NomadNetworkApp.py` `c36ce4c537fe5af534910d1f5200d2a2ea5dd50f74bf9e4157222eff15614eb2`,
`nomadnet.py` `b35bb029b83fc879111466a183607fa62224fce8f7dd5307412c4ef35d47b902`,
`ui/__init__.py` `c75b4642104362f1d5f4a338a08241cdf6740954009d4ab8093c24542f9692f9`,
`ui/NoneUI.py` `019588a54c41b3fba999733eef3834ba423f5e60731cf0a245130a486ea62ba8`
and `ui/MenuUI.py` `2debeae357ef063735b91f25ccc1dec5de98d2a115af9c0d75f2139a5da0695d`.
`NomadNetClientRuntimeVectors.json` SHA-256:
`92d439b75ef3967f0ba4fe907ed7bca8defb72896bcf9db04931dddd24789075`.

The `ClientRuntime` Pester case passes separately on Windows/Linux against fresh
strict NativeAOT candidates (SDK10.0.403/runtime and ILC10.0.14). It regenerates
the fixture, imports each immutable source config, then tests four profiles
twice: text/notify with `-d -t`, text/muted with `--textui --daemon`,
configured none with `--textui`, and text forced headless with `-d -c`.
All eight starts receive and persist actual primary-application LXMF messages;
four text starts also send back to the actual primary. Text notifications emit
BEL exactly when enabled; no headless start emits it or consumes a queued
`quit`. Settings and the delivery identity survive restart. Text exits cleanly;
Linux headless exits cleanly on SIGTERM, while Windows headless is explicitly
abrupt termination. A menu-placeholder import fails without creating a target.
These are direct TCP application flows, **not** transport-hop, packet-loss,
physical-audio or newer-dependency-minimum acceptance.

Managed full NomadNet regression with the selected verified
`DOTRETICULUM_NOMADNET_IMAGE_CONVERTER` passes **218/7 skipped** on Windows and
**225/0 skipped** on Linux. The ten added cases cover executed vectors, malformed
and placeholder settings, observer absence/failure/recovery, config-only and
identity-bearing profile import, source immutability, atomic refusal and legacy
settings defaults. Focused selector:
`--filter "FullyQualifiedName~NomadNetClientRuntime"`.
The seven Windows skips are pre-existing Linux-only OS-resource cases.

Native candidate SHA-256s:

| RID | SHA-256 |
|---|---|
| win-x64 | `331ca8da3fbab82a63376fb6b4660d1414de24afe9eccc3f1b043e5759857221` |
| linux-x64 | `3612c8cc53587ec6c0e085d9550d81e4385058b8290fd40ce8b05729863d42dc` |

Both binaries embed build version `1.4.4.91+aa2e3ed9ab`; this identifies their
build HEAD, **not a commit containing these uncommitted changes**. Frozen
source/PDB checks and manifests identify the actual compiled candidate; the
native stack remains `3cdf5bee5adbd1da903a163882a31e53c3057aec`.
Build outputs use distinct `artifacts\builds\fleet-nomad-config-*` roots (Linux
under `/home/dennis/dotreticulum-ecosystem-acfd3d43`), not shared Windows/Linux
intermediates.

Reproducible operators and manifests are ignored local evidence:
`artifacts\tests\fleet-nomad-config\frozen-win-final-v3` in the repository and
`/home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/tests/fleet-nomad-config/frozen-linux-final-v3`.
Each includes the frozen native binary, application/transitive source, fixtures,
operators, compiler-package/toolchain receipts, portable PDBs, extracted generated
sources and source-check records (277 production-source plus61 generated-source
PDB hash checks per host). Runtime-download receipts and the actual ILC response
file both identify NativeAOT runtime10.0.14. Run the operator with explicit PowerShell:

```powershell
& <manifest-PowerShell> -NoProfile -File <frozen>\Invoke-FrozenClientRuntimeAcceptance.ps1 -Manifest <frozen>\manifest.json
```

Select the serviced `DOTNET_ROOT`/PATH on Linux. The operator validates every
frozen file/PDB/compiler/converter hash, uses an explicit native working directory
and a bounded ten-minute child process, and requires exact selected totals
(Windows9 passed/1 explicit Linux-only skip; Linux10 passed/0 skips). Selected
tags are `ClientRuntime`, `Native`, `DirectoryImport`, `ProfileImport`,
`RrcImport`, `RrcPresentation`, `Rrc`. Every `ClientRuntime` logical case is
recorded in its retained `acceptance.json`; complete replay output is
`last-replay.log`. Existing separate RRC/form/profile/native-hosting regressions
do not expand this configuration slice's direct-network claims.

### Offline primary profile import

```text
nomadnet --import-profile <stopped-primary-profile> --config <absent-native-profile> --rnsconfig <explicitly-selected-RNS-directory>
```

This is a usable bounded identity/conversation/RRC migration, **not complete primary
profile parity**. Stop the primary process before importing. Import itself never
opens Reticulum or contacts peers. The target must not exist; an existing native
profile is never merged or replaced. `--import-config` and `--import-directory`
remain independent operations with their existing behavior.

The inventory below was checked against primary
`f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`; its Git-blob SHA-256s are in the
provenance table above and `NomadNetProfileImportVectors.json`. The generator
executes the complete unchanged `NomadNetworkApp` constructor on the main thread,
then actual `Directory`/`Conversation`/`LXMessage` persistence APIs from an
observer. It does not substitute an application namespace, copy implementation
code, monkey-patch the reference or fabricate a Python profile format.
The RNS1.5.7/LXMF1.2.1-versus-declared-minimum discrepancy remains applicable.

| Primary persistence contract and source | Native import disposition |
|---|---|
| `storage/identity`; `NomadNetworkApp.__init__` | **implemented**: exactly64 private-key bytes loaded through existing `Identity.FromPrivateKey`; public delivery address retained. Native imported identity is profile-owned `identity`, not another RNS configuration's `nomadnet_identity`. Old native profiles without this file retain their original RNS-storage identity behavior. Both LXMF and new native RRC sessions use the imported identity. |
| `storage/lxmf/ratchets/<delivery>.ratchets`; actual delivery destination registration | **implemented bounded subset**: validate the identity-signed file with existing `SignedDestinationRatchetFile.Decode`, enforce the native512-key limit, zero all temporary secret buffers, then preserve exact bytes in the existing router's expected path. Only this identity's delivery ratchets are activated; client-router cache migration is detailed below. |
| `storage/lxmf/available_tickets`, `outbound_stamp_costs`, `local_deliveries`, `locally_processed`; `LXMRouter` constructor/save APIs | **implemented bounded client subset**: validate primary schemas and activate within the same private absent-target transaction, using the existing LXMF stores for load/expiry/ownership. Preserve incoming/outgoing tickets, ticket-delivery interval, announced stamp costs and delivered/processed duplicate histories. Node/unknown/interrupted files stay explicitly not imported; no queued messages or links are invented. |
| `config`; `apply_config` | **partial**: existing explicit core importer translates supported keys and reports unsupported keys. Page/file/download overrides are cleared and executable pages stay disabled. Global RNS configs, interface profiles, external paths, executable pages, cached remote material and licensed assets are not activated or copied. The separately selected RNS directory is saved as `rnsConfigDirectory`; runtime `--rnsconfig` may explicitly override it. |
| `storage/peersettings` and `.bak`; constructor fallback loop and `save_peer_settings` | **implemented bounded subset**: display name, saved propagation-node selection, last delivery announce and last LXMF sync become native settings. Invalid primary MessagePack falls back to a valid backup with an explicit warning; no valid primary/backup fails without creating a target. Both original files remain inert archives. Counters, quit-confirm, node announce timestamp and unknown keys are archived, not asserted to be live native equivalents. No source file is renamed to `.corrupt`. |
| `storage/directory`, `ignored`; `Directory.save_to_disk`, `_persist_ignored_list` | **implemented** through the existing directory importer/policy: saved contacts, trust, independent ignored hashes, preferred delivery, rank, notes and identify consent retained. Transient announces are counted and archived, not converted into saved trust. |
| `storage/conversations/<peer>/<message-hash>`; `Conversation.ingest`, `LXMessage.write_to_directory/unpack_from_file` | **implemented bounded subset**: containers are opened with existing `LxMessage.UnpackContainer`, not a second wire parser. Participants must match the imported local identity and conversation directory. Native history retains ID, timestamp, title, text, renderer, historical state/method and attachment access. Generating/outbound/sending records become failed offline history, never pending network work; rejected/cancelled/paper states remain historical. Binary/non-text or oversized histories fail explicitly rather than truncate/evict. |
| Conversation directories, `unread`, `failed`; `conversation_list` | **implemented**: preserve empty conversations, directory activity timestamp, unread/failure counts and their restart/deletion behavior. Empty marker files mean1; malformed or out-of-bound counts fail rather than become a silent default. Native bounds remain explicit below. |
| Conversation `.index`; `ConversationMessage.read_index/write_index/to_index_entry/restore_from_index` | **implemented archival plus explicit historical claims**: exact bytes preserved; container content is authoritative for displayed history. Cached `signature_validated` is recorded separately as `historicalSignatureValidated`, never treated as fresh verification. All additional cached state, transport descriptions, sort timestamp, renderer, attachment names and RF observations remain in the inert archive. Corrupt caches are reported and bypassed; stale entries without a container are reported, not invented as messages from cached text. |
| `storage/attachments/<original-message-hash>/manifest`, `file_<n>`; `extract_attachments_from_lxm`, `strip_attachments_from_file` | **implemented bounded subset**: validate names, stored-file grammar, byte counts and aggregate limits; migrate payloads through existing `NomadNetAttachments` bounded storage/checksums/extractors. Original files/manifests remain byte-identical inert archives. Primary `load()` can remove fields0x05/0x06/0x07 from the signed container without resigning or renaming its file: the import retains that original ID and explicitly labels the rewritten container unauthenticated. File/image/audio bytes remain available; stripped media has no reliable field typing, so recovered preview selection detects encoded image headers rather than asserting authenticated `FIELD_IMAGE` provenance. |
| Paper; `Conversation.paper_output`, container method/state5 | **implemented historical document**: retained text, fields/container and paper state are offline saved documents, not sends. The container does not preserve the original encrypted URI/QR output; external downloads/printer outputs are not searched or regenerated/resubmitted. Existing native paper composition is separate. |
| RRC; `RRCManager.identity`, `_store_path/load/save`, `RRCHub._append_history/_load_history` | **implemented bounded subset**: string-key CBOR hub profiles and concatenated CBOR room histories convert into the existing separate native hub store. Retain destination hash/aspect/display name, effective nickname, joined and parted rooms (including empty parted rooms), reconnect/list/who policies, and the primary load-visible history window. Original activated files are inert archives; unreferenced logs remain individually not imported. Imported source/nick/mention values are explicitly unauthenticated historical claims, never live roster/membership, unread/active state or authorization. Native startup does not contact imported hubs; connection is explicit. |

#### Bounded primary client-router migration

`NomadNetRouterProfile` is part of the existing offline `--import-profile`
transaction, not a second router or an online merge command. It reads the actual
primary layout, **not Sideband's `app_storage` layout**:
`NomadNetworkApp.__init__` supplies `<profile>/storage` to `LXMRouter`, whose
constructor appends `/lxmf`. `register_delivery_identity` enables only that
identity's signed delivery-ratchet file.

| Actual primary client contract | Migration boundary |
|---|---|
| `available_tickets`; `LXMRouter.__init__`, `remember_ticket`, `generate_ticket`, `save_available_tickets` | Three maps `outbound`, `inbound`, `last_deliveries`;16-byte destination/ticket values and finite numeric timestamps. Existing `LxmTicketStore` owns copies, expiry/grace, renewal and delivery interval. Native ticket cleanup removes expired interval markers/empty inbound maps rather than retaining them indefinitely. |
| `outbound_stamp_costs`; constructor, `update_stamp_cost`, save/clean APIs |16-byte destination to `[timestamp,cost]`; finite timestamp and null or integer1–254 cost. Existing LXMF45-day cache policy applies. No inferred cost is invented from conversation text. |
| `local_deliveries`, `locally_processed`; constructor, save/clean APIs |32-byte message/transient ID to finite timestamp; existing LXMF180-day retention applies on load. Raw files can still contain expired entries until a later native write; this does not reactivate expired entries. |
| `ratchets/<own-delivery-hash>.ratchets`; delivery registration and RNS `Destination` | Existing signature/ownership decoding and native512-key limit. Other destinations' ratchets, even malformed ones, remain not imported. No RNS global remote-ratchet cache is copied. |
| `node_stats`; `save_node_stats`, constructor/`enable_propagation` | Not a client cache. The separate bounded node importer validates/activates all four counters only when imported configuration enables propagation. Otherwise explicitly excluded. |
| `peers`, `messagestore`; propagation enable/exit | Implemented by the separate bounded node importer below, not inferred from client cache acceptance. Unresolved peers are preserved separately until matching identity discovery. |
| Pending inbound/outbound, deferred stamps, direct/backchannel links, path-request caches | Memory-only primary state, **not applicable as persistence files**. Import never resumes historical sends or manufactures sockets/jobs. |
| Unknown files, `.tmp.*`/interrupted writes, other identities' ratchets, RNS/interface files | Individually reported not imported, never silently copied or activated. |

Bounds are4MiB tickets,8MiB per cache,4,096 destinations per ticket map,
16 inbound tickets per destination,65,536 entries per cache and512 signed own
ratchets. The existing4MiB signed-ratchet envelope limit,512MiB/12,000-entry
whole-profile snapshot bounds and private staging ownership remain in force.
Unknown schemas, invalid key sizes/types, nonfinite timestamps, malformed stores,
excessive entries or identity-signature mismatch abort the **entire** absent-target
transaction. This strict failure is intentional: the primary can log/reset a
corrupt cache, but offline migration must not silently erase protection state.
The source is hashed before/after and remains unchanged. Cancellation or later
conversation/configuration failure removes staged private state without
publishing any target. The preflight instantiates the public `LxmRouter` stores
without starting transport/workers or registering delivery observers.

`GenerateNomadNetRouterProfile.py` executes the complete unmodified primary
constructor, seeds public cache attributes with explicitly public synthetic
values, then executes real router save APIs and `Destination.rotate_ratchets`.
It verifies actual imported source paths/clean pinned Git objects and normalised
checkout bytes, and records Git-blob SHA-256s:

| Source | SHA-256 |
|---|---|
| `nomadnet/NomadNetworkApp.py` | `c36ce4c537fe5af534910d1f5200d2a2ea5dd50f74bf9e4157222eff15614eb2` |
| `LXMF/LXMRouter.py` | `56b6a8e6b9f79256c4ae297340791eebcc1b6cda3cec04ab2dbe2f1efb53caa9` |
| `RNS/Destination.py` | `e1117e5ff9c4baeda53f9117ae52c4cd0e1e2a4f4167de628e4afb8b4506b8ae` |

There are no reference algorithm/class/clock patches. Fixed future timestamps
2,000,000,000/2,100,000,000 keep these **synthetic** fixtures from expiring; they
are not captured traffic. Random identity/private ratchets remain only in ignored
runtime profiles; the checked-in JSON contains no private keys or real tickets.
Independent Windows/Linux execution produces identical public fixture SHA-256
`81786ad45a8f58b357b402bb6e396628a5fac8472398665bf7364db3759d10d6`.
The intentionally older dependency pins still do not meet NomadNet's declared
minimums.

Claim **C72**, exact managed selectors under
`FullyQualifiedName=NomadNet.Tests.NomadNetRouterProfileTests.`:

| TestId | Method / Pester selector |
|---|---|
| T240 | `ExecutedPrimaryFixtureRecordsClientLayoutAndPersistence` |
| T241 | `ImportsPublicReferenceStoresOwnIdentityRatchetsAndRestartsWithoutSubscribers` |
| T242 | `MalformedClientFilesFailAtomicallyWithoutRepairOrSecretReplacement` |
| T243 | `ByteEntryTicketAndOwnRatchetBoundsRejectRatherThanTruncate` |
| T244 | `CancellationAndLateFailureLeaveNoPublishedRouterStateAndRetrySucceeds` |
| T245 | `MigratedDuplicatesSurviveRestartAndObserverFaultsDoNotDisableHealthyDelivery` |
| T246 | `OptionalClientFilesAndExcludedPropagationOrForeignFilesAreExplicit` |
| T247 | `NomadNet bounded primary client router migration.T247 independently regenerates public primary constructor and router persistence fixtures` |
| T248 | `NomadNet bounded primary client router migration.T248 atomically imports selected router stores and cross loads primary native primary across restart` |
| T249 | `NomadNet bounded primary client router migration.T249 exchanges actual primary app messages with migrated identity after clean and abrupt native restart` |

The three Pester cases in `tests/Pester/NomadNetRouterProfile.Tests.ps1` explicitly
skip unless `DOTRETICULUM_NOMADNET_ROUTER_PROFILE=1`, the selected native binary
and pinned Python/source variables are provided. T248 executes actual
primary→native→primary constructor cross-load of all five selected files,
including signature/ticket/stamp/duplicate observations after two native starts.
T249 uses the existing thin `nomadnet_live_node.py` adapter around the full
unchanged primary app. Two direct TCP loopback message exchanges retain the
imported delivery identity after clean and abrupt native restart; the actual
primary conversation containers and native durable history are checked.
The adapter only observes callbacks and invokes real APIs.

A first Windows restart run exposed a harness barrier issue: the primary's old
accepted TCP child could still report online immediately after the native process
was killed. The harness now allows two seconds for old-child retirement, then
still requires the next real announcement and both message directions.
Failed receipts and stdout/stderr are retained, not relabelled as passes.
An initial Linux cancellation assertion also exposed recursive-inotify admission
timing: a new nested router directory was not necessarily watched before its
first file was created. Cancellation now observes the already-watched parent
directory's staging admission; it still requires cancellation, absent target,
complete staging cleanup and a successful retry. The failed TRX is retained.

Selected candidate verification (implementation evidence, not parent acceptance):

```powershell
dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --artifacts-path artifacts\builds\nomad-router\managed-win --logger trx --results-directory artifacts\tests\nomad-router\managed-win -v quiet
dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64 --artifacts-path artifacts\builds\nomad-router\native-build-win -o artifacts\builds\nomad-router\native-win -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -v quiet
Invoke-Pester -Path tests\Pester\NomadNetRouterProfile.Tests.ps1 -Output Detailed
```

Windows full Nomad suite: **258 passed,9 explicit Linux-only skips**;
Linux native-filesystem full Nomad suite: **267 passed,0 skipped**.
All seven C72 managed cases pass on both hosts. Explicit converters are supplied
from the manifest, rather than silently skipping media cases. Strict NativeAOT
publish and `nomadnet --help` pass on Windows/Linux with SDK10.0.403 and
runtime/ILC10.0.14. Selected native Pester: **3 passed,0 skipped** on each host.
The Linux operator selects `DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet` and
its PATH **before** launching native PowerShell; build/test outputs and working
directory live under `/home/dennis/dotreticulum-ecosystem-acfd3d43`, not mounted
Windows filesystem permissions.

Frozen candidates and no-republish operators are under
`artifacts/tests/nomad-router/frozen-win-final` and the corresponding native
Linux workspace `artifacts/tests/nomad-router/frozen-linux-final`.
Each `manifest.json` retains binary/all-file checksums, compiled portable-PDB
source checks, extracted/physical generated-source checks, actual compiler/runtime
asset and ILC-response receipts, embedded fixture hash, converter/reference paths,
exact pass/skip counts, failed receipts, source snapshots and
`Invoke-FrozenRouterAcceptance.ps1`. Replay verifies those bytes before running
the frozen full managed assembly and three native cases, without build/publish.
Both candidates retain embedded base
`1.4.4.102+ed8f5deb56` / commit
`ed8f5deb56f8ef3a5dfebd333d943203218f6f3a` plus uncommitted changes, not a future
integration version. Windows binary SHA-256:
`5a97bb3eb90f0aae94638f137aa3b87c0a312a5dee2c81d86afcf673497226f3`;
Linux:
`a87563649cddbd459508606d1becee6c45ae9d6e04110a30bced5809e5acb05b`.

No transport hop, injected loss, propagation-node state, physical hardware,
other platform or exhaustive application/dependency compatibility is claimed.
Parent independently verifies all eight owned source hashes and replays both
exact frozen candidates successfully, including313 compiled production/test
source checks and75 generated-source checks per host. A separate current-source
rebuild passes258/9 explicit Linux-only skips on Windows and267/0 on Linux;
the current native harness passes3/0 per host. Operator
`artifacts\tests\nomad-router\Parent-CurrentAcceptance.ps1 -Manifest <manifest>
-Root <current-repository> -Build <fresh-host-build> -Output <fresh-host-results>`
uses the recorded converter/reference environment and retains separate TRX and
NUnit receipts. C72/T240–249 maps ten actual passing cases per host; receipts are
`artifacts\tests\nomad-router\parent-current-windows\c72.json` and
`/home/dennis/fleet-router-parent-results/c72.json`. Current-source acceptance
does not relabel the frozen native binaries as an integration-HEAD build.

#### Bounded propagation-node profile migration (C74)

The absent-target importer activates validated node state when the imported
configuration enables propagation. It uses shared LXMF persistence/inspection
APIs and a public node preflight inside private staging, not a second node-format
implementation. Strict Windows/Linux native candidates pass executed primary
fixture regeneration, node reverse cross-load and the complete mixed-node
sync/retrieval/restart flow (three Pester cases per host), plus the separate
primary cache/drop observation. Independent parent acceptance remains outstanding.

| Pinned source contract | Observed node persistence and required boundary |
|---|---|
| `LXMRouter.enable_propagation` (563–699), `save_node_stats` (1264–1280) | `storage/lxmf/node_stats` is a MessagePack map of exactly four nonnegative integer counters: `client_propagation_messages_received`, `client_propagation_messages_served`, `unpeered_propagation_incoming`, `unpeered_propagation_rx_bytes`. All are validated before activation; missing/malformed values are not silently reset. |
| `LXMPeer.to_bytes/from_bytes` (53–175), `LXMRouter.exit_handler` (1377–1433) | `storage/lxmf/peers` is an array of binary MessagePack peer maps, not identities. Destination, timestamps/alive state, policy/limits, strategy, metadata, counters/rates, optional peering proof and handled/unhandled transient IDs are validated. The optional `peering_key` is a proof token/value, **not an identity public key**. Stale/duplicate/overlapping indices abort import rather than being repaired. |
| `LXMRouter.enable_propagation` (609–645), `LXMPeer.__init__` (177–225) | The primary drops a saved peer when its public identity is absent from the separate global RNS known-destinations cache. **Intentional native safety difference:** preserve a distinct unresolved record through restart; activate the same record only after recalled identity material derives its exact propagation hash. Never create/reset that peer or mark it synchronized to conceal missing identity material. |
| `enable_propagation` (575–603), `lxmf_propagation` (2604–2641), `clean_message_store` (1154–1207) | Store names carry full transient-ID hex, receive timestamp and stamp value; contents are opaque propagated LXMF bytes plus a32-byte PN stamp. ID hashes the payload without that stamp. Actual upstream workblock/stamp vectors precede implementation. Offline import checks filename/content/stamp agreement, finite metadata,30-day expiry and imported storage/transfer/sync quotas. Historical valid stamp values are not retroactively charged at today's admission cost. |

`ObserveNomadNetNodeProfile.py` executes the actual unmodified complete primary
node constructor, real `peer`, `LXMPeer.to_bytes`, `exit_handler` and RNS
`save_known_destinations` APIs. It uses published RFC7748/RFC8032 **public keys
only**, synthetic counters23/17 and node counters7/8/9/10, with no reference
algorithm/class/clock patch. Separate process restarts retain one peer with the
original RNS cache, but retain zero peers with an isolated fresh RNS cache.
The latter process overwrites the saved peers array with an empty array on exit.
Node statistics remain intact in both cases. Random application private identity
files stay only in ignored runtime profiles; the checked-in fixture has no private
key, real traffic, ticket or peering secret.

Shared `LxmPropagationNode.UnresolvedPeers` remains separate from active `Peers`.
Unresolved records are not scheduled, rotated or unreachable-expired, and a sync
request fails without charging an attempt or resetting counters. New/removed
store IDs update both sets. `ResolvePersistedPeers()` and genuine signed
announcement/maintenance processing activate matching identities without
claiming a transfer. Activation completes the state batch before optional
observer callbacks; observer failures/reentry do not corrupt the collection.
No RNS cache is imported, reconstructed or modified by offline migration.

Bounds are4096 peers/8MiB outer peer file, the shared8MiB individual-peer and
65,536-ID disjoint index limits,64KiB metadata and4096-byte complete node
statistics. Existing20MiB/file,512MiB/12,000-entry whole-profile bounds also apply.
Expired messages, exceeded quotas, malformed/oversize records, stale IDs and
stamp/filename mismatches abort the whole import without eviction or partial
publication. Disabled node files and interrupted/unknown files are visibly
excluded. Node files are private on Unix, including atomic rewrites after
restart. Cancellation after staging creation and a later conversation failure
both remove node staging; retry uses the same source identity. The independently
selected RNS profile remains unchanged during import.

`GenerateNomadNetNodeProfile.py` executes the complete primary constructor, real
storage/peer/flush/exit APIs and real LXStamper generation/validation. Public
opaque512-byte and5120-byte payload fixtures are **not decryptable application
messages**. Only receive metadata/filename are normalized to a public future
timestamp for reproducible nonexpiring fixtures; no reference algorithm, class
or clock is patched. Actual generated proof bytes are reused and validated
during independent regeneration. Current diagnostic names transparently replace
the former native destructive-drop expectation; old failing/diagnostic receipts
are retained and not relabelled as migration acceptance.

Managed selectors are the three `NomadNetNodeProfileBoundaryTests`, six
`NomadNetNodeProfileTests` and four `LXMF.Tests.LxmUnresolvedPeerTests`.
Pester `NomadNetNodeProfileBoundary.Tests.ps1` retains the primary drop
observation (T263); `NomadNetNodeProfile.Tests.ps1` adds executed fixture
regeneration (T269), native/primary node reverse cross-load and atomic refusals
(T270), and actual mixed-node migration/sync/retrieval/restart (T271).
Both require explicit `DOTRETICULUM_NOMADNET_NODE_PROFILE=1` plus selected
binary/Python/source environment; unsupported modes explicitly skip.

The live fixture configures4,000,000-byte transfers,8,000,000-byte syncs,
16,000,000-byte storage, stamp cost13 and20 peers on both nodes, with normal
peering cost18. External native clients use application identities initialized
by complete primary client constructors and the existing profile importer.
After replacing the Python transport with the independent native transport,
those external clients use fresh RNS profiles and must observe genuine two-hop
signed discovery; their application identities remain unchanged. The actual
primary node peer retains its original RNS profile throughout recovery.
Existing external-client cached-route recovery across that transport-identity
handover timed out and is **not claimed**; its failed profiles/logs are retained.
No global cache is copied or repaired to make this fixture pass.

T271 transfers a real SHA-checked2.2MB attachment from a native origin through
the original Python node, migrates that node, and synchronizes its persisted
message to a second unmodified Python node after genuine peer discovery.
The same native receiver retrieves it from that second node over the native
transport. A second2.2MB attachment travels via the Python node back to the
migrated native node's local delivery destination. Actual primary reverse
cross-load checks the synchronized store and handled peer index. Both clean
and abrupt native restarts precede identity recovery; peer bytes remain unchanged
while unresolved.

The selected native binaries predate concurrent printing/utility changes.
Their complete C74 source snapshots use retained pre-printing non-owned Nomad
sources and the stable C74 overlay, checked against actual compiled portable-PDB
source SHA-256s. Managed suites are rebuilt against those exact snapshot sources,
not relabelled as validation of the changing whole-project source tree.
Frozen manifests and no-republish replay operators retain native, source,
generated-code, fixture, converter, reference and serviced toolchain provenance;
the separate current-source operator rebuilds the eventual integrated tree.

Selected-source full suites pass **267/9** (passed/explicit Linux-only skipped)
on Windows and **276/0** on Linux; the complete shared LXMF suite passes
**162/28** on each. All13 C74 managed cases also pass without skips when rebuilt
against the later current printing/utility tree. Strict native publish/help,
the separate primary observation and all three native Pester cases pass on
both hosts; the same full suites/help/four Pester cases pass again through
the frozen **no-build/no-republish** operator. Parent independently repeats
both immutable recipes: the same full managed suites, separate primary
observation and three native cases pass without rebuilding or republishing.
Their source/toolchain hashes and all eight owned production hashes match.
This accepts the scoped immutable candidates, not the current integrated tree.
The first current Windows full-suite attempt exposes a printing child-process
cleanup failure, retained separately. The fixed-stack ordinary Windows integrated
gate subsequently passes all projects/tooling/aggregation/coverage, and Linux
repeats full LXMF/Nomad, strict publish/help and the four C74 cases with native
SHA-256 `bdd71abb789779e9799987ca23bee83869c051468d1e70341c08a8cebaf8a043`.
These source snapshots precede later clipboard/Linux-printing work.

A separate parent Windows replay uses the accepted fixed-stack72a5 native
candidate and verifies all eight C74-owned production hashes unchanged. Its two
actual-reference cases pass, but T270/T271 fail creating `transport_identity`
when the nested regular path exceeds raw Win32 `MAX_PATH`. The retained
`artifacts\tests\nomad-utilities\parent-fixed-stack-node-windows` result is
**2 passed /2 failed**, not a migration pass. It shares the Sideband T321 atomic
publication cause; test paths/assertions are not shortened or relaxed.
The separate local stack fix `e52da394658f6f6c723214a9f811552418789cb6`
canonicalizes/prefixes Win32 paths while preserving create-only hard links.
Parent independently replays its exact NativeAOT candidate **5/0**, covering
local/extended/authorized loopback-UNC identity contention/restart and encrypted
compressed2.2MB Resource transfer. Fresh source-guarded Windows ecosystem
NativeAOT replay now passes all four node/reference cases with that selected
stack, including T270/T271 at longer nested paths without relaxed assertions.
The same native candidate separately passes current printing, clipboard and
utility process gates. Its SHA-256 is
`34d1ff11278cf7679e5c973b71697f90008333cbabafb11c09a0e07de03f4110`;
results are under
`artifacts\tests\nomad-utilities\parent-fixed-stack-node-windows-atomic-replay`.
Replay uses `Parent-AtomicAcceptance.ps1 -Mode Process`. The selected stack
object remains local/unpushed. Current Linux replay is blocked by WSL storage
failure, distinct from the earlier accepted C74 Linux snapshots.

Local immutable manifests are
`artifacts/tests/nomad-node-migration/frozen-win-final/manifest.json` and the
corresponding native Linux workspace
`artifacts/tests/nomad-node-migration/frozen-linux-final/manifest.json`.
Each verifies873 compiled portable-PDB source documents,179 generated-source
documents and all three embedded fixtures. Native embedded version is
`1.4.4.106+f8bac40f5b`, base
`f8bac40f5b032c5643cffdeff5b905c98182fe86`, plus the recorded uncommitted C74
sources. Snapshot managed metadata is `1.4.4+f8bac40f5b` (untracked snapshot
version files have no Git height), not a newly invented release.
SDK10.0.403/runtime and ILCompiler10.0.14 provenance, actual ILC response files
and converter/reference digests are retained. Windows native SHA-256:
`c599a5b62eef325d6f5226876b51ffa13a09ffe27cff3efd0abbd2a571c6af62`;
Linux:
`70c9960de3d30e0dade3fd9671bdca4a5cca488c9424a2316aea81f043db5fb9`.
`Replay-NodeCandidate.ps1` verifies frozen inputs before/after execution;
`Current-NodeCandidate.ps1` separately builds/tests/publishes the eventual
coherent parent tree. Exact T260–276 selectors, commands, explicit skips,
provenance and retained failures are in the bounded local `handoff.json`.

Upstream pins remain NomadNet
`f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`, LXMF
`e8ba02483217af43be284a72f1c059c730b7d047`, RNS
`a29bfb51c468945c96a1ee22cae0114e993e2391`. Source Git-blob SHA-256s are recorded
in the three node fixtures. The declared newer NomadNet dependency-minimum gap
remains: pinned RNS1.5.7/LXMF1.2.1 do not meet NomadNet's declared
RNS>=1.6.0/LXMF>=1.2.2 minima. No dependency pin changes, third-party cache import,
hardware, new impairment/latency/third-transport topology or full NomadNet parity
is implied. C75 native binaries predate the shared unresolved-peer support;
C76 candidates use their own earlier freeze, before the final C74 private-write
and reentrant-observer hardening. Independent parent C74 acceptance is separate.

#### Bounded primary RRC contracts

The independent `GenerateNomadNetRrcImportVectors.py` executes the complete primary
application, then actual `RRCManager.add_hub/save/load` and
`RRCHub.add_room/set_nick_override/set_auto_reconnect/set_auto_list/set_auto_who/
_record_message`. It observes retained PART state through save/load without
inventing a network acknowledgement. `NomadNetRrcImportVectors.json` records
commit `f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`, Git blob
`f5cde894532f05708f129cae980327e9a0d27504` and canonical blob SHA-256
`195e3dd62ab6678f5192b8b9c5a3c3e399e519018f3fb2216a16315c32895925`.
The imported module's exact path and LF-normalised bytes are verified against
that blob; Windows CRLF checkout bytes are not mislabelled as the LF blob hash.
Generated JSON is UTF-8/LF and independently regenerated byte-identically on
Windows and Linux, five successive complete primary startups per acceptance.

| Primary source contract | Native behavior / boundary |
|---|---|
| `RRCManager.save/load`, lines1534–1638: `{"hubs":[...]}`; `hash`, `dest_name`, `name`, `rooms`, `parted_rooms`, `auto_reconnect`, `auto_list`, `auto_who`, optional `nick` | Validate types; preserve names/aspects and policies. Missing booleans use actual constructor default false; absent/empty nick snapshots primary peer display-name fallback. No persisted negotiated limits, members/nicks, MOTD, active room or unread/mention-room sets exist upstream, so none are fabricated. Unknown hub keys are reported and archived. |
| `_history_dir/_history_path`, lines1513–1532 | Default `rrc.hub` uses lowercase destination hex; alternate aspects append `__` plus first8 SHA-256 hex of UTF-8 aspect. Room filename replaces each run outside `[a-z0-9._-]` with `_`, takes64 ASCII characters, then `_` plus first8 room SHA-256 hex and `.log`. Resolve only joined/retained-parted room paths; reject collisions, traversal/links and unsupported names. |
| `_entry_for/_append_history/_msg_from_entry/_load_history`, lines708–796 | Concatenated CBOR maps use `k,s,n,t,ts,m`. Actual ordinary kind is `msg` (mapped to native `message`); actions/notices/system/errors retain their kinds. Preserve existing millisecond timestamps, optional16-byte source and nickname, mention claim and body. Missing/invalid fields fail rather than adopting Python's permissive defaults or inventing a current timestamp. |
| `NomadNetworkApp.apply_config`, `[rrc] history_per_room_cap`, `filter_loaded_history`; `_per_room_cap/_filter_history/_load_history` | Apply actual defaults500/true and explicitly supplied bounded values to migration: filter notice/system records, then keep the last configured count per room (0 means no per-room cap). Persist the cap per imported hub. These config keys are still reported as unsupported global runtime settings; this is migration-specific handling, not complete ConfigObj compatibility. |
| Corrupt/incomplete CBOR log tail | Deliberate strict-import difference: reject and atomically roll back the entire absent target, preserving all source bytes. Upstream loader keeps its decoded prefix; the importer never silently promotes an incomplete file to a successful migration. Fix/recover the stopped source separately before retrying. |

Additional bounds:128 hubs,4MiB per RRC source file,256 joined+parted rooms per
hub,64 UTF-8 bytes per room,32 UTF-8 bytes per effective local nickname,
256-byte ASCII dotted destination aspect,1,000 retained records and2MiB retained
body bytes per hub,256KiB per historical body. Histories that exceed aggregate
native limits fail instead of evicting records beyond primary's configured
window. The non-persistable `*` room, unusual aspect/name forms, duplicate
destination profiles and incompatible schemas fail explicitly. This bounded
profile work found no RRC persistence placeholders to implement; remaining
Channels presentation and configuration surfaces remain gaps.

Use `rrc hubs`, `rrc open <hash>`, `rrc status` and `rrc history [room]` to inspect
migrated hubs and parted-room history without networking. Imported rows display
`(imported; unauthenticated)`; this flag survives subsequent live writes and
clean/abrupt restart. `rrc connect <hash>` retains the saved nickname and joined
rooms unless explicitly overridden, validates the saved aspect against the
announced identity and rejoins only joined rooms. Imported peer claims never
populate authenticated live membership. Existing schema1 native seven-field
history records remain readable; extended records add only local provenance.

#### RRC migration acceptance and reproduction

Current-source `dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release`
with isolated `--artifacts-path` and the selected actual image converter passes
**208/215,7 Linux-only skips on Windows;215/215 on Linux**. Tests include executed
primary profiles, malformed schemas/types/limits, corrupt/truncated tails,
absent-target rollback, immutable source, retention/filtering, provenance across
native-store rewrites, no-subscriber processing and failed-observer isolation.
The existing profile suite covers cancellation, concurrent admission,
symlink/ancestor rejection and profile ownership.

Both platforms strictly publish `src\NomadNet\NomadNet.csproj -c Release -r
<win-x64|linux-x64> -p:ILLinkTreatWarningsAsErrors=true
-p:IlcTreatWarningsAsErrors=true` with SDK10.0.403/runtime and compiler10.0.14.
The selected Pester tags `Native,RrcImport,ProfileImport,RrcPresentation,Rrc`
pass **7 cases/1 explicit Linux-only skip on Windows;8/8 on Linux**.
`RrcImport` itself runs five successive complete primary startups on each host,
byte-identical canonical vectors, immutable offline import and offline terminal
inspection, then an actual primary application's saved connected hub/nick/room/
history migration. The published client retains the original identity and
messages both ways with another unchanged complete primary application through
one unchanged supplemental rrcd hub (transport enabled), before/after clean and
abrupt native restart. Both applications are TCP leaves of that hub; this is not
a separate transport-hop/loss/topology claim. Existing Linux RRC/form acceptance
separately remains green. No reference protocol handler is replaced.

Frozen manifests and non-republishing recipes are
`artifacts\tests\nomad-rrc-migration\frozen-win-final-v5\manifest.json` and
`/home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/tests/nomad-rrc-migration/
frozen-linux-final-v5/manifest.json`, each with `Invoke-FrozenRrcAcceptance.ps1`.
They verify frozen file/native hashes, portable-PDB source checksums and compiler
package versions; they operate on fresh private runtime profiles and never
rebuild or overwrite the v4 receipts. Windows invocation:

```powershell
pwsh -NoProfile -File artifacts\tests\nomad-rrc-migration\frozen-win-final-v5\Invoke-FrozenRrcAcceptance.ps1 `
    -Manifest artifacts\tests\nomad-rrc-migration\frozen-win-final-v5\manifest.json
```

Linux uses serviced `DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet`, that
directory first on `PATH`, and `/home/dennis/.dotreticulum-powershell/pwsh
-NoProfile -File <frozen-linux-final-v5>/Invoke-FrozenRrcAcceptance.ps1 -Manifest
<frozen-linux-final-v5>/manifest.json`. Linux binaries are built from the current
`/mnt/c/Git/dennispayne/DotReticulum.Ecosystem` source, not the old native-filesystem
checkout; all Linux build outputs/state are native-filesystem paths.

Failure logs are retained under the same dedicated evidence roots: the first
live import exposed actual primary kind `msg`, which is now covered by canonical
vectors; the first Linux vector comparison exposed raw CRLF-vs-LF source hashes,
fixed by comparing imported canonical bytes to the Git blob. Explicit LXMF source
selection initially failed import-path/hash/index checks rather than silently
accepting another module; the isolated Windows pinned checkout now has canonical
LF bytes and a clean Git index. A Linux regression initially used mounted
executable-page state; selecting the existing native-filesystem work option fixes
that gate without weakening its form assertions. Final current-source logs are
`managed-win-final.log`, `acceptance-win-final-v6.log`,
`managed-linux-final.log`, `acceptance-linux-final-v4.log`.
The original direct Pester6 frozen runner stalled on the existing Windows
Linux-only skip; its log is retained. The recipe uses the repository's
`tests\run-pester.ps1` environment-handling wrapper in a bounded child process
and requires the exact passed/failed/skipped summary, with unchanged test
assertions and skip condition. The Linux v2 launcher initially selected the
dotnet-tool host instead of pwsh; the recipe now explicitly records/selects the
operator PowerShell executable. Preserved native-host diagnostics then identified
the child inheriting the process's mounted working directory despite the parent's
PowerShell `Set-Location`: mounted page permissions caused “Executable page
disabled”, not a wire failure. The child now explicitly sets
`ProcessStartInfo.WorkingDirectory` to the native-filesystem scope. The native
harness also asserts host-announce reception before browsing and preserves
stdout/stderr and failed runtime roots. All feature assertions remain intact.
Frozen replay logs are `frozen-acceptance-win-final-v5.log` and
`frozen-acceptance-linux-final-v5.log`.

These are implementation receipts awaiting independent parent acceptance.
The newer primary NomadNet dependency-minimum gap, full ConfigObj/runtime
configuration and remaining terminal/Channels UI remain explicitly outside this
bounded RRC slice; node migration has its separate C74 contract above.
No release, push or tag was performed.

#### Import integrity, ownership and usage

Admission uses an exclusive, persistent sibling
`<target>.profile-import.lock`; the target remains absent until a sibling staging
directory has been completely validated. Staging also has the normal native
profile lease. On Windows its DACL grants only the current user inherited full
control; Unix staging is0700 and private identity/ratchet files are0600.
Temporary private byte/key buffers are zeroed. Windows releases the staging
lease immediately before rename because an open exclusive file handle prevents
directory rename there; the sibling admission lock remains held and atomic
non-overwriting `Directory.Move` still refuses a competing native owner.
Failed/cancelled imports remove only their own staging root. The empty admission
lock intentionally remains to avoid split-inode ownership races.

Bounds are12,000 filesystem entries,512MiB total profile bytes,20MiB per source
file,2,000 messages/conversations,5MiB native state,64KiB settings and existing
attachment per-message/profile quotas. Source trees and all ancestors must be
regular directories/files: links/reparse points, pipes and devices fail. Source
bytes are hashed before/after; a changed file set or content rejects the import.
This is an offline consistency check, not an OS-enforced lock on an uncooperative
primary process. Arbitrary concurrent adversarial source-tree replacement is not
a supported input model.

`profile-import.json` records counts, all warnings, exact archived relative paths
and every non-imported source file; the private identity/validated ratchet
destinations are the explicit active-state cases above. Inert exact source
archives use `import-archive/<relative-source-path>.bin`; none is registered as a
page, policy executable, authorization list or network configuration. Imported
message fields unsupported by the native presentation remain in these original
containers rather than being silently lost.

After import, run `nomadnet --config <native-profile>`, then `contacts`,
`conversations`, `messages [peer]`, `client status`, `propagation` and
`attachment <message-id> <zero-based-index> <output-name>`. Attachment export
uses the existing checksum-verifying reader and downloads policy, refuses
overwrite and cannot resend a historical message. Imported historical
authentication is shown explicitly: `verified-local-signature` only for an
unchanged locally signed container verified using the imported identity;
`remote-public-key-unavailable` for intact remote history without the remote key;
`invalid-local-signature` when the available local key rejects the signature;
`container-hash-mismatch-not-authenticated` for changed hashes. A cached
primary `True` claim never upgrades any of these results.

Observable acceptance is: independently execute the unchanged full primary
constructor/file APIs; import with the strict NativeAOT binary into an absent
profile; inspect contacts/settings/history/file/image/paper and exact source
preservation; refuse overwrite; run terminal, daemon ownership conflict and
same-identity process restarts; exchange messages in both directions with the
unchanged full primary `NomadNetworkApp` after restart of the imported native
profile. The earlier pinned Python LXMF endpoint exchange before restart is
separate, narrower evidence. Targeted integration tests additionally cover
malformed containers/manifests, bounds, unsafe links/special files, corrupt-cache
and peer-backup recovery, empty conversations, pre-/in-flight cancellation,
concurrent admission and rollback. Parent acceptance must independently replay
the frozen candidate; a generator/unit/native loopback result alone is not that
acceptance.

#### Profile-import implementation validation and frozen replay

The bounded importer adds15 integration cases. The final complete Nomad suite
passes **180/11 skipped on Windows** and **187/4 skipped on Linux** (191 total).
The one new platform skip is the Linux symlink/FIFO-specific case; the native
acceptance separately rejects Windows junctions and Linux symlinks.
Windows and Linux strict NativeAOT publish both pass with SDK10.0.403,
runtime/ILCompiler10.0.14 and
`-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror`.
The `ProfileImport` Pester case passes1/1 on both hosts: independently regenerated
full-primary-constructor fixture, native `--help`, linked-source rejection,
absent-target import, exact history/settings/contact inspection, real imported
PNG ASCII preview and byte-identical file/PNG export using the explicitly
selected existing FFmpeg, source preservation, non-overwrite, actual pinned
Python LXMF endpoint both directions before restart, terminal/daemon ownership,
identity-preserving clean/abrupt process restart, and fresh messages in both
directions with the unchanged full primary application after that restart.
The existing `nomadnet_live_node.py` adapter executes its complete unchanged
constructor and observes the actual delivery callback after it persists the
message; acceptance checks that original conversation container on disk,
the primary outbound delivery proof, the new native persisted message, and
the unchanged imported identity file. Both restarted native processes use
the imported target, never a separate fresh native profile. No physical
image/device or new loss/multi-hop
claim is made. The profile origin is local persistence from the complete actual
primary constructor, not a network copy: four historical messages including one
paper document, a25-byte file and a generated4×4 PNG. Fresh network acceptance is
two25-byte text bodies with the LXMF-only endpoint before restart, followed by
two29-byte text bodies with the actual primary application after restart.
Both flows use LXMF DIRECT on isolated IPv4 loopback: native
`TCPClientInterface` directly to Python `TCPServerInterface`, both leaves
with shared instances/transport disabled. The endpoint process is stopped
before the full application starts; their identities/profiles are distinct.
The application adapter checks the clean primary source revision and actual
module paths. The primary declares RNS>=1.6/LXMF>=1.2.2, while these selected
application flows still use pinned RNS1.5.7/LXMF1.2.1: passing them is not full
minimum-dependency parity. There is no transport hop or
multi-megabyte Resource transfer in this import slice; earlier distinct topology
evidence is not relabelled. Each acceptance JSON records actual attachment sizes,
binary/converter/fixture hashes and this origin/topology explicitly.

The independently executed Windows/Linux fixture bytes have SHA-256
`8655211814e942aaa391976b10392d03ebdd9b105f78d6b3618541e7662f6a3d`.
No random identity, key or identity-derived hash enters that public fixture.
The native candidate hashes are unchanged from v2: this follow-up changes
only acceptance and documentation, reusing the strict-published binaries and
their original checked compilation PDBs. The hashes are:

| Candidate | SHA-256 |
|---|---|
| Windows `artifacts\builds\nomad-profile-import-win-native-final\nomadnet.exe` | `c399c83b417a9f49224ebd5988d7d0dbf9e59b984e5ab42bc7c8057e77477c62` |
| Linux `/home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/builds/nomad-profile-import-native/nomadnet` | `329e10652cbd61314211c7c4d3bbebbd1e2fafe963735a8788ece53924b860ea` |

Both full-solution commands below passed; the Linux retry followed a concurrent
Sideband compile edit, not a Nomad failure. Validation uses native-filesystem
Linux outputs:

```powershell
dotnet test DotReticulum.Ecosystem.slnx -c Release --artifacts-path artifacts\builds\nomad-profile-import-solution-win -v quiet --logger trx --results-directory artifacts\tests\nomad-profile-import\solution-win-final191
dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64 --artifacts-path artifacts\builds\nomad-profile-import-win-native-final-build -o artifacts\builds\nomad-profile-import-win-native-final -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror -v quiet
# From the serviced Linux environment described in CONTRIBUTING:
dotnet test /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/DotReticulum.Ecosystem.slnx -c Release --artifacts-path /home/dennis/dotreticulum-ecosystem-acfd3d43/nomad-profile-import-solution-linux -v quiet --logger trx --results-directory /home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/tests/nomad-profile-import/solution-linux-final191-retry
dotnet publish /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/src/NomadNet/NomadNet.csproj -c Release -r linux-x64 --artifacts-path /home/dennis/dotreticulum-ecosystem-acfd3d43/nomad-profile-import-native-build -o /home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/builds/nomad-profile-import-native -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror -v quiet
```

Ignored frozen candidates retain exact harness/source copies, fixture and binary
hashes, original compilation portable PDBs and checked Nomad source-document
checksums. Guarded **acceptance-only** recipes reject changed frozen
source/binary/converter bytes; they do not rebuild, edit current sources, push or
release. They create uniquely named new runtime/reference profiles under the
owned ignored evidence roots:

```powershell
pwsh -NoProfile -File artifacts\tests\nomad-profile-import\frozen-win-191-v4\Invoke-FrozenAcceptance.ps1 -Manifest artifacts\tests\nomad-profile-import\frozen-win-191-v4\manifest.json
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoProfile -File /home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/tests/nomad-profile-import/frozen-linux-191-v4/Invoke-FrozenAcceptance.ps1 -Manifest /home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/tests/nomad-profile-import/frozen-linux-191-v4/manifest.json
```

The v2 frozen receipts remain intact and prove only the narrower pre-restart
LXMF endpoint exchange; v3 adds the actual application exchange after restart.
The v3 receipts and the operator failure profile
`artifacts\tests\nomad-profile-import\acceptance-5063a8036c4041219064d89ed217dcfb`
remain intact. Its primary application stdout records an RNS startup warning
and `@@{"event":"ready",...}` on the same line. Removing only a leading
`@@` incorrectly passed the timestamp prefix to `ConvertFrom-Json`.
The v4 profile-only reader locates the explicit frame marker after diagnostic
text, strictly decodes the complete remaining JSON object and validates its
string event name. Ordinary diagnostic lines remain in stdout evidence.
Malformed framed JSON/schema and peer error/failure events fail with process,
expected event, raw line and evidence-directory context; they are not skipped.
One overall bounded event deadline is retained across diagnostic lines, with
at most32 pending valid asynchronous events per process rather than discarding
out-of-order delivery events. Regressions cover the observed warning prefix,
unframed diagnostic text, malformed JSON, trailing garbage, array/non-event
schemas, and an actual missing-event timeout followed by continued use of the
same reference process. The frozen v4 recipe passes five successive fresh
actual-primary/imported-native starts per host (Windows and Linux), including
the post-restart bidirectional application flow on every run. Production,
native binaries and the unchanged primary adapter are not modified.
The frozen identity/conversation recipes have their independently recorded
acceptance; additional propagation-node migration has its separate C74 contract
and current-integration gate above. All75 primary configuration forms and
bounded ConfigObj grammar, original editor/guide and functional Micron now have
their separate acceptance below. Complete migration and unexercised terminal
contracts remain outside these receipts; selected-RNS extended grammar is the
concrete stack gap described in the configuration section.

### Primary saved-directory and ignored-list import

```powershell
nomadnet --import-directory <upstream-profile-directory> --config <native-directory>
# Or, while the native terminal owns the profile:
directory-import <upstream-profile-directory>
contacts
nodes
```

This explicitly reads **only** `<source>\storage\directory` and optional
`<source>\ignored`, without modifying either. It does not import identity,
conversations, ConfigObj settings, peer settings/backups, RRC history, transient
announces, pages, files or RNS state. A separate `--import-config` remains available
for supported INI settings. Run that first if both imports target a new profile.
The directory command can add policy to a new or existing native profile; it is
not a full-profile migration or a way to retain the upstream delivery address.

The factual persistence contract is `Directory.py:91–166,412–442` at primary
`f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`: a MessagePack dictionary containing
`entry_list` and `announce_stream`. Saved entries contain source hash, name,
trust, hosted-node flag, preferred delivery, identify flag, rank and notes.
Legacy entries with three through seven fields receive the actual primary
defaults; a null name becomes `"Undefined"`, null preferred delivery becomes
direct, absent flags become false, absent rank stays null, and null/absent notes
become empty. Duplicate source hashes use the last complete entry, as the primary
loader does. `NomadNetworkApp.py:332–352,583–589` reads/writes the independent
newline-delimited ignored hashes; native import deduplicates valid hashes and
reports malformed lines without echoing their contents. An ignored hash need not
be a saved contact. Imported suppression is applied to the existing LXMF router
immediately in terminal mode and at subsequent starts.
Directory trust classification does not rewrite the separate native
`trustedNodes` automatic-propagation allowlist; use `trust-node` explicitly for
that policy. This is directory import, not propagation-selection migration.

Imports are **additive**: an identical saved contact is idempotent, a different
saved contact at the same hash is an explicit conflict and aborts the entire
import, and existing ignored destinations are never removed. Notes, trust,
identification consent, node flags, delivery preferences and rank are compared,
not silently overwritten. No native messages, alerts, bookmarks, settings or
identity are replaced. Serialization checks the complete proposed state before
atomic replacement, refusing to evict history to make an import fit. Cancellation,
malformed source, conflicting policy, oversized state and failed replacement
leave existing in-memory and on-disk policy unchanged.

Native safety differences are explicit: maximum directory5MiB, ignored512KiB,
10,000 entries/ignored lines, bounded MessagePack depth/work, existing80-character
printable names and4096-character notes, known trust/delivery values and typed
boolean/rank fields. Unsupported/future entry fields, unknown root keys, unsafe
names or lossy normalization fail rather than silently discard policy.
Transient announce entries are counted and reported as not imported by this saved-directory command; they are
not treated as fresh network observations. This does not migrate global RNS
blackholes. The separate `announces import` command above migrates bounded transient
history without changing this command's contact-policy boundary. Complete directory presentation and the other migrations listed
above remain required software, not silently deferred as optional.

Native terminal/daemon/init/offline directory-import processes now hold an
exclusive `.nomadnet.lock` file handle for the profile. A second owner fails
before opening state; terminal import uses its existing owner. The empty lock
file remains after shutdown to avoid unlink/inode races. This protects cooperating
native CLI processes, not manual external edits or multiple independent store
objects used by library callers. SIGTERM is gracefully joined on Linux; the
Windows acceptance case terminates only its owned daemon PID and verifies restart
and lease release, not delivery of Windows console signals.

`tests\Interop\GenerateNomadNetDirectoryImportVectors.py` was executed before
implementation. Its three current/legacy/duplicate fixtures execute actual
`DirectoryEntry`, `Directory` construction/load/save and
`NomadNetworkApp._persist_ignored_list`; only application filesystem paths are
supplied by a minimal object, with no replacement parser. The fixture records
both working-file and Git-blob SHA-256:

| Primary source | Git-blob SHA-256 |
|---|---|
| `nomadnet/Directory.py` | `29467f537a64feda32a1c6fe075b3836bccbeeecee1a84628075e0e8fa3c723a` |
| `nomadnet/NomadNetworkApp.py` | `c36ce4c537fe5af534910d1f5200d2a2ea5dd50f74bf9e4157222eff15614eb2` |

The `DirectoryImport` Pester case independently regenerates public fixtures and
executes all three **complete unchanged primary application constructors**, then
observes their loaded directory/ignored policies through the thin adapter.
The same case runs published native offline import and idempotence, terminal
policy/node listing, active-profile rejection, conflict and missing-source
recovery, daemon restart and persisted identity/policy on Windows and Linux.
These are local persistence/CLI contracts with no network interfaces; they are
not a new Reticulum topology, live message exchange, primary Urwid UI or complete
migration claim. The older primary minimum-dependency discrepancy above still
applies. Malformed/boundary/concurrent/cancelled/disk-failure cases are additional
managed tests, not primary-live observations.

#### Directory-import acceptance and frozen reproduction

The selected Windows/Linux artifacts use **SDK10.0.403 and
Microsoft.DotNet.ILCompiler10.0.14**. The Linux binary was rebuilt into a fresh
serviced output after the parent upgraded the SDK; the earlier10.0.13 compiler
artifact is not selected by these recipes. No audit, trimming or AOT warnings
were suppressed; both final strict publishes succeeded without warnings.

| Acceptance | Windows | Linux |
|---|---:|---:|
| New reference/parser/transaction/failure managed cases |7 passed|7 passed|
| Full solution, default opt-ins disabled |2341 passed /117 skipped|2346 passed /112 skipped|
| Existing `Native` published executable workflows |4/4|4/4|
| `DirectoryImport`, independently executed primary plus native workflows |1/1|1/1|
| Portable PDB checksums against exact changed C# source |5/5|5/5|

The unchanged Native cases exercise actual published hosting, pages/resources,
attachments, paper and restart, not just `--help`. Existing ConversationImages,
Images and Propagation Pester cases remain unchanged; their earlier evidence is
not relabelled as a new run by this migration slice. Full-suite skip boundaries
remain explicit; passing software persistence does not establish hardware or UI
parity. No new DotReticulum API gap was encountered.

Final build/test commands (Windows uses the existing MSVC environment;
Linux sets both `DOTNET_ROOT` and the leading SDK `PATH`, runs from its native
filesystem and addresses the current source under `/mnt/c/Git/dennispayne/DotReticulum.Ecosystem`):

```powershell
dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64 --artifacts-path artifacts\builds\nomad-directory-import-native-win -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -v:q
dotnet test DotReticulum.Ecosystem.slnx -c Release --artifacts-path artifacts\builds\nomad-directory-import-final-win --logger trx --results-directory artifacts\tests\nomad-directory-import-final-win -v:q
# Linux shell, with the documented SDK environment:
dotnet publish /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/src/NomadNet/NomadNet.csproj -c Release -r linux-x64 --artifacts-path /home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/builds/nomad-directory-import-native-linux-serviced -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -v:q
dotnet test /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/DotReticulum.Ecosystem.slnx -c Release --artifacts-path /home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/builds/nomad-directory-import-final-linux-serviced --logger trx --results-directory /home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/tests/nomad-directory-import-final-linux-serviced -v:q
```

All commands above passed; published selected binaries also ran `--help`,
`Invoke-Pester -Tag Native` and `Invoke-Pester -Tag DirectoryImport` with the
results above. The latter runs actual primary constructors through
`nomadnet_live_node.py` and reference generation through the committed thin
generator, not a local replacement for the reference.

Selected executable SHA-256:

- Windows
  `artifacts\builds\nomad-directory-import-native-win\publish\NomadNet\release_win-x64\nomadnet.exe`:
  `55A9C5303728020E5476F89C929C338A183096940A68A05E78802CFD1F866FDB`.
- Linux
  `/home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/builds/nomad-directory-import-native-linux-serviced/publish/NomadNet/release_linux-x64/nomadnet`:
  `A2C5F6186426EBF07666C72236FC1DA316E0CD2FF228E87BCC7D11322119A71D`.

Ignored `artifacts\tests\nomadnet-directory-import-frozen\manifest.json`
records the exact eleven owned files, literal source snapshots, all selected
native/test dependencies, compiler package versions and Windows/Linux PDB source
checksums. Operational SHA-guarded recipes run those exact artifacts without
rebuilding, verify both current source and frozen snapshots, regenerate actual
primary data, and retain fresh receipts:

```powershell
pwsh -NoProfile -File artifacts\tests\nomadnet-directory-import-win-acceptance.ps1
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-directory-import-linux-acceptance.ps1
```

These are environment-specific replay recipes; committed tests define the
portable contract. Source snapshots and binary hashes, not a test-count-only
report, identify the accepted candidate. The separate offline-profile importer
above adds identity/peer-settings/conversation and bounded RRC hub/history
migration; remaining configuration and terminal workflows remain required work.

### Node limits and peer policy

The node-configuration fixture is generated by executing seven real, unchanged
primary constructors, not by calling a replacement configuration parser.
`GenerateNomadNetNodeConfigVectors.py` records the primary commit, Git-blob and
working-copy SHA-256, RNS/LXMF pins, complete constructor input and observations.
The working copy can have CRLF while the Git blob has LF; neither digest is
presented as the other.

`NomadNetworkApp.py:363–405,1128–1234` establishes decimal KB/MB units, transfer
minimum1KB, explicitly configured sync minimum equal to transfer, storage
minimum0.005MB, propagation cost minimum13, negative peer caps clamped0,
and absent peer cap `None`. The actual LXMF constructor maps absent peer cap
to20 and clamps effective sync even when the primary app's absent-sync value
remains10,240KB. Both configured and effective values are retained in the fixture.
Valid static duplicates survive the primary's decoding; priority registration
deduplicates. Malformed hashes are skipped by the source application; the
explicit native importer likewise skips them with bounded contextual warnings.
Native JSON settings instead reject malformed hashes rather than silently
discarding persisted policy.

The importer supports scalar, quoted and comma-separated destination lists,
including trailing commas and ASCII whitespace between complete bytes inside
a quoted hex hash. Whitespace between nibbles is invalid, matching executed
`bytes.fromhex` observations, and is skipped with a warning.
The importer retains a64KiB aggregate/8Ki-character line bound, allowing a full
128-entry quoted hash list. This is not general ConfigObj grammar. Unbounded source sizes/costs/lists are
restricted to the documented native safety caps; fractional units are converted
to whole bytes, so sub-byte announce precision is not claimed. The settings
file is bounded at64KiB on both save and load, accommodating all three bounded
policy lists plus escaped path strings; an over-limit write cannot replace a
valid profile. Settings snapshots and completed updates do not expose mutable
destination lists.

`node-config` displays saved effective startup settings separately from actual
running announce limits/storage/cost. `transfer`/`sync` take decimal KB,
`storage` decimal MB, `cost` an integer, `peers` an integer or `default`,
and `static`/`priority` a hash plus `on|off`. Successful edits are persisted
before publication and explicitly require restart; there is no misleading
partial hot reload or in-process eviction while saving policy. `node-peers`
reports actual lower-layer counters without printing peering keys.
Removing a static designation is not an implicit unpeer/blacklist operation;
as in the primary configuration workflow, persisted peers can remain discovered
peers. The `node-unpeer` terminal action removes an active or retained unresolved
peer only when its peering timebase is not newer than the local clock; it does not
blacklist the peer or edit static-peer configuration, and removal persists across
restart (T1052). The full peer-management UI remains missing.
The primary's manual peer-sync action is in
`ui/textui/Network.py:1887–1901`; the native command calls the existing
`SynchronizePeerAsync` for a known peer and joins cancellation, without
reimplementing offers, peering work, resources or storage.

Upstream settings audited in `NomadNetworkApp.apply_config`/default config:

- `[logging]`: `loglevel`, `destination`, `logfile` — **implemented bounded subset**: imported/persisted level filtering, file/stdout selection, literal paths, default file and exact daemon-gated `--console` precedence; fail-closed write failures and original console-output separation are documented above;
  native errors currently go to stderr.
- `[client]`: `enable_client`, `downloads_path`, `attachment_save_path`,
  `announce_at_start`, `announce_interval`, `try_propagation_on_send_fail`,
  `periodic_lxmf_sync`, `lxmf_sync_interval`, `lxmf_sync_limit`,
  `required_stamp_cost`, `accept_invalid_stamps`, `max_accepted_size`,
  `compact_announce_stream`, `notify_on_new_message`, `compose_in_markdown`,
  `user_interface` — **partial** only where the explicit native settings or
  implemented workflows above apply. Downloads path, required cost, invalid-stamp
  acceptance, transfer limit and client startup/periodic announcements are operational/imported;
  `enable_client` is retained unused metadata as in the actual primary; `compose_in_markdown`,
  `notify_on_new_message` and `user_interface=none|text` are operational/imported.
  `attachment_save_path` is operational/imported with explicit terminal export
  as documented above; `compact_announce_stream` is operational/imported with
  bounded receive-time history and original terminal presentation as documented above.
  Menu UI is an **upstream-placeholder**, not an invented third native UI.
- `[node]`: `enable_node`, `node_name`, `disable_propagation`, `announce_at_start`,
  `announce_interval`, `pages_path`, `files_path`, `page_refresh_interval`,
  `file_refresh_interval`, `propagation_cost`, `max_transfer_size`, `max_sync_size`,
  `prioritise_destinations`, `static_peers`, `max_peers`, `message_storage_limit`
  — **partial**: named node keys are now imported, validated and mapped onto
  existing LXMF hosting options. Terminal `node-config` edits persist atomically;
  hosting changes require restart rather than claiming unsupported hot reload.
  `node-peers` inspects actual peers and `node-peer-sync <hash>` requests transfer
  for an existing peer using LXMF, with a joined three-minute cancellation deadline.
  Configured static peering now has separate actual application acceptance on
  Windows/Linux, described below; generic underlying LXMF tests are not substituted.
- `[textui]`: `intro_time`, `intro_text`, `theme`, `colormode`, `glyphs`, `image_loading`,
  `mouse_enabled`, `editor`, `hide_guide`, `sanitize_names`, `clipboard_copy`, `animation_interval`
  — **implemented bounded functional controls / original presentation**: all12 keys have runtime effects after restart, including the nine terminal controls detailed under C95 below. `image_loading` is operational for original inline ASCII, not Kitty. `clipboard_copy` activates explicitly opted-in OSC52 content/URL copying and `editor` selects the installed external editor. Intro, original colors/icons, bounded menu/mouse interaction, guide visibility, cosmetic name sanitation and explicit active network refresh are not pixel-identical Urwid. Terminal-control injection remains refused even when cosmetic sanitation is disabled.
- `[rrc]`: `history_per_room_cap`, `filter_loaded_history`,
  `max_accepted_resource_size`, `ephemeral_notices`, `justify_msgs`, `space_msgs`,
  `nick_colors`, `mention_color`, `color_mention_timestamps`, `nick_colors_theme`,
  `render_markdown`, `render_micron`, `show_gutters`, `enable_esoterics`
  — **implemented bounded functional controls / upstream placeholder**:
  the two render switches and ten global history/style controls execute in the
  original terminal/RRC service as inventoried below; `enable_esoterics` has no
  primary runtime consumer. `rrc history-limit` is a separate stricter native
  per-hub limit. Aggregate limits and original layout/palette differences remain
  explicit; this is not complete Urwid presentation parity.
- `[utilities] enable_utilities` — **implemented bounded visibility control**:
  exposes/hides the original Utilities menu entry after restart. Explicit utility
  commands remain accessible; visibility neither starts a listener nor grants authorization.
- `[printing] print_messages`, `print_command`, `print_from`,
  `message_template` — **not implemented (deliberately omitted)**: the section
  is ignored with a warning on import (C78).

The remaining required configuration behavior is not silently deferred:
primary config-directory discovery
(`/etc/nomadnetwork`, `~/.config/nomadnetwork`, `~/.nomadnetwork`) versus the native
ApplicationData default; ConfigObj edge cases beyond the bounded grammar below and
full primary UI (functional section/reset workflows and selected-RNS forms now exist
with explicit runtime/placeholder/safety-refused dispositions); safe external-root activation during full profile import;
and remaining router/cache/peer-counter/announce-state migration. The existing
named `[node]` application keys are imported and wired, but complete peer UI,
storage-pressure order and exhaustive failure/restart acceptance remain separate
gaps. Primary `ui/textui/Config.py` also edits the separately selected Reticulum
configuration (`enable_transport`, shared-instance/name/type/ports, interface
discovery, remote-management allowlist, probes, implicit proofs and panic policy);
the native app has explicit text and functional section forms for its selected RNS file,
and both probe-response/implicit-proof fields now execute through the selected
public stack owner after restart. `panic_on_interface_error` remains an
intentional fixed failure-ownership safety refusal, not a missing platform
feature or an ineffective saved toggle. Extended RNS ConfigObj grammar remains
limited by the public parser. Migration deliberately does not
silently replace network configuration. Unknown keys continue to be reported.

Safety differences are intentional and not upstream defaults: content/queue
bounds, no symlink/hidden/alternate-stream traversal, executable-page opt-in,
short execution deadlines, separate attachment bytes rather than embedding them
in history (256 MiB/4096-file attachment quota; exhaustion preserves text and
reports rejected attachments), and bounded cache eviction (128 pages/32 MiB). The native cache
binary layout, RRC profile layout and JSON/attachment layout are .NET-local persistence formats,
not new wire protocols. Media conversion and authorised executable-page support
are separate capabilities; one does not prove the other.

### Client announcement policy

`client status` shows startup policy, interval, last successful announce,
unused `enable_client` metadata and current stamp cost. `client startup on|off`
saves the next-start policy; `client interval <30–10080>` changes subsequent
periodic checks. `client stamp-cost <none|1–254>` saves and applies the existing
LXMF inbound-cost setter; the next announce advertises it. Invalid changes fail
without altering the saved profile. `client announce` announces only delivery;
`announce` additionally announces the hosted node as before.

Actual pinned source sets a three-second startup delay,90-second jobs deferral
and five-second poll. Its strict `now > last_announce + interval` excludes the
exact boundary. The configured interval is clamped to at least30minutes on
import; the native maximum10080minutes is an explicit resource/policy bound,
not an upstream maximum. Timestamps advance only after the network announce
succeeds, unlike the hosted node's pre-attempt timestamp.

Two observed primary failures are deliberately not reproduced: a fresh profile
with startup disabled has `last_announce=None`, causing `__jobs` to raise
`TypeError`; a failed announce raises out of jobs with its old timestamp. Native
null timestamps remain quiet until a successful manual/startup announce, and
background failures are reported without disabling later work. Requests are
serialized and bounded to16pending operations; asynchronous disposal cancels
and joins all admitted work. Failing log observers do not stop processing.
`enable_client` has no runtime reader in the pinned application, so false does
not silently disable native delivery or its separately configured announces.

### Selected client-announcement acceptance

`GenerateNomadNetClientAnnounceVectors.py` executes the actual primary
`__jobs`/`announce_now` bodies at
`f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`. The fixture records the loaded
Windows source-file SHA-256
`beda2e618d67200cb14cf7d636efa34f13b4607f99717575f02a1c91fafcf60b`.
Its disclosed module-local clock stops after one iteration; router/save
observers record calls without network I/O. This is reference-vector evidence,
not unmodified live application evidence. Actual constructor vectors separately
verify imported client booleans, defaults, interval clamping and a registered
delivery destination despite `enable_client=false`. Registration alone does
not prove that the separately documented invalid 255 stamp-cost profile works.

Native deterministic tests cover strict elapsed boundaries, explicit start,
three-second startup and independent 90-second maintenance deadlines,
five-second polling, disabled startup/null timestamp safety, network failure
without timestamp advancement, continued service, failing log observers,
16-request admission, cancellation, and shared disposal joining admitted work.
All scoped tests pass with the selected converter:
**Windows 110 passed +4 explicit POSIX skips**, **Linux 114/114**.

The exact selected strict NativeAOT binaries are:

- Windows `artifacts\builds\nomadnet-client-announce-win\publish\NomadNet\release_win-x64\nomadnet.exe`;
  SHA-256 `0d533f089da7376117ae98d66186ea586e46540443004d2343cc99af587f4154`.
- Linux `artifacts/builds/nomadnet-client-announce-linux/publish/NomadNet/release_linux-x64/nomadnet`;
  SHA-256 `98f7a35605fd8a9adaa3bb4d481034279df8b3c2a66a5e68dca7a680ff1cf4ed`.

Both `dotnet publish src\NomadNet\NomadNet.csproj -c Release -r <rid>
--artifacts-path artifacts\builds\nomadnet-client-announce-<host>
-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -v:q`
invocations pass. Windows uses the existing MSVC environment in the same
process; Linux uses the pinned WSL SDK/runtime and native-filesystem test roots.
The supported platforms and exact reference/converter/runtime prerequisites
remain those recorded in the other selected acceptance sections; no macOS,
hardware or audio-device support is inferred.

Frozen recipes, independently repeated against the unchanged binaries:

```powershell
& artifacts\tests\nomadnet-client-announce-win-acceptance.ps1
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-client-announce-linux-acceptance.ps1
```

Both recipes pass **Native 4/4**, including `--help`, identity-preserving terminal
restart and representative hosted/message/attachment/paper flows. The expanded
actual primary application case passes **1/1** on Windows and Linux
(independent repeat 112.34/107.61 seconds); Linux additionally passes actual
RRC/forms/partial/replay **1/1**, 18.90 seconds. The frozen Pester copy
`artifacts\tests\nomadnet-client-announce-frozen.Tests.ps1` has SHA-256
`3b79126512ad7e190b6f68d57b54f1c194297f4d8dd9dfb2f814eb15bbc94bed`;
the recipes reject different test/binary hashes.

`DOTRETICULUM_NOMADNET_CLIENT_ANNOUNCE_LIVE=1` requires the existing explicit
configuration/hosted-policy/image modes. The actual primary starts its additional
supported delivery-announce observer before native startup; original application
handlers remain registered, and path responses are excluded. With disabled
client/node startup, no unsolicited delivery announce is observed after seven
seconds. A disclosed old persisted timestamp fixture makes periodic work due;
the genuine first broadcast still waits the real 90-second startup deferral,
with no clock acceleration or private scheduler invocation. The primary's LXMF
decoder observes stamp cost 13, then 14 after a valid manual change and announce.
Invalid 29-minute and 255-cost changes are rejected without changing saved policy.
The successful timestamp, disabled startup and cost 14 survive native restart.

The manual witness waits 1.1 seconds between announcements because RNS requires
a strictly newer whole-second emission timestamp for an equal-hop route.
There is no extra delivery retry, path expiration, deadline increase or relaxed
assertion. This new announce witness is direct application interoperability;
existing transport-hop, impaired 2,200,000-byte transfer and propagation/restart evidence
remains separately scoped, not inferred from announcement counts.

The isolated `dotnet build DotReticulum.Ecosystem.slnx -c Release
--artifacts-path artifacts\builds\nomadnet-client-announce-integrated -v:q`
passes with zero warnings/errors. `dotnet test DotReticulum.Ecosystem.slnx
-c Release --no-build --artifacts-path artifacts\builds\nomadnet-client-announce-integrated
--logger trx --results-directory artifacts\tests\nomadnet-client-announce-integrated -v:q`
passes **2079 tests, 87 explicit skips, zero failures**. Converter-dependent cases
skipped in this integrated invocation are separately exercised above.
Runtime peer display-name edits now use the existing LXMF setter as described
below; other configuration/UI contracts, migration,
complete image presentation and application-level propagation peering are not declared complete.

### Message presentation, runtime profile and paper QR

`client name <name>` saves and calls the existing `LxmRouter.SetDisplayName`,
without an implicit announce, timestamp update or identity/destination replacement.
An explicit later `client announce` advertises the new name. Native names retain
the bounded frontend policy:1–80 UTF-16 code units, no controls and valid strict
UTF-8. Primary `set_display_name` accepts empty/null/control values; native rejects
them. Native persistence completes before applying a live change, unlike the
primary's save-after-memory-update order. Failed/cancelled writes leave the running
name and in-memory settings unchanged.

`client markdown on|off` controls ordinary outbound LXMF renderer2 metadata.
Stored inbound/outbound content remains original; the optional renderer survives
history restart. Markdown conversation display reuses the stack's existing
`MarkdownMicron` at primary width80, followed by the observed primary modifier and
Micron presentation rules. The existing browser still defaults to width40 for
dividers. Executed source preserves escaped `¦` Micron markers in this conversation
pipeline and strips some inner underscores; these are recorded source outputs,
not treated as literal-rendering bugs. Unknown renderer codes remain literal in
the native frontend; broader unknown-renderer/UI parity is not claimed.

`rrc render markdown|micron on|off` changes independently persisted live/history
body presentation. Both switches off is **not** wholly literal upstream: remaining
raw Micron can still be interpreted. `rrc send-json <room> <JSON-string>` preserves
multiline/raw content while using the existing session's negotiated limits.
Notices/system messages remain literal. Colours, gutters, visual justification,
and styling across split mention spans remain required work.

`GenerateNomadNetPresentationVectors.py` executes the actual primary Markdown
pipeline and `Channels._message_widget` for14conversation bodies and56RRC cases,
covering all four rendering policies. The disclosed host provides settings,
glyphs and an **unstarted actual Urwid palette screen**; formatting methods are
not replaced. These are source-widget observations, not a Python interactive-UI
or pixel-identical claim. Source hashes and actual `set_display_name` save/no-announce
observations are recorded in the fixture.

`paper` corresponds to URI-return without saving history. `paper-qr` and
`paper-save` save to the configured downloads root as `LXM_<hash>.png`/`.txt`
(URI has a trailing newline), then persist outbound paper history using the
message's actual timestamp. Neither adds the compose renderer field.
`qr <URI> <name.png>` exports an existing encrypted envelope; `qr-show <URI>`
uses explicit black foreground/white background and half-blocks, preserving
every shared-codec module and quiet zone. It does not print the URI itself.
Image-envelope/QR validity is not signature authentication or decryption.
Printer/image-import/camera workflows are not implemented or physically verified.

`GenerateNomadNetQrVectors.py` executes actual primary
`Conversation.paper_output(save_qr)` with real RNS identities, LXMF cryptography
and `as_qr`. OUT destinations avoid unrelated service registration; only the
conversation-ingest callback observes calls instead of persisting conversations.
Actual zxing-cpp2.3.0 independently decodes the source PNG; qrcode is never used
as a decoder. The generated PNG/URI, source SHA, versions, originator observation
and one appended conversation message are recorded; private identity material
is not recorded. Creation method5 (`PAPER`) differs intentionally from URI
ingress callback method3 (`PROPAGATED`): pinned `LXMRouter.py2659–2670` invokes
`lxmf_propagation(is_paper_message=True)`, whose local delivery at2624 explicitly
uses `PROPAGATED`. This is not a native wire-method mismatch.

The application consumes the shared managed ZXing.Net0.16.11 QR implementation;
its Apache2 notice is copied transitively under `ThirdPartyNotices` in native
publish output. No second QR implementation, drawing runtime, font or native
codec is bundled by NomadNet.

### Selected presentation/profile/QR acceptance

The source-pinned generators above, genuine primary constructor configuration
observations and scoped tests pass: **Windows118passed +4explicit POSIX skips**,
**Linux122/122**. Tests cover exact14/56source body observations, runtime-name
validation/cancelled or failed persistence, original-content/renderer restart,
every displayed QR half-block/module (including the odd last row), cancellation
and foreign-envelope rejection. They are not live/device evidence.

Both strict `dotnet publish src\NomadNet\NomadNet.csproj -c Release -r <rid>
--artifacts-path artifacts\builds\nomadnet-presentation-<host>
-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true
-warnaserror -v:q` commands pass with zero warnings/errors. Windows uses the
same-process MSVC setup; Linux uses the existing WSL SDK and native-filesystem
test roots. Exact selected native SHA-256:

- Windows `artifacts\builds\nomadnet-presentation-win\publish\NomadNet\release_win-x64\nomadnet.exe`:
  `c3081138e960f6bf6d54ca405ce18fb30d1f4c8aa8715cee200faecda9b7a072`.
- Linux `artifacts/builds/nomadnet-presentation-linux/publish/NomadNet/release_linux-x64/nomadnet`:
  `3eeea2a826bd80697178fe0ff96fa2f3cf986516f41d35871838f13cc33eb1ee`.

The SHA-guarded recipes select these exact binaries, frozen managed tests and
Pester suite `aa37fd39e6f3aa7903ea60cd09cf796c1f7b22d750dc792a60985c8ca32d8cb5`:

```powershell
& artifacts\tests\nomadnet-presentation-win-acceptance.ps1
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-presentation-linux-acceptance.ps1
```

Both pass **Native4/4**, actual primary application **1/1**
(Windows118.58seconds, Linux108.50seconds), and separate actual RRC presentation
**1/1** (Windows8.55seconds, Linux3.22seconds). Linux additionally passes unchanged
RRC/forms/partials/replay **1/1**,18.42seconds. The recipes assert the transitive
Apache2 notice SHA-256
`cfae7bd1153b81c2fe81517e5122a0a67e9c2478c16cd7b189793a6072fcf6d4`.

`DOTRETICULUM_NOMADNET_PRESENTATION_LIVE=1` requires the existing client-announce
and conversation modes. The actual primary sends renderer2 and native displays
the exact source-derived body while preserving raw content/renderer. Native
compose on/off sends2/null to the primary. With compose **on**, native saves a
paper PNG; actual zxing-cpp decodes it and the original primary router ingests
the exact plaintext with renderer absent and the source-correct ingress method.
Existing-URI export decodes to the identical URI; terminal display, filename
escape rejection, URI newline, paper history and restart are exercised.
The native binary and harness explicitly use UTF-8 pipes; default Windows
OEM encoding had incorrectly encoded `¦` as byte0xdd in the initial witness.

Native runtime `Native café 雨` persists without changing announce count or
timestamp. An explicit subsequent announce, separated by the existing required
1.1-second RNS emission interval, advertises that name with the same destination
and cost14. Invalid81-unit/control inputs fail without mutation. Name, disabled
startup, timestamp, cost, renderer preferences and message metadata survive restart.

`DOTRETICULUM_NOMADNET_RRC_PRESENTATION_LIVE=1` selects an actual primary client,
unmodified supplemental rrcd0.3.2 at
`f6d7e9d72bf83c70d7a9373ae8c5edaa052a7bf2`, and native client. Clients connect
through separate TCP interfaces to the Python hub/transport process. Sixteen
primary-to-native cases cover four policies and emphasis/raw/escaped Micron/
multiline bodies; sixteen native JSON compositions reach the primary unchanged.
Native identity/history/preferences and continued messaging survive its restart.
This is not a separate external transport-hop, physical terminal, or complete
Channels visual-layout claim. Windows supplemental dependencies cbor2
6.2.0.post2/tomlkit0.15.2 match the existing Linux reference environment;
the primary/RNS/LXMF source pins are unchanged.

The same final Linux binary additionally passes the unchanged mixed propagation
gate **1/1**,251.43seconds:

```powershell
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-presentation-propagation-acceptance.ps1
```

This regresses the default renderer-enabled sender through the existing mixed
native/Python transport topology, both propagation directions, persisted-node
restarts/deletion, scheduled/cancelled retrieval,2,200,000-byte Resource transfer
with a deliberately dropped part, continued service and direct-failure fallback.
No route expiration, deadline increase or weakened assertion is added.

Parent independent acceptance repeats the Windows and Linux SHA-guarded
presentation recipes successfully against the unchanged binaries, managed tests
and assertions. The same parent independently repeats the frozen Linux mixed
propagation recipe **1/1**,240.82seconds. These are independent repetitions of
the selected software/topology evidence, not additional platform/hardware claims.

The isolated full-solution `dotnet build DotReticulum.Ecosystem.slnx -c Release
--artifacts-path artifacts\builds\nomadnet-presentation-integrated -v:q` passes
with zero warnings/errors. `dotnet test DotReticulum.Ecosystem.slnx -c Release
--no-build --artifacts-path artifacts\builds\nomadnet-presentation-integrated
--results-directory artifacts\tests\nomadnet-presentation-integrated
--logger 'trx;LogFilePrefix=integrated' -v:q` passes **2117tests,89explicit skips,
zero failures**. Separately selected converter tests above cover the integrated
run's converter skips. These receipts precede subsequent inline-media,
profile-migration, functional Micron and application peer-control slices;
their current separate dispositions are above. They do not prove later
behavior or close remaining configuration/terminal contracts. Printing is not
implemented (C78).

### Node hosting configuration acceptance

The post-hex-whitespace configuration slice uses seven executed primary
constructor observations, including a red/green regression for malformed
whitespace between hex nibbles. It does not derive reference values from C#.
All existing NomadNet tests are included in these scoped Release results:

| Post-fix acceptance | Windows x64 | Linux x64 |
|---|---|---|
| Managed, with the selected real FFmpeg available |147 passed /5 explicit Linux-only skips |152 passed /0 skipped |
| Strict NativeAOT publish, trim/ILC warnings as errors |passed, zero warnings/errors |passed, zero warnings/errors |
| Native executable/core workflows |4/4 |4/4 |
| Actual configured application peer/restart/impairment flow |1/1,91.74seconds |1/1,86.01seconds |
| Existing mixed propagation/retrieval/restart/loss regression |not rerun in this slice |1/1,245.77seconds |

Parent independent execution of both SHA-guarded post-fix recipes reproduces
the managed and Native4/4 results above. Configured peering passes
91.55seconds on Windows and88.76seconds on Linux; the separate Linux mixed
propagation regression passes240.16seconds. These are fresh process runs
against the frozen binaries/adapters, not the owner's earlier results or
the superseded pre-hex-fix acceptance.

Exact accepted post-fix executables:

- Windows: `artifacts\builds\nomadnet-node-config-corrected-win\publish\NomadNet\release_win-x64\nomadnet.exe`,
  SHA-256 `f9d8834c660b19a069810a8054496179f3cf7a72f887aec4ada7a246e024dd89`.
- Linux: `artifacts\builds\nomadnet-node-config-corrected-linux\publish\NomadNet\release_linux-x64\nomadnet`,
  SHA-256 `556f92625781fea49c43570bf1f009d19b156b90def78a5ffc5d3319036c7928`.

Publishing uses `dotnet publish src\NomadNet\NomadNet.csproj -c Release -r <rid>`
with a host-specific `--artifacts-path`,
`-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror`;
Windows uses the MSVC environment and publish in the same process.
Scoped tests use
`dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --filter "Category!=Live"`
with the corresponding isolated artifacts/results paths and real converter.
Portable PDB checksums match all five modified production source files in both
publish builds. The13-path manifest and literal source snapshots are retained
under `artifacts\tests\nomadnet-node-config-acceptance.json` and
`artifacts\tests\nomadnet-node-config-post-hex-snapshot`.

Guarded local reproduction recipes are
`artifacts\tests\nomadnet-node-config-corrected-win-acceptance.ps1` and
`artifacts\tests\nomadnet-node-config-corrected-linux-acceptance.ps1`.
They verify exact executables, managed test DLLs, the frozen Pester suite,
both thin adapters, the impairment helper and required third-party notice;
TRX output directories are unique per invocation. The committed live case is
the `NodeConfiguration` Pester tag, enabled only by
`DOTRETICULUM_NOMADNET_NODE_CONFIG_LIVE=1` and explicit actual-reference/native
paths. It never silently substitutes a PATH-selected executable.

The topology has separate native PN, actual primary Python NomadNet PN and
native client processes connected through an unmodified pinned Python RNS
transport. Actual peer paths are asserted at two hops. The native PN leg has
5ms per forwarded frame and one armed, actually dropped Resource part.
Both peer directions carry a2,200,000-byte attachment, verified by SHA-256 in
the receiving native application's attachment storage; actual Python and
native peer incoming/outgoing counters also advance. All three applications
restart with the same identities, configured policies, stored message and
native handled-peer state before the reverse flow. Static peers operate with
dynamic `max_peers=0`. Initial imported1MB/2MB/8MB limits remain running while
terminal edits save4MB/8MB/16MB limits; only restart applies the new values.
Cost13 and both priority destination configurations are explicit non-default
test settings, not assumed defaults.

Readiness requires actual cached peer costs, two-hop client routing and both
gateway interfaces online/active within30seconds. One normal public
`LXMRouter.request_path` call begins each phase, as used by the source
`LXMPeer.sync`; there is no forced expiry, peer-state/backoff mutation or
deadline extension. The Python reverse peer direction occurs after identity
caches are persisted/reloaded: fresh unknown-static-peer outgoing resolution,
storage-pressure eviction order, additional rejection/failure topologies and
every daemon/control workflow are not established by this case.

Earlier failed observations remain failures: Windows attempted sending before
the20-second propagation announce/cached cost (13.27seconds); Linux waited for
an unsolicited changed value after one premature stored-count snapshot
(150.43seconds); passive post-restart discovery had no Python PN peer path even
though both gateway routes were healthy (73.31seconds). The current gate
observes storage repeatedly and uses normal explicit path discovery without
weakening assertions. Pre-hex passing native/parent results and their manifest
remain separate, not relabelled as acceptance of the corrected importer.

## Operation

```powershell
nomadnet --init --config <app-directory>
# Edit settings.json to enable the required node/propagation settings.
nomadnet --config <app-directory> --rnsconfig <reticulum-directory>
nomadnet --daemon --config <app-directory> --rnsconfig <reticulum-directory>
nomadnet --import-config <upstream-config-file> --config <new-native-directory>
```

`help` lists the actual commands. Hosted content goes in
`storage\pages` and `storage\files`. `<path>.allowed` contains identity hashes
(not LXMF destination hashes), one 32-hex identity per line. A protected request
without an authenticated permitted identity is denied. App identity is stored
through Reticulum under the selected RNS configuration; preserve **both** app
data and RNS configuration for restart. Keep private identity material private.

### Core upstream configuration import

`--import-config` reads a bounded 64-KiB strict UTF-8 ConfigObj-style file and
atomically creates a **new, unoccupied** native data directory. Existing files or
profiles are rejected, not merged or overwritten. Cancellation/validation failure
removes the staged profile; source configuration is never changed. This imports
configuration only: no upstream identity, conversations, contacts or private
peer-settings files are silently selected/copied.

The translated keys are `[client] enable_client`, `user_interface`,
`notify_on_new_message`, `announce_at_start`,
`announce_interval`, `periodic_lxmf_sync`,
`lxmf_sync_interval`, `lxmf_sync_limit`, `try_propagation_on_send_fail`,
`required_stamp_cost`, `accept_invalid_stamps`, `max_accepted_size`,
`downloads_path`, `compose_in_markdown`; `[node] enable_node`, `disable_propagation`, `node_name`,
`announce_interval`, `pages_path`, `files_path`, `announce_at_start`,
`page_refresh_interval`, `file_refresh_interval`, `max_transfer_size`, `max_sync_size`,
`message_storage_limit`, `propagation_cost`, `max_peers`, `static_peers`,
`prioritise_destinations`; `[rrc] render_markdown`, `render_micron`;
`[textui] image_loading`. Every other supplied key is
reported on stderr as unsupported, without printing its value. This does not
reclassify unimplemented options as optional.

Executed primary rules include minutes-to-seconds sync timing, nonpositive
sync limit as unlimited, nonpositive max accepted size as 500 decimal KB,
`None`/nonpositive stamp cost as absent, cost clamping, minimum one-minute node
announce interval, current-user `~`/`~/` expansion for download paths and case-insensitive
ConfigObj boolean values. Imported node interval defaults to primary720 minutes;
legacy native profiles retain native360 unless set. Native record/size/path
bounds still apply and fail explicitly rather than weakening them.

Imported page/file refresh intervals clamp negative values to0 and interpret
positive integer values as minutes. Zero disables that automatic rescan, not
serving existing paths or an explicit `node refresh`. Native JSON additionally
permits fractional minutes to retain the earlier five-second default. The
five-second maintenance loop tests independent deadlines: refreshing pages
does not inadvertently register files. Failed scans remain due and do not
prevent the other scan or announcing from continuing. `node paths` lists the
registered handler snapshot; at most10,000 handlers may be registered, and an
oversized scan rejects without replacing the existing registry.

`nodeAnnounceAtStart=false` suppresses the six-second hosted destination
announcement and its additional propagation announcement, not the separate LXMF
client's announce, LXMF's own propagation-enable announcement, or answers to
Reticulum path requests. `node announce` immediately announces the hosted node,
resets its periodic deadline, and calls the existing propagation node's
`ScheduleAnnounce()` when enabled. This preserves pinned LXMF's separate
twenty-second deadline for **each** propagation-announcement request, including
the independent request made when propagation is enabled. It does not substitute
the native immediate-announcement extension. The host timestamp is updated before
attempting transport, as in the primary. Periodic announcing remains active even
when the startup broadcast is disabled.

Scalar quotes, `#` comments outside quotes and flat sections are supported.
Duplicate/malformed sections/keys, ambiguous scalar lists, nested ConfigObj
sections, multiline quoted values and lines over4096 characters fail explicitly.
Named-user home expansion also fails explicitly; supply an explicit path instead.
This intentionally bounded parser does not claim complete ConfigObj grammar.
Executable pages remain explicit native opt-in; importing configuration does
not enable script execution.

**Upstream boundary defect:** primary `applyConfig` clamps required stamp costs
above255 to255. Actual pinned LXMF1.2.1 `register_delivery_identity` returns a
destination for255 but its setter rejects it, leaving `stamp_cost` undefined;
`get_announce_app_data` then raises `AttributeError`. Source:
`LXMRouter.py`358–404,1045–1055, revision
`e8ba02483217af43be284a72f1c059c730b7d047`. The executed boundary observation is
retained at ignored `artifacts\tests\lxmf-stamp-cost-boundary.json`. Config
vectors preserve the primary's observed255; native import explicitly rejects
it with the 1–254 explanation. It is not silently converted to254, presented
as valid delivery, or treated as a missing native protocol capability.

Attachments are stored under `attachments\<message-hash>\<index>.bin`, with
`.name` and `.sha256` metadata. Remote names are never filesystem paths. Image
and audio commands send existing encoded bytes; no audio hardware/playback,
capture or transcoding is claimed. `paper` needs the recipient's announced identity.

### Local files and attachment composition

```text
files browse <directory>
files open <number>
files up
files selected
files done
send-files <delivery-hash> <message>
```

`files` alone opens the current user's home directory. Explicit current-user
`~`/`~/` paths use the same expansion as the core importer; named-user shortcuts
are rejected, not silently mapped to somebody else's profile. Rows show the
parent first, then directories and regular files independently sorted in Python
Unicode-codepoint order. Opening a directory retains selection; opening a file
toggles it. `files list`/`refresh` redisplay or rescan. Paths with spaces are
accepted as the remainder of `files browse`; numbering avoids quoting filenames.

`files done` commits an immutable, session-local selection. `files cancel`
restores the previously committed selection, matching the primary conversation
picker's cancel handler. `files clear` clears a committed selection. Complete
or cancel an open picker before `send-files`: sending reads the actual current
file bytes and passes the normal LXMF file-attachment field to the existing
delivery supervisor, preserving contact delivery preference and fallback.
Successful queuing clears the composer selection, as primary
`ConversationWidget.send_message`/`clear_editor` do; preparation failure leaves
it available for correction. Selection paths are not persisted across restart.
Existing `attach`, `image` and `audio` use the same safe local reader.

The primary conversation picker allows **100 MiB per file**
(`Conversations.py: ConversationWidget.MAX_ATTACHMENT_SIZE`); the native
application intentionally retains its **16 MiB per-file/aggregate** and
**64-attachment** limits. At most4096 directory entries are inspected, including
hidden entries. Dot/control-character names, symlink entries and special files
are omitted. Unreadable/changed files fail the whole preparation instead of
silently sending fewer attachments; primary logs and skips individual read
failures. These are explicit safety differences, not upstream limits.

The regular-file adapters support Windows, Linux and macOS ARM64. Linux uses
source-generated libc `open` with nonblocking/no-follow/close-on-exec flags,
then `statx(AT_EMPTY_PATH)` on that exact handle; FIFO/device/link rejection
precedes content reads. Windows uses system `CreateFileW`/handle-type and
attribute inspection, rejecting device namespaces and reparse points. macOS
ARM64 uses nonblocking/no-follow/close-on-exec `open`, checks the opened
descriptor with `fstat`, and uses `lstat` for listings. Reads
enforce length/cancellation and reject growth beyond the inspected length.
These are OS APIs, not bundled codecs or new third-party binaries. The Linux
adapter requires libc `statx` and a supporting kernel; the exercised host has
Ubuntu glibc2.43. Windows10.0.26300 x64 is exercised. Hosted macOS ARM64
[Integration run 45103329910](https://github.com/dennispayne/DotReticulum.Ecosystem/actions/runs/45103329910)
passed all five picker cases, including FIFO, device and symlink rejection;
the overall macOS Integration job failed on other tests. Other libc versions,
distributions and architectures, including macOS x64, are not inferred support.

### Images and external codecs

`media <number> <name.webp>` downloads an image descriptor into `downloads`.
`show-media <number> [color]` downloads/decodes that descriptor for bounded ASCII
or explicit ANSI true-colour preview; this is not Kitty graphics or a
pixel-identical primary widget. `image-convert <file> <name.webp>` explicitly
converts a local encoded image. Browser inline ASCII loading is described below;
original conversation previews are described next. Upstream Kitty rendering
remains missing/unverified.

#### Conversation image presentation

Primary `Conversation.py:732–741,805–866,890–902` normalizes raw bytes or
`[format, bytes]` image fields and persists encoded attachments.
`ui/textui/Conversations.py:2967–2985,3044–3096` presents clickable attachment
labels, not inline conversation image widgets. Its browser's image-loading
policy is not a conversation-image implementation. Seven independently
executed observations in `NomadNetConversationImageVectors.json`, generated by
`GenerateNomadNetConversationImageVectors.py`, record actual primary
normalization/extraction/path methods and both source SHA-256 values.
No upstream source/assets or image widget implementation is copied.

Native conversation ASCII is an original extension. `messages [hash]` displays
history and selects the current image view; `conversation active <hash>` also
selects the active peer and displays its history. Newly received images in the
active conversation join that view. New outgoing image attachments are stored
under their actual64-character LXMF message hash, not a second media format.
An optional history `imageAttachmentIndex` identifies only the actual image
field, so a normal file named `image.png` is not automatically decoded.
Legacy history without that metadata is readable but not guessed into images.

The existing persisted `images never|manual|auto|always` policy covers both
browser and conversation presentation. Never prohibits even explicit loads
and clears/cancels the current preview work. Manual waits for
`conversation images load`; `reload` explicitly retries failed previews and
decodes again using the verified conversion cache. Auto treats already-stored
conversation bytes as local using the existing image policy; it does not
invent message-link RTT or fetch remote media. Always loads displayed local
images regardless of browser link eligibility. This distinction from browser
network heuristics is intentional, not an observed primary conversation rule.
`conversation images status` reports loading/successful preview count;
`conversation images cancel` joins the loader without changing saved policy.
Changing active view, disabling images, deleting a conversation or application
disposal cancels and joins superseded work.

At most the latest32 distinct image messages are retained per view. A single
decoder processes them serially; failures remain failed until explicit reload
and do not prevent other images, terminal commands or messaging. Observer/log
exceptions retain message context and use a Trace fallback. Core work does
not require a subscriber. Saved attachment reads reject linked directories or
files, invalid indices/hashes, oversized data/checksum metadata and checksum
mismatches, with constant-time digest comparison.

Encoded input is limited to16MiB; existing shared conversion cache limits are
256files/192MiB, quality85 and maximum dimension1200. Cached WebP must pass the
existing sixteen-megapixel header bound. FFmpeg explicitly converts only one
frame, restricts input protocols to `pipe` (only `file` for staged TIFF),
has a64MiB individual allocation ceiling, bounded output/error streams
and an eight-second process deadline with cancellation/kill/join. These are
resource bounds, not a process-wide memory sandbox or codec-exploit safety
guarantee. Use never/manual to avoid automatic external decoding of received
bytes. Plain ASCII output is bounded and excludes terminal control sequences;
no physical audio, Kitty-pixel or full upstream UI claim is made.

#### Conversation image verification

Final protocol-restricted native acceptance is separate from the earlier
pre-protocol executables; the earlier passing recipes/binaries are retained
without relabelling them as evidence of the final safety boundary.

| Final selected acceptance | Windows x64 | Linux x64 |
|---|---|---|
| Scoped managed, selected real FFmpeg |163 passed /6 explicit Linux-only skips |169 passed /0 skipped |
| Strict NativeAOT publish |passed, zero warnings/errors |passed, zero warnings/errors |
| Native executable/core flows |4/4 |4/4 |
| Actual primary image messages and native conversation presentation |1/1,9.53seconds |1/1,8.53seconds |
| Existing primary `ImageData`/host/browser conversion-cache flow |1/1,13.24seconds |1/1,10.33seconds |
| Separate mixed propagation/restart/dropped-Resource regression |not rerun in this slice |1/1,240.99seconds |

Selected executables:

- `artifacts\builds\nomadnet-conversation-images-safe-win\publish\NomadNet\release_win-x64\nomadnet.exe`,
  SHA-256 `094d9fdd0edfb7ad9e01cfb4a5cfab541fa75e35bf9aaa7193905eb9ed1df741`.
- `artifacts\builds\nomadnet-conversation-images-safe-linux\publish\NomadNet\release_linux-x64\nomadnet`,
  SHA-256 `8d57e19fb51be56d3af403cabdaeb83c2b6061572422fed387e91d51dbff387b`.

The direct isolated loopback TCP application case exchanges actual primary
formatted PNG/raw WebP messages toward native and an unchanged PNG image field
toward primary; it checks actual primary attachment persistence, native typed
metadata/bytes, all four policies, eight native32-column/eight-row ASCII
frames, both application identities after restart, persisted manual policy,
joined explicit cancellation and successful actual messaging while the local
converter is missing. Synthetic accepted PNG/WebP payloads are99/58bytes;
this is not large-image, multi-hop/loss conversation-image or physical-device
evidence. The tiny live decode may complete before the cancel command;
deterministic in-flight cancellation/disposal/observer ownership is established
by the managed controller tests, not inferred from that live timing.

Real converter tests reject an SDP input at the protocol whitelist without
opening its RTP/UDP stream, and retain all previously tested image formats,
including staged TIFF. The seven primary extraction observations execute on
both hosts; native fixture bytes come from the executed Windows generator.
Linux's generated PNG has different compression bytes/size but the same
normalization, attachment naming and byte-preservation contracts. This is not
a cross-platform encoder-bitstream claim.

Frozen reproduction:

```powershell
pwsh -NoLogo -NoProfile -File artifacts\tests\nomadnet-conversation-images-win-acceptance.ps1
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-conversation-images-linux-acceptance.ps1
```

These recipes guard exact native/managed binaries, frozen Pester, actual thin
adapter, both image fixtures and required transitive notice; Linux also guards
the transport/impairment helpers used by its separate propagation regression.
Each invocation has unique TRX and Pester result paths. The13 owned source
paths, raw snapshots, twelve compiled-source PDB checksums and public results
are recorded in `artifacts\tests\nomadnet-conversation-images-acceptance.json`;
snapshots are under `artifacts\tests\nomadnet-conversation-images-source`.

Parent independent execution of both SHA-guarded recipes reproduces the scoped
managed counts and all four native core cases on each host. Conversation images
and the separate existing primary `ImageData` case each pass on both hosts;
the Linux mixed propagation/restart/dropped-Resource regression passes1/1 in
235.68seconds. Exact parent TRX directories end in
`nomadnet-conversation-images-win-recipe-75e13bd7eb2b4fe894a0b71375ca5d91`
and `nomadnet-conversation-images-linux-recipe-25927b00baa84a718329f44c30e7d331`.
All13 frozen source/snapshot hashes, recipe hashes and the selected binaries
were checked before those repetitions; this subsequent documentation update
does not change the frozen production or test source.

The final isolated full solution Release build has zero warnings/errors.
`dotnet build DotReticulum.Ecosystem.slnx -c Release --artifacts-path artifacts\builds\nn-ci -v quiet`
and `dotnet test DotReticulum.Ecosystem.slnx -c Release --no-build --artifacts-path artifacts\builds\nn-ci --logger trx --results-directory artifacts\tests\nn-ci -v quiet`
pass2328cases with109explicit skips/0failures, counted from all five actual
TRX case-result collections. Without the selected converter, that full gate
explicitly skips four additional NomadNet converter cases.
An earlier longer-artifact-root run retained17Sideband profile SQLite snapshot
failures, not NomadNet failures; Sideband was untouched by this slice.
Concurrent Sideband changes mean the fresh green gate is not a path-only
causal control. Initial fixture/observation failures and the pre-protocol
acceptance paths remain in the manifest rather than being rewritten as passes.

#### Browser inline loading

`images` reports the persisted policy. `images never` disables inline work and
joins the previous loader before returning; `manual` waits for `images load`;
`always` automatically loads page and new partial descriptors. `auto` follows
executed primary `Browser.should_load_images`: local is eligible without a
link; remote needs truthy RTT strictly below1.5seconds and measured rate
strictly above10,000 **bits/second**, not bytes/second. A nonzero link expected
rate wins; otherwise the primary fallback is
`responseTransferBytes / max(responseSeconds, 0.0001) * 8`.
Equality at either threshold does not admit loading. Native request metrics
are captured before normal link teardown. Cached pages without metrics do not
qualify for remote automatic loading; explicit manual/always still works.
Negative/nonfinite network measurements are rejected as a native safety policy.

`GenerateNomadNetInlineMediaVectors.py` executes both actual primary methods on
data-only hosts for60 combinations (four modes, local/missing link, strict
thresholds, missing/zero rate, fallback and time clamp). It replaces neither
method. Actual `Browser.py` SHA-256:
`a36f393f3e6a356a4c5b19f39596ca3f4fe8812aaf5f09be6c05573173d88079`.
The primary separately gates these methods on Kitty capability. Native inline
ASCII deliberately does not implement that graphics protocol or its layout.

One decoder runs per browser with a one-entry latest-page queue, at most32
distinct descriptors and16 admitted manual requests. Additional descriptors
remain explicitly downloadable and the limit is logged. Failures are reported,
do not disable other images/partials and are retried only by explicit reload.
Observer exceptions are isolated; loading also runs without an observer.
Navigation/disposal cancels and joins queued/in-flight work; repeated
disposal waits for the same cleanup. Form/descriptor indices and hidden folds
are unchanged. Existing explicit ANSI preview remains opt-in; inline output
is bounded ASCII without terminal control characters.

The separate native `storage/cache/remote-images` cache stores validated WebP,
keyed by canonical destination/path SHA-256; it is not the source-conversion
cache. It has a256-file/192-MiB quota,16-MiB response and4096-entry scan bounds,
atomic replacement, header-corruption regeneration and linked-path rejection.
Cached bytes still require FFmpeg for local decoding. `images reload` bypasses
the image caches while preserving current form state; unlike the primary's
force-image operation it does not reload the whole page. This is a declared
native interaction difference, not pixel-identical frontend parity.

Core INI import now includes `[textui] image_loading`. The constructor generator
selects actual `client.user_interface=text` before `--daemon` suppresses screen
startup; otherwise the primary does not parse TextUI options. It explicitly
sets `colormode=256`: the pinned missing-colormode branch errors on nonexistent
`nomadnet.ui.COLORMODE_16`. These are disclosed real configuration prerequisites,
not changes to the reference handler or source. Four actual-constructor cases
observe manual/never/default-auto/always.

Final-source inline acceptance includes the Trace fallback below and uses the
same primary/RNS/LXMF pins above:

| Gate | Windows | Linux |
|---|---|---|
| Scoped Release tests with selected FFmpeg |130passed,5explicit Linux-only skips |135passed,0skips |
| NativeAOT publish with both ILLink/ILC warnings as errors and `-warnaserror` |passed, zero warnings/errors |passed, zero warnings/errors |
| Selected native `--help` and Native Pester |4/4 |4/4 |
| Expanded actual primary application image/profile/paper workflows |1/1,149.39seconds |1/1,110.21seconds |
| Actual primary RRC presentation through Python transport hub |1/1,8.40seconds |1/1,3.80seconds |
| Existing RRC/forms/partials/replay across Python transport |not rerun in this inline gate |1/1,19.41seconds |
| Complete mixed propagation/restart/scheduling/impairment case with strengthened readiness |not exercised on Windows |1/1,246.62seconds |

The expanded image case exercises never refusing manual load, persisted manual
and always, initial and partial images, missing-image failure with subsequent
valid media, explicit refetch, one canonical remote cache entry for duplicate
URLs, and a complete browser restart loading cached page/image while the Python
application is stopped. FFmpeg remains present for that offline decode;
earlier independent native/primary hosted conversion-cache checks separately
use a missing converter. Native control/form/descriptor state remains usable.
The source's positive/negative auto thresholds are executed vectors, not
inferred live RTT/rate coverage. These are direct image application checks;
RRC/form transport-hop evidence is separate.

Frozen assertions:
`artifacts\tests\nomadnet-inline-final.Tests.ps1`,
SHA-256 `5610ab46ae269662a73a72045bc13bfb4d28cab0fa9cf16e0513e925b7966694`.
Exact selected NativeAOT binaries:

- Windows `artifacts\builds\nomadnet-inline-final-win\publish\NomadNet\release_win-x64\nomadnet.exe`,
  SHA-256 `534925914e97fc4781f48640a3378f051e23119b90d401acce67bf72857d6004`.
- Linux `artifacts/builds/nomadnet-inline-final-linux/publish/NomadNet/release_linux-x64/nomadnet`,
  SHA-256 `2d852e3db9ea972dda54801d8501a3e3a488405fd873f66a6def53ca5475c4e2`.

The final Linux mixed case separately records one actual dropped Resource part,
5 ms per forwarded frame,296 frames and2,241,628 encoded forwarded bytes, with a
SHA-256-checked2,200,000-byte attachment. It retains both implementations'
propagation-node restarts, persisted identities, retrieval/deletion, scheduled
sync, cancellation recovery, reverse delivery and direct-failure propagation
fallback. These counters belong to the final binary, not the pre-logging runs
described below. The final harness uses Pester6.2.0 on Windows and5.7.1 on Linux.

The environment-specific frozen recipes select those exact binaries, reject
changed suite/managed/native hashes and preserve the transitive QR notice:

```powershell
pwsh -NoLogo -NoProfile -File artifacts\tests\nomadnet-inline-final-win-acceptance.ps1
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-inline-final-linux-acceptance.ps1
```

The coordinator independently executes both final hash-guarded recipes with the
same final Trace-fix binaries and assertions. Windows passes130managed tests/
5Linux-only skips, Native4, Images1 (124seconds) and RRC presentation1
(10.28seconds). Linux passes135managed tests/0skips, Native4, Images1
(109.22seconds), RRC presentation1 (3.64seconds), RRC/forms1 (18.74seconds) and
the complete mixed propagation case1 (237.01seconds). These independent final
runs supersede, without relabelling, the pre-logging-fix acceptance below.

`artifacts\tests\nomadnet-inline-acceptance.json` records the17 owned source/test
paths and separate per-suite TRX counters. An earlier concurrent full-solution
invocation was **not green**:2155passed,95explicit skips,1failure in a separate
Sideband call-concurrency assertion (also reproduced unchanged in isolation).
The command is `dotnet test DotReticulum.Ecosystem.slnx -c Release
--artifacts-path artifacts\builds\nomadnet-inline-integrated-final
--logger 'trx;LogFilePrefix=integrated'
--results-directory artifacts\tests\nomadnet-inline-integrated-final --nologo -v:q`.
No Sideband code was changed by this slice. Its subsequently corrected call
concurrency and current status remain in [sideband.md](sideband.md).
The final isolated full-solution Release build passes with zero warnings/errors;
its matching non-opt-in test gate passes **2183 tests,99 explicit skips,
zero failures**, including final NomadNet Trace tests:

```powershell
dotnet build DotReticulum.Ecosystem.slnx -c Release --artifacts-path artifacts\builds\fleet-agc-integrated-final -v quiet
dotnet test DotReticulum.Ecosystem.slnx -c Release --no-build --artifacts-path artifacts\builds\fleet-agc-integrated-final --logger trx --results-directory artifacts\tests\fleet-agc-integrated-final -v quiet
```

The final per-case totals were independently read from distinct TRX files in
`artifacts\tests\fleet-agc-integrated-final` and exported to
`artifacts\tests\nomadnet-inline-final-parent-trx-observations.json`.
An earlier coordinator coverage/tooling gate reported2159passed/95skips,
62Pester tooling cases and all eight production assemblies covered; its exported
per-case observations remain in `nomadnet-inline-parent-trx-observations.json`.
That older TRX output root was subsequently reused; its newer contents are not
immutable evidence for that older aggregate. The earlier failed local artifacts
are separate and retained. The final full-solution gate does not replace opt-in primary-peer
acceptance. Scoped NomadNet gates above pass independently. No new stack API
was required.

Earlier pre-logging-fix binaries were independently accepted by the coordinator:
Windows128passed/
5Linux-only skips, Native4, actual primary1 (~114seconds), RRC presentation1;
Linux133/133, Native4, actual primary1 (~113seconds), RRC presentation1 and
RRC/forms1. That independent acceptance does not erase the separate full-suite
or mixed-propagation failures below.

The pre-logging-fix inline binary's separate mixed propagation regression failed twice after
the native PN restart: primary reported state241 (`PR_LINK_FAILED`) despite a
new observed announce count and active client-to-transport route. No delivered callback occurred;
later impaired-transfer assertions were not reached (148.78/148.98seconds).
Both failed invocations remain retained rather than counted as passing. Earlier accepted propagation
evidence is not silently relabelled as evidence for this binary.
The previously accepted presentation binary, selected by its existing frozen
recipe, passes the same complete mixed case in245.65seconds as a diagnostic
control. The old/new mixed `Describe` blocks are identical after LF
normalization (18,677characters,
SHA-256 `2df7e28d28950e0697758374801b09685226132334fabe58216b2ea0900cda0f`);
no timeout, retry, route-expiry or expected-result assertion was changed.
Both launcher-only hybrids pass the complete unchanged case: old PN/new client
in243.49seconds, new PN/old client in245.02seconds. Actual per-process binary
hashes are retained. The failed runs' existing `path-stats` reveal the distinction:
the Python **transport's** route to the restarted native PN still references
its stopped TCP interface (`online=false`, `active=false`), while the Python
client's next-hop interface is healthy. Both successful hybrids instead observe
the transport route replaced with the new connected interface. An increased
client announce count alone therefore does not prove a fresh end-to-end route.

Retained portable-PDB source checksums are identical across the old/new Core,
Crypto, Interfaces, Transport, LXMF and Applications dependencies. Their NuGet
package/version inventories also match; only seven ordinary compiled documents
differ, all in this NomadNet slice. Method-body comparisons find no Core/Crypto/
Interfaces/Transport changes; remaining shared differences are generated version
or generated-regex path identities. Configuration manifests record identical
Python PN configuration and native PN RNS configuration after replacing only
ephemeral test ports. The native PN's static relevant settings match except the
new default image-loading metadata.

The readiness gate now additionally requires the observed **transport** route
to be online and active within the original30-second deadline. It retains the
increased-announce assertion, existing poll interval, topology, binaries,
subsequent delivery assertions and sole retrieval attempt. No path expiry,
extra sender/retrieval retry or unconditional delay is introduced. Acceptance
with this stronger precondition passes **1/1 in240.91seconds**, selecting the
same Linux binary `338f7027…4236323` for both native roles. The full case observes
one dropped Resource part,295 delayed frames,2,241,830 forwarded encoded bytes
and the SHA-256-checked2,200,000-byte attachment, and covers reverse text,
scheduled retrieval, cancellation recovery and direct-failure propagation
fallback. Neither earlier failure is reclassified as a pass.

The strengthened frozen suite has SHA-256
`5610ab46ae269662a73a72045bc13bfb4d28cab0fa9cf16e0513e925b7966694`;
the earlier frozen suite and failed outputs remain untouched. Reproduction:

```powershell
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-inline-route-ready-acceptance.ps1
```

The coordinator independently executes this same hash-guarded recipe:
**1/1 passed in236.87seconds (237.38total)**, with unchanged binaries,
topology, deadline and all retained assertions. The earlier failed runs remain
failure evidence, not successful runs retroactively relabelled.

A subsequent logging-only correction reports through `Trace.TraceError` when
the inline-image logger is absent or throws, retaining the original error and
logger-failure context. The isolated Release regression below passes **9/9**,
including two new Trace-capture cases asserting failed observers do not stop
subsequent observers or successful image rendering:

```powershell
dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --artifacts-path artifacts\builds\nomadnet-inline-log-fix --filter "FullyQualifiedName~NomadNetInlineMediaTests" --logger "trx;LogFileName=inline-log-fix-final.trx" --results-directory artifacts\tests\nomadnet-inline-log-fix --nologo -v:q
```

The final selected binaries above were strictly republished after this correction,
not relabelled from pre-fix acceptance. Their portable-PDB document checksums both
match final `NomadNetInlineMedia.cs`
(`173f417e570470bf22aa7ed09e970ce9ea3a8d822022e5a17faf41194eef7be6`).
The isolated Windows/Linux build scripts are
`artifacts\tests\nomadnet-inline-final-win-build.ps1` and
`artifacts\tests\nomadnet-inline-final-linux-build.ps1`; each runs the same
strict publish flags above and complete scoped Release tests. The distinct TRX
records are `artifacts\tests\nomadnet-inline-final\windows.trx` and `linux.trx`.
Pre-logging binaries/results and earlier failed runs remain retained separately.
The initial new test used a manually built page
without image-layout rows; its two rendering-assertion failures remain in
`inline-log-fix-initial-fixture.trx`. Corrected tests use parsed Micron rows;
the separate final TRX records9 passes and no skips or failures.

Set `mediaConverter` to an available FFmpeg executable. No FFmpeg binary,
library, Python, Pillow or image assets are bundled with the application.
FFmpeg is a separately licensed external dependency; obligations depend on the
selected build (the exercised builds enable GPL components), not the .NET
application's license. There are no additional managed/native-library package
dependencies or runtime dynamic-code requirements in this slice.

Conversion uses primary defaults quality85 and maximum dimension1200 without
upscaling. Limits are 16 MiB encoded input/output, an eight-second process
deadline, 64-MiB individual FFmpeg allocation, and sixteen megapixels for decoded
WebP header/preview validation. These are native safety policies, not primary's
image-widget limits (8-MiB payload/16-MiB conversion). TIFF decoding requires a
seekable file; a bounded copy under `storage\image-work` is deleted after process
and I/O completion. Cancellation/failure kills the owned process tree and joins
its input/output. Symlink checks are not a claim of exhaustive protection
against a hostile local user racing filesystem changes.

Converted node images persist under `storage\cache\conversions` using the
executed primary `<source-sha256>.q85.d1200.webp` names, with 256-file/192-MiB
eviction. Authorization is checked before conversion/cache access. A cache hit
does not require an installed converter; corrupt headers cause regeneration.
Animated/multipage, transparency/EXIF and encoded-byte equality across different
FFmpeg versions have not been established.

### Propagation and recovery

`trust-node <nomadnetwork.node-hash> on|off` maintains explicit node trust.
`propagation auto` chooses the nearest trusted identity with an active, valid
`lxmf.propagation` announce and a known propagation path. The identity-derived
propagation destination must match the announced destination. Untrusted nodes
are never automatically selected; tied candidates keep snapshot order.
`propagation <propagation-hash>` overrides automatic selection;
`propagation off` is a native extension disabling client selection, not the
separate hosted-node setting.

`sync [limit]` starts retrieval without blocking the command loop.
`sync status`, `sync cancel` and `sync wait` observe/cancel/await that attempt.
Concurrent sync requests do not create overlapping retrievals. Manual sync
defaults to unlimited; `sync schedule on|off [minutes] [limit]` configures the
separate periodic limit. Attempts persist their timestamp **before** LXMF I/O.
Disposal cancels and joins outstanding retrieval and outbound supervision.
`fallback on|off` controls a single direct-failure propagation retry, not a
second direct delivery loop. A propagation `sent` status proves node acceptance,
not recipient delivery. `announce` reannounces delivery and hosted destinations.

Three deliberate policy corrections differ from the pinned primary: its
autoselector forwards a saved node's `source_hash` directly rather than deriving
the propagation hash, no-candidate selection retains a previously selected node,
and its job loop does not consult the parsed `periodic_lxmf_sync` flag. Native
selection uses the associated propagation destination, clears revoked/unavailable
automatic trust selections, and honors the disable flag. These are not claimed
as bug-for-bug parity. The executed primary policy fixture records the original
selection/failure decisions, with storage/network observation seams disclosed.

### Directory and conversations

`contact trust <hash> unknown|trusted|untrusted|warning` persists the primary
numeric classifications 2/255/1/0. Collision warnings follow executed
`Directory.trust_level`: a saved, non-trusted peer announcing another saved
entry's exact name gets warning0; a trusted peer keeps its status; a completely
unsaved peer remains unknown2. Names are compared ordinally, not case-folded.
`contact notes <hash> <text>` stores bounded local notes; renaming retains policy.

`contact ignore <hash> on|off` updates a separate durable blacklist and calls the
existing LXMF `IgnoreDestination`/`UnignoreDestination`. Ignored delivery sources
are also checked before storing queued incoming messages/attachments, and hidden
from the application peer listing. Forgetting a contact does not remove its
blacklist entry. This does not remove the Reticulum stack's own identity/path
knowledge. Automatic propagation trust remains the distinct `trust-node` policy.
Saving a contact with a recalled Reticulum identity also updates an already-saved
associated `nomadnetwork.node` entry's trust, in the same atomic state write.
It preserves that node's name, notes, rank and identification consent, and does
not invent a node entry or infer associations from display names.

`contact node <hash> on|off` marks a saved entry as a node.
`contact rank <hash> <integer|none>` sets its signed 32-bit rank.
`nodes` lists saved nodes and `open-node <number>` browses their default pages.
Ordering matches the actual primary: rank, descending trust, Unicode-code-point
name order. The primary's unranked fallback expression is Python `2^32`, which
evaluates to **34**, not 4,294,967,296; this observed behavior is preserved.
Saved nodes are not silently removed from that list by LXMF blacklisting.

`contact identify <node-hash> on|off` persists browser identification consent.
Unknown nodes and saved entries without consent are **anonymous by default**;
trust alone does not disclose identity. This corrects the earlier native
browser's unconditional identification. The policy covers page, partial, file
and media requests, not the distinct LXMF/RRC protocols that require identity.
Changing consent affects new network requests: use `reload` to bypass a cached
page, since a cached authorization denial or previously authorized response
can otherwise still be shown. Manual identification of a retained browser link
is not implemented; native browsing currently opens a new link per request.

`contact delivery <hash> direct|propagated|opportunistic` controls ordinary text
and attachment sends through the existing LXMF methods. Explicit
`send-propagated` overrides that preference and requires a selected propagation
node. This is not a new message transport.

`conversations` lists latest activity, counts and pending unread/failure alerts.
New inbound IDs increase unread; a final failed/rejected outbound increases
failure count once. Duplicate storage updates and stale terminal callbacks do
not inflate counters. `conversation read <hash>` clears both alerts.
`conversation active <hash|off>` selects a session-local viewed peer, clears its
alerts, and prints its incoming messages without setting unread; restart clears
that selection, not the durable counts. `messages [hash]` is an observational
history dump; use explicit read/active commands to acknowledge it.

`conversation purge-failed <hash>` removes failed/rejected outbound records but
preserves received/delivered history. `conversation delete <hash>` removes all
that peer's records/alerts and their stored attachment roots.
`conversation export <hash> <name.txt>` writes a plain UTF-8 snapshot into
`downloads`, a native extension rather than upstream persistence import.
History deletion is committed before attachment cleanup; a cleanup I/O error is
reported and may leave orphan attachment files, not silently reported as a
fully atomic multi-file deletion. New arrivals can create a new conversation.

Schema1 state without the new collections loads with empty alerts/policies;
old messages are not retrospectively assumed unread. Limits remain 2,000
messages/alerts, 10,000 contacts/ignored destinations, 4,096 note characters and
the existing 5-MiB state bound. Directory/history native JSON does not claim
compatibility with the primary's MessagePack/index/unread files.
Oversized contact-policy changes reject and restore their in-memory policy
without evicting message history or modifying the existing disk snapshot.

### Browser navigation

`history` lists successful root-page navigations and the one-based current
position, without printing submitted field values. `back` and `forward` replay
the recorded destination, path and original submitted fields; later editing of
the redisplayed form cannot change those snapshots. A fresh navigation after
going back discards the forward branch. Reload and partial/anchor updates do not
add history entries. Reload still bypasses the page cache; submitted history
entries always bypass it.

History is session-local, not restored after restart and never written to state
or cache, since it can include password fields. Native bounds are 64 entries,
64 KiB of UTF-8 form keys/values and at most 1,024 form pairs per entry
(approximately 4 MiB total form text). Oldest entries are evicted. As in the
primary, back/forward move the history cursor before retrieval; a failed replay
does not roll the cursor back. A failed fresh retrieval does not append an entry.
The terminal serializes navigation commands; partial refreshes cannot alter
history.

### Group chat and interactive pages

RRC uses the same application identity but an independent identified `rrc.hub`
Link, as required by the primary reference. It does not duplicate or repurpose
LXMF. `rrc connect <hub-hash> [nick]`, `rrc join <room> [key]`,
`rrc send <room> <text>`, `rrc action`, `rrc part`, `rrc list`, `rrc who`,
`rrc ping`, `rrc history`, `rrc disconnect` and `rrc reconnect` operate the
session. `rrc auto-reconnect on|off` persists optional recovery (default off,
matching primary NomadNet). Reconnect waits grow exponentially to 60 seconds;
HELLO is attempted five times with RTT-adjusted spacing. Each envelope must
fit the negotiated Link MDU: larger chat messages are rejected, not fragmented
with an invented format. Hub nickname/room/message/room-count/rate limits are
enforced. Unsolicited resource data is not displayed; expected notice/MOTD data
is size-matched, optionally SHA-256 checked, and bounded to 256 KiB.

Profiles live at `rrc\<hub-hash>.cbor`; they atomically retain nick, desired
rooms, optional reconnect policy and bounded history. They are **not** primary
NomadNet `rrc_hubs`/concatenated history files. Room keys are not persisted.
Only one hub is active in the terminal; stored profiles for other hubs remain.
The receive queue, members, nick map and resource expectations are bounded;
observer failures are reported without disabling core processing.

`rrc active <room|off>` selects a viewed room and marks it read. Remote messages
outside that room set unread and mention flags; local sends do not. Roomless
notices route to the viewed room; notices in other rooms mark unread but not
mentions. `rrc read <room>` clears both flags and `rrc clear <room>` also deletes
its durable history. View/read flags are session-local, matching the primary.
`rrc history-limit <0-1000>` persists a per-hub room limit; zero disables that
extra limit, not the aggregate 1,000-item/2 MiB safety bound. Primary's setting is
global and accepts larger/unlimited histories, a disclosed configuration difference.
`rrc auto-list on|off` and `rrc auto-who on|off` persist independent default-off
policies: `/list` follows WELCOME; `/who` follows our own acknowledged JOIN, not
another member's JOIN. Parsed automatic responses and room-list continuation
fragments update directory state without appearing as unread chat.

After browsing, `fields` lists controls without disclosing masked values;
`field <number> <value>` edits text (literal `\n` inserts a newline) or selects
checkbox/radio `on|off`. `open <link-number>` includes the link's selected
fields/variables. Checkbox values sharing a name comma-join; radios are mutually
exclusive by name. `anchor <name>` reveals a folded target; `fold <number>`
toggles section visibility. `partial <number>` or a `p:<id>` link retrieves
partial content with current combined form values. Initial loads and valid
refresh intervals run automatically with a one-second scheduler and four
concurrent requests; each has a 90-second deadline. Child controls/links/media
are renumbered structurally, never by rewriting literal user text. Root edits
and fold state survive refresh; replaced child controls reset to the newly
received defaults, as in primary widget replacement. `page` displays the current
composed page, and `partials` lists root targets. Like primary `detect_partials`,
automatic discovery is root-only: partials embedded inside a fetched partial
are not recursively scheduled. Native failures display bounded error content;
later periodic or explicitly forced updates can recover. A malformed target
is retryable manually rather than permanently disabled as in primary.
`media <number> <name.webp>` downloads bytes through `/media`;
it does not decode/display or validate image quality. Forms are limited to
128 aggregate controls and 64 KiB submitted text; composed encoded text is
limited to 4 MiB, links to 2,048 and media descriptors to 128. These are safety
bounds, not primary layout defaults.

### Global RRC history and original functional styling

These controls target primary1.4.4
`f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`, not copied Urwid assets.
`GenerateNomadNetRrcSettingsVectors.py` executes actual
`createDefaultConfig/applyConfig`, `RRCHub._record_message/_load_history/
_clean_history`, `Channels._message_widget` rendered through real Urwid,
`get_nick_color`, `_scan_mentions` and pane layout. The disclosed headless
owner supplies configuration/identity/UI objects; it does not replace these
methods. Cleanup observations use the real clock with one-second age margins.
The fixture records canonical Git-blob SHA256:

| Source | SHA256 |
|---|---|
| `nomadnet/RRC.py` | `195e3dd62ab6678f5192b8b9c5a3c3e399e519018f3fb2216a16315c32895925` |
| `nomadnet/ui/textui/Channels.py` | `1cc718e1abc6332ac861d3c6cd0ff504504c22f881a3aae4bee9eeafa9ee6ab7` |
| `nomadnet/NomadNetworkApp.py` | `c36ce4c537fe5af534910d1f5200d2a2ea5dd50f74bf9e4157222eff15614eb2` |

| `[rrc]` field; primary contract | Native functional disposition |
|---|---|
| `history_per_room_cap=500`; `RRC.py:691,735,761` | **implemented bounded**: trim live visible history per room; filter loaded rows before applying the cap.0 disables this cap, not native aggregate bounds. The stricter nonzero global/native `rrc history-limit` cap wins. |
| `filter_loaded_history=yes`; `RRC.py:698,761` | **implemented**: remove only loaded notice/system rows from visibility, not message/action/error or newly received notices. Keep a separate bounded durable window inside existing CBOR key3 so later filter-off restart can recover retained rows, rather than silently deleting them during save. |
| `ephemeral_notices=10` minutes; `RRC.py:703,798` | **implemented bounded**: remove visible notice/system rows only when age is strictly greater than timeout. Preserve the observed strict five-second cleanup-admission test and updating last-clean even when admission fails.0 expires past notices, not “disable cleanup”. Cleanup on traffic/history reads does not delete durable rows. |
| `color_mention_timestamps=yes`; `Channels.py:1274–1420` | **implemented original styling**: own mentions recolour timestamps when enabled. With nickname colours disabled, the timestamp uses the original default mention colour even when body mention colour is customized, matching the primary interaction. |
| `nick_colors=yes`; same widget and `get_nick_color:1254` | **implemented original styling**: sender/member/known peer-mention colours and action emphasis. Custom palette index is `(big_endian_hash+15)%palette_length`, verified against actual primary. Without custom colours, an original six-colour palette replaces the primary theme, not a copied asset. |
| `mention_color` absent | **implemented**: six-hex custom own-mention foreground, otherwise own nickname colour/default original mention colour; own mentions are emphasized. Own-nickname matching supports spaces/Unicode with ASCII word boundaries and case-insensitive comparison; known peer names follow primary's ASCII scanner. Code regions are not highlighted. Full Python Unicode case-fold equivalence is not claimed. |
| `nick_colors_theme` absent | **implemented bounded**: up to128 valid six-hex colours; empty uses original palette. Source scalar/list grammar remains handled by the existing ConfigObj parser. |
| `justify_msgs=yes` | **implemented original layout**: `rrc view` wraps to80 Unicode scalars and aligns continuation beneath the timestamp column; off wraps to the left margin. This is not grapheme/display-cell or Urwid geometry parity. Compact live/history stream keeps unindented multiline composition rather than turning every incoming line into a full-screen layout. |
| `space_msgs=no` | **implemented**: optional extra blank line for message/action, not notice/system/error. |
| `show_gutters=yes` in generated config/form; constructor/missing key false | **implemented original panes**: `rrc pane users\|channels off` hides content. With gutters on, `rrc view` retains explicit expansion-command affordances; without gutters hidden panes occupy no output. Visibility is session-local, not a fabricated persisted primary option. |
| `enable_esoterics=no` | **upstream-placeholder**: whole-primary AST inventory finds zero runtime reads of `rrc_enable_esoterics`. The flag is editable/resettable/preserved, with no invented effect or missing software feature implied. |

Source controls are edited with `config section rrc`, JSON-valued `config set`,
`config reset confirm`, `config cancel` and `config save confirm`. Full restart
is explicit; each opened RRC client snapshots its retention/filter/cleanup
policy. Do not expect saving forms to mutate an existing link. `rrc view lobby
color` explicitly requests controlled true-colour ANSI, including through a
pipe; ordinary redirected output contains no ANSI. Invalid colours, nonfinite
or negative times, caps above1000, and palettes above128 fail explicitly before
activation. This is a safety difference from primary's fallback/invalid-colour
filtering. Ephemeral time is bounded0–43200 minutes.

The existing per-hub native CBOR snapshot remains the only RRC store; no new
archive, fake history format or protocol is introduced. Visible and durable
windows each retain at most1000 rows/2MiB message text; each persisted body is
at most256KiB and encoded profile at most4MiB. Increasing a visible cap cannot
recover beyond this durable window; primary append logs are not indefinitely
retained. Clear explicitly clears both windows. Imported history remains
labelled unauthenticated and never becomes live membership/authorization.
Core persistence does not depend on message subscribers. The executable's
existing profile lease and owned receiver/gate/disposal remain in force.

Managed tests T490–496 cover executed defaults/placeholder, colour and mention
interactions, Unicode/code boundaries, layout/output bounds, cleanup boundaries,
projection/invalid values, filtering/caps/restart/clear/cancellation/disposal.
Dedicated native/reference tests T497–499 are
`tests\Pester\NomadNetRrcSettings.Tests.ps1`; selection requires
`DOTRETICULUM_NOMADNET_RRC_SETTINGS=1`, exact native/reference paths and a fresh
owned evidence directory. They exercise actual primary/rrcd messages both ways,
retention, original styled view/panes, invalid input, confirmed reset/cancel,
restart and unchanged identity, plus independently regenerated primary vectors.
This is local-presentation evidence, not a new wire/topology/audio/hardware claim.
Linux replay remains blocked by WSL EIO; terminal controls/utility visibility
have their separate C95 acceptance below. Full ConfigObj/Urwid remains incomplete. The selected-RNS policy
controls have their separate application acceptance below.

### Selected-RNS probe response and packet-proof policy

The application selects the real RNS source via `--rnsconfig <directory-or-file>`.
`config section reticulum` edits that selected file, independently of native
`settings.json` or explicitly enabled Nomad primary configuration. These two
controls are not new native JSON aliases:

| Primary field; default | Observable running-owner contract |
|---|---|
| `respond_to_probes=false`; `Config.py:672–676` | **implemented** through public `RnsdConfig.RespondToProbes`. Only a transport-enabled owner registers `rnstransport.probe`. Setting the flag without selected-RNS `enable_transport=true` does not create a responder. A connected shared client is not a transport owner. This is separate from Nomad's `[node] enable_node` hosting flag. |
| `use_implicit_proof=true`; `Config.py:679–683` | **implemented** through public `RnsdConfig.UseImplicitProof` and existing router owner. Default packet proofs follow the selected policy after restart; explicit proof overloads and explicit link proofs retain their separate stack contract. |
| `panic_on_interface_error=false`; `Config.py:686–690` | **intentional safety difference**, disposition `safety-refused`. The native fixed policy terminates the owning service on owned top-level interface failure and isolates failed child peers. Changing this policy is not implemented as a toggle. Form activation and any raw explicit panic key are refused; remove the key to use fixed ownership. The upstream form accepts/stores true, as the independent fixture records; native does not silently pretend to execute it. |

```text
config section reticulum
config set enable_transport true
config set respond_to_probes true
config set use_implicit_proof false
config save confirm
config cancel
quit
```

Saving does not hot-reload the active owner. Restart the same application with
the same selected RNS source. `status` reports immutable running configuration,
actual router transport mode/shared-client role and the public runtime
`probe_responder` statistic, not a hash fabricated from the form draft.
Native identity/delivery address and transport probe identity persist.
Cancel/reset stage only; Save still needs confirmation. Unknown untouched
comments and unrelated supported interface configuration remain byte-preserved.
Malformed booleans, unsupported RNS grammar and external source conflicts fail
without overwriting the selected file. Existing bounded writer/profile lease,
cancellation and restart serialization are retained.

The selected stack is `4989f050aed865d16ed9f1460071c1a63cd4c7e9`,
including policy implementation `64b76f6df574406f85b5ab7666b1a91dab9674fa`.
This application slice does not edit the submodule or duplicate stack wire
tests. The parent separately accepted constructor observations, proof formats,
both directions/hop/restart and encrypted resource transfer in that stack.
The current application receipt must use its own newly published binary;
the earlier C92 `e359f1b...` binary targets the previous stack and is not relabelled.

`GenerateNomadNetRnsPolicyVectors.py` executes actual primary
`f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`
`ConfigSectionView` path resolution/population, real checkbox changes,
cancel/save/reconstruction, actual Yes/No reset buttons, ConfigObj and atomic
writer. Only the headless owner/screen/modal notice/restart observations are
supplied; no router method is replaced. Canonical source SHA256:
`nomadnet/ui/textui/Config.py`
`13a0a531ccc2fc207dc6b4c7f644c3eff6d261bb34edf84781c1c6f76f4b0421`;
`nomadnet/util.py`
`77a4368c73b8a84ef2e9df41b4e423bcb1d0677e12ed18debe445996fa3a9e38`.

T510–513 cover actual form observations, defaults/reset/cancel, typed
malformed/safety refusal, selected-source/runtime-owner activation, immutable
running options, identity-preserving restart, cancellation/conflict recovery
and exact unrelated/native-file preservation. The formerly failing C89 raw
RNS test now asserts actual supported true/false policies and continues to
reject malformed input and unsupported grammar without altering bytes.
T514–516 select independently regenerated primary observations, actual native
application form saves/restarts against pinned unmodified Python RNS and
startup failure/recovery. The live test uses the stack's existing thin
`proof_probe_node.py` observer, not copied wire implementation/test classes.
It exercises native explicit96/implicit64-byte replies, native utility probes
back to Python, disabled responder and nontransport-owner negative cases
after proving the same transport healthy. Source and identity guards remain
unchanged across restarts. Full reset retains `share_instance=no` explicitly
to isolate the test from other local shared-instance users.

The stack's strict initial15-second/periodic7200-second probe announcements
and link rejection remain stack-owned; this application gate observes initial
announcements but does not claim a new two-hour scheduling or link-proof test.
No new multi-hop/impairment, Linux, physical network or hardware claim is made.
Linux replay remains blocked by WSL EIO. Full ConfigObj/Urwid remains required
software work; terminal controls and utility visibility have separate C95 acceptance.

### Original terminal controls and utility visibility (C95)

All nine formerly stored-only TextUI controls and utility visibility now execute.
They are **bounded original terminal behaviors**, not copied Urwid implementation,
palette, glyph-table or guide assets. No field in this slice is a primary
placeholder. This does not resolve full editor/grammar/presentation parity.

The reference remains primary NomadNet1.4.4
`f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`.
`tests\Interop\GenerateNomadNetTerminalControlVectors.py` executes actual
`TextUI.__init__`, `set_colormode`, `Extras.IntroDisplay`, `MenuDisplay`,
`SubDisplays._utilities_enabled`, `AnnounceTime` and both name utilities.
Separate actual constructors start NoneUI, then actual `applyConfig` reads the
requested ConfigObj text selection. This permits observation of fatal malformed
TextUI cases; it is **not** primary interactive startup. Headless screen,
main-display owner, event-loop and `stty` observers are disclosed. The generator
emits factual observations and hashes, not upstream UI assets. Its checked fixture
is `tests\NomadNet.Tests\NomadNetTerminalControlVectors.json`.

| Primary source | Git-blob SHA256 |
|---|---|
| `nomadnet/ui/TextUI.py` | `9750582a0c8b0e7eb01a67f09c678bf2a9649b44968230479421bcd52288448a` |
| `nomadnet/ui/textui/Config.py` | `13a0a531ccc2fc207dc6b4c7f644c3eff6d261bb34edf84781c1c6f76f4b0421` |
| `nomadnet/ui/textui/Main.py` | `a36de955e21e94261fefd6eebe5a6b471e1072db6f79e1833f19dc7d1fb762a5` |
| `nomadnet/util.py` | `77a4368c73b8a84ef2e9df41b4e423bcb1d0677e12ed18debe445996fa3a9e38` |

The fixture also records exact `Extras.py`, `Network.py`,
`NomadNetworkApp.py` and `Directory.py` hashes.

| Field; primary consumer | Native effect and boundary |
|---|---|
| `intro_time`, `intro_text`; `TextUI.py:234–268`, `Extras.py:IntroDisplay` | Configured original title followed by cancellation-aware delay before the terminal menu. Nonpositive durations disable both. Native accepts finite −3600–60 seconds and a control-free0–256-character title; larger or malformed values fail before activation. Headless daemon mode never shows or waits for the intro. |
| `theme`; `TextUI.py:236–239`, `THEMES`, Micron/RRC consumers | Dark/light original heading palettes color the intro, menu, local guide and network monitor. This is not a copied primary theme. Monochrome deliberately emits no color. Existing Micron/RRC rendering remains authoritative for text. |
| `colormode`; `TextUI.set_colormode`, Micron/RRC consumers | Monochrome, ANSI16, indexed88/256 and truecolor presentation. RRC timestamp/nick/body colors are quantized to the configured depth; explicit existing `rrc view … color` does not bypass monochrome. Ordinary pipes remain escape-free; deliberate RRC color mode remains explicit. Missing primary `colormode` reproduces an actual upstream `AttributeError` (`nomadnet.ui.COLORMODE_16`); native deliberately retains its valid24bit default instead of that defect. |
| `glyphs`; `TextUI.py:241–252`, menu/browser/Config consumers | Original plain, Unicode and Nerd Font menu/status icons. Source-loaded unknown values fall back to Unicode, matching primary selection; form dropdowns/native JSON validate the documented choices. Missing source glyphs use Unicode; native/generated form defaults use Nerd Font. Physical glyph coverage/display-cell width is unverified. |
| `mouse_enabled`; `TextUI.py:265`, `MainFrame.mouse_event` | Interactive fixed-row original menu accepts left-button presses only, in addition to `menu select <number>`. Windows consumes actual Win32 mouse records because ConPTY converts SGR reports and `Console.ReadKey` discards them. One input owner bounds records/escape sequences and restores and verifies the exact prior console mode on every command, failure, cancellation and before external editing. Partial typed commands cannot be replaced by a click. Disabled mouse and malformed/out-of-range/wheel/release reports cannot activate entries. Unix SGR code exists but current Linux execution is unverified. |
| `hide_guide`; `Main.py:318`, `SubDisplays.__init__` | Hides only the Guide menu entry. Explicit guide navigation remains usable. Native first client-state creation shows the original local start topic even when hidden, paralleling the primary's independent first-run guide selection; native state-file discovery is not primary config-directory/first-run migration. |
| `sanitize_names`; `Directory.simplest_display_str`, Browser/Conversations/Channels/Network consumers | Announce labels, contact/conversation/peer/node names and RRC nick/member rendering use configured sanitation without altering stored names, trust or search inputs. Enabled: NFKC, category/spacing filtering and primary block exclusions. Disabled: modifier stripping retains ordinary symbols/emoji and compatibility characters. Terminal controls and multiline names still never execute/render, an explicit stronger safety boundary. Unicode globalization is enabled for NativeAOT; invariant mode was observed to omit NFKC in a real native test. Windows system globalization passed; other current platforms are unverified. |
| `animation_interval`; `Network.py:AnnounceTime` and progress/status widgets | `menu watch <1–60>` is an explicit active network view: fresh clock/known-destination/background-utility status on each frame, with configured1–3600-second interval. Awaited, cancellation-joined refresh has no detached timer or writer after exit. Primary accepts0 (observed); native refuses zero/negative intervals to prevent unbounded busy refresh. This is not Urwid's widget layout or every primary progress animation. |
| `utilities.enable_utilities`; `Main.py:SubDisplays/MenuDisplay` | Controls the visible/selectable Utilities entry only. Direct CLI utility commands remain reachable, consistent with visibility rather than authorization. No listener starts implicitly; selected-peer authorization remains unchanged. |

Native JSON names are `introTime`, `introText`, `theme`, `colorMode`, `glyphs`,
`mouseEnabled`, `hideGuide`, `sanitizeNames`, `animationInterval` and
`enableUtilities`. The existing section forms, import/export, opted-in primary
source, source disabling and atomic persistence project all ten values.
Changes require full restart; no running menu/service silently hot-reloads.
`menu`, `menu select <number>` and `menu watch <frames>` are original commands.
The existing guide, editor, Micron, RRC and network utilities are reused.

T540–T555 cover reference defaults/consumers, malformed/boundary/error paths,
all color/glyph modes, sanitation, mouse decoding, primary projection,
confirmed form saves/reset/cancel, persistence/source disable and real delay/
cancellation. T556–T558 opt into actual Windows NativeAOT ConPTY interactions:
all five color modes, all three icon sets, both themes, custom intro, mouse
dispatch/refusal, utility selection, refresh timing, Ctrl+C and identity restart.
T559–T563 are separate Pester cases for reference regeneration, native
form/source restart/reset, Unicode name rendering, first-run/visibility and a
direct-loopback receipt-backed probe to the actual primary Nomad utility
consumer. They do not duplicate the original utility/endurance claims.

Fresh-child replay, exact native/source/stack guards, compiler receipts and
failed attempts are retained under ignored
`artifacts\tests\nomad-terminal-controls`. The selected toolchain is
SDK10.0.403, runtime/NativeAOT compiler10.0.14; stack pin remains
`4989f050aed865d16ed9f1460071c1a63cd4c7e9`.
The replay operator takes a frozen `candidate.json` and refuses changed
production/LXMF/stack inputs or a changed native hash before and after execution.
It also guards acceptance/operator sources and actual reference clean revisions.
The selected binary is
`artifacts\tests\nomad-terminal-controls\native-accepted\nomadnet.exe`,
SHA256 `b2117d8ae472172bbd97835c84428533645123fe1b0e5ef0d21ee6241b195665`.
Recorded commands/results:

```powershell
# Passed: strict native publish, zero warnings; help and representative flows ran.
dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64 -o artifacts\tests\nomad-terminal-controls\native-accepted -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror -v minimal
# Passed:390 Nomad tests,17 explicitly skipped; Pester5/0/0; configured analyzer0.
pwsh -NoProfile -File artifacts\tests\nomad-terminal-controls\Replay-Candidate.ps1 -FullSuite -OutputDirectory artifacts\tests\nomad-terminal-controls\accepted-final-replay
```

Independent replay requires a **new** output directory; the selected manifest is
`artifacts\tests\nomad-terminal-controls\candidate.json`. The final receipt is
`accepted-final-replay\child\receipt.json`. A separate selected-binary actual Vim
ConPTY regression passed2/0 (`NomadNetExternalEditorNativeTests`, opt-in editor
path `C:\Program Files\Git\usr\bin\vim.exe`); its TRX is `accepted-vim\vim.trx`.
Independent parent frozen replay also passes390 managed cases/17 skips and
Pester5/0/0 in `parent-independent-replay\child`, including the three actual
ConPTY cases and direct primary probe. Root integration is recorded separately
in [testing provenance](testing.md#baseline-provenance).
Current Linux replay remains blocked by WSL EIO/read-only filesystem; no WSL
recovery/remount/shutdown was attempted. ConPTY is not evidence of physical
terminal fonts/display cells/keyboard or hardware/network topology parity.

## Validation and reproduction

### Terminal network utilities (C79)

**Implemented bounded native software with independently replayed actual-primary
direct-loopback acceptance.** The original terminal exposes the six primary utility functions
through `utility help`, reusing the existing Reticulum owner, Links, receipts
and streaming Resources rather than implementing a second transport:

```text
utility paths|rates|interfaces
utility identity [destination-hash]
utility path <destination-hash>
utility drop <destination-hash>
utility probe <full.destination.name> <hash> [bytes] [count]
utility send <rncp-receive-hash> <file-path>
utility speed address
utility speed listen <expected-peer-speed-hash>
utility speed send <peer-speed-hash> [size-label]
utility speed status|off
utility status|cancel|wait
```

The file path is the literal remainder of the command, including spaces; it is
not evaluated by a shell. Speed labels include their spaces, for example
`4 MB`. None of these commands implicitly enables a receiver. Identity output
distinguishes Reticulum identity, LXMF, optional hosted-node and speed addresses.
Management calls target the current local/shared Reticulum owner. Expiring a
path marks its timestamp zero; a repeated expiration can still succeed until
the owner's next cull, exactly as executed `RNS.Transport.expire_path`.

Probes default to16 bytes, one request and
`max(12 + first_hop_timeout, medium_path_timeout)`. Genuine identity discovery
and the full destination-name/hash match precede encrypted packets and
validated delivery receipts. Results retain RTT, hops and loss. Local proof
RSSI/SNR and shared-owner proof RSSI/SNR/Q are optional local measurements,
not authenticated peer assertions or evidence of physical-radio acceptance.
The native safety bounds are500 requested bytes,1,000 probes and finite
timeout hints no greater than one hour; an encrypted packet can exceed its
actual MDU below that requested-size bound and then fails explicitly.
Ordinary nonnumeric inputs retain the primary defaults; enormous integers
fail rather than overflowing into an apparently successful default.

File sending identifies the application identity and uses `rncp.receive` with
the primary UTF-8-byte `name` metadata and compression enabled. Private staging
retains a protected regular-file handle through borrowed-stream transfer and
Link teardown; it does not reopen a DeleteOnClose file by name on Windows.
The complete staging/transfer operation has a five-minute deadline and a
2,000-MiB input bound. Selected peer policy can reject smaller files.

Speed tests use `nomadnetwork.utilities.speedtest`, all12 primary size labels
from2 KiB to2,000 MiB, the1-MiB default, no metadata and no compression.
Authorization compares the entered destination against the genuine identified
peer in constant time. The listener owns one incoming Link/Resource, applies
the primary `max(8 seconds, medium_path_timeout)` identification deadline,
then a five-minute transfer deadline. Sending waits600ms after identification.
Random payload generation and reception are streamed with bounded buffers;
SHA-256 and summed physical segment sizes are separately observed. One terminal
operation can run at a time; cancellation/disposal joins owned work. A successful
receiver leaves Link closure to the proven sender rather than racing the final
proof. The storage ownership marker is retained; payload/staging files are not.

`GenerateNomadNetUtilityVectors.py` executes unchanged primary controller,
authorization, completion, formatting and receipt-stat methods with disclosed
synthetic UI/transport owners. The canonical independently generated
Windows/Linux fixture SHA-256 is
`a071ac4e231274968cc719d545a460a2e6f5bc56270e1360d04c72681445ead4`.
It records primary1.4.4 commit
`f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`,
`Utilities.py` SHA-256
`61a75d64a244d13e543d7c915d1611bbde87cf28db8450def4424a5634c7c1b1`,
`Transport.py` SHA-256
`57bd1e08e5c68b452f2314626ec67d73871ea2468a5af54cc15a5b54f17323e9`,
and the actual pinned RNS path-expiry source revision/hash.
These vectors are not network or primary GUI evidence.

The current focused command
`dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release
--filter "FullyQualifiedName~NomadNetUtility"` passes13/0 on Windows and Linux.
It includes actual TCP owner/identity/path/proof behavior, authorized4-MiB
segmented transfers, both speed origins, named rncp bytes, refusal/recovery,
the measured eight-second unidentified timeout, malformed/bounded input,
no-subscriber processing, throwing observers and cancellation/disposal cleanup.
`NomadNetUtilities.Tests.ps1` separately passes3/0 per host against guarded
NativeAOT candidates: reference regeneration, native owner/cancellation/restart,
and both-origin4-MiB speed/refusal/recovery. Selected fixed-stack candidates are
Windows SHA-256
`72a5466825a29831c3330ddaf31fda4f6f645c7c7dd74416ee551c5f87a1b71a`
and Linux SHA-256
`e4c5db792112ba823dce58c8cdfdbdd4b843478a2060dcd308888d6f53484802`.
These include receipt measurements and the whole-staging deadline, and retain
SDK10.0.403/runtime and NativeAOT compiler10.0.14, dirty base
`f8bac40f5b032c5643cffdeff5b905c98182fe86`, guarded source/generated-version
snapshots and binary receipts are retained under
`artifacts\tests\nomad-utilities\native-windows` and
`/home/dennis/fleet-nomad-utilities/native`.

The actual primary-controller bridge `nomadnet_utility_peer.py` constructs
unchanged utility classes and original Urwid widgets with a disclosed bounded
headless scheduler/minimal application owner, not `NomadNetworkApp` or its GUI.
Its primary-primary Windows/Linux observations cover both speed origins,
refusal/recovery, probes, file sending, cancellation and input bounds.
The retained negative controls reproduce an intermittent stack3cdf
completed-Resource callback lifetime failure: the primary sender completes and
normally tears down, but native `ResourceReceived` can receive an already
disposed Resource before its first synchronous `OpenRead`. The separate
DotReticulum commit `2331aeed1d54b4ead2e83422255d3b8734881780` reserves completed
Resource ownership before external callback admission and defers disposal until
callback release. A synchronously acquired reader owns its subsequent lifetime.
It changes no wire format, Python source or normal sender teardown.

The selected binaries above independently pass T362/T363 **2/0 per host** through
`NomadNetUtilitiesLive.Tests.ps1`: both speed origins, authenticated3/3 probes,
an authorized named1-MiB primary rncp transfer, and sixteen spaced4-MiB pairs in
unchanged primary/native processes over at least600 monotonic seconds.
The endurance case transfers128 MiB of pair payloads, observes continued
responsiveness, exact lengths/SHA-256 and bounded work/logs/storage, and verifies
payload cleanup while alive and after shutdown. The two valid ownership markers
are not payload leaks. Parent replays take614.11s Windows and610.4s Linux for
the two cases, with retained receipts under
`artifacts\tests\nomad-utilities\parent-fixed-stack-live` and
`/home/dennis/fleet-nomad-utilities/parent-fixed-stack-live`.
The original fast T362 five-observation shape is unchanged.

The integrated e52 Windows candidate
`34d1ff11278cf7679e5c973b71697f90008333cbabafb11c09a0e07de03f4110`
also independently passes both unchanged cases, with601.876 measured endurance
seconds, sixteen pairs and128MiB pair payload, exact bytes/digests, no leftover
payloads while alive or after shutdown, and zero exit codes for both processes.
The entire two-case gate takes613.18seconds. Source and binary hashes remain
unchanged through the run. Its separate three native utility process cases also
pass. Replay and receipts:

```powershell
& artifacts\tests\nomad-utilities\Parent-AtomicAcceptance.ps1 -Mode Live
# artifacts\tests\nomad-utilities\parent-atomic-live-windows
```

Enable the usual pinned utility/live variables plus
`DOTRETICULUM_NOMADNET_UTILITIES_ENDURANCE=1` for T363. Windows primary rncp's
open-file unlink `WinError32` is explicitly asserted despite correct saved bytes;
Linux requires zero such errors. The earlier failed hidden-marker harness lookup
is retained separately; the corrected lookup does not mutate production or
reference code. These selected source snapshots predate later clipboard/Linux
printing work and are not whole-application acceptance. The stack object is
local/unpushed; no remote availability is claimed. New transport-hop/impairment,
hours/days-long operation, maximum-size transfers, full Urwid rendering and
physical RF measurements remain unverified.

### Opt-in terminal clipboard (C80)

`clipboard on|off` atomically persists the default-disabled `ClipboardCopy`
policy; `[textui] clipboard_copy` imports the same setting. Only an explicit
`copy message <id>` or `copy url` requests output. Message copying selects
stored content, never its title/attachment labels; URL copying includes ordered
`var_` form fields using the primary's literal backtick/pipe representation.
An absent current URL or empty content emits no sequence. The terminal must be
trusted and implement OSC52; no OS clipboard, shell or Python plugin is invoked.

`NomadNetClipboard` emits the primary `ESC ]52;c;<Base64 UTF8> BEL` framing,
flushes the writer and reports encoding/write/flush failures contextually.
Strict UTF8 and a one-megabyte input-byte bound deliberately reject malformed
or excessive text. Embedded controls remain Base64 data, not terminal commands.
The primary's temporary two-second icon/banner presentation is not reproduced
by the original textual status output.

`GenerateNomadNetWorkflowVectors.py` executes actual `Helpers.osc52_copy`,
`Browser.current_url` and editor confinement/atomic writes at primary
`f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`, with disclosed synthetic owners,
captured stdout/log levels and real private temporary filesystem writes.
Independent Windows/Linux canonical fixture SHA-256:
`f4144ffcee4735f777860d51de3f0d27cceafb8d1598b5932f10691fe7afd8e9`.
Raw source hashes are in the fixture; no primary UI/guide/template asset is copied.
The three clipboard/URL/config unit cases pass on both hosts, including disabled
policy, exact UTF8 bounds, invalid surrogate, hostile embedded controls and
writer/flush failure. T367/T368 pass2/0 on Windows against the independently
verified2331aee printing candidate and again against the integrated e52
NativeAOT candidate above. They regenerate the reference fixture and exercise
default denial, explicit opt-in, exactly one framed content-only copy, unchanged
message state, persisted policy/identity across restart and explicit disabling.
Their stored native message history is disclosed synthetic input, not primary
message reception evidence. Current Linux native acceptance is blocked by WSL
storage failure; its managed process preflight is not relabelled NativeAOT.
Those clipboard-suite editor observations are reference research; the separate
bounded implementation, additional executed observations and native acceptance
are described in the editor/guide section below.

### Original complete bounded local guide workflows (C102)

The local guide has18 independently authored topics, with topic/link selection,
anchors/next-heading, bounded search, logical-line viewports and32-position
back/forward history. All13 actual pinned primary topic bodies have factual
counterparts; upstream lists only12, omitting Conversations from its topic list.
No upstream guide prose, demos, display assets, fonts, templates or UI source
are copied. Original instructions cover setup, trust, conversations/media/paper,
RRC, authoring/Micron, hosting/access, configuration/restart, propagation,
browsing, network interfaces, presentation and recovery.

Use `guide topics`, `guide select <number>`, `guide <topic>`, `guide links`,
`guide open <number>`, `guide <topic>#<anchor>`, `guide next`, `guide back/forward`,
`guide view [1-100]`, `guide scroll <-10000..10000>` and `guide find <words>`.
Targets are limited to256 characters; searches to1–128 characters/64 results.
Links stay local and never execute instructions or fetch from the network.
Browser history and private editor buffers keep separate ownership.
First client-state creation still shows `start`; `hide_guide` changes only
menu visibility. Unknown anchors fail explicitly rather than silently doing
nothing. History is session-local, not an upstream persistence claim.

Actual pinned `GuideDisplay`, `GuideEntry`, `TopicList`, `ListEntry`,
`GuideLinkDelegate` and `MicronParser` execute in the reference generator.
Exact Git-blob hashes and imports are recorded. The synthetic singleton,
screen/palette and immediate alarm callbacks are disclosed seams. Image
resolution returns `None`, exercising the unresolved-image placeholder rather
than decoding/copying primary demo assets. Browser handoffs are observed, not
fetched. This is not unmodified-primary fullscreen or image-demo acceptance.
The existing primary RNS/LXMF dependency-minimum discrepancy remains.

Parent-independent frozen Windows acceptance passes **13 focused cases,
493 full Nomad cases/33 explicit skips and4 Pester cases**, without failures.
Strict builds/publish have zero warnings/errors with SDK10.0.403 and
ILCompiler10.0.14 at versioning HEAD `10e890f08b9d7b6111309c7786e3b19c4f461998`
and stack `4989f050aed865d16ed9f1460071c1a63cd4c7e9`.
Native SHA-256:
`00b16603d1969db1d292502e8446c7a8da00c335a0872e05a910ef755afc69d0`.
Help/version, actual ConPTY selection/navigation/errors/hidden menu/Ctrl+C,
ordinary all-topic/search/history and guide-driven create/preview/save/access/
handler registration survive identity-preserving restart. Exact-primary
observations independently regenerate byte-identically.

```powershell
pwsh -NoProfile -File tests\Pester\Invoke-NomadNetGuideAcceptance.ps1 -Candidate artifacts\tests\nomad-guide\parent-final\candidate.json -OutputDirectory artifacts\tests\nomad-guide\parent-final\independent
```

The receipt guards354 scoped production/toolchain inputs,113 acceptance inputs,
44 managed artifacts, exact native/SDK/HEAD/stack and clean primary references,
with unchanged before/after hashes. Original viewports use logical lines,
not wrapped terminal/Unicode columns. Full terminal keyboard/mouse/focus,
editor interaction and unexercised terminal/Unicode-column contracts remain distinct,
not physical-hardware deferrals. No new wire topology, physical display/RF/audio/
printing or current Linux replay is claimed; WSL read-only/EIO remains blocked.

### Bounded original document/configuration editor and guide

Current folder-candidate validation and fresh cross-slice regressions are recorded
under **Bounded folder management** below. Earlier explicitly named
authoring/external-editor candidate receipts retain their original source/binary
boundaries; they are not relabelled as the current tree.

The original console has a self-contained, built-in authoring
workflow. It edits the same configured roots used by `NomadNetContent`, defaulting
to `<profile>/storage/pages` and `<profile>/storage/files`; it does not invent a
second hosted-content location. Existing host rescan/`node refresh` picks up
saved, renamed and deleted documents. `config edit [native|rns]` selects native
`settings.json` or the actual RNS configuration file resolved at application startup.
The native JSON format is not primary NomadNet ConfigObj.
`guide` provides original local documentation and samples, not upstream guide,
image, font, template or snippet assets.

The factual reference is primary **1.4.4**
`f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`. The inspected raw source hashes are:

| Source | SHA-256 |
|---|---|
| `nomadnet/ui/textui/Editor.py` | `ae6c09cde38d473fd97d1758ebcbc13edaf5d7e1f97dc4e698a870c68cd8d378` |
| `nomadnet/ui/textui/Config.py` | `13a0a531ccc2fc207dc6b4c7f644c3eff6d261bb34edf84781c1c6f76f4b0421` |
| `nomadnet/ui/textui/Guide.py` | `cff869dfd8834fa55ccc25e1ddbece06a61d72329589e77c62236f72220bd2c5` |
| `nomadnet/util.py` | `77a4368c73b8a84ef2e9df41b4e423bcb1d0677e12ed18debe445996fa3a9e38` |

`GenerateNomadNetEditorVectors.py` executes actual `PageEditorDisplay.find_next`,
`replace_one`, `replace_all` and `_insert_snippet` with an actual `urwid.Edit`
buffer and synthetic owner/text, and actual `write_config_atomic` plus ConfigObj
reload on owned storage. It imports the verified actual primary checkout and
records actual module paths. It does not render primary UI, replace a reference
method, or copy its guide/snippet content. The existing
`GenerateNomadNetWorkflowVectors.py` separately executes `_confine` and
`_atomic_write`; lexical primary containment is not relabelled symlink protection.

| Primary editor/config/guide contract and source | Native status and exact boundary |
|---|---|
| `Editor._roots`, `_confine`, `build_entries`, `walk_dir`, `_dir_has_mu`, `_do_new` | **implemented bounded original controls / safety difference**: explicit pages/files scope, nested creation and expanded directory tree; case-sensitive `.mu` filtering prunes directories without matching visible descendants. Relative paths up to512 characters; up to2048 displayed entries,4096 inspected entries,16 levels and256 expanded folders. Refuses hidden/traversal/device/alternate-stream paths and observed symlink/reparse ancestors, including dangling links. Permission companions remain hidden rather than appearing as editable documents. Primary confinement is lexical and can cross between configured roots; native requires explicit scope rather than arbitrary absolute paths. C97 below records functional presentation, not Urwid tree-widget parity. |
| `load_file`, `save_file`, `_atomic_write`, `_do_saveas`, `confirm_discard` | **implemented bounded subset / safety difference**: one-megabyte strict UTF8 buffer, invalid-UTF8 read-only, save/discard/cancel, save-as, no silent overwrite, explicit dirty-open/quit refusal and nonzero dirty EOF. Random private same-directory `CreateNew` staging, flushed file and atomic replacement; save errors/cancellation retain the buffer and previous document. Optimistic snapshots reject ordinary external edits, and a shared in-process mutation gate serializes editor commits. Save-as never overwrites and refuses restricted source copies rather than accidentally publishing them. Existing Unix mode is preserved in code; current Linux execution is **unverified**. Directory fsync, hostile administrator path races and crash-atomic multi-file transactions are **not claimed**. |
| `_do_rename`, `_do_delete`, `_write_allowed`, `_apply_permissions` | **implemented bounded file/folder management and recursive permissions / safety difference**: confirmed deletion, nonoverwriting rename/move and per-file `.allowed` companion handling. Folder create/rename/cross-root move/recursive removal use a bounded inspected tree and exact one-use token, preserve descendant permission files, and rebase/clear live and persisted selections; details below. Per-file and recursive restrictions accept up to1024 validated distinct identities. Recursive permission summaries, confirmed replacement and make-public conversion are implemented with staged policy replacement/rollback; all recursive changes require explicit confirmation. Overwrite confirmation for moves is deliberately replaced by refusal. Committed cleanup failures are surfaced with retained transactions, never falsely reported as rollback. |
| `find_next`, `replace_one`, `replace_all`, `_insert_snippet` | **implemented bounded subset**: case-sensitive ordinal search with wrapping, selected-match replacement/next-match behavior, nonoverlapping replace-all and insertion. ASCII observations match executed primary operations. Native cursor positions are UTF-16 offsets rather than Python Unicode-codepoint offsets, an explicit **difference**. Five original samples are usable; the primary's18 snippet assets are not copied or claimed. |
| `on_edit_change`, `render_preview`, `is_executable`, `highlight_micron`, `MicronEdit`, `GutterEdit`, `visible_panes`, `toggle_pane`, `rebuild_columns`, `Browser.PageList` | **implemented bounded original functional controls / safety difference**: owned400ms latest-edit debounce, original source syntax colours and wrap-aware line numbers, three independently visible panes with primary22:40:38 weights and last-pane protection. Explicit and automatic textual Micron preview reuse the existing renderer, with no network/partial/image retrieval or executable-source execution. Executable files retain source/warning behavior; binary files do not preview. The existing100000 UTF-16-character preview cap remains intentionally at least as restrictive as Python's Unicode-codepoint cap. Primary syntax stops above50000 Unicode codepoints. `edit view` prints the current bounded split-pane snapshot; C99 below adds bounded independent vertical source/preview positions and scrolling commands. This is not automatic full-screen Urwid redraw; exact keyboard/mouse/cursor/terminal-width parity remains **missing/unverified**. |
| `_state_file`, `load_state`, `save_state`, `start` | **implemented bounded original controls / local-format difference**: extended schema1 `editor_state.json` stores relative selection, `.mu` filter, expanded folders and visible panes, bounded to32 KiB. Editor activation reopens an existing saved file; `edit resume` remains explicit. Old native schema1 selections still load. Folder moves/removal rebase/clear live and persisted expansions/selections. No document text, secrets or unsaved drafts are persisted. Malformed/oversized state is reported rather than silently ignored. Bounded primary state/document import is independently accepted under C100 below; external/custom roots are not rebound. |
| `ConfigSectionView.on_save`, reset/validation and `util.write_config_atomic` | **implemented bounded functional subset / safety difference**: native JSON/raw selected-RNS editing plus all71 non-printing primary form fields across seven sections (printing omitted, C78), explicitly opted-in primary ConfigObj source, confirmed staged reset/save and cancel. Exact field/runtime/grammar dispositions and reference evidence appear below. Atomic saves require restart and serialize with runtime settings writes; later runtime-setting edits are refused and automatic announce/sync timestamps stay in memory, preventing stale restart-configuration overwrite. Active service options/identity are not hot-reloaded. TextUI/utility controls have bounded functional effects under C95; complete ConfigObj/Urwid parity remains a gap. Panic failure ownership is an intentional safety difference, not a missing implementation. |
| `Config.EditorTerminal`, `[textui] editor` | **implemented bounded original workflow / safety difference**: imported/native `editor`, default `nano`, is one executable argv element, not a shell command. `config external [native|rns]` uses the public `RnshProcess` PTY/ConPTY API, a private working copy, validation and snapshot-checked atomic commit, then returns to the console. Explicit Ctrl+] cancellation, child failure and conflict refuse commit. Redirected input/output is explicitly unsupported; interactive commands and child input have one owner rather than mixing `Console.In` with raw stdin. C95 uses Win32 console records for Windows command/mouse input, restores the exact input mode before child activation, and retains `Console.ReadKey` child forwarding. No embedded Urwid/widget, complete keyboard/mouse/termios or cross-platform terminal parity is claimed. Details and actual Windows terminal evidence follow below. |
| `GuideEntry.display_topic`, `GuideLinkDelegate.handle_link`, topic reader | **implemented bounded original workflow / safety difference**:18 original topics cover all13 primary factual areas; topic/link selection, anchors/next-heading, search, logical-line viewports and32-position back/forward use the existing Micron renderer. C102 has separate frozen Windows NativeAOT ordinary/ConPTY/restart acceptance above. C95 first-state/start and menu-only `hide_guide` behavior remain. Links stay local; unknown anchors fail explicitly; history is session-only. Original assets replace primary demos; complete terminal keyboard/mouse/focus/Unicode-column behavior remains **missing/unverified**, not a new wire or current Linux claim. |

### Original editor presentation (C97)

The factual reference remains primary1.4.4
`f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`.
`GenerateNomadNetEditorPresentationVectors.py` executes actual
`PageEditorDisplay`, `Browser`, syntax functions and Urwid editor/gutter widgets
against synthetic local files. Its synthetic application owner supplies a
headless screen/palette sink and a recording alarm owner; actual preview alarm
callbacks are invoked explicitly. It observes scheduling/cancellation arguments,
but the missing primary application singleton caused preview rendering to
produce an error panel. Successful synthetic Micron rendering is separate C99
evidence below, using the disclosed singleton/palette setup. C97 does not prove
primary document rendering, wall-clock timing, a full application constructor, a physical
terminal or network interoperability. Actual imported paths and imported-file
hashes are retained separately. Canonical source hashes use `git show`, so
normal Windows checkout line endings do not become a different upstream pin.

| Canonical primary source | SHA-256 |
|---|---|
| `nomadnet/ui/textui/Editor.py` | `ae6c09cde38d473fd97d1758ebcbc13edaf5d7e1f97dc4e698a870c68cd8d378` |
| `nomadnet/ui/textui/Browser.py` | `cbce04302ec092ae6bb3cc6ce9f423139b1339b9e903f14f12cf8e6ebe736729` |
| `nomadnet/ui/textui/ReadlineEdit.py` | `3538c109151528b8adcdda217bea4890d035461ae59b29bb49c455cfc47cc7d6` |

Primary source contracts are `Editor.py:79–213` (syntax,50000-codepoint
highlight cutoff, wrap-aware gutter), `435–549` (construction, activation and
state), `617–714` (fixed pane weights/visibility, tree, filter and expansion)
and `1105–1155` (dirty state,400ms replacement alarm,100000-codepoint preview
cutoff and preview scroll preservation). Syntax and gutter are always present
upstream; there are no invented persisted syntax/gutter/weight configuration
options. Primary's preview-link external-navigation TODO is not an implemented
remote preview feature: only local anchors are acted on there.

Original native commands:

```text
edit help
edit view 120 20
edit tree
edit expand pages chapter
edit collapse pages chapter
edit filter on
edit pane files
edit pane editor
edit pane preview
edit focus next
edit focus previous
edit preview-status
```

Activation occurs on the first `edit` command, not application startup.
The source buffer, search/replace, mutations, permissions, external editor and
explicit `edit preview` remain the existing workflows. Text edits schedule a
latest-only preview without any observer registered; `edit view` consumes the
current snapshot without initiating retrieval or execution. There is one owned
worker and one pending snapshot, no background console writer. Changing
selection invalidates obsolete render results; close and session disposal
cannot publish an old document. Disposal cancels and joins the worker, including
through the existing synchronous session owner; an asynchronous disposal API is
also available. Individual observer/render failures are reported through the
configured logging callback or diagnostic trace, and subsequent edits continue.
Attempting to synchronously dispose from inside the worker's own callback is
reported rather than self-deadlocking.

`view` accepts40–240 columns and1–60 rows. The header reports nominal primary
weights and actual allocated columns; visible weights are normalized after
hiding a pane, preserving at least one pane. Original syntax colours respect
the existing terminal colour-depth/monochrome settings, not copied palettes.
ASCII wrap/gutter and lexical observations match the executed cases; UTF-16
cursor offsets, wide/combining-character terminal columns and exact Urwid
line-breaking/keyboard/mouse behavior are not claimed. Bounded source/preview
scrolling and session position retention are implemented under C99 below.
Views replace terminal controls/formatting characters with inert placeholders.
The native preview's existing100000 UTF-16-character safety cap is preserved;
the Python cap counts Unicode codepoints. Windows follows primary behavior in
not treating hosted files as Unix executables; current Unix executable-mode
replay is unverified.

Extended native schema1 persists only relative selection/filter/expansion/pane
controls. Old native schema1 files are supported; primary's state JSON is not
silently treated as migrated state. Saves are atomic and bounded, errors retain
the previous controls, and unsaved content is never persisted. Tree traversal
and descendant filtering share the entry/depth bounds, refuse unsafe paths
and ignore reparse entries rather than following them. Existing folder
move/removal transactions now rebase/clear expanded paths alongside selections.
Oversized trees fail visibly; later smaller-tree operations remain usable.

Frozen Windows evidence uses SDK10.0.403/runtime and NativeAOT10.0.14,
stack`4989f050aed865d16ed9f1460071c1a63cd4c7e9`, root acceptance base
`abfe5b121ceb3b1890f18239eb5d5dc3228faedb`, and native SHA-256
`a6296e669f605f839a321169055fe6b9172754035b50751c531b379890505062`.

- `dotnet build tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --no-restore -warnaserror --artifacts-path artifacts\tests\nomad-editor-presentation\managed-build --nologo -v:q`: **passed**, zero warnings/errors. The first isolated build correctly refused missing assets; a selected-project restore/build established the isolated assets without changing dependencies.
- `dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --no-build --no-restore -warnaserror --artifacts-path artifacts\tests\nomad-editor-presentation\managed-build --filter "FullyQualifiedName~NomadNetEditorPresentation" --logger "trx;LogFileName=focused.trx" --results-directory artifacts\tests\nomad-editor-presentation\focused-isolated -v:q`: **13 passed,3 explicit native opt-in skips**, zero failures. Manual-clock tests establish399ms refusal and400ms latest-only publication, no subscribers, observer/logger/render failures, selection/disposal races and continued service; storage tests cover malformed state/bounds, failed persistence, old-state compatibility, tree/filter/panes, folder moves/removal and clean restart.
- `dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64 -o artifacts\tests\nomad-editor-presentation\native -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror -v minimal`, with the selected MSVC environment and `vswhere` available: **passed**, no warnings/errors. Published `nomadnet.exe --help` **passed**.
- `pwsh -NoProfile -File artifacts\tests\nomad-editor-presentation\Replay-Candidate.ps1 -OutputDirectory artifacts\tests\nomad-editor-presentation\acceptance-isolated-third`: fresh-child **408 passed/15 explicit skips/zero failures** for the full Nomad suite, including **3 new actual ConPTY**, **3 existing terminal ConPTY** and **2 actual installed Vim** regressions; new scoped Pester **3 passed/zero failed/zero skipped**. Managed tests use the isolated artifacts path above. Repository-configured PowerShell analysis reports **zero diagnostics**. Source/dependency/file-set/native/managed/reference/editor guards hold before and after the replay.

Pester separately regenerates byte-identical actual-primary vectors, including
the callback/error-panel boundary above, and runs published native command
persistence/reopen and dirty-EOF/error/recovery cases. C99's successful shimmed
primary render must not be retroactively attributed to C97.
New ConPTY windows exercise syntax/gutter and split panes, latest automatic
preview, filtered expansion, selected-file/identity retention through clean
restart and pending-preview close/failure recovery. The initial Pester attempt
incorrectly assumed the piped native profile had an `identity` file; it failed
visibly. The corrected assertion compares the actual public delivery address
across restart, without reading private state or weakening identity retention.
This did not change production code or the native binary.
Expanded executed-primary syntax cases exposed Unicode-tag/newline edges;
the independently authored lexer was corrected before the final native publish.
Earlier isolated receipts retain two failures from an existing Vim test's
PID-file sharing race and one interleaved-menu source-text assertion. The
new assertion now removes only the known menu redraw while retaining the raw
ConPTY transcript; the existing Vim test is unchanged. The final fresh replay
passes both Vim regressions, but does not claim that their PID-file race is fixed.

Receipts, exact commands, frozen manifest, selector handoff and reusable
fresh-child replay are under ignored
`artifacts\tests\nomad-editor-presentation`. These are Windows local
presentation/primary-method observations, **not new wire interoperability**,
full primary UI/guide asset parity, physical-terminal/audio/hardware or current
Linux acceptance. No WSL repair was attempted. Parent-independent guarded replay
also passes408 managed cases/15 explicit skips and3/0/0 Pester, including
the actual ConPTY/Vim cases, under `parent-independent`:

```powershell
pwsh -NoProfile -File artifacts\tests\nomad-editor-presentation\Replay-Candidate.ps1 -OutputDirectory artifacts\tests\nomad-editor-presentation\parent-independent
```

### Original editor vertical scrolling (C99)

The original snapshot editor now supports:

```text
edit view 120 6
edit scroll editor page-down
edit scroll preview 17
edit scroll preview page-up
edit scroll editor end
edit scroll preview home
edit focus next
edit scroll focused down
```

`edit scroll <editor|preview|focused> <up|down|page-up|page-down|home|end|signed-Int32-delta>`
prints the resulting view with the **last successful view dimensions** (default
120 columns/20 rows). Page movement uses `max(1, height-1)`, preserving one
overlapping row where possible. Top positions clamp to
`[0, max(0, contentRows-height)]`; overflow-sized/malformed deltas, extra operands,
files focus and hidden text panes fail visibly without disabling the service.
Focus routing uses the existing pane controls, not new raw-arrow/mouse input.
There is no background terminal writer or full-screen/cursor/Urwid claim.

Source positions count wrapped original source rows with logical line-number
gutters. Preview positions count textual rendered lines; long preview lines
remain horizontally clipped, not silently rewrapped. Only the requested1–60
rows are retained for rendering, at the existing40–240-column view bound;
offscreen source rows are traversed without retaining the entire visual layout.
Unicode surrogate pairs are never split; tabs/control/format characters remain
inert placeholders. These are bounded UTF-16 snapshot widths, **not** verified
grapheme, wide/combining-character, tab-stop or physical-terminal column widths.
Full logical text, one-megabyte UTF8 edit/save bounds and preview cap remain
unchanged. Scrolling never edits the buffer, moves its search/edit cursor or
changes saved/exported source.

Independent source/preview positions survive focus/pane toggles, ordinary
refresh, save/save-as and debounced latest-preview replacement. Shorter content
or larger visible heights clamp positions when that pane is next rendered;
pending previews retain their current content and position with pending status
outside the scrolled content. Successful open/resume and close reset both
positions. Positions are deliberately **session-only**: native restart reopens
the saved selection at zero, like the primary's state schema, which has no
scroll-position fields. No unsaved text or viewport offsets are persisted.
Pending-preview cancellation and last-pane protection remain active.

`GenerateNomadNetEditorScrollingVectors.py` executes the actual primary1.4.4
`f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2` `PageList` and
`PageEditorDisplay`, with synthetic local text, an unstarted real Urwid screen
for palette registration, a fixed-size screen/alarm owner and a **disclosed
application-singleton shim** for the parser's theme/palette access. It does not
start an unmodified primary application, GUI, OS terminal or network node.
Canonical source SHA256:

- `nomadnet/ui/textui/Editor.py`: `ae6c09cde38d473fd97d1758ebcbc13edaf5d7e1f97dc4e698a870c68cd8d378`
- `nomadnet/ui/textui/Browser.py`: `cbce04302ec092ae6bb3cc6ce9f423139b1339b9e903f14f12cf8e6ebe736729`
- `nomadnet/vendor/Scrollable.py`: `afa1da21a9d330c718394bcc9cd2ebe1449436fdb2e04d11b7be7ea50d65c6a4`

Executed primary page movements/clamps match the selected native viewport
cases. Primary editor preview renders159 synthetic rows and retains position17
through hiding/showing the pane. Its replacement preview returns to zero:
`render_preview` attempts restoration before the new `PageList` has a nonzero
viewport size. Native retention across replacement is an intentional original
usability improvement, **not** exact behavioral parity with this primary edge.
The earlier C97 probe invoked real render callbacks but lacked the singleton,
so those callback observations included the primary's error panel rather than
a successfully rendered synthetic Micron document; C99 separately establishes
successful primary document rendering with the explicit shim.

Selected Windows NativeAOT SHA256:
`1d6d99096f45b20e920371a0e1b66ca0394f5f78fd8152093c6a996af1aa5548`,
SDK10.0.403/runtime and ILC10.0.14; stack unchanged
`4989f050aed865d16ed9f1460071c1a63cd4c7e9`.

- `dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --artifacts-path artifacts\builds\nomad-editor-scrolling -warnaserror --filter FullyQualifiedName~EditorScrolling`: **18 passed/zero failed/zero skipped** with `DOTRETICULUM_NOMADNET_EDITOR_SCROLLING=1` and the selected published binary, including3 actual ConPTY flows.
- `dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64 --artifacts-path artifacts\builds\nomad-editor-scrolling -o artifacts\tests\nomad-editor-scrolling\native -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror -v minimal`, in the selected MSVC18 environment with `vswhere` available: **passed**, zero warnings/errors; published `--help` **passed**.
- Full isolated Nomad suite, `dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --no-build --no-restore --artifacts-path artifacts\builds\nomad-editor-scrolling -warnaserror`, with selected C99/C97/C95 ConPTY and installed Vim modes: **426 passed/15 explicit skips/zero failures**.
- `pwsh -NoProfile -File artifacts\tests\nomad-editor-scrolling\Replay-Candidate.ps1 -OutputDirectory <fresh-owned-directory>`: independent guarded fresh-child no-build replay **426 passed/15 explicit skips/zero failures**, Pester **3 passed/zero failed/zero skipped** and repository-configured PowerShell analysis **zero diagnostics**. Source/file-set/managed/native/reference/editor fingerprints hold before and after the replay.

Frozen manifest, fresh-child no-build replay, command receipts, primary imports,
initial failing receipts and selector/ownership handoff live under ignored
`artifacts\tests\nomad-editor-scrolling`. New ConPTY flows exercise independent
positions across edit/debounce/pane/save/restart, malformed and hidden-pane
commands, cancellation, healthy service and large Unicode/tab source tails with
exact full-source save verification. The first native attempt failed because
ConPTY emits a cursor-forward escape instead of a literal space after the
prompt once output reaches the bottom row; the new operator detects the prompt
token without requiring its trailing space and retains raw transcripts.
The first frozen replay also retains an existing C97 manual-clock scheduling
timeout and the previously recorded existing Vim PID-file sharing race.
An unchanged-input fresh replay passes both without retries inside tests,
weakened assertions or claiming those pre-existing races are fixed.

Current Linux replay remains blocked by the reported WSL EIO/read-only state;
no repair/remount/shutdown was attempted, and older Linux receipts do not accept
this binary. macOS, physical terminals and primary GUI remain unverified.
Bounded primary editor-state migration is accepted under C100 below.
Remaining required software includes full
keyboard/mouse/cursor/terminal-column and configuration-grammar contracts;
the bounded original guide is independently accepted under C102 above;
this is a bounded vertical-scrolling slice, **not full editor/Urwid parity**.

Example, using JSON strings for text without invoking a shell or executable page:

```text
guide authoring
edit mkdir pages
edit new pages welcome.mu
edit text ">A local notice\nSynthetic example text.\n"
edit preview
edit save
node refresh
edit permissions ["0123456789abcdef0123456789abcdef"]
edit close discard
config edit
config validate
edit close cancel
```

Use `edit close save|discard|cancel` explicitly; `edit resume` selects the last
saved path, not a crash-recovery draft. `guide topics`, `guide links`,
`guide open <number>`, `guide hosting#access` and `guide back` are local reader
operations. See `--help` and `guide configuration` for search/replace, original
snippets, save-as, rename, delete and restart semantics.

Focused source-executed and filesystem tests (T390–T394,T398) cover buffer bounds,
malformed UTF16, original guide links/anchors, binary refusal, default hosting-root
response, unchanged running settings, memory-only timestamp persistence after
restart-config editing, conflicts, concurrent editors, cancellation, restart,
permissions and Windows second-move rollback. The complete scoped command:

```powershell
dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --no-restore --logger "trx;LogFileName=nomad-external-regression.trx" --results-directory artifacts\tests\nomad-external-editor\managed-final -v quiet
```

passes **327 tests,17 explicit skips,zero failures** on Windows. The Linux
symlink/executable/mode case (T398) is explicitly skipped there; WSL EIO/ext4
read-only storage prevents current Linux replay, and no WSL repair was attempted.

Strict Windows publish:

```powershell
$env:PATH = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer;' + $env:PATH
dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64 -o artifacts\tests\nomad-external-editor\native -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -v minimal
```

passes warning-free with SDK**10.0.403** and compiler**10.0.14**, stack
`e52da394658f6f6c723214a9f811552418789cb6`.
The exact native SHA-256 is
`90429dad43a841bdb66b3d79b1e7f476aa63558be73ca12ab9458472fb00cdf3`.
With `DOTRETICULUM_NOMADNET_EDITOR=1`, that selected
`DOTRETICULUM_NOMADNET_BINARY`, and
`DOTRETICULUM_NOMADNET_EDITOR_REFERENCE=1` plus the pinned Python/RNS/LXMF/
primary paths, run:

```powershell
pwsh -NoProfile -File tests\run-pester.ps1 -Path tests\Pester\NomadNetEditor.Tests.ps1
```

T395/T396/T397/T399 pass **4/4,zero skips/failures**, including native `--help`,
actual console authoring/Micron preview/host-handler refresh, per-file permissions,
cross-root rename/delete, local guide navigation, identity-preserving restart,
binary/invalid-input/dirty-EOF refusal, nonoverwrite and Windows junction
confinement, invalid-config rollback, protected restart-config save and a
byte-identical independent primary regeneration. Before/after source and executable
hashes are recorded under ignored `artifacts\tests\nomad-external-editor`, along with
TRX, toolchain/publish log and owned per-process stdout/stderr. These are local
software/CLI flows, not new Python-peer messaging, transport-hop/loss,
physical-terminal UI, external editor, audio or hardware acceptance. Root
integration/full-native validation remains a separate parent gate.

### Configured external editor and selected RNS configuration

The bounded editor also supports:

```text
config edit native
config edit rns
config show rns
config validate
edit save
config external native
config external rns
```

`native` defaults to native `settings.json`; `rns` always selects the actual
`File` returned by `RnsdApp.LoadConfiguration` at startup, including an explicit
`--rnsconfig` override. It never silently edits a user's default/global RNS
configuration instead. The RNS buffer is strict UTF8 and bounded to one megabyte;
native JSON retains its64-KiB bound. RNS saves validate through
`RnsdConfigParser.Parse` and preserve the edited text exactly, including comments
and untouched interface/unknown text. This parser is not complete ConfigObj
grammar or a new implementation of unsupported stack options. `LoadConfiguration`
is not used for validation because it can create files.

The factual primary contracts are `Config.py:1238–1265` (`EditorTerminal`),
`:876–882` (selected configuration path), `:1002–1044` (validation/save/restart),
`NomadNetworkApp.py:959–960,1440–1441` (`nano` default), and `util.py:6–34`
(atomic save). The source hashes above remain unchanged;
`NomadNetworkApp.py` is
`c36ce4c537fe5af534910d1f5200d2a2ea5dd50f74bf9e4157222eff15614eb2`.
`GenerateNomadNetExternalEditorVectors.py` executes the actual constructor,
closed callback, path resolution and section save with actual ConfigObj/Urwid
checkbox and owned synthetic files. **Windows Urwid lacks `Terminal`: the
generator explicitly supplies a captured terminal-construction boundary.**
It records executable/path argv and close-navigation observations, not an
unmodified primary Windows PTY run. No guide or other primary assets are copied.

The imported `[textui] editor` maps to native `editor`, default `nano`. It is one
executable argv element (spaces in an executable path are allowed), followed by
the working-copy path; strings such as `editor --option` are not shell-split.
The primary's literal `editor`→`nano` Darwin substitution exists in code and
reference observations, not current macOS execution evidence. Changing the
configured executable requires restart, like other saved service configuration.

Unlike the primary's direct native-config launch, the original workflow can
explicitly select RNS and edits a random working-copy directory beneath the
leased profile. Child success validates and commits through the existing
snapshot-checked atomic writer. Invalid input, nonzero exit, Ctrl+] cancellation
or an ordinary external edit refuses commit and discards the working copy; a
concurrent writer's contents are never rolled back. No-change exit does not
rewrite the file or mark restart pending. Native/RNS saves use the existing
restart-configuration gate; runtime settings edits are then refused and automatic
announce/sync timestamps stay memory-only. Neither the active identity/options
nor another process's shared RNS instance is hot-reloaded. Restart its actual
owner separately when changing a shared instance.

The application reuses public `RnshProcess`, including Windows ConPTY job-scoped
descendant ownership, rather than adding stack APIs. Interactive command input
and editor keystrokes share a single polled `Console.ReadKey` owner; no
`Console.In` buffering/raw-stdin handoff occurs. Redirected input/output explicitly
refuses external editing, while built-in editor commands still support pipes.
Output pumps are bounded-buffer streams, resize follows the console, cancellation
is explicit, and child/pump teardown is awaited. Basic command-line editing is
original, not full cooked-console, Urwid, mouse or all terminal-key/termios parity.

Working-copy cleanup is nonrecursive, refuses directory traversal and is bounded
to4096 entries; unexpected directories/excess entries surface a manual-cleanup
error rather than following them. Unix private creation modes are implemented;
Windows inherits the selected profile's ACL. There is no sandbox against a
malicious configured executable/administrator, crash-cleanup guarantee,
directory-fsync guarantee or forensic-erasure claim. Byte snapshots and staging
buffers are zeroed when released; managed strings are not securely erasable.

Retained Windows verification for the external-editor source boundary (current
folder-candidate verification follows):

- `dotnet build src\NomadNet\NomadNet.csproj -c Release --no-restore -v quiet`:
  zero warnings/errors.
- `dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --no-restore --logger "trx;LogFileName=nomad-external-regression.trx" --results-directory artifacts\tests\nomad-external-editor\managed-final -v quiet`:
  **327 passed,17 explicit skips,zero failures**. Two new native-only facts
  explicitly skip without their opt-in; the existing Linux-only skip remains.
- `dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64 -o artifacts\tests\nomad-external-editor\native -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -v minimal`:
  warning-free, SDK10.0.403 / ILCompiler10.0.14; native `--help` executed.
- Exact candidate SHA-256:
  `90429dad43a841bdb66b3d79b1e7f476aa63558be73ca12ab9458472fb00cdf3`.
- `pwsh -NoProfile -File artifacts\tests\nomad-external-editor\Invoke-SelectedNative.ps1`:
  **8 Pester passes,zero skips/failures**, comprising the previous four authoring
  cases plus four new cases in `tests\Pester\NomadNetExternalEditor.Tests.ps1`.
  The new terminal case executes both actual native ConPTY facts: **2/2**.

The installed real Git-for-Windows Vim9.1 (patches1–1914, MSYS2 build2025-11-14)
SHA-256 is
`dc541b4416858f5dbc781a0f0976219b63a9c46a67374edfce628549d131fa99`.
Actual nested ConPTY input edits/saves native JSON and selected RNS text,
returns to console, exercises protected runtime-setting refusal and continued
announcements/status, and preserves identity/configuration across restarts.
Separate actual Vim sessions cover invalid-save rollback, nonzero `:cq`,
external-writer conflict, Ctrl+] cancellation and termination of an owned
PowerShell sleeping descendant. The descendant test explicitly selects Vim's
installed MSYS `/usr/bin/sh`; no user's shell/editor profile is changed.
The other cases verify redirected refusal/recovery, internal selected-RNS exact
text/restart, and byte-identical actual-primary vector regeneration.

Ignored evidence:
`artifacts\tests\nomad-external-editor\acceptance-e13166efe2c04968a1fe46e778b3d7b2`
contains native process profiles/transcripts, actual-ConPTY TRX, oracle import
observations and matching before/after production/native guards
(`bdc0f6f03b571dec47b42cd05da8e0be57c0b36497ebc4a71b8d358960672cf9`).
`candidate.json` records exact production/test/compiler/editor hashes, and the
ignored replay operator selects those exact artifacts with fresh evidence.
Current Linux execution remains blocked by WSL EIO/read-only storage.
This proves bounded software terminal workflows, not a physical display/input
device, Python-peer network exchange or complete primary editor/configuration
inventory. Bounded section/reset forms and editor state/tree/pane controls are
implemented in their sections above; bounded scrolling is implemented under C99.
The original bounded guide is separately accepted under C102 and functional
Micron under C104; unexercised terminal behavior is not covered by this receipt.
Selected-RNS extended grammar remains a concrete stack boundary described below.

### Bounded folder management

The original editor supports the primary's required create/rename/move/remove
folder operations, with explicit bounded safety differences rather than a full
Urwid file-tree implementation. These are local authoring operations, not a
new Reticulum protocol.

Pinned primary **1.4.4** `f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`,
`nomadnet/ui/textui/Editor.py` SHA-256
`ae6c09cde38d473fd97d1758ebcbc13edaf5d7e1f97dc4e698a870c68cd8d378`:

| Exact primary contract | Native implementation and boundary |
|---|---|
| `_do_new`,819: trailing separator creates nested folders with `exist_ok=True` | `edit mkdir <pages|files> [relative]`, idempotent for directories, never overwrites a file; shared mutation gate, cancellation checks, bounded missing-ancestor creation and empty-directory rollback on failure. |
| `rename_dialog`,843; `_do_rename`,851: root guard, confinement, relative or absolute destination, parent creation, `os.rename`, optional overwrite confirmation, descendant current-path/tree/state rebasing | `edit folder inspect` captures the selected tree; `move` and `rename` are aliases for one confined, nonoverwriting directory rename, including explicit cross-root destinations. There is no invented upstream copy operation. Destination parent must already exist, and cross-volume rename errors do not fall back to copying. Affected dirty/stale buffers refuse the operation; live and closed persisted selections rebase. Case-only/same-path moves are refused. |
| `delete_dialog`,886; `_do_delete`,896: root guard, explicit confirmation, recursive removal, descendant editor/tree/state clearing | `edit folder remove TOKEN confirm` requires an exact current one-use inspection token and confirmation. Roots/ancestors of configured roots cannot move/remove. Clean affected live selections and saved resume selections clear after committed removal. |
| `_walk_pages`,927; `_folder_permissions`,935; `_save_permissions`,975; `_apply_permissions`,990 | **Implemented bounded recursive summaries/application/make-public confirmation, with safety differences** described below. Primary skips hidden directories and `__pycache__`, but includes hidden filenames; native refuses hidden entries and still counts skipped cache entries against safety limits. `.allowed` files are not treated as content; orphan and skipped-cache policies remain unchanged. |

`GenerateNomadNetFolderVectors.py` executes actual `_do_new`, `_do_rename`,
`_do_delete`, root UI guards, traversal/permission summary and atomic writes with
synthetic owned trees and actual `urwid.Edit`. UI/state callbacks are observers;
overwrite confirmation is observed and cancelled rather than rendered.
No primary method is replaced, and no assets are copied. The fixture includes
actual written file bytes/hashes; Windows primary text-write newline translation
is therefore observed, not manually fabricated or silently compared with LF.
Moves preserve those exact bytes.

```text
edit mkdir pages draft/sub
edit mkdir files archive
edit folder inspect pages draft
# Copy the printed 32-character token; tokens are not shell variables.
edit folder rename TOKEN pages revised
edit folder inspect pages revised
edit folder move TOKEN files archive/published
edit folder inspect files archive/published
edit folder remove TOKEN confirm
edit folder cancel
```

Successful inspection replaces the previous plan. A valid mutation attempt
consumes its token even on failure; inspect again after a conflict. An incorrect
token or missing confirmation does not authorize anything. Plans are memory-only,
not replayable authorization after restart.

Limits are native safety bounds, not asserted primary defaults: at most1024
entries including the selected root,16 levels beneath it,512-character
tree-relative paths, one million bytes per regular file and16MiB combined
content including permission companions. Selected folder paths retain the
editor's512-character bound and16-level selection bound. Traversal/hashing is
iterative, cancellation-aware and bounded; byte buffers are zeroed. The new
workflow refuses hidden entries, unsupported/device names, special files and
every observed symlink/reparse ancestor or child instead of silently traversing
or deleting them. A folder-root `folder.allowed` sidecar requires explicit manual
reconciliation; descendant `file.allowed` companions move intact. This is a
safety difference from the primary's best-effort folder-sidecar move.

Inspection snapshots include entry names/types, contents, size, timestamps,
attributes and available Unix modes. Pre/post-rename checks reject ordinary
external changes; same-process mutations serialize on the existing editor gate.
Nonoverwriting rollback restores a detached tree when verification/cancellation
fails before commit. No hostile-administrator TOCTOU sandbox or inode-identity
guarantee is claimed.

Removal first detaches to a random hidden sibling. After successful verification,
detachment commits and bounded cleanup is not interrupted by cancellation.
Cleanup checks each known file before deleting it and deletes only known,
nonrecursive directories; it never recursively follows newly added entries.
Failures after destructive cleanup begins explicitly report **committed removal,
incomplete cleanup** and the retained tombstone, clear affected selections, and
leave the command service running. They do not promise rollback of destroyed
bytes. Operators must reconcile an unexpected tombstone manually. Windows
integration tests exercise both deny-delete failure before mutation and a
read-only permission file causing this explicit post-commit cleanup failure.
Directory-fsync/crash-atomic editor-state transactions and current Unix
execution remain unverified.

Frozen candidate validation:

- `dotnet build src\NomadNet\NomadNet.csproj -c Release --no-restore -v quiet`:
  zero warnings/errors.
- `dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~NomadNetFolderTests" --logger "trx;LogFileName=focused-final.trx" --results-directory artifacts\tests\nomad-folders -v quiet`:
  **10/10 passed**, covering executed-primary trees/bytes/selection, idempotent
  creation, confinement, dirty/closed selection, explicit confirmation and
  token reuse, ordinary and concurrent-editor conflicts, depth/width/per-file/
  aggregate bounds, cancellation, reparse/root/sidecar refusal and real Windows
  lock/post-commit cleanup/recovery.
- `dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --no-build --logger "trx;LogFileName=nomad-folders-regression.trx" --results-directory artifacts\tests\nomad-folders\managed-final -v quiet`:
  **337 passed,17 explicit skips,zero failures**.
- `dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64 -o artifacts\tests\nomad-folders\native -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -v minimal`:
  warning-free SDK10.0.403 / ILCompiler10.0.14; native `--help` executed.
- Exact native SHA-256:
  `1e80c6e066387a3d3b6e11b5c94fba8f9133c19d25b613eb3ce717265f574487`.
- `pwsh -NoProfile -File artifacts\tests\nomad-folders\Invoke-SelectedNative.ps1`:
  **11 Pester passes,zero skipped/failed**: new T427–T429 **3/3**, plus the
  previous authoring/external-editor eight cases, including actual Vim ConPTY
  facts **2/2** on this exact binary. `-FoldersOnly` selects only the new three.

The new native cases use actual processes and isolated RNS/profiles: create,
same-root rename, cross-root move, preserved file restrictions/host-handler
refresh, current/closed selection, removal and identity-preserving restarts;
invalid paths/confirmation/token, external-writer conflict, occupied destination,
Windows deny-delete sharing, directory junction refusal, plan cancellation and
healthy move/restart recovery. The third case independently regenerates the
byte-identical actual-primary receipt. These standard-console flows use pipes;
they are not physical terminal/UI or new Python-peer/network acceptance.

Ignored frozen evidence:
`artifacts\tests\nomad-folders\acceptance-1b2457bc3b2840059410b1ccb249c92e`,
including matching production/test/native before/after guards
`9307b0d07f30c99f128f6eb20e1b27bd81be8c450850dc48d0e7c4e55b9e8ff0`.
`candidate.json` records exact sources/compiler/binary, and the ignored operator
replays those artifacts into a fresh evidence root. Reference fixture SHA-256:
`d4df2f4fec4984ca29543354da457fb62c46d4667fad669776f4813644e4d266`.
Earlier candidate evidence remains separately retained. Current Linux replay is
still blocked by WSL EIO/read-only storage; no WSL repair was attempted.
Full ConfigObj/primary presentation and remaining guide/terminal presentation
remain required work. Bounded primary editor-state import is accepted under C100.
Bounded state/tree/pane controls are implemented under C97 above.

### Bounded recursive folder permissions

The original console editor implements recursive permission summaries and
confirmed replacement/removal of hosted-file `.allowed` policies. This is a
local authoring workflow, not an additional network protocol or complete
primary editor UI.

Pinned primary **1.4.4**, commit
`f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`, `nomadnet/ui/textui/Editor.py`
SHA-256 `ae6c09cde38d473fd97d1758ebcbc13edaf5d7e1f97dc4e698a870c68cd8d378`:

| Exact primary contract | Native behavior and explicit difference |
|---|---|
| `_read_allowed`,917; `_folder_permissions`,935 | Trim nonempty policy lines, count total and restricted content files, and return a common body only when every file shares one nonempty body. Order and duplicate lines in existing policies remain significant. Empty/all-public/mixed folders have no common body. Invalid UTF-8 is an explicit native inspection error, not silently reported as public. Inspection never executes policy generators. |
| `_walk_pages`,927 | Recurse through selected hosted subfolders, excluding `.allowed` content and `__pycache__` targets. Orphan `.allowed` files and cache policies remain byte-for-byte unchanged. All entries, including skipped cache content, count against C86 traversal/byte limits. Primary skips hidden directories but includes hidden filenames; native refuses hidden entries entirely. Primary can select configured roots; native retains C86's selected-subfolder/root-companion restrictions. |
| `_save_permissions`,975; `ConfirmDialog`,308 | Primary asks Make public confirmation only when clearing existing folder restrictions. Native additionally requires an exact one-use permission-inspection token and explicit `confirm` for every recursive replacement, including adding recipients to a nonempty policy. Wrong/missing confirmation authorizes nothing. |
| `_apply_permissions`,990; `_write_allowed`,1006 | Replace each target policy with validated distinct identity hashes, or remove it for public access. Primary accepts arbitrary textual lines and can leave an applied prefix on failure. Native pre-stages bounded new policies, verifies tree snapshots, and attempts reverse rollback on cancellation/failure. Existing restrictions use atomic per-file replacement plus backups, never a remove-then-create public interval. This is not an all-files/crash-atomic transaction. |

```text
edit folder permissions inspect pages chapter
edit folder permissions apply TOKEN confirm ["0123456789abcdef0123456789abcdef"]
edit folder permissions inspect pages chapter
edit folder permissions public TOKEN confirm
edit folder cancel
```

Inspection prints total/restricted counts, JSON-escaped common entries and a
32-character token. A fresh successful inspection replaces the previous plan.
Valid mutation attempts consume their token even on failure. Tokens are not
persisted or replayable after restart; ordinary folder-management tokens cannot
authorize permission application. An empty recipient array in the CLI is refused:
use the explicit `public` operation.

The C86 limits remain:1024 total entries including root and sidecars,16 levels,
512-character relative paths,1,000,000 bytes per file and16,777,216 aggregate
content bytes. The projected resulting tree must also fit those entry/byte/path
bounds. At most1024 distinct validated identity hashes can be applied. Links,
reparse ancestors/descendants, hidden/special names and read-only policies refuse.
The existing mutation gate serializes cooperating editor instances. Affected
dirty/stale buffers refuse changes; successful permission application refreshes
the current document's policy snapshot without changing content, selection or
saved resume state. Unrelated documents, orphan/cache policies, configuration
and identity are unchanged.

New policy files are staged in a random hidden sibling transaction directory,
with Unix0700 directory/0600 new-file creation and existing policy Unix modes
retained where applicable. Existing Windows attributes are preserved; Windows
rename-generated Archive/Normal backup differences are accounted for. Actual
Unix execution and NTFS ACL variants are not verified here. The staging area
is not advertised, but is not claimed to sandbox a hostile local administrator.
Every targeted policy is checked before mutation, followed by verification of
unchanged content/unrelated policies and exact resulting policy bodies.

Cancellation/disposal before completion attempts rollback without cancellation.
Replacing an existing restriction and restoring its backup use atomic per-file
replacement. Rollback never overwrites an ordinary external change. If rollback
fails, the error explicitly reports potentially partial policies and a retained
transaction for manual reconciliation. After successful verification the change
is committed; cleanup ignores cancellation and deletes only known staged/backup
files and the now-empty transaction directory nonrecursively. Unexpected entries
are retained. Cleanup failure explicitly reports **Permissions committed;
transaction cleanup incomplete** rather than pretending success or rollback.
Interrupted/crashed transactions require manual reconciliation; no automatic
crash recovery or simultaneous network-visible all-files switch is claimed.

`GenerateNomadNetFolderPermissionVectors.py` executes the actual primary
summary/save/apply/write methods with synthetic owned files, an observing UI
owner and actual `ConfirmDialog` escape/Make public button callbacks. No method,
wire format, policy interpreter or primary asset is substituted. Receipt
regeneration checks actual import paths and pinned revisions. Windows primary
atomic-write CRLF bytes are recorded as Base64; native policy output is LF with
equivalent entries. This does not resolve the separately documented primary
Windows executable-policy authorization defect or claim new network evidence.

Current selected Windows evidence:

- `dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~NomadNetFolderPermissionTests" --logger "trx;LogFileName=focused-final.trx" --results-directory artifacts\tests\nomad-folder-permissions -v quiet`: **11 passed**, no failures/skips. Includes actual second-policy deny-delete rollback, private-staging cancellation, concurrent editors, disposal, dirty/stale buffers, bounds, reparse refusal and restart.
- `dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --no-build --logger "trx;LogFileName=managed-final.trx" --results-directory artifacts\tests\nomad-folder-permissions -v quiet`: **348 passed,17 explicit skips**, zero failures.
- `dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64 -o artifacts\tests\nomad-folder-permissions\native -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -v minimal`: warning-free, with actual published `--help` execution. SDK10.0.403/compiler10.0.14; native SHA-256 `ef0c7a2b8a2bb0ac310d992fc90ebaa175bbba6205dc3be7772a53c17a300863`.
- `pwsh -NoProfile -File artifacts\tests\nomad-folder-permissions\Invoke-SelectedNative.ps1`: **14 passed, zero failures/skips**, including the three new cases and eleven existing editor/folder regressions (actual Vim ConPTY **2/2** inside the external-editor case). `-PermissionsOnly` selects only the three new cases. New native cases cover recursive summaries/replacement/public confirmation, selected-buffer save, identity/configuration/binary/orphan retention through restarts, actual locked-policy rollback, conflicts/cancel/dirty/reparse refusal, and healthy continued service after an explicitly committed cleanup failure. That failure uses an unexpected synthetic transaction entry, verifies it is retained, and manually removes only that owned marker/empty directory. The third case independently regenerates the receipt byte-identically.

The ignored `candidate.json` freezes production/test/source/compiler/native
hashes; the replay operator creates fresh evidence and matching before/after
guards, restoring process environment afterward. Earlier candidate evidence
retains its own original source and binary association. Current Linux replay
remains blocked by WSL EIO/read-only storage; WSL was not repaired.
The75 configuration forms, bounded own-profile ConfigObj grammar, original guide
and functional Micron have separate acceptance; selected-RNS extended grammar
and unexercised terminal contracts remain explicit, not hardware deferrals.
Bounded primary editor-state
import is accepted under C100 below. Bounded native state/tree/filter/gutter/panes and
debounced preview are implemented under C97 above. No full editor/UI parity,
physical-terminal or additional live-network acceptance is claimed.

### Bounded configuration sections, reset and ConfigObj source

The original terminal implements functional forms for every **71 non-printing editable
primary fields across seven sections**, not copied Urwid screens or primary
assets. Native JSON remains the default. Operators explicitly opt into the
owned `<profile>\config` ConfigObj source; unrelated native-only settings,
identity and persistence remain native-owned.

The reference is primary **1.4.4**, commit
`f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`, and RNS ConfigObj at
`a29bfb51c468945c96a1ee22cae0114e993e2391`. Exact source SHA-256:

| Source | SHA-256 |
|---|---|
| `nomadnet/ui/textui/Config.py` | `13a0a531ccc2fc207dc6b4c7f644c3eff6d261bb34edf84781c1c6f76f4b0421` |
| `nomadnet/NomadNetworkApp.py` | `c36ce4c537fe5af534910d1f5200d2a2ea5dd50f74bf9e4157222eff15614eb2` |
| `nomadnet/util.py` | `77a4368c73b8a84ef2e9df41b4e423bcb1d0677e12ed18debe445996fa3a9e38` |
| `nomadnet/vendor/additional_urwid_widgets/FormWidgets.py` | `dd8f20052ead5de2b05a5d1ae8f377fd2758f8dd1058553d615995e1d22f81c6` |
| actual imported `RNS/vendor/configobj.py` | `fb7036cfe845a256889752ac5839d7e8dce3356eef9c7b440a92a646be4bec8e` |

`GenerateNomadNetConfigurationFormVectors.py` executes actual
`ConfigSectionView` construction/population/back/reset/save, the actual Yes/No
reset buttons, `createDefaultConfig/applyConfig`, ConfigObj parsing/dictionary
resolution/writing and `write_config_atomic` **before implementation**. Only
screen size, modal notice display and navigation/restart observers use synthetic
owners; core form/parser/writer methods are unchanged. Source/import/version
receipts accompany the fixture; no configuration comment/guide/UI assets are copied.

| Exact primary contract and observable completion criterion | Current disposition |
|---|---|
| `Config.py:CONFIG_SECTIONS`, `ConfigSectionView` field creation | **implemented functional forms**: node16, client14, logging3, textui12, utilities1, rrc13, reticulum12. Every key/type/default/dropdown option/validator is compared to the executed actual form catalog; the primary printing4 section is deliberately omitted (C78). The editor action is not an eighth section. |
| `ConfigSectionView` population, `_default_write_value`, `_widget_write_value` | **implemented bounded subset**: missing fields show form defaults, checkbox truth normalization, scalar-to-list population, list-to-text joining and unknown dropdown fallback. JSON scalar/bool/number/string-list input avoids shell parsing. Limits:128 list items,4096 characters per item, finite floats, whole-number fields, validated hash/color/port fields and final native runtime validation. Invalid edits retain the staged value/file and permit recovery. Control characters in form values are refused; raw ConfigObj multiline editing is separate. |
| `on_reset`, `on_back`, `on_save:1003–1070` | **implemented / safety difference**: reset requires `confirm`, stages defaults only, cancel discards, and save separately requires `confirm`. Empty keys are deleted; absent fields equal to defaults are omitted, so reset does not materialize an absent section. Existing empty sections and unknown keys survive. Forms never apply running service options; explicit full restart is required. Native snapshots reject external changes instead of the primary's reload-and-merge/swallowed reload-error behavior. |
| Runtime node/client/logging fields; `NomadNetworkApp.applyConfig` | **implemented bounded projection** onto existing runtime settings. The source loader uses the existing importer semantics and native bounds, not a new protocol. `enable_client` and `user_interface` are preserved raw controls outside the client's14 form fields, as in primary. Native-only identity/display name/trust/manual propagation/timestamps/media settings are retained. The form default is not necessarily the missing loader default: e.g. node360/starttrue versus missing node720/startfalse. Fixture records generated-default application separately. Unsupported source keys remain preserved and reported by import; they are not silently applied. |
| TextUI12, utilities1, RRC13 operational effects | **implemented bounded functional controls / explicit placeholder**: TextUI and utility visibility have functional native controls, including C95's scoped Windows NativeAOT acceptance below. Both RRC render switches and ten history/style fields are operational in the original bounded terminal service; `enable_esoterics` is an executed/inventoried **upstream-placeholder**, not an invented no-op feature. Original layout/palette/safety bounds remain explicit, not full Urwid parity. |
| Selected Reticulum12 fields, `Config.py:672–690,876–882` | **eleven runtime fields / one intentional safety difference**: selected actual RNS file, not native JSON/default-global RNS. `respond_to_probes` and `use_implicit_proof` now join the nine supported controls, validate through public `RnsdConfigParser` and activate in the running owner after full restart. `panic_on_interface_error` is **safety-refused**: fixed native top-level failure termination and child-peer failure isolation, not missing hardware or a no-op toggle. Raw explicit panic keys are refused as well, including false; remove the key to use fixed native ownership. |
| ConfigObj root assignments, quoted names/scalars, comma lists, nested sections, comments, literal backslashes, triple quotes and interpolation | **implemented bounded grammar with executed vectors**. Root `enable_node=yes` is legal and retained/reported unsupported, not treated as `[node] enable_node`. Empty scalar, empty list, one-item list and scalar-with-comma remain distinct. Interpolation resolves section/DEFAULT/ancestor values; absent/cyclic expansion fails explicitly. Unknown nested/multiline/comment text is retained byte-for-byte when untouched. Duplicate names, malformed lists/nesting/quotes, invalid UTF8 and NUL refuse before save. |
| Full ConfigObj grammar/serialization and RNS parser integration | **partial / safety difference**: primary source64KiB, selected RNS1,000,000 bytes,8192 characters/line,2048 entries/sections/list members,16 section levels,32 interpolation levels,64KiB expanded value and four-times-input aggregate expansion budget. Eager interpolation validation differs from ConfigObj's deferred access errors. Ambiguous dotted flattened paths and unencodable quote combinations explicitly fail. Native retains BOM/newline/comments/unknown untouched bytes instead of canonicalizing the entire file. Edited quoted keys remain safely quoted instead of reproducing the primary writer's ambiguous equals-key output. Selected RNS refuses interpolation/triple quotes because the public stack would not execute them faithfully; passing ConfigObj validation alone never grants RNS grammar parity. |
| Atomic writer `util.py:6–34`, restart serialization | **implemented bounded safety subset**: private same-directory `CreateNew` staging, file flush/fsync, preserved existing Unix modes in code, snapshot-checked atomic per-file replacement and cleanup on failure/cancellation. Existing profile lease remains required by the executable. Directory fsync, hostile-admin path races, crash-atomic multi-file activation and current Unix mode execution are **unverified**, not claimed. |

```text
config sections
config primary enable confirm
config section client
config fields
config set announce_interval 120
config set notify_on_new_message false
config reset confirm
config cancel
config section client
config set announce_interval 120
config save confirm
config cancel
config section reticulum
config set instance_name "selected-instance"
config save confirm
config cancel
```

Activation exports supported current native settings only if the primary source
is absent; existing primary text is never overwritten. Export refuses fractional
native page/file refresh minutes (including the native five-second default)
rather than rounding. Reconcile those settings explicitly first. A failed native settings
commit attempts snapshot-checked deletion of a newly created inactive source;
rollback failure explicitly retains it for manual reconciliation. This is not
a crash-atomic two-file transaction. Missing/malformed active source fails
explicitly; restore it or repair native JSON, not silently revert to defaults.

`config primary disable confirm`, with the configuration buffer closed, freezes
effective supported settings into native JSON and retains source text.
`config edit|show|external primary` supports the same raw validation/conflict-safe
editor flow. While primary source is active, competing runtime settings commands
refuse rather than write an ineffective JSON setting. The existing propagation
gate serializes source/section commits with automatic timestamps; successful
restart saves make subsequent timestamps memory-only. Running options/identity
are unchanged until restart. This is tightly coupled writer ownership, not new
propagation behavior.

T450–T459 cover executed grammar/catalog/reset semantics, typed/budget/refusal
boundaries, byte-preserving staged saves, missing source and activation errors,
cancel/reset/conflict recovery, selected RNS refusal, restart/source disable,
native-only retention, cancellation/disposal and competing runtime writers.
T460–T463 select actual native console workflows and independent primary
regeneration. Physical terminal/layout parity and current Linux execution are
not claimed; WSL EIO remains blocked and no repair was attempted. Remaining
required software includes complete
ConfigObj/RNS edge grammar and primary guide/terminal presentation, not merely
hardware acceptance. Bounded primary editor-state import is accepted under C100.
Bounded native editor presentation and controls are implemented under C97 above.

**Concrete selected-RNS parser gap:** the application's own ConfigObj document
supports bounded scalar/list quoting, multiline values and inherited
interpolation, including executed primary observations. Selected RNS-file saves
nevertheless explicitly refuse interpolation and triple-quoted values before
publication (`NomadNetEditor.ValidateConfiguration`, T458), because the pinned public
`DotReticulum.Applications.Rnsd.RnsdConfigParser.Parse` reads a narrower
line-oriented grammar. A successfully parsed native application profile does
not prove that the stack can load the same RNS configuration. This required
software boundary must be resolved in the separate DotReticulum repository,
with its tests and a published package release adopted through `DotReticulumVersion`,
not by bypassing validation or copying stack code into this repository. No stack fix or authorization to push one is claimed.
The original guide/editor and bounded grammar acceptance above must not be
described as absent merely because exact Urwid presentation is not reproduced.

Preceding Windows validation for the original configuration candidate, before
the read-only LXMF readiness addition (current refreshed acceptance is above):

- `dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --no-restore -warnaserror --filter "FullyQualifiedName~NomadNetConfiguration|FullyQualifiedName~NomadNetConfigTests" -v quiet`: **26 passed**, zero failures/skips. The obsolete rejection of legal root `enable_node=yes` was replaced with the exact executed-root disposition assertion; all other existing malformed-config assertions remain.
- `dotnet build src\NomadNet\NomadNet.csproj -c Release --no-restore -warnaserror -v quiet`: zero warnings/errors.
- `dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --no-restore -warnaserror --logger "trx;LogFileName=configuration-forms-frozen.trx" --results-directory artifacts\tests\nomad-config-forms\managed-final -v quiet`: **360 passed,17 explicit skips**, zero failures.
- `dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64 -o artifacts\tests\nomad-config-forms\native -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror -v minimal`: zero warnings/errors; published `--help` executed successfully. SDK10.0.403/compiler10.0.14; stack `e52da394658f6f6c723214a9f811552418789cb6`.
- Exact native SHA-256: `f846045e0df7b30257e0df2694d1f7689e6267ed2af6b45259aec0e7e9f0757c`.
- `pwsh -NoProfile -File artifacts\tests\nomad-config-forms\Invoke-SelectedNative.ps1 -FormsOnly`: **4 passed**, zero failures/skips. Actual native opt-in/no-overwrite, all-section display, typed edit/cancel/reset confirmation/save, selected RNS text/comment retention, blocked-stack/invalid-source/fractional-export/profile-lease/external-conflict/reparse refusal, identity-preserving restarts and healthy continued service. The fourth case independently regenerates the actual-primary fixture byte-identically.
- `pwsh -NoProfile -File artifacts\tests\nomad-config-forms\Invoke-SelectedNative.ps1`: **18 passed**, zero failures/skips, including the previous fourteen editor/folder/permission/external-editor cases and actual installed Vim ConPTY **2/2** on this exact binary. These new form workflows use actual standard-console processes with pipes, not physical terminal/layout acceptance.

Ignored frozen `candidate.json` includes exact NomadNet/**current LXMF**
production and test/generator hashes, SDK/compiler/stack and selected real editor.
The operator requires fresh owned evidence and verifies those guards both before
and after replay. Full replay receipt:
`artifacts\tests\nomad-config-forms\acceptance-5530a70624524a10bc1e5f675077b4c2`;
matching before/after guard SHA-256
`a4b095d194bb7be56ab4352870d85e6a164b632b9a116ffa2f1debe259b418a1`.
Fixture SHA-256:
`28b869842b7159dda0a0ef289f554cdcdd3a079eac98bfa9ab69b1ee5a2fab27`.
Earlier candidate evidence retains its own source/binary boundaries.

### Selected local picker/composer acceptance

`GenerateNomadNetFileBrowserVectors.py` executes actual primary
`FileBrowser` construction, listing, selection, oversize notices, navigation,
cancel and single-selection callbacks. The sole disclosed seam supplies the
shared UI's glyph host; it does not replace filesystem/selection methods.
This is reference-vector evidence, not full primary UI or network evidence.
The fixture records primary commit
`f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2` and raw `FileBrowser.py` SHA-256
`116c1755c672e30728d30105f589ff1bf0b6940448be8022862ae4d0c86e1983`.

The five filesystem integration cases cover those vectors, immutable snapshots,
single-selection callback ordering, cancel preserving the prior composition,
failing observers with continuing callbacks, no-subscriber operation, count/
aggregate/directory limits, missing/oversize/empty files, cancellation before
opening and between files, and Linux FIFO/device/link rejection without content
reads. The FIFO case is an explicit Windows skip, not a passing Windows claim.
All **Windows 104 +4 explicit skips**, **Linux 108/108** scoped cases pass when
the selected FFmpeg is configured.

Final strict NativeAOT selections:

- Windows `artifacts\builds\nomadnet-file-picker-win\publish\NomadNet\release_win-x64\nomadnet.exe`;
  SHA-256 `18a8f9f9b8c41bcd74b46ddfe8ea5422ee90f09d7f50d91fe9582ca4d6897a7a`.
- Linux `artifacts/builds/nomadnet-file-picker-linux/publish/NomadNet/release_linux-x64/nomadnet`;
  SHA-256 `84df69345260ed03704aa7a37a3a598da5686d20ae8902137dfb8c73b62a6d3e`.

Both `dotnet publish src\NomadNet\NomadNet.csproj -c Release -r <rid>
--artifacts-path artifacts\builds\nomadnet-file-picker-<host>
-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -v:q`
commands pass. Both exact binaries pass **Native 4/4**, including the existing
large attachment/resource workflow. The expanded actual primary application
case passes **1/1 on Windows (106.61 seconds)** and **1/1 on Linux (96.90 seconds)**.
It opens/navigates the real native picker with spaces in directory names,
retains selection across directories, commits two files, rolls a subsequent
edit back on cancel, and sends actual bytes through LXMF. The primary app
receives both names, sizes and SHA-256 values; its actual router then sends both
files back and native durable bytes/names are checked. Successful queueing clears
the native selection. These new two-file flows use a generated64-KiB binary
and text note with default primary receive limits; they are not a new
multi-megabyte or transport-hop claim. Linux additionally passes actual
RRC/forms/partial/replay **1/1 (18.75 seconds)**.

```powershell
& artifacts\tests\nomadnet-file-picker-win-acceptance.ps1
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-file-picker-linux-acceptance.ps1
```

The ignored recipes require the same pinned references/converters as the
hosted-policy gate and additionally enable
`DOTRETICULUM_NOMADNET_FILE_PICKER_LIVE=1`. They SHA-guard both the native binary
and frozen Pester file (`5f9c553e85dff441f9e2b0d1d25fb7bad77423cbb99bd3f2e3a512f0424d2aa3`).
Successful application artifacts record `filePicker=true`.

`dotnet build DotReticulum.Ecosystem.slnx -c Release --artifacts-path
artifacts\builds\nomadnet-file-picker-integrated -v:q` passes with zero warnings/
errors. `dotnet test DotReticulum.Ecosystem.slnx -c Release --no-build
--artifacts-path artifacts\builds\nomadnet-file-picker-integrated --logger trx
--results-directory artifacts\tests\nomadnet-file-picker-integrated -v:q`
passes **1,799 tests, 87 explicit skips, zero failures**. Converter-dependent
checks skipped there are separately exercised by the selected scoped runs.
Remaining Kitty presentation, configuration, migration, complete primary editor,
and remaining presentation workflows are not declared complete; bounded QR
export/display and opt-in clipboard are described separately above; printing is not implemented (C78).

### Selected hosted-policy acceptance

The regenerated configuration fixture executes actual primary constructors for
startup-announce on/off, page/file rescan minutes, missing defaults and negative
interval clamping. The upstream `Node.__jobs` elapsed-time rules are separately
audited and tested with deterministic native schedules, including independent
deadlines, disabled scans, backwards clocks and unacknowledged work. Real
filesystem tests cover 10,000-handler boundary rejection without registry
mutation, independent root enumeration, invalid/nonfinite settings, asynchronous
disposal and chaining the existing propagation callback without a log subscriber.

Selected strict NativeAOT binaries:

- Windows `artifacts\builds\nomadnet-host-policy-win\publish\NomadNet\release_win-x64\nomadnet.exe`;
  SHA-256 `731f7d10dab3a008bd69c16e9615e5cbdb9d58ec05b6f7b4e28d99895d754af4`.
- Linux `artifacts/builds/nomadnet-host-policy-linux/publish/NomadNet/release_linux-x64/nomadnet`;
  SHA-256 `c43dd7da8e88c3a08fd582f4564ad0ff72bf5068bc7be8373d1e728bf5f61f27`.

Both `dotnet publish src\NomadNet\NomadNet.csproj -c Release -r <rid>
--artifacts-path artifacts\builds\nomadnet-host-policy-<host>
-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -v:q`
commands pass. Scoped managed tests with the selected converter pass
**Windows 100 +3 explicit Linux-only skips**, **Linux 103/103**.
Both selected binaries pass **Native 4/4** and the expanded actual primary
application case **1/1** (Windows 95.47 seconds; Linux 85.90 seconds).
Linux also passes actual primary RRC/form/partial/replay **1/1** (18.62 seconds).
The live Windows fixture opens an atomic state snapshot with read/write/delete
sharing, so observing state does not conflict with the application's atomic
replacement. Assertions and existing deadlines are unchanged.

The additional `DOTRETICULUM_NOMADNET_HOST_POLICY_LIVE=1` mode starts the real
primary observer before the native process. A supported extra announce handler
records genuine `nomadnetwork.node` announces without replacing the application's
handlers. After seven seconds it verifies no startup broadcast, then observes
the explicit native announcement. A newly created page becomes registered after
the configured real one-minute interval, while an independently disabled file
scan does not register its new file. The actual primary requests that new page,
then an explicit refresh registers the file and the primary downloads it.
No clock acceleration, private scheduler invocation or synthetic callback
substitutes for the real minute in this application flow.

The selected Linux binary also passes the complete mixed propagation/restart/
scheduled retrieval/cancellation/dropped-Resource-part case **1/1**, 243.92 seconds.
The topology and non-default peer settings are those recorded below: actual
primary Python transport/propagation node, primary Python client, native node
and native client, with the impairment proxy on the native client's connection.
The restart gate observes a genuine fresh `lxmf.propagation` announcement before
retrieval, not merely the new process's startup banner. Its thirty-second
observation window accommodates the source-required twenty-second propagation
schedule. Existing transfer/link deadlines and assertions are unchanged; there
is no path expiration, stamp-cost reduction or failed-transfer retry masking
the restart. Public reference route/interface diagnostics are recorded.

```powershell
& artifacts\tests\nomadnet-host-policy-win-acceptance.ps1
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-host-policy-linux-acceptance.ps1
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-host-policy-propagation-acceptance.ps1
```

These ignored recipes select matching frozen managed DLLs/binaries and the
same pinned primary/RNS/LXMF, converter and runtime prerequisites as directory
acceptance below. Older recipes explicitly clear the new opt-in. The application
artifact records `hostPolicy=true`. Client announcement acceptance is recorded
separately below; node
statistics are implemented under C107; other unsupported configuration retains
its explicit inventory disposition.

The isolated integrated checkpoint `dotnet build DotReticulum.Ecosystem.slnx
-c Release --artifacts-path artifacts\builds\nomadnet-host-policy-integrated -v:q`
passes with zero warnings/errors; `dotnet test DotReticulum.Ecosystem.slnx
-c Release --no-build --artifacts-path artifacts\builds\nomadnet-host-policy-integrated
--logger trx --results-directory artifacts\tests\nomadnet-host-policy-integrated -v:q`
passes **1,750 tests, 83 explicit skips, zero failures**. Converter-dependent
NomadNet checks are among the explicit skips in this integrated invocation;
the separately selected Windows/Linux converter runs above exercise them.

### Selected directory/application acceptance

`GenerateNomadNetDirectoryVectors.py` executes actual primary
`Directory.known_nodes`, identification getters/setters and `remember`, using a
real isolated RNS runtime and actual directory persistence. The ephemeral test
identity is not exported. The fixture records primary source SHA-256
`2c1e333044d0417573eef7c280737a849d34f5942303142891770aed05cf8744`;
vectors include the XOR rank fallback, non-BMP Unicode sorting, default/no
identification consent and associated-node trust. Native tests additionally
exercise atomic metadata preservation, cancellation and absent/invalid
associations without creating nodes.

Exact strict NativeAOT binaries:

- Windows `artifacts\builds\nomadnet-directory-win\publish\NomadNet\release_win-x64\nomadnet.exe`;
  SHA-256 `0f8c8effc358c71e4206ed1cf19b98a781e54ac2bbd54c9b68e638ca80bf862b`.
- Linux `artifacts/builds/nomadnet-directory-linux/publish/NomadNet/release_linux-x64/nomadnet`;
  SHA-256 `30cc87c563138a99188ffd69d9decd8bb4f4c0be6c0cb1dab6fe8cabb0658184`.

Both strict `dotnet publish src\NomadNet\NomadNet.csproj -c Release -r <rid>
--artifacts-path artifacts\builds\nomadnet-directory-<host>
-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -v:q`
commands pass. Scoped managed tests with the selected converter pass
**Windows 90 +3 explicit Linux-only skips**, **Linux 93/93**. Both selected
binaries pass **Native 4/4** and actual primary image/conversation/configuration/
navigation/directory **1/1** (Windows 29.4 seconds; Linux 25.54 seconds).
Linux also passes actual primary RRC/form/partial/replay **1/1** (18.72 seconds).

The directory mode verifies saved-node UI, opening, rank and trust propagation,
anonymous/identified/anonymous requests, native authorization of an actual
primary request, persisted node policy and restart/cache behavior. The thin
adapter observes real link-close identities via supported callbacks, chaining
the original primary `peer_connected`/`peer_disconnected` methods; it does not
replace authorization, requests, identity processing or transport. Observations
are bounded and overflow/insufficient evidence fails explicitly.

**Primary Windows defect:** `Node.request_allowed` treats a plain `.allowed`
list as executable because Windows `os.access(path, X_OK)` returns true, then
fails with **WinError 193**. Correctly identified requests are still rejected.
The Windows gate records actual identity disclosure separately and verifies
native static-list authorization in the reverse direction; it does not claim
successful primary static-list authorization on Windows. The unmodified Linux
primary permits the whitelisted identity and denies anonymous requests.
No reference shim suppresses this defect. A previously cached denial survived
restart as expected; the test explicitly reloads rather than weakening the
authorization assertion.

Saved ignored recipes select exact DLLs, binaries, converters and pinned primary
environments and enable `DOTRETICULUM_NOMADNET_DIRECTORY_LIVE=1` alongside
the preceding modes:

```powershell
& artifacts\tests\nomadnet-directory-win-acceptance.ps1
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-directory-linux-acceptance.ps1
```

Prerequisites remain the selected .NET 10 SDK, local strict-AOT toolchain,
PowerShell/Pester, pinned Python environment/source, and external FFmpeg
described below. Linux executable-page test work stays on the native Linux
filesystem. Python worker count2 remains a disclosed standard runtime bound.
Older frozen acceptance recipes explicitly clear the directory opt-in.

The final integrated `dotnet build DotReticulum.Ecosystem.slnx -c Release
--artifacts-path artifacts\builds\nomadnet-directory-solution -v:q` passes
with zero warnings/errors; matching `dotnet test ... --no-build -v:q --logger trx
--results-directory artifacts\tests\nomadnet-directory-full-final` passes
**1,654 tests, 81 explicit skips, zero failures** across all five projects.

### Selected navigation/application acceptance

`GenerateNomadNetNavigationVectors.py` executes the pinned actual primary
`Browser.write_history`, `back` and `forward` with a disclosed retrieval observer,
not a translated history implementation. The fixture records source SHA-256
`a36f393f3e6a356a4c5b19f39596ca3f4fe8812aaf5f09be6c05573173d88079`.
It covers bounds on back/forward, submitted-field replay and forward-branch
truncation. Four native unit tests additionally cover immutable snapshots,
64-entry eviction, invalid input and oversized forms without mutation.

Selected strict NativeAOT binaries:

- Windows `artifacts\builds\nomadnet-navigation-win\publish\NomadNet\release_win-x64\nomadnet.exe`;
  SHA-256 `1f4d092326132a9d4e78acf54e682ff5eefa7dc7417b4a9c45cb398d39dd9b6f`.
- Linux `artifacts/builds/nomadnet-navigation-linux/publish/NomadNet/release_linux-x64/nomadnet`;
  SHA-256 `a827dafdb077c2c4bb1ff4a0c6afcf29b77835cfd01cbaf9381385e5d9c009b7`.

Both `dotnet publish src\NomadNet\NomadNet.csproj -c Release -r <rid>
--artifacts-path artifacts\builds\nomadnet-navigation-<host>
-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -v:q`
commands pass. Windows scoped managed tests with the selected converter pass
**86 +3 explicit Linux-only skips**; Linux passes **89/89**. Each selected binary
passes **Native 4/4** and the actual primary image/conversation/configuration/
navigation case **1/1** (Windows 37.48 seconds; Linux 26 seconds).
Linux additionally passes actual primary RRC/form/partial **1/1**
(19.14 seconds), including a back/forward/reload round trip to the unmodified
primary executable form through a Python transport. Back recreates the initial
form and partial fields; forward/reload still submit the earlier edited values,
not those new defaults. Windows verifies static primary-page navigation,
reload, branch truncation and empty history after restart, not executable forms.

The saved ignored recipes explicitly select binaries, managed DLLs, converters
and references and enable `DOTRETICULUM_NOMADNET_HISTORY_LIVE=1` in addition to
the image/conversation/configuration modes:

```powershell
& artifacts\tests\nomadnet-navigation-win-acceptance.ps1
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-navigation-linux-acceptance.ps1
```

The image case writes the selected binary hash and explicit `navigation` flag
to its `acceptance.json`; an older frozen binary must not enable the new mode.
The current local file picker and inline ASCII loader are documented separately
above, including original conversation ASCII; complete terminal UI and Kitty
workflows remain missing.

The integrated `dotnet build DotReticulum.Ecosystem.slnx -c Release
--artifacts-path artifacts\builds\nomadnet-navigation-solution -v:q` passes
with zero warnings/errors. The matching `dotnet test ... --no-build -v:q
--logger trx --results-directory artifacts\tests\nomadnet-navigation-full-final`
passes **702 tests, 81 explicit skips, zero failures** across all five projects.

### Selected automatic Micron/browser acceptance

Use the **browser** binaries with the current expanded Pester test file; the old
activity binaries do not contain automatic composition and must not be selected
for these new assertions.

- Windows `artifacts\builds\nomadnet-browser-win\publish\NomadNet\release_win-x64\nomadnet.exe`;
  SHA-256 `ccd68949660e4e8a104a9ed1afad2228aea757b528b98b79c432d8213492e142`.
- Linux `artifacts/builds/nomadnet-browser-linux/publish/NomadNet/release_linux-x64/nomadnet`;
  SHA-256 `2c9dcab9408b3b87a75d5e88b97a27e698053f7696d6d0704b7b10d84d845415`.

Strict NativeAOT publish (`nomadnet-browser-win`, `win-x64`; `nomadnet-browser-linux`,
`linux-x64`) passed on both hosts. Windows scoped tests passed **57** with two
explicit Linux-only skips; Linux passed **59/59**. Native Pester passed **4/4
each**, now including actual automatic initial load/refresh, changed server
content, retained root edits, child field/link numbering and navigation away.
The actual primary app + rrcd + native client RRC/form gate passed **1/1**
(19.37 seconds) through a Python transport. Its executable Python-hosted page
returns a child field and anchor: automatic initial/periodic updates, explicit
`p:summary` refresh, submission of edited child fields and child anchor activation
all run, followed by the group-chat/restart checks above.

The saved ignored `artifacts\tests\nomadnet-browser-acceptance.ps1` freezes both
the selected Linux native binary and the already-built Linux managed test DLL,
avoiding shared source/obj races during independent acceptance. It sets the same
reference paths below and enforces managed exit success, Native4/4 and RRC1/1:

```powershell
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-browser-acceptance.ps1
```

`GenerateNomadNetVectors.py` executes primary `parse_partial`, `detect_partials`,
`partial_received`, combined field collection and `update_partials`. The fixture
records parser/Browser revision and source hashes and discloses layout/alarm/load
observation seams. These are independent policy/composition vectors, not live
Python browser automation; actual live acceptance is native browser to primary
hosted application pages, not an automated Urwid screenshot/pixel comparison.

The matching integrated checkpoint also passed `dotnet build
DotReticulum.Ecosystem.slnx -c Release --artifacts-path
artifacts\builds\nomadnet-browser-solution -v:q` with zero warnings/errors and
`dotnet test DotReticulum.Ecosystem.slnx -c Release --artifacts-path
artifacts\builds\nomadnet-browser-solution --no-build -v:q`: **406 passed,
70 explicit opt-in/platform skips, zero failures** across the five test projects.

### Selected core-configuration acceptance

`GenerateNomadNetConfigVectors.py` executes the real, unmodified primary
constructor and `applyConfig` in isolated no-interface application subprocesses.
The adapter only observes attributes; it does not replace constructor,
configuration, LXMF or RNS behavior. The fixture records exact revision,
raw source SHA-256, inputs, clamped values and path observations. No private
identity material is included. This independently checks policy translation;
the application flow below separately checks operational behavior.

Selected Windows strict NativeAOT:
`artifacts\builds\nomadnet-config-win\publish\NomadNet\release_win-x64\nomadnet.exe`;
SHA-256 `957ca4f45d6da55fd2a6968a27979783824d05260de0680f5308d742af49bab8`.
The strict publish command uses `-r win-x64 --artifacts-path
artifacts\builds\nomadnet-config-win -p:ILLinkTreatWarningsAsErrors=true
-p:IlcTreatWarningsAsErrors=true`. Scoped managed tests with the selected
FFmpeg pass **82 +3 explicit Linux-only skips**; selected Native cases pass
**4/4**; actual primary image/conversation/configuration case passes **1/1**
(34.05 seconds).

Selected Linux strict NativeAOT:
`artifacts/builds/nomadnet-config-linux/publish/NomadNet/release_linux-x64/nomadnet`;
SHA-256 `0f0ee45e6368ffbc89d698fa89b95a747b74d38a0e5fcf29070a4a27cd663895`.
The same strict publish uses `-r linux-x64` and the matching
`nomadnet-config-linux` artifacts root. Linux scoped managed passes **85/85**;
Native **4/4**, actual primary image/conversation/configuration **1/1**
(32.30 seconds), actual primary RRC/form/partial **1/1** (21.80 seconds).

The configuration mode additionally sets
`DOTRETICULUM_NOMADNET_CONFIG_LIVE=1` and runs the actual published import
command, rejects an occupied target, checks unsupported-key reporting/source
immutability, then starts with those settings. It serves from non-default page
and file roots, writes media and conversation export to the non-default download
root, and persists the configuration through restart. Real primary direct replies
meet the native required stamp cost13 with enforcement enabled; there is no
stamp-source replacement or lowered private protocol constant.
`PYTHON_CPU_COUNT=2` is a disclosed standard Python runtime worker bound in these
selected runs, not a reference code shim.

The saved ignored Windows script selects frozen DLL/binary/tool/reference paths:

```powershell
& artifacts\tests\nomadnet-config-win-acceptance.ps1
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-config-linux-acceptance.ps1
```

Previous image/conversation scripts explicitly clear this new opt-in; their
accepted frozen binaries remain valid for their matching tests.

The integrated command `dotnet build DotReticulum.Ecosystem.slnx -c Release
--artifacts-path artifacts\builds\nomadnet-config-solution -v:q` passes with
**zero warnings/errors**. `dotnet test ... --no-build -v:q --logger trx
--results-directory artifacts\tests\nomadnet-config-full-final` passes
**667 tests, 81 explicit opt-in/platform skips, zero failures** across five
projects. This is not a claim that the remaining unsupported configuration
keys, complete ConfigObj grammar or upstream profile migration are implemented.

### Selected conversation/application acceptance

The current conversation slice's exact strict NativeAOT binaries are:

- Windows `artifacts\builds\nomadnet-conversations-win\publish\NomadNet\release_win-x64\nomadnet.exe`;
  SHA-256 `1dafec172a12793e045b6bfa23bc5f28df86e90e9b31e7d86200e433f63f7dbf`.
- Linux `artifacts/builds/nomadnet-conversations-linux/publish/NomadNet/release_linux-x64/nomadnet`;
  SHA-256 `51ad8c36d4d7a8c900ea89c08e31a07ff1d891bc8edd2e04691348828b0e4815`.

The strict publish commands below use the corresponding
`nomadnet-conversations-*` artifacts paths, with no new dependency. Scoped
managed tests with the selected FFmpeg pass **67 +3 Linux-only skips on
Windows**, **70/70 Linux**; Native cases pass **4/4 each**. The image application
case, additionally setting `DOTRETICULUM_NOMADNET_CONVERSATION_LIVE=1`, passes
**1/1 each** with these additional assertions:

- Native peer preference sends real opportunistic LXMF text; actual primary
  reception reports method1, and its real conversation unread/read APIs run.
- Actual primary direct replies create durable native unread counts; selecting
  the viewed peer clears them and prints a subsequent reply without unread.
  Native UTF-8 export contains that reply.
- Contact trust, notes, preference, blacklist and read state survive native
  restart. Forgetting the contact retains the independent blacklist.
- Actual primary generates fresh encrypted paper messages; native ingestion is
  rejected while ignored both before/after restart, and accepted/persisted after
  unignore. Ignored content never enters history. This verifies application
  blacklist wiring without weakening direct-send retry policy.
- Conversation deletion removes its records/alerts. Deterministic integration
  tests additionally verify selected attachment-root deletion, Linux symlink
  rejection, failure purge, duplicate counters, concurrent reception,
  cancellation and quota rejection without history eviction.

The same Linux binary passes current actual primary RRC/form/partial acceptance
**1/1** (18.88 seconds). Image/restart checks remain in the same live case.
This direct two-application evidence does not establish opportunistic multihop,
propagated attachment preference or interactive Urwid automation.

```powershell
& artifacts\tests\nomadnet-conversations-win-acceptance.ps1
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-conversations-linux-acceptance.ps1
```

These ignored scripts select frozen managed DLLs/binaries plus the exact
reference/tool paths documented below and require expected pass counts.
Older image scripts explicitly disable the new conversation opt-in, preserving
their frozen binary/test provenance. The parent independently accepted the
image slice on both hosts: scoped managed, Native4/4, Images1/1, Linux RRC1/1.

`GenerateNomadNetConversationVectors.py` executes primary `Directory.trust_level`,
`DirectoryEntry`, `Conversation.ingest`, `mark_conversation_read` and
`delete_conversation`. The fixture records exact revision/source hashes; its
message persistence callback writes a generated public marker rather than
LXMF bytes. These are policy vectors, not invented wire vectors or live proof.

`dotnet build DotReticulum.Ecosystem.slnx -c Release --artifacts-path
artifacts\builds\nomadnet-conversations-solution -v:q` passes with **zero
warnings/errors**. The corresponding frozen `dotnet test ... --no-build -v:q
--logger trx --results-directory artifacts\tests\nomadnet-conversations-full-ready`
passes **484 tests, 80 explicit opt-in/platform skips, zero failures**.
Earlier integrated runs encountered unrelated Sideband voice-startup races;
those failures were reported with TRX evidence. Its owner corrected actual
server-accept readiness before announce, and the final rebuilt suite passes.
No Sideband code was changed by the NomadNet owner.

### Selected image/application acceptance

The current image slice's frozen binaries are:

- Windows `artifacts\builds\nomadnet-images-win\publish\NomadNet\release_win-x64\nomadnet.exe`;
  SHA-256 `fbfc62d5056deb9ecca77ff6ffcb01fd5ac67ae152c4a89875ebe9e3bc4568fb`.
- Linux `artifacts/builds/nomadnet-images-linux/publish/NomadNet/release_linux-x64/nomadnet`;
  SHA-256 `a33f54488e4369395deec12f419bb05503675639c0eb61c76180f0495057f906`.

Strict `dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64
--artifacts-path artifacts\builds\nomadnet-images-win
-p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true` passes;
the corresponding `linux-x64`/`nomadnet-images-linux` publish passes. Windows
uses SDK10.0.403; Linux SDK10.0.402. Selected published binaries run help,
identity restart, hosting/transfers/paper and the actual image flow, not only
startup.

With `DOTRETICULUM_NOMADNET_IMAGE_CONVERTER` set to the exact local FFmpeg:

- Windows scoped `dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c
  Release --artifacts-path artifacts\builds\nomadnet-images-win -v:q`:
  **61 passed, 2 explicit Linux-only skips**.
- Linux scoped equivalent on native Linux filesystem:
  **63 passed, 0 skipped**.
- `Invoke-Pester -Path tests\Pester\NomadNet.Tests.ps1 -Tag Native`:
  **4/4 each host**.
- `Invoke-Pester -Path tests\Pester\NomadNet.Tests.ps1 -Tag Images`, with
  `DOTRETICULUM_NOMADNET_IMAGE_LIVE=1`: **1/1 each host**.
- Selected Linux `-Tag Rrc`: **1/1**, including actual primary form/automatic
  partial and real group-chat/restart regression (19.28 seconds).

The image case runs a selected native TCP-server application and actual pinned
primary TCP-client application directly. Native conversion serves generated PNG
to the primary's actual `ImageData(remote_source=True)` WebP-to-PNG path; native
ASCII/colour preview decodes primary-converted PNG. Both processes restart with
unavailable converters and still serve their persisted conversion caches.
The test waits for the real Python TCP client to be online before reannouncing
the native node; it does not replace networking or image handlers.
This is direct application evidence, not multi-hop/packet-loss/Urwid graphics
or physical display-quality evidence.

`GenerateNomadNetImageVectors.py tests\NomadNet.Tests\NomadNetImageVectors.json`
executes actual primary `Node.convert_media_to_webp` through pinned RNS
`rngit.media`, including six input formats, two oversized aspect ratios and
malformed-input rejection. Fixture records primary/raw-source hashes, backend
version and executable hash. Public generated inputs/outputs are not copied
upstream assets. Test-only Pillow12.2.0 and imageio-ffmpeg0.6.0 provision the
isolated reference tools; production has no Python dependency. Supported
`RNGIT_MEDIA_BACKEND=ffmpeg`/`NOMADNET_IMAGE_BACKEND=ffmpeg` select the actual
reference backend without a source shim.

Exact exercised external binaries:

- Windows `artifacts\builds\nomadnet-reference-tools\ffmpeg.exe`, FFmpeg
  `7.1-essentials_build-www.gyan.dev`;
  SHA-256 `2ce797a0f88d7f067180338fb227f7b1928ea727bd9a4d7a1d022f7c52af71a3`.
- Linux `/home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/builds/nomadnet-reference-tools/ffmpeg`,
  FFmpeg `7.0.2-static https://johnvansickle.com/ffmpeg/`;
  SHA-256 `e7e7fb30477f717e6f55f9180a70386c62677ef8a4d4d1a5d948f4098aa3eb99`.

The saved ignored scripts freeze managed DLL, native binary and reference/tool
selection and reject unexpected skips/failures:

```powershell
& artifacts\tests\nomadnet-images-win-acceptance.ps1
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-images-linux-acceptance.ps1
```

The Linux script selects native-filesystem work. An initial RRC regression
attempt using WSL's mounted Windows filesystem failed because the primary
treated a static form as executable; choosing the required Linux filesystem
resolved it without altering assertions or timeouts. Scripts for older activity
binaries below are archived provenance, not recipes for today's expanded tests.

The integrated image checkpoint passes `dotnet build
DotReticulum.Ecosystem.slnx -c Release --artifacts-path
artifacts\builds\nomadnet-images-solution -v:q` with **zero warnings/errors**;
corresponding `dotnet test ... --no-build -v:q` passes **457 tests,
78 explicit opt-in/platform skips, zero failures** across five projects.

### RRC activity evidence

Strict NativeAOT publish with `--artifacts-path artifacts\builds\nomadnet-activity-win`
(`win-x64`) and `artifacts/builds/nomadnet-activity-linux` (`linux-x64`) passed.
Windows scoped tests passed **52**, with two explicit Linux-only skips; Linux
passed **54**, no skips. `Invoke-Pester -Path tests\Pester\NomadNet.Tests.ps1
-Tag Native -Output Detailed -PassThru` passed **4/4 on each host**. Selected binaries:

- Windows `artifacts\builds\nomadnet-activity-win\publish\NomadNet\release_win-x64\nomadnet.exe`;
  SHA-256 `2abbf7314e77ced8e7da707c4a72c35e4911a3a9e5cc9779dc81367fe4e298ee`.
- Linux `artifacts/builds/nomadnet-activity-linux/publish/NomadNet/release_linux-x64/nomadnet`;
  SHA-256 `881c1e893e4f4f8f02d19eb3a4fabbd34f6f2526911c72853bccf51f478b3a16`.

The saved ignored activity acceptance script records that earlier selected
binary's Native4/4 and actual RRC/form1/1 (17.79 seconds). Use the current image
script above for current expanded tests, not that older binary.

The unchanged primary `f6b5604` client and supplemental actual `rrcd` `f6d7e9d`
hub run as separate Python processes, with the hub's RNS node also forwarding
between clients. Automatic `/who` learns Python's nickname without any manual
roster query; active-room messages stay read, inactive mentions set both flags,
manual read clears them, and retention/automatic policies survive native restart.
The RRC vector generator additionally executes primary activity/retention and
automatic-directory handlers; only disk and transport boundaries are replaced
by observations, disclosed in the fixture. These vectors are not live UI proof.

Select exact native binaries with `DOTRETICULUM_NOMADNET_BINARY`; do not infer
that a binary on PATH is the one just built. Build outputs below are isolated
under `artifacts\builds`; process evidence/work is under `artifacts\tests`.

- `dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release`:
  managed unit/integration acceptance, including malformed paths, identity
  authorization, cache expiry/no-cache/restart, limits and settings migration.
  Linux additionally exercises executable handlers, cancellation and symlink
  rejection; those cases explicitly skip on Windows.
- `GenerateNomadNetVectors.py --output tests\NomadNet.Tests\NomadNetVectors.json`
  executes the primary attachment helpers and Micron `make_output`. Fixture
  records revision, source SHA-256 and exact methods. A configuration-only
  uninitialised upstream app supplies dark theme, real Urwid screen and
  256-colour settings for parser execution; no parser method is replaced.
  This is **not** full UI or live-node evidence.
- The same generator executes real Urwid field parsing and
  `Browser.handle_link`; only its `retrieve_url` I/O boundary is captured to
  observe generated request dictionaries. This disclosed generator seam does
  not replace form logic and is not the live executable-page test.
- `GenerateNomadNetRrcVectors.py tests\NomadNet.Tests\NomadNetRrcVectors.json`
  executes primary `RRC._make_envelope` and vendored CBOR encode/decode for
  twelve packet types, recording full revision and raw source SHA-256
  (checkout line endings affect raw-file hashes). C# byte-for-byte codec
  round trips, malformed/deep/duplicate data, UTF-8 limits, resource integrity,
  echo suppression, bounded state and disposal-cancelled path lookup pass.
- `GenerateNomadNetPropagationVectors.py tests\NomadNet.Tests\NomadNetPropagationVectors.json`
  executes the primary application's selection, sync initiation and conversation
  fallback methods. Only directory/path lookup, persistence and outbound I/O
  boundaries are observed/faked; the actual policy methods are not replaced.
  The fixture records the revision and executed raw-file SHA-256 values.
- Windows NativeAOT:
  `dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64
  --artifacts-path artifacts\builds\nomadnet-functional-win
  -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true`.
- Linux NativeAOT: same command with `linux-x64` and
  `artifacts/builds/nomadnet-functional-linux`, using WSL .NET SDK 10.0.403.
  Both publish checks pass with trim/AOT warnings treated as errors.
- `Invoke-Pester -Path tests\Pester\NomadNet.Tests.ps1 -Tag Native` tests the
  selected executable: help/version/init/status; terminal shutdown/identity
  restart; hosted pages, cache/reload, 2.2 MB Resource downloads/attachments,
  paper ingestion/duplicate detection, propagation selection/send/retrieval
  and empty repeat sync, then identity/history restart.
- `Invoke-Pester -Path tests\Pester\NomadNet.Tests.ps1 -Tag Live` requires
  `DOTRETICULUM_NOMADNET_LIVE=1` for the Python LXMF endpoint case;
  `DOTRETICULUM_NOMADNET_NODE_LIVE=1` enables the Linux primary app case.
  Set `DOTRETICULUM_PYTHON`, `DOTRETICULUM_UPSTREAM`,
  `DOTRETICULUM_LXMF_UPSTREAM`, `DOTRETICULUM_NOMADNET_SOURCE` and the selected
  binary. Source revision, clean tree, actual import paths and pinned reference
  fingerprints are checked. The adapters call real upstream app/router/link
  APIs, not replacement handlers.
- The primary application case tests pages/files both serving directions,
  2.2 MB files and attachments both ways (SHA-256 checked), paper both ways,
  bidirectional direct text and persistent identity/history across restart.
  Upstream `enable_node=yes`, `max_accepted_size=4000` KB and native
  `deliveryTransferLimitBytes=4000000` are explicit non-default settings.
  Reverse hosted requests use the real upstream app identity and RNS request
  APIs, not the upstream interactive browser UI.
- `DOTRETICULUM_NOMADNET_TRANSPORT=direct|python|native` selects that same
  primary-app test topology. `python` inserts an unmodified RNS transport;
  `native` inserts the selected `nomadnet --daemon` with transport enabled.
  The app processes are leaves on separate TCP interfaces through the middle
  node (two Reticulum hops). Linux test work must use a native Linux filesystem,
  selected with `DOTRETICULUM_NOMADNET_WORK`; WSL mount executable-bit reporting
  is not an executable-page test.
- `Invoke-Pester -Path tests\Pester\NomadNet.Tests.ps1 -Tag Propagation` is
  opt-in with `DOTRETICULUM_NOMADNET_PROPAGATION_LIVE=1` on Linux. It uses the
  same explicit Python/RNS/LXMF/primary-source/binary variables. Other hosts
  explicitly skip rather than imply actual-primary-app coverage.

### Recorded acceptance

- Windows scoped managed: **48 passed, 2 explicit Linux-only skips**.
- Linux scoped managed: **50 passed, 0 skipped** (native Linux filesystem artifacts).
- Windows and Linux selected NativeAOT binaries: **4/4 Native cases each**.
- Windows selected NativeAOT against Python LXMF: **1 passed**; the explicitly
  Linux-only primary-app case **skipped**.
- Linux primary-app direct, Python transport and native transport selections:
  **1/1 primary-app case passed per topology**, including 2.2 MB transfers,
  paper both directions and restart. In the native topology the middle process
  is the selected `nomadnet --daemon` with transport enabled; it is not an
  inferred hardware or `rnsd` packaging claim.
- Full Windows solution at this integrated source state:
  `dotnet build DotReticulum.Ecosystem.slnx -c Release
  --artifacts-path artifacts\builds\nomadnet-propagation-baseline` —
  **0 warnings/errors**; corresponding `dotnet test ... --no-build` —
  **332 passed, 59 skipped, 0 failed** across five projects.

The harness waits for durable direct `delivered` state before shutting down
its sending process. An application race where stale callbacks could overwrite
terminal delivery status was fixed with atomic status updates; a regression
test preserves terminal states. Test work directories are isolated across hosts.

#### RRC and executable-form application acceptance

The supplemental hub is the upstream-linked canonical GitHub repository
`https://github.com/kc1awv/rrcd`, version **0.3.2**, pinned to
`f6d7e9d72bf83c70d7a9373ae8c5edaa052a7bf2`. It is not a replacement for the
primary NomadNet pin. `nomadnet_rrc_hub.py` verifies clean revision/import and
calls the unmodified `HubService`; the existing app adapter calls the actual
primary `NomadNetworkApp.rrc` APIs. No RRC handler is replaced. Declared missing
`cbor2` and `tomlkit` dependencies were installed into the isolated reference
venv; the pinned RNS/LXMF source selection remains verified.

`Invoke-Pester -Path tests\Pester\NomadNet.Tests.ps1 -Tag Rrc`:
**1 passed, 0 skipped** on Linux selected NativeAOT. Set
`DOTRETICULUM_NOMADNET_RRC_LIVE=1`,
`DOTRETICULUM_NOMADNET_RRCD_SOURCE` and the existing primary-source, Python,
RNS, LXMF and exact binary variables. The topology has native and actual
Python app clients attached to a real Python rrcd hub. Group chat is directly
to the hub; the Python app's executable page is reached across the hub's
enabled RNS transport (two hops). Tests exchange messages/actions both ways,
receive a 2 KiB Resource MOTD, observe `/who` and ping, reject over-limit text,
reconnect a link, restart the native client with durable room/history/nick,
restart the hub with retained identity, and verify automatic client recovery.
The native browser edits text/checkbox/radio fields and submits exact
`field_*`/`var_*` values to real Python executable form and partial pages.
This does not claim upstream Urwid interaction, Windows RRC live acceptance,
large-file chat or packet-loss recovery. Current browser/image acceptance above
additionally exercises automatic partial composition.

Selected application binaries (including rerun RRC/form acceptance):

- Windows: `artifacts\builds\nomadnet-propagation-win\publish\NomadNet\release_win-x64\nomadnet.exe`;
  SHA-256 `c05b6ee01cb771aaa201270c7d8b9fa4ec10069d43836a75ef84401c9cd1026b`.
- Linux: `artifacts\builds\nomadnet-propagation-linux\publish\NomadNet\release_linux-x64\nomadnet`;
  SHA-256 `5d3054b81e9033919a7a02808f523959e64ddc6b7a0921079d7893e78da2d83f`.

Both use the publish commands above with their `nomadnet-propagation-*` artifacts paths,
trim/AOT warnings as errors. Windows and Linux selected Native cases pass **4/4 each**.
The propagation slice's Linux SDK is 10.0.402; Windows uses 10.0.403.

The actual primary-app adapter reads bounded command lines with `os.read`,
not Python's buffered stdin iterator. This avoids an inherited buffered-reader
lock when the unmodified upstream stamp manager forks children. Only adapter
command transport changes; application, router and stamp code remain unmodified.
The mixed propagation harness sets Python 3.14's standard `PYTHON_CPU_COUNT=2`
runtime option to bound worker count, without replacing `cpu_count` or changing
stamp cost. Primary and native propagation costs remain the default 16.

#### Mixed propagation, restart and impaired TCP acceptance

The selected Linux binary passes the **1/1 Propagation case**, including:

- Four actual application processes: Python PN with RNS transport/server,
  native PN, primary Python client (node enabled, propagation hosting disabled)
  and native client. Clients use separate TCP interfaces; the native client
  passes through a bounded test-only C# HDLC impairment proxy.
- Primary client retrieval from native PN across the Python transport after
  native PN restart; native client retrieval from primary Python PN after its
  restart. Identities remain stable. An empty repeat sync in each direction
  verifies retrieval acknowledgment/deletion. These are client retrieval flows,
  not a claim of propagation-node peer synchronization.
- Trusted node autoselection maps the primary node identity to its distinct
  propagation destination. Manual override, cancellation of a pending unknown
  path and subsequent recovery pass. A real scheduled attempt downloads one
  message with a one-minute interval/one-message limit; it does not invoke a
  synthetic timer callback.
- A SHA-256-checked **2,200,000-byte** native-to-primary file attachment with
  **one actual Resource-part drop** and **5 ms delay per forwarded HDLC frame**;
  final direct `delivered` state and reverse application text after impairment.
  The recorded run forwarded **302 frames / 2,242,737 encoded bytes** overall.
  Frame count is observed, not inferred from a fixed MTU: these links negotiate
  larger frames. Reverse multi-megabyte impairment and burst/random loss are
  not claimed.
- Primary client exit, actual native direct failure and automatic one-time
  propagation fallback, then primary client restart and retrieval of that
  message. Node acceptance (`sent`) is checked separately from final primary
  reception. No artificial router failure state or shortened retry policy is
  injected.

The thin adapter uses actual `NomadNetworkApp` selection/sync and registered
delivery/persistence callbacks. Origination calls that app's actual LXMF router;
this is not an automated Urwid composition/UI test. Graceful adapter shutdown
invokes the real application, LXMF and RNS exit handlers to persist node state.
Non-default peer settings are `enable_node=yes`, PN `disable_propagation=no`,
native `enablePropagation=true`, and 4000 KB/4,000,000-byte delivery limits.
The scheduled native client uses a one-minute/one-message sync configuration.
No reference source or stamp code is patched.

For the provisioned WSL environment, complete selected-binary reproduction
commands are retained as ignored operational scripts:

```powershell
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-propagation-acceptance.ps1
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-propagation-regression.ps1
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-propagation-primary-regression.ps1
```

Results: propagation **1/1**, scoped Linux managed **50/50**, Native **4/4**,
RRC/form **1/1**, and primary-app direct/Python-hop/native-hop **1/1 each**.
The scripts select source
`/home/dennis/dotreticulum-ecosystem-acfd3d43/artifacts/builds/nomadnet-primary-reference`,
venv `/home/dennis/dotreticulum-ecosystem-acfd3d43/reference-venv/bin/python`,
and its adjacent `rns`/`lxmf` pinned checkouts; they reject failed or missing
expected passes. They are environment-specific conveniences, not distributed
test prerequisites; the committed Pester cases and variables define acceptance.

RF, physical audio/printers, macOS and other operating systems remain
**unverified**. No new DotReticulum API gap was needed for this slice.
Remaining RRC behaviors, Kitty presentation,
configuration/import parity and additional propagation-node peering acceptance remain
required work rather than a claim of completed NomadNet parity.
### Primary editor persisted-state import (C100)

The existing offline `--import-profile <stopped-primary> --config <absent-native>
--rnsconfig <selected-RNS>` transaction now translates primary
`editor_state.json` into the existing original editor's schema1 state.
NomadNet1.4.4 `f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2`,
`ui/textui/Editor.py:PageEditorDisplay.__init__/load_state/save_state/start`
is the authority. Its persisted fields are exactly `filter_mu`, `expanded`,
`pane_visible`, and `last_file`. **Primary cursor, focus, dirty contents and
source/preview scroll offsets are not persisted**; no such state is invented.
This section does not expand or replace the accepted C97/C99 contracts.

| Primary field / input | Native disposition |
|---|---|
| `filter_mu` | Activates the existing case-sensitive `.mu` tree filter. Absent is false. |
| `expanded` | Absolute paths under this source profile's owned `storage/pages` and `storage/files` become bounded native relative expansion keys. Absent retains both expanded roots; an empty list remains empty. Existing referenced empty folders are retained. Missing folders retain inert keys with a warning. |
| `pane_visible` | Named files/editor/preview booleans map to the existing visible-pane list, in primary order. Omitted entries default true. All false uses primary `visible_panes()`'s editor-only fallback. Native persistence records that effective pane, rather than three unusable false flags. |
| `last_file` | Owned saved document selection maps to native scope/path and opens on the first original editor command. External/custom-root/relative/foreign-platform paths are explicitly not rebound by suffix. Missing documents are reported in the import report; activation logs the missing selection and remains usable without fabricating a buffer. |
| No state file | Explicit report warning; native defaults remain. No authoring files are activated in this case. |
| Unknown top-level fields or pane names | Named, bounded/sanitized warnings; original JSON retained only in the inert archive. No guessed preference or scroll setting is applied. |
| Malformed JSON, duplicate known/root keys, nonboolean known preferences, invalid paths or bounds | Entire profile transaction fails. Unlike primary's permissive `bool()` conversion and exception-swallowing partial load, migration rejects ambiguous/partially corrupt input rather than silently changing native state. |

When state is present, the profile's supported nonhidden editor documents in
owned `storage/pages` and `storage/files` migrate with exact bytes and existing
`.allowed` sidecars. This is stopped-profile user content, **not copied upstream
guide/example/UI assets**. Native-reserved hidden/device/invalid paths and orphan
policies remain explicitly not imported; a selected invalid document fails.
Existing selected document permissions are never loosened; executable pages
remain disabled. Unix source owner executable bits are preserved for the existing
source-only preview policy, but this slice's native Unix execution is unverified.
Configured external page/file roots remain unactivated under the existing
profile-import safety policy, not silently redirected to a local suffix match.

Bounds are32 KiB JSON (including whitespace), nesting8,256 expanded input paths,
2048 characters per original absolute path,512 characters per mapped native
relative path,16 folder levels,2048 documents,1,000,000 bytes per document/sidecar
and16 MiB combined documents/sidecars, inside the existing whole-profile limits.
The existing bounded tree renderer can additionally reject an overlarge displayed
tree rather than truncate it. Duplicate expansion paths collapse as the primary
set does. Identity, histories, settings, router/RRC state and independent native
editor ownership keep their existing owners: an existing target is never merged
or replaced, and activation never replaces an already open clean or dirty editor.
Draft text is never serialized into imported editor state.

All changes stage under the existing private absent-target transaction, using
the whole-profile immutable-source snapshots and per-file hash checks. Cancellation,
read/validation/write errors or target conflicts remove staging without exposing
a partial target. The original state JSON is byte-identical
`import-archive/editor_state.json.bin`. Native editor operations subsequently own
`editor_state.json`, saved documents and ordinary restart recovery; no new
background importer or runtime ownership path is added.

Evidence and reproducible acceptance:

- `tests/Interop/GenerateNomadNetEditorStateImportVectors.py` executes actual
  unmodified `PageEditorDisplay` constructors, `start`, `load_state`, `save_state`,
  actual Browser/Urwid widgets, and the actual peer-settings persistence API on
  synthetic files. The generated fixture records canonical source-file SHA-256s;
  observation receipts record actual imported paths and on-disk hashes.
  Headless palette/alarm owners are disclosed: this is **not the full application
  constructor or interactive primary GUI**. The previously disclosed newer
  primary dependency-minimum discrepancy remains; the reference pins are unchanged.
- T730–740/T748 cover reference vectors, strict malformed/duplicate/type/depth/
  path/count/byte limits, atomicity, source immutability, cancellation, locked reads,
  concurrent import, exact permission retention, current clean/dirty ownership,
  absent/missing/external selections, unknown fields and restart.
- T741–742 exercise the selected Windows native executable through actual ConPTY:
  profile import, activated selection/preferences/tree, editing/saving/restart,
  dirty native-configuration ownership and failed-control recovery.
- T743–747 are process-level Pester gates for byte-identical actual-primary
  regeneration, native import/editor/restart, malformed/oversize/duplicate/
  unsafe-state atomic refusal and recovery, absent/missing/unknown disposition,
  existing-target ownership and Windows junction refusal.
- Strict win-x64 publish and receipts live under
  `artifacts\tests\nomad-editor-state-import`; build outputs are separately
  `artifacts\builds\nomad-editor-state-import`.
  `tests\Pester\Invoke-NomadNetEditorStateImportAcceptance.ps1` is the frozen
  fresh-child replay operator. Its manifest checks native/managed bytes, the
  exact production and acceptance input inventories, build HEAD/version evidence,
  unchanged stack/reference pins and before/after hashes. Parent-owned
  README/catalog/global attributes and unrelated Sideband files are not frozen.
  Use `-Candidate artifacts\tests\nomad-editor-state-import\candidate.json
  -OutputDirectory artifacts\tests\nomad-editor-state-import\parent-fresh-UNIQUE`.

Parent-independent frozen acceptance passes **441 managed cases/26 explicit skips
and5 Pester cases/zero failures/skips**:

```powershell
pwsh -NoProfile -File tests\Pester\Invoke-NomadNetEditorStateImportAcceptance.ps1 -Candidate artifacts\tests\nomad-editor-state-import\candidate.json -OutputDirectory artifacts\tests\nomad-editor-state-import\parent-independent
```

The exact native SHA-256 is
`57de210c177c2f4dd9aa010efb1b122a869137b6d832b53d14eb4e7859427e69`,
built at `c37c95d65a0a805ddb896ad8c1dcf3127ef5f1c3` with SDK10.0.403/
NativeAOT compiler10.0.14 and stack `4989f050aed865d16ed9f1460071c1a63cd4c7e9`.
The receipt is `parent-independent\child\result.json`. The separate handoff-file
SHA-256 is not the executable hash. No post-integration rebuild is inferred.

Windows software acceptance is the selected platform. Linux native replay is
explicitly blocked by the existing WSL EIO/unavailable filesystem; no WSL repair,
full primary GUI, physical hardware, new wire interoperability or full NomadNet
UI parity is claimed.
