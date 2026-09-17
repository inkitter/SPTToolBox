using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using SPTMap.Config;
using SPTMap.Utils;
using UnityEngine;
using MapMarker = SPTMap.Data.MapMarker;

namespace SPTMap.DynamicMarkers
{
    // Loose-loot markers driven by a single pass over GameWorld.LootList each Rescan, rather than
    // one provider class per feature - a wishlist item and the player's own dropped backpack are
    // both just "this LootItem currently in the world should get a marker with this look", so they
    // share one scan/diff/lifecycle and differ only in their IItemRule (which items to mark, and
    // how). Adding another "highlight this kind of loose loot" feature means adding a new IItemRule
    // here, not a new provider class with its own copy of the scan/diff/OnRaidStart/OnRaidEnd
    // wiring in SPTMapController.
    public class ItemMarkerProvider
    {
        private readonly struct MarkerSpec
        {
            public readonly string Category;
            public readonly string ImagePath;
            public readonly Color Color;
            public readonly bool ShowLabel;

            public MarkerSpec(string category, string imagePath, Color color, bool showLabel)
            {
                Category = category;
                ImagePath = imagePath;
                Color = color;
                ShowLabel = showLabel;
            }
        }

        private interface IItemRule
        {
            void OnRaidStart();
            void OnRaidEnd();

            // called once per Rescan, before any TryGetMarkerSpec calls - lets a rule recompute
            // whatever external state it needs (the wishlist id set, which item id just got
            // dropped, ...) once per scan instead of once per loot item.
            void PrepareRescan();

            bool TryGetMarkerSpec(LootItem loot, out MarkerSpec spec);
        }

        // Wishlist items lying in the world as loose loot. Ported from the predecessor project's
        // (archived) LootMarkerProvider. Wishlist membership comes straight off the loaded profile
        // (Profile.WishlistManager.GetWishlist()) - no server call, no caching needed.
        private class WishlistRule : IItemRule
        {
            private const string Category = "Wishlist";
            private const string ImagePath = "Markers/star.png";
            private static readonly Color MarkerColor = Color.yellow;

            private readonly HashSet<string> _wishlistIds = new();

            public void OnRaidStart()
            {
            }

            public void OnRaidEnd()
            {
                _wishlistIds.Clear();
            }

            public void PrepareRescan()
            {
                _wishlistIds.Clear();
                if (!Settings.ShowWishlist.Value)
                {
                    return;
                }

                // Player is a UnityEngine.Object-derived (MonoBehaviour) - explicit ifs instead of
                // ?., see git history/memory ("?./?? bypasses Unity's fake-null override").
                var player = GameUtils.GetMainPlayer();
                if (player == null)
                {
                    return;
                }

                var wishlistManager = player.Profile?.WishlistManager;
                var wishlist = wishlistManager?.GetWishlist();
                if (wishlist == null)
                {
                    return;
                }

                foreach (var id in wishlist.Keys.ToSystemList())
                {
                    _wishlistIds.Add(id);
                }
            }

            public bool TryGetMarkerSpec(LootItem loot, out MarkerSpec spec)
            {
                if (_wishlistIds.Contains(loot.TemplateId))
                {
                    spec = new MarkerSpec(Category, ImagePath, MarkerColor, false);
                    return true;
                }

                spec = default;
                return false;
            }
        }

        // The local player's own dropped/thrown backpack specifically - not corpses', not other
        // players', not static loot backpacks (matches the predecessor project's
        // ShowDroppedBackpackInRaid setting: "the player's dropped backpacks, not anyone else's").
        // Patch-free: instead of hooking PlayerInventoryController.ThrowItem like the predecessor
        // did, this watches the main player's Backpack equipment slot each PrepareRescan and
        // remembers the contained item's Id when the slot goes from occupied to empty (i.e.
        // dropped), then matches that Id against the same LootList pass every other rule uses.
        private class BackpackRule : IItemRule
        {
            private const string Category = "Dropped Backpack";
            private const string ImagePath = "Markers/backpack.png";
            private static readonly Color MarkerColor = Color.green;

            private string _lastEquippedItemId;
            private string _pendingDroppedItemId;
            // once a dropped backpack's item id is seen once in the world it's remembered
            // permanently - item ids are per-instance GUIDs, not template ids, so this can never
            // false-match a different item and doesn't need explicit clearing when the backpack is
            // eventually picked back up (it just stops appearing in the scan).
            private string _trackedItemId;

            public void OnRaidStart()
            {
                _lastEquippedItemId = GetEquippedBackpackItemId();
                _pendingDroppedItemId = null;
                _trackedItemId = null;
            }

            public void OnRaidEnd()
            {
                _lastEquippedItemId = null;
                _pendingDroppedItemId = null;
                _trackedItemId = null;
            }

            public void PrepareRescan()
            {
                var currentEquippedId = GetEquippedBackpackItemId();
                if (_lastEquippedItemId != null && currentEquippedId == null)
                {
                    _pendingDroppedItemId = _lastEquippedItemId;
                }

                _lastEquippedItemId = currentEquippedId;
            }

