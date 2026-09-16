using System;
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

        private static Camera ResolveActiveCamera()
        {
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

            return best;
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
                var screen = camera.WorldToScreenPoint(corner);
                if (screen.z <= 0f)
                {
                    // behind the camera - the projected xy is meaningless (mirrored), bail entirely
                    // rather than draw a garbage box.
                    return false;
                }

                // WorldToScreenPoint returns coordinates in the camera's own render-pixel space
                // (camera.pixelWidth/pixelHeight), which is not guaranteed to equal Screen.width/
                // height - render-scale settings or OS DPI scaling can make them differ by a
                // constant factor. GUIUtility.ScreenToGUIPoint assumes they match, so under a
                // mismatch it silently produced a box scaled toward the top-left corner (e.g. at
                // exactly half size/position under a 2x pixel-vs-logical mismatch). Normalize by
                // the camera's own pixel dimensions first, then remap into logical Screen space,
                // so a mismatch between the two can't skew the result.
                var normalizedX = screen.x / camera.pixelWidth;
                var normalizedY = screen.y / camera.pixelHeight;
                var guiPoint = new Vector2(normalizedX * Screen.width, (1f - normalizedY) * Screen.height);
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

            var chest = player.HealthController.GetBodyPartHealth(EBodyPart.Chest, false);
            var hpText = $"{Mathf.CeilToInt(chest.Current)}/{Mathf.CeilToInt(chest.Maximum)}";
            var distanceText = $"{distance:0}m";

            // the label is wider than most enemy boxes on screen (especially at range) - size it
            // independently of boxRect.width and center it on the box, rather than cramming the
            // text into the box's own (often narrower) width where it wraps.
            var centerX = boxRect.x + boxRect.width / 2f;
            var labelX = centerX - LabelWidth / 2f;

            var prevColor = GUI.color;
            GUI.color = color;
            GUI.Label(new Rect(labelX, boxRect.y - 30f, LabelWidth, 16f), hpText, HpLabelStyle);
            GUI.Label(new Rect(labelX, boxRect.y - 16f, LabelWidth, 16f), distanceText, HpLabelStyle);
            GUI.color = prevColor;

            return true;
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
