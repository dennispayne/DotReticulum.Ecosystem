"""Observe pinned FilePlayer state/completion with fake queue and clock boundaries."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_LXST_SHA, verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--working-directory", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    import LXST.Primitives.Players as players
    import LXST

    working = Path(args.working_directory)
    working.mkdir(parents=True, exist_ok=True)
    audio = working / "reference-input.opus"
    baseline = json.loads((Path(__file__).parent / "Fixtures" / "lxst-file-vectors.json").read_text())
    audio.write_bytes(base64.b64decode(baseline["file_base64"]))
    try:
        player = object.__new__(players.FilePlayer)
        old_source = SimpleNamespace(should_run=True)
        player._FilePlayer__source = old_source
        player._FilePlayer__sink = SimpleNamespace(frame_deque=[])
        player._FilePlayer__gain = 0.0
        player._FilePlayer__loop = False
        player._FilePlayer__raw = LXST.Codecs.Raw()
        player._FilePlayer__loopback = LXST.Sources.Loopback()
        player.set_source(str(audio))
        source_observation = {
            "input_timed": player._FilePlayer__source.timed,
            "target_frame_ms": player._FilePlayer__source.target_frame_ms,
            "old_source_running_after_replacement": old_source.should_run,
            "replacement_source_running": player._FilePlayer__source.should_run,
            "player_running_after_replacement": bool(player.running),
        }
        player._FilePlayer__source.release()
    finally:
        audio.unlink()

    observations = []
    for source_present in [False, True]:
        for source_running in [False, True]:
            for queued in [0, 1, 3]:
                player = object.__new__(players.FilePlayer)
                player._FilePlayer__source = SimpleNamespace(should_run=source_running) if source_present else None
                player._FilePlayer__sink = SimpleNamespace(frame_deque=[None] * queued)
                observations.append({
                    "source_present": source_present,
                    "source_running": source_running,
                    "queued": queued,
                    "running": bool(player.running),
                    "playing": bool(player.playing),
                })

    completed = []
    for release in [False, True]:
        player = object.__new__(players.FilePlayer)
        events = []
        source = SimpleNamespace(should_run=False, release=lambda: events.append("source-release"))
        sink = SimpleNamespace(frame_deque=[None] * 3)
        player._FilePlayer__source = source
        player._FilePlayer__sink = sink
        player._FilePlayer__release_on_finish = release
        player._FilePlayer__finished_callback = lambda p: events.append(
            {"callback_running": bool(p.running), "queued": len(p._FilePlayer__sink.frame_deque)})
        player._FilePlayer__input_pipeline = SimpleNamespace(release=lambda: events.append("input-release"))
        player._FilePlayer__output_pipeline = SimpleNamespace(release=lambda: events.append("output-release"))
        player._FilePlayer__loopback = object()
        sleeps = []

        def sleep(seconds):
            sleeps.append(seconds)
            if seconds == 0.1:
                sink.frame_deque.pop()

        original_time = players.time
        players.time = SimpleNamespace(sleep=sleep)
        try:
            player._FilePlayer__callback_job()
        finally:
            players.time = original_time
        completed.append({
            "release_on_finish": release, "sleeps": sleeps, "events": events,
            "released": player._FilePlayer__source is None,
        })

    source = Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"]) / "LXST" / "Primitives" / "Players.py"
    result = {
        "revision": PINNED_LXST_SHA,
        "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "method": "Actual FilePlayer.running/playing and __callback_job; only source/queue/pipeline/clock boundary objects substituted. No player method replaced.",
        "states": observations,
        "source": source_observation,
        "completion": completed,
    }
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
