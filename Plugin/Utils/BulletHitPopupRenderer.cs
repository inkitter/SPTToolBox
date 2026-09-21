using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using SPTMap.Config;
using UnityEngine;

namespace SPTMap.Utils
{
    // Alternative to HitDamagePopupRenderer: instead of polling every alive enemy's summed body
    // HP every frame (which calls into HealthController.GetBodyPartHealth even for a player who
    // may have died/been destroyed that same frame - the suspected cause of an intermittent
    // AccessViolationException there), this subscribes directly to each tracked player's
    // Player.OnDamageReceived event and only does any work exactly when a real hit lands.
    // OnDamageReceived's delegate signature is Action<float damage, EBodyPart, EDamageType,
    // float armorDamage, MaterialType> - all plain value types (floats/enums), which
    // Il2CppInterop's DelegateSupport.ConvertDelegate can build a native trampoline for without
    // throwing. This is unlike the older ActiveHealthController.ApplyDamageEvent (see
    // HitDamagePopupRenderer's header comment), whose DamageInfo struct parameter is not
    // blittable and made every subscribe attempt throw - the repeated failing native interop
    // call from that is suspected to have corrupted the Il2Cpp runtime state badly enough to
    // cause an unrelated fatal crash elsewhere. OnDamageReceived carries no such risk.
    //
    // Off by default via Settings.ShowHitDamageNumbersV2 and fully independent of
    // HitDamagePopupRenderer/Settings.ShowHitDamageNumbers - the two can be compared side by
    // side, and disabling this one alone is enough to stop it from running at all.
    public static class BulletHitPopupRenderer
    {
        private const float PopupLifetimeSeconds = 1.2f;
        private const float RiseSpeed = 40f; // GUI pixels/sec
        private const int FontSize = 28;
        private const float MinDamageThreshold = 1f;

        private struct Popup
        {
            public Vector3 WorldPos;
            public float Damage;
            public float SpawnTime;
        }

        private struct HitEvent
        {
            public string ProfileId;
            public EBodyPart BodyPart;
            public float Damage;
        }

        private static readonly List<Popup> _popups = new();

        // hit events are enqueued from inside the native damage callback (see Subscribe) and
        // drained on the next Tick - keeps the callback itself to plain value-type bookkeeping
        // only, no further Il2Cpp calls from a context we don't fully control the timing of.
        private static readonly Queue<HitEvent> _pendingHits = new();

        // keyed by ProfileId (stable string), not just held as a HashSet of Player refs - same
        // reasoning as HitDamagePopupRenderer/UnitMarkerProvider for why a Player-keyed
        // collection is unreliable across different native list reads.
        private static readonly Dictionary<string, Player> _subscribed = new();

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
                if (player == null || player.IsYourPlayer || _subscribed.ContainsKey(player.ProfileId))
                {
                    continue;
                }

                try
                {
                    Subscribe(player);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"BulletHitPopupRenderer: subscribe failed for a player, skipping: {e}");
                }
            }

            DrainPendingHits();
        }

        private static void Subscribe(Player player)
        {
            var profileId = player.ProfileId;

            // implicit conversion from a plain Action to Player.DamageDelegate (see
            // DelegateSupport.ConvertDelegate note above) only kicks in through a method-argument
            // or explicit-cast context, not via a bare `+=` on a lambda - hence the explicitly
            // typed local and add_OnDamageReceived call instead of `player.OnDamageReceived += ...`.
            Action<float, EBodyPart, EDamageType, float, MaterialType> handler =
                (damage, bodyPart, damageType, armorDamage, materialType) =>
                {
                    if (damage < MinDamageThreshold)
                    {
                        return;
                    }

                    _pendingHits.Enqueue(new HitEvent { ProfileId = profileId, BodyPart = bodyPart, Damage = damage });
                };

            player.add_OnDamageReceived(handler);
            _subscribed[profileId] = player;
        }

        private static void DrainPendingHits()
        {
            while (_pendingHits.Count > 0)
            {
                var hit = _pendingHits.Dequeue();

                if (!_subscribed.TryGetValue(hit.ProfileId, out var victim) || victim == null)
                {
                    continue;
                }

                try
                {
                    SpawnPopup(victim, hit.BodyPart, hit.Damage);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"BulletHitPopupRenderer: spawn failed for a hit, skipping: {e}");
                }
            }
        }

        private static void SpawnPopup(Player victim, EBodyPart bodyPart, float damage)
        {
            var mainParts = victim.MainParts;
            var partType = ToBodyPartType(bodyPart);
            var worldPos = mainParts != null && mainParts.ContainsKey(partType)
                ? mainParts[partType].Position
                : victim.transform.position;

            _popups.Add(new Popup
            {
                WorldPos = worldPos,
                Damage = damage,
                SpawnTime = Time.time,
            });
        }

        // BodyPartType (used to key Player.MainParts) only distinguishes head/left-right arm/
        // left-right leg - torso hits (chest/stomach/common) all land on the same "body" part.
        private static BodyPartType ToBodyPartType(EBodyPart bodyPart)
        {
            switch (bodyPart)
            {
                case EBodyPart.Head:
                    return BodyPartType.head;
                case EBodyPart.LeftArm:
                    return BodyPartType.leftArm;
                case EBodyPart.RightArm:
                    return BodyPartType.rightArm;
                case EBodyPart.LeftLeg:
                    return BodyPartType.leftLeg;
                case EBodyPart.RightLeg:
                    return BodyPartType.rightLeg;
                default:
                    return BodyPartType.body;
            }
        }

        public static void Draw()
        {
            if (!Settings.ShowHitDamageNumbersV2.Value || _popups.Count == 0)
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
                GUI.color = new Color(0.25f, 0.6f, 1f, alpha);
                GUI.Label(new Rect(guiPoint.x - 40f, guiPoint.y - 40f, 80f, 40f), Mathf.CeilToInt(popup.Damage).ToString(), Style);
                GUI.color = prevColor;
            }
        }

        public static void OnRaidEnd()
        {
            _subscribed.Clear();
            _pendingHits.Clear();
            _popups.Clear();
        }
    }
}
