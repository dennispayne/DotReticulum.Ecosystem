# NomadNet application reference

Experimental, independently authored .NET terminal application targeting primary
NomadNet **1.4.4**, not an official implementation or a full port. The application
reuses `ReticulumNode`, `ClientLinks` and `LxmRouter`; it does not duplicate LXMF,
paper encryption, propagation, Reticulum links or Resource protocols.
**Required software remains missing**, including remaining RRC presentation,
inline media integration and configuration/UI parity. These are implementation gaps, not
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
| Entry point, identity, delivery; `nomadnet.py`, `NomadNetworkApp.py` | **partial**: persistent identity and LXMF destination; `--config`, `--rnsconfig`, `--version`, `--daemon`, `--console`, `--textui` accepted. Daemon ignores terminal input and serves until cancellation. `--console` selects the existing console logging; `--textui` selects the original command loop, not Urwid. Extensions: `--help`, `--init`, `--status`, explicit non-overwriting core `--import-config`. |
| Direct/opportunistic conversations; `Conversation.py` | **partial**: direct text, durable inbound/outbound history, displayed delivery state, per-peer direct/propagated/opportunistic preference, unread/failure counters, explicit viewed/read state, latest-activity listing, deletion/failed purge and plain-text export. Bounded outbound supervision retries failed direct delivery once through a selected propagation node, preserving the LXMF hash, timestamp, binary content and fields. Cancellation, rejection and preparation errors do not trigger fallback. Native opportunistic text is received by the actual primary; direct reply/viewed state and both applications' read counters are exercised. Upstream `.index`/conversation import and full conversation UI remain missing. |
| File/image/audio attachments; `ConversationMessage` helpers | **partial**: `attach`, `image`, `audio`; bounded extraction of LXMF fields 0x05/0x06/0x07 and safe display names; durable payloads outside history JSON. File attachments live-verified both ways, including 2,200,000 bytes. Encoded image files can be explicitly converted with the local image adapter; automatic conversation-image display remains missing. Audio is stored/sent as encoded bytes. The pinned conversation helpers save encoded audio; an upstream recording/playback/transcoding composer was not found there, and no such native codec/device capability is claimed. |
| Paper; `Conversation.paper_output`, `LXMessage` | **partial**: `paper` generates encrypted `lxm://` URI through LXMF; `ingest` uses the router's duplicate-aware ingestion. Actual primary app ingest/generation tested both ways. QR image generation, QR scan and printer commands are missing. |
| Propagation selection and retrieval; `NomadNetworkApp.py`, `Conversation.py`, `Directory.py` | **partial, live-verified core**: persisted manual/trusted automatic selection, explicit propagated send, limited/manual/scheduled sync, asynchronous status/cancellation and direct-failure fallback; optional hosted propagation node using `LxmRouter`. Actual primary Python client retrieves from native PN after restart/deletes messages; native client retrieves from actual primary Python PN after restart/deletes messages. Scheduled retrieval and direct-failure fallback after peer exit are exercised. Propagation-node peering and broader configuration remain unverified/incomplete at the application layer. |
| Directory and announces; `Directory.py`, `Conversation.received_announce` | **partial**: bounded known-destination snapshot and decoded names; persisted trusted/untrusted/unknown/warning contact classification, exact primary saved-name collision policy, independent blacklist applied to the existing LXMF router and filtered application peer view, delivery preference and local notes. Saved-node listing/opening, primary rank/trust/name ordering, explicit browser identification consent and existing associated-node trust updates are operational. Explicit trusted node identities and validated active propagation-node classification drive automatic selection separately. Complete announce-stream/node-directory presentation and upstream directory import remain missing. |
| Hosted pages; `Node.register_pages`, `serve_page`, `announce` | **partial**: `nomadnetwork.node`, UTF-8 name app-data, default index, nested static pages, configurable six-second startup announce and periodic node announce; independent configurable page/file rescan intervals, explicit refresh and bounded handler inspection. New/deleted paths update handlers. `.allowed` identity lists enforced. Node announces chain the existing hosted propagation announce rather than duplicate its protocol. Source-specific counters and upstream default assets remain missing. |
| Hosted files; `Node.register_files`, `serve_file` | **implemented bounded subset**: `/file/...` uses the existing metadata-bearing Resource response (`name` UTF-8 bytes), up to 16 MiB. No hidden or `.allowed` files are advertised. Both serving directions verified with 2.2 MB files; streaming beyond that bound is not supported. |
| Executable pages and executable `.allowed`; `Node.serve_page`, `request_allowed` | **partial**: Linux only, explicit opt-in, direct executable (no shell interpolation), `PATH`, `link_id`, `remote_identity`, `field_*`, `var_*` environment. Bounded output, 15-second deadline, cancellation kills child tree. OS-process tests cover native execution; selected Linux native browser also submits real forms to unmodified Python executable pages across a Python transport. Python-to-native executable-form browser acceptance remains unverified. Windows intentionally does not execute pages, matching upstream page handling; Windows executable authorization generators are not implemented. |
| Media; `Node.serve_media`, `convert_media_to_webp` | **partial, live-verified core**: `/media` dictionary requires `path` and `key`; jailed, authorised native `.webp` responses and PNG/JPEG/BMP/GIF/TIFF conversion through configured external FFmpeg. Primary quality85/dimension1200 and content-hash cache naming, 256-file/192-MiB quota, corruption regeneration and restart/cache-only serving are exercised. `show-media` decodes browser images to ASCII or explicit ANSI true-colour half-blocks. Actual primary `ImageData` decodes native-served images and native previews primary-served images on Windows/Linux. Animated/multipage, alpha/EXIF behavior, automatic inline loading and Kitty graphics remain unverified/missing. |
| Browser; `ui/textui/Browser.py`, `FileBrowser.py` | **partial**: bounded page/file/media retrieval with explicit identification consent, links/bookmarks, reload, session navigation history with back/forward and submitted-field replay, durable bounded cache with upstream `#!c=<seconds>` and 12-hour default. Editable forms/variables, anchors/folds, automatic initial/periodic partial retrieval and in-place textual composition with actionable child fields/links/media; forced `p:<id>` updates use current combined fields. Navigation/disposal cancels and joins updates. Submitted and partial requests bypass cache. Explicit image previews work; automatic inline image widgets and interactive file browser remain missing. |
| Micron; `ui/textui/MicronParser.py` | **partial**: textual headings, literal blocks/comments/dividers, true backtick-prefixed links, escaping; formatting/color controls stripped rather than leaked. Primary-executed inline, field, submission and heading-slug vectors. Text/password/multiline fields, grouped checkbox/radio selection, `*`/selected-field submission, variable assignment, heading/explicit anchors and collapsible sections are functional in the terminal. Table rows remain readable text, not upstream column layout. Partial/media descriptors are bounded and actionable. No pixel-identical style/layout or visual image widget claim. |
| RRC; `RRC.py`, `ui/textui/Channels.py` | **partial, live-verified core**: identified `rrc.hub` link, CBOR v1 HELLO/WELCOME and negotiated limits; join/part; messages/actions/notices/errors; nicknames, bounded full-hash/prefix membership with promotion and mentions; ping/pong; bounded hashed notice/MOTD resources; `/list`/`/who` and parsed directory state including fragmented notices; persisted automatic list/who policy with silent replies; session-local active/read/unread/mention state; bounded per-room retention; manual reconnect and opt-in exponential reconnect; durable hub nick/rooms/history. Actual primary client and real rrcd exchange messages/actions both ways through a Python transport, including hub/client restarts, automatic roster learning and active/unread transitions. Markdown/Micron message rendering and complete Channels UI remain missing. RRC is its own upstream CBOR-over-Link protocol, not LXMF multicast. |
| UI; `ui/TextUI.py`, `ui/textui/*`, guides/examples | **partial**: original standard-console command loop. Urwid menus, editor, notifications, clipboard, utilities and guide workflows are missing. GraphicalUI/WebUI abort upstream: **upstream-placeholder**, not an implemented or required invented GUI. |
| Persistence/recovery; `NomadNetworkApp.py`, `Directory.py`, `Conversation.py`, `RRC.py` | **partial**: versioned JSON settings/state with atomic replacement, bounded contact policy/ignore state/messages/unread/failure alerts/bookmarks, LXMF storage, attachment byte files, page/conversion caches and separate bounded CBOR RRC hub profiles/history; application identity/history/policies/caches and RRC nick/rooms/history survive restart. Explicit core INI import stages a new native profile and reports every unsupported key. Complete ConfigObj compatibility, directory/peer-settings/conversation/RRC migration, peer-settings backup recovery and full restart policy remain missing. |

