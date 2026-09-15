# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Workflow

Before making any code change (edits, new files, running build/asset scripts), state in 1-2 sentences what you're about to change and why, then proceed. This gives the user a chance to interrupt if the approach is wrong before time is spent on it. Trivial read-only actions (Read/Grep/Glob, `git status`) don't need this preamble.

## ENV
"python" to run python. Path to d:\git\uv, and then use uv add to install new package.
use bun, not node.
 
## What this is

SPTMap is a BepInEx IL2CPP plugin for SPT (Single Player Tarkov) that renders an in-game minimap using Unity's legacy IMGUI system. It is a rewrite of an earlier uGUI-based dynamic maps mod, deliberately avoiding uGUI and Unity.VectorGraphics to sidestep IL2CPP generic-method-unstripping crashes and BSG UI-version churn.

## Build & deploy

`TarkovDir` (path to the local SPT install) defaults to `D:\Game\SPT5\` in the tracked csproj. Override it per machine in `Plugin\SPTMap.csproj.user` (gitignored, MSBuild auto-imports it) if your install lives elsewhere:

```xml
<Project>
  <PropertyGroup>
    <TarkovDir>D:\Game\SPT5\</TarkovDir>
  </PropertyGroup>
</Project>
```

```powershell
# From Plugin\
 dotnet build

# Or override for one build without a .csproj.user:
 dotnet build -p:TarkovDir="D:\Game\SPT5\"
