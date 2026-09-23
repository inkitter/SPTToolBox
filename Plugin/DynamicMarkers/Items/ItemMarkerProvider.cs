using System.Collections.Generic;
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

        // Backpacks the local player wore at some point this raid and has since dropped - not
        // corpses', not other players', not static loot backpacks. Patch-free: Sample() reads the
        // main player's Backpack slot on a cheap ~1s poll (ItemMarkerProvider.Tick) plus every
        // rescan, remembering every backpack instance Id ever equipped. A loose LootItem whose Id is
        // in that set and isn't what's currently worn is a dropped backpack. Item ids are
        // per-instance GUIDs, so this can't false-match a different backpack of the same template,
        // and a picked-up backpack simply stops appearing in the loot scan.
        private class BackpackRule : IItemRule
        {
            private const string Category = "Dropped Backpack";
            private const string ImagePath = "Markers/backpack.png";
            private static readonly Color MarkerColor = Color.green;

            private readonly HashSet<string> _everEquippedIds = new();
            private string _currentEquippedId;

            public void OnRaidStart()
            {
                _everEquippedIds.Clear();
                _currentEquippedId = null;
                Sample();
            }

            public void OnRaidEnd()
            {
                _everEquippedIds.Clear();
                _currentEquippedId = null;
            }

            public void Sample()
            {
                _currentEquippedId = GetEquippedBackpackItemId();
                if (_currentEquippedId != null)
                {
                    _everEquippedIds.Add(_currentEquippedId);
                }
            }

            public void PrepareRescan()
            {
                Sample();
            }

            public bool TryGetMarkerSpec(LootItem loot, out MarkerSpec spec)
            {
                spec = default;
                if (!Settings.ShowDroppedBackpack.Value || _everEquippedIds.Count == 0)
                {
                    return false;
                }

                var itemId = loot.Item?.Id;
                if (itemId == null || itemId == _currentEquippedId || !_everEquippedIds.Contains(itemId))
                {
                    return false;
                }

                spec = new MarkerSpec(Category, ImagePath, MarkerColor, true);
                return true;
            }

            // Player is a UnityEngine.Object-derived (MonoBehaviour) - explicit if instead of ?..
            // Inventory/Equipment/Slot/Item are plain Il2CppSystem.Object, so ?. on those is fine.
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

        private const float BackpackSampleIntervalSeconds = 1f;

        private readonly BackpackRule _backpackRule = new();
        private float _backpackSampleTimer;

        private readonly List<IItemRule> _rules;

        public ItemMarkerProvider()
        {
            _rules = new List<IItemRule> { new WishlistRule(), _backpackRule };
        }

        // Keyed by the item's instance Id (a stable string), not the LootItem itself - Il2Cpp
        // wrapper objects for the same native LootItem aren't guaranteed to be the same managed
        // instance across scans, which would make every rescan drop and re-add every marker.
        private readonly Dictionary<(IItemRule Rule, string ItemId), MapMarker> _markers = new();
        // reused across Rescan calls instead of allocating fresh collections every trigger.
        private readonly HashSet<(IItemRule Rule, string ItemId)> _foundScratch = new();
        private readonly List<(IItemRule Rule, string ItemId)> _staleScratch = new();

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
        // Cheap per-frame hook: only samples the local player's Backpack slot about once a second
        // so a backpack worn briefly between map opens (picked up, then dropped) is still known.
        // No world/loot scanning here - that stays map-open-only.
        public void Tick(float deltaTime)
        {
            if (!Settings.ShowDroppedBackpack.Value)
            {
                return;
            }

            _backpackSampleTimer += deltaTime;
            if (_backpackSampleTimer < BackpackSampleIntervalSeconds)
            {
                return;
            }

            _backpackSampleTimer = 0f;
            _backpackRule.Sample();
        }

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

            // Reads the shared LootScanCache (rescanned once per event by SPTMapController)
            // instead of walking GameWorld.LootList itself - QuestUtils reads the same cache, so
            // the full world loot list isn't scanned twice for one map-open/raid-start.
            _foundScratch.Clear();
            foreach (var loot in LootScanCache.Items)
            {
                foreach (var rule in _rules)
                {
                    if (!rule.TryGetMarkerSpec(loot, out var spec))
                    {
                        continue;
                    }

                    var itemId = loot.Item?.Id;
                    if (itemId == null)
                    {
                        continue;
                    }

                    var key = (rule, itemId);
                    _foundScratch.Add(key);
                    if (!_markers.ContainsKey(key))
                    {
                        AddMarker(key, loot, spec);
                    }
                }
            }

            Plugin.Log.LogInfo($"[items] rescan: {LootScanCache.Items.Count} loose loot, {_markers.Count} marked");

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

        private void AddMarker((IItemRule Rule, string ItemId) key, LootItem loot, MarkerSpec spec)
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
