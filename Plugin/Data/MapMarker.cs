using System;
using UnityEngine;

namespace SPTMap.Data
{
    // A drawable marker (extract, quest objective, other player, corpse, ...). Position/facing are
    // functions rather than fixed values so movable entities (other players) can be redrawn live
    // each frame without the provider having to update anything; static markers (extracts, quest
    // objectives, corpses) just close over a captured Vector2. Both are in raw (pre-CoordinateRotation)
    // map space - MarkerManager applies the active MapDef's rotation at draw time, same as the main
    // player marker in Plugin.cs.
    public class MapMarker
    {
        public string Category;
        public string ImagePath;
        public string Text;
        public Color Color = Color.white;

        // whether Text should be drawn as a small always-on label under the icon (subject to
        // Settings.ShowMarkerLabels), rather than only on hover. Opt-in per provider - markers like
        // player nicknames stay hover-only by default so the map doesn't get cluttered.
        public bool ShowLabel;

        // optional live overrides for ImagePath/Color, invoked every draw - for markers whose
        // appearance can change while they exist (e.g. a tracked player dying mid-raid) without
        // needing an event to go rewrite the marker. Falls back to ImagePath/Color when null.
        public Func<string> GetImagePath;
        public Func<Color> GetColor;

        // null return = don't draw this frame (e.g. entity temporarily invalid).
        public Func<Vector2?> GetPosition;

        // raw XZ forward direction for markers that should rotate to face something (other
        // players); null = icon always drawn upright.
        public Func<Vector2?> GetFacing;

        // full raw world position (including height) used to resolve which floor this marker is
        // on for multi-level maps - null if unknown, in which case the marker is always drawn at
        // full opacity. Separate from GetPosition because that one already dropped height.
        public Func<Vector3?> GetWorldPosition;
    }
}
