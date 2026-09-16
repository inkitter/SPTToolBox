using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using SPTMap.Config;
using UnityEngine;

namespace SPTMap.Utils
{
    // First-person overlay: draws a screen-space box + chest HP over every hostile player within
    // Settings.EspDistance, regardless of line of sight (positions come straight from the Player
    // object, not a Physics query, so walls don't matter). Off entirely when EspDistance is 0.
    public static class EnemyEspRenderer
    {
        // extra padding above the head part / below the leg parts so the box doesn't hug the
        // silhouette too tightly.
        private const float VerticalPad = 0.12f;
        private const float HalfWidth = 0.32f;
        private const float LabelWidth = 90f;
        private const float ChestToFeetFallback = 1.05f;

        private static GUIStyle _hpLabelStyle;
        private static GUIStyle HpLabelStyle => _hpLabelStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            alignment = TextAnchor.UpperCenter,
            wordWrap = false,
        };

        public static void Draw()
        {
            var maxDistance = Settings.EspDistance.Value;
            if (maxDistance <= 0f)
            {
                return;
            }

            // Player._camera (the game's own per-player field) turned out to not reliably get
            // populated for MainPlayer in practice - it stayed null for an entire raid in testing,
            // permanently blanking the overlay. Camera.main is also unreliable (EFT tags several
            // objects MainCamera - scope optics, spectator, etc). Instead, resolve the screen
            // camera ourselves: the enabled, non-render-texture camera with the highest depth is
            // the one actually drawing to the screen, regardless of what the game's own player
            // state thinks the "current" camera is.
            var camera = ResolveActiveCamera();
            var gameWorld = Singleton<GameWorld>.Instance;
            if (camera == null || gameWorld == null)
            {
                LogUnavailableOnce($"camera or gameWorld null (camera: {camera != null}, gameWorld: {gameWorld != null})");
                return;
            }

            var drawn = 0;
            var skippedNoBodyParts = 0;
            foreach (var player in gameWorld.AllAlivePlayersList)
            {
                try
                {
                    if (DrawIfEligible(player, camera, maxDistance))
                    {
                        drawn++;
                    }
                    else if (player != null && !player.IsYourPlayer && player.MainParts?.ContainsKey(BodyPartType.head) != true)
                    {
                        skippedNoBodyParts++;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"EnemyEspRenderer: draw threw for a player, skipping: {e}");
                }
            }

            if (drawn == 0 && skippedNoBodyParts > 0)
            {
                LogUnavailableOnce($"{skippedNoBodyParts} enemy player(s) had no MainParts[head] yet (not perceived/registered by hit system?)");
            }
        }

        // shared with HitDamagePopupRenderer, which needs the same "which camera is actually
        // drawing the screen" resolution to project damage-number popups. Cached across both
        // callers - Camera.allCameras allocates a fresh array of every scene camera on every call,
        // and without caching this ran twice per frame (once from each renderer) for the whole
        // raid, a steady GC source that was a measurable contributor to periodic frame drops. The
        // active screen camera essentially never changes frame-to-frame outside a scene transition,
        // so a short re-resolve interval is plenty; an explicit `!= null` (not `?.`/`??` - see
        // CLAUDE.md/memory) also forces an immediate re-resolve if the cached camera gets destroyed
        // (Unity fake-null) before the interval is up.
        private const float CacheDurationSeconds = 1f;
        private static Camera _cachedCamera;
        private static float _cacheExpireTime;

        public static Camera ResolveActiveCamera()
        {
            if (_cachedCamera != null && Time.time < _cacheExpireTime)
            {
                return _cachedCamera;
            }

            Camera best = null;
            var cameras = Camera.allCameras;
            foreach (var cam in cameras)
            {
                if (cam == null || !cam.enabled || cam.targetTexture != null)
                {
                    continue;
                }

                if (best == null || cam.depth > best.depth)
                {
                    best = cam;
                }
            }

            _cachedCamera = best;
            _cacheExpireTime = Time.time + CacheDurationSeconds;
            return best;
        }

        // shared with HitDamagePopupRenderer - see the normalization comment on its call site
        // below for why this isn't a plain GUIUtility.ScreenToGUIPoint call.
        public static bool TryWorldToGui(Camera camera, Vector3 worldPos, out Vector2 guiPoint)
        {
            var screen = camera.WorldToScreenPoint(worldPos);
            if (screen.z <= 0f)
            {
                guiPoint = default;
                return false;
            }

            var normalizedX = screen.x / camera.pixelWidth;
            var normalizedY = screen.y / camera.pixelHeight;
            guiPoint = new Vector2(normalizedX * Screen.width, (1f - normalizedY) * Screen.height);
            return true;
        }

        private static string _lastUnavailableReason;

        private static void LogUnavailableOnce(string reason)
        {
            if (reason == _lastUnavailableReason)
            {
                return;
            }

            _lastUnavailableReason = reason;
            Plugin.Log.LogWarning($"EnemyEspRenderer: nothing drawn - {reason}");
        }

