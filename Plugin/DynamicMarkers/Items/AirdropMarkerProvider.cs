using System.Collections.Generic;
using EFT.SynchronizableObjects;
using SPTMap.Data;
using SPTMap.Utils;
using UnityEngine;

namespace SPTMap.DynamicMarkers
{
    // Airdrop crates. The old SPT-DynamicMaps project only found these via a Harmony patch on
    // ClientAirDrop.CloseParachute (i.e. only once the crate had already landed). This instead
    // does a live Object.FindObjectsOfType<AirdropSynchronizableObject> scan on a short timer -
    // no patch to maintain, and the sync object exists in the scene (and is found by this scan)
    // as soon as the drop starts, not just once it lands, so a crate still under its parachute
    // shows up too. NOT yet verified in-game whether the object is actually present/positioned
    // correctly mid-flight vs. only appearing on landing - check this first if markers seem to
    // appear late.
    public class AirdropMarkerProvider
    {
        private const string Category = "Airdrop";
        private const string ImagePath = "Markers/airdrop.png";
        private static readonly Color MarkerColor = new(1f, 0.65f, 0f);

        private readonly Dictionary<AirdropSynchronizableObject, MapMarker> _markers = new();
        // reused across Rescan calls instead of allocating a fresh HashSet/List every scan.
        private readonly HashSet<AirdropSynchronizableObject> _foundScratch = new();
        private readonly List<AirdropSynchronizableObject> _staleScratch = new();

        public void OnRaidStart()
        {
            Rescan();
        }

        public void OnRaidEnd()
        {
            foreach (var marker in _markers.Values)
            {
                MarkerManager.Remove(marker);
            }

            _markers.Clear();
            _foundScratch.Clear();
            _staleScratch.Clear();
        }

        // Called once on the frame the map is opened (see SPTMapController's peek-toggle edge) -
        // this is the only place airdrop markers refresh after the initial raid-start scan. No
        // periodic re-trigger even if the map stays open a long time: this is a full-scene
        // FindObjectsOfType scan, and even at 20s it was a measurable contributor to periodic
        // frame drops with every disable-able feature (ESP/hit numbers/etc) turned off - avoiding
        // that matters far more than catching a crate the instant it spawns. Close and reopen the
        // map to force a fresh look.
        public void RefreshNow()
        {
            Rescan();
        }

        private void Rescan()
        {
            var found = Object.FindObjectsOfType<AirdropSynchronizableObject>();
            if (found == null)
            {
                return;
            }

            _foundScratch.Clear();
            foreach (var airdrop in found)
            {
                _foundScratch.Add(airdrop);
            }

            _staleScratch.Clear();
            foreach (var tracked in _markers.Keys)
            {
                if (!_foundScratch.Contains(tracked))
                {
                    _staleScratch.Add(tracked);
                }
            }

            foreach (var stale in _staleScratch)
            {
                MarkerManager.Remove(_markers[stale]);
                _markers.Remove(stale);
            }

            foreach (var airdrop in _foundScratch)
            {
                AddMarker(airdrop);
            }
        }

        private void AddMarker(AirdropSynchronizableObject airdrop)
        {
            if (_markers.ContainsKey(airdrop) || airdrop == null)
            {
                return;
            }

            var worldTransform = airdrop.transform;
            var marker = new MapMarker
            {
                Category = Category,
                ImagePath = ImagePath,
                Text = "Airdrop",
                Color = MarkerColor,
                ShowLabel = true,
                GetPosition = () => MathUtils.ConvertToMapPosition(worldTransform.position),
                GetWorldPosition = () => worldTransform.position,
            };

            _markers[airdrop] = marker;
            MarkerManager.Add(marker);
        }
    }
}
