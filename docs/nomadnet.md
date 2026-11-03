# NomadNet application reference

Experimental, independently authored .NET terminal application targeting primary
NomadNet **1.4.4**, not an official implementation or a full port. The application
reuses `ReticulumNode`, `ClientLinks` and `LxmRouter`; it does not duplicate LXMF,
paper encryption, propagation, Reticulum links or Resource protocols.
**Required software remains missing**, including remaining RRC presentation
behavior, automatic Micron partial composition and configuration/UI parity. These are implementation gaps, not
hardware or optional-platform blockers. Sideband has its own implementation and
inventory in [sideband.md](sideband.md); this document does not track it.

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
| Entry point, identity, delivery; `nomadnet.py`, `NomadNetworkApp.py` | **partial**: persistent identity and LXMF destination; `--config`, `--rnsconfig`, `--version`, `--daemon`, `--console`, `--textui` accepted. Daemon ignores terminal input and serves until cancellation. `--console` selects the existing console logging; `--textui` selects the original command loop, not Urwid. Extensions: `--help`, `--init`, `--status`. |
| Direct/opportunistic conversations; `Conversation.py` | **partial**: direct text, durable inbound/outbound history, displayed delivery state, contacts, names; live direct text both ways. Bounded outbound supervision retries failed direct delivery once through a selected propagation node, preserving the LXMF hash, timestamp, binary content and fields. Cancellation, rejection and preparation errors do not trigger fallback. No application selector for opportunistic delivery, read/unread state, conversation deletion/export or upstream `.index` compatibility. |
| File/image/audio attachments; `ConversationMessage` helpers | **partial**: `attach`, `image`, `audio`; bounded extraction of LXMF fields 0x05/0x06/0x07 and safe display names; durable payloads outside history JSON. File attachments live-verified both ways, including 2,200,000 bytes. Images/audio are stored/sent as encoded bytes, not decoded, played, recorded, transcoded or visually displayed; those application workflows are missing. |
| Paper; `Conversation.paper_output`, `LXMessage` | **partial**: `paper` generates encrypted `lxm://` URI through LXMF; `ingest` uses the router's duplicate-aware ingestion. Actual primary app ingest/generation tested both ways. QR image generation, QR scan and printer commands are missing. |
| Propagation selection and retrieval; `NomadNetworkApp.py`, `Conversation.py`, `Directory.py` | **partial, live-verified core**: persisted manual/trusted automatic selection, explicit propagated send, limited/manual/scheduled sync, asynchronous status/cancellation and direct-failure fallback; optional hosted propagation node using `LxmRouter`. Actual primary Python client retrieves from native PN after restart/deletes messages; native client retrieves from actual primary Python PN after restart/deletes messages. Scheduled retrieval and direct-failure fallback after peer exit are exercised. Propagation-node peering and broader configuration remain unverified/incomplete at the application layer. |
| Directory and announces; `Directory.py`, `Conversation.received_announce` | **partial**: bounded known-destination snapshot and decoded names; explicit trusted node identities and validated active propagation-node classification for automatic selection. General trusted/untrusted/unknown warning policies, blacklist/ignore UI, full directory classification and upstream directory import are missing. |
| Hosted pages; `Node.register_pages`, `serve_page`, `announce` | **partial**: `nomadnetwork.node`, UTF-8 name app-data, default index, nested static pages, six-second startup announce and periodic node announce; explicit refresh and five-second directory scans. New/deleted paths update handlers. `.allowed` identity lists enforced. Source-specific counters, refresh interval settings and upstream default assets are not implemented. |
| Hosted files; `Node.register_files`, `serve_file` | **implemented bounded subset**: `/file/...` uses the existing metadata-bearing Resource response (`name` UTF-8 bytes), up to 16 MiB. No hidden or `.allowed` files are advertised. Both serving directions verified with 2.2 MB files; streaming beyond that bound is not supported. |
| Executable pages and executable `.allowed`; `Node.serve_page`, `request_allowed` | **partial**: Linux only, explicit opt-in, direct executable (no shell interpolation), `PATH`, `link_id`, `remote_identity`, `field_*`, `var_*` environment. Bounded output, 15-second deadline, cancellation kills child tree. OS-process tests cover native execution; selected Linux native browser also submits real forms to unmodified Python executable pages across a Python transport. Python-to-native executable-form browser acceptance remains unverified. Windows intentionally does not execute pages, matching upstream page handling; Windows executable authorization generators are not implemented. |
| Media; `Node.serve_media`, `convert_media_to_webp` | **partial**: `/media` dictionary requires `path` and `key`; jailed, authorised native `.webp` metadata-bearing responses. Conversion of PNG/JPEG/BMP/GIF/TIFF to WebP, conversion cache/quality/dimension settings and image display are missing, not silently substituted. |
| Browser; `ui/textui/Browser.py`, `FileBrowser.py` | **partial**: bounded page/file retrieval, identifies requests, links/bookmarks, reload, durable bounded cache with upstream `#!c=<seconds>` and 12-hour default. Editable form and variable submission; named anchor jumps; fold toggles; manual partial requests using current fields; explicit image-byte download. Submitted requests bypass cache, an intentional safety difference. Navigation history, automatic partial refresh/in-place widget composition, visual image decode and interactive file browser remain missing. |
| Micron; `ui/textui/MicronParser.py` | **partial**: textual headings, literal blocks/comments/dividers, true backtick-prefixed links, escaping; formatting/color controls stripped rather than leaked. Primary-executed inline, field, submission and heading-slug vectors. Text/password/multiline fields, grouped checkbox/radio selection, `*`/selected-field submission, variable assignment, heading/explicit anchors and collapsible sections are functional in the terminal. Table rows remain readable text, not upstream column layout. Partial/media descriptors are bounded and actionable. No pixel-identical style/layout or visual image widget claim. |
| RRC; `RRC.py`, `ui/textui/Channels.py` | **partial, live-verified core**: identified `rrc.hub` link, CBOR v1 HELLO/WELCOME and negotiated limits; join/part; messages/actions/notices/errors; nicknames, bounded full-hash/prefix membership with promotion and mentions; ping/pong; bounded hashed notice/MOTD resources; `/list`/`/who` and parsed directory state including fragmented notices; persisted automatic list/who policy with silent replies; session-local active/read/unread/mention state; bounded per-room retention; manual reconnect and opt-in exponential reconnect; durable hub nick/rooms/history. Actual primary client and real rrcd exchange messages/actions both ways through a Python transport, including hub/client restarts, automatic roster learning and active/unread transitions. Markdown/Micron message rendering and complete Channels UI remain missing. RRC is its own upstream CBOR-over-Link protocol, not LXMF multicast. |
| UI; `ui/TextUI.py`, `ui/textui/*`, guides/examples | **partial**: original standard-console command loop. Urwid menus, editor, notifications, clipboard, utilities and guide workflows are missing. GraphicalUI/WebUI abort upstream: **upstream-placeholder**, not an implemented or required invented GUI. |
| Persistence/recovery; `NomadNetworkApp.py`, `Directory.py`, `Conversation.py`, `RRC.py` | **partial**: versioned JSON settings/state with atomic replacement, bounded contacts/messages/bookmarks, LXMF storage, attachment byte files, page cache and separate bounded CBOR RRC hub profiles/history; application identity/history/cache and RRC nick/rooms/history survive restart. Upstream config/directory/peer-settings/conversation/RRC file import and migration, peer-settings backup recovery and full restart policy are missing. |

