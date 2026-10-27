# LXMF

`DotLXMF` implements part of LXMF 1.2.1 on [DotReticulum](https://github.com/dennispayne/DotReticulum). Message signing/validation, delivery proofs, proof-of-work, persistent tickets and ratchets, paper-message URIs, message containers, opportunistic/direct delivery, propagation delivery, authenticated retrieval and propagation storage exist. Healthy explicit peer synchronization is Python-live-verified in both directions. Autonomous scheduling and full failure-policy behavior do not yet have complete live evidence. A NativeAOT `lxmd` executable also exists. This is not a full-parity claim.

## Message format and API

An LXMF message contains `destination(16) || source(16) || signature(64) || msgpack([timestamp, title, content, fields[, stamp]])`. The message hash covers destination, source, and the payload without the stamp. The signature is validated during unpacking. `LxMessage` exposes generating, outbound, sent, delivered, rejected, failed, and cancelled states, plus completion tracking.

A typical application creates a router with a DotReticulum `TransportRouter` and an identity, registers its receive callback, starts it, announces its destination, and submits messages:

```csharp
var lxmf = new LxmRouter(transportRouter, identity, "dotnet");
lxmf.MessageReceived = received => Console.WriteLine(received.Message.Content);
lxmf.Start();
await lxmf.AnnounceAsync();
var message = LxMessage.Create(destinationHash, identity, "content", "title", fields: null, LxmMethod.Direct);
await lxmf.SendAsync(message);
var finalState = await message.Completion;
```

Dispose `LxmRouter` asynchronously with the transport router that owns it. `LxmRouterOptions` configures retry attempts (default 5), retry wait (10 seconds), path request wait (7 seconds), pathless tries (1), processing interval (4 seconds), inbound direct-resource transfer size (1,000,000 bytes), inbound stamp cost/enforcement, and optional opportunistic receipt timeout. `SendAsync` packs and queues the message; delivery runs in the background, so observe `State` or `Completion`.

## Security and access

Messages are signed and signature-validated. Opportunistic payloads are encrypted to the destination identity; direct delivery uses an encrypted Link. The router has no identity allow-list or application-level access-control policy for its inbound delivery destination. Configure an inbound stamp cost and keep enforcement enabled when proof-of-work gating is required; stamps do not replace peer authorization policy.

## Delivery behavior

Opportunistic delivery sends one encrypted packet and uses a delivery receipt. Its content ceiling is derived from the encrypted-packet MDU and message overhead (287 bytes for the pinned settings); larger messages requested as opportunistic are promoted to direct delivery. Direct delivery uses a Link: messages up to 319 bytes fit in one packet with the pinned settings; larger messages use a Resource. The router requests paths, retries failures, and suppresses duplicate received hashes for 180 days (upstream's `MESSAGE_EXPIRY * 6`), with a 65,536-entry bound. `StoragePath` persists this history in upstream's `local_deliveries` binary-key map before application callbacks. Incoming direct Resources are accepted only up to `DeliveryPerTransferLimit`.

`LxStamper` generates and validates 32-byte proof-of-work stamps. When a destination announces a stamp cost, outbound messages receive a stamp. A router may advertise and require an inbound stamp cost. Invalid stamps are dropped by default when enforcement is enabled; otherwise the callback receives `StampValid=false`. Ticket stamps are checked before ordinary work. `Tickets` provides issuance, renewal, expiry, grace and atomic upstream-compatible persistence; `IncludeTicket` requests automatic signed reply-ticket exchange. Keys expire after 21 days, renew inside 14 days, retain expired inbound keys for five-day cleanup grace, and suppress repeat issuance for one day after confirmed ticket delivery.

The delivery destination is `lxmf.delivery`. Announce data may carry a display name, inbound stamp cost and compression support. Advertised explicit lack of compression is honored for direct Resources. Delivery ratchets are enabled by default; `EnforceRatchets` rejects non-ratcheted opportunistic encryption. Configure `StoragePath` to persist signed upstream-compatible ratchet files in `lxmf/ratchets/<destination>.ratchets`; without a path the keys are memory-only. The router owns the ring, not the supplied identity or transport.

Direct links identify the sender; opportunistic packets are encrypted to the destination identity or announced ratchet. Message callbacks execute serially on a separate bounded receive worker; duplicate messages are suppressed even without subscribers. One observer throwing does not prevent other observers or later processing. Inbound overload is explicitly logged and dropped (default capacity 256); the bounded outbound queue rejects additions at capacity (default 256). Disposal cancels pending outbound completion tasks.

`CreateBinary` preserves arbitrary title/content bytes. For paper messages, create with `LxmMethod.Paper`, call `Pack(destinationIdentity: identity)`, then `AsUri()`. The URI is `lxm://` plus unpadded URL-safe base64 of `destination || encrypted(source || signature || payload)`, with an encrypted-size ceiling of 2210 bytes. `FromPaperUri` decrypts for the addressed identity and validates the message; an optional decryptor supports a caller-owned ratchet ring. `AsUri()` finalizes with upstream's paper state `0x05` (not a network delivery confirmation). QR image generation and router URI ingestion/duplicate signalling are not implemented.

`PackedContainer()` / `UnpackContainer()` preserve upstream's `state`, `lxmf_bytes`, `transport_encrypted`, `transport_encryption` and `method` MsgPack map. Container file ownership belongs to applications; `lxmd` writes inbox containers atomically.

## Propagation APIs

Set `OutboundPropagationNode` to an announced `lxmf.propagation` destination and send a message with `LxmMethod.Propagated`. Its completion means **Sent to the node**, not delivery to the final recipient. `RequestMessagesFromPropagationNodeAsync()` identifies this router, lists only its delivery destination's messages, decrypts them, and acknowledges deletion unless `retain: true`.

`EnablePropagationNode()` requires `StoragePath`; it creates encrypted `lxmf/messagestore` files, reloads the index on restart, validates PN stamps and enforces transfer/sync/storage limits. `RequireAuthentication` and `AllowedIdentities` restrict downloads; `FromStaticOnly` and `StaticPeers` independently restrict inbound deposits. Storage weight is `size * max(1, age_days / 4)`, reduced tenfold for `PrioritisedDestinations`; highest-weight entries are evicted first. Independently executed upstream weight/eviction vectors and an actual three-message propagation test cover priority survival. Unlike upstream's periodic over-limit cleanup, admission enforces the byte bound before writing; an incoming highest-weight message may be discarded, not reported as recipient-delivered.

`SynchronizePeerAsync()` performs `/offer`, peering-key proof-of-work, wanted-ID negotiation and encrypted Resource transfer. Persistent `LxmPeer` snapshots retain only message IDs still in the store. Default scheduling is one serialized peer sync every 24 seconds, with 720-second incremental failure backoff and 14-day discovered-peer expiry. Autopeer respects announced enabled state, hop depth and maximum accepted peering cost. Rotation runs every 1,344 seconds and preserves static peers; executed upstream cases cover lowest-acceptance selection and static protection. Incoming offers gate sequential validation and maximum inbound syncs (default 3), static peers may bypass these gates, and invalid-stamp Resources throttle identified peers for 180 seconds. These busy/failure policies still lack complete live evidence. Lazy-strategy semantics and complete metric accounting remain incomplete.

The separate `lxmf.propagation.control` destination implements `/pn/get/stats`, `/pn/peer/sync` and `/pn/peer/unpeer`. It authorizes the node's own identity plus `ControlAllowedIdentities`; being allowed to download does **not** grant control. `CompileStats()` exposes the corresponding message/peer/storage map. Native daemon control queries have actual Python interoperability; automatic scheduling and valid remote peer-sync/unpeer lifecycle flows do not yet have equivalent live coverage.

## Limitations and differences from upstream

Full propagation peer policy parity, multiple delivery identities per router, persistent outbound stamp-cost caches, router paper ingestion/QR rendering and several introspection/mutation APIs remain incomplete. Ratchet restart compatibility is not yet Python-live-verified. An `lxmd` configured with its default `delivery_transfer_max_accepted_size` of 1 KB may reject larger direct Resource deliveries; this is peer policy, not an LXMF wire-format limit. Set the daemon's acceptance limit deliberately when testing larger messages. Successful delivery to an `lxmd` destination does not establish propagation peer synchronization.

## Platform notes

LXMF message and delivery formats are not OS-specific; actual network reachability depends on the configured Reticulum interfaces and their support on the host. Managed or loopback tests do not establish platform-wide interface support.

## Verification

`tests/DotLXMF.Tests` contains executed Python vectors and local message/stamp/router/store tests. Opt-in `LiveLxmfTests.cs` includes bidirectional opportunistic, direct single-packet, large-resource, reply-ticket, propagation-client/node and explicit peer-sync round trips against Python LXMF 1.2.1. Both retrieval directions passed with deletion acknowledgments. The peer case invokes actual Python `LXMPeer.sync`, pushes an offline recipient's message to the .NET node, then pushes a Python-addressed message back through the .NET `/offer` client; a second offer confirms no new transfer. Separate live cases exercise Python `lxmd`; native-daemon acceptance is in `tests/Pester/Lxmd.Tests.ps1`.

Current Windows command, with live mode and the exact primary checkout selected: `dotnet test tests\DotLXMF.Tests\DotLXMF.Tests.csproj -c Release --artifacts-path artifacts\lxmf-build-check --verbosity quiet --logger trx --results-directory artifacts\lxmf-live-results` passed **65/65**, zero skips. Parent independently ran the 12 live cases successfully. Focused weight/priority, rotation/control/restart/cancellation, ignored-source and unit checks passed **38/38**. These are direct loopback topologies, not multi-hop, lossy-network or hardware evidence.

Published `lxmd` NativeAOT acceptance passed **5/5 on Windows x64** and **5/5 on WSL Ubuntu/Linux x64**, with warning-as-error publication. The Linux adapter initially deadlocked upstream forked stamp workers by holding Python's buffered stdin lock while waiting for commands. Its bounded `os.read` command reader fixes adapter ownership only; upstream source, work costs (delivery 12 / propagation 13), stamp generation and acceptance were not modified. Linux signal shutdown, real inbound-handler container decoding, service logs, authenticated PN restart/retrieval and actual Python/native control queries ran successfully. This does not establish native daemon node-to-node peering.

## Reference provenance and acceptance boundary

The inventory targets **LXMF 1.2.1, commit `e8ba02483217af43be284a72f1c059c730b7d047`, tree `f5ad44f221707dacf600edcbeeffee57f2c9cc43`**, fetched through the verified primary Git locator `rns://7649a50d84610232d1416b41d2896aff/reticulum/lxmf`. A mirror-seeded native .NET Reticulum fetch obtained 13,622 missing bundle bytes and passed strict Git fsck. This establishes retrieved content, not publisher authentication. The README's `a8d24177d946de4f1f0a0fe1af9a1338` address is a NomadNetwork page, **not** the Git destination.

All nine Python source hashes match the official 1.2.1 wheel (SHA-256 `4bd95325b00e5fae81241e28f26a5ed22b38fea941e128d1c63a33c99e931440`). Exact source hashes are in `tests/Interop/reference_environment.py` and the new fixtures. The stale GitHub mirror `c3ff2d6dc2f256daab896dadc044dd5a913ecbb7` reports 1.2.0 and is not the target. `DOTRETICULUM_LXMF_UPSTREAM` optionally selects and verifies the primary checkout; the default isolated wheel remains supported.

`GenerateLxmfContractVectors.py` was actually rerun against that exact primary checkout with RNS `a29bfb51c468945c96a1ee22cae0114e993e2391`. It executes upstream message/paper/container packing, ticket lifecycle, PN/peering workblocks and validation, peer serialization, announce helpers and delivery-cache persistence. No-network shims disable destination registration and supply empty blackhole/used-destination bookkeeping; fixed clocks and in-memory ticket-save suppression isolate policy. Cryptography, encoding and stamp verification are unmodified. Randomized paper/propagated encryption is stored, not claimed reproducible byte-for-byte. Older `GenerateLxmfVectors.py` fixtures retain their original RNS `e40191b3d193b46b7f2d8a44424a594cd758839b` provenance.

Status meanings: **implemented** = local behavior exists with the cited focused evidence; **partial** = only the described portion exists; **missing** = no corresponding implementation; **unverified** = implementation exists without the required interoperability evidence; **not-applicable** = Python-specific execution mechanism, not a wire/API contract; **upstream-placeholder** = reference TODO, not behavior to invent. Implemented does not imply live coverage unless explicitly stated.

## Upstream contract inventory

Source paths below are relative to the official wheel's `LXMF/` directory. This is a present-state inventory, not a promise of complete parity.

### Message contracts (`LXMessage.py`)

| Contract / upstream entry points | Status and evidence |
|---|---|
| Constructor destination/source identities and explicit unknown hashes; `destination`, `source`, `get_destination`, `set_destination`, `get_source`, `set_source` | Partial: .NET creation takes delivery hash and source identity; unpack supports unknown identities. One-time reassignment APIs are not exposed. |
| `set_title_from_string`, `set_content_from_string`, `title_as_string`, `content_as_string` | Implemented through UTF-8 creation/properties; original and new vectors. Invalid UTF-8 presentation uses .NET replacement characters rather than Python's exception. |
| `set_title_from_bytes`, `set_content_from_bytes`, `set_fields`, `get_fields` | Implemented through `CreateBinary`, byte properties and MsgPack fields; binary preservation test. Byte properties remain mutable; mutation after packing is not supported. |
| States `GENERATING=0`, `OUTBOUND=1`, `SENDING=2`, `SENT=4`, `DELIVERED=8`, `REJECTED=253`, `CANCELLED=254`, `FAILED=255`; paper finalization sets `PAPER=5` | Implemented enum/state/completion model; observer, paper and propagation tests. Propagated completion means sent, not recipient delivery. |
| Methods `UNKNOWN=0`, `OPPORTUNISTIC=1`, `DIRECT=2`, `PROPAGATED=3`, `PAPER=5`; representations unknown/packet/resource/paper | Partial: all concrete methods implemented; resource-vs-packet through `IsResource`, without a separate representation enum. |
| `pack`, hash, message-id, Ed25519 signed part, timestamp and optional stamp exclusion | Implemented; byte-identical Python vectors. Repacking explicitly fails. |
| Opportunistic oversize promotion; direct packet/resource threshold | Implemented; original vectors, loopback and existing live endpoint tests. Plain/group outbound destinations missing. |
| `unpack_from_bytes`, source-unknown/signature-invalid reasons, blackholed source metadata | Partial: signature/unknown-source handling implemented; blackhole policy/metadata not integrated. |
| `register_delivery_callback`, `register_failed_callback` | Partial: state event/completion replaces callbacks idiomatically; fault isolation tested. Separate delivery/failure callback registration APIs not exposed. |
| `set_delivery_destination`, `send`, packet/Resource conversion, callbacks, progress and retries | Partial: router owns links/receipts/Resources; no application-supplied delivery destination or transfer progress/cancellation handle. |
| `determine_compression_support` | Implemented for direct Resources: absent/legacy/invalid capability list defaults to compression; explicit empty list disables it. Python helper vectors cover interpretation; live non-compression negotiation unverified. |
| `determine_transport_encryption`, `transport_encrypted`, descriptive cipher names, `ratchet_id`, link ID and physical telemetry | Partial: encrypted transport and container cipher metadata exist; ratchet/physical-stat exposure missing. Reference explicitly marks cipher descriptions outdated (**upstream-placeholder** for revised descriptions). |
| `validate_stamp`, `get_stamp` | Partial: proof-of-work and automatic ticket selection/validation implemented and Python-verified; full stamp value/check metadata missing. |
| Ticket constants: 16-byte key; expiry 21 days; grace 5 days; renew 14 days; delivery interval 1 day; ticket stamp value `0x100` | Implemented by `LxmTicketStore`; executed lifecycle/store vectors, boundary/reopen tests and live high-cost ticket bypass. |
| `get_propagation_stamp`, transient ID, PN workblock expansion, deferred generation | Partial: generation/transient association implemented with upstream vectors and two live propagation directions; no shared deferred-job registry. `timeout` parameters on upstream stamp methods are not enforced (**upstream-placeholder**). |
| Propagated envelope `msgpack([time, [destination || encrypted(source || signature || payload) || propagation_stamp]])` | Implemented; generated Python encrypted fixture and actual propagation client/node round trips. |
| Paper encryption, `as_uri(finalise)`, URI scheme and size ceiling | Implemented primitives; upstream-generated paper URI decrypts/signature-validates and normalizes identically. Reverse Python acceptance is separately tested by adapter when enabled. |
| `as_qr`: optional `qrcode`, error correction L, border 1 | Missing. QR image rendering is optional upstream functionality, not needed to parse URI bytes. No QR interoperability claim. |
| `packed_container`, `write_to_directory`, `unpack_from_file` | Partial: container pack/unpack implemented, upstream fixture plus Python reverse adapter. Daemon owns atomic hash-named inbox files; standalone message file helpers are not exposed. |
| Python locks, worker threads and callback registration spelling | Not-applicable as implementation mechanisms; observable serialization, bounds, cancellation and ownership use .NET abstractions instead. |

### Fields and announce helpers (`LXMF.py`)

All MsgPack field values are preserved rather than transcoded into application-specific objects. Field presence does not mean the library renders or executes their content.

| Contract | Status |
|---|---|
| `FIELD_EMBEDDED_LXMS=01`, `TELEMETRY=02`, `TELEMETRY_STREAM=03`, `ICON_APPEARANCE=04`, `FILE_ATTACHMENTS=05`, `IMAGE=06`, `AUDIO=07`, `THREAD=08`, `COMMANDS=09`, `RESULTS=0A`, `GROUP=0B`, `TICKET=0C`, `EVENT=0D`, `RNR_REFS=0E`, `RENDERER=0F`, `REPLY_TO=30`, `REPLY_QUOTE=31`, `REACTION=40`, `COMMENT=41`, `CONTINUATION=42`, `CUSTOM_TYPE=FB`, `CUSTOM_DATA=FC`, `CUSTOM_META=FD`, `NON_SPECIFIC=FE`, `DEBUG=FF` | Implemented as named constants and generic round-trip fields. Attachments/embedded messages/tickets have existing field vectors; per-field semantic validation is not imposed upstream. |
| Attachment, image and audio client conventions | Partial: opaque nested fields round-trip; no file extraction, image decoder, audio codec or playback. These are client concerns, not a second message framing format. |
| Optional audio IDs: Codec2 `450PWB=01`, `450=02`, `700C=03`, `1200=04`, `1300=05`, `1400=06`, `1600=07`, `2400=08`, `3200=09`; Opus `OGG=10`, `LBW=11`, `MBW=12`, `PTT=13`, `RT_HDX=14`, `RT_FDX=15`, `STANDARD=16`, `HQ=17`, `BROADCAST=18`, `LOSSLESS=19`; custom `FF` | Partial: preserved as field data; no named .NET codec enum or codec implementation. Upstream LXMF does not implement these codecs. |
| Renderer IDs plain/micron/markdown/bbcode `0/1/2/3` | Partial: transported, not rendered; upstream explicitly optional. |
| Reaction dictionary indices `TO=0`, `CONTENT=1`; comment `FOR=0`; continuation `OF=0`; reply-to/thread hashes and UTF-8 reply quotes | Partial: transported without application interpretation; upstream leaves sanitization/display to clients. |
| Reserved core fields `00..80`; experimental use recommended above `FF`; custom encapsulation fields | Implemented via unrestricted integer-keyed MsgPack fields; no blocking of unknown extensions. |
| `display_name_from_app_data` legacy raw UTF-8 and list form; list names strip NUL and surrounding whitespace | Implemented; new executed helper vectors. Invalid MsgPack is rejected safely rather than bubbling decoder exceptions. |
| `stamp_cost_from_app_data` and delivery announce `[name_bytes-or-nil, cost-or-nil, [supported-functionality]]` | Partial: normal cost `1..254` and nil supported; .NET rejects out-of-range announced costs rather than returning arbitrary upstream object values. |
| `compression_support_from_app_data`, `SF_COMPRESSION=0` | Implemented helper vectors, direct Resource negotiation. |
| `pn_name_from_app_data`, `pn_stamp_cost_from_app_data`, `pn_announce_data_is_valid` | Implemented through `LxmfPropagationAnnounce`; executed seven-element upstream announce fixture and actual Python nodes. |
| PN metadata keys version `00`, name `01`, sync-stratum `02`, sync-throttle `03`, auth-band `04`, utilization-pressure `05`, implementation `FE`, custom `FF` | Partial: version/name/implementation emitted, all map values preserved. Other metrics not synthesized; upstream warns allocations are unstable. |

### Router configuration and lifecycle (`LXMRouter.py`)

| Constructor option / public control | Status and exact boundary |
|---|---|
| `identity`, mandatory `storagepath`, `name` | Partial: caller-owned identity; optional storage persists ratchets/tickets/history and is required for PN storage/peers. Propagation and delivery names are separate. |
| `autopeer=True`, `autopeer_maxdepth=4`, `max_peers=20`, `static_peers=[]`, `from_static_only=False`, `sync_strategy=STRATEGY_PERSISTENT` | Partial: corresponding node options, scheduling/discovery, rotation and static admission implemented; lazy strategy incomplete, live scheduling unverified. |
| `propagation_limit=256`, `sync_limit=10240`, `delivery_limit=1000` (decimal KB) | Partial: byte-valued limits implemented at these defaults. Invalid sync-below-transfer configuration is rejected rather than clamped. |
| `enforce_ratchets=False`, `enforce_stamps=False` | Partial: ratchets supported and enforcement optional; stamp enforcement defaults **true** here, intentionally differing from router constructor (not necessarily daemon policy). |
| `propagation_cost=16` (minimum 13), `propagation_cost_flexibility=3`, `peering_cost=18`, `max_peering_cost=26`, `max_inbound_syncs=3`, `sequential_validation=True`, `static_sequential=False` | Partial: options and admission/validation/throttle gates implemented; full contention/failure interoperability remains unverified. |
| `register_delivery_identity`, `announce`, `register_delivery_callback`, `set_inbound_stamp_cost`, `get_outbound_stamp_cost`, `get_announce_app_data` | Partial: one destination per router, immutable inbound stamp option, announce cache lookup. Multiple delivery identities and mutable stamp cost APIs missing. |
| `set_active_propagation_node`, `set_outbound_propagation_node`, `get_outbound_propagation_node`, `get_outbound_propagation_cost` | Partial: `OutboundPropagationNode` plus verified announced-cost lookup. Combined inbound/outbound setter not exposed. |
| `set_inbound_propagation_node`, `get_inbound_propagation_node` | Upstream-placeholder: both raise `NotImplementedError`; not silently implemented here. |
| `set_retain_node_lxms`, `set_authentication`, `requires_authentication`, `allow`, `disallow`, `allow_control`, `disallow_control` | Partial: per-retrieval retain flag and separate download/control node option lists; runtime mutation APIs missing. |
| `prioritise`, `unprioritise`, `ignore_destination`, `unignore_destination`, `identity_allowed` | Partial: ignored-source mutation and configured priority weighting implemented. Runtime priority mutation and generic identity policy missing. Upstream `unprioritise` references an undefined variable (**upstream-placeholder/defect**, not a working contract). |
| `enable_propagation`, `disable_propagation`, `announce_propagation_node`, `get_propagation_node_app_data`, `get_propagation_node_announce_metadata` | Partial: enable, announce, parse/pack and async disposal exist. Disable/re-enable on the same live router is not exposed. |
| `enforce_stamps`, `ignore_stamps` | Partial: immutable `EnforceStamps` option instead of runtime toggles. |
| `set_message_storage_limit`, `message_storage_limit`, `message_storage_size`, `set_information_storage_limit`, `information_storage_limit`, `information_storage_size` | Partial: immutable storage bound and `StoredBytes`/stats exist. Runtime unit-valued setters and information-storage accounting missing. |
| `delivery_link_available`, `handle_outbound`, `process_outbound`, `attempt_limit`, `attempt_due`, `schedule_attempt` | Partial: direct/opportunistic queue, proofs, retries. Medium-aware attempt scheduling, debounce, link inactivity retirement and backchannel reuse are not equivalent. |
| `medium_path_timeout`, `destination_round_trip`, `path_request_wait`, `slow_interface_online`, `request_path`, `path_request_pending` | Partial: stack path request exists; upstream medium-aware router policies not reproduced. |
| `cancel_outbound`, `get_outbound_progress`, `get_outbound_lxm_stamp_cost`, `get_outbound_lxm_propagation_stamp_cost` | Partial: `Cancel` cancels queued/active Resources; message `Progress` exposes transfer progress. Stamp-cost inspection and cancellation by message ID during generation missing. |
| `inbound_count`, `inbound_resources`, `cancel_inbound`, `cancel_all_inbound` | Implemented as `InboundResources`, `CancelInbound`, `CancelAllInbound`; network cancellation failure-path coverage remains incomplete. |
| `generate_ticket`, `remember_ticket`, `get_outbound_ticket`, `get_outbound_ticket_expiry`, `get_inbound_tickets` | Implemented through `Tickets`; upstream policy/store vectors, focused lifetime tests and live exchange. |
| `clean_available_tickets`, `save_available_tickets`, `reload_available_tickets`, `clean_outbound_stamp_costs`, `save_outbound_stamp_costs`, `update_stamp_cost` | Partial: automatic ticket cleanup/load/save; outbound stamp-cost persistence missing. |
| `has_message`, `lxmf_delivery`, `delivery_packet`, `delivery_link_established`, `delivery_link_closed`, `delivery_remote_identified`, `delivery_resource_transfer_began`, `delivery_resource_advertised`, `delivery_resource_concluded` | Partial: receive/decrypt, persistent dedup, resource policy, ticket remembrance and `HasMessage` implemented. Blackhole/ignore policy and physical stats missing. |
| `jobs`, `jobloop`, `flush_queues`, `clean_resource_tracking`, `clean_links`, `clean_transient_id_caches`, `clean_throttled_peers`, `clean_message_store`, `save_locally_delivered_transient_ids`, `save_locally_processed_transient_ids`, `save_node_stats` | Partial: bounded delivery workers, persistent delivered IDs/node counters, expiring PN store and peer maintenance. Processed-ID persistence, full link-idle cleanup and stamp/throttle jobs missing. |
| `exit_handler`, `sigint_handler`, `sigterm_handler` | Partial: async disposal cancels pending messages, deregisters destinations and flushes peer/ticket state; daemon owns process signals. Python thread/signal spelling is not-applicable. |
| `get_size`, `get_weight`, `get_stamp_value` | Implemented as `GetSize`, `GetWeight`, `GetStampValue`; weight fixtures execute upstream age/priority calculations. Unknown-size lookup returns null rather than Python's key exception. |
| `compile_stats`, `stats_get_request`, `peer_sync_request`, `peer_unpeer_request` | Partial: separate authorized control destination and exact paths/maps implemented; local authorization tests pass. Metrics and live remote control coverage incomplete. |
| `request_messages_from_propagation_node`, `cancel_propagation_node_requests`, `request_messages_path_job`, `message_list_response`, `message_get_response`, `message_get_progress`, `message_get_failed`, `acknowledge_sync_completion`, `message_get_request` | Partial: full identified list/download/acknowledge workflow, retain/max-message options and cancellation token. Two Python-live directions pass; exact intermediate progress and cached-link reuse semantics differ. |
| Download states `PR_IDLE=00` through `PR_COMPLETE=07`; no-path `F0`, link-failed `F1`, transfer-failed `F2`, missing-identity `F3`, no-access `F4`, failed `FE`; `PR_ALL_MESSAGES=0` | Partial: enum and terminal error mapping exist; precise intermediate state/progress fidelity unverified. |
| `peer`, `unpeer`, `rotate_peers`, `sync_peers`, `propagation_link_established`, `propagation_resources_transferring`, `propagation_resource_transfer_began`, `propagation_resource_advertised`, `propagation_packet`, `offer_request`, `propagation_resource_concluded` | Partial: sync, rotation, unpeer, `/offer`, stamps, limits/static/sequential/throttle policies implemented; healthy explicit sync live in both directions. Contention/fault-path verification remains incomplete. |
| `enqueue_peer_distribution`, `flush_peer_distribution_queue`, `lxmf_propagation`, `ingest_lxm_uri`, `propagation_transfer_signalling_packet`, `process_deferred_stamps`, `fail_message` | Missing propagation/deferred stamp distribution; paper decrypt helper is not router ingestion or duplicate signalling. |
| Router constants/retry defaults | Partial: attempts 5, processing 4 s, retry 10 s, path wait 7 s, pathless tries 1, PN expiry 30 d and delivered-cache expiry 180 d. Missing debounce 60 s, slow threshold 2000 bps, delivery-link idle 600 s, propagation-link idle 180 s, stamp-cost expiry 45 d, initial node delay 20 s, exact PN path timeout 10 s and stamp throttle 180 s. |
| Periodic jobs | Partial: outbound processing 4 s, peer scheduling 24 s, rotation 1,344 s and peer/stat flush 480 s. Full job parity absent: stamps/links 1 tick, resources 2, transients 60, storage 120 and peer ingest 6. |

### Peer contracts (`LXMPeer.py`) and handlers (`Handlers.py`)

**Partial:** `LxmPeer` implements the upstream serialized map, cost/key state, handled/unhandled indices, counters and acceptance rate. Executed upstream `to_bytes` / `from_bytes` vectors prove format compatibility and filtering of IDs no longer present. The node implements key generation, `/offer` negotiation, Resource transfer, failures, backoff, announce discovery, static peers and maintenance. Internal queue mechanics use bounded channels rather than Python's per-peer deques. Healthy bidirectional peer synchronization passed live; lazy-strategy connection retention, exact state/progress fidelity, rate accounting and failure recovery remain incomplete.

The serialized map includes destination hash, peering timebase, alive, metadata, last heard, sync strategy, peering key, link establishment/sync transfer rates, transfer/sync limits, propagation cost/flexibility, peering cost, last sync attempt, offered/outgoing/incoming and RX/TX byte counters, handled/unhandled IDs. On load, only IDs still in the propagation store survive. Strategies lazy `1` and persistent `2` (default); unreachable horizon 14 days, failure backoff step 720 s, path request grace 7.5 s. States idle `0`, link establishing `1`, link ready `2`, request sent `3`, response received `4`, resource transferring `5`; errors no identity `F0`, no access `F1`, invalid key `F3`, invalid data `F4`, invalid stamp `F5`, throttled `F6`, not found `FD`, timeout `FE`.

`LXMFDeliveryAnnounceHandler.received_announce` is **partial**: stack identity/app-data caching feeds outbound delivery and ticket exchange, but persistent stamp-cost caches and application-specific announce hooks are absent. `LXMFPropagationAnnounceHandler.received_announce` is **partial**: node discovery updates peer costs/limits/metadata and autopeer state, with static path-response handling and depth/cost rejection. Discovery through an incoming sync without an announce is not independently implemented.

### Stamper contracts (`LXStamper.py`)

`stamp_workblock`, `stamp_value`, `stamp_valid`, and `generate_stamp` are **implemented** for ordinary delivery, PN stamps and peering keys with independently executed vectors. `GenerateAsync` bounds active generation to one all-core job, supports queued/active cancellation and releases capacity after failure; cancellation/recovery is tested. The deferred-message registry and cancellation-by-message-ID remain **missing**; router disposal cancels preparation tokens. Python worker selection (`USE_WORKER_MANAGER`, spawn/fork, worker-process counts) is **not-applicable**. Ticket stamps are tested before ordinary proof-of-work.

### Persistence and restart inventory

All router files live beneath `<storagepath>/lxmf` upstream. No claim of full recovery exists.

| File / behavior | Status |
|---|---|
| `ratchets/<delivery-hash>.ratchets`: signed MsgPack ratchet-key list | Implemented using stack signed-file API when `StoragePath` configured; atomic save-before-publish and exclusive lease supplied by stack. Live restart/import testing remains unverified. |
| `local_deliveries`, `locally_processed`: transient/message IDs with timestamps | Partial: durable delivered-message binary map and 180-day retention implemented; processed propagated-transient ID cache missing. |
| `outbound_stamp_costs`: destination cache with time and cost | Missing persistent cache; current announce app data supplies costs. |
| `available_tickets`: `{"outbound":{destination:[expiry,ticket]}, "inbound":{destination:{ticket:[expiry]}}, "last_deliveries":{destination:time}}` | Implemented with atomic writes/exclusive ownership and upstream fixture import. Expired inbound keys remain through grace but are not returned for validation. |
| `messagestore/<transient-id>_<received-time>_<stamp-value>`: encrypted message plus PN stamp | Implemented atomic files, hash/size-validated restart index, 30-day expiry and age/size/priority eviction. Admission is more strictly bounded than upstream periodic cleanup; full crash/loss interoperability remains unverified. |
| `peers`: list of individually MsgPack-serialized peer dictionaries | Implemented representation and reopen filtering; complete peer-live restart interoperability unverified. |
| `node_stats`: persisted propagation RX/TX and client/unpeered counters | Implemented representation and graceful flush/reload; transfer-rate accounting remains partial. |
| Application message container named full message hash | Implemented container primitives and daemon inbox persistence; see daemon-specific acceptance. |
| Interrupted writes, malformed persistence, restart while queued, full store and version migration | Partial: atomic writes, leases, bounded/validated files and ticket tests; crash recovery of active peer/Resource work and full-store interoperability unverified. |

### Daemon and CLI inventory (`Utilities/lxmd.py`)

**Partial:** `src/lxmd` ships a NativeAOT executable named `lxmd` with persistent identity/config, inbox container persistence, direct delivery and propagation-node integration. `tests/Pester/Lxmd.Tests.ps1` exercises the published binary, distinct from library tests against Python `lxmd`. Unsupported settings/actions must fail explicitly; the daemon's help and tests delimit its currently implemented CLI surface.

CLI: `--config`, `--rnsconfig`, `-p/--propagation-node`, `-i/--on-inbound PATH`, repeated/clustered `-v/--verbose`, `-q/--quiet`, `-s/--service`, `--status`, `--peers`, `--sync HASH`, `-b/--break HASH`, `--timeout SECONDS`, `-r/--remote HASH`, `--identity PATH`, `--exampleconfig`, `--version`, `-h/--help` and `--long=value` are **implemented**. Status/peers/sync/unpeer use authorized network requests, not local formatting alone. Remote exit codes cover missing config/identity, identity loading, malformed hashes, timeout, authorization, absent peers and empty responses. Status rendering is deliberately compact, not byte-identical upstream terminal layout.

Configuration inventory (parser coverage is independent from core-policy parity; the daemon rejects unsupported non-default behavior):

* `[lxmf]` **implemented**: `display_name` (Anonymous Peer), `announce_at_start` (false), optional `announce_interval` (minutes), `stamp_cost` (12, accepted 1..254), `delivery_transfer_max_accepted_size` (1 decimal KB, minimum 0.38), optional `on_inbound`. The CLI inbound-command override intentionally works, unlike upstream 1.2.1's ignored-parameter bug.
* `[propagation]` **implemented configuration wiring**: `enable_node` (false), optional `node_name`, `auth_required` (false), `announce_at_start` (false in parser; true in example), optional `announce_interval` (minutes; example 360), `autopeer` (true), `autopeer_maxdepth` (4; example 6), `message_storage_limit` (500 MB, minimum .005), legacy `propagation_transfer_max_accepted_size` (256 KB), `propagation_message_max_accepted_size` (256 KB), `propagation_sync_max_accepted_size` (10240 KB), `propagation_stamp_cost_target` (16, minimum 13), `propagation_stamp_cost_flexibility` (3, minimum 0), `peering_cost` (18, minimum 0), `remote_peering_cost_max` (26, minimum 0), `control_allowed`, `static_peers`, `max_peers` (20), `from_static_only` (false), `prioritise_destinations`, `sequential_pn_stamp_validation` (true), `static_peers_bypass_sequential` (true), `max_inbound_syncs` (3, minimum 1). The final four are explicitly configured during native PN acceptance; this verifies wiring/startup/healthy flow, not every contention policy.
* `[logging]` **implemented**: `loglevel` (3 when omitted, 4 in example), service file logging and CLI verbosity/quietness overrides. Service logging reuses the stack logger with a 5 MiB single backup.

The configuration parser is **partial** ConfigObj compatibility: simple INI sections, quoted values/comments and comma-separated hash lists; interpolation, multiline values and nested lxmd sections are not supported. Inbound commands use bounded 60-second subprocess execution, discard child output and terminate the child on shutdown.

Observable upstream parser quirk: the legacy `propagation_transfer_max_accepted_size` setting is assigned and then overwritten by the `propagation_message_max_accepted_size` branch/default. Do not silently reinterpret both as independent enforced limits.

Daemon filesystem inventory: config discovery (`/etc/lxmd`, XDG user config or `~/.lxmd`), `config`, `identity`, `ignored`, `allowed`, storage/message directories and log files. Identity reuse, generated config, ignored-source filtering, allowed-identity download ACLs, atomic inbox files and command invocation are **implemented**. Native acceptance covers authenticated propagation deposit, process restart, retrieval/deletion and Python/native control queries with explicit non-default settings (cost 13, flexibility 0, autopeer off, authentication enabled and the Python identity allowed). Default startup/job timing and complete platform-specific discovery remain **unverified**. No native daemon node-to-node peering acceptance is claimed.

### Required remaining interoperability

Verified evidence includes propagation delivery/retrieval and healthy explicit peer synchronization in both Python/.NET directions, ticket exchange, message/container/paper vectors, weight/priority behavior and native daemon flows on Windows/Linux. It does **not** yet establish all cost/concurrency limits, full restart recovery of in-flight work, cancellation mid-Resource, mixed Python/.NET transport hops, injected loss/latency, paper QR rendering, audio/image device behavior or platform-wide interface support.

A failed peer-live teardown exposed a stack ownership defect: `Link.CloseLocked()` starts `DrainResourceWorkAsync()` while holding the link gate; a completed work item's removal continuation needs that gate, allowing the synchronous drain loop to spin. Managed thread evidence identifies both stacks. This is separate from the first-packet `LinkEstablished` ordering issue repaired in stack `21ec8ac9e167c7ad6df289dc5b7d2a347b703ce9`, after which the healthy peer case passed. The additional teardown/failure defect remains unresolved; healthy success does not prove that failure path.