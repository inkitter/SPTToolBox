using System.Collections.Generic;
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

        // one map can have dozens of these - a full-size icon per container clutters the map
        // badly, so this draws as a small dot instead (no ImagePath falls back to a plain filled
        // square in SPTMapController.DrawMarker, sized down via IconSizeOverride).
        private const float DotSize = 6f;

        private readonly List<MapMarker> _markers = new();
        private bool _populated;

        // Called only on the frame the big map opens (never at raid start - raid load is already
        // hitch-prone). Containers are static level geometry, so the one full-scene
        // FindObjectsOfType scan runs until it finds any; later opens are no-ops.
        //
        // Deliberately position-only: never reads a container's contents. Walking a container's
        // item tree (GetAllItems() is a native lazy iterator) left half-enumerated native
        // iterators behind and caused a per-frame engine-side NullReferenceException flood.
        public void RefreshNow()
        {
            if (_populated)
            {
                return;
            }

            var containers = Object.FindObjectsOfType<LootableContainer>();
            if (containers != null)
            {
                foreach (var container in containers)
                {
                    AddMarker(container);
                }
            }

            _populated = _markers.Count > 0;
        }

        public void OnRaidEnd()
        {
            foreach (var marker in _markers)
            {
                MarkerManager.Remove(marker);
            }

            _markers.Clear();
            _populated = false;
        }

        private void AddMarker(LootableContainer container)
        {
            if (container == null)
            {
                return;
            }

            var worldPos = container.transform.position;
            var pos = MathUtils.ConvertToMapPosition(worldPos);

            // container's own localized display name (e.g. "Weapon box") - read once here, not
            // per frame; falls back to the generic category if there's no backing Item.
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
            };

            _markers.Add(marker);
            MarkerManager.Add(marker);
        }
    }
}
