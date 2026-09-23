# CLAUDE.md

Guidance for Claude Code in this repo. Detailed rationale, history, and procedures live in [PROMPT.md](PROMPT.md) — read the relevant section before working in that area.

## Workflow

Before making any code change (edits, new files, running build/asset scripts), state in 1-2 sentences what you're about to change and why, then proceed. Read-only actions don't need this.

## ENV

- `python` to run Python; install packages with `uv add` in `D:\Git\uv`.
- Use bun, not node.

## What this is

Two independent projects in one repo (SPTToolBox):

- **SPTMap** (`Plugin/`) — BepInEx IL2CPP client plugin, in-game minimap rendered with legacy IMGUI (deliberately no uGUI / Unity.VectorGraphics). Deploys to `BepInEx/plugins/sptmap/`.
- **DbPostPatcher** (`Server/DbPostPatcher/`) — server C# mod applying small JSON patches to SPT's in-memory DB at boot. Deploys to `user/mods/DbPostPatcher/`. Patch format: [`patches/README.md`](Server/DbPostPatcher/patches/README.md).

## Build & deploy

- SPTMap: `dotnet build` from `Plugin\`. `TarkovDir` defaults to `D:\Game\SPT5\`; override in `Plugin\SPTMap.csproj.user` (gitignored) or `-p:TarkovDir=...`. **Close the game first** — PostBuild copy fails if `EscapeFromTarkov.exe` is running.
- DbPostPatcher: `dotnet build` from `Server\DbPostPatcher\`. `SptRuntimeDir` defaults to `D:\Game\SPT5\SPT_Runtime`; override in `DbPostPatcher.csproj.user`. **Always run the server once after adding a new patch file.**

## Release

Via `gh` (full path: `C:\Program Files\GitHub CLI\gh.exe`) to `inkitter/SPTToolBox`. **Ask the user whether to bump the version before publishing.** Full steps: PROMPT.md → Release procedure.

## Asset scripts (`py\`)

Run from repo root; need `cairosvg` + `pillow`. Rebuild `Plugin\` afterward.

- `update_maps_floors.py` — source of truth for the 9 tarkov.dev maps.
- `build_maps.py` — legacy, Labs/Labyrinth only. **Never run it for the other 9 maps** (overwrites with stale art).
- `calibrate_bounds.py` — affine recalibration from in-game landmarks.
- `DYNAMICMAPS_REPO` defaults to `D:\Git\SPT-DynamicMaps`; override in `py\local_config.py`.

## Key files

| File | Role |
|------|------|
| `Plugin/Plugin.cs` | Entry point, thin `SPTMapBehaviour` forwarding to controller |
| `Plugin/SPTMapController.cs` | All render/input logic (plain C#, not IL2CPP-registered) |
| `Plugin/Data/MapDef.cs`, `MapMarker.cs` | Data models |
| `Plugin/Utils/MapUtils.cs` | JSON loading, texture cache |
| `Plugin/Utils/MarkerManager.cs` | Marker list |
| `Plugin/Utils/GameUtils.cs` | Raid detection, player categorization |
| `Plugin/Utils/MathUtils.cs` | `Rotate90Multiple`, coordinate math |
| `Plugin/Utils/QuestDebugPanel.cs`, `PrestigeDebugPatches.cs` | F9 dev panel (quest finish, prestige, give item) |
| `Plugin/Utils/EnemyEspRenderer.cs` | ESP overlay + shared world-to-GUI helpers |
| `Plugin/Utils/BulletHitPopupRenderer.cs` | Hit damage popups (`OnDamageReceived`) |
| `Plugin/Utils/DbPostPatcherClient.cs` | Client for DbPostPatcher HTTP routes |
| `Plugin/Config/Settings.cs` | BepInEx `ConfigEntry` bindings |
| `Plugin/DynamicMarkers/{MapFeatures,Units,Items}/` | Marker providers |

## Hard rules

- **Position rotation:** raw world positions are rotated by `MathUtils.Rotate90Multiple(pos, CoordinateRotation)` before comparing to pre-rotated `Bounds` — keep consistent everywhere.
- **Harmony:** register patches by explicit type, never `PatchAll(assembly)`.
- **Scan cost:** no per-frame/timer world scans while the map is closed. Expensive rescans go through `RefreshNow()` (fired when M opens the map); raid-start retries must be bounded (`MaxRetrySeconds`), never "retry until found". Only `UnitMarkerProvider` polls per-frame.
- **No `?.`/`??` on `UnityEngine.Object`-derived types** (`GameObject`, `Component`, `Player`, `GameWorld`, UI screens...) — bypasses Unity's fake-null check. Use explicit `if (x == null)`. Plain `Il2CppSystem.Object` types (`Profile`, `Item`, ...) are fine. When unsure, use `if`.
- **No LINQ in raid/hot-path code** — use plain loops. One-shot menu code is fine.
- **Cached `UnityEngine.Object`s** (e.g. `Texture2D`): set `HideFlags.DontUnloadUnusedAsset` and treat a fake-null cache hit as a miss.
- **Il2Cpp delegates:** use `Il2CppSystem.Action<>`/`Func<>`, except where the generated delegate type has an implicit operator from `System.Action<...>` (e.g. `Player.DamageDelegate` — subscribe via `add_OnDamageReceived(typedLocal)`).
- Don't use `ActiveHealthController.ApplyDamageEvent` (non-blittable, `ConvertDelegate` throws).
- Don't force `PrestigeController.CanUpgrade` via patch (per-frame exception flood).
- SPT server responses are zlib-compressed and byte-shuffled unless path starts with `/singleplayer/` (response only, not request).
