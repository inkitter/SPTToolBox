# PROMPT.md

Detailed background, design rationale, and history moved out of `CLAUDE.md` to keep that file short. Read the relevant section here when working on that area.

## Release procedure (GitHub)

Releases are published from https://github.com/inkitter/SPTToolBox via `gh`. `gh` is installed via winget but isn't on PATH by default — call it as `& "C:\Program Files\GitHub CLI\gh.exe" ...`. Confirm auth first: `gh auth status`.

1. Close the game, `dotnet build` from `Plugin\` (PostBuild deploys to `$(TarkovDir)BepInEx\plugins\sptmap\`).
2. Zip that deployed folder under a `BepInEx/plugins/sptmap/...` prefix (so it extracts straight into an SPT install root) — stage it into a scratch dir first:
   ```powershell
   mkdir <stage>\BepInEx\plugins\sptmap
   cp -r "$TarkovDir\BepInEx\plugins\sptmap\." <stage>\BepInEx\plugins\sptmap\
   # zip <stage> contents (with the BepInEx\... prefix preserved) to SPTMap-vX.Y.Z.zip
   ```
3. If replacing an existing tag (same-version republish), move it to the current commit:
   ```
   git tag -d vX.Y.Z
   git tag vX.Y.Z -m "vX.Y.Z - built for <compat table entry>"
   git push origin :refs/tags/vX.Y.Z
   git push origin vX.Y.Z
   ```
4. Delete the old release (if any) and create the new one:
   ```
   gh release delete vX.Y.Z --repo inkitter/SPTToolBox --yes --cleanup-tag=false
   gh release create vX.Y.Z --repo inkitter/SPTToolBox --title "SPTMap vX.Y.Z" --notes "..." SPTMap-vX.Y.Z.zip
   ```

## DbPostPatcher details

Replaces a one-off TypeScript script (`d:\Git\SPT\ZServer_mod\editor\patch.ts`) that hand-edited raw database JSON on disk — fragile across SPT updates and left no record of *why* a value changed.

The csproj references the server's already-built DLLs (`SPTarkov.Server.Core.dll` etc. under `SptRuntimeDir`) via `<Reference>`+`HintPath`, **not** `ProjectReference` to `server-csharp` source — that would drag the whole large solution into every build.

### How patching works

- Each change is its own small JSON file under `patches\`, a restricted form of RFC 6902 JSON Patch (`add`/`replace`/`remove` + JSON Pointer), applied against the serialization of just the one object targeted (`table`+`id`). See [`patches/README.md`](Server/DbPostPatcher/patches/README.md) — including `replace` (field must exist, fails loudly) vs `add` (create-or-overwrite).
- `PatchLoaderMod` (`IOnLoad`, `OnLoadOrder.Preload`) applies files listed in `patches.enabled.json`'s `enabled`. Its `available` list is regenerated every boot from existing `*.json` under `patches\` (skipping `_`-prefixed reference files) — never hand-maintained.
- `PatchTableRegistry` maps `table` → live object: `items`/`profileTemplates` write into `TemplateTable` dictionaries; `globals`/`config:repair` are DI singletons, so they go through reflection-based property copy instead of reference swap (works on `init`-only properties — `init` isn't enforced by the CLR).
- A failed operation is logged and only that op is skipped. See [`NOTES.md`](Server/DbPostPatcher/NOTES.md) #7 for two bugs only caught by actually running the server.

### Custom HTTP routes ("Give Item")

`Routing/GiveItemRouter.cs` registers routes under `/singleplayer/dbpostpatcher/...` (ping/catalog/give-item) backing SPTMap's F9 "Give Item" panel — mails an item into the live profile via `MailSendService`, so it appears immediately via normal mail push. `Plugin/Utils/DbPostPatcherClient.cs` is the client: session id and backend URL come from the game's launch command line (`-token=` / `-config={"BackendUrl":...}`) via `Environment.GetCommandLineArgs()`. The panel pings first and shows "backend not detected" if the mod isn't deployed.

Every SPT server response (see `NOTES.md` #8):
- is zlib-deflate compressed (`System.IO.Compression.ZLibStream` client-side);
- is byte-shuffled unless the path starts with `/singleplayer/...` — that prefix exempts only the *response*, **not the request**. So a route needing a request body must avoid one (this router bakes item ids into per-entry static routes) or handle `RequestEncryptionUtil.DeShuffleAsync` server-side.

Verify a new route by starting `SPT.Server.exe` locally and hitting it with `curl -k` + manual zlib decompression.

## SPTMap architecture details

### Plugin lifecycle

`BasePlugin` isn't a `MonoBehaviour` under IL2CPP BepInEx — no `OnGUI`/`Update`. The render loop lives on `SPTMapBehaviour` (registered via `ClassInjector`, attached to `CommonUI`'s GameObject by a Harmony postfix on `CommonUI.Awake`). CommonUI persists across menu/hideout/raid; a self-created root GameObject gets destroyed on scene transition even with `DontDestroyOnLoad`.

`SPTMapBehaviour` only forwards `Update`/`OnGUI` into `SPTMapController` (plain C# class). Reason: `ClassInjector.RegisterTypeInIl2Cpp` scans every method on a registered type and logs noisy "Method unstripping failed" warnings for signatures mentioning plain C# types (`MapDef`, `MapMarker`, ...).

`Plugin.Load()` checks the game build (`FileVersionInfo` on the game exe) against `Plugin.BuiltForVersion` as a `>=` floor, not an exact match — EFT bumps build on every patch and this plugin only touches generic surfaces. Only an *older* game is refused (logged + added to `IL2CPPChainloader.Instance.DependencyErrors`). The idle "SPTMap Loaded" label shows `BuiltForVersion`.

Harmony patches are registered by explicit type — `PatchAll(assembly)` caused a total render failure.

### Map data flow

1. `update_maps_floors.py` fetches tarkov.dev SVGs → PNGs via `cairosvg`/`PIL`, bakes `CoordinateRotation` into both image and `MapDef.Bounds` → writes `Plugin/Resources/Maps/<Name>/<Name>.json` + `<Name>.png` per floor.
2. `MapUtils.cs` loads them into `MapDef` (textures cached with `HideFlags.DontUnloadUnusedAsset`).
3. `SPTMapController` renders minimap (top-right) and full map (hold M) via `GUI.DrawTexture`.
4. Player raw world position is rotated by `MathUtils.Rotate90Multiple(pos, CoordinateRotation)` before mapping against pre-rotated `Bounds` — must be consistent everywhere.

### Multi-floor

Each `MapDef` has a `Levels` list (empty = single-level). Each level has its own image + 3D `GameBounds` boxes. Auto-detect (Keypad 4) picks the smallest-volume matching box. Keypad 7/1 step floors; 9/3 cycle map defs; 6 returns to auto.

### Markers

`MarkerManager.cs` holds a flat `List<MapMarker>`. Providers under `Plugin/DynamicMarkers/`:
- `MapFeatures/` — Extract, Transit, Door, Switch (switches from live `FindObjectsOfType<Switch>`).
- `Units/` — `UnitMarkerProvider` (players, corpses, BTR gunner — just an invincible bot `Player`).
- `Items/` — Airdrop (rescans `AirdropSynchronizableObject`, surfaces while still under parachute), `ItemMarkerProvider`, `LootableContainerMarkerProvider`.
- `QuestMarkerProvider` at top level.

`DrawMarkers` wraps each draw in `try/catch`. Marker positions are raw pre-rotation world space.

`UnitMarkerProvider` keeps a `TrackedPlayerState` per player refreshed on `Settings.OtherPlayersPollIntervalMs`. Deliberately poll-based: `Player.OnDead` and `GameWorld.UnregisterPlayer` raced (unregister happened before the `OnDead` postfix), so deaths could be missed.

**Scan-cost model:** expensive rescans (quest, items, airdrops, extract re-checks) run only via `RefreshNow()` once on the frame M opens the full map. `OnRaidStart` population retries each frame within a bounded `MaxRetrySeconds` window (unconditional after first scan). `UnitMarkerProvider` is the only genuine per-frame poll.

`ItemMarkerProvider` does one pass over `GameWorld.LootList` per rescan, diffed against an `IItemRule` list — add a new loose-loot feature as a new `IItemRule`, not a new provider. `ItemMarkerProvider` and `LootableContainerMarkerProvider` are hardcoded off (`SPTMapController.ItemMarkersDisabled` / `LootableContainerMarkersDisabled` = `true`; the latter also needs `Settings.ShowLootableContainers`).

Removed providers (see `git log`): `WishlistMarkerProvider`, `HiddenStashMarkerProvider`, `SecretMarkerProvider`, `BackpackMarkerProvider`.

Raid start/end is edge-detected in `SPTMapController.Update()` off `GameUtils.IsInRaid()`, driving providers' `OnRaidStart`/`OnRaidEnd` + `MarkerManager.Clear()`.

### Hit damage popups

`BulletHitPopupRenderer.cs` (`Settings.ShowHitDamageNumbers`, on by default) subscribes to each tracked player's `Player.OnDamageReceived`; the callback only enqueues a value-type struct, spawning happens on next `Tick()`. It replaced (2026-09-22) a poll-based `HitDamagePopupRenderer` that diffed `HealthController.GetBodyPartHealth` every frame — suspected cause of an intermittent `AccessViolationException`. Don't use `ActiveHealthController.ApplyDamageEvent`: its `DamageInfo` isn't blittable and `ConvertDelegate` throws.

Subscribe with `player.add_OnDamageReceived(handler)` using an explicitly-typed `System.Action<...>` local — not `+= lambda`; the implicit conversion to `DamageDelegate` only resolves in method-argument/explicit-cast context.

### Quest debug panel (F9)

`QuestDebugPanel.cs` — dev-only floating panel listing quests (Incomplete/Not started/Completed) with a "Finish" button calling `QuestController.TryInstantFinishQuest` (same as the native Tasks-screen button, so rewards/unlocks fire for real). Uses `ItemUiContext.Instance.QuestController`. Should be gated/removed before a release for other players.

Prestige section (`PrestigeDebugPatches.cs`): forcing `PrestigeController.CanUpgrade` true via postfix caused a per-frame `NotImplementedException` flood (other UI polls it and hit unimplemented server paths). Instead: "Show Prestige Screen" calls `_prestigeScreen.Show(profile, prestigeController, inventoryController, session)` on the open `InventoryScreen` (Inventory must be open); "Click Obtain Prestige" calls `PrestigeScreen.ObtainPrestigeHandler()` directly. `PrestigeGlobalsLoadPatch` (flips `GameModeDescriptor.GameMode` to `Regular` during `TarkovApplication.GlobalsDataLoader.Load` so PvE populates `PrestigeTemplate`) is the one remaining always-on patch — one-shot, not per-frame. Confirmed working 2026-09-15.

## Map calibration notes

- 9 maps refreshed from tarkov.dev, confirmed current 2026-09-10. Labs/Labyrinth have no `svgPath` on tarkov.dev and stay on old vendored data.
- If a map doesn't line up after a refresh: manual affine recalibration via `calibrate_bounds.py` (using in-game Keypad `.` landmarks logged by `LogLandmark`); proportionally expanding `Bounds` is last resort.
- Floors with no SVG art on tarkov.dev are skipped: Customs 4th floor, Reserve above-ground floors except Bunkers — they show the Ground image.

## History / status log

- Some display constants (mini-map size, zoom speed) are `ConfigEntry`s in `Settings.cs`; box sizing and key bindings are still hardcoded in `Plugin.cs`. No POI labels yet.
- 2026-09-15: fixed ~10 fps drop from an early hidden-stash provider whose "populated" flag only flipped once a match was found, so a full-scene `FindObjectsOfType` re-ran every frame on maps without a match. Provider since removed; the bounded-retry pattern survives.
- 2026-09-16: scan-cost rework (see Markers). `DynamicMarkers/` reorganized into `MapFeatures/`, `Units/`, `Items/`.
- Still unverified in-game: quest markers, extract status-color updates, unit marker cleanup on death/raid-end, `ItemMarkerProvider`/`LootableContainerMarkerProvider`.
- 2026-09-22: `BulletHitPopupRenderer` replaced the polling `HitDamagePopupRenderer` (deleted).