### Configuration audit

The .NET application uses `settings.json`, not upstream ConfigObj/INI. Existing
schema-1 files without the new properties still load with defaults. It does not
silently claim to accept upstream option names.

| Native setting | Default / behavior |
|---|---|
| `displayName` | `NomadNet user`; 1–80 printable characters |
| `enableNode`, `nodeName` | false; absent name derives from display name |
| `nodeAnnounceIntervalMinutes` | 360; validated 1–10080 |
| `enableExecutablePages` | false; explicit Linux execution/authorization-generator opt-in |
| `enablePropagation` | false; requires `enableNode`; uses existing LXMF propagation defaults |
| `propagationNode`, `usePropagation` | null / true; `propagation <hash|auto|off>` persists manual, trusted automatic or disabled client selection |
| `trustedNodes` | empty; at most 128 `nomadnetwork.node` hashes, not delivery/propagation hashes |
| `periodicLxmfSync`, `lxmfSyncIntervalMinutes`, `lxmfSyncLimit` | true / 360 / 8; interval 1–525600 minutes, limit 0–100000 (0 is unlimited); five-second scheduler after 90-second startup deferral |
| `lastLxmfSync` | absent until first attempt; persisted UTC timestamp before retrieval; failure/cancellation do not erase it |
| `tryPropagationOnSendFail` | true; one propagation attempt after failed direct delivery if a node is selected |
| `deliveryTransferLimitBytes` | 1,000,000; validated 1 KiB–64 MiB. Independent attachment extraction limit is 16 MiB. |

Upstream settings audited in `NomadNetworkApp.apply_config`/default config:

- `[logging]`: `loglevel`, `destination` — **missing** configurable destinations;
  native errors currently go to stderr.
- `[client]`: `enable_client`, `downloads_path`, `attachment_save_path`,
  `announce_at_start`, `announce_interval`, `try_propagation_on_send_fail`,
  `periodic_lxmf_sync`, `lxmf_sync_interval`, `lxmf_sync_limit`,
  `required_stamp_cost`, `accept_invalid_stamps`, `max_accepted_size`,
  `compact_announce_stream`, `notify_on_new_message`, `compose_in_markdown`,
  `user_interface` — **partial** only where the explicit native settings or
  implemented workflows above apply; other application options are **missing**.
- `[node]`: `enable_node`, `node_name`, `disable_propagation`, `announce_at_start`,
  `announce_interval`, `pages_path`, `files_path`, `page_refresh_interval`,
  `file_refresh_interval`, `propagation_cost`, `max_transfer_size`, `max_sync_size`,
  `prioritise_destinations`, `static_peers`, `max_peers`, `message_storage_limit`
  — **partial** enable/name/announce/manual propagation; the other knobs are
  **missing** at the application layer, even where LXMF has corresponding APIs.
