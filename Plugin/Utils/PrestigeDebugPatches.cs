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
            var inventoryScreen = UnityEngine.Object.FindObjectOfType<InventoryScreen>();
            var prestigeScreen = inventoryScreen?._prestigeScreen;
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
        [ThreadStatic]
        private static EGameMode _savedMode;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TarkovApplication.GlobalsDataLoader), nameof(TarkovApplication.GlobalsDataLoader.Load))]
        public static void Prefix(IEftSession __0)
        {
            var descriptor = Descriptor(__0);
            if (descriptor == null)
            {
                return;
            }

            _savedMode = descriptor.GameMode;
            descriptor._GameMode_k__BackingField = EGameMode.Regular;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TarkovApplication.GlobalsDataLoader), nameof(TarkovApplication.GlobalsDataLoader.Load))]
        public static void Postfix(IEftSession __0)
        {
            var descriptor = Descriptor(__0);
            if (descriptor != null)
            {
                descriptor._GameMode_k__BackingField = _savedMode;
            }
        }

        private static GameModeDescriptor Descriptor(IEftSession session) =>
            session?.TryCast<ClientBackendSession>()?._GameModeDescriptor_k__BackingField;
    }
}
