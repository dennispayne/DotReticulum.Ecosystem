# LXMF (`DotReticulum.Lxmf`)

A .NET implementation of LXMF messaging that follows upstream LXMF 1.2.1 on the wire.

- `LxMessage`: pack/unpack of `destination(16) || source(16) || signature(64) || msgpack([timestamp, title, content, fields(, stamp)])`; the hash covers destination, source and the payload without the stamp; signature validation; states (`Generating`, `Outbound`, `Sent`, `Delivered`, `Rejected`, `Failed`, ...).
- `LxmRouter` (a "router-lite"): registers the `lxmf.delivery` destination (announce app_data carries the display name and stamp cost), receives messages via a callback, sends with an outbound queue and retries. Delivery methods: opportunistic (single encrypted packet, content up to 287 bytes), direct over a Link (single packet up to 319 bytes, larger messages as a Resource), delivery proofs/receipts.
- `LxStamper`: generate and validate stamps (required by recipients that demand a stamp cost).

```csharp
var lxmf = new LxmRouter(node.Router, identity, "dotnet");
lxmf.MessageReceived = received => Console.WriteLine(received.Message.Content);
lxmf.Start();
await lxmf.AnnounceAsync();
var message = LxMessage.Create(destinationHash, identity, "content", "title", fields: null, method);   // opportunistic or direct
await lxmf.SendAsync(message);
```

(see `tests/DotReticulum.Lxmf.Tests` for exact, current usage).

## Not implemented

- Propagation node client (syncing queued messages from `lxmd` propagation nodes), propagation delivery and peering. Live tests cover bidirectional delivery with a Python `LXMRouter`, and separately .NET delivery into a real `lxmd` delivery destination. They do not cover propagation sync or establish bidirectional interaction with the daemon.
- Ratchets on the `lxmf.delivery` destination; tickets; `lxmd` peering.
- `lxmd` by default refuses Resource deliveries over 1 KB (`delivery_transfer_max_accepted_size`), so large direct messages to a default `lxmd` are rejected; this is `lxmd` policy, not a protocol limit.

State-change observers run independently: a failing observer is reported through `LxMessage.Log` (inherited from `LxmRouter.Log` when queued, or a diagnostic trace if unset), without suppressing other observers or message completion.
