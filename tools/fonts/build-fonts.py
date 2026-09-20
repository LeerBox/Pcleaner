"""
Builds PCleaner's own UI typeface from the Ubuntu font family (Canonical Ltd., Ubuntu Font Licence 1.0).

    Ubuntu derivative PCleaner    Regular / Medium / Bold

The family name follows clause 2(c) of the Ubuntu Font Licence to the letter: a Modified Version that is not
"Substantially Changed" must keep the original name and append "derivative X", X being the new work. The licence
also requires the copyright notice and the licence text to travel with every copy - they are written into the
name table (IDs 0, 13, 14) and shipped as LICENCE-Ubuntu-UFL.txt next to the fonts.

What is changed for PCleaner (none of it alters a single outline):
  * Only the scripts a Windows cleaner needs are kept (Latin, punctuation, currency, math, shapes); other scripts
    - and the arrows Ubuntu never had - fall back to Windows' own fonts through WPF's per-character fallback.
    Three faces end up at roughly half of the original size, which matters for a single-file executable.
  * The "fi"/"fl" ligatures are dropped. PCleaner shows file names and paths; letters must stay individual.
  * Ubuntu already uses tabular figures by default - sizes, counts and dates line up in every column. The "tnum"
    variants of the remaining symbols are made the default too, so the feature never has to be switched on.
  * Dalton Maag's manual TrueType hinting is preserved - the reason classic Ubuntu renders crisply at 12-13 px.
  * Renamed with proper legacy and typographic name records so WPF/DirectWrite groups the weights correctly and
    the fonts cannot collide with an Ubuntu installed on the machine.

Run:  python tools/fonts/build-fonts.py --source <folder with Ubuntu-Regular/Medium/Bold.ttf + UFL.txt> --out src/PCleaner.App/Fonts
The files are the ones Google Fonts distributes: https://github.com/google/fonts/tree/main/ufl/ubuntu
Requires: pip install fonttools
"""

from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path

from fontTools import subset
from fontTools.ttLib import TTFont

ORIGINAL_FAMILY = "Ubuntu"
FAMILY = f"{ORIGINAL_FAMILY} derivative PCleaner"  # Ubuntu Font Licence 1.0, clause 2(c)
POSTSCRIPT_FAMILY = "UbuntuDerivativePCleaner"
VERSION = "1.000"
VENDOR_ID = "PCLN"
LICENCE_URL = "https://ubuntu.com/legal/font-licence"

# (source file, weight name, usWeightClass)
FACES = [
    ("Ubuntu-Regular.ttf", "Regular", 400),
    ("Ubuntu-Medium.ttf", "Medium", 500),
    ("Ubuntu-Bold.ttf", "Bold", 700),
]

# Unicode ranges kept. Everything else is rendered by WPF's font fallback (Segoe UI and friends).
UNICODE_RANGES = [
    (0x0000, 0x007F),  # Basic Latin
    (0x00A0, 0x024F),  # Latin-1 Supplement, Latin Extended-A/B
    (0x02C6, 0x02DC),  # spacing modifier letters used by Latin (circumflex, caron, breve, ring, tilde)
    (0x0300, 0x036F),  # combining diacritics (decomposed file names)
    (0x2000, 0x206F),  # general punctuation (dashes, bullets, ellipsis, quotes)
    (0x20A0, 0x20CF),  # currency symbols
    (0x2100, 0x214F),  # letterlike symbols (trademark, numero)
    (0x2150, 0x218F),  # number forms (fractions)
    (0x2200, 0x22FF),  # mathematical operators (minus, approximately, less/greater-equal, infinity)
    (0x25A0, 0x25FF),  # geometric shapes (bullets, triangles)
    (0xFEFF, 0xFEFF),  # byte order mark (rendered as nothing)
    (0xFFFD, 0xFFFD),  # replacement character
]

# OpenType layout features kept. "liga" (fi/fl) is deliberately absent; Ubuntu has no "calt" arrow ligatures.
LAYOUT_FEATURES = ["kern", "locl", "case", "tnum", "pnum"]


def feature_mapping(font: TTFont, tag: str) -> dict[str, str]:
    """All single substitutions (glyph -> glyph) behind a GSUB feature, extension lookups included."""
    gsub = font["GSUB"].table
    lookups: set[int] = set()
    for record in gsub.FeatureList.FeatureRecord:
        if record.FeatureTag == tag:
            lookups.update(record.Feature.LookupListIndex)

    mapping: dict[str, str] = {}
    for index in sorted(lookups):
        lookup = gsub.LookupList.Lookup[index]
        for sub_table in lookup.SubTable:
            if sub_table.LookupType == 7:
                sub_table = sub_table.ExtSubTable
            if getattr(sub_table, "mapping", None):
                mapping.update(sub_table.mapping)
    return mapping


def bake_feature_as_default(font: TTFont, tag: str) -> int:
    """Points every cmap entry that maps to a glyph substituted by `tag` at the substituted glyph instead."""
    mapping = feature_mapping(font, tag)
    changed = 0
    for table in font["cmap"].tables:
        for code, glyph in list(table.cmap.items()):
            if glyph in mapping:
                table.cmap[code] = mapping[glyph]
                changed += 1
    return changed


