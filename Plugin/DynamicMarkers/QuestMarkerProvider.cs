using System.Collections.Generic;
using SPTMap.Data;
using SPTMap.Utils;

namespace SPTMap.DynamicMarkers
{
    // Ported from the old SPT-DynamicMaps project's QuestMarkerProvider. No scav-raid quests, same
    // as the old project (scav raids don't have PMC quest objectives to show).
    public class QuestMarkerProvider
    {
        // Conditions (and so markers) can go from incomplete to complete mid-raid - re-derive the
        // whole marker set on this interval so completed objectives drop off instead of lingering
        // for the rest of the raid.
        private const float RefreshIntervalSeconds = 3f;

        private readonly List<MapMarker> _markers = new();
        private float _refreshAccumulator;

        // Mirrors ExtractMarkerProvider/DoorMarkerProvider - the main player isn't necessarily
        // resolvable the instant OnRaidStart first runs, so keep retrying each frame until it is.
        private bool _populated;

        public void OnRaidStart()
        {
            if (_populated)
            {
                return;
            }

            if (GameUtils.IsScavRaid())
            {
                _populated = true;
                return;
            }

            AddQuestMarkers();
        }

        public void OnRaidEnd()
        {
            QuestUtils.DiscardQuestData();
            RemoveMarkers();
            _populated = false;
            _refreshAccumulator = 0f;
        }

        // Called every frame from SPTMapBehaviour.Update while in a raid; only actually refreshes
        // once the configured interval has elapsed and the initial marker set has been populated.
        public void Tick(float deltaTime)
        {
            if (!_populated)
            {
                return;
            }

            _refreshAccumulator += deltaTime;
            if (_refreshAccumulator < RefreshIntervalSeconds)
            {
                return;
            }

            _refreshAccumulator = 0f;
            RemoveMarkers();
            AddQuestMarkers();
        }

        private void AddQuestMarkers()
        {
            QuestUtils.TryCaptureQuestData();

            var player = GameUtils.GetMainPlayer();
            if (player == null)
            {
                return;
            }

            foreach (var marker in QuestUtils.GetMarkersForPlayer(player))
            {
                _markers.Add(marker);
                MarkerManager.Add(marker);
            }

            _populated = true;
        }

        private void RemoveMarkers()
        {
            foreach (var marker in _markers)
            {
                MarkerManager.Remove(marker);
            }

            _markers.Clear();
        }
    }
}