        private static bool DrawIfEligible(Player player, Camera camera, float maxDistance)
        {
            if (player == null || player.IsYourPlayer || player.IsHeadlessClient() || player.IsBTRShooter())
            {
                return false;
            }

            if (!player.HealthController.IsAlive || player.IsGroupedWithMainPlayer())
            {
                return false;
            }

            bool isBoss = player.IsTrackedBoss();
            bool isPmc = player.IsPMC();
            bool isScav = player.IsScav();
            if (!isBoss && !isPmc && !isScav)
            {
                return false;
            }

            if (isBoss && !Settings.ShowBoss.Value) return false;
            if (!isBoss && isPmc && !Settings.ShowPmc.Value) return false;
            if (!isBoss && isScav && !Settings.ShowScav.Value) return false;

            // legs aren't always populated in MainParts (varies by bot/perception state) - only
            // head+body are load-bearing; fall back to a fixed offset below the chest for the feet
            // so a missing leg entry doesn't blank the whole box out.
            var mainParts = player.MainParts;
            if (mainParts == null || !mainParts.ContainsKey(BodyPartType.head) || !mainParts.ContainsKey(BodyPartType.body))
            {
                return false;
            }

            var headPos = mainParts[BodyPartType.head].Position;
            var chestPos = mainParts[BodyPartType.body].Position;

            Vector3 feetPos;
            if (mainParts.ContainsKey(BodyPartType.leftLeg) && mainParts.ContainsKey(BodyPartType.rightLeg))
            {
                feetPos = Vector3.Min(mainParts[BodyPartType.leftLeg].Position, mainParts[BodyPartType.rightLeg].Position);
            }
            else
            {
                feetPos = chestPos - Vector3.up * ChestToFeetFallback;
            }

            var distance = Vector3.Distance(camera.transform.position, chestPos);
            if (distance > maxDistance)
            {
                return false;
            }

            var top = headPos + Vector3.up * VerticalPad;
            var bottom = new Vector3(feetPos.x, feetPos.y - VerticalPad, feetPos.z);
            var right = camera.transform.right * HalfWidth;

            Span<Vector3> corners = stackalloc Vector3[4]
            {
                bottom - right,
                bottom + right,
                top - right,
                top + right,
            };

            var minX = float.MaxValue;
            var maxX = float.MinValue;
            var minY = float.MaxValue;
            var maxY = float.MinValue;

            foreach (var corner in corners)
            {
                // behind the camera - the projected xy would be meaningless (mirrored) - bail the
                // whole box rather than draw a garbage rect. See TryWorldToGui for why this isn't
                // a plain GUIUtility.ScreenToGUIPoint call.
                if (!TryWorldToGui(camera, corner, out var guiPoint))
                {
                    return false;
                }

                if (guiPoint.x < minX) minX = guiPoint.x;
                if (guiPoint.x > maxX) maxX = guiPoint.x;
                if (guiPoint.y < minY) minY = guiPoint.y;
                if (guiPoint.y > maxY) maxY = guiPoint.y;
            }

            var color = isBoss ? new Color(0.6f, 0f, 0.8f)
                : isPmc ? Color.red
                : new Color(1f, 0.55f, 0f);

            var boxRect = new Rect(minX, minY, maxX - minX, maxY - minY);
            DrawBoxOutline(boxRect, color);

            if (Settings.ShowAiInfo.Value)
            {
                DrawAiInfo(player, boxRect, color);
            }

            if (Settings.ShowBodyPartHealth.Value)
            {
                DrawBodyPartHealthBreakdown(player, boxRect, color);
            }
            else
            {
                var chest = player.HealthController.GetBodyPartHealth(EBodyPart.Chest, false);
                var hpText = $"{Mathf.CeilToInt(chest.Current)}/{Mathf.CeilToInt(chest.Maximum)}";
                var distanceText = $"{distance:0}m";

                // the label is wider than most enemy boxes on screen (especially at range) - size
                // it independently of boxRect.width and center it on the box, rather than
                // cramming the text into the box's own (often narrower) width where it wraps.
                var centerX = boxRect.x + boxRect.width / 2f;
                var labelX = centerX - LabelWidth / 2f;

                var prevColor = GUI.color;
                GUI.color = color;
                GUI.Label(new Rect(labelX, boxRect.y - 30f, LabelWidth, 16f), hpText, HpLabelStyle);
                GUI.Label(new Rect(labelX, boxRect.y - 16f, LabelWidth, 16f), distanceText, HpLabelStyle);
                GUI.color = prevColor;
            }

            return true;
        }

        private const float AiInfoWidth = 150f;
        private const float AiInfoHeight = 14f;

