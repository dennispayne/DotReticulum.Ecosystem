import hashlib
import json
import math
import os
import subprocess
from pathlib import Path
from types import SimpleNamespace

import numpy as np

from LXST.Codecs.Codec import resample
from LXST.Codecs.Codec import resample_bytes
from LXST.Codecs.Codec import Null
from LXST.Codecs.Opus import Opus
from LXST.Codecs.Raw import Raw
from LXST import Filters
from LXST.Filters import AGC, BandPass, HighPass, LowPass
from LXST.Generators import ToneSource
from LXST.Mixer import Mixer
from LXST.Sources import Loopback
from LXST.Sources import Source
from LXST.Sinks import Sink
from LXST.Pipeline import Pipeline
from LXST import Network
from LXST.Network import codec_header_byte, codec_type
from LXST.Codecs.Codec2 import Codec2


ROOT = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"]).resolve(strict=True)
PINNED_REVISION = "3b596f48ddd252ddc9c315ef8f3020cfd3688223"
SOURCE_FILES = [
    "LXST/Codecs/Codec.py",
    "LXST/Codecs/Opus.py",
    "LXST/Codecs/Codec2.py",
    "LXST/Filters.py",
    "LXST/Filters.c",
    "LXST/Filters.h",
    "LXST/Mixer.py",
    "LXST/Generators.py",
    "LXST/Pipeline.py",
    "LXST/Sources.py",
    "LXST/Sinks.py",
    "LXST/Network.py",
    "LXST/Codecs/Raw.py",
    "LXST/Codecs/__init__.py",
]


def samples(values):
    return np.asarray(values, dtype=np.float32)


def filter_vector(filter_type, values, rate):
    processor = filter_type
    result = processor.handle_frame(samples(values), rate)
    return np.asarray(result, dtype=np.float32).reshape(-1).tolist()


class RecordingSink:
    def __init__(self):
        self.accepting = True
        self.frames = []

    def can_receive(self, from_source=None):
        return self.accepting

    def handle_frame(self, frame, source):
        self.frames.append((frame, source))


class MixerSource:
    channels = 1
    samplerate = 8000


class MixerSink:
    def __init__(self):
        self.frames = []
        self.mixer = None

    def can_receive(self, from_source=None):
        return True

    def handle_frame(self, frame, source):
        self.frames.append(np.asarray(frame, dtype=np.float32).reshape(-1).tolist())
        self.mixer.stop()


class HeadlessPipelineSource(Source):
    def __init__(self):
        self.samplerate = 8_000
        self.should_run = False

    def start(self):
        self.should_run = True

    def stop(self):
        self.should_run = False


class HeadlessPipelineSink(Sink):
    pass


class CapturedPacket:
    observations = []

    def __init__(self, destination, data, create_receipt):
        self.observations.append((destination, bytes(data), create_receipt))

    def send(self):
        return True


def mix_reference(gain_db, muted):
    sink = MixerSink()
    mixer = Mixer(samplerate=8000, sink=sink, gain=gain_db)
    sink.mixer = mixer
    mixer.muted = muted
    source_a = MixerSource()
    source_b = MixerSource()
    mixer.handle_frame(np.asarray([0.2, 0.6, -0.5, 0.1], dtype=np.float32), source_a, decoded=True)
    mixer.handle_frame(np.asarray([0.3, 0.5, -0.6, -0.2], dtype=np.float32), source_b, decoded=True)
    mixer.should_run = True
    mixer._mixer_job()
    return sink.frames[0]


