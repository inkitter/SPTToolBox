using System;
using System.Collections.Generic;
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

        // teleport-to-mouse, only meaningful with the full map open (M held) since that's the
        // only time the cursor is free and _lastMapImageRect reflects a precise, zoomed-in click
        // target rather than the tiny corner minimap.
        private const KeyCode TeleportKey = KeyCode.F8;

        // how far above the clicked X/Z the ground-finding raycast starts when the active level
        // has no GameBounds box for that spot (single-level maps, or a level whose box list means
        // "everywhere") - has to clear the tallest roof/terrain on the map. When a box IS found,
        // its own Max.y is used instead (see TryFindGroundHeight), which is what keeps multi-floor
        // maps from raycasting down onto a lower floor's ceiling instead of the intended one.
        private const float TeleportRaycastFallbackHeight = 50f;
        private const float TeleportRaycastMaxDistance = 200f;

        // landed exactly on the raycast hit point would have the player's feet origin sunk into
        // the ground by however thick their collider's own margin is - this tiny lift avoids that
        // without being enough to trigger fall damage on landing.
        private const float TeleportStandOffset = 0.05f;
        private static int MinimapSize => SPTMapConfig.MiniMapSize.Value;

        // mini-map rect for a map of the given aspect (height / width): the configured size is the
        // longer side, the shorter side follows the aspect so the map is never letterboxed.
        private static Rect GetMinimapRect(float aspect)
        {
            float width;
            float height;
            if (aspect >= 1f)
            {
                height = MinimapSize;
                width = MinimapSize / aspect;
            }
            else
            {
                width = MinimapSize;
                height = MinimapSize * aspect;
            }

            var origin = GetMinimapOrigin(width, height);
            LogAnchorIfChanged(origin, width, height);
            return new Rect(origin.x, origin.y, width, height);
        }

        // diagnostic: logs once whenever anchor/padding/window size changes, so a wrong placement
        // report can be checked against what was actually computed.
        private static string _lastAnchorLog;

        private static void LogAnchorIfChanged(Vector2 origin, float width, float height)
        {
            var key = $"{SPTMapConfig.AnchorLeft.Value}|{SPTMapConfig.AnchorBottom.Value}|{SPTMapConfig.PaddingX.Value}|{SPTMapConfig.PaddingY.Value}|{Screen.width}x{Screen.height}|{Mathf.RoundToInt(width)}x{Mathf.RoundToInt(height)}";
            if (key == _lastAnchorLog)
            {
                return;
            }

            _lastAnchorLog = key;
            Plugin.Log.LogInfo($"[minimap] anchorLeft={SPTMapConfig.AnchorLeft.Value} anchorBottom={SPTMapConfig.AnchorBottom.Value} padding=({SPTMapConfig.PaddingX.Value},{SPTMapConfig.PaddingY.Value}) "
                + $"window={Screen.width}x{Screen.height} rect=({origin.x:0},{origin.y:0},{width:0},{height:0})");
        }
        private const float PeekScreenFraction = 0.8f;

        private static Vector2 GetMinimapOrigin(float width, float height)
        {
            var paddingX = SPTMapConfig.PaddingX.Value;
            var paddingY = SPTMapConfig.PaddingY.Value;

            var x = SPTMapConfig.AnchorLeft.Value ? paddingX : Screen.width - width - paddingX;
            var y = SPTMapConfig.AnchorBottom.Value ? Screen.height - height - paddingY : paddingY;

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
        private UnitMarkerProvider _unitMarkerProvider;
        private QuestMarkerProvider _questMarkerProvider;
        private TransitMarkerProvider _transitMarkerProvider;
        private SwitchMarkerProvider _switchMarkerProvider;
        private AirdropMarkerProvider _airdropMarkerProvider;
        private ItemMarkerProvider _itemMarkerProvider;
        private LootableContainerMarkerProvider _lootableContainerMarkerProvider;

        // Hard kill switches, independent of any ConfigEntry - flipping these to true must
        // guarantee the corresponding provider's code never runs, not just default-off.
        private const bool ItemMarkersDisabled = false;
        private const bool LootableContainerMarkersDisabled = true;

        // reused snapshot buffer for DrawMarkers - a provider can add/remove markers mid-frame
        // (e.g. death during this same frame's event handling), so iterating MarkerManager.Markers
        // directly would throw; copying into this List (Clear + AddRange reuses its backing array
        // across frames once capacity settles) gets the same safety as the old
        // MarkerManager.Markers.ToArray() without a fresh heap allocation every OnGUI call.
        private readonly List<MapMarker> _markerDrawScratch = new();
        private bool _wasInRaid;

        private float _zoom = MinZoom;

        // full map (M) has its own zoom, independent of the mini-map's _zoom - mouse wheel or
        // keypad 8/5 while it's open only change this one, and vice versa.
        private float _peekZoom = MinZoom;
        private const float WheelZoomStep = 1.15f;

        // null = auto-detect from GameUtils.GetCurrentMapInternalName(); Keypad 9/6 cycle through
        // every known map def manually (for when auto-detect doesn't match, or just to browse
        // other maps), Keypad 3 drops back to auto-detect.
        private MapDef _manualDef;

        // null = auto-detect the current floor from player height (MapDef.Levels' HeightMin/Max);
        // Keypad 7/4 step up/down through a multi-level map's floors manually, Keypad 1 drops back
        // to auto-detect. Meaningless (ignored) for single-level maps.
        private int? _manualLevel;

        private bool _peekToggled;

        // last full-map draw's screen-space image rect + the world-space view window it covers -
        // captured every DrawMap call so TryTeleportToMouse (Keypad-independent, F8) can invert
        // "where on screen did they click" back into a world position without redoing the whole
        // zoom/pan computation. One frame stale at worst (Update runs after the prior frame's
        // OnGUI) - imperceptible for a manual teleport click.
        private Rect? _lastMapImageRect;
        private Vector2 _lastMapViewMin;
        private Vector2 _lastMapViewMax;
        private MapDef _lastMapDef;

        public void Update()
        {
            try
            {
                UpdateInternal();
            }
            catch (Exception e)
            {
                // unlike the marker-provider calls below (each already wrapped individually so one
                // bad provider can't block the rest), this is the outermost catch-all - without it
                // an exception anywhere in Update (e.g. the F8 teleport handler) would silently
                // stop that whole frame's input handling with nothing visible in-game, only in the
                // log.
                Plugin.Log.LogError($"SPTMapController.Update exception: {e}");
            }
        }

        private void UpdateInternal()
        {
            PrestigeGlobalsLoadPatch.Tick();

            if (GameUtils.IsInRaid() && SPTMapConfig.ShowHitDamageNumbers.Value)
            {
                try
                {
                    BulletHitPopupRenderer.Tick();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"BulletHitPopupRenderer.Tick exception: {e}");
                }
            }

            if (Input.GetKeyDown(PeekKey))
            {
                _peekToggled = !_peekToggled;

                if (!_peekToggled)
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
                else if (GameUtils.IsInRaid())
                {
                    // map just opened - refresh the throttled quest/wishlist markers immediately
                    // instead of leaving them stale until their next 60s tick fires.
                    // Single shared loot-list scan for both consumers below (QuestMarkerProvider's
                    // find-item condition, ItemMarkerProvider's wishlist/backpack rules) instead of
                    // each walking GameWorld.LootList itself.
                    try
                    {
                        LootScanCache.Rescan();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"Loot scan cache refresh-on-open exception: {e}");
                    }

                    try
                    {
                        _questMarkerProvider?.RefreshNow();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"Quest marker refresh-on-open exception: {e}");
                    }

                    try
                    {
                        _itemMarkerProvider?.RefreshNow();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"Item marker refresh-on-open exception: {e}");
                    }

                    try
                    {
                        _airdropMarkerProvider?.RefreshNow();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"Airdrop marker refresh-on-open exception: {e}");
                    }

                    try
                    {
                        _extractMarkerProvider?.RefreshNow();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"Extract marker refresh-on-open exception: {e}");
                    }

                    try
                    {
                        _switchMarkerProvider?.RefreshNow();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"Switch marker refresh-on-open exception: {e}");
                    }

                }
            }

            var inRaid = GameUtils.IsInRaid();

            // re-asserted every frame (not just on the M keydown frame) while peeking in a raid -
            // EFT's own player-control code re-locks/re-hides the cursor every frame during normal
            // gameplay, which otherwise wins the race against a one-shot Cursor.visible = true set
            // here and the cursor never actually appears. Only while in a raid - out of raid the
            // menu already manages the cursor itself (see the OnRaidEnd comment below).
            if (_peekToggled && inRaid)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
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
                TryRetryTransitMarkers();
                TryRetryLootableContainerMarkers();
                TryTickUnits();

                // Quest/wishlist/airdrop/backpack/extract/door-key-ownership are all expensive
                // rescans with no natural per-frame trigger - rather than re-running them on any
                // periodic timer (even a long one) while the map is open, each is refreshed exactly
                // once, on the frame the map is opened (see the PeekKey handler below calling each
                // provider's RefreshNow()/SetMapOpen()). Staying open longer doesn't re-trigger
                // them - close and reopen the map to force a fresh look. Other-players (which now
                // also covers the BTR, tracked as its turret gunner) are left as a genuine
                // per-frame poll since their whole point is live tracking.
                DoorMarkerProvider.SetMapOpen(_peekToggled);
            }
            _wasInRaid = inRaid;

            // keypad 8/5/2 zoom whichever map is showing; the two zoom levels never affect each other
            var zoom = _peekToggled ? _peekZoom : _zoom;
            if (Input.GetKeyDown(KeyCode.Keypad2))
            {
                zoom = MinZoom;
            }
            else if (Input.GetKey(KeyCode.Keypad8))
            {
                zoom = Mathf.Clamp(zoom * Mathf.Pow(ZoomPerSecond, Time.deltaTime), MinZoom, MaxZoom);
            }
            else if (Input.GetKey(KeyCode.Keypad5))
            {
                zoom = Mathf.Clamp(zoom / Mathf.Pow(ZoomPerSecond, Time.deltaTime), MinZoom, MaxZoom);
            }

            // mouse wheel only while the full map is open (the cursor is free then; otherwise the
            // wheel belongs to the game's own weapon/action controls)
            if (_peekToggled)
            {
                var scroll = Input.mouseScrollDelta.y;
                if (scroll > 0f)
                {
                    zoom = Mathf.Clamp(zoom * Mathf.Pow(WheelZoomStep, scroll), MinZoom, MaxZoom);
                }
                else if (scroll < 0f)
                {
                    zoom = Mathf.Clamp(zoom / Mathf.Pow(WheelZoomStep, -scroll), MinZoom, MaxZoom);
                }

                _peekZoom = zoom;
            }
            else
            {
                _zoom = zoom;
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

            if (_peekToggled && inRaid && Input.GetKeyDown(TeleportKey))
            {
                TryTeleportToMouse();
            }

            QuestDebugPanel.HandleInput();
        }

        private void OnRaidStart()
        {
            MarkerManager.Clear();
            try
            {
                _unitMarkerProvider ??= new UnitMarkerProvider();
                _unitMarkerProvider.OnRaidStart();
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
                // Shared with ItemMarkerProvider's OnRaidStart scan below - see the map-open
                // handler above for why this is a single cache rescan, not one per consumer.
                LootScanCache.Rescan();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidStart loot scan cache setup exception: {e}");
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

            try
            {
                _transitMarkerProvider ??= new TransitMarkerProvider();
                _transitMarkerProvider.OnRaidStart();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidStart transit marker setup exception: {e}");
            }

            try
            {
                _switchMarkerProvider ??= new SwitchMarkerProvider();
                _switchMarkerProvider.OnRaidStart();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidStart switch marker setup exception: {e}");
            }

            try
            {
                _airdropMarkerProvider ??= new AirdropMarkerProvider();
                _airdropMarkerProvider.OnRaidStart();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidStart airdrop marker setup exception: {e}");
            }

            try
            {
                if (!ItemMarkersDisabled)
                {
                    _itemMarkerProvider ??= new ItemMarkerProvider();
                    _itemMarkerProvider.OnRaidStart();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidStart item marker setup exception: {e}");
            }

            try
            {
                if (!LootableContainerMarkersDisabled && SPTMapConfig.ShowLootableContainers.Value)
                {
                    _lootableContainerMarkerProvider ??= new LootableContainerMarkerProvider();
                    _lootableContainerMarkerProvider.OnRaidStart();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidStart lootable container marker setup exception: {e}");
            }
        }

        private void TryRetryTransitMarkers()
        {
            try
            {
                _transitMarkerProvider?.OnRaidStart();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Transit marker retry exception: {e}");
            }
        }

        private void TryRetryLootableContainerMarkers()
        {
            try
            {
                if (!LootableContainerMarkersDisabled && SPTMapConfig.ShowLootableContainers.Value)
                {
                    // lazily created here too, not just in the main OnRaidStart - if the setting
                    // was off when the raid started, OnRaidStart's own block above never ran this
                    // provider's ??=, so the field stays null forever and toggling the setting on
                    // mid-raid (this retry loop runs every frame regardless) had nothing to
                    // populate: a bare `?.OnRaidStart()` on a still-null field is a silent no-op.
                    _lootableContainerMarkerProvider ??= new LootableContainerMarkerProvider();
                    _lootableContainerMarkerProvider.OnRaidStart();
                }
                else
                {
                    // toggled off mid-raid: drop any markers already drawn and reset the provider's
                    // populate state, so it does a fresh FindObjectsOfType scan (not a stale one) if
                    // the setting gets flipped back on later in the same raid.
                    _lootableContainerMarkerProvider?.OnRaidEnd();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Lootable container marker retry exception: {e}");
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

        private void TryTickUnits()
        {
            try
            {
                _unitMarkerProvider?.Tick(Time.deltaTime);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Unit marker poll exception: {e}");
            }
        }

        private void OnRaidEnd()
        {
            try
            {
                _unitMarkerProvider?.OnRaidEnd();
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

            try
            {
                _transitMarkerProvider?.OnRaidEnd();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidEnd transit marker teardown exception: {e}");
            }

            try
            {
                _switchMarkerProvider?.OnRaidEnd();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidEnd switch marker teardown exception: {e}");
            }


            try
            {
                _airdropMarkerProvider?.OnRaidEnd();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidEnd airdrop marker teardown exception: {e}");
            }

            try
            {
                _itemMarkerProvider?.OnRaidEnd();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidEnd item marker teardown exception: {e}");
            }

            try
            {
                _lootableContainerMarkerProvider?.OnRaidEnd();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidEnd lootable container marker teardown exception: {e}");
            }

            try
            {
                BulletHitPopupRenderer.OnRaidEnd();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidEnd bullet hit popup teardown exception: {e}");
            }

            try
            {
                EnemyEspRenderer.OnRaidEnd();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnRaidEnd enemy ESP teardown exception: {e}");
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

        // inverts the screen-space mapping DrawMap used (imageRect + viewMin/viewMax) to turn the
        // current mouse position back into a world position, then teleports the main player
        // there. Bounds-checked twice: against the drawn image rect (a click outside it isn't a
        // map click at all) and against the map's own world-space Bounds (belt-and-suspenders -
        // should already be implied by the first check, since viewMin/viewMax are clamped inside
        // Bounds, but cheap to confirm).
        private void TryTeleportToMouse()
        {
            Plugin.Log.LogInfo("[teleport] F8 pressed");

            if (_lastMapImageRect is not { } imageRect || _lastMapDef == null)
            {
                Plugin.Log.LogInfo("[teleport] no map drawn yet, ignored");
                return;
            }

            var player = GameUtils.GetMainPlayer();
            if (player == null)
            {
                Plugin.Log.LogInfo("[teleport] no main player, ignored");
                return;
            }

            // Input.mousePosition is bottom-up (origin bottom-left); imageRect was built in GUI
            // space (origin top-left, same as Event.current.mousePosition) - flip Y to match.
            var mouse = Input.mousePosition;
            var guiMouse = new Vector2(mouse.x, Screen.height - mouse.y);

            if (!imageRect.Contains(guiMouse))
            {
                Plugin.Log.LogInfo("[teleport] click outside the map image, ignored");
                return;
            }

            var u = (guiMouse.x - imageRect.x) / imageRect.width;
            var v = 1f - (guiMouse.y - imageRect.y) / imageRect.height;
            var mapPos = new Vector2(
                Mathf.Lerp(_lastMapViewMin.x, _lastMapViewMax.x, u),
                Mathf.Lerp(_lastMapViewMin.y, _lastMapViewMax.y, v));

            var bounds = _lastMapDef.Bounds;
            if (mapPos.x < bounds.Min.x || mapPos.x > bounds.Max.x || mapPos.y < bounds.Min.y || mapPos.y > bounds.Max.y)
            {
                Plugin.Log.LogInfo("[teleport] resolved position outside map bounds, ignored");
                return;
            }

            // undo the map's baked-in CoordinateRotation to get back to raw world-space X/Z (the
            // same rotation LogLandmark/DrawMap apply, just in reverse).
            var rawMapPos = MathUtils.Rotate90Multiple(mapPos, -_lastMapDef.CoordinateRotation);
            var currentPos = player.Cast<IPlayer>().Position;
            var activeLevel = _lastMapDef.HasLevels ? ResolveActiveLevel(_lastMapDef) : null;

            if (!TryFindGroundHeight(activeLevel, rawMapPos.x, rawMapPos.y, currentPos.y, out var groundY))
            {
                Plugin.Log.LogInfo("[teleport] no ground found under that point (hole in the map, or outside collision), ignored");
                return;
            }

            var targetPos = new Vector3(rawMapPos.x, groundY + TeleportStandOffset, rawMapPos.y);

            Plugin.Log.LogInfo($"[teleport] to ({targetPos.x:0.#}, {targetPos.y:0.#}, {targetPos.z:0.#})");
            player.Teleport(targetPos, true);
        }

        // finds the exact ground height under (x, z) via a downward raycast, so the player is
        // placed precisely on the terrain instead of falling to it (which risks real fall damage,
        // and doesn't account for how much a "floor"'s actual ground height can vary within
        // itself - e.g. sloped terrain, stairwells). When the active level has a GameBounds box
        // covering (x, z), the ray is confined to that box's own height band - this is what stops
        // a multi-floor map's raycast from punching through to a lower floor's ceiling and calling
        // that "ground" for the floor actually being shown.
        private bool TryFindGroundHeight(MapLevel activeLevel, float x, float z, float playerY, out float groundY)
        {
            var box = activeLevel?.GameBounds?.FirstOrDefault(b => x >= b.Min.x && x <= b.Max.x && z >= b.Min.z && z <= b.Max.z);

            float rayStartY;
            float maxDistance;
            if (box != null)
            {
                rayStartY = box.Max.y + 1f;
                maxDistance = (box.Max.y - box.Min.y) + 5f;
            }
            else
            {
                rayStartY = playerY + TeleportRaycastFallbackHeight;
                maxDistance = TeleportRaycastMaxDistance;
            }

            var origin = new Vector3(x, rayStartY, z);
            if (Physics.Raycast(origin, Vector3.down, out var hit, maxDistance, ~0, QueryTriggerInteraction.Ignore))
            {
                groundY = hit.point.y;
                return true;
            }

            groundY = 0f;
            return false;
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

                // OnGUI runs once per GUI event (Layout + Repaint every frame, plus one per mouse/
                // key event). The debug panel above uses GUILayout and needs every event; everything
                // below is plain GUI.* drawing with no interactive controls, so only the Repaint pass
                // does anything visible - skipping the rest cuts the map/marker/ESP cost 2x or more.
                if (Event.current.type != EventType.Repaint)
                {
                    return;
                }

                // outside a raid and not peeking, there's nothing worth showing permanently -
                // just prove the plugin's alive. In raid, the minimap is always up (small, corner);
                // holding M enlarges it to most of the screen for a full browse instead of
                // replacing a hidden map, matching how the peek key is expected to behave.
                if (!peeking && !inRaid)
                {
                    var labelOrigin = GetMinimapOrigin(MinimapSize, 20);
                    var labelRect = new Rect(labelOrigin.x, labelOrigin.y, MinimapSize, 20);
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

                    try
                    {
                        BulletHitPopupRenderer.Draw();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"BulletHitPopupRenderer.Draw exception: {e}");
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
                    // follows the current map's own aspect ratio so the mini-map never shows
                    // letterboxed empty space or crops the map - the configured size is the longer side.
                    var aspect = texture != null ? texture.height / (float)texture.width : 1f;
                    rect = GetMinimapRect(aspect);
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

                // full map and mini-map each keep their own zoom level
                var effectiveZoom = peeking ? _peekZoom : _zoom;
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

            _lastMapImageRect = imageRect;
            _lastMapViewMin = viewMin;
            _lastMapViewMax = viewMax;
            _lastMapDef = def;

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
            _markerDrawScratch.Clear();
            _markerDrawScratch.AddRange(MarkerManager.Markers);
            foreach (var marker in _markerDrawScratch)
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
            var iconSize = marker.IconSizeOverride ?? MarkerIconSize;
            var rect = new Rect(screenX - iconSize / 2f, screenY - iconSize / 2f, iconSize, iconSize);
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
                        if (marker.HideOnOtherFloors)
                        {
                            return null;
                        }

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
                return hovered ? (marker.GetText?.Invoke() ?? marker.Text) : null;
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
            return hovered ? (marker.GetText?.Invoke() ?? marker.Text) : null;
        }

        // routes a marker's always-on-label toggle by its Category string (set by the owning
        // provider - see UnitMarkerProvider/ExtractMarkerProvider/TransitMarkerProvider for
        // the exact category strings) rather than a single blanket setting, so e.g. enemy names can
        // stay on while teammate names are hidden.
        private static bool IsLabelCategoryEnabled(string category)
        {
            return category switch
            {
                "Friendly Player" => SPTMapConfig.ShowFriendlyPlayerLabels.Value,
                "Enemy Player" or "Scav" or "Boss" => SPTMapConfig.ShowEnemyPlayerLabels.Value,
                "Extract" => SPTMapConfig.ShowExtractLabels.Value,
                "Transit" => SPTMapConfig.ShowTransitLabels.Value,
                _ => SPTMapConfig.ShowOtherMarkerLabels.Value,
            };
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
            if (!marker.ShowLabel || string.IsNullOrEmpty(marker.Text) || !IsLabelCategoryEnabled(marker.Category))
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
