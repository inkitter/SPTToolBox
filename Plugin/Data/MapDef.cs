using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace SPTMap.Data
{
    public class BoundingRectangle
    {
        public Vector2 Min { get; set; }
        public Vector2 Max { get; set; }
    }

    // Ported from the old SPT-DynamicMaps project's Data/Defs.cs - a full 3D world-space box (not
    // just a height band), because a level's floor can be localized to part of the map (e.g. only
    // Interchange's mall building has upper floors; the rest of the map has none at any height).
    public class BoundingRectangularSolid
    {
        public Vector3 Min { get; set; }
        public Vector3 Max { get; set; }
    }

    // One floor of a multi-level map (e.g. Interchange's Ground/1st/2nd floors). All levels of a
    // map share the same Bounds/CoordinateRotation - only the image differs - since they're
    // different <g> layers rasterized from the same source SVG/viewBox.
    public class MapLevel
    {
        public int Level { get; set; }
        public string ImagePath { get; set; }

        // raw (pre-rotation) world-space boxes this level occupies - same convention as the old
        // project's MapLayerDef.GameBounds, and matched against the player's raw, unrotated
        // position (see SPTMapBehaviour.ResolveActiveLevel), not the rotated map-space position
        // used for drawing. Empty means "everywhere" (e.g. a map's base/ground level).
        public List<BoundingRectangularSolid> GameBounds { get; set; } = new();
    }

    // Deliberately trimmed down from SPT-DynamicMaps' MapDef - just enough to place a static,
    // pre-rasterized PNG (no markers yet, those come back in a later stage).
    public class MapDef
    {
        public string DisplayName { get; set; }
        public List<string> MapInternalNames { get; set; } = new();
        public string ImagePath { get; set; }
        public BoundingRectangle Bounds { get; set; }

        // the image and Bounds above are already pre-rotated by this many degrees (a multiple of
        // 90, baked in offline at asset-build time - see SPTMap/build_maps.py) so screen "up"
        // matches the orientation the old SPT-DynamicMaps project used. The player's raw world
        // position still needs the same rotation applied at runtime before it's compared against
        // Bounds - see MathUtils.Rotate90Multiple.
        public float CoordinateRotation { get; set; } = 0f;

        // empty (the common case so far) means this map has just the one level - ImagePath above
        // is it. Non-empty means multi-floor: ImagePath above is still kept in sync with
        // DefaultLevel's image for any code path that doesn't care about floors at all.
        public List<MapLevel> Levels { get; set; } = new();
        public int DefaultLevel { get; set; } = 0;

        public bool HasLevels => Levels.Count > 0;

        public static MapDef LoadFromPath(string absolutePath)
        {
            return JsonConvert.DeserializeObject<MapDef>(File.ReadAllText(absolutePath));
        }
    }
}
