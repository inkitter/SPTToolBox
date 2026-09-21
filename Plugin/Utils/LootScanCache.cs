using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;

namespace SPTMap.Utils
{
    // Single pass over GameWorld.LootList, shared by every marker provider that needs to look at
    // live loose loot (ItemMarkerProvider's wishlist/backpack rules, QuestUtils' find-item
    // condition) - scanning and TryCast<LootItem>'ing the whole list once per refresh, instead of
    // once per consumer, avoids walking the entire world loot list twice on the same map-open/raid
    // -start event. SPTMapController calls Rescan() exactly once per such event, before invoking
    // any consumer; consumers only ever read Items, never call Rescan() themselves.
    public static class LootScanCache
    {
        private static readonly List<LootItem> _items = new();

        public static IReadOnlyList<LootItem> Items => _items;

        public static void Rescan()
        {
            _items.Clear();

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

            foreach (var killable in lootList)
            {
                var loot = killable.TryCast<LootItem>();
                if (loot != null)
                {
                    _items.Add(loot);
                }
            }
        }
    }
}
