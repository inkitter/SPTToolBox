using System.Collections.Generic;
using SPTMap.Data;

namespace SPTMap.Utils
{
    // Flat registry of every marker currently on the map, populated by the DynamicMarkers
    // providers and drawn by SPTMapBehaviour. Deliberately not filtered by category/floor here -
    // that's a later config/UI concern (see PROGRESS.md); for now everything registered gets drawn.
    public static class MarkerManager
    {
        public static readonly List<MapMarker> Markers = new();

        public static void Add(MapMarker marker) => Markers.Add(marker);

        public static void Remove(MapMarker marker) => Markers.Remove(marker);

        public static void Clear() => Markers.Clear();
    }
}
