# DotLXST reference audit

## Status and scope

**Not implemented.** There is no DotLXST library, test project, `rnphone`
executable, reference-vector evidence, NativeAOT evidence or live LXST
interoperability evidence in this repository. This document records a preliminary
reference audit, not a completed contract inventory or a compatibility claim.

The project is experimental, AI-assisted and not an official or endorsed LXST
implementation.

## Reference provenance and licensing

The inspected reference is the public GitHub mirror
[`markqvist/LXST`](https://github.com/markqvist/LXST), commit
`721b2eee6dd37606e423914a4937a11a3c3a761f`. Its `LXST/_version.py` reports
**0.4.5**. This is a research revision, not an installed live-reference pin:
`tests/Interop/reference_environment.py` does not yet verify LXST.

The mirror's README says active development and current source are distributed
over Reticulum using `rngit`; `MIRROR.md` also identifies GitHub as a mirror.
The [Reticulum software manual](https://reticulum.network/manual/software.html)
directs readers to project Nomad Network or `rngit` nodes for current source.
The inspected material does not establish a concrete LXST `rns://` repository
locator, and no primary-host checkout or publisher authentication has been
verified. `setup.py` advertises `https://git.unsigned.io/markqvist/lxst`; resolving
that host failed in the audit environment. The GitHub revision must not be
described as a verified latest primary-host release.

**LXST does not use this repository's Reticulum License.** The inspected
`LICENSE` is Creative Commons Attribution-NonCommercial-NoDerivatives 4.0
International. Section 2(a)(1)(b) grants noncommercial production and reproduction,
but not sharing, of Adapted Material. `setup.py` classifies its license as
Other/Proprietary. The root Reticulum License cannot be assumed to authorize
redistribution of an LXST source adaptation.

No reference source, sounds, vendored libraries or translated implementation is
included in this audit. Before distributing a source-derived implementation,
establish the necessary authorization. An independently authored compatible
implementation based on noncopyrightable protocol facts is a separate approach;
this audit does not determine its legal status or offer legal advice.

### Source digests

SHA-256 hashes of the inspected files at the revision above:

| File | SHA-256 |
|---|---|
| `README.md` | `892343dcb468d520c800196b40b3b1cfda7d09e5a1535b2e0ce2fe5ba3dcedca` |
| `LICENSE` | `a60cc60ac16dd9d0067d760265a020357865de09dc3a3966e89d85bda13ed203` |
| `setup.py` | `83155aa062c802e29dcceae0e0bf6cb0d6986ffd8e52ee5f8ba6a7876f6b2a3f` |
| `LXST/_version.py` | `e6c6350de2a2647e89bcad7adae62c4512a01b7c6264d59498c89aac56787252` |
| `LXST/Network.py` | `62360aad07bbe94af64e267b574e5cbdd304b634e5c78a191eb54cb989ef503f` |
| `LXST/Codecs/Raw.py` | `497683da61f371416ad445b039721eb32f737bd7599198e8dc94f37170732feb` |
| `LXST/Codecs/Codec2.py` | `ee7d880ef67ebea1e163a2b0d10de227e2b5e39abce00a9296e6ec99cc9b1e7b` |
| `LXST/Codecs/Opus.py` | `8076fc7b3886955399f3676d8693d97231ad7d9875f35c5cdbca96299b811fca` |
| `LXST/Primitives/Telephony.py` | `c639a07b9d81b9bfb85d54230e6f4052b9787619177a97131e76f28740db5380` |
| `LXST/Utilities/rnphone.py` | `cf0bff6f53955335f9ea0f8e09942b98c0e2a0bbcdf715f0d1458d2eb701a8db` |

## Contract map

`missing` means absent from .NET; `unverified` means a reference or environment
claim has not been executed; `upstream-placeholder` means the inspected source
does not implement the advertised branch. Rows group related public contracts;
they are not an exhaustive method-by-method acceptance checklist.

### Transport, signalling and sessions

| Contract | Status | Reference |
|---|---|---|
| Application namespace `lxst` | missing | `LXST/__init__.py` |
| MessagePack map envelope; signalling key `0`, frame key `1` | missing | `Network.py`, field constants and packet handlers |
| Signalling scalar/list reception, immediate send, proxy receiver | missing | `Network.py`, `SignallingReceiver` |
| Deferred/in-band signalling scheduling | upstream-placeholder | `Network.py`, `signal(immediate=False)` and TODOs |
| Codec discriminator before each encoded frame; raw `0`, Opus `1`, Codec2 `2` | missing | `Codecs/__init__.py`, `Packetizer.handle_frame` |
| Null codec identity operation; no packetizer mapping for Null | missing | `Codecs/Codec.py`, `Codecs/__init__.py` |
| Single/list frame reception and mid-stream codec replacement | missing | `Network.py`, `LinkSource._packet` |
| Reticulum packet send without receipts; inactive-link suppression and failure callback | missing | `Network.py`, `Packetizer` |
| Decode/deliver frame then process signalling in a combined envelope | missing | `Network.py`, `LinkSource._packet` |
| Error reporting for incoming malformed packets | missing | `Network.py`, exception handlers |
| Telephone destination `lxst.telephony`; announces, remote identity identification | missing | `Primitives/Telephony.py`, `Telephone` |
| Calling, available, ringing, connecting and established transitions; busy/rejected outcomes | missing | `Primitives/Telephony.py`, `Signalling`, `Telephone.signalling_received` |
| Incoming authorization: all, none, identity list or callable; blocked callers take precedence | missing | `Primitives/Telephony.py`, `set_allowed`, `set_blocked`, `__is_allowed` |
| One active call; incoming contention, answer, hangup and teardown | missing | `Primitives/Telephony.py`, incoming handlers, `answer`, `hangup`, `teardown` |
| Ring/wait/connect timeouts, path requests, periodic announcements | missing | `Primitives/Telephony.py`, timeout jobs, `call`, `__jobs` |
| Ringing/established/ended/busy/rejected callbacks | missing | `Primitives/Telephony.py`, callback setters |
| Profile negotiation (`255 + profile`), selection and mid-call switch | missing | `Primitives/Telephony.py`, `Profiles`, `switch_profile`, `signalling_received` |
| Receive/transmit gain, mute, AGC, low-latency output, ringer/device selection | missing | `Primitives/Telephony.py`, public setters |
| Legacy `CallEndpoint` | upstream-placeholder | `Call.py`: `answer` references undefined packet source/sink and frame-time names |
| Pre-establishment outgoing-close callback | upstream-placeholder | `Primitives/Telephony.py`, `__outgoing_link_closed` is empty |
| Explicit network quality/loss adaptation, retransmission and resource-transfer contract | unverified | Not established by this preliminary audit; no behavior is inferred |

Telephone defaults differ from utility defaults. `Telephone` uses 60-second
ring, 70-second wait and 5-second connect timeouts. `ReticulumTelephone` supplies
30-second ring, 60-second wait and a separate 10-second path lookup limit.
These are source observations, not executed behavioral evidence.

### Codecs and audio processing

| Contract | Status | Reference |
|---|---|---|
| Raw interleaved floating samples, channel expansion/truncation, bit-depth selection and header | missing | `Codecs/Raw.py` |
| Raw float16/32/64/128 platform representation and byte order | unverified | `Raw.py` uses NumPy native dtypes; portable float128 is not established |
| Codec2 modes 700C, 1200, 1300, 1400, 1600, 2400 and 3200; default 2400 | missing | `Codecs/Codec2.py` |
| Codec2 mode header, whole-frame aggregation/truncation, mode changes, unknown-header fallback | missing | `Codecs/Codec2.py`, `encode`, `decode` |
| Codec2 mono 8 kHz conversion and signed-16-bit scaling/resampling | missing | `Codecs/Codec2.py`, `Codec.py` |
| Nine Opus voice/audio profiles; channel/rate/application/byte-ceiling selection | missing | `Codecs/Opus.py` |
| Opus 2.5/5/10/20/40/60 ms frames, channel conversion and resampling | missing | `Codecs/Opus.py`, `Codec.py` |
| Opus decoder output configured from sink; encoder/decoder state lifetime | missing | `Codecs/Opus.py` |
| Partial Opus input-frame padding | upstream-placeholder | `Codecs/Opus.py`, TODO in `encode` |
| Resample functions, normalize option and supported channel handling | missing | `Codecs/Codec.py` |
| Pipeline source/codec/sink wiring, processor slot, codec replacement and start/stop | missing | `Pipeline.py` |
| Mixer buffering, per-source limits, gain, mute, channel conversion and timed output | missing | `Mixer.py` |
| General mixer source resampling | upstream-placeholder | `Mixer.py`, TODO for all source types |
| High-pass, low-pass, band-pass and automatic gain-control state | missing | `Filters.py`, `Filters.c`, `Filters.h` |
| Tone frequency, gain, easing, frame duration and start/stop | missing | `Generators.py`, `ToneSource` |
| Line capture/playback, preferred devices, buffers, gain, filters and low latency | missing | `Sources.py`, `Sinks.py` |
| Loopback source/sink and buffering | missing | `Sources.py`, `Loopback` |
| Opus file playback, looping/timing, writing/finalizing and completion callbacks | missing | `Sources.py`, `Sinks.py`, `Primitives/Players.py`, `Primitives/Recorders.py` |

Telephony exposes eight profiles: ultra-low/very-low/low bandwidth; medium/high/
maximum quality; low/ultra-low latency. Their nominal frame durations are
400/320/200/60/60/60/20/10 ms respectively. The bandwidth profiles select Codec2
700C/1600/3200; the remaining profiles select Opus. The default is medium quality.
Codec2 aggregation durations are not Opus frame durations.

### Utilities, configuration and persistence

| Contract | Status | Reference |
|---|---|---|
| Console entry point `rnphone` | missing | `setup.py`, `Utilities/rnphone.py` |
| `--help`, `-l/--list-devices`, `--config`, `--rnsconfig`, `-s/--service`, `--systemd`, `--version`, repeated `-v/--verbose` | missing | `Utilities/rnphone.py`, `main` |
| Interactive dial/redial, answer/hangup, identity/destination display, phonebook and profile controls | missing | `Utilities/rnphone.py`, menus and `run` |
| Service mode, signals, shutdown cleanup, systemd example | missing | `Utilities/rnphone.py` |
| Configuration directory precedence, default config creation and first-run behavior | missing | `Utilities/rnphone.py`, `reload_config` |
| Identity generation/load/failure, `storage` directory, bundled ringtone copying | missing | `Utilities/rnphone.py`, `reload_config` |
| `[telephone]`: `ringtone`, `speaker`, `microphone`, `ringer`, `allowed_callers`, `blocked_callers` | missing | `Utilities/rnphone.py`, `apply_config` |
| `[phonebook]`: names, identity hashes, optional filtered numeric aliases, authorization integration | missing | `Utilities/rnphone.py`, `load_phonebook` |
| `[hardware]`: `keypad`, `keypad_hook_pin`, `display` | missing | `Utilities/rnphone.py`, `apply_config` |
| Commented amplifier-mute options | upstream-placeholder | Default config mentions `amp_mute_pin`/`amp_mute_level`; `apply_config` does not consume them |
| `ignored` and `allowed` paths | upstream-placeholder | `reload_config` assigns paths; no persistence implementation established |
| GPIO 4x4/5x5 keypad events, hook and key maps; I2C LCD1602 text/sleep/wake | missing | `Primitives/hardware/*.py` |
| Example programs: file player/recorder/source, filters, mixer, pipelines, tone generator | missing | `examples/*.py`; these are examples, not additional installed console entry points |

### Dependencies and platform evidence

The Python packaging manifest requires Python 3.11+, RNS 1.0.4+, LXMF 0.9.3+,
NumPy 2.3.4+, pycodec2 4.1.0+, CFFI 2.0.0+, and audioop-lts on Python 3.13+.
These are upstream minimum requirements, not permission to move this repository's
RNS/LXMF pins. Opus uses bundled PyOgg bindings and native codec libraries;
Codec2 uses pycodec2. The package can build a native filter extension and skips
that extension build on Windows. Device backends exist for Linux, Windows,
macOS and Android. Hardware peripherals additionally depend on appropriate
GPIO/I2C modules and physical devices.

No .NET codec strategy or native-library redistribution decision has been made.
Any future native interop must isolate codec handles, expose explicit ownership,
declare library versions/licensing, and demonstrate trimming/NativeAOT behavior.
No target OS, real codec bitstream, microphone/speaker, GPIO/I2C device or
RAK/RNode transport has been verified for DotLXST.

## Evidence and outstanding acceptance

The audit used `gh api repos/markqvist/LXST`,
`gh api repos/markqvist/LXST/commits/master`, an ordinary Git clone,
`git rev-parse HEAD`, source inspection and `Get-FileHash -Algorithm SHA256`.
These establish inspected mirror provenance and file contents only.

There are no generated upstream vectors, executed LXST imports, .NET unit tests,
call setup/audio tests, native executable tests or live topologies. Direct and
transport-hop bidirectional audio, codec switching, loss/latency, caller policy,
timeouts, cancellation/disposal races, teardown/recovery, persistence and device
I/O all remain unverified. No missing DotReticulum API has yet been established
by an implemented LXST session.

Required next decisions are a redistribution-compatible implementation approach
and verified primary-source provenance. The source/API inventory then needs
completion and executable upstream vectors before implementation; neither a
framing-only library nor a successful local codec round trip would establish
full LXST interoperability.
