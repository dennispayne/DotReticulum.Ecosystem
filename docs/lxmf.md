# LXMF

`DotReticulum.Lxmf` implements LXMF 1.2.1 message encoding and direct/opportunistic delivery on the Reticulum stack. It provides message signing and validation, delivery proofs, stamps, retrying outbound delivery, and receive callbacks. The router is lightweight: it does not implement propagation-node synchronization or peering.

## Message format and API

An LXMF message contains `destination(16) || source(16) || signature(64) || msgpack([timestamp, title, content, fields[, stamp]])`. The message hash covers destination, source, and the payload without the stamp. The signature is validated during unpacking. `LxMessage` exposes generating, outbound, sent, delivered, rejected, failed, and cancelled states, plus completion tracking.

A typical application creates a router with a transport router and identity, registers its receive callback, starts it, announces its destination, and submits messages:

```csharp
var lxmf = new LxmRouter(node.Router, identity, "dotnet");
lxmf.MessageReceived = received => Console.WriteLine(received.Message.Content);
lxmf.Start();
await lxmf.AnnounceAsync();
var message = LxMessage.Create(destinationHash, identity, "content", "title", fields: null, LxmMethod.Direct);
await lxmf.SendAsync(message);
var finalState = await message.Completion;
```

Dispose `LxmRouter` asynchronously with the owning node. `LxmRouterOptions` configures retry attempts (default 5), retry wait (10 seconds), path request wait (7 seconds), pathless tries (1), processing interval (4 seconds), inbound direct-resource transfer size (1,000,000 bytes), inbound stamp cost/enforcement, and optional opportunistic receipt timeout. `SendAsync` packs and queues the message; delivery runs in the background, so observe `State` or `Completion`.

## Security and access

Messages are signed and signature-validated. Opportunistic payloads are encrypted to the destination identity; direct delivery uses an encrypted Link. The router has no identity allow-list or application-level access-control policy for its inbound delivery destination. Configure an inbound stamp cost and keep enforcement enabled when proof-of-work gating is required; stamps do not replace peer authorization policy.

## Delivery behavior

Opportunistic delivery sends one encrypted packet and uses a delivery receipt. Its content ceiling is derived from the encrypted-packet MDU and message overhead (287 bytes for the pinned settings); larger messages requested as opportunistic are promoted to direct delivery. Direct delivery uses a Link: messages up to 319 bytes fit in one packet with the pinned settings; larger messages use a Resource. The router requests paths, retries failures, and suppresses duplicate received hashes for one hour. Incoming direct Resources are accepted only up to `DeliveryPerTransferLimit`.

`LxStamper` generates and validates 32-byte proof-of-work stamps. When a destination announces a stamp cost, outbound messages receive a stamp. A router may advertise and require an inbound stamp cost. Invalid stamps are dropped by default when enforcement is enabled; otherwise the callback receives `StampValid=false`.

The delivery destination is `lxmf.delivery`. Announce data may carry a display name and inbound stamp cost. Direct links identify the sender; opportunistic packets are encrypted to the destination identity. Message callbacks execute away from the router receive thread; duplicate valid messages are suppressed. Observer exceptions are logged without suppressing message completion or other observers.

## Limitations and differences from upstream

Propagation-node clients, queued-message synchronization, propagation delivery/peering, `lxmf.delivery` ratchets, tickets, and `lxmd` peering are not implemented. An `lxmd` configured with its default `delivery_transfer_max_accepted_size` of 1 KB may reject larger direct Resource deliveries; this is peer policy, not an LXMF wire-format limit. Set the daemon's acceptance limit deliberately when testing larger messages. Successful delivery to an `lxmd` destination does not establish propagation synchronization or bidirectional daemon interaction.

## Platform notes

LXMF message and delivery formats are not OS-specific; actual network reachability depends on the configured Reticulum interfaces and their support on the host. Managed or loopback tests do not establish platform-wide interface support.

## Verification

`tests/DotReticulum.Lxmf.Tests` contains message, stamp, and router tests. Opt-in `LiveLxmfTests.cs` exercises bidirectional opportunistic, direct single-packet, and large-resource delivery against pinned Python LXMF 1.2.1. Separate live cases exercise a real `lxmd` delivery destination; these do not verify propagation synchronization or peering.