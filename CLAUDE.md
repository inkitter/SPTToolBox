# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Workflow

Before making any code change (edits, new files, running build/asset scripts), state in 1-2 sentences what you're about to change and why, then proceed. This gives the user a chance to interrupt if the approach is wrong before time is spent on it. Trivial read-only actions (Read/Grep/Glob, `git status`) don't need this preamble.

## ENV
"python" to run python. Path to d:\git\uv, and then use uv add to install new package.
use bun, not node.
 
## What this is

This repo (SPTToolBox) hosts two independent tools for SPT (Single Player Tarkov):

- **SPTMap** (`Plugin/`) — a BepInEx IL2CPP client plugin that renders an in-game minimap using Unity's legacy IMGUI system. It is a rewrite of an earlier uGUI-based dynamic maps mod, deliberately avoiding uGUI and Unity.VectorGraphics to sidestep IL2CPP generic-method-unstripping crashes and BSG UI-version churn.
- **DbPostPatcher** (`Server/DbPostPatcher/`) — a server-side C# mod that applies small, targeted JSON patches to SPT's in-memory database once at boot (items, globals, profile templates, repair config, ...), instead of hand-editing the raw database JSON files on disk. See [DbPostPatcher](#dbpostpatcher-server-mod) below.

They deploy to different places (`BepInEx/plugins/sptmap/` vs `user/mods/DbPostPatcher/`) and don't depend on each other — treat them as separate projects that happen to share a repo.

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

## Release (GitHub)

Releases are published from https://github.com/inkitter/SPTToolBox via `gh`. **Before publishing, ask the user whether to bump the version number** (tag/csproj `<AssemblyName>`-adjacent version, release title, zip filename) rather than assuming a same-version republish.

`gh` is installed via winget but isn't on this session's PATH by default — call it by full path: `& "C:\Program Files\GitHub CLI\gh.exe" ...` (PowerShell) or add it to PATH for bash. Confirm auth first: `gh auth status`.

To build and publish (or republish) a release zip for version `vX.Y.Z`:

