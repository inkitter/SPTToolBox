using EFT.Vehicle;
using SPTMap.Data;
using SPTMap.Utils;
using UnityEngine;

namespace SPTMap.DynamicMarkers
{
    // BTR (armored transport). Not baked per-map data and not always present (map-dependent, and
    // can leave/return during a raid) - so this polls GameUtils.GetBTRView() each tick rather than
    // subscribing to any spawn/despawn event, mirroring OtherPlayersMarkerProvider's poll-don't-patch
    // approach. GetPosition/GetWorldPosition read the live transform directly (no cached state
    // needed - a Transform read is cheap, unlike the Il2Cpp inventory scan DoorMarkerProvider has
    // to throttle).
    public class BTRMarkerProvider
    {
        private const string Category = "BTR";
        private const string ImagePath = "Markers/btr.png";
        private static readonly Color MarkerColor = Color.white;

        private MapMarker _marker;
        private BTRView _view;

        public void OnRaidStart()
        {
            TryAcquire();
        }

        public void OnRaidEnd()
        {
            if (_marker != null)
            {
                MarkerManager.Remove(_marker);
            }

            _marker = null;
            _view = null;
        }

        // called every frame from SPTMapController.Update while in a raid - cheap enough (one
        // singleton field read) to not need throttling; also re-acquires if the BTR wasn't found
        // yet, or drops the marker if the view goes away (e.g. destroyed with the map/scene).
        public void Tick()
        {
            if (_view == null)
            {
                TryAcquire();
                return;
            }

            if (_view.gameObject == null)
            {
                OnRaidEnd();
            }
        }

        private void TryAcquire()
        {
            var view = GameUtils.GetBTRView();
            if (view == null || view == _view)
            {
                return;
            }

            _view = view;

            var worldTransform = view.transform;
            var marker = new MapMarker
            {
                Category = Category,
                ImagePath = ImagePath,
                Text = "BTR",
                Color = MarkerColor,
                ShowLabel = true,
                GetPosition = () => MathUtils.ConvertToMapPosition(worldTransform.position),
                GetWorldPosition = () => worldTransform.position,
            };

            _marker = marker;
            MarkerManager.Add(marker);
        }
    }
}
