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
    // predecessor project, and this class's old name), but that's a design still being worked out
    // (see git history), so for now this deliberately marks everything since a debug tool should
    // default to showing everything rather than a curated subset. A live
    // Object.FindObjectsOfType<LootableContainer> scan instead of a Harmony patch on
    // GameWorld.OnGameStarted, since these containers are ordinary level geometry present in the
    // scene for the whole raid.
    //
    // Tried GameWorld.WorldInteractiveObjects() instead (LootableContainer's own base type is
    // WorldInteractiveObject, confirmed via decompile) - reverted after confirming in-game that it
    // throws a NullReferenceException every single call this early in the raid lifecycle, spamming
    // the log every frame via DoorMarkerProvider's identical retry loop (see that class for the
    // same revert). A decompiled interop stub only proves the API's signature exists, not that
    // it's safe to call here - don't repeat this without an in-game check first.
    public class LootableContainerMarkerProvider
    {
        private const string Category = "Container";
        private static readonly Color MarkerColor = new(0.6f, 0.3f, 0.1f);

        // some maps genuinely have zero lootable containers loaded this early, so "found nothing"
        // can't stop the retry the way it does for ExtractMarkerProvider/QuestMarkerProvider - only
        // a bounded time budget can, otherwise this full-scene FindObjectsOfType scan re-runs every
        // single frame for the rest of the raid on those maps (this is exactly what tanked FPS
        // before - see git history).
        private const float MaxRetrySeconds = 5f;

        private readonly List<MapMarker> _markers = new();
        private bool _populated;
        private float _firstAttemptTime = -1f;

        // GetText's tooltip build below walks the container's live native item tree
        // (ItemOwner.RootItem.GetAllVisibleItems()) - DrawMarker calls it every single OnGUI pass
        // while the mouse sits over that marker's tiny dot, not once per hover. Concurrently
        // mutating that same native collection (a bot looting the container, items shifting) while
        // this walk is mid-enumeration is exactly the "AccessViolationException surfaces somewhere
        // unrelated later in the frame" failure mode DoorMarkerProvider's key-scan comment already
        // documents for the player's own inventory. Caching the last-built tooltip and only
        // rebuilding when the hovered container actually changes (an edge, not every frame)
        // collapses "N frames of hovering" down to one walk per hover, the same risk-reduction
        // Door's throttle achieves, just edge-triggered instead of timer-throttled since only one
        // marker is realistically hovered at a time.
        private LootableContainer _cachedTooltipContainer;
        private string _cachedTooltipText;

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
            _cachedTooltipContainer = null;
            _cachedTooltipText = null;
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
                // rebuilt whenever this becomes the newly-hovered container (see the cache fields'
                // comment above) so it reflects what's still inside right now, not a stale snapshot
                // from raid start - e.g. after another player/bot loots it empty - without walking
                // the live item tree every single frame the mouse happens to sit still over it.
                GetText = () => GetTooltip(container, text),
            };

            _markers.Add(marker);
            MarkerManager.Add(marker);
        }

        private string GetTooltip(LootableContainer container, string displayName)
        {
            // container is a UnityEngine.Object - == is Unity's fake-null-aware overload, safe to
            // use for this identity comparison (see git history/memory on ?./?? vs ==).
            if (_cachedTooltipContainer == container)
            {
                return _cachedTooltipText;
            }

            _cachedTooltipContainer = container;
            _cachedTooltipText = BuildTooltip(container, displayName);
            return _cachedTooltipText;
        }

        private static string BuildTooltip(LootableContainer container, string displayName)
        {
            var sb = new StringBuilder(displayName);

            // container is a UnityEngine.Object - it can be destroyed between when this marker was
            // added and whenever the player actually hovers it, so this needs the explicit null
            // check even though it only runs on hover, not every frame.
            if (container == null)
            {
                return sb.ToString();
            }

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
