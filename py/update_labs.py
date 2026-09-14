"""
One-off port of Labs' multi-floor data from the old SPT-DynamicMaps project (it has no
svgPath on tarkov.dev, so it's excluded from update_maps_floors.py - see PROGRESS.md).
Unlike that script, this doesn't need fresh art: the old project's own
Labs_TarkovDev.jsonc already has real per-floor GameBounds (Technical/First/Second Level),
just in the pre-multi-floor MapDef shape - this reshapes that data into the new
MapDef.Levels format and rasterizes each floor's already-vendored SVG layer, using the
same pipeline as the old build_maps.py (cairosvg + PIL rotate).
"""

import json
import os

import cairosvg
from PIL import Image

try:
    from local_config import DYNAMICMAPS_REPO
except ImportError:
    DYNAMICMAPS_REPO = r"D:\Git\SPT-DynamicMaps"

SRC_ROOT = os.path.join(DYNAMICMAPS_REPO, "Plugin", "release", "DynamicMaps", "BepInEx", "plugins", "mpstark-dynamicmaps")
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(REPO_ROOT, "Plugin", "Resources", "Maps", "Labs")

ROTATION = 270
# raw (pre-rotation) world bounds - same as the old jsonc's top-level "Bounds"
RAW_BOUNDS = {"Min": {"x": -292, "y": -441}, "Max": {"x": -96, "y": -223}}

# (level number, svg source path, GameBounds - copied verbatim from the old jsonc's
# Layers.*.GameBounds, already in raw world x/y/z form)
LEVELS = [
    (
        -1,
        r"Maps\Labs_TarkovDev\Layers\Labs_Technical_Level.svg",
        [{"Min": {"x": -292, "y": -100, "z": -441}, "Max": {"x": -96, "y": -0.9, "z": -223}}],
    ),
    (
        0,
        r"Maps\Labs_TarkovDev\Layers\Labs_First_Level.svg",
        [{"Min": {"x": -292, "y": -0.9, "z": -441}, "Max": {"x": -96, "y": 3, "z": -223}}],
    ),
    (
        1,
        r"Maps\Labs_TarkovDev\Layers\Labs_Second_Level.svg",
        [{"Min": {"x": -271, "y": 3, "z": -422}, "Max": {"x": -101, "y": 100, "z": -270}}],
    ),
]
# note: the old jsonc's GameBounds use {x, y, z} where x/z are world x/z (horizontal) and
# y is world height - matches BoundingRectangularSolid exactly, no reordering needed.


def rotate90k_point(x, y, k):
    k = k % 4
    if k == 0:
        return x, y
    if k == 1:
        return -y, x
    if k == 2:
        return -x, -y
    return y, -x


def rotated_bounds(bounds, k):
    corners = [
        (bounds["Min"]["x"], bounds["Min"]["y"]),
        (bounds["Min"]["x"], bounds["Max"]["y"]),
        (bounds["Max"]["x"], bounds["Min"]["y"]),
        (bounds["Max"]["x"], bounds["Max"]["y"]),
    ]
    rotated = [rotate90k_point(x, y, k) for x, y in corners]
    xs = [p[0] for p in rotated]
    ys = [p[1] for p in rotated]
    return {"Min": {"x": min(xs), "y": min(ys)}, "Max": {"x": max(xs), "y": max(ys)}}


os.makedirs(OUT_DIR, exist_ok=True)

old_png = os.path.join(OUT_DIR, "Labs.png")
if os.path.exists(old_png):
    os.remove(old_png)

k = round(ROTATION / 90) % 4
out_bounds = rotated_bounds(RAW_BOUNDS, k)

level_entries = []
for level_num, rel_svg_path, game_bounds in LEVELS:
    svg_path = os.path.join(SRC_ROOT, rel_svg_path)

    png_name = f"Labs_L{level_num}.png"
    png_path = os.path.join(OUT_DIR, png_name)
    raw_png_path = png_path + ".raw.png"
    cairosvg.svg2png(url=svg_path, write_to=raw_png_path, output_width=2048)

    img = Image.open(raw_png_path)
    if ROTATION % 360 != 0:
        img = img.rotate(ROTATION, expand=True)
    img.save(png_path)
    os.remove(raw_png_path)

    level_entries.append({
        "Level": level_num,
        "ImagePath": f"Maps/Labs/{png_name}",
        "GameBounds": game_bounds,
    })
    print(f"level {level_num} ({rel_svg_path}) -> {png_path}")

level_entries.sort(key=lambda e: e["Level"])

map_def = {
    "DisplayName": "The Lab",
    "MapInternalNames": ["laboratory"],
    "ImagePath": next(e["ImagePath"] for e in level_entries if e["Level"] == 0),
    "Bounds": out_bounds,
    "CoordinateRotation": ROTATION,
    "DefaultLevel": 0,
    "Levels": level_entries,
}

with open(os.path.join(OUT_DIR, "Labs.json"), "w", encoding="utf-8") as f:
    json.dump(map_def, f, indent=2)

print(f"bounds={out_bounds}")
print("done")
