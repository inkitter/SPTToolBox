using System.Collections.Generic;
using EFT.Interactive;
using SPTMap.Data;
using SPTMap.Utils;
using UnityEngine;

namespace SPTMap.DynamicMarkers
{
    // Hidden stash caches - a fixed set of LootableContainers per map, identifiable only by a
    // handful of known GameObject name prefixes (no dedicated EFT type/flag for "this is a hidden
    // stash" - credit to the predecessor project/RaiRai for finding these). Same
    // populated-once-with-retry shape as DoorMarkerProvider: a live Object.FindObjectsOfType scan
    // instead of the predecessor's Harmony patch on GameWorld.OnGameStarted, since these containers
    // are ordinary level geometry present in the scene for the whole raid.
    public class HiddenStashMarkerProvider
    {
        private const string Category = "Hidden Stash";
        private const string ImagePath = "Markers/door_with_lock.png";
        private static readonly Color MarkerColor = new(0.6f, 0.3f, 0.1f);

        private static readonly string[] NamePrefixes =
        {
            "scontainer_wood_CAP",
            "scontainer_Blue_Barrel_Base_Cap",
        };

        // some maps genuinely have zero hidden stashes (or zero LootableContainers loaded this
        // early), so "found nothing" can't stop the retry the way it does for
        // ExtractMarkerProvider/QuestMarkerProvider - only a bounded time budget can, otherwise
        // this full-scene FindObjectsOfType scan re-runs every single frame for the rest of the
        // raid on those maps (this is exactly what tanked FPS before - see git history).
        private const float MaxRetrySeconds = 5f;

        private readonly List<MapMarker> _markers = new();
        private bool _populated;
        private float _firstAttemptTime = -1f;

        public void OnRaidStart()
        {
            if (_populated)
            {
                return;
            }

            if (_firstAttemptTime < 0f)
            {
                _firstAttemptTime = Time.time;
            }

            var containers = Object.FindObjectsOfType<LootableContainer>();
            if (containers != null)
            {
                foreach (var container in containers)
                {
                    if (IsHiddenStash(container))
                    {
                        AddMarker(container);
                    }
                }
            }

            if (_markers.Count > 0 || Time.time - _firstAttemptTime >= MaxRetrySeconds)
            {
                _populated = true;
            }
        }

        public void OnRaidEnd()
        {
            foreach (var marker in _markers)
            {
                MarkerManager.Remove(marker);
            }

            _markers.Clear();
            _populated = false;
            _firstAttemptTime = -1f;
        }

        private static bool IsHiddenStash(LootableContainer container)
        {
            var name = container.name;
            foreach (var prefix in NamePrefixes)
            {
                if (name.StartsWith(prefix))
                {
                    return true;
                }
            }

            return false;
        }

        private void AddMarker(LootableContainer container)
        {
            var worldPos = container.transform.position;
            var pos = MathUtils.ConvertToMapPosition(worldPos);
            var marker = new MapMarker
            {
                Category = Category,
                ImagePath = ImagePath,
                Text = Category,
                Color = MarkerColor,
                ShowLabel = true,
                GetPosition = () => pos,
                GetWorldPosition = () => worldPos,
            };

            _markers.Add(marker);
            MarkerManager.Add(marker);
        }
    }
}
