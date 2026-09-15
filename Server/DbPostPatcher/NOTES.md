# Design notes / open questions (draft)

Migrating everything from `ZServer_mod/editor/patch.ts` surfaced a few things the plain
per-`table`+`id` JSON Patch format (see `patches/README.md`) doesn't cleanly cover yet.
Flagging them here instead of guessing at a solution.

## 1. Why in-memory patching sidesteps the old idempotency guard

`patch.ts` read `templates/profiles.json` off disk, mutated it, and wrote it back. Re-running
the script re-read its *own previous output*, so the THICC-container injection needed an
`if (!char.Inventory.items.find(...))` guard to avoid appending duplicates on every run.

This engine never touches the files on disk - the C# server loads `TemplateTable` fresh into
memory on every boot, and the patch engine runs once per boot against that fresh copy. So a
plain `add` (append) is safe to leave unguarded: it can never see its own prior output. This
is a meaningful behavior difference from the old script, worth confirming end-to-end once the
engine exists (start the server twice, check the container count didn't grow).

## 2. `prestige.json` - dropped, not needed

The old `patch_prestige` ("clear conditions from every element", awkward to express as fixed
JSON Pointer paths since it touches however many elements the list happens to have) is moot:
prestige gating is now bypassed directly through the F9 debug panel's Prestige tools
(`Plugin/Utils/PrestigeDebugPatches.cs` in the SPTMap client, confirmed working in-game
2026-09-15). Not porting this to the server patch engine.

## 3. `repair.json` - same registry pattern as the template DB, just a different DI type

Confirmed by reading `RepairConfig.cs`: it's a plain DI-injectable singleton
(`SPTarkov.Server.Core.Models.Spt.Config.RepairConfig`, injected directly into
`RepairService` and others), same shape as `TemplateTable`/`GlobalTable` - mutable properties,
not read off disk by the patch engine. So this isn't actually a different mechanism, just a
different registry entry (`"table": "config:repair"` -> resolves to the injected `RepairConfig`
instance instead of `TemplateTable`). `patches/config/repair.json` ports `patch_config`'s
`applyRandomizeDurabilityLoss = false`. Other config classes (see `ConfigTypes.cs` for the
full list) can be added to the registry the same way as needed.

## 4. `patch_custom` (WTT-ContentBackport headphone/mag/sniper overrides) is out of scope

Those are a *third-party mod's own* JSON files (`user/mods/WTT-ContentBackport/db/...`), not
the SPT template database - this engine only reaches into `TemplateTable`/`GlobalTable` via DI,
it has no reason to know about another mod's private config files. If this backport-tuning is
still wanted, it stays as a small standalone script (can still borrow this repo's `PatchFile`
loader/applier code if useful, just pointed at that file instead of a DI-injected table).

## 5. Everything else already ported (disabled by default - not yet in `patches.enabled.json`)

- `patches/items/goldenstar.json`, `gingy.json`, `gamma-container.json`, `sicc.json`,
  `ammobox.json` - active in `patch.ts` (`patch_items`).
- `patches/profileTemplates/unheard-usec-health.json` - `setHealth` portion of `patch_tplprofile`.
- `patches/profileTemplates/unheard-usec-hideout-containers.json` - THICC/SICC portion of
  `patch_tplprofile`; see caveats in that file's own `description` field before enabling.
- `patches/globals/stimulant-buffs.json`, `skill-settings.json` - `patch_globals` (was already
  disabled in `patch.ts` too).
- `patches/config/repair.json` - `patch_config`.

## 6. Second batch: the commented-out `patch_items` tweaks (flashlights, mags, scopes, mounts)

Ported per-id, all still off by default. Every id was checked against the live database with
`tools/check_item_names.py` (en.json display name lookup) before porting - one real mismatch
turned up:

- **`67d418d0ffb910d21f04720e`** - the old comment says "AK-50 弹匣" but this id now resolves to
  **"M82A1 .50 BMG 10-round magazine"**, a completely different weapon's mag. **Not ported.**
  Either the id was wrong to begin with, or it got reassigned in a database update since
  `patch.ts` was written - either way, don't guess; if you still want an AK-50 magazine buffed,
  find its current id and write a fresh patch file.
- `5aa66c72e5b5b00016327c93` had two separate commented lines with conflicting labels ("AGS-74"
  vs "Nightforce Multimount 34"). The live name is "Nightforce Magmount 34mm ring scope mount" -
  both lines were actually targeting the same mount, "AGS-74" was just a mislabeled comment in
  the original. Merged into one file (`nightforce-magmount.json`), no behavior lost.

Everything else matched its comment and got ported as-is:
- `patches/items/{gl21,surefire-xc1,baldr-pro,raptar,x400}-*.json`, `mawl-c1-device.json` -
  `editmisc`/`editmisc2` group (flashlights/laser/rangefinder: better stats, quieter, cooler).
- `patches/items/mag-*.json` - `editCart`/`editCarts` group (capacity bumps; the two `editCarts`
  ones also do an intentional whole-array ammo-filter replace, same "restrict to this list"
  pattern as `gamma-container.json`).
- `patches/items/gun-*-anyammo.json` - `editgunammo` group.
- `patches/items/aimtech-tiger-shark-mount.json`, `glock19-slide-mount.json` -
  `editmount`/`pushset` group. These are the first real use of the **append** pattern (`add` +
  `/-`) outside the reference-only demo file - see each file's `description` for the dedupe
  caveat (harmless if it ever double-adds).
- `patches/items/monster-mini-suppressor.json` - `editmuz` (only the 4 fields that were actually
  live in `patch.ts`; the Ergonomics/Recoil/Velocity/Accuracy lines were commented out there too).
- `patches/items/scope-*.json` - the three riflescope Zoom/FOV edits.
- `patches/items/gral-s-grip.json`, `f1-firearms-grip.json`, `nightforce-magmount.json`,
  `recknagel-eratac-{30,34}mm.json` - the `ExtraSizeDown`/`ExtraSizeUp` = 0 group.

## 7. Engine built, compiled, and smoke-tested against the live server

Verified end-to-end (2026-09-15): `dotnet build` deploys the DLL + `patches/*.json` +
`patches.enabled.json` into `D:\Game\SPT5\SPT_Runtime\user\mods\DbPostPatcher\`, and a
real `SPT.Server.exe` run applies all 9 currently-enabled patch files with 0 failures. Two real
bugs surfaced by actually running it against the live database (not just reading the schema):

- `goldenstar.json`'s `HeavyBleeding`/`LightBleeding`/`Fracture`/`DestroyedPart` fields don't
  exist on GoldenStar's `effects_damage` by default in the current DB - `op: "replace"` correctly
  refused to silently no-op and logged instead (exactly the safety behavior described in
  `patches/README.md`); fixed by switching those four to `op: "add"`.
- `DestroyedPart`'s value was missing `duration`, which `EffectsDamageProperties.Duration` marks
  `required` - deserializing the patched object back into that C# type threw. The original
  `patch.ts` produced the same incomplete object but never round-tripped it through a typed
  deserializer, so it never caught this. Fixed by adding `"duration": 0`.

Both are exactly the kind of thing `replace`-vs-`add` semantics and the log-and-skip design were
meant to catch loudly instead of silently corrupting data - worth remembering when writing new
patch files: **run the server once after adding a patch, don't just trust the JSON looks right.**

The csproj references the server's already-built DLLs in `SptRuntimeDir` directly (`<Reference>`
+ `HintPath`, `Private=false`) rather than `ProjectReference`-ing `server-csharp`'s source
projects - the latter was tried first and works, but drags the entire (large) server solution
into every build here. Override `SptRuntimeDir`/`ModDeployDir` via
`DbPostPatcher.csproj.user` (gitignored) if your install isn't at `D:\Game\SPT5\SPT_Runtime`.

Patch files and `patches.enabled.json` are deployed as loose files next to the DLL, not embedded
resources - editing them post-build and restarting the server is enough, no rebuild needed.

Not ported: `patch_tplprofile`'s other commented sections (`unlockedProductionRecipe`,
`Skills.Common` upgrade loop, trader standing/jaeger unlock, hideout level overrides) and
`patch_profile` (the live-save health editor, distinct from the template health file already
ported) - these weren't reviewed in this pass; same "verify the id against the live DB first"
treatment should happen before porting them too, ask if you want that done next.
