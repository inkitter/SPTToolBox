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

        // some extracts (e.g. a train extract) are only isActiveAndEnabled during a raid-time
        // window - filtered out of the scan at raid start like any other still-closed extract, but
        // unlike a merely-closed one (which stays in the dictionary and gets live status updates
        // via OnStatusChanged) it never even enters _markers, so it needs a periodic rescan to pick
        // it up once the engine activates it mid-raid rather than relying on _populated's one-shot
        // retry-until-first-found.
        private const float RescanIntervalSeconds = 5f;
        private float _rescanAccumulator;

        public ExtractMarkerProvider()
        {
            _onStatusChanged = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ExfiltrationPoint, EExfiltrationStatus>>(
                new Action<ExfiltrationPoint, EExfiltrationStatus>(UpdateStatus));
        }

        public void OnRaidStart()
        {
            // gated so the caller's every-frame retry (until the controller's point lists are
            // populated at all) doesn't re-scan every frame forever afterwards - Tick below is
            // what keeps catching newly-activated extracts (e.g. a train) once this has found at
            // least the map's regular ones.
            if (_populated)
            {
                return;
            }

            ScanForExtracts();
        }

        // called every frame from SPTMapController.Update while in a raid; only actually rescans
        // once the interval elapses. AddMarker is idempotent so repeated calls are cheap no-ops
        // for extracts already tracked.
        public void Tick(float deltaTime)
        {
            _rescanAccumulator += deltaTime;
            if (_rescanAccumulator < RescanIntervalSeconds)
            {
                return;
            }

            _rescanAccumulator = 0f;
            ScanForExtracts();
        }

        private void ScanForExtracts()
        {
            // GameWorld is a MonoBehaviour (UnityEngine.Object) - explicit ifs instead of ?., see
            // git history/memory ("?./?? bypasses Unity's fake-null override").
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null || gameWorld.ExfiltrationController == null)
            {
                return;
            }

            var player = GameUtils.GetMainPlayer();
            if (player == null)
            {
                return;
            }

            IEnumerable<ExfiltrationPoint> extracts = GameUtils.IsScavRaid()
                ? gameWorld.ExfiltrationController.ScavExfiltrationPoints
                : gameWorld.ExfiltrationController.ExfiltrationPoints;

            foreach (var extract in extracts)
            {
                if (extract.isActiveAndEnabled && extract.InfiltrationMatch(player))
                {
                    AddMarker(extract);
                }
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
            _rescanAccumulator = 0f;
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
