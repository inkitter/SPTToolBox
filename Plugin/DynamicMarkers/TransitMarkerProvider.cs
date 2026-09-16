using System.Collections.Generic;
using EFT.Interactive;
using SPTMap.Data;
using SPTMap.Utils;
using UnityEngine;

namespace SPTMap.DynamicMarkers
{
    // Transit points (map-to-map border crossings). Found via a live scene scan
    // (Object.FindObjectsOfType<TransitPoint>()) rather than GameWorld.TransitController.pointsById -
    // TransitPoint is itself a MonoBehaviour, so a scene scan finds it regardless of whether/when
    // the controller's own dictionary gets populated, mirroring DoorMarkerProvider's approach for
    // the same reason. Shown red when currently unusable (Enabled/IsActive false - e.g. a
    // requirement not met yet) rather than hidden, so it's still visible as a "there's a transit
    // point here" landmark even before it opens up.
    public class TransitMarkerProvider
    {
        private const string Category = "Transit";
        private const string ImagePath = "Markers/transit.png";

        private static readonly Color UsableColor = Color.cyan;
        private static readonly Color UnusableColor = Color.red;

        private readonly Dictionary<TransitPoint, MapMarker> _markers = new();

        // like DoorMarkerProvider - transit points may not all be alive/enabled in the scene the
        // instant game.InRaid flips true, so keep retrying each frame until at least one is found;
        // AddMarker is idempotent so repeated calls are harmless.
        private bool _populated;

        public void OnRaidStart()
        {
            if (_populated)
            {
                return;
            }

            var points = Object.FindObjectsOfType<TransitPoint>();
            if (points == null)
            {
                return;
            }

            foreach (var point in points)
            {
                AddMarker(point);
            }

            // stop retrying regardless of count - TransitPoint is static level geometry, not a
            // raid-random spawn, so an empty result here means this map genuinely has none, not
            // "not loaded yet". Gating on points.Length would re-run this full-scene
            // FindObjectsOfType scan every single frame for the rest of the raid on those maps
            // (see git history: same bug tanked FPS via HiddenStashMarkerProvider).
            _populated = true;
        }

        public void OnRaidEnd()
        {
            foreach (var marker in _markers.Values)
            {
                MarkerManager.Remove(marker);
            }

            _markers.Clear();
            _populated = false;
        }

        private void AddMarker(TransitPoint point)
        {
            if (_markers.ContainsKey(point))
            {
                return;
            }

            var worldPos = point.transform.position;
            var pos = MathUtils.ConvertToMapPosition(worldPos);
            var marker = new MapMarker
            {
                Category = Category,
                ImagePath = ImagePath,
                // point.Description returns the raw numeric transit id, not a display name - the
                // actual locale-keyed name lives on the nested parameters struct (see
                // LocationSettings.Location.TransitParameters.name/.description in the decompiled
                // Assembly-CSharp.dll).
                Text = point.parameters.name.BSGLocalized(),
                ShowLabel = true,
                GetPosition = () => pos,
                GetWorldPosition = () => worldPos,
                GetColor = () => point.Enabled && point.IsActive ? UsableColor : UnusableColor,
            };

            _markers[point] = marker;
            MarkerManager.Add(marker);
        }
    }
}
