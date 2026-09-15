using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Vehicle;

namespace SPTMap.Utils
{
    public static class GameUtils
    {
        // bosses worth calling out distinctly on the map (arena/event spawns included) - ported
        // from the old project's tracked-boss list.
        private static readonly HashSet<WildSpawnType> _trackedBosses = new()
        {
            WildSpawnType.bossBoar,
            WildSpawnType.bossBully,
            WildSpawnType.bossGluhar,
            WildSpawnType.bossKilla,
            WildSpawnType.bossKnight,
            WildSpawnType.followerBigPipe,
            WildSpawnType.followerBirdEye,
            WildSpawnType.bossKolontay,
            WildSpawnType.bossKojaniy,
            WildSpawnType.bossSanitar,
            WildSpawnType.bossTagilla,
            WildSpawnType.bossPartisan,
            WildSpawnType.bossZryachiy,
            WildSpawnType.gifter,
            WildSpawnType.arenaFighterEvent,
            WildSpawnType.sectantPriest,
            WildSpawnType.bossTagillaAgro,
            WildSpawnType.bossKillaAgro,
            WildSpawnType.tagillaHelperAgro,
            (WildSpawnType)199, // Legion
            (WildSpawnType)801, // Punisher
        };

        public static bool IsInRaid()
        {
            var game = Singleton<AbstractGame>.Instance;
            return game != null && game.InRaid;
        }

        public static string GetCurrentMapInternalName()
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            return gameWorld?.MainPlayer?.Location;
        }

        public static Player GetMainPlayer()
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            return gameWorld?.MainPlayer;
        }

        public static BTRView GetBTRView()
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            return gameWorld?.BtrController?.BtrView;
        }

        public static bool IsScavRaid()
        {
            var player = GetMainPlayer();
            return IsInRaid() && player != null && player.Side == EPlayerSide.Savage;
        }

        public static int? GetIntelLevel()
        {
            var player = GetMainPlayer();
            return player?.Profile?.Hideout?.Areas
                .SingleOrDefault(a => a.AreaType == EAreaType.IntelligenceCenter)?.Level;
        }

        public static string BSGLocalized(this string id)
        {
            return string.IsNullOrWhiteSpace(id) ? "" : id.Localized();
        }

        public static bool IsGroupedWithMainPlayer(this Player player)
        {
            var mainPlayerGroupId = GetMainPlayer()?.GroupId;
            return !string.IsNullOrEmpty(mainPlayerGroupId) && player.GroupId == mainPlayerGroupId;
        }

        public static bool IsTrackedBoss(this Player player)
        {
            return PlayerRules.IsTrackedBoss(player.Profile.Side, player.Profile.Info.Settings.Role, _trackedBosses);
        }

        public static bool IsPMC(this Player player)
        {
            return PlayerRules.IsPMC(player.Profile.Side, player.Profile.Info.Settings.Role);
        }

        public static bool IsScav(this Player player)
        {
            return PlayerRules.IsScav(player.Profile.Side, player.Profile.Info.Settings.Role);
        }

        public static bool DidMainPlayerKill(this Player player)
        {
            var aggressor = player.LastAggressor;
            var mainPlayer = GetMainPlayer();
            if (aggressor == null || mainPlayer == null) return false;

            return PlayerRules.DidKill(aggressor.ProfileId, mainPlayer.ProfileId);
        }

        public static bool DidTeammateKill(this Player player)
        {
            var aggressor = player.LastAggressor;
            var mainPlayer = GetMainPlayer();
            if (aggressor == null || mainPlayer == null) return false;

            return PlayerRules.DidTeammateKill(aggressor.ProfileId, aggressor.GroupId, mainPlayer.ProfileId, mainPlayer.GroupId);
        }

        public static bool IsBTRShooter(this Player player)
        {
            return PlayerRules.IsBTRShooter(player.Profile.Side, player.Profile.Info.Settings.Role);
        }

        public static bool HasCorpse(this Player player)
        {
            return player.Corpse != null;
        }

        public static bool IsHeadlessClient(this Player player)
        {
            return PlayerRules.IsHeadlessClient(player.Profile.Info.MemberCategory);
        }

        public static List<T> ToSystemList<T>(this Il2CppSystem.Collections.Generic.IEnumerable<T> source)
        {
            // see the old project's MEMORY.md - some Il2Cpp IEnumerable<T> props (e.g.
            // AllPlayersEverExisted) hand back a freshly-constructed native enumerable/enumerator
            // every call; GC.KeepAlive pins it until the loop's done so the managed wrapper being
            // collected mid-loop can't free the underlying native object out from under us.
            var result = new List<T>();
            var enumerator = source.GetEnumerator();
            var moveNextEnumerator = enumerator.Cast<Il2CppSystem.Collections.IEnumerator>();
            while (moveNextEnumerator.MoveNext())
            {
                result.Add(enumerator.Current);
            }

            GC.KeepAlive(enumerator);
            GC.KeepAlive(source);

            return result;
        }
    }
}
