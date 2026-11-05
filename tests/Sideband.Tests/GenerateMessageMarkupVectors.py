"""Observe primary message renderer selection and pre-font message markup."""
import argparse
import ast
import hashlib
import importlib
import json
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from types import MethodType, SimpleNamespace

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
    files = ("sbapp/sideband/core.py", "sbapp/main.py", "sbapp/ui/messages.py", "sbapp/md/md.py")
    sources = {
        name: subprocess.check_output(["git", "-C", str(args.source), "show", f"{PIN}:{name}"])
        for name in files
    }
    sys.path.insert(0, str(args.source))
    import RNS
    import LXMF
    import mistune
    import kivy
    from kivy.utils import escape_markup
    converter = importlib.import_module("sbapp.md.md")
    if Path(converter.__file__).resolve() != (args.source / "sbapp/md/md.py").resolve():
        raise RuntimeError("Wrong primary Markdown converter")
    if hashlib.sha256(Path(converter.__file__).read_bytes().replace(b"\r\n", b"\n")).hexdigest() != hashlib.sha256(sources["sbapp/md/md.py"]).hexdigest():
        raise RuntimeError("Primary Markdown converter differs from its Git blob")

    namespace = {"RNS": RNS, "LXMF": LXMF, "re": re, "time": time, "sp": lambda value: value}
    for path, names in (
        ("sbapp/sideband/core.py", {"has_bb_markup", "send_message"}),
        ("sbapp/main.py", {"md_to_bbcode", "process_bb_markup"}),
    ):
        methods = [node for node in ast.walk(ast.parse(sources[path]))
                   if isinstance(node, ast.FunctionDef) and node.name in names]
        if len(methods) != len(names):
            raise RuntimeError("Unexpected primary methods")
        exec(compile(ast.Module(body=methods, type_ignores=[]), path, "exec"), namespace)

    update = next(node for node in ast.walk(ast.parse(sources["sbapp/ui/messages.py"]))
                  if isinstance(node, ast.FunctionDef) and node.name == "update_widget")
    loop = next(node for node in ast.walk(update) if isinstance(node, ast.For) and
                isinstance(node.target, ast.Name) and node.target.id == "m")
    body = loop.body[0].body
    stop = next(index for index, node in enumerate(body) if isinstance(node, ast.Assign) and
                any(isinstance(target, ast.Name) and target.id == "message_text_for_markup" for target in node.targets))
    render = compile(ast.Module(body=body[:stop], type_ignores=[]), "messages.py", "exec")
    ui = SimpleNamespace(mdconv=converter.mdconv)
    ui.md_to_bbcode = MethodType(namespace["md_to_bbcode"], ui)
    ui.process_bb_markup = MethodType(namespace["process_bb_markup"], ui)

    send_inputs = ["", "plain", "#!md\n", "#!md\n**bold**", "#!md\r\n**bold**",
                   "[b]bold[/b]", "before [b]bold[/b]", " [b]bold[/b]",
                   "[unknown]literal", "[\n]", "[x\ny]", "[x]", "[/]", "[b", "\n[b]x[/b]"]
    sent = []
    with tempfile.TemporaryDirectory(prefix="sideband-markup-reference-") as directory:
        config = Path(directory)
        (config / "config").write_text("[reticulum]\nshare_instance=no\n[interfaces]\n", encoding="utf-8")
        RNS.Reticulum(configdir=str(config), loglevel=0)
        source = RNS.Destination(RNS.Identity(), RNS.Destination.IN, RNS.Destination.SINGLE, "lxmf", "delivery")
        remote_identity = RNS.Identity()
        remote = RNS.Destination(remote_identity, RNS.Destination.OUT, RNS.Destination.SINGLE, "lxmf", "delivery")
        RNS.Identity.remember(bytes(32), remote.hash, remote_identity.get_public_key())
        pending = []
        router = SimpleNamespace(delivery_link_available=lambda _: True,
                                 get_outbound_propagation_node=lambda: None,
                                 handle_outbound=pending.append)
        owner = SimpleNamespace(allow_service_dispatch=False, is_client=False, lxmf_destination=source,
                                message_router=router, is_trusted=lambda _: False,
                                get_message_fields=lambda _: {}, message_notification_no_display=lambda _: None)
        owner.has_bb_markup = MethodType(namespace["has_bb_markup"], owner)
        for markdown in (False, True):
            owner.config = {"compose_in_markdown": markdown}
            for content in send_inputs:
                pending.clear()
                accepted = namespace["send_message"](owner, content, remote.hash, False, no_display=True)
                if accepted and len(pending) != 1:
                    raise RuntimeError("Primary dispatch did not produce exactly one message")
                message = pending[0] if pending else None
                sent.append({"markdown": markdown, "input": content, "accepted": accepted,
                             "content": message.content.decode("utf-8") if message else None,
                             "renderer": message.fields.get(LXMF.FIELD_RENDERER) if message else None})
        RNS.Reticulum.exit_handler()

    samples = [
        "", "plain & [literal]", "[b]bold[/b] [i]italic[/i] [u]under[/u] [s]strike[/s]",
        "[b]nested [i]both[/i] bold[/b]", "[size=24]large[/size]", "[color=#ff0000]red[/color]",
        "[code]x < y & z[/code]", "[url=https://example.invalid]link[/url]",
        "[img]https://example.invalid/a.png[/img]", "[heading=1]heading[/heading]",
        "[list]\n[*]one\n[*]two[/list]", "**bold** and *italic*", "~~strike~~ ++insert++ ==mark==",
        "H~2~O x^2^", "`[b]code[/b]`", "# Heading", "a\n\nb", "- one\n- two",
        "1. one\n2. two", "> quote", "[link](https://example.invalid/a)",
        "![alt](https://example.invalid/a.png)", "```text\n<&>\n```",
        "<div>block</div>", "inline <b>HTML</b>", "- [ ] task\n- [x] done",
        "note[^1]\n\n[^1]: body", ">! spoiler !<", "---", "^^underlined^^",
        "inline >! hidden !< end", "```text\n'\u00e9' & \"quoted\"\n```",
        "[b]unclosed", "[B]case sensitive[/B]", "[size=0]zero[/size]", "[color=red]named[/color]",
    ]
    rendered = []
    for renderer in (None, LXMF.RENDERER_MARKDOWN, LXMF.RENDERER_BBCODE, 255):
        for trusted_only, trusted in ((False, False), (True, False), (True, True)):
            ui.sideband = SimpleNamespace(config={"trusted_markup_only": trusted_only})
            view = SimpleNamespace(app=ui, is_trusted=trusted)
            for content in samples:
                fields = {} if renderer is None else {LXMF.FIELD_RENDERER: renderer}
                scope = {"self": view, "m": {"content": content.encode("utf-8"),
                         "lxm": SimpleNamespace(fields=fields)}, "LXMF": LXMF,
                         "escape_markup": escape_markup, "RNS": RNS}
                exec(render, scope)
                rendered.append({"renderer": renderer, "trusted_only": trusted_only,
                                 "trusted": trusted, "content": content,
                                 "markup": scope["message_input"].decode("utf-8")})
    result = {
        "revision": PIN,
        "source_sha256": {name: hashlib.sha256(value).hexdigest() for name, value in sources.items()},
        "mistune_version": mistune.__version__,
        "kivy_version": kivy.__version__,
        "renderers": {"field": LXMF.FIELD_RENDERER, "markdown": LXMF.RENDERER_MARKDOWN, "bbcode": LXMF.RENDERER_BBCODE},
        "method": "Actual AST-extracted primary send_message/has_bb_markup, real RNS destinations/LXMF messages and synthetic router/notification boundary; actual primary mdconv, md_to_bbcode/process_bb_markup, actual pre-font update_widget statements and Kivy escape_markup. Unit font density sp(x)=x; no widgets, URL-ref/font shaping, GUI, devices or live transport claim.",
        "sending": sent, "rendering": rendered,
    }
    with args.output.open("w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(result, indent=2, ensure_ascii=True) + "\n")


if __name__ == "__main__":
    main()
