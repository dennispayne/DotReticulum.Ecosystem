"""Explicit test-only device backend, not a physical/audio-quality oracle.

Only the device backend factories and public pipeline sink composition are
replaced. Telephone, codecs, packetization, link callbacks and protocol clocks
remain the actual pinned upstream implementation.
"""
import hashlib
import threading
import time
from types import SimpleNamespace

import numpy as np


class SyntheticDeviceBackend:
    SAMPLERATE = 48000

    def __init__(self, preferred_device=None, samplerate=48000):
        self.samplerate = samplerate
        self.channels = 2
        self.bitdepth = 32
        self.device = SimpleNamespace(channels=2)
        self.offset = 0

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False

    def get_recorder(self, samples_per_frame):
        return self

    def record(self, numframes):
        time.sleep(numframes / self.samplerate)
        indices = np.arange(self.offset, self.offset + numframes)
        self.offset += numframes
        samples = (.15 * np.sin(2 * np.pi * 660 * indices / self.samplerate)).astype(np.float32)
        return np.repeat(samples[:, None], 2, axis=1)

    def get_player(self, samples_per_frame=None, low_latency=None):
        return self

    def play(self, frame):
        time.sleep(len(frame) / self.samplerate)

    def release_recorder(self):
        pass

    def release_player(self):
        pass

    def flush(self):
        pass


class FrameObservations:
    def __init__(self):
        self.lock = threading.Lock()
        self.received = 0
        self.transmitted = 0
        self.codecs = {}
        self.latest = {}

    def attach(self, phone):
        if not phone.active_call or not hasattr(phone.active_call, "audio_source"):
            return
        source = phone.active_call.audio_source
        if not isinstance(source.sink, ObservingSink):
            source.sink = ObservingSink(source.sink, self, True)
        pipeline = phone.transmit_pipeline
        if pipeline and not isinstance(pipeline.sink, ObservingSink):
            pipeline.source.sink = ObservingSink(pipeline.sink, self, False)

    def snapshot(self):
        with self.lock:
            return dict(received=self.received, transmitted=self.transmitted,
                        codecs=dict(self.codecs), latest=dict(self.latest))


class ObservingSink:
    def __init__(self, sink, observations, receive):
        self.sink = sink
        self.observations = observations
        self.receive = receive

    def __getattr__(self, name):
        return getattr(self.sink, name)

    def can_receive(self, from_source=None):
        return self.sink.can_receive(from_source)

    def handle_frame(self, frame, source, **kwargs):
        # Forward to the actual sink first; observers must not replace processing.
        self.sink.handle_frame(frame, source, **kwargs)
        direction = "receive" if self.receive else "transmit"
        codec = type(source.codec).__name__
        raw = frame.astype("<f4").tobytes() if self.receive else bytes(frame)
        detail = dict(codec=codec, bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest())
        if self.receive:
            detail.update(samples=int(frame.shape[0]), channels=int(frame.shape[1]),
                          finite=bool(np.all(np.isfinite(frame))))
        with self.observations.lock:
            if self.receive:
                self.observations.received += 1
            else:
                self.observations.transmitted += 1
            key = direction + ":" + codec
            self.observations.codecs[key] = self.observations.codecs.get(key, 0) + 1
            self.observations.latest[direction] = detail
