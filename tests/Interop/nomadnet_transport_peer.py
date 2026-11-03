"""Thin lifecycle adapter for an unmodified pinned Python RNS transport."""
import argparse
import json
import os

import RNS

from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    args = parser.parse_args()
    verify_reference()
    RNS.Reticulum(configdir=args.config, loglevel=RNS.LOG_ERROR)
    print("@@" + json.dumps({"event": "transport-ready", "module": RNS.__file__}), flush=True)
    for line in __import__("sys").stdin:
        if line.strip() == "quit":
            RNS.Reticulum.exit_handler()
            os._exit(0)


if __name__ == "__main__":
    main()
