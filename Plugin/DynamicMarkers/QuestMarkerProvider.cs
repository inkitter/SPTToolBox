using System.Collections.Generic;
using SPTMap.Data;
using SPTMap.Utils;

namespace SPTMap.DynamicMarkers
{
    // Ported from the old SPT-DynamicMaps project's QuestMarkerProvider. No scav-raid quests, same
    // as the old project (scav raids don't have PMC quest objectives to show).
    public class QuestMarkerProvider
    {
        private readonly List<MapMarker> _markers = new();

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
        }

        // Called once on the frame the map is opened (see SPTMapController's peek-toggle edge) -
        // this is the only place quest markers refresh after the initial raid-start population.
        // No periodic re-trigger even if the map stays open a long time: a fixed-frame-cost rescan
        // (re-deriving every incomplete quest's conditions) matters far less than avoiding another
        // source of periodic frame drops - close and reopen the map to force a fresh look.
        public void RefreshNow()
        {
            if (!_populated)
            {
                return;
            }

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
