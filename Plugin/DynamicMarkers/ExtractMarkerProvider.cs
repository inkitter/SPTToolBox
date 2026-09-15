using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Il2CppInterop.Runtime;
using SPTMap.Data;
using SPTMap.Utils;
using UnityEngine;

namespace SPTMap.DynamicMarkers
{
    // Ported from the old SPT-DynamicMaps project's ExtractMarkerProvider, trimmed to drop the
    // config/Settings dependency SPTMap doesn't have yet - colors are hardcoded consts instead.
    public class ExtractMarkerProvider
    {
        private const string Category = "Extract";
        private const string ImagePath = "Markers/exit.png";

        private static readonly Color ClosedColor = Color.red;
        private static readonly Color RequirementsColor = Color.yellow;
        private static readonly Color OpenColor = Color.green;

        private readonly Dictionary<ExfiltrationPoint, MapMarker> _markers = new();
        private readonly Il2CppSystem.Action<ExfiltrationPoint, EExfiltrationStatus> _onStatusChanged;

        // ExfiltrationController's point lists aren't populated/activated yet the instant
        // game.InRaid flips true (they show up a bit later during raid setup), so a single
        // OnRaidStart call can find nothing. Keep this false until at least one point is found,
        // and have the caller retry each frame until then - AddMarker is idempotent, so repeated
        // calls after that are harmless no-ops.
        private bool _populated;

        public ExtractMarkerProvider()
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
            var player = GameUtils.GetMainPlayer();
            if (gameWorld?.ExfiltrationController == null || player == null)
            {
                return;
            }

            IEnumerable<ExfiltrationPoint> extracts = GameUtils.IsScavRaid()
                ? gameWorld.ExfiltrationController.ScavExfiltrationPoints
                : gameWorld.ExfiltrationController.ExfiltrationPoints;

            foreach (var extract in extracts.Where(p => p.isActiveAndEnabled && p.InfiltrationMatch(player)))
            {
                AddMarker(extract);
            }

            if (_markers.Count > 0)
            {
                _populated = true;
            }
        }

        public void OnRaidEnd()
        {
            foreach (var extract in _markers.Keys.ToList())
            {
                extract.OnStatusChanged -= _onStatusChanged;
                MarkerManager.Remove(_markers[extract]);
            }

            _markers.Clear();
            _populated = false;
        }

        private void AddMarker(ExfiltrationPoint extract)
        {
            if (_markers.ContainsKey(extract))
            {
                return;
            }

            var worldPos = extract.transform.position;
            var pos = MathUtils.ConvertToMapPosition(worldPos);
            var marker = new MapMarker
            {
                Category = Category,
                ImagePath = ImagePath,
                Text = extract.Settings.Name.BSGLocalized(),
                ShowLabel = true,
                GetPosition = () => pos,
                GetWorldPosition = () => worldPos,
            };

            _markers[extract] = marker;
            MarkerManager.Add(marker);

            extract.OnStatusChanged += _onStatusChanged;
            UpdateStatus(extract, extract.Status);
        }

        private void UpdateStatus(ExfiltrationPoint extract, EExfiltrationStatus status)
        {
            if (!_markers.TryGetValue(extract, out var marker))
            {
                return;
            }

            marker.Color = status switch
            {
                EExfiltrationStatus.NotPresent => ClosedColor,
                EExfiltrationStatus.UncompleteRequirements => RequirementsColor,
                _ => OpenColor,
            };
        }
    }
}
