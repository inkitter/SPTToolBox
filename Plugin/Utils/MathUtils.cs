using UnityEngine;

namespace SPTMap.Utils
{
    public static class MathUtils
    {
        // top-down map position: world X stays X, world Z (forward/north) becomes map Y.
        // World height (Y) is dropped - not needed for a 2D map.
        public static Vector2 ConvertToMapPosition(Vector3 worldPosition)
        {
            return new Vector2(worldPosition.x, worldPosition.z);
        }

        // CCW rotation restricted to multiples of 90 degrees - matches the discrete image/Bounds
        // rotation baked into each map's assets offline (build_maps.py), so a map position can be
        // rotated the same way at runtime without any trig or precision concerns.
        public static Vector2 Rotate90Multiple(Vector2 v, float degrees)
        {
            var k = ((int)System.Math.Round(degrees / 90f)) % 4;
            if (k < 0) k += 4;

            return k switch
            {
                1 => new Vector2(-v.y, v.x),
                2 => new Vector2(-v.x, -v.y),
                3 => new Vector2(v.y, -v.x),
                _ => v,
            };
        }

        public static bool ApproxEquals(float first, float second)
        {
            return Mathf.Abs(first - second) < float.Epsilon;
        }

        // the visible map window: shrinks as zoom increases, centered on focusPos (falling back to
        // the map's own center when there's none, e.g. no player found) and clamped so it never
        // reads past boundsMin/boundsMax. At zoom == 1 the clamp range collapses to a single point,
        // so the same formula covers both the full-map and zoomed-in cases.
        //
        // Takes/returns plain float pairs rather than UnityEngine.Vector2: under IL2CPP interop,
        // Vector2's members proxy to native calls and only work inside a running game process, so
        // a Vector2 signature here couldn't be unit tested outside the game. Callers convert at
        // the boundary (see SPTMapBehaviour.DrawMap).
        public static ((float x, float y) viewMin, (float x, float y) viewMax) ComputeViewBounds(
            (float x, float y) boundsMin, (float x, float y) boundsMax, float zoom, (float x, float y)? focusPos)
        {
            var halfSpanX = (boundsMax.x - boundsMin.x) / zoom / 2f;
            var halfSpanY = (boundsMax.y - boundsMin.y) / zoom / 2f;
            var (centerX, centerY) = focusPos ?? ((boundsMin.x + boundsMax.x) / 2f, (boundsMin.y + boundsMax.y) / 2f);
            centerX = System.Math.Clamp(centerX, boundsMin.x + halfSpanX, boundsMax.x - halfSpanX);
            centerY = System.Math.Clamp(centerY, boundsMin.y + halfSpanY, boundsMax.y - halfSpanY);

            return ((centerX - halfSpanX, centerY - halfSpanY), (centerX + halfSpanX, centerY + halfSpanY));
        }
    }
}
