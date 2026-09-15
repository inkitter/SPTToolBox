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

## 8. "Give Item" - F9 panel button that mails an item into the live profile

`Routing/GiveItemRouter.cs` + `Routing/GiveItemCatalog.cs` on the server, `Plugin/Utils/
DbPostPatcherClient.cs` + the "Give Item" section in `Plugin/Utils/QuestDebugPanel.cs` on the
client. Lets you spawn one of a fixed set of hideout-slot items (same catalog as
`unheard-usec-hideout-containers.json`, #6 above) into your *current, already-running* profile
without a server restart or relog - useful when you didn't enable that static injection before
character creation, or just want the container without touching the profile template at all.

### Why mail, not a direct inventory splice

First approach considered: reach into the client's live inventory and drop the item straight in,
the way a "give item" console command works in a lot of games. Investigated via `ilspycmd`
against `BepInEx/interop/Assembly-CSharp.dll` (an IL2CPP interop *stub* - method signatures are
real, bodies are native trampolines, so this only gets you the type surface, never behavior) and
found `EFT.ItemFactory.CreateItem`/`CreateManyItems` exist, but never found a live, safely-
reachable instance to call them on, nor confirmed which native call actually places a received
item into the stash/sorting-table UI live (the community-known `InteractionsHandlerClass` isn't
present under that name in this build - renamed/obfuscated). IL2CPP native method bodies aren't
recoverable by decompiling further, either - there's no IL to decompile, it's compiled to native
code, so this dead-ended at "would need live Harmony tracing in an actual raid to find the real
call chain," which wasn't in scope for this pass.

Pivoted to mail instead: `MailSendService.SendSystemMessageToPlayer(sessionId, message, items)`
in server-csharp is what quest rewards / insurance returns / trader "offer sold" notifications
already use, and its last step (`notificationSendHelper.SendMessageAsync`) pushes a live
notification over the same channel the client is already listening on for those - so mailing an
item gets the "shows up immediately, no relog" behavior for free, using only fully-documented,
non-obfuscated server code. The client side only ever needs a session id and a base URL, both
plain strings - no client-side IL2CPP inventory manipulation at all.

### Session id / backend URL, and getting them without touching IL2CPP objects

First attempt read these off `EFT.UI.ItemUiContext.Instance.Session` cast to `ClientBackendSession`
(`Backend.PhpSessionId`) - `PhpSessionId` genuinely is a real, safe, top-level public property, but
the presumed `Backend.url` property was a **misread**: it's actually a captured local variable
inside the compiler-generated state machine for `Backend.KeepAliveCoroutine`, which just happens
to also be named `url` - `ilspycmd`'s flat text output doesn't make nesting depth obvious at a
glance, and grepping "string url" anywhere in the dump found that nested one, not a real session
field (there isn't one on `Backend`).

Replaced with something much simpler and fully outside IL2CPP entirely: the SPT launcher passes
`-token=<sessionId>` and `-config={"BackendUrl":"...",...}` on the game's own command line - both
visible verbatim in `BepInEx/LogOutput.log` at startup ("key:token value:...", "key:config
value:{'BackendUrl':'https://127.0.0.1:6969',...}"). `Environment.GetCommandLineArgs()` is a
plain managed .NET API, zero native/IL2CPP risk, and it's authoritative for *this* running game
instance by construction (it's what the game itself was actually launched with).

### The response body is always zlib-compressed, and optionally also byte-shuffled

Every response coming back from any SPT server route - not just custom mod routes - is
zlib-deflate compressed (`SptHttpListener.WriteFrameAsync`: always wraps the JSON in a
`ZLibStream` before anything else). On top of that, `RequestEncryptionUtil.ShuffleInPlace`
applies a second, reproducible byte-permutation pass (not real cryptography - a deterministic
formula, matching what live Tarkov's own wire protocol does) to *most* paths -
`SptHttpListener.ShouldShuffleResponse` exempts responses under `/singleplayer/...`, and
`ShouldShuffleRequest` separately exempts `/launcher`, `/client/metadata`, `/v2/shop`, `/files`
(request bodies, not response bodies - see below).

Debugging this from the client side without realizing any of this produced a confusing trail:
raw response bytes looked like noise, and treating them as UTF-8 text and feeding them to
`JsonSerializer.Deserialize<T>(string)` produced misleading errors like `'0xEF' is an invalid
start of a value` or `'0x00' is an invalid start of a value` - those are just the first raw
compressed/shuffled bytes happening to *also* be invalid JSON starts, not a real parsing bug, a
TLS problem, or a BOM (an actual early theory that also turned out wrong once the real cause was
found - though BOM-stripping was kept anyway since decompressed bodies can still carry one).
Confirmed the real cause by manually `zlib.decompressobj(15).decompress(...)` -ing a captured
response in Python and getting the exact expected JSON back. Fixed on the client with
`System.IO.Compression.ZLibStream` (added in .NET 6, matches `Plugin.csproj`'s target) wrapping
the raw response bytes before reading them as text - see `DbPostPatcherClient.ReadBodyAsync`.

**This matters for any *new* custom route, not just this one**: any response body needs
`ZLibStream` decompression regardless of path. `/singleplayer/...` only gets you out of the
*shuffle* layer, not the compression layer.

The GET-with-path-segment shape of `/singleplayer/dbpostpatcher/give-item/{itemTemplateId}` (one
static route registered per catalog entry, rather than a single route parsing an id out of a
POST body) exists because `ShouldShuffleRequest` does **not** exempt `/singleplayer/...` - only
`ShouldShuffleResponse` does. A POST body sent to a `/singleplayer/...` route still gets run
through `RequestEncryptionUtil.DeShuffleAsync` server-side as if it arrived shuffled, which would
silently corrupt (or, once past a 4-byte minimum, throw on an "impossible length" check) any
plain unshuffled JSON body sent from a hand-rolled `HttpClient` request. Since `GiveItemCatalog`
is small and fixed, one exact-match route per entry sidesteps needing to solve "how do I send an
unshuffled request body" for a single string at all.

### Verifying this without running the game

All of the above was confirmed by starting `SPT.Server.exe` locally and hitting the new routes
directly with `curl -k` (self-signed cert) plus manual Python zlib decompression of the captured
bytes - no game client involved. That's a generally useful technique for anything server-route-
shaped in this project going forward: you don't need the game running to verify a DbPostPatcher
HTTP route actually works, `curl`/`Invoke-WebRequest` + zlib-decompressing the body is enough.
