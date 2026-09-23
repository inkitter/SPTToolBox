using System.Collections.Generic;
using EFT.Interactive;
using SPTMap.Data;
using SPTMap.Utils;
using UnityEngine;

namespace SPTMap.DynamicMarkers
{
    // Power switches / levers (e.g. Customs ZB-013, Interchange power, Reserve D-2 extract lever).
    // Found via a live scene scan (Object.FindObjectsOfType<Switch>()) at raid start rather than
    // the old SPT-DynamicMaps project's static per-map JSON positions - map updates can move them,
    // and the scene is always current. Same Operatable && HasAuthority filter the old project's
    // DumpSwitches used to pick out the real player-usable switches (skips decorative/linked
    // child switches). Yellow while off, green once turned on - read live, since the state flips
    // mid-raid.
    public class SwitchMarkerProvider
    {
        private const string Category = "Switch";
        private const string ImagePath = "Markers/lever.png";

        private static readonly Color OffColor = Color.yellow;
        private static readonly Color OnColor = Color.green;

        // Keyed by position string, not the Switch object - Il2Cpp wrappers from different native
        // calls aren't guaranteed to be the same managed instance (see ExtractMarkerProvider).
        private readonly Dictionary<string, MapMarker> _markers = new();

        private bool _populated;

        public void OnRaidStart()
        {
            if (_populated)
            {
                return;
            }

            Scan();

            // static level geometry, like TransitPoint - an empty result means this map has none,
            // so stop regardless of count (gating on count would rescan every frame all raid).
            _populated = true;
        }

        // Called once on the frame the full map is opened - safety net in case switches weren't
        // loaded yet when OnRaidStart's single scan ran. Idempotent (position-keyed).
        public void RefreshNow()
        {
            if (_populated)
            {
                Scan();
            }
        }

        private void Scan()
        {
            var switches = Object.FindObjectsOfType<Switch>();
            if (switches != null)
            {
                foreach (var @switch in switches)
                {
                    if (@switch == null || !@switch.Operatable || !@switch.HasAuthority)
                    {
                        continue;
                    }

                    AddMarker(@switch);
                }
            }
        }

        // Switches have no dedicated display-name field. Best localized source first: the extract
        // the switch powers (e.g. "ZB-013"), then its zone/context-menu tip keys, then the raw
        // scene object name. Raw fields are logged once per switch so the order can be refined.
        private static string GetDisplayName(Switch @switch)
        {
            var objectName = @switch.gameObject.name;

            string extractName = null;
            var extract = @switch.ExfiltrationPoint;
            if (extract != null)
            {
                extractName = LocalizationUtils.TryLocalize(extract.Settings.Name) ?? extract.Settings.Name;
            }

            Plugin.Log.LogInfo($"[switch] object='{objectName}' extract='{extractName}' zoneTip='{@switch.ExtractionZoneTip}' "
                + $"menuTip='{@switch.ContextMenuTip}' typeKey='{@switch.TypeKey}'");

            var tip = LocalizationUtils.TryLocalize(@switch.ExtractionZoneTip)
                ?? LocalizationUtils.TryLocalize(@switch.ContextMenuTip);

            if (!LocalizationUtils.IsBlank(extractName))
            {
                return tip != null ? $"{extractName} - {tip}" : extractName;
            }

            return tip ?? (LocalizationUtils.IsBlank(objectName) ? "Switch" : objectName);
        }

        public void OnRaidEnd()
        {
            foreach (var marker in _markers.Values)
            {
                MarkerManager.Remove(marker);
            }

            _markers.Clear();
            _populated = false;
        }

        private void AddMarker(Switch @switch)
        {
            var worldPos = @switch.transform.position;
            var key = $"{worldPos.x:0.0},{worldPos.y:0.0},{worldPos.z:0.0}";
            if (_markers.ContainsKey(key))
            {
                return;
            }

            var pos = MathUtils.ConvertToMapPosition(worldPos);
            var marker = new MapMarker
            {
                Category = Category,
                ImagePath = ImagePath,
                Text = GetDisplayName(@switch),
                ShowLabel = false,
                GetPosition = () => pos,
                GetWorldPosition = () => worldPos,
                // @switch is a UnityEngine.Object read live every OnGUI - guard against destroyed
                GetColor = () => @switch != null && @switch.DoorState == EDoorState.Open ? OnColor : OffColor,
            };

            _markers[key] = marker;
            MarkerManager.Add(marker);
        }
    }
}
