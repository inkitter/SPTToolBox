using System.Collections.Generic;
using System.Linq;
using EFT.Interactive;
using SPTMap.Data;
using SPTMap.Utils;
using UnityEngine;
using static EFT.InventoryLogic.ItemExtensions;

namespace SPTMap.DynamicMarkers
{
    // Locked doors, discovered live from the raid scene (EFT.Interactive.Door) rather than baked
    // per-map data - unlike extracts, doors are ordinary level geometry present in the scene for
    // the whole raid, so a live scan is enough (no corpse-style timing gotcha either).
    public class DoorMarkerProvider
    {
        private const string Category = "Locked Door";
        private const string KeyImagePath = "Markers/door_with_key.png";
        private const string LockImagePath = "Markers/door_with_lock.png";

        private static readonly Color HasKeyColor = Color.green;
        private static readonly Color NoKeyColor = Color.red;

        private readonly List<MapMarker> _markers = new();

        // like ExtractMarkerProvider, the scene's doors aren't necessarily all live the instant
        // game.InRaid flips true - stays false until at least one is found, so the caller can
        // retry each frame; OnRaidStart itself is a safe no-op to call repeatedly.
        private bool _populated;

        public void OnRaidStart()
        {
            if (_populated)
            {
                return;
            }

            var doors = Object.FindObjectsOfType<Door>();
            if (doors == null || doors.Length == 0)
            {
                return;
            }

            foreach (var door in doors)
            {
                if (!string.IsNullOrEmpty(door.KeyId))
                {
                    AddMarker(door);
                }
            }

            _populated = true;
        }

        public void OnRaidEnd()
        {
            foreach (var marker in _markers)
            {
                MarkerManager.Remove(marker);
            }

            _markers.Clear();
            _populated = false;
        }

        private void AddMarker(Door door)
        {
            var worldPos = door.transform.position;
            var pos = MathUtils.ConvertToMapPosition(worldPos);
            var keyId = door.KeyId;

            var marker = new MapMarker
            {
                Category = Category,
                Text = door.Id,
                GetPosition = () => pos,
                GetWorldPosition = () => worldPos,
                // checked live every frame - a door drawn red at raid start should flip green the
                // moment the player picks the matching key up off the ground.
                GetImagePath = () => PlayerHasKey(keyId) ? KeyImagePath : LockImagePath,
                GetColor = () => PlayerHasKey(keyId) ? HasKeyColor : NoKeyColor,
            };

            _markers.Add(marker);
            MarkerManager.Add(marker);
        }

        // GetAllItems() walks the live native inventory tree. Doing that from OnGUI - called for
        // every door marker, multiple times a frame - means a real chance of the scan landing
        // while the player is concurrently moving items in their inventory, mutating the native
        // collection mid-enumeration and crashing with an uncatchable AccessViolationException
        // (see D:\Git\SPT-DynamicMaps\MEMORY.md "运行时联调记录（续六/七）" for the identical
        // failure mode on AllPlayersEverExisted). Throttling the scan to a few times a second
        // shrinks that window to effectively nothing while staying visually "live enough".
        private const float KeyCacheRefreshInterval = 0.5f;
        private static float _keyCacheTime = float.NegativeInfinity;
        private static HashSet<string> _keyCache = new();

        private static bool PlayerHasKey(string keyId)
        {
            if (Time.time - _keyCacheTime >= KeyCacheRefreshInterval)
            {
                _keyCacheTime = Time.time;
                _keyCache = ScanOwnedKeyIds();
            }

            return _keyCache.Contains(keyId);
        }

        private static HashSet<string> ScanOwnedKeyIds()
        {
            var player = GameUtils.GetMainPlayer();
            var equipment = player?.Inventory?.Equipment;
            if (equipment is null)
            {
                return new HashSet<string>();
            }

            return equipment.GetAllItems().ToSystemList().Select(i => i.StringTemplateId).ToHashSet();
        }
    }
}
