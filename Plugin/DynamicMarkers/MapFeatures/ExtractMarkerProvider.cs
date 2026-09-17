using System;
using System.Collections.Generic;
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
    // Covers both regular and secret extracts as one uniform "extract point" - to the player both
    // just mean "red = can't use it right now, green = can", so there's no reason to track them as
    // separate types/categories; SecretExfiltrationPoint is itself an ExfiltrationPoint, so a
    // single Dictionary<ExfiltrationPoint, MapMarker> covers both without a cast anywhere.
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
            // gated so the caller's every-frame retry (until the controller's point lists are
            // populated at all) doesn't re-scan every frame forever afterwards - RefreshNow (called
            // on the map-open edge) is what keeps catching newly-activated extracts (e.g. a train)
            // once this has found at least the map's regular ones.
            if (_populated)
            {
                return;
            }

            ScanForExtracts();
        }

        // Called once on the frame the map is opened (see SPTMapController's peek-toggle edge) -
        // this is the only place extracts refresh after the initial raid-start scan. Some extracts
        // (e.g. a train extract) are only isActiveAndEnabled during a raid-time window - filtered
        // out of the scan at raid start like any other still-closed extract, but unlike a merely-
        // closed one (which stays in the dictionary and gets live status updates via
        // OnStatusChanged) it never even enters _markers until a rescan finds it active. No
        // periodic re-trigger even if the map stays open a long time - a delayed extract sighting
        // matters far less than avoiding another source of periodic frame drops. Close and reopen
        // the map to force a fresh look.
        public void RefreshNow()
        {
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

            // regular extracts need a resolved player (InfiltrationMatch checks side/group) -
            // secret extracts have no such filter, so they're scanned regardless.
            var player = GameUtils.GetMainPlayer();
            if (player != null)
            {
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
            }

            foreach (var secret in gameWorld.ExfiltrationController.SecretExfiltrationPoints)
            {
                AddMarker(secret);
            }

            if (_markers.Count > 0)
            {
                _populated = true;
            }
        }

        public void OnRaidEnd()
        {
            // copy keys first - unsubscribing/removing while enumerating the dictionary itself
            // would throw.
            var extracts = new List<ExfiltrationPoint>(_markers.Count);
            foreach (var extract in _markers.Keys)
            {
                extracts.Add(extract);
            }

            foreach (var extract in extracts)
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
