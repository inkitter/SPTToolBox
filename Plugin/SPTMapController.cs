using System;
using System.Linq;
using EFT;
using SPTMap.Data;
using SPTMap.DynamicMarkers;
using SPTMap.Utils;
using SPTMapConfig = SPTMap.Config.Settings;
using UnityEngine;

namespace SPTMap
{
    // Everything SPTMapBehaviour used to do, minus being a MonoBehaviour itself. Il2CppInterop's
    // ClassInjector.RegisterTypeInIl2Cpp scans every method on a registered type to build its
    // IL2CPP-side method table, and logs a "Method unstripping failed"/"unsupported ... type"
    // warning (harmless, but noisy) for any method whose signature mentions a plain C# type
    // (MapDef, MapLevel, MapMarker, ...) that IL2CPP has no representation for. None of these
    // methods are ever called from the IL2CPP side - they're pure C#-to-C# helpers - so moving
    // them off the registered MonoBehaviour type entirely (onto this plain class instead) drops
    // every one of those warnings without changing any behavior. SPTMapBehaviour keeps only the
    // two real Unity messages (Update/OnGUI) it needs and forwards straight into here.
    public class SPTMapController
    {
        private const KeyCode PeekKey = KeyCode.M;

        // Calibration aid for the known map/world misalignment (see PROGRESS.md "Known issue"):
        // stand at a clearly-identifiable landmark, hit this key, and the raw + rotated map
        // position gets logged so two such readings can be solved into an affine Bounds fix
        // without needing new art.
        private const KeyCode LandmarkKey = KeyCode.KeypadPeriod;
        private static int MinimapWidth => SPTMapConfig.MiniMapWidth.Value;
        private const float PeekScreenFraction = 0.8f;

        private static Vector2 GetMinimapOrigin(float width, float height)
        {
            var paddingX = SPTMapConfig.PaddingX.Value;
            var paddingY = SPTMapConfig.PaddingY.Value;

            var x = SPTMapConfig.Anchor.Value switch
            {
                Config.MiniMapAnchor.TopLeft or Config.MiniMapAnchor.BottomLeft => paddingX,
                _ => Screen.width - width - paddingX,
            };

            var y = SPTMapConfig.Anchor.Value switch
            {
                Config.MiniMapAnchor.TopLeft or Config.MiniMapAnchor.TopRight => paddingY,
                _ => Screen.height - height - paddingY,
            };

            return new Vector2(x, y);
        }

        private const float MinZoom = 1f;
        private const float MaxZoom = 8f;
        private static float ZoomPerSecond => SPTMapConfig.ZoomSpeed.Value; // multiplicative rate

        // no map loaded yet outside a raid - fall back to this so there's something on screen to
        // validate the render path with. Later stages replace this with real current-map lookup.
        private const string FallbackMapInternalName = "Interchange";

        // extracts/other players/corpses/quest objectives - populate MarkerManager on raid
        // start/end. Constructed lazily (not as field initializers) - their constructors do
        // IL2Cpp delegate conversion (DelegateSupport.ConvertDelegate), and a field initializer
        // throwing here would run inside AddComponent<SPTMapBehaviour>() itself, i.e. inside the
        // CommonUIAwakePatch postfix, silently preventing the whole component (map included) from
        // ever getting attached. Deferring construction to first raid start keeps that blast
        // radius contained to the marker providers alone.
        private ExtractMarkerProvider _extractMarkerProvider;
        private DoorMarkerProvider _doorMarkerProvider;
        private OtherPlayersMarkerProvider _otherPlayersMarkerProvider;
        private QuestMarkerProvider _questMarkerProvider;
        private bool _wasInRaid;

        private float _zoom = MinZoom;

        // null = auto-detect from GameUtils.GetCurrentMapInternalName(); Keypad 9/6 cycle through
        // every known map def manually (for when auto-detect doesn't match, or just to browse
        // other maps), Keypad 3 drops back to auto-detect.
        private MapDef _manualDef;

        // null = auto-detect the current floor from player height (MapDef.Levels' HeightMin/Max);
        // Keypad 7/4 step up/down through a multi-level map's floors manually, Keypad 1 drops back
        // to auto-detect. Meaningless (ignored) for single-level maps.
        private int? _manualLevel;

