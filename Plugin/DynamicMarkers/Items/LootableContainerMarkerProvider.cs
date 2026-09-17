using System.Collections.Generic;
using System.Text;
using EFT.Interactive;
using SPTMap.Data;
using SPTMap.Utils;
using UnityEngine;

namespace SPTMap.DynamicMarkers
{
    // Debug tool: marks every LootableContainer on the map (ammo boxes, weapon crates, medbags,
    // safes, the works), not just a curated "hidden stash" subset - this used to filter down to a
    // handful of known GameObject name prefixes (a hidden-cache heuristic borrowed from the
    // predecessor project), but that's a design still being worked out (see git history), so for
    // now this deliberately marks everything since a debug tool should default to showing
    // everything rather than a curated subset. Same populated-once-with-retry shape as
    // DoorMarkerProvider: a live Object.FindObjectsOfType scan instead of a Harmony patch on
    // GameWorld.OnGameStarted, since these containers are ordinary level geometry present in the
    // scene for the whole raid.
    public class HiddenStashMarkerProvider
    {
        private const string Category = "Hidden Stash";
        private static readonly Color MarkerColor = new(0.6f, 0.3f, 0.1f);

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
                    AddMarker(container);
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

        // one map can have dozens of these - a full-size icon per container clutters the map
        // badly, so this draws as a small dot instead (no ImagePath falls back to a plain filled
        // square in SPTMapController.DrawMarker, sized down via IconSizeOverride).
        private const float DotSize = 6f;

        private void AddMarker(LootableContainer container)
        {
            var worldPos = container.transform.position;
            var pos = MathUtils.ConvertToMapPosition(worldPos);

            // container's own localized display name (e.g. "Weapon box", "Wooden ammo box") -
            // same ItemOwner.RootItem.ShortName path used for real loot items elsewhere in this
            // project - falls back to the generic category if the container has no backing Item
            // for some reason.
            var itemName = container.ItemOwner?.RootItem?.ShortName.BSGLocalized();
            var text = string.IsNullOrEmpty(itemName) ? Category : itemName;

            var marker = new MapMarker
            {
                Category = Category,
                Text = text,
                Color = MarkerColor,
                ShowLabel = false,
                IconSizeOverride = DotSize,
                HideOnOtherFloors = true,
                GetPosition = () => pos,
                GetWorldPosition = () => worldPos,
                // recomputed live each time the tooltip is actually shown (not every frame for
                // every container) so it reflects what's still inside right now, not a stale
                // snapshot from raid start - e.g. after another player/bot loots it empty.
                GetText = () => BuildTooltip(container, text),
            };

            _markers.Add(marker);
            MarkerManager.Add(marker);
        }

        private static string BuildTooltip(LootableContainer container, string displayName)
        {
            var sb = new StringBuilder(displayName);

            var rootItem = container.ItemOwner?.RootItem;
            var items = rootItem?.GetAllVisibleItems();
            if (items == null)
            {
                return sb.ToString();
            }

            var any = false;
            foreach (var item in items)
            {
                if (item == null || item == rootItem)
                {
                    continue;
                }

                any = true;
                sb.Append("\n- ").Append(item.ShortName.BSGLocalized());
            }

            if (!any)
            {
                sb.Append("\n(empty)");
            }

            return sb.ToString();
        }
    }
}
