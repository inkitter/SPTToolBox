# SPTMap

An in-game minimap plugin for [SPT](https://sp-tarkov.com) (Single Player Tarkov), built with Unity's legacy IMGUI system.

## Origin

SPTMap is a rewrite of [SPT-DynamicMaps](https://github.com/mpstark/SPT-DynamicMaps) by mpstark. The rewrite exists to sidestep two recurring problems with that project's uGUI + Unity.VectorGraphics approach: IL2CPP generic-method-unstripping crashes, and breakage whenever BSG changes UI-internal assembly versions between game updates. SPTMap instead renders everything through legacy `OnGUI`/`GUI.DrawTexture` calls, which only depend on `UnityEngine.IMGUIModule` — no BSG UI assembly, no VectorGraphics — so it survives game updates that would otherwise break a uGUI-based map mod.

Map source data (SVGs, per-floor bounds) is fetched from [tarkov.dev](https://tarkov.dev) / [the-hideout/tarkov-dev](https://github.com/the-hideout/tarkov-dev), whose own map data traces back to the same source art SPT-DynamicMaps used. See [`Plugin/Resources/Maps/LICENSE.md`](Plugin/Resources/Maps/LICENSE.md) for full map-asset attribution.

## Features

- In-raid minimap (always shown) and full-screen map (hold `M`)
- Multi-floor support with automatic floor detection based on player position
- Player position/facing marker, zoom, and pan-follow
- Dynamic markers: extracts (with status), secret extracts, transit points, other players, corpses, quest objectives (including quest items), locked doors, BTR, airdrops
- **`F9`** opens a quest debug panel with an instant-finish button per quest (calls the same engine entry point the native "Complete quest" button uses, so rewards/chain unlocks fire for real). Ships enabled by default — see `Plugin/Utils/QuestDebugPanel.cs`.

## Compatibility

| | |
|---|---|
| SPT release | `SPT-BLEEDINGEDGEMODS-5.0.0-47242-ec15a40-20260914` |
| EFT build | `47242`+ ([`Plugin.BuiltForVersion`](Plugin/Plugin.cs)) |

The plugin checks the running game's build number at load and refuses to load on anything older than `Plugin.BuiltForVersion` — see [Plugin lifecycle](CLAUDE.md#plugin-lifecycle). This table will be updated as the plugin is retested against newer SPT releases.

## Installation

Drop the built `BepInEx/plugins/sptmap/` folder into your SPT install's `BepInEx/plugins/` directory. See [Build & deploy](CLAUDE.md#build--deploy) for build instructions if you're building from source.

Some display settings (minimap size/position, zoom speed, etc.) are exposed as in-game sliders, but only if [BepInEx.ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager) is also installed — it's what draws the `F12` settings menu. Without it, SPTMap still works, just with its hardcoded defaults.

## License

The plugin code is licensed under the [MIT License](LICENSE). Map images under `Plugin/Resources/Maps/` are third-party assets under a separate license — see [`Plugin/Resources/Maps/LICENSE.md`](Plugin/Resources/Maps/LICENSE.md).