        private bool _peekToggled;

        public void Update()
        {
            if (Input.GetKeyDown(PeekKey))
            {
                _peekToggled = !_peekToggled;

                if (_peekToggled)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
                else
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
            }

            var inRaid = GameUtils.IsInRaid();
            if (inRaid && !_wasInRaid)
            {
                OnRaidStart();
            }
            else if (!inRaid && _wasInRaid)
            {
                OnRaidEnd();

                // peek/cursor state is left alone here on purpose - forcing it back to
                // locked/hidden right as the player lands in the menu (where they need a visible
                // pointer) fought the menu's own cursor management and could leave the cursor
                // stuck after a few raids. M is now a purely manual toggle.
            }
            else if (inRaid)
            {
                // extracts/doors aren't necessarily populated yet the instant OnRaidStart first
                // runs (see ExtractMarkerProvider/DoorMarkerProvider) - keep retrying each frame;
                // it's a no-op once found.
                TryRetryExtractMarkers();
                TryRetryDoorMarkers();
                TryRetryQuestMarkers();
                TryTickQuestMarkers();
                TryTickOtherPlayers();
            }
            _wasInRaid = inRaid;

            if (Input.GetKeyDown(KeyCode.Keypad2))
            {
                _zoom = MinZoom;
            }
            else if (Input.GetKey(KeyCode.Keypad8))
            {
                _zoom = Mathf.Clamp(_zoom * Mathf.Pow(ZoomPerSecond, Time.deltaTime), MinZoom, MaxZoom);
            }
            else if (Input.GetKey(KeyCode.Keypad5))
            {
                _zoom = Mathf.Clamp(_zoom / Mathf.Pow(ZoomPerSecond, Time.deltaTime), MinZoom, MaxZoom);
            }

            if (Input.GetKeyDown(KeyCode.Keypad3))
            {
                _manualDef = null;
                _manualLevel = null;
            }
            else if (Input.GetKeyDown(KeyCode.Keypad9) || Input.GetKeyDown(KeyCode.Keypad6))
            {
                CycleManualMap(Input.GetKeyDown(KeyCode.Keypad9) ? 1 : -1);
                _manualLevel = null;
            }

            if (Input.GetKeyDown(KeyCode.Keypad1))
            {
                _manualLevel = null;
            }
            else if (Input.GetKeyDown(KeyCode.Keypad7) || Input.GetKeyDown(KeyCode.Keypad4))
            {
                ChangeLevel(Input.GetKeyDown(KeyCode.Keypad7) ? 1 : -1);
            }

            if (Input.GetKeyDown(LandmarkKey))
            {
                LogLandmark();
            }

            QuestDebugPanel.HandleInput();
        }

        private void OnRaidStart()
        {
            MarkerManager.Clear();
            try
            {
                _otherPlayersMarkerProvider ??= new OtherPlayersMarkerProvider();
                _otherPlayersMarkerProvider.OnRaidStart();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidStart marker setup exception: {e}");
            }

            try
            {
                _extractMarkerProvider ??= new ExtractMarkerProvider();
                _extractMarkerProvider.OnRaidStart();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidStart extract marker setup exception: {e}");
            }

            try
            {
                _doorMarkerProvider ??= new DoorMarkerProvider();
                _doorMarkerProvider.OnRaidStart();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidStart door marker setup exception: {e}");
            }

            try
            {
                _questMarkerProvider ??= new QuestMarkerProvider();
                _questMarkerProvider.OnRaidStart();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidStart quest marker setup exception: {e}");
            }
        }

        private void TryRetryExtractMarkers()
        {
            try
            {
                _extractMarkerProvider?.OnRaidStart();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Extract marker retry exception: {e}");
            }
        }

        private void TryRetryDoorMarkers()
        {
            try
            {
                _doorMarkerProvider?.OnRaidStart();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Door marker retry exception: {e}");
            }
        }

        private void TryRetryQuestMarkers()
        {
            try
            {
                _questMarkerProvider?.OnRaidStart();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Quest marker retry exception: {e}");
            }
        }

