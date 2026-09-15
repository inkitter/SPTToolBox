using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.Interactive.SecretExfiltrations;
using Il2CppInterop.Runtime;
using SPTMap.Data;
using SPTMap.Utils;
using UnityEngine;

namespace SPTMap.DynamicMarkers
{
    // Secret extracts - same shape as ExtractMarkerProvider (status-driven color via
    // ExfiltrationPoint.OnStatusChanged), just a different source list and a "hidden" base state
    // instead of "closed".
    public class SecretMarkerProvider
    {
        private const string Category = "Secret Extract";
        private const string ImagePath = "Markers/exit.png";

        private static readonly Color HiddenColor = Color.magenta;
        private static readonly Color RequirementsColor = Color.yellow;
        private static readonly Color OpenColor = Color.green;

        private readonly Dictionary<SecretExfiltrationPoint, MapMarker> _markers = new();
        private readonly Il2CppSystem.Action<ExfiltrationPoint, EExfiltrationStatus> _onStatusChanged;

        private bool _populated;

        public SecretMarkerProvider()
        {
            _onStatusChanged = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ExfiltrationPoint, EExfiltrationStatus>>(
                new Action<ExfiltrationPoint, EExfiltrationStatus>(UpdateStatus));
        }

        public void OnRaidStart()
        {
            if (_populated)
            {
                return;
            }

            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld?.ExfiltrationController == null)
            {
                return;
            }

            foreach (var point in gameWorld.ExfiltrationController.SecretExfiltrationPoints)
            {
                AddMarker(point);
            }

            if (_markers.Count > 0)
            {
                _populated = true;
            }
        }

        public void OnRaidEnd()
        {
            foreach (var point in _markers.Keys.ToList())
            {
                point.OnStatusChanged -= _onStatusChanged;
                MarkerManager.Remove(_markers[point]);
            }

            _markers.Clear();
            _populated = false;
        }

        private void AddMarker(SecretExfiltrationPoint point)
        {
            if (_markers.ContainsKey(point))
            {
                return;
            }

            var pos = MathUtils.ConvertToMapPosition(point.transform.position);
            var marker = new MapMarker
            {
                Category = Category,
                ImagePath = ImagePath,
                Text = point.Settings.Name.BSGLocalized(),
                ShowLabel = true,
                GetPosition = () => pos,
            };

            _markers[point] = marker;
            MarkerManager.Add(marker);

            point.OnStatusChanged += _onStatusChanged;
            UpdateStatus(point, point.Status);
        }

        private void UpdateStatus(ExfiltrationPoint point, EExfiltrationStatus status)
        {
            if (point.TryCast<SecretExfiltrationPoint>() is not { } secret || !_markers.TryGetValue(secret, out var marker))
            {
                return;
            }

            marker.Color = status switch
            {
                EExfiltrationStatus.NotPresent => HiddenColor,
                EExfiltrationStatus.UncompleteRequirements => RequirementsColor,
                _ => OpenColor,
            };
        }
    }
}
