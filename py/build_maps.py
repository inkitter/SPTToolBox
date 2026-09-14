import commentjson, glob, os, json
import cairosvg
from PIL import Image

try:
    from local_config import DYNAMICMAPS_REPO
except ImportError:
    DYNAMICMAPS_REPO = r"D:\Git\SPT-DynamicMaps"

SRC_ROOT = os.path.join(DYNAMICMAPS_REPO, "Plugin", "release", "DynamicMaps", "BepInEx", "plugins", "mpstark-dynamicmaps")
MAPS_SRC = os.path.join(SRC_ROOT, "Maps")
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_ROOT = os.path.join(REPO_ROOT, "Plugin", "Resources", "Maps")

# short folder-safe names for each source map
SHORT_NAMES = {
    "Customs_TarkovDev": "Customs",
    "Factory_TarkovDev": "Factory",
    "GroundZero_TarkovDev": "GroundZero",
    "Interchange_TarkovDev": "Interchange",
    "Labs_TarkovDev": "Labs",
    "Labyrinth": "Labyrinth",
    "Lighthouse_TarkovData": "Lighthouse",
    "Reserve_TarkovData": "Reserve",
    "Shoreline_TarkovData": "Shoreline",
    "Streets_TarkovData": "Streets",
    "Woods_TarkovData": "Woods",
}


def rotate90k_point(x, y, k):
    k = k % 4
    if k == 0:
        return x, y
    if k == 1:
        return -y, x
    if k == 2:
        return -x, -y
    return y, -x  # k == 3


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


for jsonc_path in sorted(glob.glob(os.path.join(MAPS_SRC, "*", "*.jsonc"))):
    with open(jsonc_path, encoding="utf-8") as f:
        data = commentjson.load(f)

    folder_name = os.path.basename(os.path.dirname(jsonc_path))
    short_name = SHORT_NAMES[folder_name]

    default_level = data.get("DefaultLevel", 0)
    layers = data.get("Layers", {})
    chosen = None
    for name, layer in layers.items():
        if layer.get("Level") == default_level:
            chosen = layer
            break
    if chosen is None:
        chosen = next(iter(layers.values()))

    svg_path = os.path.join(SRC_ROOT, chosen["ImagePath"].replace("/", os.sep))
    coordinate_rotation = data.get("CoordinateRotation", 0)
    k = round(coordinate_rotation / 90) % 4

    out_dir = os.path.join(OUT_ROOT, short_name)
    os.makedirs(out_dir, exist_ok=True)

    png_name = f"{short_name}.png"
    png_path = os.path.join(out_dir, png_name)

    raw_png_path = png_path + ".raw.png"
    cairosvg.svg2png(url=svg_path, write_to=raw_png_path, output_width=2048)

    img = Image.open(raw_png_path)
    if coordinate_rotation % 360 != 0:
        img = img.rotate(coordinate_rotation, expand=True)
    img.save(png_path)
    os.remove(raw_png_path)

    out_bounds = rotated_bounds(data["Bounds"], k)

    map_def = {
        "DisplayName": data["DisplayName"],
        "MapInternalNames": data["MapInternalNames"],
        "ImagePath": f"Maps/{short_name}/{png_name}",
        "Bounds": out_bounds,
        # image + Bounds above are already pre-rotated to match this (so no runtime image
        # rotation is needed) - the C# side still needs this to rotate the player's raw world
        # position the same way each frame before mapping it against the (rotated) Bounds.
        "CoordinateRotation": coordinate_rotation,
    }

    with open(os.path.join(out_dir, f"{short_name}.json"), "w", encoding="utf-8") as f:
        json.dump(map_def, f, indent=2)

    print(f"{short_name}: {svg_path} (rot {coordinate_rotation}) -> {png_path}  bounds={out_bounds}")

print("done")