        private void TryTickQuestMarkers()
        {
            try
            {
                _questMarkerProvider?.Tick(Time.deltaTime);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Quest marker refresh exception: {e}");
            }
        }

        private void TryTickOtherPlayers()
        {
            try
            {
                _otherPlayersMarkerProvider?.Tick(Time.deltaTime);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Other-players marker poll exception: {e}");
            }
        }

        private void OnRaidEnd()
        {
            try
            {
                _otherPlayersMarkerProvider?.OnRaidEnd();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidEnd marker teardown exception: {e}");
            }

            try
            {
                _extractMarkerProvider?.OnRaidEnd();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidEnd extract marker teardown exception: {e}");
            }

            try
            {
                _doorMarkerProvider?.OnRaidEnd();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidEnd door marker teardown exception: {e}");
            }

            try
            {
                _questMarkerProvider?.OnRaidEnd();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidEnd quest marker teardown exception: {e}");
            }

            MarkerManager.Clear();
        }

        // resolves the same "which MapDef is active" logic OnGUI uses, without the display-string
        // bookkeeping - shared so Keypad 7/4 level-stepping agrees with what's actually on screen.
        private MapDef ResolveActiveDef()
        {
            if (_manualDef != null) return _manualDef;

            var internalName = GameUtils.GetCurrentMapInternalName() ?? FallbackMapInternalName;
            var (def, _) = MapUtils.GetForInternalName(internalName);
            if (def != null) return def;

            var all = MapUtils.GetAllDefs();
            return all.Count > 0 ? all[0] : null;
        }

        private MapLevel ResolveActiveLevel(MapDef def)
        {
            if (def == null || !def.HasLevels) return null;

            if (_manualLevel.HasValue)
            {
                return def.Levels.FirstOrDefault(l => l.Level == _manualLevel.Value)
                       ?? def.Levels.OrderBy(l => l.Level).First();
            }

            var player = GameUtils.GetMainPlayer();
            if (player != null)
            {
                var best = ResolveLevelForPosition(def, player.Cast<IPlayer>().Position);
                if (best != null) return best;
            }

            return def.Levels.FirstOrDefault(l => l.Level == def.DefaultLevel)
                   ?? def.Levels.OrderBy(l => l.Level).First();
        }

        // ported from the old project's MapView.FindMatchingLayerByCoordinate: match a raw
        // (unrotated) world position against each level's GameBounds boxes, and if more than one
        // box contains it (e.g. a small room box nested inside a whole-map box), prefer the
        // smallest one as the more specific match. Shared between resolving the player's own
        // floor and resolving which floor a given marker belongs to.
        private static MapLevel ResolveLevelForPosition(MapDef def, Vector3 pos)
        {
            MapLevel best = null;
            var bestVolume = float.MaxValue;
            foreach (var level in def.Levels)
            {
                foreach (var box in level.GameBounds)
                {
                    if (pos.x < box.Min.x || pos.x > box.Max.x) continue;
                    if (pos.y < box.Min.y || pos.y > box.Max.y) continue;
                    if (pos.z < box.Min.z || pos.z > box.Max.z) continue;

                    var size = box.Max - box.Min;
                    var volume = size.x * size.y * size.z;
                    if (volume < bestVolume)
                    {
                        bestVolume = volume;
                        best = level;
                    }
                }
            }

            return best;
        }

        private void ChangeLevel(int delta)
        {
            var def = ResolveActiveDef();
            if (def == null || !def.HasLevels) return;

            var levels = def.Levels.OrderBy(l => l.Level).ToList();
            var current = ResolveActiveLevel(def);
            var index = current != null ? levels.IndexOf(current) : 0;
            index = Mathf.Clamp(index + delta, 0, levels.Count - 1);
            _manualLevel = levels[index].Level;
        }