def subset_font(font: TTFont) -> TTFont:
    options = subset.Options()
    options.layout_features = list(LAYOUT_FEATURES)
    options.name_IDs = ["*"]
    options.name_legacy = True
    options.name_languages = ["*"]
    options.glyph_names = False
    options.hinting = True
    options.legacy_kern = True  # Ubuntu carries a legacy 'kern' table next to GPOS; keep both
    options.notdef_outline = True
    options.recommended_glyphs = True
    options.prune_unicode_ranges = True
    options.recalc_bounds = True
    options.recalc_average_width = True
    options.recalc_timestamp = False
    options.ignore_missing_unicodes = True
    options.drop_tables += ["DSIG", "meta"]

    unicodes = [code for start, end in UNICODE_RANGES for code in range(start, end + 1)]
    subsetter = subset.Subsetter(options=options)
    subsetter.populate(unicodes=unicodes)
    subsetter.subset(font)
    return font


def rename(font: TTFont, weight: str, weight_class: int, source_name: str) -> None:
    name = font["name"]
    original_copyright = name.getDebugName(0) or "Copyright 2011 Canonical Ltd. Licensed under the Ubuntu Font Licence 1.0"

    full_name = f"{FAMILY} {weight}" if weight != "Regular" else FAMILY
    postscript_name = f"{POSTSCRIPT_FAMILY}-{weight}"

    # Legacy (Win32) grouping knows only Regular/Bold/Italic/BoldItalic per family; Medium is a family of its own
    # there. The typographic names (16/17) carry the real family for DirectWrite/WPF.
    if weight in ("Regular", "Bold"):
        legacy_family, legacy_style = FAMILY, weight
    else:
        legacy_family, legacy_style = f"{FAMILY} {weight}", "Regular"

    records = {
        0: f"{original_copyright}. This Modified Version ('{FAMILY}') was made for PCleaner: subset to Latin and symbols, ligatures removed, tabular symbol variants made default, renamed as required by the licence. No outline was altered.",
        1: legacy_family,
        2: legacy_style,
        3: f"{VERSION};{VENDOR_ID};{postscript_name}",
        4: full_name,
        5: f"Version {VERSION}; derived from Ubuntu 0.83 ({source_name})",
        6: postscript_name,
        7: "Ubuntu and Canonical are registered trademarks of Canonical Ltd. This derivative is not endorsed by Canonical.",
        8: "Dalton Maag Ltd (original); PCleaner (derivative)",
        9: "Dalton Maag Ltd",
        10: f"{FAMILY} is the user-interface typeface of PCleaner, a Modified Version of the Ubuntu font family by Dalton Maag for Canonical, distributed under the Ubuntu Font Licence 1.0. Weight {weight_class}.",
        11: "https://design.ubuntu.com/font",
        13: "Licensed under the Ubuntu Font Licence 1.0. The full licence text ships with this software as LICENCE-Ubuntu-UFL.txt.",
        14: LICENCE_URL,
        16: FAMILY,
        17: weight,
    }

    name.names = []
    for name_id, text in records.items():
        name.setName(text, name_id, 3, 1, 0x409)  # Windows, Unicode BMP, en-US
        name.setName(text, name_id, 1, 0, 0)  # Macintosh, Roman, English

    os2 = font["OS/2"]
    os2.usWeightClass = weight_class
    os2.achVendID = VENDOR_ID
    font["head"].fontRevision = float(VERSION)


def build(source: Path, out: Path) -> None:
    out.mkdir(parents=True, exist_ok=True)
    for stale in list(out.glob("*.ttf")) + list(out.glob("LICEN*")) + list(out.glob("COPYRIGHT*")):
        stale.unlink()

    copyright_notices: list[str] = []
    for source_name, weight, weight_class in FACES:
        path = source / source_name
        if not path.exists():
            sys.exit(f"missing source font: {path}")

        font = TTFont(str(path))
        original_size = path.stat().st_size
        baked = bake_feature_as_default(font, "tnum")
        subset_font(font)
        rename(font, weight, weight_class, source_name)
        copyright_notices.append(font["name"].getDebugName(0))

        target = out / f"{POSTSCRIPT_FAMILY}-{weight}.ttf"
        font.save(str(target))
        hinted = all(table in font for table in ("fpgm", "prep", "cvt "))
        print(f"{target.name:38} {original_size // 1024:>4} KB -> {target.stat().st_size // 1024:>3} KB   glyphs={len(font.getGlyphOrder()):<4} tnum symbols baked={baked:<3} hinted={hinted}")

    licence = source / "UFL.txt"
    if not licence.exists():
        sys.exit(f"missing licence text: {licence} (clause 1 of the UFL requires it to ship with the fonts)")
    shutil.copy(licence, out / "LICENCE-Ubuntu-UFL.txt")
    copyright_file = source / "COPYRIGHT.txt"
    notices = "\n\n".join(dict.fromkeys(copyright_notices)) + "\n"
    if copyright_file.exists():
        notices += "\n" + copyright_file.read_text(encoding="utf-8")
    (out / "COPYRIGHT-Ubuntu.txt").write_text(notices, encoding="utf-8")
    print("LICENCE-Ubuntu-UFL.txt and COPYRIGHT-Ubuntu.txt written - the fonts are distributed with them")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", required=True, type=Path, help="folder containing Ubuntu-Regular.ttf, Ubuntu-Medium.ttf, Ubuntu-Bold.ttf, UFL.txt (and COPYRIGHT.txt)")
    parser.add_argument("--out", required=True, type=Path, help="output folder (src/PCleaner.App/Fonts)")
    args = parser.parse_args()
    build(args.source, args.out)


if __name__ == "__main__":
    main()