- `[textui]`: `intro_time`, `theme`, `colormode`, `glyphs`, `image_loading`,
  `mouse_enabled`, `editor`, `hide_guide`, `sanitize_names`, `clipboard_copy`
  — **missing** upstream UI; native terminal output sanitises control characters.
- `[rrc]`: `history_per_room_cap`, `filter_loaded_history`,
  `max_accepted_resource_size`, `ephemeral_notices`, `justify_msgs`, `space_msgs`,
  `nick_colors`, `mention_color`, `color_mention_timestamps`, `nick_colors_theme`,
  `render_markdown`, `render_micron`, `show_gutters`, `enable_esoterics`
  — **partial**: `rrc history-limit` configures per-hub retention; other listed
  options remain missing as configuration surfaces. The implemented RRC core uses the
  upstream 256 KiB resource default, a local 1,000-item/2 MiB aggregate history
  bound and terminal-safe plain display; these are not configuration parity.
- `[utilities] enable_utilities`; `[printing] print_messages`, `print_command`,
  `print_from` — **missing**.

Safety differences are intentional and not upstream defaults: content/queue
bounds, no symlink/hidden/alternate-stream traversal, executable-page opt-in,
short execution deadlines, separate attachment bytes rather than embedding them
in history (256 MiB/4096-file attachment quota; exhaustion preserves text and
reports rejected attachments), and bounded cache eviction (128 pages/32 MiB). The native cache
binary layout, RRC profile layout and JSON/attachment layout are .NET-local persistence formats,
not new wire protocols. Media conversion and authorised executable-page support
are separate capabilities; one does not prove the other.

## Operation

```powershell
nomadnet --init --config <app-directory>
# Edit settings.json to enable the required node/propagation settings.
nomadnet --config <app-directory> --rnsconfig <reticulum-directory>
nomadnet --daemon --config <app-directory> --rnsconfig <reticulum-directory>
```

`help` lists the actual commands. Hosted content goes in
`storage\pages` and `storage\files`. `<path>.allowed` contains identity hashes
(not LXMF destination hashes), one 32-hex identity per line. A protected request
without an authenticated permitted identity is denied. App identity is stored
through Reticulum under the selected RNS configuration; preserve **both** app
data and RNS configuration for restart. Keep private identity material private.

Attachments are stored under `attachments\<message-hash>\<index>.bin`, with
`.name` and `.sha256` metadata. Remote names are never filesystem paths. Image
and audio commands send existing encoded bytes; there is no hardware playback,
capture or conversion claim. `paper` needs the recipient's announced identity.

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
partial text with current form values. Partial refresh intervals are parsed
but **not scheduled**, and fetched partial widgets are not merged into the
parent page. `media <number> <name.webp>` downloads bytes through `/media`;
it does not decode/display or validate image quality. Forms are limited to
128 controls and 64 KiB submitted text, independently of upstream layout sizes.

## Validation and reproduction

### Selected RRC activity acceptance

Strict NativeAOT publish with `--artifacts-path artifacts\builds\nomadnet-activity-win`
(`win-x64`) and `artifacts/builds/nomadnet-activity-linux` (`linux-x64`) passed.
Windows scoped tests passed **52**, with two explicit Linux-only skips; Linux
passed **54**, no skips. `Invoke-Pester -Path tests\Pester\NomadNet.Tests.ps1
-Tag Native -Output Detailed -PassThru` passed **4/4 on each host**. Selected binaries:

- Windows `artifacts\builds\nomadnet-activity-win\publish\NomadNet\release_win-x64\nomadnet.exe`;
  SHA-256 `2abbf7314e77ced8e7da707c4a72c35e4911a3a9e5cc9779dc81367fe4e298ee`.
- Linux `artifacts/builds/nomadnet-activity-linux/publish/NomadNet/release_linux-x64/nomadnet`;
  SHA-256 `881c1e893e4f4f8f02d19eb3a4fabbd34f6f2526911c72853bccf51f478b3a16`.

The saved ignored `artifacts\tests\nomadnet-activity-acceptance.ps1` selects the
Linux binary and exact existing reference environment, runs Linux scoped tests
on the native filesystem, Native4/4 and actual RRC/form1/1 (17.79 seconds). Run:

```powershell
wsl.exe -- env DOTNET_ROOT=/home/dennis/.dotreticulum-dotnet PATH=/home/dennis/.dotreticulum-dotnet:/usr/local/bin:/usr/bin:/bin /home/dennis/.dotreticulum-powershell/pwsh -NoLogo -NoProfile -File /mnt/c/Git/dennispayne/DotReticulum.Ecosystem/artifacts/tests/nomadnet-activity-acceptance.ps1
```

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
large-file chat, packet-loss recovery, or automatic partial composition.

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
Remaining RRC behaviors, automatic Micron partial composition, media conversion,
configuration/import parity and propagation-node peering acceptance remain
required work rather than a claim of completed NomadNet parity.