        private void LogLandmark()
        {
            var player = GameUtils.GetMainPlayer();
            if (player == null)
            {
                Plugin.Log.LogInfo("[landmark] no player (not in a raid?)");
                return;
            }

            var internalName = GameUtils.GetCurrentMapInternalName() ?? FallbackMapInternalName;
            var (def, _) = MapUtils.GetForInternalName(internalName);
            var rawMapPos = MathUtils.ConvertToMapPosition(player.Cast<IPlayer>().Position);
            var rotatedPos = def != null ? MathUtils.Rotate90Multiple(rawMapPos, def.CoordinateRotation) : rawMapPos;

            Plugin.Log.LogInfo(
                $"[landmark] map='{internalName}' def={(def != null ? def.DisplayName : "NONE")} "
                + $"raw=({rawMapPos.x:0.###}, {rawMapPos.y:0.###}) rotated=({rotatedPos.x:0.###}, {rotatedPos.y:0.###})");
        }

        private void CycleManualMap(int delta)
        {
            var all = MapUtils.GetAllDefs();
            if (all.Count == 0) return;

            var index = _manualDef != null ? all.IndexOf(_manualDef) : -1;
            if (index < 0)
            {
                var autoName = GameUtils.GetCurrentMapInternalName() ?? FallbackMapInternalName;
                var (autoDef, _) = MapUtils.GetForInternalName(autoName);
                index = autoDef != null ? all.IndexOf(autoDef) : 0;
            }

            index = ((index + delta) % all.Count + all.Count) % all.Count;
            _manualDef = all[index];
        }

        public void OnGUI()
        {
            try
            {
                var peeking = _peekToggled;
                var inRaid = GameUtils.IsInRaid();

                QuestDebugPanel.Draw();

                // outside a raid and not peeking, there's nothing worth showing permanently -
                // just prove the plugin's alive. In raid, the minimap is always up (small, corner);
                // holding M enlarges it to most of the screen for a full browse instead of
                // replacing a hidden map, matching how the peek key is expected to behave.
                if (!peeking && !inRaid)
                {
                    var labelOrigin = GetMinimapOrigin(MinimapWidth, 20);
                    var labelRect = new Rect(labelOrigin.x, labelOrigin.y, MinimapWidth, 20);
                    var color = GUI.color;
                    GUI.color = new Color(1f, 1f, 1f, 0.5f);
                    GUI.Label(labelRect, $"SPTMap Loaded (built for {Plugin.BuiltForVersion})");
                    GUI.color = color;
                    return;
                }

                // only meaningful over the normal first-person view - holding M already covers the
                // screen with the full map.
                if (!peeking && inRaid)
                {
                    try
                    {
                        EnemyEspRenderer.Draw();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"EnemyEspRenderer.Draw exception: {e}");
                    }
                }

                MapDef def;
                Texture2D texture;
                string matchInfo;

                if (_manualDef != null)
                {
                    def = _manualDef;
                    texture = MapUtils.GetTexture(def);
                    matchInfo = $"manual: {def.DisplayName}";
                }
                else
                {
                    var internalName = GameUtils.GetCurrentMapInternalName() ?? FallbackMapInternalName;
                    (def, texture) = MapUtils.GetForInternalName(internalName);
                    matchInfo = def != null
                        ? $"match: '{internalName}' -> {def.DisplayName}"
                        : $"match: '{internalName}' -> NOT FOUND";

                    // per user: a bad/missing match shouldn't leave the map blank - show
                    // whatever's available instead so there's always something on screen, useful
                    // both to verify rendering itself works and as a manual fallback.
                    if (def == null)
                    {
                        var all = MapUtils.GetAllDefs();
                        if (all.Count > 0)
                        {
                            def = all[0];
                            texture = MapUtils.GetTexture(def);
                            matchInfo += $" (showing {def.DisplayName} instead, keypad 9/3 to pick another)";
                        }
                    }
                }

                Rect rect;
                if (peeking)
                {
                    var size = Mathf.Min(Screen.width, Screen.height) * PeekScreenFraction;
                    rect = new Rect((Screen.width - size) / 2f, (Screen.height - size) / 2f, size, size);
                }
                else
                {
                    // height follows the current map's own aspect ratio so the mini-map never
                    // shows letterboxed empty space or crops the map - only width is configurable.
                    var aspect = texture != null ? texture.height / (float)texture.width : 1f;
                    var height = MinimapWidth * aspect;
                    var origin = GetMinimapOrigin(MinimapWidth, height);
                    rect = new Rect(origin.x, origin.y, MinimapWidth, height);
                }

                GUI.Box(rect, GUIContent.none);

                if (texture == null)
                {
                    GUI.Label(rect, "no maps loaded at all");
                    return;
                }

                MapLevel level = null;
                if (def.HasLevels)
                {
                    level = ResolveActiveLevel(def);
                    var levelTexture = MapUtils.GetLevelTexture(level);
                    if (levelTexture != null) texture = levelTexture;
                    matchInfo += _manualLevel.HasValue
                        ? $" | floor: {level?.Level} (manual)"
                        : $" | floor: {level?.Level} (auto)";
                }

                // peeking (M held) always shows the full map, ignoring the configured zoom - which
                // resumes as soon as M is released, since this doesn't touch _zoom itself.
                var effectiveZoom = peeking ? MinZoom : _zoom;
                DrawMap(rect, def, texture, level, effectiveZoom);

                var topBarRect = new Rect(rect.x, rect.y, rect.width, 18);
                var prevColor = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.6f);
                GUI.DrawTexture(topBarRect, Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(topBarRect, matchInfo);
                GUI.color = prevColor;

                GUI.Label(new Rect(rect.x + 4, rect.y + rect.height - 18, rect.width - 8, 18), $"{def.DisplayName} ({effectiveZoom:0.0}x)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"SPTMapBehaviour.OnGUI exception: {e}");
            }
        }

