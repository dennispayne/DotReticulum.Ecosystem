"""Observe actual primary conversation icon/color policy with synthetic owners."""
import argparse
import ast
import hashlib
import itertools
import json
import subprocess
from pathlib import Path
from types import SimpleNamespace

from reference_environment import verify_reference

PIN = "d069122af14880e43607aed0b71add4c7806fbe0"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    verify_reference(require_lxmf=True)
    actual = subprocess.check_output(["git", "-C", str(args.source), "rev-parse", "HEAD"], text=True).strip()
    if actual != PIN:
        raise RuntimeError("Unexpected primary revision")
    core = subprocess.check_output(["git", "-C", str(args.source), "show", f"{PIN}:sbapp/sideband/core.py"])
    source = subprocess.check_output(["git", "-C", str(args.source), "show", f"{PIN}:sbapp/ui/conversations.py"])
    constants = {}
    for node in ast.walk(ast.parse(core)):
        if isinstance(node, ast.Assign):
            for target in node.targets:
                if isinstance(target, ast.Name) and target.id in ("DEFAULT_APPEARANCE", "CONV_P2P", "CONV_VOICE"):
                    constants[target.id] = ast.literal_eval(node.value)
    if set(constants) != {"DEFAULT_APPEARANCE", "CONV_P2P", "CONV_VOICE"}:
        raise RuntimeError("Missing primary appearance constants")
    defaults = {}
    for node in ast.walk(ast.parse(core)):
        if isinstance(node, ast.Assign):
            for target in node.targets:
                if isinstance(target, ast.Subscript) and isinstance(target.value, ast.Attribute) and target.value.attr == "config":
                    key = target.slice.value if isinstance(target.slice, ast.Constant) else None
                    if key in ("display_style_in_contact_list", "display_style_from_all"):
                        defaults[key] = ast.literal_eval(node.value)
    if set(defaults) != {"display_style_in_contact_list", "display_style_from_all"}:
        raise RuntimeError("Missing primary appearance defaults")
    methods = [node for node in ast.walk(ast.parse(source))
               if isinstance(node, ast.FunctionDef) and node.name in ("trust_icon", "get_icon_colors")]
    if len(methods) != 2:
        raise RuntimeError("Unexpected primary policy methods")
    namespace = {}
    exec(compile(ast.Module(body=methods, type_ignores=[]), "conversations.py", "exec"), namespace)
    appearances = {"absent": None, "default": constants["DEFAULT_APPEARANCE"],
                   "custom": ["rocket", [0.2, 0.4, 0.6, 1], [1, 0.8, 0.4, 1]]}
    cases = []
    for enabled, from_all, trusted, unread, requests in itertools.product((False, True), repeat=5):
        for label, appearance in appearances.items():
            peer = SimpleNamespace(
                **constants,
                config={"display_style_in_contact_list": enabled, "display_style_from_all": from_all},
                peer_appearance=lambda *_args, **_kwargs: appearance,
                requests_allowed_from=lambda *_args, **_kwargs: requests)
            owner = SimpleNamespace(app=SimpleNamespace(sideband=peer))
            conversation = {"type": constants["CONV_P2P"], "dest": bytes(16),
                            "trust": int(trusted), "unread": unread}
            cases.append({"enabled": enabled, "from_all": from_all, "trusted": trusted,
                          "unread": unread, "requests_allowed": requests, "appearance": label,
                          "icon": namespace["trust_icon"](owner, conversation),
                          "colors": namespace["get_icon_colors"](owner, conversation)})
    result = {
        "revision": PIN, "source_sha256": hashlib.sha256(source).hexdigest(),
        "core_sha256": hashlib.sha256(core).hexdigest(), "default_appearance": constants["DEFAULT_APPEARANCE"],
        "source_defaults": defaults,
        "method": "Actual AST-extracted trust_icon/get_icon_colors and source constants; synthetic LXMF conversation, owner, cached appearance and request-authorization observations only. No constructor, assets, Kivy, network or independent authorization claim",
        "cases": cases}
    with args.output.open("w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(result, indent=4) + "\n")


if __name__ == "__main__":
    main()
