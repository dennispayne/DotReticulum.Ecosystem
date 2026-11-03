"""Execute clean primary Sideband media APIs; no reference codecs are reimplemented."""
import argparse
import ast
import base64
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import struct
import subprocess
import sys
from types import SimpleNamespace

from PIL import Image, ImageDraw, __version__ as pillow_version
import LXMF
import LXST
import pycodec2

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"
DIGESTS = {
    "sbapp/main.py": "5b962d9ed2ccbc066bb19865d78c77da9ecf4008900f6f0e7e9dd1eb1c4767aa",
    "sbapp/sideband/audioproc.py": "5e2570ece2a08f3f4a09e19e8203b553f5ef13c49995275862f61b2fb9d2ebce",
}


def b64(data):
    return base64.b64encode(data).decode("ascii")


def observe_ogg_packets(data):
    packets = []
    packet = bytearray()
    offset = 0
    while offset < len(data):
        if data[offset:offset + 4] != b"OggS":
            raise RuntimeError("Reference did not produce Ogg")
        count = data[offset + 26]
        lengths = data[offset + 27:offset + 27 + count]
        body = offset + 27 + count
        for length in lengths:
            packet.extend(data[body:body + length])
            body += length
            if length < 255:
                packets.append(bytes(packet))
                packet.clear()
        offset = body
    if packet:
        raise RuntimeError("Reference returned an incomplete Ogg packet")
    return packets


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True, require_lxst=True)
    revision = subprocess.check_output(
        ["git", "-C", str(args.source), "rev-parse", "HEAD"], text=True).strip()
    if revision != PIN:
        raise RuntimeError(f"Expected {PIN}, got {revision}")
    sources = {}
    for name, digest in DIGESTS.items():
        raw = (args.source / name).read_bytes()
        if hashlib.sha256(raw).hexdigest() != digest:
            raise RuntimeError(f"Unexpected source digest: {name}")
        sources[name] = raw
    spec = importlib.util.spec_from_file_location(
        "sideband_reference_audioproc", args.source / "sbapp/sideband/audioproc.py")
    audioproc = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(audioproc)

    image_branch = next(node for node in ast.walk(ast.parse(sources["sbapp/main.py"]))
                        if isinstance(node, ast.If)
                        and ast.unparse(node.test) == "self.attach_type == 'lbimg'")
    image_code = compile(ast.Module(body=[image_branch], type_ignores=[]),
                         "sbapp/main.py:unmodified-image-attachment-branches", "exec")
    work = args.output.parent / "sideband-media-generator"
    work.mkdir(parents=True, exist_ok=True)
    images = []
    try:
        for width, height in [(1601, 801), (37, 19), (501, 1301)]:
            original = Image.new("RGB", (width, height), (31, 78, 123))
            draw = ImageDraw.Draw(original)
            for x in range(0, width, 29):
                draw.rectangle((x, 0, min(x + 13, width - 1), height - 1),
                               fill=(x % 256, (x * 3) % 256, (x * 7) % 256))
            source_path = work / "synthetic.png"
            original.save(source_path, format="png")
            for policy in ("lbimg", "defimg", "hqimg"):
                namespace = {
                    "self": SimpleNamespace(attach_type=policy, attach_path=str(source_path)),
                    "PilImage": Image, "io": io,
                }
                exec(image_code, namespace)
                fmt, encoded = namespace["image"]
                result = Image.open(io.BytesIO(encoded))
                images.append({
                    "policy": policy, "input": b64(source_path.read_bytes()),
                    "input_width": width, "input_height": height,
                    "format": fmt, "width": result.width, "height": result.height,
                    "encoded": b64(encoded), "sha256": hashlib.sha256(encoded).hexdigest(),
                })

        # Integer arithmetic generates original synthetic test PCM, not captured audio.
        samples = [((i * 193) % 20001) - 10000 for i in range(961)]
        codec2 = []
        for mode, bitrate in audioproc.codec2_modes.items():
            encoded = audioproc.encode_codec2(samples, mode)
            decoded = audioproc.decode_codec2(encoded, mode)
            repeated = audioproc.decode_codec2(encoded, mode)
            parameters = pycodec2.Codec2(bitrate)
            codec2.append({
                "mode": mode, "bitrate": bitrate, "encoded": encoded.hex(),
                "decoded_pcm": b64(decoded), "decoded_samples": len(decoded) // 2,
                "samples_per_frame": parameters.samples_per_frame(),
                "bytes_per_frame": parameters.bytes_per_frame(),
                "repeated_decode_sha256": hashlib.sha256(repeated).hexdigest(),
                "decode_is_deterministic": decoded == repeated,
            })
        opus_path = work / "synthetic.ogg"
        pcm = struct.pack("<" + "h" * len(samples), *samples)
        if not audioproc.samples_to_ogg(
                samples=pcm, file_path=str(opus_path), input_rate=8000):
            raise RuntimeError("Actual Sideband samples_to_ogg failed")
        opus = opus_path.read_bytes()
        decoded = audioproc.samples_from_ogg(str(opus_path))
        opus_file = audioproc.OpusFile(str(opus_path))
        state = 0x51DEBAD
        native_samples = []
        for _ in range(7201):
            state = (state * 1664525 + 1013904223) & 0xFFFFFFFF
            native_samples.append(state % 60001 - 30000)
        native_pcm = struct.pack("<" + "h" * len(native_samples), *native_samples)
        if not audioproc.samples_to_ogg(
                samples=native_pcm, file_path=str(opus_path), input_rate=12000, output_rate=12000):
            raise RuntimeError("Actual native-rate samples_to_ogg failed")
        native_ogg = opus_path.read_bytes()
        native_packets = observe_ogg_packets(native_ogg)
        probe = audioproc.OpusBufferedEncoder()
        probe.set_application("audio")
        probe.set_sampling_frequency(12000)
        probe.set_channels(1)
        probe.set_frame_size(60)
        fixture = {
            "sideband_sha": PIN, "source_sha256": DIGESTS,
            "lxmf_sha": "e8ba02483217af43be284a72f1c059c730b7d047",
            "lxst_sha": "eba5abc77fef7fef3bcb6c9c091c80b7f7492c9f",
            "lxst_source_sha256": {
                name: hashlib.sha256((Path(LXST.__file__).parent / "Codecs" / "libs" / "pyogg" / name).read_bytes()).hexdigest()
                for name in ("opus_encoder.py", "opus_buffered_encoder.py", "ogg_opus_writer.py")
            },
            "pillow_version": pillow_version,
            "method": "Actual audioproc functions; unchanged image branches extracted via AST from main.py. Synthetic PNG/PCM. No Sideband GUI or audio hardware.",
            "constants": {name: getattr(LXMF, name) for name in dir(LXMF) if name.startswith("AM_")},
            "images": images, "input_pcm": b64(pcm), "codec2": codec2,
            "input_pcm_sha256": hashlib.sha256(pcm).hexdigest(),
            "opus": {"mode": LXMF.AM_OPUS_OGG, "encoded": b64(opus),
                     "sha256": hashlib.sha256(opus).hexdigest(),
                     "decoded_rate": opus_file.frequency,
                     "decoded_samples": len(opus_file.as_array()),
                     "decoded_8khz_samples": len(decoded)},
            "opus_native12k": {
                "input_pcm": b64(native_pcm),
                "encoded": b64(native_ogg),
                "packets": [packet.hex() for packet in native_packets[2:]],
                "canonical_packet_sha256": hashlib.sha256(b"".join(
                    struct.pack("<I", len(packet)) + packet for packet in native_packets[2:])).hexdigest(),
                "preskip": struct.unpack_from("<H", native_packets[0], 10)[0],
                "algorithmic_delay": probe.get_algorithmic_delay(),
                "maximum_bytes_per_frame": probe._max_bytes_per_frame.value,
                "decoded_samples": len(audioproc.OpusFile(str(opus_path)).as_array()),
            },
        }
        args.output.write_text(json.dumps(fixture, indent=2) + "\n", encoding="utf-8")
    finally:
        for path in work.iterdir():
            path.unlink()
        work.rmdir()


if __name__ == "__main__":
    main()
