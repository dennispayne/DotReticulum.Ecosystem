# Telemetry

`telemetry` (Telemetry) publishes periodic system and node metrics from one [DotReticulum](https://github.com/dennispayne/DotReticulum) node and lets another node watch or retrieve reports. This MessagePack format is DotReticulum-specific; it is not part of upstream Reticulum or LXMF, and Python Reticulum nodes do not implement it.

## Usage

```sh
telemetry daemon --config <path> -i 10 -b 0
telemetry watch <destination-hash> --config <path> -n 10
telemetry get <destination-hash> --config <path>
telemetry -p --config <path>
```

`daemon` publishes reports every `-i` seconds (default 10) and announces at startup. `-b` controls subsequent announcement interval; zero means startup announcement only (default). `watch` prints reports until interrupted or the optional `-n` count is reached. `get` prints one report and exits. `-w` sets the connection timeout (default 30 seconds). `--identity` chooses the identity file, created if missing; `--config` selects a Reticulum config directory or file. `-p` prints the local identity and telemetry destination without starting the network. `-v`/`-q` increase or decrease logging verbosity. Use `telemetry --help` for parser details.

## Destination and access model

The destination is `dotreticulum.telemetry`, a single inbound destination using `storage/identities/telemetry` by default. Announce app data is a MessagePack map `{"v": 1, "name": <host name>}`. Links are accepted without authentication. The `report` request path permits all requesters and returns one fresh report as MessagePack binary data. Active links also receive one encrypted link packet per reporting interval; a report larger than link MDU is sent as a Resource.

The viewer resolves the destination from its path table; the daemon announcement or a path request answered by a transport node must reach it first. Reports disclose machine and runtime details, process resource use, transport status, link count, and interface names/types. Do not expose the service on networks where those details are sensitive or where untrusted access is unacceptable. Link encryption does not provide application-level authorization.

## Report format

Reports are MessagePack maps with string keys. Receivers ignore unknown keys and discard versions they do not recognize. Version changes only for breaking format changes.

| Key | Type | Meaning |
| --- | --- | --- |
| `v` | integer | Format version, currently 1 |
| `seq` | integer | Per-daemon sequence number, increasing by one per report |
| `ts` | float | Unix timestamp in seconds when the report was built |
| `host` | string | Machine name |
| `os`, `arch`, `runtime` | strings | OS description, CPU architecture, .NET runtime description |
| `uptime` | float | Process uptime in seconds |
| `cpu_count` | integer | Logical processor count |
| `cpu_time` | float | Process CPU time in seconds |
| `mem_rss` | integer | Process working set in bytes |
| `mem_total` | integer | Memory available to the process (GC) |
| `gc_heap` | integer | Managed heap size in bytes |
| `threads` | integer | Process thread count |
| `load` | float or nil | One-minute load average (Linux only) |
| `disk_total`, `disk_free` | integer or nil | Capacity and free bytes for the drive containing config `storage` |
| `transport` | boolean | Whether this node routes for others |
| `links` | integer | Links known to this node's link manager |
| `interfaces` | array of maps | One `{name, type}` map per configured interface |

Per-interface traffic counters are unavailable because the packet-interface contract does not expose them.

## Platform notes and differences from upstream

This application-specific format has no upstream equivalent or Python interoperability target. Load average is populated on Linux and nil elsewhere; other platform metrics depend on available runtime and OS APIs.

## Verification

`tests/Telemetry.Tests/TelemetryTests.cs` covers MessagePack round trips, unknown-version rejection, direct publishing, and delivery through a .NET `rnsd` transport hop. These tests establish DotReticulum node behavior, not compatibility with a Python telemetry service.