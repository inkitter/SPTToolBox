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

        private readonly Dictionary<LootItem, MapMarker> _markers = new();
        // reused across Rescan calls instead of allocating three fresh collections every trigger -
        // same shape as AirdropMarkerProvider's scratch fields.
        private readonly HashSet<string> _wishlistIdsScratch = new();
        private readonly HashSet<LootItem> _foundScratch = new();
        private readonly List<LootItem> _staleScratch = new();

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
            _wishlistIdsScratch.Clear();
            _foundScratch.Clear();
            _staleScratch.Clear();
        }

        // Called once on the frame the map is opened (see SPTMapController's peek-toggle edge) -
        // this is the only place wishlist markers refresh after the initial raid-start scan. No
        // periodic re-trigger even if the map stays open a long time: re-diffing the whole
        // GameWorld.LootList matters far less than avoiding another source of periodic frame drops
        // - close and reopen the map to force a fresh look.
        public void RefreshNow()
        {
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

            _wishlistIdsScratch.Clear();
            foreach (var id in wishlist.Keys.ToSystemList())
            {
                _wishlistIdsScratch.Add(id);
            }

            _foundScratch.Clear();
            foreach (var killable in lootList)
            {
                var loot = killable.TryCast<LootItem>();
                if (loot != null && _wishlistIdsScratch.Contains(loot.TemplateId))
                {
                    _foundScratch.Add(loot);
                }
            }

            _staleScratch.Clear();
            foreach (var tracked in _markers.Keys)
            {
                if (!_foundScratch.Contains(tracked))
                {
                    _staleScratch.Add(tracked);
                }
            }

            foreach (var item in _staleScratch)
            {
                MarkerManager.Remove(_markers[item]);
                _markers.Remove(item);
            }

            foreach (var loot in _foundScratch)
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