def main():
    revision = subprocess.check_output(
        ["git", "-C", str(ROOT), "rev-parse", "HEAD"], text=True
    ).strip()
    if revision != PINNED_REVISION:
        raise RuntimeError(f"Expected LXST {PINNED_REVISION}, found {revision}")
    hashes = {
        name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest()
        for name in SOURCE_FILES
    }

    resample_input = [
        [-0.75, 0.25],
        [-0.5, 0.5],
        [-0.25, 0.75],
        [0.0, -0.75],
        [0.25, -0.5],
        [0.5, -0.25],
        [0.75, 0.0],
        [0.4, 0.2],
    ]
    resampled = resample(samples(resample_input), 16, 2, 8000, 12000)
    normalized = resample(samples(resample_input), 16, 2, 8000, 12000, normalize=True)
    byte_cases = []
    for channels, input_rate, output_rate, values, normalize_bytes in (
        (1, 8000, 12000, [-5, 0, 5, -2, 0, 2], False),
        (2, 44100, 48000, [-32768, 1234, 32767, -2345, 8192, -8192, 0, 12], False),
        (1, 22050, 8000, [0, 1, 3, 7, -7, -3, -1], True),
        (2, 48000, 16000, [123, -456, 789, -1000, 321, -654, 987, -111], False),
    ):
        source_bytes = np.asarray(values, dtype=np.int16).tobytes()
        output_bytes = resample_bytes(source_bytes, 16, channels, input_rate, output_rate, normalize_bytes)
        byte_cases.append({
            "channels": channels,
            "inputRate": input_rate,
            "outputRate": output_rate,
            "normalize": normalize_bytes,
            "input": source_bytes.hex(),
            "output": output_bytes.hex(),
        })

    codec_input_rate = 44_100
    codec_input = np.asarray(
        [math.sin(2 * math.pi * 440 * i / codec_input_rate) * 0.3 for i in range(1_764)],
        dtype=np.float32,
    ).reshape(-1, 1)
    reference_opus = Opus()
    reference_opus.source = SimpleNamespace(samplerate=codec_input_rate)
    codec_encoded = reference_opus.encode(codec_input)
    pipeline_source = HeadlessPipelineSource()
    pipeline_sink = HeadlessPipelineSink()
    pipeline_codec = Raw(channels=1, bitdepth=16)
    pipeline = Pipeline(pipeline_source, pipeline_codec, pipeline_sink, processor=object())
    pipeline.start()
    pipeline_running_after_start = pipeline.running
    pipeline.stop()
    pipeline_raw_input = np.asarray([[0.25], [-0.5]], dtype=np.float32)
    pipeline_raw_encoded = pipeline_codec.encode(pipeline_raw_input)
    codec_markers = {codec.__name__: codec_header_byte(codec).hex() for codec in (Raw, Opus, Codec2)}
    original_packet = Network.RNS.Packet
    packetizer = Network.Packetizer(object())
    packetizer.source = pipeline_source
    CapturedPacket.observations = []
    try:
        Network.RNS.Packet = CapturedPacket
        packetizer.handle_frame(pipeline_raw_encoded)
    finally:
        Network.RNS.Packet = original_packet
    _, packet_data, create_receipt = CapturedPacket.observations[0]
    unpacked_packet = Network.mp.unpackb(packet_data)
    packetized_frame = unpacked_packet[Network.FIELD_FRAMES]

    filter_input = [
        0.0, 0.5, -0.25, 0.75, -0.5, 0.25, 1.0, -1.0,
        0.4, -0.2, 0.8, -0.6, 0.1, -0.9, 0.3, 0.7,
    ]
    high = filter_vector(HighPass(180), filter_input, 8000)
    low = filter_vector(LowPass(1200), filter_input, 8000)
    band = filter_vector(BandPass(180, 1200), filter_input, 8000)
    bandpass_input_rate = 48_000
    bandpass_input = [
        float(np.sin(2 * np.pi * 440 * i / bandpass_input_rate) * 0.4 + np.sin(2 * np.pi * 3000 * i / bandpass_input_rate) * 0.2)
        for i in range(32)
    ]
    bandpass_outputs = {}
    for rate in (8000, 24000, 48000):
        bandpass_outputs[str(rate)] = filter_vector(BandPass(250, 8500), bandpass_input, rate)

    agc_input = np.asarray(
        [[0.08, -0.04], [0.1, -0.05], [0.12, -0.06], [0.15, -0.08]] * 40,
        dtype=np.float32,
    )
    agc = AGC(target_level=-12.0, max_gain=12.0)
    agc_output = agc.handle_frame(agc_input, 8000).reshape(-1).tolist()

    tone = ToneSource(frequency=440, gain=0.2, ease=False, target_frame_ms=10, codec=Null())
    tone.ease_gain = 1.0
    tone_output = tone._ToneSource__generate().reshape(-1).tolist()
    transition_targets = [0.04, 0.0, 0.0, 0.04, 0.04, 0.01]
    tone_transitions = {}
    for ease in (False, True):
        transitioning_tone = ToneSource(
            frequency=400, gain=0.04, ease=ease, ease_time_ms=1,
            target_frame_ms=1, codec=Null(), channels=2,
        )
        transitioning_tone.ease_gain = 0.0 if ease else 1.0
        transition_frames = []
        for target in transition_targets:
            transitioning_tone.gain = target
            transition_frames.append(transitioning_tone._ToneSource__generate().reshape(-1).tolist())
        tone_transitions[str(ease).lower()] = transition_frames

    null = Null()
    null_input = object()
    null_encoded = null.encode(null_input)
    null_decoded = null.decode(null_encoded)
    recording_sink = RecordingSink()
    loopback = Loopback(codec=null, sink=recording_sink)
    loopback.sink = recording_sink
    assigned_source = object()
    loopback.source = assigned_source
    loopback.start()
    forwarded_frame = object()
    loopback.handle_frame(forwarded_frame, object())
    accepting = loopback.can_receive()
    recording_sink.accepting = False
    rejecting = loopback.can_receive()

    output = {
        "reference": {
            "revision": revision,
            "version": "0.4.5",
            "method": "Executed pinned LXST APIs in the isolated reference venv, including Null identity and Loopback sink observations; filters use the loaded upstream filter backend.",
            "filterBackend": "native" if Filters.USE_NATIVE_FILTERS else "python",
            "sourceSha256": hashes,
        },
        "resample": {
            "input": resample_input,
            "bitDepth": 16,
            "channels": 2,
            "inputRate": 8000,
            "outputRate": 12000,
            "output": resampled.tolist(),
            "normalizedOutput": normalized.tolist(),
        },
        "resampleBytes": {
            "bitDepth": 16,
            "cases": byte_cases,
        },
        "resamplingCodec": {
            "codec": "Opus",
            "inputRate": codec_input_rate,
            "codecRate": 8_000,
            "channels": 1,
            "input": codec_input.reshape(-1).tolist(),
            "encoded": codec_encoded.hex(),
        },
        "pipeline": {
            "pipelineSetsSourceBackReference": pipeline_source.pipeline is pipeline,
            "pipelineAssignsSinkToSource": pipeline_source.sink is pipeline_sink,
            "pipelineAssignsCodecToSource": pipeline_source.codec is pipeline_codec,
            "pipelineAssignsSourceAndSinkToCodec": (
                pipeline_codec.source is pipeline_source and pipeline_codec.sink is pipeline_sink
            ),
            "startStartsSource": pipeline_running_after_start,
            "stopStopsSource": not pipeline.running,
            "processorArgumentObserved": hasattr(pipeline, "processor"),
            "codecMarkers": codec_markers,
            "codecMarkerRoundTrips": {
                codec.__name__: codec_type(codec_header_byte(codec)[0]) is codec
                for codec in (Raw, Opus, Codec2)
            },
            "rawFrame": {
                "input": pipeline_raw_input.reshape(-1).tolist(),
                "channels": 1,
                "encoded": pipeline_raw_encoded.hex(),
            },
            "packetizerUsesCodecMarkerAndFrameField": (
                packetized_frame == bytes.fromhex(codec_markers["Raw"]) + pipeline_raw_encoded
            ),
            "packetCreateReceipt": create_receipt,
            "packetizerImplementsReliabilityOrFragmentation": False,
            "packetizedFrame": packetized_frame.hex(),
        },
        "filters": {
            "input": filter_input,
            "sampleRate": 8000,
            "highPassCut": 180,
            "highPass": high,
            "lowPassCut": 1200,
            "lowPass": low,
            "bandPass": band,
            "agcInput": agc_input.reshape(-1).tolist(),
            "agcSampleRate": 8000,
            "agc": agc_output,
        },
        "tone": {
            "frequency": 440,
            "gain": 0.2,
            "sampleRate": 48000,
            "channels": 1,
            "samples": tone_output,
        },
        "toneGainTransitions": {
            "frequency": 400,
            "initialGain": 0.04,
            "sampleRate": 48_000,
            "channels": 2,
            "frameMilliseconds": 1,
            "easeMilliseconds": 1,
            "targetGains": transition_targets,
            "framesByEase": tone_transitions,
        },
        "nullCodec": {
            "encodeReturnsSameObject": null_encoded is null_input,
            "decodeReturnsSameObject": null_decoded is null_input,
        },
        "loopback": {
            "maximumFrameBuffer": Loopback.MAX_FRAMES,
            "initialQueuedFrames": len(loopback.frame_deque),
            "startSetsRunning": loopback.should_run,
            "sourceSetterPreservesReference": loopback.source is assigned_source,
            "canReceiveWhenDownstreamAccepts": accepting,
            "canReceiveWhenDownstreamRejects": rejecting,
            "forwardsSameFrame": len(recording_sink.frames) == 1 and recording_sink.frames[0][0] is forwarded_frame,
            "forwardsLoopbackAsSource": len(recording_sink.frames) == 1 and recording_sink.frames[0][1] is loopback,
            "stopClearsRunning": None,
            "constructorTargetFrameMs": Loopback.__init__.__defaults__[0],
            "targetFrameMsUsedByLoopback": False,
            "upstreamClockedOutput": False,
        },
    }
    loopback.stop()
    output["loopback"]["stopClearsRunning"] = not loopback.should_run
    mixer_input = [[0.2, 0.6, -0.5, 0.1], [0.3, 0.5, -0.6, -0.2]]
    mixer_gain = mix_reference(6.0, False)
    mixer_muted = mix_reference(6.0, True)
    output["bandpassAboveNyquist"] = {
        "lowCut": 250,
        "highCut": 8500,
        "sampleRates": [8000, 24000, 48000],
        "input": bandpass_input,
        "inputRate": bandpass_input_rate,
        "outputs": bandpass_outputs,
    }
    output["mixerGain"] = {
        "input": mixer_input,
        "gainDb": 6.0,
        "mixedOutput": mixer_gain,
        "mutedOutput": mixer_muted,
        "gainLinear": 10 ** (6.0 / 10.0),
    }
    fixture = Path(__file__).parent / "Fixtures" / "lxst-audio-processing.json"
    fixture.write_text(json.dumps(output, separators=(",", ":")) + "\n")
    print(f"Wrote {fixture} from LXST {revision}")


if __name__ == "__main__":
    main()
