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

        // same rationale as AiInfoState/BodyPartHealthState below: avoids a fresh string per
        // visible enemy on every OnGUI pass when the underlying int hasn't changed since last
        // frame (chest HP only changes on a hit; rounded distance often holds steady too).
        private struct HpTextCache
        {
            public int Current, Max;
            public string Text;
            public bool Initialized;
        }

        private struct DistanceTextCache
        {
            public int RoundedDistance;
            public string Text;
            public bool Initialized;
        }

        private static readonly Dictionary<string, HpTextCache> _hpTextCache = new();
        private static readonly Dictionary<string, DistanceTextCache> _distanceTextCache = new();

        private static string GetCachedHpText(string profileId, int current, int max)
        {
            _hpTextCache.TryGetValue(profileId, out var cached);
            if (!cached.Initialized || cached.Current != current || cached.Max != max)
            {
                cached.Current = current;
                cached.Max = max;
                cached.Text = $"{current}/{max}";
                cached.Initialized = true;
                _hpTextCache[profileId] = cached;
            }

            return cached.Text;
        }

        private static string GetCachedDistanceText(string profileId, int roundedDistance)
        {
            _distanceTextCache.TryGetValue(profileId, out var cached);
            if (!cached.Initialized || cached.RoundedDistance != roundedDistance)
            {
                cached.RoundedDistance = roundedDistance;
                cached.Text = $"{roundedDistance}m";
                cached.Initialized = true;
                _distanceTextCache[profileId] = cached;
            }

            return cached.Text;
        }

        // clears every per-bot text cache above - stale ProfileId entries would otherwise sit
        // in these dictionaries for the rest of the session across raids.
        public static void OnRaidEnd()
        {
            _hpTextCache.Clear();
            _distanceTextCache.Clear();
            _aiInfoCache.Clear();
            _bodyPartHealthCache.Clear();
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

            var profileId = player.ProfileId;

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
                DrawAiInfo(player, profileId, boxRect, color);
            }

            if (Settings.ShowBodyPartHealth.Value)
            {
                DrawBodyPartHealthBreakdown(player, profileId, boxRect, color);
            }
            else
            {
                var chest = player.HealthController.GetBodyPartHealth(EBodyPart.Chest, false);
                var hpText = GetCachedHpText(profileId, Mathf.CeilToInt(chest.Current), Mathf.CeilToInt(chest.Maximum));
                var distanceText = GetCachedDistanceText(profileId, Mathf.RoundToInt(distance));

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

        // rebuilding this every OnGUI pass (Layout + Repaint, so twice per frame) for every
        // visible bot allocated a fresh List<string> + string.Join result even though the three
        // components rarely change frame-to-frame (difficulty is fixed for the raid, status/layer
        // only flip on AI state transitions) - a steady per-frame GC source contributing to the
        // camera/walk-bob micro-stutter reported 2026-09-17. Cached per bot (keyed by ProfileId),
        // rebuilt only when a component's string actually differs from last frame.
        private struct AiInfoState
        {
            public string Difficulty;
            public string Status;
            public string Layer;
            public string Text;
        }

        private static readonly Dictionary<string, AiInfoState> _aiInfoCache = new();

        // real players have no AIData.BotOwner - this is a no-op for them, only bots carry the
        // difficulty/behavior state this reads. BotOwner itself is a MonoBehaviour
        // (UnityEngine.Object-derived), so it gets the explicit `== null` treatment rather than
        // `?.` (see CLAUDE.md/memory) - everything hanging off it here (BotMemory, BotSettings,
        // EnemyInfo, StandartBotBrain) is a plain Il2CppSystem.Object though, so `?.` is fine on
        // those.
        private static void DrawAiInfo(Player player, string profileId, Rect boxRect, Color color)
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
            var difficultyStr = difficulty.HasValue ? difficulty.Value.ToString() : null;
            var status = ResolveAiStatus(bot);
            var layer = bot.Brain?.ActiveLayerName();

            if (string.IsNullOrEmpty(difficultyStr) && string.IsNullOrEmpty(status) && string.IsNullOrEmpty(layer))
            {
                return;
            }

            _aiInfoCache.TryGetValue(profileId, out var cached);
            if (cached.Text == null || cached.Difficulty != difficultyStr || cached.Status != status || cached.Layer != layer)
            {
                var parts = new List<string>(3);
                if (!string.IsNullOrEmpty(difficultyStr)) parts.Add(difficultyStr);
                if (!string.IsNullOrEmpty(status)) parts.Add(status);
                if (!string.IsNullOrEmpty(layer)) parts.Add(layer);

                cached.Difficulty = difficultyStr;
                cached.Status = status;
                cached.Layer = layer;
                cached.Text = string.Join(" / ", parts);
                _aiInfoCache[profileId] = cached;
            }

            var prevColor = GUI.color;
            GUI.color = color;
            GUI.Label(new Rect(boxRect.x + boxRect.width / 2f - AiInfoWidth / 2f, boxRect.yMax + 2f, AiInfoWidth, AiInfoHeight), cached.Text, HpLabelStyle);
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
        // one cached int + string per tracked body part per bot - .ToString() (and the closure
        // that used to wrap health) only reruns when that part's HP actually changed since last
        // frame, instead of every OnGUI pass. See AiInfoState comment above for why this matters.
        private struct BodyPartHealthState
        {
            public int Head, Chest, Stomach, LeftArm, RightArm, LeftLeg, RightLeg;
            public string HeadText, ChestText, StomachText, LeftArmText, RightArmText, LeftLegText, RightLegText;
            public bool Initialized;
        }

        private static readonly Dictionary<string, BodyPartHealthState> _bodyPartHealthCache = new();

        private static void DrawBodyPartHealthBreakdown(Player player, string profileId, Rect boxRect, Color color)
        {
            var health = player.HealthController;

            _bodyPartHealthCache.TryGetValue(profileId, out var cached);

            int Update(EBodyPart part, ref int lastValue, ref string lastText)
            {
                var value = Mathf.CeilToInt(health.GetBodyPartHealth(part, false).Current);
                if (!cached.Initialized || value != lastValue)
                {
                    lastValue = value;
                    lastText = value.ToString();
                }

                return value;
            }

            Update(EBodyPart.Head, ref cached.Head, ref cached.HeadText);
            Update(EBodyPart.Chest, ref cached.Chest, ref cached.ChestText);
            Update(EBodyPart.Stomach, ref cached.Stomach, ref cached.StomachText);
            Update(EBodyPart.LeftArm, ref cached.LeftArm, ref cached.LeftArmText);
            Update(EBodyPart.RightArm, ref cached.RightArm, ref cached.RightArmText);
            Update(EBodyPart.LeftLeg, ref cached.LeftLeg, ref cached.LeftLegText);
            Update(EBodyPart.RightLeg, ref cached.RightLeg, ref cached.RightLegText);
            cached.Initialized = true;
            _bodyPartHealthCache[profileId] = cached;

            var centerX = boxRect.x + boxRect.width / 2f;

            var prevColor = GUI.color;
            GUI.color = color;

            GUI.Label(new Rect(centerX - PartLabelWidth / 2f, boxRect.y - PartLabelHeight - 2f, PartLabelWidth, PartLabelHeight), cached.HeadText, HpLabelStyle);
            GUI.Label(new Rect(centerX - PartLabelWidth / 2f, boxRect.y + boxRect.height * 0.2f, PartLabelWidth, PartLabelHeight), cached.ChestText, HpLabelStyle);
            GUI.Label(new Rect(centerX - PartLabelWidth / 2f, boxRect.yMax - boxRect.height * 0.2f - PartLabelHeight, PartLabelWidth, PartLabelHeight), cached.StomachText, HpLabelStyle);
            GUI.Label(new Rect(boxRect.x - PartLabelWidth - SideLabelOffset, boxRect.y + boxRect.height * 0.3f, PartLabelWidth, PartLabelHeight), cached.LeftArmText, HpLabelStyle);
            GUI.Label(new Rect(boxRect.xMax + SideLabelOffset, boxRect.y + boxRect.height * 0.3f, PartLabelWidth, PartLabelHeight), cached.RightArmText, HpLabelStyle);
            GUI.Label(new Rect(boxRect.x - PartLabelWidth - SideLabelOffset, boxRect.yMax - PartLabelHeight, PartLabelWidth, PartLabelHeight), cached.LeftLegText, HpLabelStyle);
            GUI.Label(new Rect(boxRect.xMax + SideLabelOffset, boxRect.yMax - PartLabelHeight, PartLabelWidth, PartLabelHeight), cached.RightLegText, HpLabelStyle);

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
