using System;
using System.Diagnostics;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using EFT.UI;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SPTMapConfig = SPTMap.Config.Settings;
using UnityEngine;

namespace SPTMap
{
    // Stage 2: player position + zoom/pan-follow. Default state shows a translucent "SPTMap
    // Loaded" line top-right; holding M shows a box with the current map and a player marker.
    // Keypad 8/5 zoom in/out, Keypad 2 resets to the full map (no panning needed at full zoom -
    // panning/centering-on-player only kicks in once zoomed past 1x). Falls back to Factory with
    // no player marker when not in a raid, just so there's something to look at.
    [BepInPlugin("com.sptmap.plugin", "SPTMap", "0.0.1")]
    public class Plugin : BasePlugin
    {
        public static Plugin Instance;
        public static new ManualLogSource Log => Instance.PublicLog;
        public ManualLogSource PublicLog => base.Log;
        public static string Path = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        // EFT build this plugin was tested against. The check below is deliberately a >= floor,
        // not an exact match: EFT's build number bumps on every game patch, and this plugin only
        // touches generic Unity/IMGUI/Harmony/reflection surfaces that don't change per-patch, so
        // refusing to load on a newer game build would be needless breakage. An *older* game than
        // this plugin targets is the direction that's actually risky (the IL2CPP interop/reflection
        // assumptions this plugin makes may not hold), so that direction is still refused.
        public const int BuiltForVersion = 47242;
        public static int CurrentGameVersion { get; private set; }

        public override void Load()
        {
            Instance = this;

            CurrentGameVersion = FileVersionInfo.GetVersionInfo(Process.GetCurrentProcess().MainModule.FileName).FilePrivatePart;
            if (CurrentGameVersion < BuiltForVersion)
            {
                var message = $"SPTMap was built for EFT {BuiltForVersion}+, but this install is running {CurrentGameVersion}. Refusing to load.";
                Log.LogError(message);
                IL2CPPChainloader.Instance.DependencyErrors.Add(message);
                return;
            }

            SPTMapConfig.Init(Config);

            ClassInjector.RegisterTypeInIl2Cpp<SPTMapBehaviour>();

            // explicit type list rather than PatchAll(assembly) - that was the culprit behind the
            // total render failure (see PROGRESS.md/conversation history): something about the
            // assembly-wide scan picking up all three patch types together broke CommonUIAwakePatch
            // itself. Patching each type by name individually avoids whatever that interaction was.
            var harmony = new Harmony("com.sptmap.plugin");
            harmony.PatchAll(typeof(CommonUIAwakePatch));

            Log.LogInfo("SPTMap loaded (stage 2: player tracking + zoom)");
        }
    }

    // A GameObject created at plugin Load() time (during BepInEx chainloader startup, before the
    // game's first scene has settled) gets destroyed by the engine's own scene transition shortly
    // after, even with DontDestroyOnLoad called on it - confirmed by lifecycle logging (Awake ->
    // OnEnable -> OnDisable -> OnDestroy, all before a single Update/OnGUI). CommonUI is created
    // later and lives for the whole game session (menu/hideout/raid), so parent our component to
    // it instead of trying to make our own root object persistent.
    internal static class CommonUIAwakePatch
    {
        private static bool _attached;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CommonUI), nameof(CommonUI.Awake))]
        public static void Postfix(CommonUI __instance)
        {
            if (_attached) return;

            try
            {
                __instance.gameObject.AddComponent<SPTMapBehaviour>();
                _attached = true;
                Plugin.Log.LogInfo("SPTMapBehaviour attached to CommonUI");
            }
            catch (Exception e)
            {
                // don't latch _attached on failure - CommonUI.Awake only fires once per session
                // normally, but leaving this false at least means a future retry path (e.g. a
                // scene reload) isn't permanently blocked by one bad attempt.
                Plugin.Log.LogError($"AddComponent<SPTMapBehaviour> failed: {e}");
            }
        }
    }

    // Thin Il2Cpp-registered shell: only the two real Unity messages live here (their signatures
    // are IL2CPP-compatible - void, no plain-C#-type params/returns - so registering them raises
    // no Il2CppInterop warnings). Everything else lives on SPTMapController, a plain (unregistered)
    // C# class - see its own comment for why.
    public class SPTMapBehaviour(IntPtr pointer) : MonoBehaviour(pointer)
    {
        private readonly SPTMapController _controller = new();

        private void Update() => _controller.Update();
        private void OnGUI() => _controller.OnGUI();
    }

}