        private void DrawMap(Rect box, MapDef def, Texture2D texture, MapLevel activeLevel, float zoom)
        {
            var boundsMin = def.Bounds.Min;
            var boundsMax = def.Bounds.Max;
            var fullSize = boundsMax - boundsMin;

            Vector2? playerPos = null;
            var player = GameUtils.GetMainPlayer();
            if (player != null)
            {
                // Cast<IPlayer>() rather than a direct .Position access - under this IL2CPP
                // interop, Player's C# metadata doesn't always statically show interfaces it
                // really implements at the native level (see old SPT-DynamicMaps' MEMORY.md for
                // the same gotcha), Cast<T>() sidesteps that reliably.
                var rawMapPos = MathUtils.ConvertToMapPosition(player.Cast<IPlayer>().Position);
                playerPos = MathUtils.Rotate90Multiple(rawMapPos, def.CoordinateRotation);
            }

            var (viewMinT, viewMaxT) = MathUtils.ComputeViewBounds(
                (boundsMin.x, boundsMin.y), (boundsMax.x, boundsMax.y), zoom,
                playerPos.HasValue ? (playerPos.Value.x, playerPos.Value.y) : ((float x, float y)?)null);
            var viewMin = new Vector2(viewMinT.x, viewMinT.y);
            var viewMax = new Vector2(viewMaxT.x, viewMaxT.y);

            var imageRect = FitInside(box, texture.width / (float)texture.height);

            var u0 = (viewMin.x - boundsMin.x) / fullSize.x;
            var u1 = (viewMax.x - boundsMin.x) / fullSize.x;
            var v0 = (viewMin.y - boundsMin.y) / fullSize.y;
            var v1 = (viewMax.y - boundsMin.y) / fullSize.y;
            var texCoords = new Rect(u0, v0, u1 - u0, v1 - v0);
            GUI.DrawTextureWithTexCoords(imageRect, texture, texCoords);

            if (playerPos.HasValue)
            {
                var u = Mathf.InverseLerp(viewMin.x, viewMax.x, playerPos.Value.x);
                var v = Mathf.InverseLerp(viewMin.y, viewMax.y, playerPos.Value.y);
                var screenX = imageRect.x + u * imageRect.width;
                // v increases "north"/up in world space, screen Y increases downward - flip.
                var screenY = imageRect.y + (1f - v) * imageRect.height;

                DrawPlayerIcon(player, def, screenX, screenY);
            }

            DrawMarkers(def, activeLevel, imageRect, viewMin, viewMax);
        }

        private const float MarkerIconSize = 14f;

