# Deploy

Practical build/deploy commands for everything that ends up running against the local SPT install at `D:\Game\SPT5\`. This repo (SPTToolBox) hosts two of the three things below (SPTMap, DbPostPatcher); the SPT server itself lives in a separate repo (`d:\Git\SPT\server-csharp`) but is included here since it's the runtime all three actually run against.

Full architectural detail for SPTMap/DbPostPatcher lives in [CLAUDE.md](CLAUDE.md) — this file is just the "what command do I run" cheat sheet.

## 1. SPTMap (client plugin)

Source: `Plugin\`. Deploys to `$(TarkovDir)BepInEx\plugins\sptmap\` (default `D:\Game\SPT5\BepInEx\plugins\sptmap\`).

```powershell
# Close the game first - the copy fails if EscapeFromTarkov.exe is running.

# From Plugin\
dotnet build

# Or override the target install for one build without a .csproj.user:
dotnet build -p:TarkovDir="D:\Game\SPT5\"
```

PostBuild auto-copies `SPTMap.dll`, `Newtonsoft.Json.dll`, and `Resources\` to the target folder. Override `TarkovDir` per machine in `Plugin\SPTMap.csproj.user` (gitignored, MSBuild auto-imports it) instead of passing `-p:` every time:

```xml
<Project>
  <PropertyGroup>
    <TarkovDir>D:\Game\SPT5\</TarkovDir>
  </PropertyGroup>
</Project>
```

## 2. DbPostPatcher (server mod, this repo)

Source: `Server\DbPostPatcher\`. Deploys to `$(SptRuntimeDir)\user\mods\DbPostPatcher\` (default `D:\Game\SPT5\SPT_Runtime\user\mods\DbPostPatcher\`).

```powershell
# From Server\DbPostPatcher\
dotnet build
```

`dotnet build` deploys `DbPostPatcher.dll` + every `patches\**\*.json` file into the target `user\mods\DbPostPatcher\` folder. Override `SptRuntimeDir` per machine in `Server\DbPostPatcher\DbPostPatcher.csproj.user` (gitignored), same pattern as above.

After adding a new patch file, **run the server once** — `patches.enabled.json`'s `available` list is regenerated on every boot from whatever files exist under `patches\`, but a new file still needs to be added to its `enabled` list (by hand, or by the mod's own UI if it has one) to actually apply. See [`Server/DbPostPatcher/patches/README.md`](Server/DbPostPatcher/patches/README.md) for the patch file format.

## 3. SPT server (`d:\Git\SPT\server-csharp`, separate repo)

Not part of this repo — included here because DbPostPatcher and SPTMap's F9 "Give Item" panel both run against it. Deploys to `D:\Game\SPT5\SPT_Runtime\` (a flat publish-output directory: server binaries + `SPT_Data` at the top level, plus `user\` holding profiles/mods/credentials — never touch `user\` from a server publish).

**Requires `git-lfs`** (`items.json`, `looseLoot.json`, `background.mp4` are LFS-tracked per `.gitattributes`). If it's not installed, checkout silently leaves LFS pointer text (`version https://git-lfs.github.com/spec/v1...`) in place of the real file, and the server fails to boot with a JSON parse error like `'v' is an invalid start of a value` on `items.json`. Install once via `winget install --id GitHub.GitLFS -e`, then `git lfs install && git lfs pull` in the repo. A freshly-installed git-lfs may not be on PATH for an already-open terminal/IDE — restart it, or for this session: `export PATH="$PATH:/c/Program Files/Git LFS"`.

