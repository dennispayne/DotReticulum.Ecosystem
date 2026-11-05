"""Execute pinned Micron APIs on original synthetic grammar, never copied guide assets.

A synthetic singleton supplies the real Urwid screen/palette and theme. No parser,
table converter or form behavior is patched. This is headless grammar evidence,
not an unmodified primary fullscreen terminal or network interoperability claim.
"""
import argparse
import hashlib
import importlib
import json
import subprocess
import traceback
import sys
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from GenerateNomadNetClientRuntimeVectors import reference, REVISION


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--csharp")
    parser.add_argument("--observations", required=True)
    args = parser.parse_args()
    source, imports = reference()
    import nomadnet
    import urwid
    micron = importlib.import_module("nomadnet.ui.textui.MicronParser")
    converter = importlib.import_module("RNS.Utilities.rngit.util")
    screen = urwid.raw_display.Screen()
    previous = nomadnet.NomadNetworkApp._shared_instance
    nomadnet.NomadNetworkApp._shared_instance = SimpleNamespace(
        config={"textui": {"theme": "dark"}},
        ui=SimpleNamespace(screen=screen, colormode=256, glyphs={}))

    def observe(text):
        widgets = micron.markup_to_attrmaps(text)
        fields = []
        def plain(widget):
            if hasattr(widget, "field_name"):
                kind = "radio" if isinstance(widget, urwid.RadioButton) else "checkbox" if isinstance(widget, urwid.CheckBox) else "field"
                item = {"kind": kind, "name": widget.field_name}
                if kind == "field":
                    item.update(value=widget.edit_text, masked=bool(widget._mask), rows=widget._min_rows)
                else:
                    item.update(value=widget.field_value, selected=widget.state, label=widget.label)
                fields.append(item)
                return ""
            if isinstance(widget, urwid.Text):
                return widget.get_text()[0]
            if isinstance(widget, urwid.Divider):
                return widget.render((40,)).text[0].decode("utf-8")
            if isinstance(widget, urwid.Columns):
                return "".join(plain(child) for child, _ in widget.contents)
            if hasattr(widget, "original_widget"):
                return plain(widget.original_widget)
            if hasattr(widget, "_w"):
                return plain(widget._w)
            raise TypeError(type(widget).__name__)
        styled = []
        for widget in widgets:
            target = widget
            while hasattr(target, "original_widget"):
                target = target.original_widget
            if isinstance(target, micron.CollapsibleHeading):
                target = target._w
                while hasattr(target, "original_widget"):
                    target = target.original_widget
            if isinstance(target, urwid.Text):
                text, attributes = target.get_text()
                position = 0
                spans = []
                for attribute, length in attributes:
                    spans.append({"text": text[position:position + length], "style": attribute})
                    position += length
                styled.append({"text": text, "align": str(target.align), "spans": spans})
        return {"text": "\n".join(plain(widget) for widget in widgets).rstrip(),
                "fields": fields, "anchors": widgets.anchors,
                "headers": widgets.header_rows, "levels": widgets.header_levels, "styled": styled,
                "foldGlyphs": widgets.fold_glyphs}

    cases = {
        "trailing-backtick": "prefix`",
        "trailing-backslash": "prefix\\",
        "ordinary-backslash": "one\\two",
        "escaped-slash": "one\\\\two",
        "escaped-format": "one\\`!two",
        "escaped-heading": "\\>not a heading",
        "escaped-comment": "\\#not a comment",
        "escaped-divider": "\\-not a divider",
        "divider-default": "-",
        "divider-grave": "-`",
        "divider-backslash": "-\\",
        "divider-tab": "-\t",
        "divider-nonbmp": "-😀",
        "escaped-reset": "\\<not a reset",
        "escaped-first-backtick": "\\`!not bold",
        "escaped-table-directive": "\\`t\n| A | B |\n|---|---|\n| one | two |\n`t",
        "unknown-format": "one`ztwo",
        "short-fg": "one`F12",
        "short-bg": "one`B",
        "short-truecolor": "one`FT12345",
        "valid-colors": "`F123one`FT112233two`B456three`BT445566four`f`bfive",
        "styles": "`!one`! `_two`_ `*three`* ``four `c`r`l`afive",
        "empty-link": "one`[]two",
        "empty-link-target": "one`[label`]two",
        "too-many-link-parts": "one`[a`b`c`d]two",
        "missing-link-end": "one`[twothree",
        "link-label-fallback": "`[`/page/original.mu]",
        "link-literal-label": "`[\\literal`/page/original.mu]",
        "empty-field": "one`<name`>two",
        "missing-field-backtick": "one`<nametwo",
        "missing-field-end": "one`<name`two",
        "field-controls-heading": ">>`<name`value>",
        "field-mask-precedence": "`<?!|flag|yes|*`Label> `<^!|pick|one|*`First>",
        "field-rows-extra-x": "`<12x3x4|input`value>",
        "field-empty-name": "`<`value>",
        "comment": "#comment\nvisible",
        "literal": "`=\n#literal\n>literal\n\\`=\n`=\nvisible",
        "recursive-reset-comment": ">Heading\n<##comment\nvisible",
        "recursive-reset-heading": ">>Deep\n<<<>>New\ntext",
        "heading-empty": ">",
        "heading-field-sanitization": ">>Heading `<name`value>\nafter",
        "heading-field-comment": ">>#comment `<name`value>\nafter",
        "table-leading-escape": "`t\n\\| A | B |\n|---|---|\n| one | two |\n`t",
        "empty-line": "one\n\nthree",
        "standalone-anchor": "`:here\nvisible",
        "anchor-before-comment": "`:here\n#comment\nvisible",
        "anchor-empty-before-heading": "`:here\n>Heading",
        "anchor-duplicate": "`:same first\n`:same second",
        "anchor-unicode-alphanumeric": "`:place²Ⅳ𐐀 visible\nsecond",
        "table-empty": "`t\n`t\nvisible",
        "table-unclosed": "`t\n| a | b |\n| c | d |",
        "table-single": "`t\n| a | b |\n`t\nvisible",
        "table-basic": "`t\n| A | B |\n|---|---|\n| one | two |\n`t",
        "table-aligned": "`tr40\n| A | B |\n|:---:|---:|\n| x | longer |\n| longer | x |\n`t",
        "table-limited": "`t20\n| Heading | Other |\n|---|---|\n| abcdefghijklmnop | 1234567890 |\n`t",
        "table-escaped-pipe": "`t\n| A | B |\n|---|---|\n| a\\|b | c |\n`t",
        "table-uneven": "`t\n| A | B |\n|---|\n| short |\n| one | two | ignored |\n`t",
        "table-style-width": "`t\n| `!A`! | B |\n|---|---|\n| `F123one`f | two |\n`t",
        "table-style-limited": "`t20\n| A | B |\n|---|---|\n| `F123abcdefghijklmno`f | 1234567890 |\n`t",
        "reset-fold": "`->Fold\nhidden\n<visible",
        "style-cross-line": "`!bold\nstill`! plain",
        "style-all-reset": "`!`_`*`FT123456`BT654321marked`` plain",
        "style-colors-default": "`F123short`f default `FTabcdeflong`f `B456back`b `Fg50gray`f",
        "align-cross-line": "`ccenter\nstill\n`rright\n`lleft\n`creset``default",
        "align-last-control": "`cone`rtwo`lthree",
        "heading-latched-style": "`!body\n>heading\nbody\n`!plain",
        "heading-explicit-style": ">`!bold`_under`*italic`F123color",
        "custom-fold-glyphs": "#!fold OPEN SHUT\n`->Fold\nhidden\n<end",
        "table-align-follow": "`tr40\n| A | B |\n|---|---|\n| x | y |\n`t\nafter",
        "table-unclosed-style": "`t\n| `!A | B |\n|---|---|\n| x | y |\n`t\nafter",
        "style-literal-state": "`!bold\n`=\n`!literal\n`=\ncontinued",
        "table-align-inherited": "`cstart\n`t\n| A | B |\n|---|---|\n| x | y |\n`t\nafter",
        "fold-empty-formatting-heading": "`->`!\n`->Real\nhidden\n<end",
        "table-style-reset-width": "`t\n| abcdef`` | B |\n|---|---|\n| x | y |\n`t",
        "table-gray-width": "`t\n| `Fg50abcdef`f | B |\n|---|---|\n| x | y |\n`t",
        "table-leading-reset-cell": "`t\n| A |\n|---|\n<original\n`t",
    }
    try:
        rows = []
        for name, text in cases.items():
            try:
                rows.append({"name": name, "source": text, "observed": observe(text)})
            except Exception as error:
                trace = traceback.format_exc()
                print(trace, file=sys.stderr)
                trace = trace.replace(str(source), "<primary>").replace(str(Path(__file__).resolve().parents[2]), "<repository>")
                rows.append({"name": name, "source": text, "error": type(error).__name__, "message": str(error),
                             "traceback": trace})
        partials = []
        for text in ("/page/a.mu}", "/page/a.mu`0.5`pid=a}", "/page/a.mu`2`*|pid=first|pid=last}",
                     "/page/a.mu`1`pid=a=b}", "/page/a.mu`bad}", "/page/a.mu`1`*`extra}"):
            parsed = micron.parse_partial(text)
            partials.append({"source": text, "observed": None if parsed is None else {
                "id": parsed[0].partial_id, "target": parsed[0].partial_url,
                "refresh": parsed[0].partial_refresh, "fields": "|".join(parsed[0].partial_fields)}})
        images = []
        delegate = SimpleNamespace(resolve_image=lambda _: None, g={"image": "[image]"})
        for text in ("alt`/image.webp)", "alt`w=12`h=4`a=r`/image.webp)",
                     "alt`w=50%`h=25.5%`a=l`/image.webp)", "alt`w=n`h=N`a=c`/image.webp)",
                     "alt`w=7`w=15`a=>`/image.webp)", "alt`w=0`h=bad`a=bad`/image.webp)",
                     "alt`w=１２`h=٢٥%`a=right`/image.webp)", "alt`h=22.9%`/image.webp)",
                     "alt`w=37.9%`/image.webp)"):
            parsed = micron.parse_image(text, micron.default_state(), delegate)
            image = parsed[0]
            image._ti_data = SimpleNamespace(ok=True, size=(80, 40))
            with patch.object(importlib.import_module(
                    micron.ImageWidget.__module__)._termlib, "get_cell_size", return_value=(1, 2)), \
                 patch.object(importlib.import_module(micron.ImageWidget.__module__)._termlib,
                              "get_terminal_size", return_value=(80, 32)):
                size = image._display_size(40, 20)
            images.append({"source": text, "width": image.width, "height": image.height,
                           "align": image.align, "display": size})
        result = {"revision": REVISION, "parserSha256": hashlib.sha256(
            (source / "nomadnet" / "ui" / "textui" / "MicronParser.py").read_bytes()).hexdigest(),
            "tableConverterSha256": hashlib.sha256(Path(converter.__file__).read_bytes()).hexdigest(),
            "imageWidgetSha256": hashlib.sha256(Path(importlib.import_module(
                micron.ImageWidget.__module__).__file__).read_bytes()).hexdigest(),
            "imageLayoutBoundary": "Actual parse_image/ImageWidget setters/_display_size; synthetic 80x40 decoded metadata and patched terminal queries (1x2 cells, 80x32 terminal). No image decoder or Kitty transmission claim.",
            "boundary": __doc__, "cases": rows, "partials": partials, "images": images}
        result["sourceBytes"] = {}
        for name in ("nomadnet/ui/textui/MicronParser.py", "nomadnet/ui/textui/images/widget.py", "nomadnet/util.py"):
            canonical = subprocess.check_output(["git", "-C", str(source), "show", f"{REVISION}:{name}"])
            filesystem = (source / name).read_bytes()
            result["sourceBytes"][name] = {
                "gitBlobSha256": hashlib.sha256(canonical).hexdigest(),
                "filesystemSha256": hashlib.sha256(filesystem).hexdigest(),
                "gitBytes": len(canonical), "filesystemBytes": len(filesystem),
                "filesystemCrLfCount": filesystem.count(b"\r\n"),
                "equalAfterCrLfNormalization": filesystem.replace(b"\r\n", b"\n") == canonical}
        from nomadnet import util
        from RNS.Utilities.rngit.util import MarkdownToMicron
        result["consumerControls"] = []
        for text in ("`rRight `cCenter `lLeft", "**bold** and *italic*",
                     "¦!bold¦! ¦_under¦_ ¦*italic¦* ¦FT123456color¦f ¦BT654321back¦b"):
            result["consumerControls"].append({
                "source": text,
                "conversation": observe(util.strip_modifiers(util.strip_non_formatting_tags(
                    MarkdownToMicron(max_width=80).format_block(text)))),
                "rrcMicronOnly": observe(util.strip_modifiers(util.strip_non_formatting_tags(
                    util.unescape_micron(text))))})
        content = json.dumps(result, indent=2, ensure_ascii=False) + "\n"
        Path(args.output).write_text(content, encoding="utf-8", newline="\n")
        if args.csharp:
            Path(args.csharp).write_text(
                "// Executed primary observations; regenerate with GenerateNomadNetMicronGrammarVectors.py.\n"
                "namespace NomadNet.Tests;\n\ninternal static class NomadNetMicronGrammarVectors\n{\n"
                '    internal const string Json = """\n' + content + '""";\n}\n',
                encoding="utf-8", newline="\n")
        Path(args.observations).write_text(json.dumps({"imports": imports, "parser": micron.__file__, "tableConverter": converter.__file__,
            "urwid": urwid.__file__, "version": urwid.__version__, "boundary": __doc__}, indent=2) + "\n",
            encoding="utf-8", newline="\n")
    finally:
        nomadnet.NomadNetworkApp._shared_instance = previous


if __name__ == "__main__":
    main()