        // markers resolved to a different floor than the one currently shown are dimmed instead
        // of hidden - still useful context (e.g. a corpse one floor up/down), just visually
        // de-emphasized.
        private const float OtherFloorAlpha = 0.35f;

        private void DrawMarkers(MapDef def, MapLevel activeLevel, Rect imageRect, Vector2 viewMin, Vector2 viewMax)
        {
            string hoveredText = null;

            // copy first - providers can add/remove markers (e.g. death during this same frame's
            // event handling) and mutating List<T> while foreach-ing it throws.
            foreach (var marker in MarkerManager.Markers.ToArray())
            {
                try
                {
                    var text = DrawMarker(marker, def, activeLevel, imageRect, viewMin, viewMax);
                    if (text != null)
                    {
                        hoveredText = text;
                    }
                }
                catch (Exception e)
                {
                    // one bad marker (e.g. an Il2Cpp object destroyed the same frame it's drawn)
                    // shouldn't take the whole map down.
                    Plugin.Log.LogError($"DrawMarker exception for '{marker.Category}': {e}");
                }
            }

            // only meaningful while the cursor is free to hover (big map open) - the mini-map
            // never gets mouse input since the cursor stays locked/hidden for gameplay then.
            if (_peekToggled && hoveredText != null)
            {
                DrawTooltip(hoveredText, Event.current.mousePosition);
            }
        }

        private static void DrawTooltip(string text, Vector2 mousePos)
        {
            var size = GUI.skin.label.CalcSize(new GUIContent(text));
            var rect = new Rect(mousePos.x + 14f, mousePos.y + 14f, size.x + 8f, size.y + 4f);

            var prevColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(rect, text);
            GUI.color = prevColor;
        }

        // returns the marker's display text if the cursor is hovering it (so the caller can draw
        // one tooltip on top of everything, after all markers are drawn), null otherwise.
        private string DrawMarker(MapMarker marker, MapDef def, MapLevel activeLevel, Rect imageRect, Vector2 viewMin, Vector2 viewMax)
        {
            var rawPos = marker.GetPosition?.Invoke();
            if (!rawPos.HasValue)
            {
                return null;
            }

            var mapPos = MathUtils.Rotate90Multiple(rawPos.Value, def.CoordinateRotation);
            var u = Mathf.InverseLerp(viewMin.x, viewMax.x, mapPos.x);
            var v = Mathf.InverseLerp(viewMin.y, viewMax.y, mapPos.y);
            if (u < 0f || u > 1f || v < 0f || v > 1f)
            {
                return null;
            }

            var screenX = imageRect.x + u * imageRect.width;
            var screenY = imageRect.y + (1f - v) * imageRect.height;

            var imagePath = marker.GetImagePath?.Invoke() ?? marker.ImagePath;
            var texture = MapUtils.GetTextureByPath(imagePath);
            var rect = new Rect(screenX - MarkerIconSize / 2f, screenY - MarkerIconSize / 2f, MarkerIconSize, MarkerIconSize);
            var hovered = !string.IsNullOrEmpty(marker.Text) && rect.Contains(Event.current.mousePosition);

            var markerColor = marker.GetColor?.Invoke() ?? marker.Color;
            if (def.HasLevels && activeLevel != null)
            {
                var worldPos = marker.GetWorldPosition?.Invoke();
                if (worldPos.HasValue)
                {
                    var markerLevel = ResolveLevelForPosition(def, worldPos.Value);
                    if (markerLevel != null && markerLevel != activeLevel)
                    {
                        markerColor.a *= OtherFloorAlpha;
                    }
                }
            }

            var prevColor = GUI.color;
            GUI.color = markerColor;

            if (texture == null)
            {
                GUI.DrawTexture(rect, Texture2D.whiteTexture);
                GUI.color = prevColor;
                DrawMarkerLabelIfEnabled(marker, screenX, rect.yMax, markerColor.a);
                return hovered ? marker.Text : null;
            }

            var rawFacing = marker.GetFacing?.Invoke();
            if (rawFacing.HasValue)
            {
                var rotatedFacing = MathUtils.Rotate90Multiple(rawFacing.Value, def.CoordinateRotation);
                var angle = Mathf.Atan2(rotatedFacing.x, rotatedFacing.y) * Mathf.Rad2Deg;

                var prevMatrix = GUI.matrix;
                GUIUtility.RotateAroundPivot(angle, new Vector2(screenX, screenY));
                GUI.DrawTexture(rect, texture);
                GUI.matrix = prevMatrix;
            }
            else
            {
                GUI.DrawTexture(rect, texture);
            }

            GUI.color = prevColor;
            DrawMarkerLabelIfEnabled(marker, screenX, rect.yMax, markerColor.a);
            return hovered ? marker.Text : null;
        }