        // real players have no AIData.BotOwner - this is a no-op for them, only bots carry the
        // difficulty/behavior state this reads. BotOwner itself is a MonoBehaviour
        // (UnityEngine.Object-derived), so it gets the explicit `== null` treatment rather than
        // `?.` (see CLAUDE.md/memory) - everything hanging off it here (BotMemory, BotSettings,
        // EnemyInfo, StandartBotBrain) is a plain Il2CppSystem.Object though, so `?.` is fine on
        // those.
        private static void DrawAiInfo(Player player, Rect boxRect, Color color)
        {
            var aiData = player.AIData;
            if (aiData == null || !aiData.IsAI)
            {
                return;
            }

            var bot = aiData.BotOwner;
            if (bot == null)
            {
                return;
            }

            var difficulty = bot.Settings?._difficulty;
            var status = ResolveAiStatus(bot);
            var layer = bot.Brain?.ActiveLayerName();

            var parts = new List<string>(3);
            if (difficulty.HasValue) parts.Add(difficulty.Value.ToString());
            if (!string.IsNullOrEmpty(status)) parts.Add(status);
            if (!string.IsNullOrEmpty(layer)) parts.Add(layer);

            if (parts.Count == 0)
            {
                return;
            }

            var text = string.Join(" / ", parts);

            var prevColor = GUI.color;
            GUI.color = color;
            GUI.Label(new Rect(boxRect.x + boxRect.width / 2f - AiInfoWidth / 2f, boxRect.yMax + 2f, AiInfoWidth, AiInfoHeight), text, HpLabelStyle);
            GUI.color = prevColor;
        }

        private static string ResolveAiStatus(BotOwner bot)
        {
            var memory = bot.Memory;
            if (memory == null)
            {
                return null;
            }

            if (memory.IsPeace)
            {
                return "peaceful";
            }

            var mainPlayer = GameUtils.GetMainPlayer();
            var goalEnemy = memory.GoalEnemy;
            if (goalEnemy != null && mainPlayer != null && goalEnemy.Person != null && goalEnemy.Person.ProfileId == mainPlayer.ProfileId)
            {
                return goalEnemy.IsVisible ? "chasing you!" : "searching for you";
            }

            return memory.HaveEnemy ? "in combat" : "alert";
        }

        private const float PartLabelWidth = 34f;
        private const float PartLabelHeight = 14f;
        private const float SideLabelOffset = 2f;

        // Positions are box-relative fractions, not separate world-space projections per part -
        // EFT doesn't track a live world position for "stomach" (only a health value split off
        // the chest hit), and reusing the box we already computed from head/feet keeps every
        // label glued to the same silhouette instead of drifting independently. Matches the
        // requested layout: head above the box, chest upper-inside, stomach lower-inside, arms on
        // the left/right edges, legs at the bottom corners.
        private static void DrawBodyPartHealthBreakdown(Player player, Rect boxRect, Color color)
        {
            var health = player.HealthController;

            string Hp(EBodyPart part)
            {
                var hp = health.GetBodyPartHealth(part, false);
                return Mathf.CeilToInt(hp.Current).ToString();
            }

            var centerX = boxRect.x + boxRect.width / 2f;

            var prevColor = GUI.color;
            GUI.color = color;

            GUI.Label(new Rect(centerX - PartLabelWidth / 2f, boxRect.y - PartLabelHeight - 2f, PartLabelWidth, PartLabelHeight), Hp(EBodyPart.Head), HpLabelStyle);
            GUI.Label(new Rect(centerX - PartLabelWidth / 2f, boxRect.y + boxRect.height * 0.2f, PartLabelWidth, PartLabelHeight), Hp(EBodyPart.Chest), HpLabelStyle);
            GUI.Label(new Rect(centerX - PartLabelWidth / 2f, boxRect.yMax - boxRect.height * 0.2f - PartLabelHeight, PartLabelWidth, PartLabelHeight), Hp(EBodyPart.Stomach), HpLabelStyle);
            GUI.Label(new Rect(boxRect.x - PartLabelWidth - SideLabelOffset, boxRect.y + boxRect.height * 0.3f, PartLabelWidth, PartLabelHeight), Hp(EBodyPart.LeftArm), HpLabelStyle);
            GUI.Label(new Rect(boxRect.xMax + SideLabelOffset, boxRect.y + boxRect.height * 0.3f, PartLabelWidth, PartLabelHeight), Hp(EBodyPart.RightArm), HpLabelStyle);
            GUI.Label(new Rect(boxRect.x - PartLabelWidth - SideLabelOffset, boxRect.yMax - PartLabelHeight, PartLabelWidth, PartLabelHeight), Hp(EBodyPart.LeftLeg), HpLabelStyle);
            GUI.Label(new Rect(boxRect.xMax + SideLabelOffset, boxRect.yMax - PartLabelHeight, PartLabelWidth, PartLabelHeight), Hp(EBodyPart.RightLeg), HpLabelStyle);

            GUI.color = prevColor;
        }

        private const float LineThickness = 2f;

        private static void DrawBoxOutline(Rect rect, Color color)
        {
            var prevColor = GUI.color;
            GUI.color = color;

            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, LineThickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - LineThickness, rect.width, LineThickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, LineThickness, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMax - LineThickness, rect.y, LineThickness, rect.height), Texture2D.whiteTexture);

            GUI.color = prevColor;
        }
    }
}