**Don't build the `5.0x-dev` branch tip and assume it's runnable.** It's a dev branch — the tip has genuinely failed to compile more than once (a newly-added file with a real type error, not an environment issue). It's also not guaranteed compatible with whatever EFT client build is actually installed — the server can start but blow up at runtime instead (e.g. shipping an achievement condition type or a new `SPT_Data/database/templates/*.json` file the installed client/older server code doesn't know about). Both `server-csharp` and `d:\Git\SPT\modules` (the client-side BepInEx patch — see below) publish matching tags like `5.0.0-BEM-20260914`; **build both repos from the same tag**, not independently-latest branch tips, and prefer the newest tag that's confirmed to build over the branch tip.

```powershell
# From d:\Git\SPT\server-csharp — pin both repos to the same tag first:
git checkout 5.0.0-BEM-20260914   # match whatever tag you're using in modules/
dotnet publish SPTushonka.Server/SPTushonka.Server.csproj -c Release -o <scratch-dir>
```

Then copy the publish output into `D:\Game\SPT5\SPT_Runtime\`, **excluding `user\`** so profiles/mods/credentials are never touched:

```powershell
Robocopy <scratch-dir> "D:\Game\SPT5\SPT_Runtime" /E /XD user /NFL /NDL /NJH /R:1 /W:1
```

Robocopy exit code `1` means "files copied successfully" (not an error) — treat `0` or `1` as success, anything `>=8` as a real failure. Publish to a scratch directory first and inspect it (rather than publishing straight at `SPT_Runtime`) so a `dotnet publish` that unexpectedly drops or renames files doesn't silently clobber the live install.

Don't `dotnet build` this repo expecting a runnable server — `SPTushonka.Server` is a multi-project solution (`Libraries\SPTushonka.Server.Core`, `.Assets`, `.Web`, plus a `Ceciler` IL-patching build step) and only `dotnet publish` produces the full flat layout `SPT_Runtime\` expects.

**Switching to an older commit/tag after a newer one was deployed can leave stray data files behind and crash the server at boot.** Plain `Robocopy /E` only adds/overwrites — it never deletes a destination file that isn't in the source, so a database file introduced by a newer version (e.g. `SPT_Data\database\templates\leagueRanks.json`) survives a "downgrade" deploy untouched. Older server code doesn't know that file's property (`TemplateTable` has no matching field) and crashes on boot with `Unable to find property '<name>' for type 'TemplateTable'`. Fix: after switching commits/tags, mirror just the affected data directory instead of the whole runtime (full `/MIR` on `SPT_Runtime` risks deleting root-level files — launcher exe, native argon2 libs — that aren't part of this publish output at all and may be needed by something else):

```powershell
Robocopy <scratch-dir>\SPT_Data\database\templates "D:\Game\SPT5\SPT_Runtime\SPT_Data\database\templates" /MIR /NFL /NDL /NJH /R:1 /W:1
```

Preview first with `/L` (list-only, no changes) if unsure what a `/MIR` would delete.

## 4. modules (`d:\Git\SPT\modules`, separate repo — client-side BepInEx patch)

The actual compatibility layer that makes the EFT client talk to an SPT server (distinct from SPTMap's own plugin). Deploys to `D:\Game\SPT5\BepInEx\` (`plugins/sptushonka/` + `patchers/`). Must be built from the **same tag** as server-csharp (see above) — they ship matched releases.

```powershell
# From d:\Git\SPT\modules
git checkout 5.0.0-BEM-20260914   # same tag as server-csharp
dotnet build Modules.slnx -c Release -p:GameDir="D:\Game\SPT5"
```

`SPTushonka.Build`'s post-build target assembles everything into `Build\BepInEx\` (wiping that folder first so removed plugins don't linger), which you then copy over the install:

```powershell
Robocopy "Build\BepInEx" "D:\Game\SPT5\BepInEx" /E /NFL /NDL /NJH /R:1 /W:1
```

Default `GameDir` in `Directory.Build.props` is `D:/SPT-5.0.0/DEV`, not this machine's install — always pass `-p:GameDir="D:\Game\SPT5"` (or set it in a local override, same pattern as the other two projects). Close the game before building/deploying, same reasoning as SPTMap.
