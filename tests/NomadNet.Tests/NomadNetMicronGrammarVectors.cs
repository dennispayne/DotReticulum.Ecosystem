// Executed primary observations; regenerate with GenerateNomadNetMicronGrammarVectors.py.
namespace NomadNet.Tests;

internal static class NomadNetMicronGrammarVectors
{
    internal const string Json = """
{
  "revision": "f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2",
  "parserSha256": "9932db8038b6380742e2586250dc77b2509b72d7ed8a6fdfd954f03602cf4e03",
  "tableConverterSha256": "ac74160c788c62b139866d91113903bc10d18466539a373624737d6d4f4d0ef2",
  "imageWidgetSha256": "9ece9d789e9b61656f52b72b89b76bd141a0772190765485856510ede28ef631",
  "imageLayoutBoundary": "Actual parse_image/ImageWidget setters/_display_size; synthetic 80x40 decoded metadata and patched terminal queries (1x2 cells, 80x32 terminal). No image decoder or Kitty transmission claim.",
  "boundary": "Execute pinned Micron APIs on original synthetic grammar, never copied guide assets.\n\nA synthetic singleton supplies the real Urwid screen/palette and theme. No parser,\ntable converter or form behavior is patched. This is headless grammar evidence,\nnot an unmodified primary fullscreen terminal or network interoperability claim.\n",
  "cases": [
    {
      "name": "trailing-backtick",
      "source": "prefix`",
      "observed": {
        "text": "prefix",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "prefix",
            "align": "left",
            "spans": [
              {
                "text": "prefix",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "trailing-backslash",
      "source": "prefix\\",
      "observed": {
        "text": "prefix",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "prefix",
            "align": "left",
            "spans": [
              {
                "text": "prefix",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "ordinary-backslash",
      "source": "one\\two",
      "observed": {
        "text": "onetwo",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "onetwo",
            "align": "left",
            "spans": [
              {
                "text": "onetwo",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "escaped-slash",
      "source": "one\\\\two",
      "observed": {
        "text": "one\\two",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "one\\two",
            "align": "left",
            "spans": [
              {
                "text": "one\\two",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "escaped-format",
      "source": "one\\`!two",
      "observed": {
        "text": "one`!two",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "one`!two",
            "align": "left",
            "spans": [
              {
                "text": "one`!two",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "escaped-heading",
      "source": "\\>not a heading",
      "observed": {
        "text": ">not a heading",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": ">not a heading",
            "align": "left",
            "spans": [
              {
                "text": ">not a heading",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "escaped-comment",
      "source": "\\#not a comment",
      "observed": {
        "text": "#not a comment",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "#not a comment",
            "align": "left",
            "spans": [
              {
                "text": "#not a comment",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "escaped-divider",
      "source": "\\-not a divider",
      "observed": {
        "text": "-not a divider",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "-not a divider",
            "align": "left",
            "spans": [
              {
                "text": "-not a divider",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "divider-default",
      "source": "-",
      "observed": {
        "text": "────────────────────────────────────────",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "divider-grave",
      "source": "-`",
      "observed": {
        "text": "````````````````````````````````````````",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "divider-backslash",
      "source": "-\\",
      "observed": {
        "text": "\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "divider-tab",
      "source": "-\t",
      "observed": {
        "text": "────────────────────────────────────────",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "divider-nonbmp",
      "source": "-😀",
      "observed": {
        "text": "────────────────────────────────────────",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "escaped-reset",
      "source": "\\<not a reset",
      "observed": {
        "text": "<not a reset",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "<not a reset",
            "align": "left",
            "spans": [
              {
                "text": "<not a reset",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "escaped-first-backtick",
      "source": "\\`!not bold",
      "observed": {
        "text": "`!not bold",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "`!not bold",
            "align": "left",
            "spans": [
              {
                "text": "`!not bold",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "escaped-table-directive",
      "source": "\\`t\n| A | B |\n|---|---|\n| one | two |\n`t",
      "observed": {
        "text": "┌─────┬─────┐\n│ A   │ B   │\n├─────┼─────┤\n│ one │ two │\n└─────┴─────┘",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "┌─────┬─────┐",
            "align": "left",
            "spans": [
              {
                "text": "┌─────┬─────┐",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ A   │ B   │",
            "align": "left",
            "spans": [
              {
                "text": "│ A   │ B   │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "├─────┼─────┤",
            "align": "left",
            "spans": [
              {
                "text": "├─────┼─────┤",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ one │ two │",
            "align": "left",
            "spans": [
              {
                "text": "│ one │ two │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "└─────┴─────┘",
            "align": "left",
            "spans": [
              {
                "text": "└─────┴─────┘",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "unknown-format",
      "source": "one`ztwo",
      "observed": {
        "text": "onetwo",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "onetwo",
            "align": "left",
            "spans": [
              {
                "text": "onetwo",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "short-fg",
      "source": "one`F12",
      "observed": {
        "text": "one12",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "one12",
            "align": "left",
            "spans": [
              {
                "text": "one12",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "short-bg",
      "source": "one`B",
      "observed": {
        "text": "one",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "one",
            "align": "left",
            "spans": [
              {
                "text": "one",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "short-truecolor",
      "source": "one`FT12345",
      "observed": {
        "text": "one345",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "one345",
            "align": "left",
            "spans": [
              {
                "text": "one",
                "style": "micron_222_default_"
              },
              {
                "text": "345",
                "style": "micron_T12_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "valid-colors",
      "source": "`F123one`FT112233two`B456three`BT445566four`f`bfive",
      "observed": {
        "text": "onetwothreefourfive",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "onetwothreefourfive",
            "align": "left",
            "spans": [
              {
                "text": "one",
                "style": "micron_123_default_"
              },
              {
                "text": "two",
                "style": "micron_112233_default_"
              },
              {
                "text": "three",
                "style": "micron_112233_456_"
              },
              {
                "text": "four",
                "style": "micron_112233_445566_"
              },
              {
                "text": "five",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "styles",
      "source": "`!one`! `_two`_ `*three`* ``four `c`r`l`afive",
      "observed": {
        "text": "one two three four five",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "one two three four five",
            "align": "left",
            "spans": [
              {
                "text": "one",
                "style": "micron_222_default_,bold"
              },
              {
                "text": " ",
                "style": "micron_222_default_"
              },
              {
                "text": "two",
                "style": "micron_222_default_,underline"
              },
              {
                "text": " ",
                "style": "micron_222_default_"
              },
              {
                "text": "three",
                "style": "micron_222_default_,italics"
              },
              {
                "text": " four five",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "empty-link",
      "source": "one`[]two",
      "observed": {
        "text": "onetwo",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "onetwo",
            "align": "left",
            "spans": [
              {
                "text": "onetwo",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "empty-link-target",
      "source": "one`[label`]two",
      "observed": {
        "text": "onetwo",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "onetwo",
            "align": "left",
            "spans": [
              {
                "text": "onetwo",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "too-many-link-parts",
      "source": "one`[a`b`c`d]two",
      "observed": {
        "text": "onetwo",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "onetwo",
            "align": "left",
            "spans": [
              {
                "text": "onetwo",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "missing-link-end",
      "source": "one`[twothree",
      "observed": {
        "text": "onetwothree",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "onetwothree",
            "align": "left",
            "spans": [
              {
                "text": "onetwothree",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "link-label-fallback",
      "source": "`[`/page/original.mu]",
      "observed": {
        "text": "/page/original.mu",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "/page/original.mu",
            "align": "left",
            "spans": [
              {
                "text": "/page/original.mu",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "link-literal-label",
      "source": "`[\\literal`/page/original.mu]",
      "observed": {
        "text": "\\literal",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "\\literal",
            "align": "left",
            "spans": [
              {
                "text": "\\literal",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "empty-field",
      "source": "one`<name`>two",
      "observed": {
        "text": "onetwo",
        "fields": [
          {
            "kind": "field",
            "name": "name",
            "value": "",
            "masked": false,
            "rows": 1
          }
        ],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "missing-field-backtick",
      "source": "one`<nametwo",
      "observed": {
        "text": "onenametwo",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "onenametwo",
            "align": "left",
            "spans": [
              {
                "text": "onenametwo",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "missing-field-end",
      "source": "one`<name`two",
      "observed": {
        "text": "onenamewo",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "onenamewo",
            "align": "left",
            "spans": [
              {
                "text": "onenamewo",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "field-controls-heading",
      "source": ">>`<name`value>",
      "observed": {
        "text": "",
        "fields": [
          {
            "kind": "field",
            "name": "name",
            "value": "value",
            "masked": false,
            "rows": 1
          }
        ],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "field-mask-precedence",
      "source": "`<?!|flag|yes|*`Label> `<^!|pick|one|*`First>",
      "observed": {
        "text": "",
        "fields": [
          {
            "kind": "checkbox",
            "name": "flag",
            "value": "yes",
            "selected": true,
            "label": "Label"
          },
          {
            "kind": "radio",
            "name": "pick",
            "value": "one",
            "selected": true,
            "label": "First"
          }
        ],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "field-rows-extra-x",
      "source": "`<12x3x4|input`value>",
      "observed": {
        "text": "",
        "fields": [
          {
            "kind": "field",
            "name": "input",
            "value": "value",
            "masked": false,
            "rows": 1
          }
        ],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "field-empty-name",
      "source": "`<`value>",
      "observed": {
        "text": "",
        "fields": [
          {
            "kind": "field",
            "name": "",
            "value": "value",
            "masked": false,
            "rows": 1
          }
        ],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "comment",
      "source": "#comment\nvisible",
      "observed": {
        "text": "visible",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "visible",
            "align": "left",
            "spans": [
              {
                "text": "visible",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "literal",
      "source": "`=\n#literal\n>literal\n\\`=\n`=\nvisible",
      "observed": {
        "text": "#literal\n>literal\n`=\nvisible",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "#literal",
            "align": "left",
            "spans": [
              {
                "text": "#literal",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": ">literal",
            "align": "left",
            "spans": [
              {
                "text": ">literal",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "`=",
            "align": "left",
            "spans": [
              {
                "text": "`=",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "visible",
            "align": "left",
            "spans": [
              {
                "text": "visible",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "recursive-reset-comment",
      "source": ">Heading\n<##comment\nvisible",
      "observed": {
        "text": "Heading\nvisible",
        "fields": [],
        "anchors": {
          "heading": 0
        },
        "headers": [
          0
        ],
        "levels": {
          "0": 1
        },
        "styled": [
          {
            "text": "Heading",
            "align": "left",
            "spans": [
              {
                "text": "",
                "style": null
              },
              {
                "text": "Heading",
                "style": "micron_000_777_"
              }
            ]
          },
          {
            "text": "visible",
            "align": "left",
            "spans": [
              {
                "text": "visible",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "recursive-reset-heading",
      "source": ">>Deep\n<<<>>New\ntext",
      "observed": {
        "text": "  Deep\n  New\ntext",
        "fields": [],
        "anchors": {
          "deep": 0,
          "new": 1
        },
        "headers": [
          0,
          1
        ],
        "levels": {
          "0": 2,
          "1": 2
        },
        "styled": [
          {
            "text": "  Deep",
            "align": "left",
            "spans": [
              {
                "text": "  ",
                "style": null
              },
              {
                "text": "Deep",
                "style": "micron_111_aaa_"
              }
            ]
          },
          {
            "text": "  New",
            "align": "left",
            "spans": [
              {
                "text": "  ",
                "style": null
              },
              {
                "text": "New",
                "style": "micron_111_aaa_"
              }
            ]
          },
          {
            "text": "text",
            "align": "left",
            "spans": [
              {
                "text": "text",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "heading-empty",
      "source": ">",
      "observed": {
        "text": "",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "heading-field-sanitization",
      "source": ">>Heading `<name`value>\nafter",
      "observed": {
        "text": "Heading \nafter",
        "fields": [
          {
            "kind": "field",
            "name": "name",
            "value": "value",
            "masked": false,
            "rows": 1
          }
        ],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "after",
            "align": "left",
            "spans": [
              {
                "text": "after",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "heading-field-comment",
      "source": ">>#comment `<name`value>\nafter",
      "observed": {
        "text": "after",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "after",
            "align": "left",
            "spans": [
              {
                "text": "after",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "table-leading-escape",
      "source": "`t\n\\| A | B |\n|---|---|\n| one | two |\n`t",
      "observed": {
        "text": "┌─────┬─────┐\n│ A   │ B   │\n├─────┼─────┤\n│ one │ two │\n└─────┴─────┘",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "┌─────┬─────┐",
            "align": "left",
            "spans": [
              {
                "text": "┌─────┬─────┐",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ A   │ B   │",
            "align": "left",
            "spans": [
              {
                "text": "│ A   │ B   │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "├─────┼─────┤",
            "align": "left",
            "spans": [
              {
                "text": "├─────┼─────┤",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ one │ two │",
            "align": "left",
            "spans": [
              {
                "text": "│ one │ two │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "└─────┴─────┘",
            "align": "left",
            "spans": [
              {
                "text": "└─────┴─────┘",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "empty-line",
      "source": "one\n\nthree",
      "observed": {
        "text": "one\n\nthree",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "one",
            "align": "left",
            "spans": [
              {
                "text": "one",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "",
            "align": "left",
            "spans": []
          },
          {
            "text": "three",
            "align": "left",
            "spans": [
              {
                "text": "three",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "standalone-anchor",
      "source": "`:here\nvisible",
      "observed": {
        "text": "visible",
        "fields": [],
        "anchors": {
          "here": 0
        },
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "visible",
            "align": "left",
            "spans": [
              {
                "text": "visible",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "anchor-before-comment",
      "source": "`:here\n#comment\nvisible",
      "observed": {
        "text": "visible",
        "fields": [],
        "anchors": {
          "here": 0
        },
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "visible",
            "align": "left",
            "spans": [
              {
                "text": "visible",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "anchor-empty-before-heading",
      "source": "`:here\n>Heading",
      "observed": {
        "text": "Heading",
        "fields": [],
        "anchors": {
          "here": 0,
          "heading": 0
        },
        "headers": [
          0
        ],
        "levels": {
          "0": 1
        },
        "styled": [
          {
            "text": "Heading",
            "align": "left",
            "spans": [
              {
                "text": "",
                "style": null
              },
              {
                "text": "Heading",
                "style": "micron_000_777_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "anchor-duplicate",
      "source": "`:same first\n`:same second",
      "observed": {
        "text": " first\n second",
        "fields": [],
        "anchors": {
          "same": 0
        },
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": " first",
            "align": "left",
            "spans": [
              {
                "text": " first",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": " second",
            "align": "left",
            "spans": [
              {
                "text": " second",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "anchor-unicode-alphanumeric",
      "source": "`:place²Ⅳ𐐀 visible\nsecond",
      "observed": {
        "text": " visible\nsecond",
        "fields": [],
        "anchors": {
          "place²Ⅳ𐐀": 0
        },
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": " visible",
            "align": "left",
            "spans": [
              {
                "text": " visible",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "second",
            "align": "left",
            "spans": [
              {
                "text": "second",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "table-empty",
      "source": "`t\n`t\nvisible",
      "observed": {
        "text": "visible",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "visible",
            "align": "left",
            "spans": [
              {
                "text": "visible",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "table-unclosed",
      "source": "`t\n| a | b |\n| c | d |",
      "observed": {
        "text": "",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "table-single",
      "source": "`t\n| a | b |\n`t\nvisible",
      "observed": {
        "text": "visible",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "visible",
            "align": "left",
            "spans": [
              {
                "text": "visible",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "table-basic",
      "source": "`t\n| A | B |\n|---|---|\n| one | two |\n`t",
      "observed": {
        "text": "┌─────┬─────┐\n│ A   │ B   │\n├─────┼─────┤\n│ one │ two │\n└─────┴─────┘",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "┌─────┬─────┐",
            "align": "left",
            "spans": [
              {
                "text": "┌─────┬─────┐",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ A   │ B   │",
            "align": "left",
            "spans": [
              {
                "text": "│ A   │ B   │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "├─────┼─────┤",
            "align": "left",
            "spans": [
              {
                "text": "├─────┼─────┤",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ one │ two │",
            "align": "left",
            "spans": [
              {
                "text": "│ one │ two │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "└─────┴─────┘",
            "align": "left",
            "spans": [
              {
                "text": "└─────┴─────┘",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "table-aligned",
      "source": "`tr40\n| A | B |\n|:---:|---:|\n| x | longer |\n| longer | x |\n`t",
      "observed": {
        "text": "┌────────┬────────┐\n│ A      │ B      │\n├────────┼────────┤\n│   x    │ longer │\n│ longer │      x │\n└────────┴────────┘",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "┌────────┬────────┐",
            "align": "right",
            "spans": [
              {
                "text": "┌────────┬────────┐",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ A      │ B      │",
            "align": "right",
            "spans": [
              {
                "text": "│ A      │ B      │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "├────────┼────────┤",
            "align": "right",
            "spans": [
              {
                "text": "├────────┼────────┤",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│   x    │ longer │",
            "align": "right",
            "spans": [
              {
                "text": "│   x    │ longer │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ longer │      x │",
            "align": "right",
            "spans": [
              {
                "text": "│ longer │      x │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "└────────┴────────┘",
            "align": "right",
            "spans": [
              {
                "text": "└────────┴────────┘",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "table-limited",
      "source": "`t20\n| Heading | Other |\n|---|---|\n| abcdefghijklmnop | 1234567890 |\n`t",
      "observed": {
        "text": "┌─────┬────────────┐\n│ He… │ Other      │\n├─────┼────────────┤\n│ ab… │ 1234567890 │\n└─────┴────────────┘",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "┌─────┬────────────┐",
            "align": "left",
            "spans": [
              {
                "text": "┌─────┬────────────┐",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ He… │ Other      │",
            "align": "left",
            "spans": [
              {
                "text": "│ He… │ Other      │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "├─────┼────────────┤",
            "align": "left",
            "spans": [
              {
                "text": "├─────┼────────────┤",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ ab… │ 1234567890 │",
            "align": "left",
            "spans": [
              {
                "text": "│ ab… │ 1234567890 │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "└─────┴────────────┘",
            "align": "left",
            "spans": [
              {
                "text": "└─────┴────────────┘",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "table-escaped-pipe",
      "source": "`t\n| A | B |\n|---|---|\n| a\\|b | c |\n`t",
      "observed": {
        "text": "┌─────┬─────┐\n│ A   │ B   │\n├─────┼─────┤\n│ a|b │ c   │\n└─────┴─────┘",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "┌─────┬─────┐",
            "align": "left",
            "spans": [
              {
                "text": "┌─────┬─────┐",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ A   │ B   │",
            "align": "left",
            "spans": [
              {
                "text": "│ A   │ B   │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "├─────┼─────┤",
            "align": "left",
            "spans": [
              {
                "text": "├─────┼─────┤",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ a|b │ c   │",
            "align": "left",
            "spans": [
              {
                "text": "│ a|b │ c   │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "└─────┴─────┘",
            "align": "left",
            "spans": [
              {
                "text": "└─────┴─────┘",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "table-uneven",
      "source": "`t\n| A | B |\n|---|\n| short |\n| one | two | ignored |\n`t",
      "observed": {
        "text": "┌───────┬─────┐\n│ A     │ B   │\n├───────┼─────┤\n│ short │     │\n│ one   │ two │\n└───────┴─────┘",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "┌───────┬─────┐",
            "align": "left",
            "spans": [
              {
                "text": "┌───────┬─────┐",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ A     │ B   │",
            "align": "left",
            "spans": [
              {
                "text": "│ A     │ B   │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "├───────┼─────┤",
            "align": "left",
            "spans": [
              {
                "text": "├───────┼─────┤",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ short │     │",
            "align": "left",
            "spans": [
              {
                "text": "│ short │     │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ one   │ two │",
            "align": "left",
            "spans": [
              {
                "text": "│ one   │ two │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "└───────┴─────┘",
            "align": "left",
            "spans": [
              {
                "text": "└───────┴─────┘",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "table-style-width",
      "source": "`t\n| `!A`! | B |\n|---|---|\n| `F123one`f | two |\n`t",
      "observed": {
        "text": "┌─────┬─────┐\n│ A   │ B   │\n├─────┼─────┤\n│ one │ two │\n└─────┴─────┘",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "┌─────┬─────┐",
            "align": "left",
            "spans": [
              {
                "text": "┌─────┬─────┐",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ A   │ B   │",
            "align": "left",
            "spans": [
              {
                "text": "│ ",
                "style": "micron_222_default_"
              },
              {
                "text": "A",
                "style": "micron_222_default_,bold"
              },
              {
                "text": "   │ B   │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "├─────┼─────┤",
            "align": "left",
            "spans": [
              {
                "text": "├─────┼─────┤",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ one │ two │",
            "align": "left",
            "spans": [
              {
                "text": "│ ",
                "style": "micron_222_default_"
              },
              {
                "text": "one",
                "style": "micron_123_default_"
              },
              {
                "text": " │ two │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "└─────┴─────┘",
            "align": "left",
            "spans": [
              {
                "text": "└─────┴─────┘",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "table-style-limited",
      "source": "`t20\n| A | B |\n|---|---|\n| `F123abcdefghijklmno`f | 1234567890 |\n`t",
      "observed": {
        "text": "┌─────┬────────────┐\n│ A   │ B          │\n├─────┼────────────┤\n│ ab… │ 1234567890 │\n└─────┴────────────┘",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "┌─────┬────────────┐",
            "align": "left",
            "spans": [
              {
                "text": "┌─────┬────────────┐",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ A   │ B          │",
            "align": "left",
            "spans": [
              {
                "text": "│ A   │ B          │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "├─────┼────────────┤",
            "align": "left",
            "spans": [
              {
                "text": "├─────┼────────────┤",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ ab… │ 1234567890 │",
            "align": "left",
            "spans": [
              {
                "text": "│ ",
                "style": "micron_222_default_"
              },
              {
                "text": "ab",
                "style": "micron_123_default_"
              },
              {
                "text": "… │ 1234567890 │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "└─────┴────────────┘",
            "align": "left",
            "spans": [
              {
                "text": "└─────┴────────────┘",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "reset-fold",
      "source": "`->Fold\nhidden\n<visible",
      "observed": {
        "text": "▸ Fold\nhidden\nvisible",
        "fields": [],
        "anchors": {
          "fold": 0
        },
        "headers": [
          0
        ],
        "levels": {
          "0": 1
        },
        "styled": [
          {
            "text": "▸ Fold",
            "align": "left",
            "spans": [
              {
                "text": "▸ ",
                "style": null
              },
              {
                "text": "Fold",
                "style": "micron_000_777_"
              }
            ]
          },
          {
            "text": "hidden",
            "align": "left",
            "spans": [
              {
                "text": "hidden",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "visible",
            "align": "left",
            "spans": [
              {
                "text": "visible",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "style-cross-line",
      "source": "`!bold\nstill`! plain",
      "observed": {
        "text": "bold\nstill plain",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "bold",
            "align": "left",
            "spans": [
              {
                "text": "bold",
                "style": "micron_222_default_,bold"
              }
            ]
          },
          {
            "text": "still plain",
            "align": "left",
            "spans": [
              {
                "text": "still",
                "style": "micron_222_default_,bold"
              },
              {
                "text": " plain",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "style-all-reset",
      "source": "`!`_`*`FT123456`BT654321marked`` plain",
      "observed": {
        "text": "marked plain",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "marked plain",
            "align": "left",
            "spans": [
              {
                "text": "marked",
                "style": "micron_123456_654321_,bold,underline,italics"
              },
              {
                "text": " plain",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "style-colors-default",
      "source": "`F123short`f default `FTabcdeflong`f `B456back`b `Fg50gray`f",
      "observed": {
        "text": "short default long back gray",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "short default long back gray",
            "align": "left",
            "spans": [
              {
                "text": "short",
                "style": "micron_123_default_"
              },
              {
                "text": " default ",
                "style": "micron_222_default_"
              },
              {
                "text": "long",
                "style": "micron_abcdef_default_"
              },
              {
                "text": " ",
                "style": "micron_222_default_"
              },
              {
                "text": "back",
                "style": "micron_222_456_"
              },
              {
                "text": " ",
                "style": "micron_222_default_"
              },
              {
                "text": "gray",
                "style": "micron_g50_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "align-cross-line",
      "source": "`ccenter\nstill\n`rright\n`lleft\n`creset``default",
      "observed": {
        "text": "center\nstill\nright\nleft\nresetdefault",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "center",
            "align": "center",
            "spans": [
              {
                "text": "center",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "still",
            "align": "center",
            "spans": [
              {
                "text": "still",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "right",
            "align": "right",
            "spans": [
              {
                "text": "right",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "left",
            "align": "left",
            "spans": [
              {
                "text": "left",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "resetdefault",
            "align": "left",
            "spans": [
              {
                "text": "resetdefault",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "align-last-control",
      "source": "`cone`rtwo`lthree",
      "observed": {
        "text": "onetwothree",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "onetwothree",
            "align": "left",
            "spans": [
              {
                "text": "onetwothree",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "heading-latched-style",
      "source": "`!body\n>heading\nbody\n`!plain",
      "observed": {
        "text": "body\nheading\nbody\nplain",
        "fields": [],
        "anchors": {
          "heading": 1
        },
        "headers": [
          1
        ],
        "levels": {
          "1": 1
        },
        "styled": [
          {
            "text": "body",
            "align": "left",
            "spans": [
              {
                "text": "body",
                "style": "micron_222_default_,bold"
              }
            ]
          },
          {
            "text": "heading",
            "align": "left",
            "spans": [
              {
                "text": "",
                "style": null
              },
              {
                "text": "heading",
                "style": "micron_000_777_"
              }
            ]
          },
          {
            "text": "body",
            "align": "left",
            "spans": [
              {
                "text": "body",
                "style": "micron_222_default_,bold"
              }
            ]
          },
          {
            "text": "plain",
            "align": "left",
            "spans": [
              {
                "text": "plain",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "heading-explicit-style",
      "source": ">`!bold`_under`*italic`F123color",
      "observed": {
        "text": "boldunderitaliccolor",
        "fields": [],
        "anchors": {
          "boldunderitaliccolor": 0
        },
        "headers": [
          0
        ],
        "levels": {
          "0": 1
        },
        "styled": [
          {
            "text": "boldunderitaliccolor",
            "align": "left",
            "spans": [
              {
                "text": "",
                "style": null
              },
              {
                "text": "bold",
                "style": "micron_000_777_,bold"
              },
              {
                "text": "under",
                "style": "micron_000_777_,bold,underline"
              },
              {
                "text": "italic",
                "style": "micron_000_777_,bold,underline,italics"
              },
              {
                "text": "color",
                "style": "micron_123_777_,bold,underline,italics"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "custom-fold-glyphs",
      "source": "#!fold OPEN SHUT\n`->Fold\nhidden\n<end",
      "observed": {
        "text": "SHUT Fold\nhidden\nend",
        "fields": [],
        "anchors": {
          "fold": 0
        },
        "headers": [
          0
        ],
        "levels": {
          "0": 1
        },
        "styled": [
          {
            "text": "SHUT Fold",
            "align": "left",
            "spans": [
              {
                "text": "SHUT ",
                "style": null
              },
              {
                "text": "Fold",
                "style": "micron_000_777_"
              }
            ]
          },
          {
            "text": "hidden",
            "align": "left",
            "spans": [
              {
                "text": "hidden",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "end",
            "align": "left",
            "spans": [
              {
                "text": "end",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "OPEN",
          "SHUT"
        ]
      }
    },
    {
      "name": "table-align-follow",
      "source": "`tr40\n| A | B |\n|---|---|\n| x | y |\n`t\nafter",
      "observed": {
        "text": "┌─────┬─────┐\n│ A   │ B   │\n├─────┼─────┤\n│ x   │ y   │\n└─────┴─────┘\nafter",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "┌─────┬─────┐",
            "align": "right",
            "spans": [
              {
                "text": "┌─────┬─────┐",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ A   │ B   │",
            "align": "right",
            "spans": [
              {
                "text": "│ A   │ B   │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "├─────┼─────┤",
            "align": "right",
            "spans": [
              {
                "text": "├─────┼─────┤",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ x   │ y   │",
            "align": "right",
            "spans": [
              {
                "text": "│ x   │ y   │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "└─────┴─────┘",
            "align": "right",
            "spans": [
              {
                "text": "└─────┴─────┘",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "after",
            "align": "left",
            "spans": [
              {
                "text": "after",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "table-unclosed-style",
      "source": "`t\n| `!A | B |\n|---|---|\n| x | y |\n`t\nafter",
      "observed": {
        "text": "┌─────┬─────┐\n│ A   │ B   │\n├─────┼─────┤\n│ x   │ y   │\n└─────┴─────┘\nafter",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "┌─────┬─────┐",
            "align": "left",
            "spans": [
              {
                "text": "┌─────┬─────┐",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ A   │ B   │",
            "align": "left",
            "spans": [
              {
                "text": "│ ",
                "style": "micron_222_default_"
              },
              {
                "text": "A   │ B   │",
                "style": "micron_222_default_,bold"
              }
            ]
          },
          {
            "text": "├─────┼─────┤",
            "align": "left",
            "spans": [
              {
                "text": "├─────┼─────┤",
                "style": "micron_222_default_,bold"
              }
            ]
          },
          {
            "text": "│ x   │ y   │",
            "align": "left",
            "spans": [
              {
                "text": "│ x   │ y   │",
                "style": "micron_222_default_,bold"
              }
            ]
          },
          {
            "text": "└─────┴─────┘",
            "align": "left",
            "spans": [
              {
                "text": "└─────┴─────┘",
                "style": "micron_222_default_,bold"
              }
            ]
          },
          {
            "text": "after",
            "align": "left",
            "spans": [
              {
                "text": "after",
                "style": "micron_222_default_,bold"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "style-literal-state",
      "source": "`!bold\n`=\n`!literal\n`=\ncontinued",
      "observed": {
        "text": "bold\n`!literal\ncontinued",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "bold",
            "align": "left",
            "spans": [
              {
                "text": "bold",
                "style": "micron_222_default_,bold"
              }
            ]
          },
          {
            "text": "`!literal",
            "align": "left",
            "spans": [
              {
                "text": "`!literal",
                "style": "micron_222_default_,bold"
              }
            ]
          },
          {
            "text": "continued",
            "align": "left",
            "spans": [
              {
                "text": "continued",
                "style": "micron_222_default_,bold"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "table-align-inherited",
      "source": "`cstart\n`t\n| A | B |\n|---|---|\n| x | y |\n`t\nafter",
      "observed": {
        "text": "start\n┌─────┬─────┐\n│ A   │ B   │\n├─────┼─────┤\n│ x   │ y   │\n└─────┴─────┘\nafter",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "start",
            "align": "center",
            "spans": [
              {
                "text": "start",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "┌─────┬─────┐",
            "align": "center",
            "spans": [
              {
                "text": "┌─────┬─────┐",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ A   │ B   │",
            "align": "center",
            "spans": [
              {
                "text": "│ A   │ B   │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "├─────┼─────┤",
            "align": "center",
            "spans": [
              {
                "text": "├─────┼─────┤",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ x   │ y   │",
            "align": "center",
            "spans": [
              {
                "text": "│ x   │ y   │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "└─────┴─────┘",
            "align": "center",
            "spans": [
              {
                "text": "└─────┴─────┘",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "after",
            "align": "center",
            "spans": [
              {
                "text": "after",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "fold-empty-formatting-heading",
      "source": "`->`!\n`->Real\nhidden\n<end",
      "error": "TypeError",
      "message": "object of type 'NoneType' has no len()",
      "traceback": "Traceback (most recent call last):\n  File \"<repository>\\tests\\Interop\\GenerateNomadNetMicronGrammarVectors.py\", line 166, in main\n    rows.append({\"name\": name, \"source\": text, \"observed\": observe(text)})\n                                                           ^^^^^^^^^^^^^\n  File \"<repository>\\tests\\Interop\\GenerateNomadNetMicronGrammarVectors.py\", line 39, in observe\n    widgets = micron.markup_to_attrmaps(text)\n              ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^\n  File \"<primary>\\nomadnet\\ui\\textui\\MicronParser.py\", line 133, in markup_to_attrmaps\n    display_widgets = parse_line(line, state, url_delegate)\n                      ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^\n  File \"<primary>\\nomadnet\\ui\\textui\\MicronParser.py\", line 585, in parse_line\n    if len(output) > 0:\n       ^^^^^^^^^^^\nTypeError: object of type 'NoneType' has no len()\n"
    },
    {
      "name": "table-style-reset-width",
      "source": "`t\n| abcdef`` | B |\n|---|---|\n| x | y |\n`t",
      "observed": {
        "text": "┌──────────┬─────┐\n│ abcdef │ B   │\n├──────────┼─────┤\n│ x        │ y   │\n└──────────┴─────┘",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "┌──────────┬─────┐",
            "align": "left",
            "spans": [
              {
                "text": "┌──────────┬─────┐",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ abcdef │ B   │",
            "align": "left",
            "spans": [
              {
                "text": "│ abcdef │ B   │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "├──────────┼─────┤",
            "align": "left",
            "spans": [
              {
                "text": "├──────────┼─────┤",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ x        │ y   │",
            "align": "left",
            "spans": [
              {
                "text": "│ x        │ y   │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "└──────────┴─────┘",
            "align": "left",
            "spans": [
              {
                "text": "└──────────┴─────┘",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "table-gray-width",
      "source": "`t\n| `Fg50abcdef`f | B |\n|---|---|\n| x | y |\n`t",
      "observed": {
        "text": "┌─────────────┬─────┐\n│ abcdef │ B   │\n├─────────────┼─────┤\n│ x           │ y   │\n└─────────────┴─────┘",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "┌─────────────┬─────┐",
            "align": "left",
            "spans": [
              {
                "text": "┌─────────────┬─────┐",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ abcdef │ B   │",
            "align": "left",
            "spans": [
              {
                "text": "│ ",
                "style": "micron_222_default_"
              },
              {
                "text": "abcdef",
                "style": "micron_g50_default_"
              },
              {
                "text": " │ B   │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "├─────────────┼─────┤",
            "align": "left",
            "spans": [
              {
                "text": "├─────────────┼─────┤",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ x           │ y   │",
            "align": "left",
            "spans": [
              {
                "text": "│ x           │ y   │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "└─────────────┴─────┘",
            "align": "left",
            "spans": [
              {
                "text": "└─────────────┴─────┘",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "name": "table-leading-reset-cell",
      "source": "`t\n| A |\n|---|\n<original\n`t",
      "observed": {
        "text": "┌───────────┐\n│ A         │\n├───────────┤\n│ <original │\n└───────────┘",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "┌───────────┐",
            "align": "left",
            "spans": [
              {
                "text": "┌───────────┐",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ A         │",
            "align": "left",
            "spans": [
              {
                "text": "│ A         │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "├───────────┤",
            "align": "left",
            "spans": [
              {
                "text": "├───────────┤",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "│ <original │",
            "align": "left",
            "spans": [
              {
                "text": "│ <original │",
                "style": "micron_222_default_"
              }
            ]
          },
          {
            "text": "└───────────┘",
            "align": "left",
            "spans": [
              {
                "text": "└───────────┘",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    }
  ],
  "partials": [
    {
      "source": "/page/a.mu}",
      "observed": {
        "id": null,
        "target": "/page/a.mu",
        "refresh": null,
        "fields": ""
      }
    },
    {
      "source": "/page/a.mu`0.5`pid=a}",
      "observed": {
        "id": "a",
        "target": "/page/a.mu",
        "refresh": null,
        "fields": "pid=a"
      }
    },
    {
      "source": "/page/a.mu`2`*|pid=first|pid=last}",
      "observed": {
        "id": "last",
        "target": "/page/a.mu",
        "refresh": 2.0,
        "fields": "*|pid=first|pid=last"
      }
    },
    {
      "source": "/page/a.mu`1`pid=a=b}",
      "observed": {
        "id": "a",
        "target": "/page/a.mu",
        "refresh": 1.0,
        "fields": "pid=a=b"
      }
    },
    {
      "source": "/page/a.mu`bad}",
      "observed": null
    },
    {
      "source": "/page/a.mu`1`*`extra}",
      "observed": null
    }
  ],
  "images": [
    {
      "source": "alt`/image.webp)",
      "width": null,
      "height": null,
      "align": "|",
      "display": [
        40,
        10
      ]
    },
    {
      "source": "alt`w=12`h=4`a=r`/image.webp)",
      "width": 12,
      "height": 4,
      "align": ">",
      "display": [
        12,
        4
      ]
    },
    {
      "source": "alt`w=50%`h=25.5%`a=l`/image.webp)",
      "width": "50%",
      "height": "25.5%",
      "align": "<",
      "display": [
        20,
        6
      ]
    },
    {
      "source": "alt`w=n`h=N`a=c`/image.webp)",
      "width": "n",
      "height": "n",
      "align": "|",
      "display": [
        40,
        10
      ]
    },
    {
      "source": "alt`w=7`w=15`a=>`/image.webp)",
      "width": 15,
      "height": null,
      "align": ">",
      "display": [
        15,
        4
      ]
    },
    {
      "source": "alt`w=0`h=bad`a=bad`/image.webp)",
      "width": null,
      "height": null,
      "align": "|",
      "display": [
        40,
        10
      ]
    },
    {
      "source": "alt`w=１２`h=٢٥%`a=right`/image.webp)",
      "width": 12,
      "height": "٢٥%",
      "align": ">",
      "display": [
        12,
        6
      ]
    },
    {
      "source": "alt`h=22.9%`/image.webp)",
      "width": null,
      "height": "22.9%",
      "align": "|",
      "display": [
        20,
        5
      ]
    },
    {
      "source": "alt`w=37.9%`/image.webp)",
      "width": "37.9%",
      "height": null,
      "align": "|",
      "display": [
        15,
        4
      ]
    }
  ],
  "sourceBytes": {
    "nomadnet/ui/textui/MicronParser.py": {
      "gitBlobSha256": "995a8fd8883ec57cd173d117b6758447e508ca2ddf96d8521260cc70e18a8e4e",
      "filesystemSha256": "9932db8038b6380742e2586250dc77b2509b72d7ed8a6fdfd954f03602cf4e03",
      "gitBytes": 52335,
      "filesystemBytes": 53678,
      "filesystemCrLfCount": 1343,
      "equalAfterCrLfNormalization": true
    },
    "nomadnet/ui/textui/images/widget.py": {
      "gitBlobSha256": "08a9e667dddd4b732ad94141f4c68b49ad477a0ee36c41ba0e0d5fd33314ad1c",
      "filesystemSha256": "9ece9d789e9b61656f52b72b89b76bd141a0772190765485856510ede28ef631",
      "gitBytes": 11285,
      "filesystemBytes": 11568,
      "filesystemCrLfCount": 283,
      "equalAfterCrLfNormalization": true
    },
    "nomadnet/util.py": {
      "gitBlobSha256": "77a4368c73b8a84ef2e9df41b4e423bcb1d0677e12ed18debe445996fa3a9e38",
      "filesystemSha256": "c1c3173e35f15887b470e5cca108aa60687e4b097d4a29254ff5f8f24cc1e61e",
      "gitBytes": 7441,
      "filesystemBytes": 7662,
      "filesystemCrLfCount": 221,
      "equalAfterCrLfNormalization": true
    }
  },
  "consumerControls": [
    {
      "source": "`rRight `cCenter `lLeft",
      "conversation": {
        "text": "rRight cCenter Left",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "rRight cCenter Left",
            "align": "left",
            "spans": [
              {
                "text": "rRight ",
                "style": "micron_ddd_383838_"
              },
              {
                "text": "cCenter Left",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      },
      "rrcMicronOnly": {
        "text": "Right Center Left",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "Right Center Left",
            "align": "left",
            "spans": [
              {
                "text": "Right Center Left",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "source": "**bold** and *italic*",
      "conversation": {
        "text": "bold and italic",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "bold and italic",
            "align": "left",
            "spans": [
              {
                "text": "bold",
                "style": "micron_222_default_,bold"
              },
              {
                "text": " and ",
                "style": "micron_222_default_"
              },
              {
                "text": "italic",
                "style": "micron_222_default_,italics"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      },
      "rrcMicronOnly": {
        "text": "**bold** and *italic*",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "**bold** and *italic*",
            "align": "left",
            "spans": [
              {
                "text": "**bold** and *italic*",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    },
    {
      "source": "¦!bold¦! ¦_under¦_ ¦*italic¦* ¦FT123456color¦f ¦BT654321back¦b",
      "conversation": {
        "text": "¦!bold¦! ¦under¦ ¦italic¦ ¦FT123456color¦f ¦BT654321back¦b",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "¦!bold¦! ¦under¦ ¦italic¦ ¦FT123456color¦f ¦BT654321back¦b",
            "align": "left",
            "spans": [
              {
                "text": "¦!bold¦! ¦",
                "style": "micron_222_default_"
              },
              {
                "text": "under¦",
                "style": "micron_222_default_,italics"
              },
              {
                "text": " ¦",
                "style": "micron_222_default_"
              },
              {
                "text": "italic¦",
                "style": "micron_222_default_,italics"
              },
              {
                "text": " ¦FT123456color¦f ¦BT654321back¦b",
                "style": "micron_222_default_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      },
      "rrcMicronOnly": {
        "text": "bold under italic color back",
        "fields": [],
        "anchors": {},
        "headers": [],
        "levels": {},
        "styled": [
          {
            "text": "bold under italic color back",
            "align": "left",
            "spans": [
              {
                "text": "bold",
                "style": "micron_222_default_,bold"
              },
              {
                "text": " ",
                "style": "micron_222_default_"
              },
              {
                "text": "under",
                "style": "micron_222_default_,underline"
              },
              {
                "text": " ",
                "style": "micron_222_default_"
              },
              {
                "text": "italic",
                "style": "micron_222_default_,italics"
              },
              {
                "text": " ",
                "style": "micron_222_default_"
              },
              {
                "text": "color",
                "style": "micron_123456_default_"
              },
              {
                "text": " ",
                "style": "micron_222_default_"
              },
              {
                "text": "back",
                "style": "micron_222_654321_"
              }
            ]
          }
        ],
        "foldGlyphs": [
          "▾",
          "▸"
        ]
      }
    }
  ]
}
""";
}
