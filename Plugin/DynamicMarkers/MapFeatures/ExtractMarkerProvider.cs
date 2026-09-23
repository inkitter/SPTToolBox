using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.MovingPlatforms;
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
    // single dictionary covers both without a cast anywhere.
    public class ExtractMarkerProvider
    {
        private const string Category = "Extract";
        private const string ImagePath = "Markers/exit.png";

        private static readonly Color ClosedColor = Color.red;
        private static readonly Color RequirementsColor = Color.yellow;
        private static readonly Color OpenColor = Color.green;

        // Keyed by a string (name + position), not by the ExfiltrationPoint itself: Il2Cpp wrapper
        // objects handed back from different native calls aren't guaranteed to be the same managed
        // instance (see UnitMarkerProvider), so an object-keyed ContainsKey could miss and every
        // RefreshNow (each map open) would stack duplicate markers - growing draw cost all raid.
        private readonly Dictionary<string, TrackedExtract> _markers = new();

        private struct TrackedExtract
        {
            public ExfiltrationPoint Extract;
            public MapMarker Marker;
        }

        // Found-nothing can't be the only stop condition (a raid with zero matching extracts would
        // rescan every frame forever - the same bug that tanked FPS before), so bound it in time.
        private const float MaxRetrySeconds = 5f;
        private float _firstAttemptTime = -1f;
        private readonly Il2CppSystem.Action<ExfiltrationPoint, EExfiltrationStatus> _onStatusChanged;

        // Lighthouse train extract: icon colour follows the train's own travel state instead of
        // the extract status - red not yet coming, yellow on its way, green boardable (arrived),
        // gray departing/gone. Locomotive is found with one scene scan, only on a map that has
        // this extract; retried once per map open (RefreshNow) if it wasn't found yet.
        private const string TrainExtractName = "EXFIL_Train";
        private static readonly Color TrainNotStartedColor = Color.red;
        private static readonly Color TrainIncomingColor = Color.yellow;
        private static readonly Color TrainBoardableColor = Color.green;
        private static readonly Color TrainGoneColor = Color.gray;
        private Locomotive _locomotive;
        private bool _hasTrainExtract;

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

            if (_firstAttemptTime < 0f)
            {
                _firstAttemptTime = Time.time;
            }

            ScanForExtracts();

            if (Time.time - _firstAttemptTime >= MaxRetrySeconds)
            {
                _populated = true;
            }
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

            if (_hasTrainExtract && _locomotive == null)
            {
                FindLocomotive();
            }
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
            foreach (var tracked in _markers.Values)
            {
                try
                {
                    tracked.Extract.OnStatusChanged -= _onStatusChanged;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"ExtractMarkerProvider: unsubscribe failed: {e.Message}");
                }

                MarkerManager.Remove(tracked.Marker);
            }

            _markers.Clear();
            _populated = false;
            _locomotive = null;
            _hasTrainExtract = false;
            _firstAttemptTime = -1f;
        }

        private void AddMarker(ExfiltrationPoint extract)
        {
            var worldPos = extract.transform.position;
            var key = GetKey(extract, worldPos);
            if (_markers.ContainsKey(key))
            {
                return;
            }

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

            _markers[key] = new TrackedExtract { Extract = extract, Marker = marker };
            MarkerManager.Add(marker);

            if (extract.Settings.Name == TrainExtractName)
            {
                _hasTrainExtract = true;
                FindLocomotive();
                marker.GetColor = () => GetTrainColor(marker.Color);
            }

            extract.OnStatusChanged += _onStatusChanged;
            UpdateStatus(extract, extract.Status);
        }

        private void FindLocomotive()
        {
            var locomotives = UnityEngine.Object.FindObjectsOfType<Locomotive>();
            if (locomotives != null && locomotives.Length > 0)
            {
                _locomotive = locomotives[0];
            }
        }

        // Locomotive is a MonoBehaviour - explicit == null (fake-null safe), no ?.
        private Color GetTrainColor(Color fallback)
        {
            if (_locomotive == null)
            {
                return fallback;
            }

            var travelState = _locomotive.TravelState;
            if (travelState == null)
            {
                return fallback;
            }

            return travelState.Value switch
            {
                Locomotive.ETravelState.NotStarted => TrainNotStartedColor,
                Locomotive.ETravelState.OnRouteToDestination => TrainIncomingColor,
                Locomotive.ETravelState.Arrived => TrainBoardableColor,
                _ => TrainGoneColor,
            };
        }

        private static string GetKey(ExfiltrationPoint extract, Vector3 worldPos)
        {
            return $"{extract.Settings.Name}@{worldPos.x:0.0},{worldPos.y:0.0},{worldPos.z:0.0}";
        }

        private void UpdateStatus(ExfiltrationPoint extract, EExfiltrationStatus status)
        {
            if (extract == null || !_markers.TryGetValue(GetKey(extract, extract.transform.position), out var tracked))
            {
                return;
            }

            tracked.Marker.Color = status switch
            {
                EExfiltrationStatus.NotPresent => ClosedColor,
                EExfiltrationStatus.UncompleteRequirements => RequirementsColor,
                _ => OpenColor,
            };
        }
    }
}
