"""
Refreshes every map that has a live SVG on tarkov.dev, generating one PNG per floor plus
a multi-level MapDef, from https://github.com/the-hideout/tarkov-dev's src/data/maps.json
(fetched live) + the SVGs it points at (assets.tarkov.dev). Supersedes update_interchange.py
(which did this by hand for Interchange only - this generalizes that to every other map
with a svgPath). See PROGRESS.md "Known issue" / multi-floor section for why: tarkov.dev's
data is the actively-maintained source that matches the live game, and unlike the old
vendored SVG pack, its per-floor GameBounds are precise 3D world-space boxes (not just a
height band), so floor auto-detection is accurate even where a floor only covers part of
the map footprint (e.g. Interchange's upper floors only cover the mall building).

Labs and Labyrinth are NOT covered - tarkov.dev has no svgPath for either (raster-tile-only
on their site), and as established when Interchange was first done, swapping in their
Bounds numbers without matching art risks making those two worse, not better.

Level numbering follows the old SPT-DynamicMaps project's own convention (see its
Resources/Maps/*/*.jsonc): ground/base level is 0, floors going up are 1, 2, 3..., and
anything below ground (basement/tunnels/garage/bunkers) is -1, -2... in the order tarkov.dev
lists them (which is already bottom-to-top for the above-ground ones).
"""

import json
import os
import re
import urllib.request
import xml.etree.ElementTree as ET

import cairosvg
from PIL import Image

MAPS_JSON_URL = "https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/maps.json"
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_ROOT = os.path.join(REPO_ROOT, "Plugin", "Resources", "Maps")

# tarkov.dev normalizedName -> (our short folder name, DisplayName, MapInternalNames)
# internal names are the game's own location ids (case-insensitive match, see MapUtils.cs)
MAP_TARGETS = {
    "customs": ("Customs", "Customs", ["bigmap"]),
    "factory": ("Factory", "Factory", ["factory4_day", "factory4_night"]),
    # "sandbox_start" is Ground Zero's brief pre-raid transition location id, seen in-game before
    # the real "sandbox"/"sandbox_high" location loads - without it MapUtils logs a "No map def"
    # warning every frame for the duration of that transition.
    "ground-zero": ("GroundZero", "Ground Zero", ["sandbox", "sandbox_high", "sandbox_start"]),
    "interchange": ("Interchange", "Interchange", ["Interchange"]),
    "lighthouse": ("Lighthouse", "Lighthouse", ["lighthouse"]),
    "reserve": ("Reserve", "Reserve", ["rezervbase"]),
    "shoreline": ("Shoreline", "Shoreline", ["shoreline"]),
    "streets-of-tarkov": ("Streets", "Streets of Tarkov", ["tarkovstreets"]),
    "woods": ("Woods", "Woods", ["woods"]),
    # "the-lab" / "the-labyrinth": no svgPath on tarkov.dev - intentionally not covered.
}

BELOW_GROUND_KEYWORDS = ("underground", "tunnel", "basement", "garage", "bunker")
WHOLE_MAP_HEIGHT = (-100000.0, 100000.0)


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


def fetch(url):
    req = urllib.request.Request(url, headers={"User-Agent": "SPTMap-update_maps_floors.py"})
    with urllib.request.urlopen(req, timeout=30) as resp:
        return resp.read()


def fetch_text(url):
    return fetch(url).decode("utf-8")


def isolate_layer(svg_text, keep_layer_id):
    """Keeps <style>/<defs>/etc untouched; drops every top-level <g id="..."> group except
    keep_layer_id and any sibling explicitly marked data-keep-with-group="<keep_layer_id>"
    (several of tarkov.dev's SVGs split a few ground-floor-only decorations into their own
    group this way, e.g. Customs/GroundZero/Shoreline/Streets all have a "First_Floor" group
    meant to always render alongside "Ground_Level", not as its own selectable floor)."""
    ET.register_namespace("", "http://www.w3.org/2000/svg")
    ET.register_namespace("xlink", "http://www.w3.org/1999/xlink")
    root = ET.fromstring(svg_text)
    ns = "{http://www.w3.org/2000/svg}"
    for child in list(root):
        if child.tag != f"{ns}g":
            continue
        if child.get("id") == keep_layer_id:
            continue
        if child.get("data-keep-with-group") == keep_layer_id:
            continue
        root.remove(child)
    return ET.tostring(root, encoding="unicode")


def raw_bounds_from_maps_json(bounds_pair):
    (x0, z0), (x1, z1) = bounds_pair
    return {"Min": {"x": min(x0, x1), "y": min(z0, z1)}, "Max": {"x": max(x0, x1), "y": max(z0, z1)}}


