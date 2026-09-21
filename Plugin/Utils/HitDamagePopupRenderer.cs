using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using SPTMap.Config;
using UnityEngine;

namespace SPTMap.Utils
{
    // Floating damage numbers over an enemy's head whenever their HP drops, from any source (not
    // just the local player - simpler and avoids needing an attacker check at all). Polls each
    // alive enemy's summed body-part HP every frame and diffs against the last poll instead of
    // subscribing to ActiveHealthController.ApplyDamageEvent: that event's signature carries a
    // DamageInfo struct, which is non-blittable, and Il2CppInterop's DelegateSupport.ConvertDelegate
    // cannot build a native trampoline for a non-blittable struct parameter - every subscribe
    // attempt threw, and since the throw happened every frame for the same not-yet-subscribed
    // player forever, that repeated failing native interop call is suspected to have corrupted the
    // Il2Cpp runtime state badly enough to cause an unrelated fatal AccessViolationException
    // elsewhere shortly after (see git history). A frame-diff can merge multiple simultaneous hits
    // (e.g. a shotgun's pellets) into one popup - considered an acceptable tradeoff for not
    // crashing the game.
    public static class HitDamagePopupRenderer
    {
        private const float PopupLifetimeSeconds = 1.2f;
        private const float RiseSpeed = 40f; // GUI pixels/sec
        private const int FontSize = 28;

        // ignores HP regen/tick noise (e.g. slow blood-loss recovery from a heal item) rounding
        // into a "damage" the same size as a graze - keeps the popup meaningful.
        private const float MinDamageThreshold = 1f;

        private static readonly EBodyPart[] TrackedBodyParts =
        {
            EBodyPart.Head, EBodyPart.Chest, EBodyPart.Stomach,
            EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg, EBodyPart.RightLeg,
        };

        private struct Popup
        {
            public Vector3 WorldPos;
            public float Damage;
            public float SpawnTime;
        }

        private static readonly List<Popup> _popups = new();

        // keyed by ProfileId (stable string), not the Il2Cpp Player reference - see
        // UnitMarkerProvider for why a Player-keyed dictionary is unreliable across
        // different native list reads.
        private static readonly Dictionary<string, float> _lastTotalHealth = new();

        private static GUIStyle _style;
        private static GUIStyle Style => _style ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = FontSize,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };

        // called every frame from SPTMapController.Update while in a raid.
        public static void Tick()
        {
            // Pruned here, not just in Draw() - Draw() is only called while the full map isn't
            // open (see SPTMapController.OnGUI's peeking check), but Tick() (and thus _popups.Add)
            // keeps running regardless. Without this, holding M during a firefight let _popups
            // grow unbounded for as long as the map stayed open, since nothing was left removing
            // expired entries.
            var now = Time.time;
            for (var i = _popups.Count - 1; i >= 0; i--)
            {
                if (now - _popups[i].SpawnTime >= PopupLifetimeSeconds)
                {
                    _popups.RemoveAt(i);
                }
            }

            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
            {
                return;
            }

            foreach (var player in gameWorld.AllAlivePlayersList)
            {
                if (player == null || player.IsYourPlayer)
                {
                    continue;
                }

                try
                {
                    PollPlayer(player);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"HitDamagePopupRenderer: poll threw for a player, skipping: {e}");
                }
            }
        }

        private static void PollPlayer(Player player)
        {
            var health = player.HealthController;
            if (health == null)
            {
                return;
            }

            var total = 0f;
            foreach (var part in TrackedBodyParts)
            {
                total += health.GetBodyPartHealth(part, false).Current;
            }

            var profileId = player.ProfileId;
            if (_lastTotalHealth.TryGetValue(profileId, out var lastTotal))
            {
                var damage = lastTotal - total;
                if (damage >= MinDamageThreshold)
                {
                    SpawnPopup(player, damage);
                }
            }

            _lastTotalHealth[profileId] = total;
        }

        private static void SpawnPopup(Player victim, float damage)
        {
            var mainParts = victim.MainParts;
            var worldPos = mainParts != null && mainParts.ContainsKey(BodyPartType.head)
                ? mainParts[BodyPartType.head].Position
                : victim.transform.position;

            _popups.Add(new Popup
            {
                WorldPos = worldPos,
                Damage = damage,
                SpawnTime = Time.time,
            });
        }

        public static void Draw()
        {
            if (!Settings.ShowHitDamageNumbers.Value || _popups.Count == 0)
            {
                return;
            }

            var camera = EnemyEspRenderer.ResolveActiveCamera();
            if (camera == null)
            {
                return;
            }

            var now = Time.time;
            for (var i = _popups.Count - 1; i >= 0; i--)
            {
                var popup = _popups[i];
                var age = now - popup.SpawnTime;
                if (age >= PopupLifetimeSeconds)
                {
                    _popups.RemoveAt(i);
                    continue;
                }

                if (!EnemyEspRenderer.TryWorldToGui(camera, popup.WorldPos, out var guiPoint))
                {
                    continue;
                }

                guiPoint.y -= age * RiseSpeed;

                var alpha = 1f - age / PopupLifetimeSeconds;
                var prevColor = GUI.color;
                GUI.color = new Color(1f, 0.25f, 0.15f, alpha);
                GUI.Label(new Rect(guiPoint.x - 40f, guiPoint.y - 40f, 80f, 40f), Mathf.CeilToInt(popup.Damage).ToString(), Style);
                GUI.color = prevColor;
            }
        }

        public static void OnRaidEnd()
        {
            _lastTotalHealth.Clear();
            _popups.Clear();
        }
    }
}
