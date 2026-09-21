using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using Il2CppInterop.Runtime;
using SPTMap.Config;
using SPTMap.Data;
using SPTMap.Utils;
using UnityEngine;

namespace SPTMap.DynamicMarkers
{
    // Ported from the old SPT-DynamicMaps project's OtherPlayersMarkerProvider, merged with what
    // used to be a separate CorpseMarkerProvider, and now also covers the BTR (its turret gunner is
    // an invincible bot Player - see TryAddMarker) since that's just another live-tracked unit, not
    // a different problem - no reason for a separate BTRMarkerProvider. Each tracked player gets its
    // own small mutable TrackedPlayerState snapshot, refreshed on a configurable timer
    // (Settings.OtherPlayersPollIntervalMs) via Tick/Poll rather than read live off the Il2Cpp
    // Player object every draw. This used to be event-driven (Player.OnDead +
    // GameWorld.UnregisterPlayer), but GameWorld unregisters a dying player before OnDead's postfix
    // runs in practice, so the two events raced and a death could be missed entirely (see git
    // history). Polling our own state sidesteps that: death is just "IsAlive read as false this
    // poll", independent of which engine event fires when or in what order, and a marker going
    // stale is bounded by one poll interval instead of an unbounded miss.
    public class UnitMarkerProvider
    {
        private const string ArrowImagePath = "Markers/arrow.png";
        private const string StarImagePath = "Markers/star.png";
        private const string SkullImagePath = "Markers/skull.png";
        private const string BtrImagePath = "Markers/btr.png";

        private const string FriendlyCategory = "Friendly Player";
        private const string EnemyCategory = "Enemy Player";
        private const string ScavCategory = "Scav";
        private const string BossCategory = "Boss";
        private const string BtrCategory = "BTR";

        private static readonly Color FriendlyColor = Color.green;
        private static readonly Color PmcBearColor = Color.red;
        private static readonly Color PmcUsecColor = Color.yellow;
        private static readonly Color ScavColor = new(1f, 0.55f, 0f);
        private static readonly Color BossColor = new(0.6f, 0f, 0.8f);
        private static readonly Color BtrColor = Color.white;

        private static readonly Color FriendlyCorpseColor = Color.green;
        private static readonly Color KilledCorpseColor = Color.Lerp(Color.green, Color.white, 0.5f);
        private static readonly Color FriendlyKilledCorpseColor = Color.Lerp(Color.green, Color.red, 0.5f);
        private static readonly Color OtherCorpseColor = Color.gray;

        // A tracked player's live snapshot. Mutated in place by Poll(); the marker's Get* closures
        // just read whatever's currently in here, so drawing never touches the Il2Cpp Player object
        // directly. IsAlive false means the state is frozen for good (corpse) - Poll skips it from
        // then on.
        private class TrackedPlayerState
        {
            public Vector2 Position;
            public Vector3 WorldPosition;
            public Vector2? Facing;
            public bool IsAlive = true;
            public Color CorpseColor;
        }

        private class TrackedEntry
        {
            // null once the entry is confirmed dead - LockAsCorpse clears this. Nothing reads
            // Player after that point (Poll's IsAlive-false branch never dereferences it, and the
            // marker's closures only ever read State), so holding onto a full Player reference
            // (its whole Inventory/Equipment/AI component tree) for the rest of a long raid with
            // continuous bot spawns was pure waste - the corpse marker itself stays untouched.
            public Player Player;
            public MapMarker Marker;
            public TrackedPlayerState State;
        }

        // keyed by ProfileId (a stable string) rather than the Il2Cpp Player reference itself -
        // Il2Cpp wrapper objects handed back from different native calls (e.g. AllAlivePlayersList
        // vs. the reference captured at spawn) aren't guaranteed to be reference-equal, so a
        // Dictionary/HashSet keyed on Player could silently never match and Poll would think every
        // tracked player had left the instant it looked, deleting the marker one poll after it was
        // created.
        private readonly Dictionary<string, TrackedEntry> _entries = new();
        private readonly HashSet<string> _aliveScratch = new();
        // reused across Poll calls instead of allocating a fresh List every poll (default 150ms
        // interval, so this ran up to ~6-7 times/sec) just to safely snapshot _entries.Keys before
        // RemoveEntry mutates the dictionary mid-iteration.
        private readonly List<string> _trackedKeysScratch = new();
        private readonly Il2CppSystem.Action<IPlayer> _onPersonAdd;
        private float _pollAccumulator;

