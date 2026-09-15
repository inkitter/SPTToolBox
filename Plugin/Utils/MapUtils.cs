using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SPTMap.Data;
using UnityEngine;

namespace SPTMap.Utils
{
    public static class MapUtils
    {
        // the game's location id casing doesn't reliably match what we write in map json files
        // (e.g. GameWorld reports "interchange" lowercase even though "Interchange" is what's
        // commonly used elsewhere) - match case-insensitively.
        private static readonly Dictionary<string, MapDef> Defs = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Texture2D> Textures = new();
        private static readonly HashSet<string> WarnedMissingNames = new(StringComparer.OrdinalIgnoreCase);
        private static bool _scanned;

        // internalName (e.g. "factory4_day") -> loaded def+texture, or null if none found/loadable
        public static (MapDef def, Texture2D texture) GetForInternalName(string internalName)
        {
            if (string.IsNullOrEmpty(internalName))
                return (null, null);

            ScanOnce();

            if (!Defs.TryGetValue(internalName, out var def))
            {
                // Called every OnGUI frame while a raid is on a location we don't have (or don't yet
                // have) an alias for - logging unconditionally floods the log for the whole raid.
                // Once per distinct missing name is enough to catch and fix it.
                if (WarnedMissingNames.Add(internalName))
                {
                    Plugin.Log.LogWarning($"No map def for '{internalName}'. Known: [{string.Join(", ", Defs.Keys)}]");
                }
                return (null, null);
            }

            var texture = GetTexture(def);
            return (def, texture);
        }

        // distinct defs (Defs.Values has one entry per alias, e.g. factory4_day/factory4_night
        // both point at the same MapDef instance) sorted for a stable cycle order.
        public static List<MapDef> GetAllDefs()
        {
            ScanOnce();
            return Defs.Values.Distinct().OrderBy(d => d.DisplayName).ToList();
        }

        public static Texture2D GetTexture(MapDef def)
        {
            if (def == null) return null;

            // don't trust a cache hit blindly - entering a raid triggers the engine's own asset
            // cleanup, and a Texture2D held only by this static dictionary (nothing in any scene
            // references it) is exactly the kind of thing that sweep reclaims. A destroyed Unity
            // Object still satisfies "found the key" here but compares == null afterwards (Unity's
            // overridden equality for destroyed objects), so check for that and reload from disk
            // instead of handing back a dead reference forever.
            if (Textures.TryGetValue(def.ImagePath, out var texture) && texture != null)
                return texture;

            texture = LoadTexture(def.ImagePath);
            Textures[def.ImagePath] = texture;
            return texture;
        }

        public static Texture2D GetLevelTexture(MapLevel level)
        {
            return level == null ? null : GetTextureByPath(level.ImagePath);
        }

        // same cache + dead-reference recovery as GetTexture(MapDef), for one-off images (marker
        // icons etc) that aren't tied to a map def.
        public static Texture2D GetTextureByPath(string relativePath)
        {
            if (Textures.TryGetValue(relativePath, out var texture) && texture != null)
                return texture;

            texture = LoadTexture(relativePath);
            Textures[relativePath] = texture;
            return texture;
        }

        private static void ScanOnce()
        {
            if (_scanned) return;
            _scanned = true;

            var mapsDir = Path.Combine(Plugin.Path, "Maps");
            if (!Directory.Exists(mapsDir))
            {
                Plugin.Log.LogError($"Maps directory not found: {mapsDir}");
                return;
            }

            var jsonFiles = Directory.GetFiles(mapsDir, "*.json", SearchOption.AllDirectories);
            Plugin.Log.LogInfo($"Scanning {mapsDir}, found {jsonFiles.Length} map json file(s): [{string.Join(", ", jsonFiles)}]");

            foreach (var jsonPath in jsonFiles)
            {
                MapDef def;
                try
                {
                    def = MapDef.LoadFromPath(jsonPath);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogError($"Failed loading map def {jsonPath}: {e.Message}");
                    continue;
                }

                if (def?.MapInternalNames == null)
                {
                    Plugin.Log.LogWarning($"Loaded {jsonPath} but MapInternalNames was null (DisplayName={def?.DisplayName})");
                    continue;
                }

                Plugin.Log.LogInfo($"Loaded map def '{def.DisplayName}' with internal names: [{string.Join(", ", def.MapInternalNames)}]");

                foreach (var name in def.MapInternalNames)
                    Defs[name] = def;
            }
        }

        private static Texture2D LoadTexture(string relativeImagePath)
        {
            var absolutePath = Path.Combine(Plugin.Path, relativeImagePath);
            if (!File.Exists(absolutePath))
            {
                Plugin.Log.LogError($"Map image not found: {absolutePath}");
                return null;
            }

            var bytes = File.ReadAllBytes(absolutePath);
            var texture = new Texture2D(2, 2);
            texture.LoadImage(bytes);
            // only our own static dictionary references this - nothing in any scene does - so
            // without this flag a raid load's asset cleanup can reclaim it as "unused" out from
            // under us (see the reload-on-dead-cache-hit comment in GetTexture for the fallback).
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return texture;
        }
    }
}
