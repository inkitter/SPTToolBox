using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using SPTMap.Data;
using SPTMap.Utils;
using UnityEngine;

namespace SPTMap.DynamicMarkers
{
    // The local player's own dropped/thrown backpack specifically - not corpses', not other
    // players', not static loot backpacks (matches the predecessor project's
    // ShowDroppedBackpackInRaid setting: "the player's dropped backpacks, not anyone else's").
    // Patch-free: instead of hooking PlayerInventoryController.ThrowItem like the predecessor
    // did, this polls the main player's Backpack equipment slot each tick and remembers the
    // contained item's Id when the slot goes from occupied to empty (i.e. dropped), then matches
    // that Id against GameWorld.LootList once the physical LootItem has spawned there - same
    // rescan/track shape as WishlistMarkerProvider, just keyed off a single remembered id instead
    // of a whole wishlist.
    public class BackpackMarkerProvider
    {
        private const string Category = "Dropped Backpack";
        private const string ImagePath = "Markers/backpack.png";
        private static readonly Color MarkerColor = Color.green;

        private const float RescanIntervalSeconds = 1f;

        private string _lastEquippedItemId;
        private string _pendingDroppedItemId;
        private LootItem _trackedLoot;
        private Data.MapMarker _marker;
        private float _rescanAccumulator;

        public void OnRaidStart()
        {
            _lastEquippedItemId = GetEquippedBackpackItemId();
        }

        public void OnRaidEnd()
        {
            if (_marker != null)
            {
                MarkerManager.Remove(_marker);
            }

            _marker = null;
            _trackedLoot = null;
            _pendingDroppedItemId = null;
            _lastEquippedItemId = null;
            _rescanAccumulator = 0f;
        }

        // Called once on the frame the map is opened (see SPTMapController's peek-toggle edge), so
        // a pending drop isn't left waiting for the map-open interval before its LootList lookup
        // runs. Equivalent to Tick(RescanIntervalSeconds) - always past the throttle.
        public void RefreshNow()
        {
            Tick(RescanIntervalSeconds);
        }

        // Only called while the full map is open (see SPTMapController.UpdateInternal's
        // _peekToggled gate), same as quest/wishlist/airdrop - the equipment-slot check itself is
        // cheap (one dictionary/array lookup, no scene scan), but gating it too means a backpack
        // dropped while the map is closed is only noticed once it's opened, not the instant it
        // happens.
        public void Tick(float deltaTime)
        {
            var currentEquippedId = GetEquippedBackpackItemId();
            if (_lastEquippedItemId != null && currentEquippedId == null)
            {
                _pendingDroppedItemId = _lastEquippedItemId;
            }

            _lastEquippedItemId = currentEquippedId;

            // already tracking the dropped backpack's LootItem - just confirm it's still in the
            // world (picked back up/destroyed removes it from LootList).
            if (_trackedLoot != null)
            {
                _rescanAccumulator += deltaTime;
                if (_rescanAccumulator < RescanIntervalSeconds)
                {
                    return;
                }

                _rescanAccumulator = 0f;
                if (!IsStillInWorld(_trackedLoot))
                {
                    MarkerManager.Remove(_marker);
                    _marker = null;
                    _trackedLoot = null;
                }

                return;
            }

            if (_pendingDroppedItemId == null)
            {
                return;
            }

            _rescanAccumulator += deltaTime;
            if (_rescanAccumulator < RescanIntervalSeconds)
            {
                return;
            }

            _rescanAccumulator = 0f;

            var found = FindLootById(_pendingDroppedItemId);
            if (found != null)
            {
                _trackedLoot = found;
                _pendingDroppedItemId = null;
                AddMarker(found);
            }
        }

        // Player is a MonoBehaviour (UnityEngine.Object) - explicit ifs instead of a ?. chain, per
        // the "?./?? bypasses Unity's fake-null override" rule (see git history/memory). Inventory/
        // Equipment/Slot/Item are plain Il2CppSystem.Object, not UnityEngine.Object, so ?. on those
        // hops is fine.
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

        private static bool IsStillInWorld(LootItem loot)
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
            {
                return false;
            }

            var lootList = gameWorld.LootList;
            if (lootList == null)
            {
                return false;
            }

            foreach (var killable in lootList)
            {
                if (killable.TryCast<LootItem>() == loot)
                {
                    return true;
                }
            }

            return false;
        }

        private static LootItem FindLootById(string itemId)
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
            {
                return null;
            }

            var lootList = gameWorld.LootList;
            if (lootList == null)
            {
                return null;
            }

            foreach (var killable in lootList)
            {
                var loot = killable.TryCast<LootItem>();
                if (loot == null)
                {
                    continue;
                }

                var item = loot.Item;
                if (item != null && item.Id == itemId)
                {
                    return loot;
                }
            }

            return null;
        }

        private void AddMarker(LootItem loot)
        {
            var worldTransform = loot.transform;
            _marker = new Data.MapMarker
            {
                Category = Category,
                ImagePath = ImagePath,
                Text = loot.Item.ShortName.BSGLocalized(),
                Color = MarkerColor,
                ShowLabel = true,
                GetPosition = () => MathUtils.ConvertToMapPosition(worldTransform.position),
                GetWorldPosition = () => worldTransform.position,
            };

            MarkerManager.Add(_marker);
        }
    }
}
