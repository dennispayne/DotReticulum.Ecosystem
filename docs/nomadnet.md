# NomadNet application reference

Experimental, independently authored .NET terminal application targeting primary
NomadNet **1.4.4**, not an official implementation or a full port. The application
reuses `ReticulumNode`, `ClientLinks` and `LxmRouter`; it does not duplicate LXMF,
paper encryption, propagation, Reticulum links or Resource protocols.
**Required software remains missing**, especially RRC group chat, complete Micron
interaction and configuration/UI parity. These are implementation gaps, not
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
| Direct/opportunistic conversations; `Conversation.py` | **partial**: direct text, durable inbound/outbound history, displayed delivery state, contacts, names; live direct text both ways. No application selector for opportunistic delivery, retry-as-propagated policy, read/unread state, conversation deletion/export or upstream `.index` compatibility. |
| File/image/audio attachments; `ConversationMessage` helpers | **partial**: `attach`, `image`, `audio`; bounded extraction of LXMF fields 0x05/0x06/0x07 and safe display names; durable payloads outside history JSON. File attachments live-verified both ways, including 2,200,000 bytes. Images/audio are stored/sent as encoded bytes, not decoded, played, recorded, transcoded or visually displayed; those application workflows are missing. |
| Paper; `Conversation.paper_output`, `LXMessage` | **partial**: `paper` generates encrypted `lxm://` URI through LXMF; `ingest` uses the router's duplicate-aware ingestion. Actual primary app ingest/generation tested both ways. QR image generation, QR scan and printer commands are missing. |
| Propagation selection and retrieval; `NomadNetworkApp.py`, `Conversation.py`, `Directory.py` | **partial**: persisted manual selection, explicit propagated send, `sync`, optional hosted propagation node using `LxmRouter`. Native process retrieval/deletion is tested separately from Python application flows. Trusted-node autoselection, scheduled sync, direct-failure fallback, sync-limit UI/cancel and app-level mixed Python propagation/peering acceptance remain missing/unverified. |
| Directory and announces; `Directory.py`, `Conversation.received_announce` | **partial**: bounded known-destination snapshot and decoded names. Trusted/untrusted/unknown warning policies, blacklist/ignore UI, node/propagation classification and upstream directory import are missing. |
| Hosted pages; `Node.register_pages`, `serve_page`, `announce` | **partial**: `nomadnetwork.node`, UTF-8 name app-data, default index, nested static pages, six-second startup announce and periodic node announce; explicit refresh and five-second directory scans. New/deleted paths update handlers. `.allowed` identity lists enforced. Source-specific counters, refresh interval settings and upstream default assets are not implemented. |
| Hosted files; `Node.register_files`, `serve_file` | **implemented bounded subset**: `/file/...` uses the existing metadata-bearing Resource response (`name` UTF-8 bytes), up to 16 MiB. No hidden or `.allowed` files are advertised. Both serving directions verified with 2.2 MB files; streaming beyond that bound is not supported. |
| Executable pages and executable `.allowed`; `Node.serve_page`, `request_allowed` | **partial**: Linux only, explicit opt-in, direct executable (no shell interpolation), `PATH`, `link_id`, `remote_identity`, `field_*`, `var_*` environment. Bounded output, 15-second deadline, cancellation kills child tree. Deterministic OS-process tests, not Python live execution acceptance. Windows intentionally does not execute pages, matching upstream page handling; Windows executable authorization generators are not implemented. |
| Media; `Node.serve_media`, `convert_media_to_webp` | **partial**: `/media` dictionary requires `path` and `key`; jailed, authorised native `.webp` metadata-bearing responses. Conversion of PNG/JPEG/BMP/GIF/TIFF to WebP, conversion cache/quality/dimension settings and image display are missing, not silently substituted. |
| Browser; `ui/textui/Browser.py`, `FileBrowser.py` | **partial**: bounded page/file retrieval, identifies requests, links/bookmarks, reload, durable bounded cache with upstream `#!c=<seconds>` and 12-hour default. Form-submission cache keys, navigation history, anchor jumps, partial updates, media requests/display and interactive file browser remain missing. |
| Micron; `ui/textui/MicronParser.py` | **partial**: textual headings, literal blocks/comments/dividers, true backtick-prefixed links, escaping; formatting/color controls stripped rather than leaked as text. Primary-executed inline vectors. Bare `[brackets]` are ordinary text. No full style/layout parity, editable fields, radio/check boxes, table layout, fold state, partial rendering or image widgets. Unsupported images/partials/forms are labelled. |
| RRC; `RRC.py`, `ui/textui/Channels.py` | **missing required software**: hub connection/HELLO/WELCOME, CBOR envelope version 1, join/part, message/action/notice, nickname/member/mention state, ping/pong, resource envelopes, `/list`/`/who`, reconnect and durable per-room history. No invented chat format or substitute LXMF multicast is supplied. |
| UI; `ui/TextUI.py`, `ui/textui/*`, guides/examples | **partial**: original standard-console command loop. Urwid menus, editor, notifications, clipboard, utilities and guide workflows are missing. GraphicalUI/WebUI abort upstream: **upstream-placeholder**, not an implemented or required invented GUI. |
| Persistence/recovery; `NomadNetworkApp.py`, `Directory.py`, `Conversation.py`, `RRC.py` | **partial**: versioned JSON settings/state with atomic replacement, bounded contacts/messages/bookmarks, LXMF storage, attachment byte files and page cache; application identity/history/cache survive restart. Upstream config/directory/peer-settings/conversation/RRC file import and migration, peer-settings backup recovery and full restart policy are missing. |

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
| `propagationNode` | null or 32-hex propagation destination; `propagation <hash|off>` persists selection |
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
  — **missing**, together with the required RRC implementation.
- `[utilities] enable_utilities`; `[printing] print_messages`, `print_command`,
  `print_from` — **missing**.

Safety differences are intentional and not upstream defaults: content/queue
bounds, no symlink/hidden/alternate-stream traversal, executable-page opt-in,
short execution deadlines, separate attachment bytes rather than embedding them
in history (256 MiB/4096-file attachment quota; exhaustion preserves text and
reports rejected attachments), and bounded cache eviction (128 pages/32 MiB). The native cache
binary layout and JSON/attachment layout are .NET-local persistence formats,
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

## Validation and reproduction

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

### Recorded acceptance

- Windows scoped managed: **24 passed, 2 explicit Linux-only skips**.
- Linux scoped managed: **26 passed, 0 skipped**.
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
  --artifacts-path artifacts\builds\nomadnet-solution-acceptance` —
  **0 warnings/errors**; corresponding `dotnet test ... --no-build` —
  **268 passed, 51 skipped, 0 failed** across five projects.

The harness waits for durable direct `delivered` state before shutting down
its sending process. An application race where stale callbacks could overwrite
terminal delivery status was fixed with atomic status updates; a regression
test preserves terminal states. Test work directories are isolated across hosts.

Packet-loss/latency impairment, RF, physical audio/printers, macOS and other
operating systems remain **unverified**. No new DotReticulum API gap was needed
for this bounded slice. Group chat, full Micron, media conversion, configuration
parity and mixed Python application-level propagation acceptance remain required
work rather than a claim of completed NomadNet parity.
