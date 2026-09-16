using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using SPTMap.Data;
using SPTMap.Utils;
using UnityEngine;

namespace SPTMap.DynamicMarkers
{
    // Wishlist items lying in the world as loose loot. Ported from the predecessor project's
    // (archived) LootMarkerProvider, patch-free per this project's convention: scans
    // GameWorld.LootList directly on a timer (same rescan-and-diff shape as AirdropMarkerProvider)
    // instead of hooking pickup/spawn events. Wishlist membership comes straight off the loaded
    // profile (Profile.WishlistManager.GetWishlist()) - no server call, no caching needed.
    public class WishlistMarkerProvider
    {
        private const string Category = "Wishlist";
        private const string ImagePath = "Markers/star.png";
        private static readonly Color MarkerColor = Color.yellow;

        private const float RescanIntervalSeconds = 5f;

        private readonly Dictionary<LootItem, MapMarker> _markers = new();
        private float _rescanAccumulator;

        public void OnRaidStart()
        {
            Rescan();
        }

        public void OnRaidEnd()
        {
            foreach (var marker in _markers.Values)
            {
                MarkerManager.Remove(marker);
            }

            _markers.Clear();
            _rescanAccumulator = 0f;
        }

        // called every frame from SPTMapController.Update while in a raid; only actually rescans
        // once the interval elapses, since walking GameWorld.LootList + a fresh wishlist lookup
        // every frame is unnecessary overhead. Re-diffing (not just adding) picks up items other
        // players/bots looted since the last scan, so their markers don't linger.
        public void Tick(float deltaTime)
        {
            _rescanAccumulator += deltaTime;
            if (_rescanAccumulator < RescanIntervalSeconds)
            {
                return;
            }

            _rescanAccumulator = 0f;
            Rescan();
        }

        private void Rescan()
        {
            // Player/GameWorld are UnityEngine.Object-derived (MonoBehaviours) - Unity overrides
            // their ==/!= to catch a destroyed-but-not-yet-GC'd native object, but ?./?? bypass
            // that override and see raw CLR non-null, so this is explicit ifs all the way down
            // instead of a ?. chain.
            var player = GameUtils.GetMainPlayer();
            if (player == null)
            {
                return;
            }

            var profile = player.Profile;
            var wishlistManager = profile?.WishlistManager;
            if (wishlistManager == null)
            {
                return;
            }

            var wishlist = wishlistManager.GetWishlist();

            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
            {
                return;
            }

            var lootList = gameWorld.LootList;
            if (wishlist == null || lootList == null)
            {
                return;
            }

            var wishlistIds = new HashSet<string>();
            foreach (var id in wishlist.Keys.ToSystemList())
            {
                wishlistIds.Add(id);
            }

            var found = new HashSet<LootItem>();
            foreach (var killable in lootList)
            {
                var loot = killable.TryCast<LootItem>();
                if (loot != null && wishlistIds.Contains(loot.TemplateId))
                {
                    found.Add(loot);
                }
            }

            var stale = new List<LootItem>();
            foreach (var tracked in _markers.Keys)
            {
                if (!found.Contains(tracked))
                {
                    stale.Add(tracked);
                }
            }

            foreach (var item in stale)
            {
                MarkerManager.Remove(_markers[item]);
                _markers.Remove(item);
            }

            foreach (var loot in found)
            {
                AddMarker(loot);
            }
        }

        private void AddMarker(LootItem loot)
        {
            if (_markers.ContainsKey(loot) || loot.transform == null)
            {
                return;
            }

            var worldTransform = loot.transform;
            var marker = new MapMarker
            {
                Category = Category,
                ImagePath = ImagePath,
                Text = loot.Item.ShortName.BSGLocalized(),
                Color = MarkerColor,
                ShowLabel = false,
                GetPosition = () => MathUtils.ConvertToMapPosition(worldTransform.position),
                GetWorldPosition = () => worldTransform.position,
            };

            _markers[loot] = marker;
            MarkerManager.Add(marker);
        }
    }
}