        public UnitMarkerProvider()
        {
            _onPersonAdd = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<IPlayer>>(new Action<IPlayer>(TryAddMarker));
        }

        public void OnRaidStart()
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            foreach (var player in gameWorld.AllAlivePlayersList)
            {
                if (!player.IsYourPlayer)
                {
                    TryAddMarker(player.Cast<IPlayer>());
                }
            }

            // only used for prompt discovery of newly-spawned players (e.g. reinforcements) - safe
            // to keep as an event since it only ever adds, never the removal/death side that used to
            // race.
            gameWorld.OnPersonAdd += _onPersonAdd;
        }

        public void OnRaidEnd()
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld is not null)
            {
                gameWorld.OnPersonAdd -= _onPersonAdd;
            }

            foreach (var entry in _entries.Values)
            {
                MarkerManager.Remove(entry.Marker);
            }

            _entries.Clear();
            _aliveScratch.Clear();
            _trackedKeysScratch.Clear();
            _pollAccumulator = 0f;
        }

        // Called every frame from SPTMapBehaviour.Update while in a raid; only actually refreshes
        // tracked state once the configured interval has elapsed.
        public void Tick(float deltaTime)
        {
            _pollAccumulator += deltaTime;
            var intervalSeconds = Settings.OtherPlayersPollIntervalMs.Value / 1000f;
            if (_pollAccumulator < intervalSeconds)
            {
                return;
            }

            _pollAccumulator = 0f;
            Poll();
        }

        private void Poll()
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld is null)
            {
                return;
            }

            _aliveScratch.Clear();
            foreach (var player in gameWorld.AllAlivePlayersList)
            {
                _aliveScratch.Add(player.ProfileId);
            }

            // copy keys first - RemoveEntry mutates _entries mid-iteration.
            _trackedKeysScratch.Clear();
            _trackedKeysScratch.AddRange(_entries.Keys);
            foreach (var profileId in _trackedKeysScratch)
            {
                var entry = _entries[profileId];
                if (!entry.State.IsAlive)
                {
                    // frozen onto its corpse - it doesn't move, so nothing left to refresh.
                    continue;
                }

                var player = entry.Player;
                bool isAlive;
                try
                {
                    isAlive = player.HealthController.IsAlive;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Poll: IsAlive check threw for a tracked player, dropping: {e}");
                    RemoveEntry(profileId);
                    continue;
                }

                try
                {
                    if (!isAlive)
                    {
                        LockAsCorpse(entry);
                    }
                    else if (!_aliveScratch.Contains(profileId))
                    {
                        // still alive but no longer in the world's tracked list - extracted or
                        // disconnected, nothing left to show.
                        RemoveEntry(profileId);
                    }
                    else
                    {
                        UpdateLiveState(player, entry.State);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Poll: state refresh threw for a tracked player, dropping: {e}");
                    RemoveEntry(profileId);
                }
            }
        }

        private static void UpdateLiveState(Player player, TrackedPlayerState state)
        {
            var worldPos = player.Cast<IPlayer>().Position;
            state.WorldPosition = worldPos;
            state.Position = MathUtils.ConvertToMapPosition(worldPos);

            var forward = player.transform.forward;
            state.Facing = new Vector2(forward.x, forward.z);
        }

        private static void LockAsCorpse(TrackedEntry entry)
        {
            var player = entry.Player;
            var state = entry.State;
            UpdateLiveState(player, state); // capture the final position at time of death detection
            state.Facing = null;
            state.CorpseColor = GetCorpseColor(player);
            state.IsAlive = false;
            entry.Player = null;
        }

        private void TryAddMarker(IPlayer iPlayer)
        {
            var player = iPlayer.TryCast<Player>();
            if (player is null || player.IsYourPlayer || player.IsHeadlessClient() || _entries.ContainsKey(player.ProfileId))
            {
                return;
            }

            // side/allegiance doesn't change mid-raid, so category and the "alive" look are fixed
            // here at spawn; the corpse look (once dead) is resolved once in GetCorpseColor at the
            // poll that first notices the death, since kill-attribution (LastAggressor) is only
            // meaningful after death.
            string category;
            string aliveImagePath;
            Color aliveColor;
            string text = player.Profile?.Info?.Nickname;

            // the BTR's turret gunner is an invincible bot Player - shown as the vehicle itself
            // rather than as an enemy, reusing this class's existing position/facing tracking
            // instead of the old separate BTRMarkerProvider (which read BTRView.transform directly
            // and showed a visibly wrong facing).
            if (player.IsBTRShooter())
            {
                category = BtrCategory;
                aliveImagePath = BtrImagePath;
                aliveColor = BtrColor;
                text = "BTR";
            }
            else if (player.IsGroupedWithMainPlayer())
            {
                category = FriendlyCategory;
                aliveImagePath = ArrowImagePath;
                aliveColor = FriendlyColor;
            }
            else if (player.IsTrackedBoss())
            {
                if (!Settings.ShowBoss.Value)
                {
                    return;
                }

                category = BossCategory;
                aliveImagePath = StarImagePath;
                aliveColor = BossColor;
            }
            else if (player.IsPMC())
            {
                if (!Settings.ShowPmc.Value)
                {
                    return;
                }

                category = EnemyCategory;
                aliveImagePath = ArrowImagePath;
                // AI PMC bots report Side Savage (same as scavs) and are only distinguishable as
                // Bear vs Usec via Role - see PlayerRules.IsPMC.
                var isBear = player.Side == EPlayerSide.Bear
                    || player.Profile.Info.Settings.Role == WildSpawnType.pmcBEAR;
                aliveColor = isBear ? PmcBearColor : PmcUsecColor;
            }
            else if (player.IsScav())
            {
                if (!Settings.ShowScav.Value)
                {
                    return;
                }

                category = ScavCategory;
                aliveImagePath = ArrowImagePath;
                aliveColor = ScavColor;
            }
            else
            {
                return;
            }

            var state = new TrackedPlayerState();
            try
            {
                UpdateLiveState(player, state);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"TryAddMarker: initial state read threw, skipping: {e}");
                return;
            }

            var marker = new MapMarker
            {
                Category = category,
                Text = text,
                ShowLabel = true,
                GetPosition = () => state.Position,
                GetWorldPosition = () => state.WorldPosition,
                GetFacing = () => state.Facing,
                GetImagePath = () => state.IsAlive ? aliveImagePath : SkullImagePath,
                GetColor = () => state.IsAlive ? aliveColor : state.CorpseColor,
            };

            _entries[player.ProfileId] = new TrackedEntry { Player = player, Marker = marker, State = state };
            MarkerManager.Add(marker);
        }

        private static Color GetCorpseColor(Player player)
        {
            var killedByMainOrTeammate = player.DidMainPlayerKill() || player.DidTeammateKill();

            if (player.IsGroupedWithMainPlayer())
            {
                return killedByMainOrTeammate ? FriendlyKilledCorpseColor : FriendlyCorpseColor;
            }

            if (player.IsTrackedBoss() || killedByMainOrTeammate)
            {
                return killedByMainOrTeammate ? KilledCorpseColor : BossColor;
            }

            return OtherCorpseColor;
        }

        private void RemoveEntry(string profileId)
        {
            if (!_entries.TryGetValue(profileId, out var entry))
            {
                return;
            }

            MarkerManager.Remove(entry.Marker);
            _entries.Remove(profileId);
        }
    }
}