            public bool TryGetMarkerSpec(LootItem loot, out MarkerSpec spec)
            {
                spec = default;
                var itemId = loot.Item?.Id;
                if (itemId == null)
                {
                    return false;
                }

                if (itemId != _trackedItemId && itemId != _pendingDroppedItemId)
                {
                    return false;
                }

                _trackedItemId = itemId;
                _pendingDroppedItemId = null;
                spec = new MarkerSpec(Category, ImagePath, MarkerColor, true);
                return true;
            }

            // Player is a UnityEngine.Object-derived (MonoBehaviour) - explicit ifs instead of a ?.
            // chain, per the "?./?? bypasses Unity's fake-null override" rule (see git
            // history/memory). Inventory/Equipment/Slot/Item are plain Il2CppSystem.Object, not
            // UnityEngine.Object, so ?. on those hops is fine.
            private static string GetEquippedBackpackItemId()
            {
                var player = GameUtils.GetMainPlayer();
                if (player == null)
                {
                    return null;
                }

                var equipment = player.Inventory?.Equipment;
                return equipment?.GetSlot(EquipmentSlot.Backpack)?.ContainedItem?.Id;
            }
        }

        private readonly List<IItemRule> _rules = new()
        {
            new WishlistRule(),
            new BackpackRule(),
        };

        private readonly Dictionary<(IItemRule Rule, LootItem Loot), MapMarker> _markers = new();
        // reused across Rescan calls instead of allocating fresh collections every trigger.
        private readonly HashSet<(IItemRule Rule, LootItem Loot)> _foundScratch = new();
        private readonly List<(IItemRule Rule, LootItem Loot)> _staleScratch = new();

        public void OnRaidStart()
        {
            foreach (var rule in _rules)
            {
                rule.OnRaidStart();
            }

            Rescan();
        }

        public void OnRaidEnd()
        {
            foreach (var marker in _markers.Values)
            {
                MarkerManager.Remove(marker);
            }

            _markers.Clear();
            _foundScratch.Clear();
            _staleScratch.Clear();

            foreach (var rule in _rules)
            {
                rule.OnRaidEnd();
            }
        }

        // Called once on the frame the map is opened (see SPTMapController's peek-toggle edge) -
        // this is the only place these markers refresh after the initial raid-start scan. No
        // periodic re-trigger even if the map stays open a long time: re-diffing the whole
        // GameWorld.LootList matters far less than avoiding another source of periodic frame drops
        // - close and reopen the map to force a fresh look.
        public void RefreshNow()
        {
            Rescan();
        }

        private void Rescan()
        {
            foreach (var rule in _rules)
            {
                rule.PrepareRescan();
            }

            // GameWorld is a UnityEngine.Object-derived (MonoBehaviour) - explicit ifs instead of
            // ?., see git history/memory ("?./?? bypasses Unity's fake-null override").
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
            {
                return;
            }

            var lootList = gameWorld.LootList;
            if (lootList == null)
            {
                return;
            }

            _foundScratch.Clear();
            foreach (var killable in lootList)
            {
                var loot = killable.TryCast<LootItem>();
                if (loot == null)
                {
                    continue;
                }

                foreach (var rule in _rules)
                {
                    if (!rule.TryGetMarkerSpec(loot, out var spec))
                    {
                        continue;
                    }

                    var key = (rule, loot);
                    _foundScratch.Add(key);
                    if (!_markers.ContainsKey(key))
                    {
                        AddMarker(key, loot, spec);
                    }
                }
            }

            _staleScratch.Clear();
            foreach (var key in _markers.Keys)
            {
                if (!_foundScratch.Contains(key))
                {
                    _staleScratch.Add(key);
                }
            }

            foreach (var key in _staleScratch)
            {
                MarkerManager.Remove(_markers[key]);
                _markers.Remove(key);
            }
        }

        private void AddMarker((IItemRule Rule, LootItem Loot) key, LootItem loot, MarkerSpec spec)
        {
            if (loot.transform == null)
            {
                return;
            }

            // Position is snapshotted once here rather than read from a live Transform every OnGUI
            // frame - loose loot only needs to be right as of the last Rescan() (map-open or
            // raid-start), not tracked in real time, and a snapshot can't crash on a Transform the
            // engine destroys later (item picked up/despawned) between rescans.
            var worldPos = loot.transform.position;
            var marker = new MapMarker
            {
                Category = spec.Category,
                ImagePath = spec.ImagePath,
                Text = loot.Item.ShortName.BSGLocalized(),
                Color = spec.Color,
                ShowLabel = spec.ShowLabel,
                GetPosition = () => MathUtils.ConvertToMapPosition(worldPos),
                GetWorldPosition = () => worldPos,
            };

            _markers[key] = marker;
            MarkerManager.Add(marker);
        }
    }
}
