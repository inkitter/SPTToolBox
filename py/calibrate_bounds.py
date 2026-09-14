"""
Calibration helper for the map/world misalignment (see PROGRESS.md "Known issue").

Usage:
  1. In-raid on the affected map, stand at a landmark you can also pinpoint on the
     rendered minimap (a distinctive building corner, sign, etc). Press Keypad . to
     log a line like:
       [landmark] map='woods' def=Woods raw=(123.4, -56.7) rotated=(123.4, -56.7)
  2. Note where that landmark falls in the CURRENT (wrong) map image, as a fraction
     of the image (u, v) in [0, 1] - u from left, v from BOTTOM (matches Bounds/GUI
     convention used in Plugin.cs DrawMap). Eyeball it against the PNG in
     Plugin/Resources/Maps/<Map>/<Map>.png.
  3. Repeat at a second, well-separated landmark.
  4. Fill in the two (rotated_world_xy, image_uv) pairs below and run this script.
     It solves a per-axis affine fit and prints a corrected Bounds you can paste
     into Plugin/Resources/Maps/<Map>/<Map>.json (the built one - build_maps.py
     will overwrite it on next run, so also patch the source .jsonc's "Bounds" in
     the old project's release/.../Maps/<Map>/<Map>.jsonc to make it stick).

The fit: image u = (world_x - Bounds.Min.x) / (Bounds.Max.x - Bounds.Min.x), and
same for v/y. Two points per axis is exactly enough to solve for Min/Max (2
unknowns, 2 equations) - no least-squares needed.
"""

# --- fill these in from two in-game landmark readings ---
MAP_NAME = "Woods"

# (rotated world x, image u in [0,1])
POINT_A_X = (0.0, 0.0)
POINT_B_X = (1.0, 1.0)

# (rotated world y, image v in [0,1], v measured from image BOTTOM)
POINT_A_Y = (0.0, 0.0)
POINT_B_Y = (1.0, 1.0)
# ----------------------------------------------------------


def solve_axis(point_a, point_b):
    (w_a, u_a), (w_b, u_b) = point_a, point_b
    # u = (w - min) / (max - min)  =>  w = min + u * (max - min)
    # two points -> solve for min, max (equivalently: slope + intercept)
    if u_b == u_a:
        raise ValueError("need two landmarks with different u/v, not the same spot")
    scale = (w_b - w_a) / (u_b - u_a)  # = (max - min)
    min_ = w_a - u_a * scale
    max_ = min_ + scale
    return min_, max_


if __name__ == "__main__":
    min_x, max_x = solve_axis(POINT_A_X, POINT_B_X)
    min_y, max_y = solve_axis(POINT_A_Y, POINT_B_Y)
    print(f"{MAP_NAME} corrected Bounds:")
    print('  "Bounds": {')
    print(f'    "Min": {{"x": {min_x:.2f}, "y": {min_y:.2f}}},')
    print(f'    "Max": {{"x": {max_x:.2f}, "y": {max_y:.2f}}}')
    print("  }")