1. Close the game, `dotnet build` from `Plugin\` (PostBuild deploys to `$(TarkovDir)BepInEx\plugins\sptmap\` - see above).
2. Zip that deployed folder under a `BepInEx/plugins/sptmap/...` path prefix (so it extracts by merging straight into an SPT install root) - stage it into a scratch dir first, e.g.:
   ```powershell
   mkdir <stage>\BepInEx\plugins\sptmap
   cp -r "$TarkovDir\BepInEx\plugins\sptmap\." <stage>\BepInEx\plugins\sptmap\
   # zip <stage> contents (with the BepInEx\... prefix preserved) to SPTMap-vX.Y.Z.zip
   ```
3. If replacing an existing tag (same-version republish), move it to the current commit rather than leaving it stale:
   ```
   git tag -d vX.Y.Z
   git tag vX.Y.Z -m "vX.Y.Z - built for <compat table entry>"
   git push origin :refs/tags/vX.Y.Z
   git push origin vX.Y.Z
   ```
4. Delete the old release (if any) and create the new one with the zip attached:
   ```
   gh release delete vX.Y.Z --repo inkitter/SPTToolBox --yes --cleanup-tag=false
   gh release create vX.Y.Z --repo inkitter/SPTToolBox --title "SPTMap vX.Y.Z" --notes "..." SPTMap-vX.Y.Z.zip
   ```

## Asset build scripts (`py\`)

Both scripts require `cairosvg` and `pillow`. Install via `uv add <pkg>` in `D:\Git\uv` (the project's Python env), not bare `pip install`.

`py\build_maps.py` and `py\update_labs.py` read from a sibling checkout of the old SPT-DynamicMaps project, via `DYNAMICMAPS_REPO`. Default (if no `py\local_config.py`) is `D:\Git\SPT-DynamicMaps`; to override, copy `py\local_config.py.example` to `py\local_config.py` (gitignored) and set `DYNAMICMAPS_REPO` there.

- **`py\update_maps_floors.py`** — source of truth for the 9 tarkov.dev-backed maps (Customs, Factory, GroundZero, Interchange, Lighthouse, Reserve, Shoreline, Streets, Woods). Fetches `the-hideout/tarkov-dev` `maps.json` + live SVGs fresh each run. Rerun to pick up future tarkov.dev changes.
- **`py\build_maps.py`** — legacy, reads the vendored SVG pack under `<DYNAMICMAPS_REPO>\Plugin\release\...\Maps\`. Only relevant for Labs and Labyrinth (not covered by `update_maps_floors.py`). **Do not run it for the other 9 maps** — it overwrites the tarkov.dev-sourced data with stale vendored art.
- **`py\calibrate_bounds.py`** — affine recalibration tool. Use with in-game Keypad `.` landmarks (logged by `Plugin.cs LogLandmark`) if a map's bounds drift from ground truth.

Run these from the repo root (e.g. `python py\update_maps_floors.py`) — their output paths resolve relative to the script's own location, one level up. After running either asset script, `dotnet build` from `Plugin\` to redeploy.

## DbPostPatcher (server mod)

`Server/DbPostPatcher/` is a standalone C# server mod (its own `.csproj`, unrelated to `Plugin/`) that patches SPT's in-memory database once at boot, replacing what used to be a one-off TypeScript script (`d:\Git\SPT\ZServer_mod\editor\patch.ts`) that hand-edited the raw database JSON files on disk — fragile across SPT updates and left no record of *why* a value was changed.

### Build & deploy

```powershell
# From Server\DbPostPatcher\
dotnet build
```

`dotnet build` deploys `DbPostPatcher.dll` + every `patches\**\*.json` file into `$(SptRuntimeDir)\user\mods\DbPostPatcher\`. `SptRuntimeDir` defaults to `D:\Game\SPT5\SPT_Runtime` in the tracked csproj — override per machine in `Server\DbPostPatcher\DbPostPatcher.csproj.user` (gitignored), same pattern as `Plugin\SPTMap.csproj.user`.

The csproj references the server's own already-built DLLs (`SPTarkov.Server.Core.dll` etc. under `SptRuntimeDir`) via `<Reference>`+`HintPath`, **not** `ProjectReference` to `server-csharp`'s source projects — a `ProjectReference` would drag that entire (large) solution into every build here.

### How patching works

- Each change is its own small JSON file under `patches\`, written as a restricted form of [RFC 6902 JSON Patch](https://datatracker.ietf.org/doc/html/rfc6902) (`add`/`replace`/`remove` + JSON Pointer paths), applied against the serialization of just the one object the patch targets (`table`+`id`) — not the whole database — so a patch only ever touches the fields it lists. See [`patches/README.md`](Server/DbPostPatcher/patches/README.md) for the full format, including the `replace` (field must already exist, fails loudly if not) vs `add` (create-or-overwrite) distinction and why it matters.
- `PatchLoaderMod` (`IOnLoad`, `OnLoadOrder.Preload`) reads `patches.enabled.json`'s `enabled` list and applies just those files. That config's `available` list is regenerated by the mod on every boot from whatever `*.json` files actually exist under `patches\` (skipping `_`-prefixed reference-only files) — it's never hand-maintained, so it can't go stale.
- `PatchTableRegistry` maps a patch's `table` name to the live object: `items`/`profileTemplates` write straight into `TemplateTable`'s dictionaries; `globals`/`config:repair` are DI singletons other services already hold references to, so those go through a reflection-based property copy instead of a reference swap (works even on `record`'s `init`-only properties — `init` is a C#-compiler-only restriction, not enforced by the CLR, so `PropertyInfo.SetValue` still works post-construction).
- A failure on any single patch operation (missing field, unknown id, bad path) is logged and only that operation is skipped — never crashes the server, never blocks the rest of that file or other patch files. **Always run the server once after adding a new patch file** — see [`NOTES.md`](Server/DbPostPatcher/NOTES.md) #7 for two real bugs (a field that didn't exist where expected, a missing required field) this caught only by actually running it, not by reading the JSON.

### Custom HTTP routes ("Give Item")

`Routing/GiveItemRouter.cs` registers a few custom routes under `/singleplayer/dbpostpatcher/...` (ping/catalog/give-item) that back SPTMap's F9 "Give Item" panel — mails a hideout-slot item into the live profile via `MailSendService`, so it shows up immediately through the game's normal mail-notification push, no server restart or relog needed. `Plugin/Utils/DbPostPatcherClient.cs` is the client side: it gets its session id and the backend URL off the game's own launch command line (`-token=`/`-config={"BackendUrl":...}`, visible in `BepInEx/LogOutput.log`) via plain `Environment.GetCommandLineArgs()` — no IL2CPP object access needed for that at all. The F9 panel pings first and shows a clear "backend not detected" message if this mod isn't deployed on the connected server, rather than failing silently on click.

**Two things every SPT server response requires, not just this route** (see [`NOTES.md`](Server/DbPostPatcher/NOTES.md) #8 for the full investigation): every response body is always zlib-deflate compressed (`System.IO.Compression.ZLibStream` on the client side to read it back), and additionally byte-shuffled (a reproducible permutation, not real crypto) unless the path starts with `/singleplayer/...` — that prefix only exempts the *response* from shuffling, **not the request**, so a route needing a request body still needs to either avoid sending one (this router bakes item ids into per-entry static routes instead of a POST body) or account for `RequestEncryptionUtil.DeShuffleAsync` running on it server-side. Verify a new route by starting `SPT.Server.exe` locally and hitting it with `curl -k` + manual zlib decompression — no game client needed.

## SPTMap architecture

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

`MarkerManager.cs` holds a flat `List<MapMarker>`. Providers (`ExtractMarkerProvider`, `SecretMarkerProvider`, `TransitMarkerProvider`, `DoorMarkerProvider`, `OtherPlayersMarkerProvider` (also covers corpses), `QuestMarkerProvider`, `BTRMarkerProvider`, `AirdropMarkerProvider`, `WishlistMarkerProvider`, `HiddenStashMarkerProvider`, `BackpackMarkerProvider`) add/remove entries. `SPTMapController.DrawMarkers` iterates every `OnGUI`, each draw wrapped in its own `try/catch` so one bad marker can't blank the map. Marker positions are raw pre-rotation world space — `DrawMarkers` applies `CoordinateRotation` the same way the player marker does.

All providers are patch-free by design: extracts/secrets/transit/doors resolve their source lists straight off engine controllers (`ExfiltrationController`, `TransitController`) or a scene scan (`Object.FindObjectsOfType<Door>()`/`Object.FindObjectsOfType<LootableContainer>()`) at `OnRaidStart`, retried each frame until populated; BTR/airdrops/wishlist/backpack poll a live `Tick()` each frame/on a short timer instead (`GameUtils.GetBTRView()`, `Object.FindObjectsOfType<AirdropSynchronizableObject>()`, `GameWorld.LootList`) since those can appear/disappear/get-picked-up mid-raid. `AirdropMarkerProvider`'s live rescan is a deliberate departure from the predecessor project, which relied on a Harmony patch on `ClientAirDrop.CloseParachute` (only fires once a crate has already landed) — the rescan approach should also surface a crate while it's still under its parachute, but this hasn't been confirmed in-game yet.

Three markers ported from the predecessor project's Harmony-patch-based implementations, redone patch-free per this project's convention (see `git log` for the porting commits if the reasoning below needs more detail):
- `WishlistMarkerProvider` — items on the ground matching `Profile.WishlistManager.GetWishlist()` (no server call, already loaded on the profile). Rescans `GameWorld.LootList` on a timer instead of the predecessor's `GameWorld.LootList` one-shot scan, so a marker disappears once another player/bot loots the item.
- `HiddenStashMarkerProvider` — a fixed set of `LootableContainer`s per map, identifiable only by known GameObject name prefixes (`scontainer_wood_CAP`, `scontainer_Blue_Barrel_Base_Cap` — credit to the predecessor project/RaiRai for finding these). Predecessor hooked `GameWorld.OnGameStarted`; here it's a populated-once-with-retry `Object.FindObjectsOfType<LootableContainer>()` scan, same shape as `DoorMarkerProvider`.
- `BackpackMarkerProvider` — the *local player's own* dropped/thrown backpack only (not corpses', not other players', not static loot backpacks). Predecessor hooked `PlayerInventoryController.ThrowItem`; here it polls `Player.Inventory.Equipment.GetSlot(EquipmentSlot.Backpack)` each frame (cheap - no scene/list scan) and only falls back to a throttled `GameWorld.LootList` scan once the slot goes from occupied to empty, to find the matching `LootItem` by `Item.Id`.

Raid start/end is edge-detected in `SPTMapBehaviour.Update()` off `GameUtils.IsInRaid()` and drives all providers' `OnRaidStart`/`OnRaidEnd` + `MarkerManager.Clear()`.

### Quest debug panel

`Plugin/Utils/QuestDebugPanel.cs` — dev-only tool, `F9` toggles a floating IMGUI panel listing every quest (tabbed Incomplete/Not started/Completed) with a "Finish" button per quest. The button calls `QuestController.TryInstantFinishQuest` (the same public entry point the native Tasks-screen "Complete quest" button uses), so reward grants/condition bookkeeping/chain unlocks all fire for real — it's not a fake completion. Works from the main menu or in a raid, via `ItemUiContext.Instance.QuestController` (persistent, not Player-bound). Exists to unblock testing later quests in a chain when an earlier quest's own completion trigger is bugged, without re-running a whole raid. Should be gated behind a debug/dev config flag (or removed) before treating this as a release build for other players, since it's a completion cheat.

The same panel has a Prestige section backed by `Plugin/Utils/PrestigeDebugPatches.cs`. First attempt forced `PrestigeController.CanUpgrade` to `true` via Harmony postfix so the native tab's own gate would pass — but that getter is polled every frame by other native UI, and forcing it true made that other code actually execute a prestige-data path the local SPT server doesn't implement, flooding the log with `NotImplementedException` every frame. Replaced with direct calls that skip the gate instead of forcing it: "Show Prestige Screen" finds the open `InventoryScreen` and calls `_prestigeScreen.Show(profile, prestigeController, inventoryController, session)` directly (the same call the native tab's button click ends up making — requires Inventory to already be open); "Click Obtain Prestige" then calls `PrestigeScreen.ObtainPrestigeHandler()` directly (the button's own click handler), bypassing the button's disabled/interactable state. `PrestigeGlobalsLoadPatch` (flips `GameModeDescriptor.GameMode` to `Regular` for the duration of `TarkovApplication.GlobalsDataLoader.Load` so PvE sessions still populate `PrestigeTemplate` data) is the one remaining always-on Harmony patch here — it's a one-shot startup patch, not per-frame, so it doesn't have the same footgun. Confirmed working end-to-end in-game (2026-09-15): Show Prestige Screen → Click Obtain Prestige successfully triggers the native prestige flow.

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
| `Plugin/Utils/QuestDebugPanel.cs` | F9 dev panel, instant quest completion, Prestige debug tools |
| `Plugin/Utils/PrestigeDebugPatches.cs` | Direct-invoke Prestige screen/button helpers backing the F9 panel |
| `Plugin/Config/Settings.cs` | BepInEx `ConfigEntry` bindings |
| `Plugin/DynamicMarkers/` | Marker providers (extracts, secrets, transit, doors, players/corpses, quests, BTR, airdrops, wishlist, hidden stashes, dropped backpack) |

## Current state (as of 2026-09-10)

- All 11 maps working with multi-floor support (Labs/Labyrinth single-level, stale art/bounds — no tarkov.dev SVG available).
- Markers ported from the predecessor project but **not yet verified in-game**. Things to check first: extract marker status colors update correctly; other players/corpses appear and clean up on death/raid-end without leaking; quest markers are positioned correctly and don't spam-log errors from `QuestUtils`'s reflection-based loot-item lookup (not exercised yet in this project).
- Secret/transit/BTR markers (added 2026-09-14) are unverified in-game. Airdrop markers (also added 2026-09-14) use a live `FindObjectsOfType<AirdropSynchronizableObject>` rescan instead of the predecessor's landing-only Harmony patch, specifically so a crate shows up while still under its parachute — whether that object is actually present/positioned correctly pre-landing hasn't been confirmed yet.
- Map calibration: 9 maps refreshed from tarkov.dev and confirmed current as of 2026-09-10 (`the-hideout/tarkov-dev`'s `maps.json` + live SVGs, same coordinate convention as `Bounds`/`CoordinateRotation`/`GameBounds`). Labs/Labyrinth have no `svgPath` on tarkov.dev (raster-tile-only) and stay on old vendored data. If a map still doesn't line up with in-game terrain after a tarkov.dev refresh, fall back to manual affine recalibration via `calibrate_bounds.py`; proportionally expanding `Bounds` is the last-resort option if neither source is available.
- A few floors have no distinct art on tarkov.dev at all (tile-only, not SVG) and are skipped rather than guessed at: Customs' 4th floor and Reserve's above-ground floors except Bunkers. Those areas just show the Ground level image underneath — a readability gap, not a correctness one.
- Some display constants (mini-map size, zoom speed) are exposed via `Settings.cs` `ConfigEntry`s (visible as sliders in BepInEx ConfigurationManager); box sizing and key bindings are still hardcoded in `Plugin.cs`.
- No POI labels yet.
- 2026-09-15: added `WishlistMarkerProvider`, `HiddenStashMarkerProvider`, `BackpackMarkerProvider` (see Markers section above) - **not yet verified in-game**. `backpack.png` is a placeholder icon generated with PIL, not a real asset. Also reworked the F9 panel's Prestige tools from a `CanUpgrade`-forcing bypass (caused per-frame `NotImplementedException` spam once enabled) to direct `PrestigeScreen` method calls - this part **is** confirmed working in-game.
- 2026-09-15: fixed a severe FPS drop (~10 fps) caused by `HiddenStashMarkerProvider`'s initial version - the populated-once-with-retry `_populated` flag only flipped true once a hidden stash was actually *found*, so on any map without those specific container names, `Object.FindObjectsOfType<LootableContainer>()` (a full-scene scan) re-ran every single frame for the rest of the raid. `TransitMarkerProvider` had the identical bug shape (gating on result count instead of "did the scan run") and got the same fix. Both providers now use a bounded retry window (`MaxRetrySeconds`/unconditional-after-first-scan) instead of "retry forever until something is found" - `DoorMarkerProvider` still has the same theoretical shape but wasn't touched since Door components are realistically never absent from a map's scene, unlike transit points/hidden stashes.
- 2026-09-15: `ExtractMarkerProvider` gained a `Tick()` (5s rescan) alongside its existing `OnRaidStart()`. Reported bug: a raid-time-windowed extract (e.g. a train extract that only arrives partway through the raid, then leaves again) never appeared at all - its `ExfiltrationPoint` isn't `isActiveAndEnabled` at raid start, so it's filtered out of the initial scan and, unlike an ordinary closed extract, never even enters `_markers` to get live `OnStatusChanged` updates once it does activate. `Tick()` periodically re-scans and idempotently adds any newly-active extract; once added, its color already updates live via the existing `OnStatusChanged` subscription (this also covers condition-gated extracts like a power-switch requirement flipping `UncompleteRequirements` → open - no separate fix needed there, that path already worked as long as the extract was in `_markers` to begin with).

## IL2CPP interop notes

- `Texture2D` objects held only in a static C# dictionary get destroyed by the engine's unused-asset cleanup on raid load. Fix: set `HideFlags.DontUnloadUnusedAsset` at load time **and** treat a Unity fake-null cache hit as a cache miss (`MapUtils.GetTexture`). Apply the same treatment to any new cached `UnityEngine.Object`.
- Use `Il2CppSystem.Action<>` / `Il2CppSystem.Func<>` (not `System.`) when wiring Il2Cpp delegates (event handlers, callbacks on EFT types).
- A `GameObject` created during `BasePlugin.Load()` gets destroyed by the engine's own scene transition shortly after, even with `DontDestroyOnLoad` — this is why the render component is instead parented to `CommonUI`'s GameObject via the Harmony postfix described above.
- **Never use `?.`/`??` on a `UnityEngine.Object`-derived type** (`GameObject`, `Component`, `MonoBehaviour` and any IL2CPP-bound subclass - `Player`, `GameWorld`, `Door`, `LootItem`, `TransitPoint`, UI screens like `InventoryScreen`/`PrestigeScreen`, etc.). Unity overrides `==`/`!=`/`true`/`false` on `UnityEngine.Object` to treat a destroyed-but-not-yet-GC'd native object as null (the same fake-null pattern as the `Texture2D` bullet above) - `?.`/`??` compile to a raw CLR null check that bypasses that override entirely, so `myPlayer?.Something()` can still invoke a method on an already-destroyed native object instead of safely no-oping. Use an explicit `if (x == null) { return; }` every time instead - no exceptions, even when "it can't actually be null here." Plain `Il2CppSystem.Object` types that are *not* `UnityEngine.Object` (`Profile`, `Item`, `WishlistManager`, `InventoryController`, `InventoryEquipment`, `Slot`, etc. - anything that isn't a scene object/component) don't have this override and are fine with `?.`/`??`. When unsure which category a type falls into, play it safe and use the explicit `if`.
- **No LINQ in raid/hot-path code** (`Tick`/`OnRaidStart` marker-provider scans, anything running every frame or on a short timer during a raid). LINQ's enumerators/closures allocate garbage that doesn't get cleaned up properly in this environment - use plain `foreach`/manual loops instead. Menu/out-of-raid, one-shot code is fine with LINQ.