def game_bounds_for_extents(extents, whole_map_xz):
    boxes = []
    for extent in extents:
        h_min, h_max = extent["height"]
        regions = extent.get("bounds")
        if not regions:
            (x_min, z_min), (x_max, z_max) = whole_map_xz
            boxes.append((x_min, h_min, z_min, x_max, h_max, z_max))
            continue
        for region in regions:
            (x0, z0), (x1, z1) = region[0], region[1]
            boxes.append((min(x0, x1), h_min, min(z0, z1), max(x0, x1), h_max, max(z0, z1)))
    return boxes


def assign_level_numbers(layers):
    above = 0
    below = 0
    numbered = []
    for layer in layers:
        if not layer.get("svgLayer"):
            print(f"    skipping '{layer['name']}' - no svgLayer (tile-only on tarkov.dev, no art available)")
            continue
        key = f"{layer['name']} {layer.get('svgLayer', '')}".lower()
        if any(kw in key for kw in BELOW_GROUND_KEYWORDS):
            below -= 1
            level_num = below
        else:
            above += 1
            level_num = above
        numbered.append((level_num, layer))
    return numbered


def build_map(normalized_name, entry, short_name, display_name, internal_names):
    print(f"=== {short_name} ({normalized_name}) ===")
    rotation = entry.get("coordinateRotation", 0) or 0
    raw_bounds = raw_bounds_from_maps_json(entry["bounds"])
    whole_map_xz = (
        (raw_bounds["Min"]["x"], raw_bounds["Min"]["y"]),
        (raw_bounds["Max"]["x"], raw_bounds["Max"]["y"]),
    )

    svg_text = fetch_text(entry["svgPath"])

    out_dir = os.path.join(OUT_ROOT, short_name)
    os.makedirs(out_dir, exist_ok=True)

    # remove any stale single-level PNG/JSON from a previous non-multi-floor build
    old_png = os.path.join(out_dir, f"{short_name}.png")
    if os.path.exists(old_png):
        os.remove(old_png)

    levels_to_build = [(0, {"name": "Ground", "svgLayer": entry["svgLayer"]})]
    levels_to_build += assign_level_numbers(entry.get("layers", []))

    level_entries = []
    for level_num, layer in levels_to_build:
        svg_layer_id = layer["svgLayer"]
        filtered = isolate_layer(svg_text, svg_layer_id)

        tmp_svg_path = os.path.join(out_dir, f"_tmp_{short_name}_L{level_num}.svg")
        with open(tmp_svg_path, "w", encoding="utf-8") as f:
            f.write(filtered)

        png_name = f"{short_name}_L{level_num}.png"
        png_path = os.path.join(out_dir, png_name)
        raw_png_path = png_path + ".raw.png"
        cairosvg.svg2png(url=tmp_svg_path, write_to=raw_png_path, output_width=2048)
        os.remove(tmp_svg_path)

        img = Image.open(raw_png_path)
        img.save(png_path)
        os.remove(raw_png_path)

        if level_num == 0:
            game_bounds_raw = [
                (whole_map_xz[0][0], WHOLE_MAP_HEIGHT[0], whole_map_xz[0][1],
                 whole_map_xz[1][0], WHOLE_MAP_HEIGHT[1], whole_map_xz[1][1])
            ]
        else:
            game_bounds_raw = game_bounds_for_extents(layer["extents"], whole_map_xz)

        level_entries.append({
            "Level": level_num,
            "ImagePath": f"Maps/{short_name}/{png_name}",
            "GameBounds": [
                {
                    "Min": {"x": b[0], "y": b[1], "z": b[2]},
                    "Max": {"x": b[3], "y": b[4], "z": b[5]},
                }
                for b in game_bounds_raw
            ],
        })
        print(f"    level {level_num} ({layer['name']} / {svg_layer_id}) -> {png_path}")

    level_entries.sort(key=lambda e: e["Level"])
    out_bounds = rotated_bounds(raw_bounds, round(rotation / 90) % 4)

    map_def = {
        "DisplayName": display_name,
        "MapInternalNames": internal_names,
        "ImagePath": next(e["ImagePath"] for e in level_entries if e["Level"] == 0),
        "Bounds": out_bounds,
        "CoordinateRotation": rotation,
        "DefaultLevel": 0,
        "Levels": level_entries,
    }

    with open(os.path.join(out_dir, f"{short_name}.json"), "w", encoding="utf-8") as f:
        json.dump(map_def, f, indent=2)

    print(f"    bounds={out_bounds}")


maps_json = json.loads(fetch(MAPS_JSON_URL))

for group in maps_json:
    if group["normalizedName"] not in MAP_TARGETS:
        continue
    short_name, display_name, internal_names = MAP_TARGETS[group["normalizedName"]]
    entry = next(m for m in group["maps"] if m["projection"] == "interactive")
    if not entry.get("svgPath"):
        print(f"skipping {group['normalizedName']} - no svgPath")
        continue
    build_map(group["normalizedName"], entry, short_name, display_name, internal_names)

print("done")