### Configuration audit

The running .NET application uses `settings.json`, not an automatically loaded
upstream ConfigObj/INI. Existing schema-1 files without new properties still
load with defaults. The explicit core importer below translates only named
contracts and reports unsupported keys; it is not complete configuration parity.

| Native setting | Default / behavior |
|---|---|
| `displayName` | `NomadNet user`; 1–80 printable characters |
| `enableNode`, `nodeName` | false; absent name derives from display name |
| `nodeAnnounceIntervalMinutes` | 360; validated 1–10080 |
| `nodeAnnounceAtStart` | true for legacy native profiles; false when absent from imported primary node configuration; controls the six-second hosted node announce and its additional scheduled propagation announcement |
| `nodePageRefreshIntervalMinutes`, `nodeFileRefreshIntervalMinutes` | native defaults 5/60 minutes preserve earlier five-second scans; imported primary defaults0 disable rescans; independently validated finite0–10080 minutes |
| `enableExecutablePages` | false; explicit Linux execution/authorization-generator opt-in |
| `mediaConverter` | `ffmpeg`; local executable name or absolute path; no shell, 1–1024 printable characters; required only for conversion/preview, not cached or native WebP serving |
| `inboundStampCost`, `acceptInvalidStamps` | null / false; stamp cost null or 1–254; existing LXMF enforces required stamps unless explicitly accepting invalid stamps |
| `pagesPath`, `filesPath`, `downloadsPath` | null uses native `storage\pages`, `storage\files`, `downloads`; explicit paths override those roots; remote names still cannot escape selected roots |
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
  implemented workflows above apply. Downloads path, required cost, invalid-stamp
  acceptance and transfer limit are operational/imported; other application
  options are **missing**.
