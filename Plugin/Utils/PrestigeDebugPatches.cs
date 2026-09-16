using System;
using EFT;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace SPTMap.Utils
{
    // Dev-only: lets the F9 debug panel reach the native Prestige flow. Originally this forced
    // PrestigeController.CanUpgrade to true via Harmony postfix so the native tab's own gate would
    // let a player through - but CanUpgrade is polled every frame by other native UI (main menu
    // indicator etc), and forcing it true made that code actually execute a prestige-data path the
    // local SPT server doesn't implement, flooding the log with NotImplementedException every frame
    // (see git history). ShowPrestigeScreen below instead calls InventoryScreen._prestigeScreen.Show
    // directly - the same call the native tab's button click ends up making - skipping the
    // CanUpgrade gate entirely instead of forcing it, so that per-frame poll is never touched.
    // TarkovApplication.GlobalsDataLoader.Load only fetches prestige settings/templates from the
    // server in Regular mode, so PrestigeGlobalsLoadPatch below is still needed for the screen to
    // have any PrestigeTemplate data to show in PvE.
    public static class PrestigeDebugPatches
    {
        public static void Enable(Harmony harmony)
        {
            harmony.PatchAll(typeof(PrestigeGlobalsLoadPatch));
        }

        // Mirrors what the native Prestige tab's own button click does once CanUpgrade lets it
        // through, minus the gate itself - so no CanUpgrade patch is needed at all.
        public static void ShowPrestigeScreen()
        {
            var inventoryScreen = UnityEngine.Object.FindObjectOfType<InventoryScreen>();
            if (inventoryScreen == null)
            {
                Plugin.Log.LogWarning("PrestigeDebugPatches: no InventoryScreen found - open Inventory first.");
                return;
            }

            var prestigeScreen = inventoryScreen._prestigeScreen;
            if (prestigeScreen == null)
            {
                Plugin.Log.LogWarning("PrestigeDebugPatches: InventoryScreen._prestigeScreen is null.");
                return;
            }

            prestigeScreen.Show(
                inventoryScreen._profile,
                inventoryScreen._prestigeController,
                inventoryScreen._inventoryController,
                inventoryScreen._backEndSession);
            Plugin.Log.LogInfo("PrestigeDebugPatches: PrestigeScreen.Show invoked directly.");
        }

        // The "转生"/Obtain button on the screen (_obtainPrestige, a DefaultUiButtonNewStyle) is
        // itself gated non-interactable until some other condition is met - simulate the click by
        // calling its handler (ObtainPrestigeHandler, public/parameterless) directly instead of
        // fighting the button's own interactable state.
        public static void ClickObtainPrestige()
        {
            // InventoryScreen is a UnityEngine.Object (UIElement/MonoBehaviour) - explicit if
            // instead of ?., see git history/memory ("?./?? bypasses Unity's fake-null override").
            var inventoryScreen = UnityEngine.Object.FindObjectOfType<InventoryScreen>();
            if (inventoryScreen == null)
            {
                Plugin.Log.LogWarning("PrestigeDebugPatches: no InventoryScreen found - open it via Show Prestige Screen first.");
                return;
            }

            var prestigeScreen = inventoryScreen._prestigeScreen;
            if (prestigeScreen == null)
            {
                Plugin.Log.LogWarning("PrestigeDebugPatches: no PrestigeScreen found - open it via Show Prestige Screen first.");
                return;
            }

            prestigeScreen.ObtainPrestigeHandler();
            Plugin.Log.LogInfo("PrestigeDebugPatches: ObtainPrestigeHandler invoked directly.");
        }
    }

    // The globals loader only requests prestige settings/templates from the server while
    // GameModeDescriptor.GameMode reads as Regular - flips it for the duration of the load call so
    // PvE sessions still populate PrestigeTemplate data, then restores the real mode. Always on (not
    // gated behind a toggle) since it only affects what data gets loaded at startup, not any
    // per-frame UI behavior - mirrors LoadPrestigeSettingsPatch from SPTushonka.Custom.
    internal static class PrestigeGlobalsLoadPatch
    {
        // __state (per-call, from Harmony) instead of [ThreadStatic]: Load is async, and its
        // continuation can resume on a different pooled thread than the one Prefix ran on, which
        // would make a [ThreadStatic] save/restore read back the wrong (default) value.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TarkovApplication.GlobalsDataLoader), nameof(TarkovApplication.GlobalsDataLoader.Load))]
        public static void Prefix(IEftSession __0, out EGameMode __state)
        {
            var descriptor = Descriptor(__0);
            __state = descriptor?.GameMode ?? EGameMode.Regular;
            if (descriptor != null)
            {
                descriptor._GameMode_k__BackingField = EGameMode.Regular;
            }
        }

        // Harmony's Postfix on a Task-returning method runs as soon as the synchronous part of
        // Load returns the Task, not once that Task actually completes - restoring GameMode here
        // directly would put the real mode back well before Load's own async work (which reads
        // GameMode again past its first await) is done, leaving a window where the shared
        // GameModeDescriptor is wrong for any other code reading it concurrently (e.g. matching
        // start-up right after). The real return type is Il2CppSystem.Threading.Tasks.Task (not
        // System.Threading.Tasks.Task) - Harmony's IL emitter checks __result's declared type
        // against the original method's actual return type, so a System.Threading.Tasks.Task
        // parameter here fails to patch at all ("Cannot assign method return type ... to __result
        // type ..."). Rather than relying on Il2Cpp Task's own ContinueWith (delegate marshaling
        // there is unverified in this project), queue the pending restore and poll
        // Il2CppSystem.Threading.Tasks.Task.IsCompleted once per frame from SPTMapController.Update.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TarkovApplication.GlobalsDataLoader), nameof(TarkovApplication.GlobalsDataLoader.Load))]
        public static void Postfix(IEftSession __0, Il2CppSystem.Threading.Tasks.Task __result, EGameMode __state)
        {
            var descriptor = Descriptor(__0);
            if (descriptor == null)
            {
                return;
            }

            if (__result == null)
            {
                descriptor._GameMode_k__BackingField = __state;
                return;
            }

            _pendingTask = __result;
            _pendingDescriptor = descriptor;
            _pendingState = __state;
        }

        private static Il2CppSystem.Threading.Tasks.Task _pendingTask;
        private static GameModeDescriptor _pendingDescriptor;
        private static EGameMode _pendingState;

        // Polled from SPTMapController.Update - see comment on Postfix above for why this isn't a
        // ContinueWith callback.
        public static void Tick()
        {
            if (_pendingTask == null)
            {
                return;
            }

            if (!_pendingTask.IsCompleted)
            {
                return;
            }

            if (_pendingDescriptor != null)
            {
                _pendingDescriptor._GameMode_k__BackingField = _pendingState;
            }

            _pendingTask = null;
            _pendingDescriptor = null;
        }

        private static GameModeDescriptor Descriptor(IEftSession session)
        {
            if (session == null)
            {
                return null;
            }

            var backendSession = session.TryCast<ClientBackendSession>();
            return backendSession == null ? null : backendSession._GameModeDescriptor_k__BackingField;
        }
    }
}