```

PostBuild auto-copies `SPTMap.dll`, `Newtonsoft.Json.dll`, and `Resources\` to `$(TarkovDir)BepInEx\plugins\sptmap\`. **Close the game before rebuilding** — the copy fails if `EscapeFromTarkov.exe` is running.

## Asset build scripts (`py\`)

Both scripts require `cairosvg` and `pillow`. Install via `uv add <pkg>` in `D:\Git\uv` (the project's Python env), not bare `pip install`.

`py\build_maps.py` and `py\update_labs.py` read from a sibling checkout of the old SPT-DynamicMaps project, via `DYNAMICMAPS_REPO`. Default (if no `py\local_config.py`) is `D:\Git\SPT-DynamicMaps`; to override, copy `py\local_config.py.example` to `py\local_config.py` (gitignored) and set `DYNAMICMAPS_REPO` there.

- **`py\update_maps_floors.py`** — source of truth for the 9 tarkov.dev-backed maps (Customs, Factory, GroundZero, Interchange, Lighthouse, Reserve, Shoreline, Streets, Woods). Fetches `the-hideout/tarkov-dev` `maps.json` + live SVGs fresh each run. Rerun to pick up future tarkov.dev changes.
- **`py\build_maps.py`** — legacy, reads the vendored SVG pack under `<DYNAMICMAPS_REPO>\Plugin\release\...\Maps\`. Only relevant for Labs and Labyrinth (not covered by `update_maps_floors.py`). **Do not run it for the other 9 maps** — it overwrites the tarkov.dev-sourced data with stale vendored art.
- **`py\calibrate_bounds.py`** — affine recalibration tool. Use with in-game Keypad `.` landmarks (logged by `Plugin.cs LogLandmark`) if a map's bounds drift from ground truth.

Run these from the repo root (e.g. `python py\update_maps_floors.py`) — their output paths resolve relative to the script's own location, one level up. After running either asset script, `dotnet build` from `Plugin\` to redeploy.

## Architecture

### Plugin lifecycle

`BasePlugin` is not a `MonoBehaviour` under IL2CPP BepInEx — it never gets `OnGUI`/`Update`. The render loop lives on `SPTMapBehaviour`, a `MonoBehaviour` registered via `ClassInjector` and attached to `CommonUI`'s `GameObject` via a Harmony postfix on `CommonUI.Awake`. CommonUI persists across menu/hideout/raid, which is why this works while a naively created root GameObject does not (the engine destroys it during scene transition even with `DontDestroyOnLoad`).

`Plugin.Load()` checks the running game's build number (via `FileVersionInfo` on the game process's own exe) against `Plugin.BuiltForVersion` before doing anything else. This is a `>=` floor, not an exact-version match like the predecessor project used — EFT's build number bumps on every game patch, and this plugin only touches generic Unity/IMGUI/Harmony/reflection surfaces that don't change per-patch, so refusing to load on a newer game build would be needless breakage. Only a game *older* than `BuiltForVersion` is refused (logged + added to `IL2CPPChainloader.Instance.DependencyErrors`, load aborted) — that's the direction where this plugin's IL2CPP interop/reflection assumptions might not hold. The minimap's idle "SPTMap Loaded" label shows `BuiltForVersion` so a mismatch is visible in-game, not just in the log.

Harmony patches are registered by explicit type, not `PatchAll(assembly)` — the assembly-wide scan caused a total render failure.

### Map data flow

1. `update_maps_floors.py` fetches tarkov.dev SVGs → rasterizes to PNGs via `cairosvg`/`PIL`, bakes `CoordinateRotation` into both the image (PIL rotate) and `MapDef.Bounds` (corner-rotation math) → writes `Plugin/Resources/Maps/<Name>/<Name>.json` + `<Name>.png` per floor.
2. At runtime, `MapUtils.cs` loads these JSONs/PNGs into `MapDef` objects (cached as `Texture2D` with `HideFlags.DontUnloadUnusedAsset` to survive raid-load asset cleanup).
3. `Plugin.cs SPTMapBehaviour.OnGUI` renders the minimap (always, top-right) and full map (while holding M) using `GUI.DrawTexture`.
4. The player's raw world position is rotated by `MathUtils.Rotate90Multiple(pos, CoordinateRotation)` before being mapped against the pre-rotated `Bounds` — this is intentional and must be consistent everywhere position is compared to bounds.

### Multi-floor

Each `MapDef` has a `Levels` list (empty = single-level, old behavior). Each level has its own image + a list of 3D `GameBounds` world-space boxes. Auto-detect (`Keypad 4`) matches the player's raw position against all boxes, picking the smallest-volume match on overlap. Keypad 7/1 step manually; Keypad 9/3 cycle map defs; Keypad 6 returns to auto-detect.

### Markers

`MarkerManager.cs` holds a flat `List<MapMarker>`. Providers (`ExtractMarkerProvider`, `SecretMarkerProvider`, `TransitMarkerProvider`, `DoorMarkerProvider`, `OtherPlayersMarkerProvider` (also covers corpses), `QuestMarkerProvider`, `BTRMarkerProvider`, `AirdropMarkerProvider`) add/remove entries. `SPTMapController.DrawMarkers` iterates every `OnGUI`, each draw wrapped in its own `try/catch` so one bad marker can't blank the map. Marker positions are raw pre-rotation world space — `DrawMarkers` applies `CoordinateRotation` the same way the player marker does.

All providers are patch-free by design: extracts/secrets/transit/doors resolve their source lists straight off engine controllers (`ExfiltrationController`, `TransitController`) or a scene scan (`Object.FindObjectsOfType<Door>()`) at `OnRaidStart`, retried each frame until populated; BTR and airdrops poll a live `Tick()` each frame/on a short timer instead (`GameUtils.GetBTRView()`, `Object.FindObjectsOfType<AirdropSynchronizableObject>()`) since those can appear/disappear mid-raid. `AirdropMarkerProvider`'s live rescan is a deliberate departure from the predecessor project, which relied on a Harmony patch on `ClientAirDrop.CloseParachute` (only fires once a crate has already landed) — the rescan approach should also surface a crate while it's still under its parachute, but this hasn't been confirmed in-game yet.

Raid start/end is edge-detected in `SPTMapBehaviour.Update()` off `GameUtils.IsInRaid()` and drives all providers' `OnRaidStart`/`OnRaidEnd` + `MarkerManager.Clear()`.

### Quest debug panel

`Plugin/Utils/QuestDebugPanel.cs` — dev-only tool, `F9` toggles a floating IMGUI panel listing every quest (tabbed Incomplete/Not started/Completed) with a "Finish" button per quest. The button calls `QuestController.TryInstantFinishQuest` (the same public entry point the native Tasks-screen "Complete quest" button uses), so reward grants/condition bookkeeping/chain unlocks all fire for real — it's not a fake completion. Works from the main menu or in a raid, via `ItemUiContext.Instance.QuestController` (persistent, not Player-bound). Exists to unblock testing later quests in a chain when an earlier quest's own completion trigger is bugged, without re-running a whole raid. Should be gated behind a debug/dev config flag (or removed) before treating this as a release build for other players, since it's a completion cheat.

### Key files

| File | Role |
|------|------|
| `Plugin/Plugin.cs` | Entry point, `SPTMapBehaviour` (all `OnGUI`/`Update` logic) |
| `Plugin/Data/MapDef.cs` | Map/level/bounds data model |
| `Plugin/Data/MapMarker.cs` | Marker data class (position/facing as `Func<Vector2?>`) |
| `Plugin/Utils/MapUtils.cs` | JSON loading, texture cache (`GetTexture`/`GetTextureByPath`) |
| `Plugin/Utils/MarkerManager.cs` | Marker list, Add/Remove |
| `Plugin/Utils/GameUtils.cs` | EFT state helpers (raid detection, player categorization) |
| `Plugin/Utils/MathUtils.cs` | `Rotate90Multiple`, coordinate math |
| `Plugin/Utils/QuestDebugPanel.cs` | F9 dev panel, instant quest completion |
| `Plugin/Config/Settings.cs` | BepInEx `ConfigEntry` bindings |
| `Plugin/DynamicMarkers/` | Marker providers (extracts, secrets, transit, doors, players/corpses, quests, BTR, airdrops) |

## Current state (as of 2026-09-10)

- All 11 maps working with multi-floor support (Labs/Labyrinth single-level, stale art/bounds — no tarkov.dev SVG available).
- Markers ported from the predecessor project but **not yet verified in-game**. Things to check first: extract marker status colors update correctly; other players/corpses appear and clean up on death/raid-end without leaking; quest markers are positioned correctly and don't spam-log errors from `QuestUtils`'s reflection-based loot-item lookup (not exercised yet in this project).
- Secret/transit/BTR markers (added 2026-09-14) are unverified in-game. Airdrop markers (also added 2026-09-14) use a live `FindObjectsOfType<AirdropSynchronizableObject>` rescan instead of the predecessor's landing-only Harmony patch, specifically so a crate shows up while still under its parachute — whether that object is actually present/positioned correctly pre-landing hasn't been confirmed yet.
- Map calibration: 9 maps refreshed from tarkov.dev and confirmed current as of 2026-09-10 (`the-hideout/tarkov-dev`'s `maps.json` + live SVGs, same coordinate convention as `Bounds`/`CoordinateRotation`/`GameBounds`). Labs/Labyrinth have no `svgPath` on tarkov.dev (raster-tile-only) and stay on old vendored data. If a map still doesn't line up with in-game terrain after a tarkov.dev refresh, fall back to manual affine recalibration via `calibrate_bounds.py`; proportionally expanding `Bounds` is the last-resort option if neither source is available.
- A few floors have no distinct art on tarkov.dev at all (tile-only, not SVG) and are skipped rather than guessed at: Customs' 4th floor and Reserve's above-ground floors except Bunkers. Those areas just show the Ground level image underneath — a readability gap, not a correctness one.
- Some display constants (mini-map size, zoom speed) are exposed via `Settings.cs` `ConfigEntry`s (visible as sliders in BepInEx ConfigurationManager); box sizing and key bindings are still hardcoded in `Plugin.cs`.
- No POI labels yet.

## IL2CPP interop notes

- `Texture2D` objects held only in a static C# dictionary get destroyed by the engine's unused-asset cleanup on raid load. Fix: set `HideFlags.DontUnloadUnusedAsset` at load time **and** treat a Unity fake-null cache hit as a cache miss (`MapUtils.GetTexture`). Apply the same treatment to any new cached `UnityEngine.Object`.
- Use `Il2CppSystem.Action<>` / `Il2CppSystem.Func<>` (not `System.`) when wiring Il2Cpp delegates (event handlers, callbacks on EFT types).
- A `GameObject` created during `BasePlugin.Load()` gets destroyed by the engine's own scene transition shortly after, even with `DontDestroyOnLoad` — this is why the render component is instead parented to `CommonUI`'s GameObject via the Harmony postfix described above.
