using System.Collections.Generic;
using System.Linq;
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

        private const float RescanIntervalSeconds = 2f;

        private readonly Dictionary<AirdropSynchronizableObject, MapMarker> _markers = new();
        private float _rescanAccumulator;

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
            _rescanAccumulator = 0f;
        }

        // called every frame from SPTMapController.Update while in a raid; only actually rescans
        // once the interval elapses, since FindObjectsOfType walks the whole scene.
        public void Tick(float deltaTime)
        {
            _rescanAccumulator += deltaTime;
            if (_rescanAccumulator < RescanIntervalSeconds)
            {
                return;
            }

            _rescanAccumulator = 0f;
            Rescan();
        }

        private void Rescan()
        {
            var found = Object.FindObjectsOfType<AirdropSynchronizableObject>();
            if (found == null)
            {
                return;
            }

            var foundSet = found.ToHashSet();

            foreach (var stale in _markers.Keys.Where(k => !foundSet.Contains(k)).ToList())
            {
                MarkerManager.Remove(_markers[stale]);
                _markers.Remove(stale);
            }

            foreach (var airdrop in foundSet)
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