- `[node]`: `enable_node`, `node_name`, `disable_propagation`, `announce_at_start`,
  `announce_interval`, `pages_path`, `files_path`, `page_refresh_interval`,
  `file_refresh_interval`, `propagation_cost`, `max_transfer_size`, `max_sync_size`,
  `prioritise_destinations`, `static_peers`, `max_peers`, `message_storage_limit`
  — **partial** enable/name/announce interval/manual propagation and custom
  page/file roots, startup announce and independent rescan intervals; the other knobs are
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

The seventeen translated keys are `[client] periodic_lxmf_sync`,
`lxmf_sync_interval`, `lxmf_sync_limit`, `try_propagation_on_send_fail`,
`required_stamp_cost`, `accept_invalid_stamps`, `max_accepted_size`,
`downloads_path`; `[node] enable_node`, `disable_propagation`, `node_name`,
`announce_interval`, `pages_path`, `files_path`, `announce_at_start`,
`page_refresh_interval`, `file_refresh_interval`. Every other supplied key is
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

### Images and external codecs

`media <number> <name.webp>` downloads an image descriptor into `downloads`.
`show-media <number> [color]` downloads/decodes that descriptor for bounded ASCII
or explicit ANSI true-colour preview; this is not Kitty graphics or a
pixel-identical primary widget. `image-convert <file> <name.webp>` explicitly
converts a local encoded image. Automatic inline loading remains required work.

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

## Validation and reproduction

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
artifact records `hostPolicy=true`. Client announce settings, logging, node
counters and other unsupported configuration remain required gaps.

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
This does not implement the remaining file-browser, inline-image or complete
terminal UI workflows.

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
Remaining RRC behaviors, automatic inline image integration,
configuration/import parity and propagation-node peering acceptance remain
required work rather than a claim of completed NomadNet parity.