        private static GUIStyle _markerLabelStyle;
        private static GUIStyle MarkerLabelStyle => _markerLabelStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = 9,
            wordWrap = false,
        };

        // always-on small label under a marker's icon (vs. the hover-only tooltip in DrawTooltip) -
        // opt-in per marker via MapMarker.ShowLabel, gated by Settings.ShowMarkerLabels. Drawn twice
        // (dark offset, then light) as a cheap fake outline so it stays readable over any map color.
        private static void DrawMarkerLabelIfEnabled(MapMarker marker, float centerX, float topY, float alpha)
        {
            if (!marker.ShowLabel || string.IsNullOrEmpty(marker.Text) || !SPTMapConfig.ShowMarkerLabels.Value)
            {
                return;
            }

            var style = MarkerLabelStyle;
            var size = style.CalcSize(new GUIContent(marker.Text));
            var rect = new Rect(centerX - size.x / 2f, topY + 1f, size.x, size.y);

            var prevColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.85f * alpha);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), marker.Text, style);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(rect, marker.Text, style);
            GUI.color = prevColor;
        }

        private const string PlayerArrowImagePath = "Markers/arrow.png";
        private const float PlayerIconSize = 16f;

        // arrow.png (from the old SPT-DynamicMaps project's marker set) points "up" at zero
        // rotation. GUI.DrawTexture vs a plain colored rect cost the same - one textured quad draw
        // call either way - so there's no reason not to use the real, direction-aware icon.
        private void DrawPlayerIcon(Player player, MapDef def, float screenX, float screenY)
        {
            var texture = MapUtils.GetTextureByPath(PlayerArrowImagePath);
            var rect = new Rect(screenX - PlayerIconSize / 2f, screenY - PlayerIconSize / 2f, PlayerIconSize, PlayerIconSize);

            if (texture == null)
            {
                // fallback so a missing/failed-to-load icon still shows *something*
                var prevColor = GUI.color;
                GUI.color = Color.red;
                GUI.DrawTexture(rect, Texture2D.whiteTexture);
                GUI.color = prevColor;
                return;
            }

            // rotate the facing direction the same way the position was rotated (both need to
            // agree with the map's baked-in CoordinateRotation), then convert to a compass-style
            // angle (clockwise from "up") - that's exactly what GUIUtility.RotateAroundPivot
            // expects since screen Y increases downward.
            var forward3D = player.transform.forward;
            var rotatedForward = MathUtils.Rotate90Multiple(new Vector2(forward3D.x, forward3D.z), def.CoordinateRotation);
            var angle = Mathf.Atan2(rotatedForward.x, rotatedForward.y) * Mathf.Rad2Deg;

            var prevMatrix = GUI.matrix;
            var prevIconColor = GUI.color;
            GUI.color = Color.green;
            GUIUtility.RotateAroundPivot(angle, new Vector2(screenX, screenY));
            GUI.DrawTexture(rect, texture);
            GUI.matrix = prevMatrix;
            GUI.color = prevIconColor;
        }

        private static Rect FitInside(Rect container, float aspect)
        {
            var containerAspect = container.width / container.height;
            float w, h;
            if (aspect > containerAspect)
            {
                w = container.width;
                h = w / aspect;
            }
            else
            {
                h = container.height;
                w = h * aspect;
            }

            var x = container.x + (container.width - w) / 2f;
            var y = container.y + (container.height - h) / 2f;
            return new Rect(x, y, w, h);
        }
    }
}
