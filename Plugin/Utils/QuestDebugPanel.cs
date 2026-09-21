using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Quests;
using EFT.UI;
using UnityEngine;

namespace SPTMap.Utils
{
    // Dev-only tool: a floating IMGUI panel (no native uGUI touched, same reasoning as the rest of
    // this project - see CLAUDE.md) listing every not-yet-finished quest, split into main-story and
    // side, each with a "Finish" button. The button doesn't poke QuestStatus directly - it calls
    // QuestController.TryInstantFinishQuest, the same public engine entry point the native "Complete
    // quest" button in the Tasks screen ends up calling, so reward grants/condition bookkeeping/quest
    // chain unlocks all fire for real. Exists because some quests can't be completed through their
    // intended trigger during development (bugged condition), and re-running a whole raid to hit a
    // broken trigger just to test the *next* quest in a chain isn't practical.
    //
    // QuestController isn't Player-bound - EFT.UI.ItemUiContext.Instance.QuestController is the same
    // persistent-across-menu/hideout/raid accessor the native Tasks UI itself is wired up with, so
    // this works from the main menu (the primary use case) as well as in a raid.
    public static class QuestDebugPanel
    {
        private const KeyCode ToggleKey = KeyCode.F9;
        private static readonly string[] TabLabels = { "Incomplete", "Not started", "Completed" };
        private static readonly string[] PageLabels = { "Quest", "Item", "Prestige", "Character", "Airdrop" };

        private static bool _visible;
        private static int _page;
        private static int _tab;
        private static Vector2 _scroll;
        private static GUIStyle _headerStyle;
        private static GUIStyle HeaderStyle => _headerStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
        };

        private static GUIStyle _descriptionStyle;
        private static GUIStyle DescriptionStyle => _descriptionStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = 10,
            wordWrap = true,
            normal = { textColor = new Color(0.75f, 0.75f, 0.75f) },
        };

        private static string _giveItemResult = "";

        // GiveItemAsync's onResult callback fires off the main thread (see its own comment) and
        // writes straight into _giveItemResult. Unity calls OnGUI multiple times per frame (Layout,
        // then Repaint) and expects the exact same sequence of GUILayout calls on every pass of a
        // given frame - if that callback (or a DbPostPatcherClient background update) lands between
        // the Layout and Repaint pass, whether the trailing result Label/an item row gets drawn can
        // flip mid-frame, and Unity throws "Mismatched LayoutGroup.repaint". Freezing one snapshot of
        // every value DrawGiveItemSection reads, refreshed only once per Unity frame (frameCount),
        // guarantees every pass of a frame sees identical data regardless of when a background
        // continuation happens to land.
        private static int _giveItemSnapshotFrame = -1;
        private static bool? _giveItemSnapshotBackendAvailable;
        private static string _giveItemSnapshotLastStatus = "";
        private static DbPostPatcherClient.CatalogEntry[] _giveItemSnapshotCatalog = Array.Empty<DbPostPatcherClient.CatalogEntry>();
        private static string _giveItemSnapshotResult = "";

        // Character tab - writes back to the live server profile via CharacterDebugRouter, see its
        // own header comment (Server/DbPostPatcher/Routing/CharacterDebugRouter.cs) for the route
        // shapes. Every row here is a fixed, unconditional set of controls (never a foreach over
        // fetched/variable-length data, never an "if got data yet" branch around a control) -
        // learned from the give-item mismatched-layout bug above: only the *text content* of a
        // Label/TextField is allowed to change out from under an async callback, never whether a
        // control exists at all, since that's what Unity's Layout/Repaint pass comparison trips on.
        private static readonly string[] BodyPartNames = { "Head", "Chest", "Stomach", "LeftArm", "RightArm", "LeftLeg", "RightLeg" };
        private static readonly string[] VitalNames = { "hydration", "energy", "temperature" };
        private static readonly Dictionary<string, string> _bodyPartDisplay = new();
        private static readonly Dictionary<string, string> _bodyPartInput = new();
        private static readonly Dictionary<string, string> _vitalDisplay = new();
        private static readonly Dictionary<string, string> _vitalInput = new();
        // Mirrors SPTarkov.Server.Core.Models.Enums.SkillTypes (server-csharp) in enum declaration
        // order - CharacterDebugRouter's set-skill route validates against that same enum name, so
        // this list must be kept in sync by hand if that enum ever changes. A fixed compile-time
        // array (not the variable-length list of skills the server returns, which only carries
        // entries the profile has already touched) so every skill is always pickable, not just ones
        // already progressed, and so the row count here never varies between OnGUI passes.
        private static readonly string[] AllSkillNames =
        {
            "Endurance", "Strength", "Vitality", "Health", "StressResistance", "Metabolism",
            "Immunity", "Perception", "Intellect", "Attention", "Charisma", "Memory",
            "MagDrills", "Pistol", "Revolver", "SMG", "Assault", "Shotgun", "Sniper", "LMG", "HMG",
            "Launcher", "AttachedLauncher", "Throwing", "Misc", "Melee", "DMR",
            "DrawMaster", "AimMaster", "RecoilControl", "TroubleShooting", "Sniping",
            "CovertMovement", "ProneMovement",
            "FirstAid", "FieldMedicine", "Surgery",
            "LightVests", "HeavyVests", "WeaponModding", "AdvancedModding",
            "NightOps", "SilentOps", "Lockpicking", "Search", "WeaponTreatment",
            "Freetrading", "Auctions", "Cleanoperations", "Barter", "Shadowconnections", "Taskperformance",
            "BearAssaultoperations", "BearAuthority", "BearAksystems", "BearHeavycaliber", "BearRawpower",
            "UsecArsystems", "UsecDeepweaponmodding", "UsecLongrangeoptics", "UsecNegotiations", "UsecTactics",
            "BotReload", "BotSound", "AimDrills", "HideoutManagement", "Crafting",
        };
        private static readonly Dictionary<string, string> _skillDisplay = new();
        private static readonly Dictionary<string, string> _skillInput = new();
        private static Vector2 _skillScroll;
        private static string _characterStatus = "";
        private static bool _characterLoadedOnce;

        public static void HandleInput()
        {
            if (Input.GetKeyDown(ToggleKey))
            {
                _visible = !_visible;
                if (_visible)
                {
                    // Re-check on every open, not every frame - a ping is a real network round
                    // trip, and the panel may be opened against a different backend than last time.
                    DbPostPatcherClient.CheckBackendAsync();
                    _giveItemResult = "";
                }
            }
        }

        public static void Draw()
        {
            if (!_visible)
            {
                return;
            }

            var width = Mathf.Min(560f, Screen.width - 40f);
            var height = Mathf.Min(700f, Screen.height - 40f);
            var rect = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);

            GUI.Box(rect, GUIContent.none);
            GUILayout.BeginArea(rect);

            GUILayout.Label("Debug Panel  (F9 to close)", HeaderStyle);

            // Plain buttons, not GUILayout.Toolbar/SelectionGrid - those hit the same IL2CPP
            // generic-method-unstripping crash this project otherwise avoids entirely (see CLAUDE.md).
            GUILayout.BeginHorizontal();
            for (var i = 0; i < PageLabels.Length; i++)
            {
                var prevPageColor = GUI.color;
                GUI.color = _page == i ? Color.white : new Color(0.6f, 0.6f, 0.6f);
                if (GUILayout.Button(PageLabels[i]))
                {
                    _page = i;
                    if (i == 3 && !_characterLoadedOnce)
                    {
                        RefreshCharacterState();
                    }
                }
                GUI.color = prevPageColor;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);

            switch (_page)
            {
                case 1:
                    DrawGiveItemSection();
                    break;
                case 2:
                    DrawPrestigeSection();
                    break;
                case 3:
                    DrawCharacterSection();
                    break;
                case 4:
                    DrawAirdropSection();
                    break;
                default:
                    DrawQuestPage();
                    break;
            }

            GUILayout.EndArea();
        }

        private static void DrawQuestPage()
        {
            var questController = ItemUiContext.Instance?.QuestController;
            var quests = questController?.Quests?.List;
            if (questController == null || quests == null)
            {
                GUILayout.Label("No quest data available yet (still loading into a profile?)");
                return;
            }

            void DrawSection(bool mainOnly, int tab)
            {
                var any = false;
                foreach (var quest in quests)
                {
                    if (quest?.Template == null || quest.Template.IsMainQuest != mainOnly)
                    {
                        continue;
                    }

                    var matchesTab = tab switch
                    {
                        // not yet accepted - AvailableForStart/AutoStart/AvailableAfter aren't
                        // something to force-finish, they need to be picked up through the real
                        // trader/task-accept flow, so this tab is view-only (no button).
                        1 => quest.QuestStatus is EQuestStatus.AvailableForStart
                            or EQuestStatus.AutoStart
                            or EQuestStatus.AvailableAfter,
                        2 => quest.QuestStatus == EQuestStatus.Success,
                        // accepted and in progress - the only ones a "Finish" makes sense for.
                        _ => quest.QuestStatus is EQuestStatus.Started or EQuestStatus.AvailableForFinish,
                    };
                    if (!matchesTab)
                    {
                        continue;
                    }

                    any = true;
                    GUILayout.BeginVertical(GUI.skin.box);

                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{quest.QuestStatus}] {quest.Name} ({quest.Id})");
                    GUILayout.FlexibleSpace();
                    if (tab == 1)
                    {
                        if (GUILayout.Button("Accept", GUILayout.Width(70f)))
                        {
                            TryAccept(questController, quest);
                        }
                    }
                    else if (tab == 2)
                    {
                        if (GUILayout.Button("Reset", GUILayout.Width(70f)))
                        {
                            TryReset(questController, quest);
                        }
                    }
                    else
                    {
                        if (GUILayout.Button("Finish", GUILayout.Width(70f)))
                        {
                            TryFinish(questController, quest);
                        }
                        if (GUILayout.Button("Force", GUILayout.Width(60f)))
                        {
                            TryForceFinish(questController, quest);
                        }
                        if (quest.QuestStatus == EQuestStatus.Started
                            && GUILayout.Button("Conditions", GUILayout.Width(80f)))
                        {
                            TrySatisfyConditions(questController, quest);
                        }
                    }
                    GUILayout.EndHorizontal();

                    // main-story quests all share the same title ("塔科夫之旅" etc covers every
                    // step of that chain) - the id in the header line and the description are what
                    // tell one step apart from the next, so both are shown for every quest, not just
                    // main. Some quests have neither a name nor a description locale entry - the
                    // locale table does still carry a string keyed by the AvailableForFinish
                    // condition's own id (that's what the native "go do X" hand-in hint reads from),
                    // so that's shown too whenever it differs from quest.Description, rather than
                    // only as a fallback for a blank one.
                    if (!string.IsNullOrEmpty(quest.Description))
                    {
                        GUILayout.Label(quest.Description, DescriptionStyle);
                    }
                    var conditionHint = GetConditionHint(quest);
                    if (!string.IsNullOrEmpty(conditionHint) && conditionHint != quest.Description)
                    {
                        GUILayout.Label(conditionHint, DescriptionStyle);
                    }

                    GUILayout.EndVertical();
                }

                if (!any)
                {
                    GUILayout.Label("(none)");
                }
            }

            // GUILayout.Toolbar/SelectionGrid hit the same IL2CPP generic-method-unstripping crash
            // this project otherwise avoids entirely (see CLAUDE.md) - plain buttons don't.
            GUILayout.BeginHorizontal();
            for (var i = 0; i < TabLabels.Length; i++)
            {
                var prevColor = GUI.color;
                GUI.color = _tab == i ? Color.white : new Color(0.6f, 0.6f, 0.6f);
                if (GUILayout.Button(TabLabels[i]))
                {
                    _tab = i;
                }
                GUI.color = prevColor;
            }
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label("Main story", HeaderStyle);
            DrawSection(mainOnly: true, tab: _tab);

            GUILayout.Space(10f);
            GUILayout.Label("Side quests", HeaderStyle);
            DrawSection(mainOnly: false, tab: _tab);

            GUILayout.EndScrollView();
        }

        // Doesn't touch profile data or call the prestige request itself - calls
        // InventoryScreen._prestigeScreen.Show directly (see PrestigeDebugPatches.ShowPrestigeScreen),
        // the same call the native tab's own button click ends up making, so the item-transfer
        // picker and confirmation dialog still run for real. Requires Inventory to be open already
        // (needs a live InventoryScreen instance to read profile/controllers/session off of).
        private static void DrawPrestigeSection()
        {
            GUILayout.BeginHorizontal(GUI.skin.box);
            GUILayout.Label("Prestige: open Inventory first, then show the screen directly");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Show Prestige Screen", GUILayout.Width(160f)))
            {
                PrestigeDebugPatches.ShowPrestigeScreen();
            }
            if (GUILayout.Button("Click Obtain Prestige", GUILayout.Width(160f)))
            {
                PrestigeDebugPatches.ClickObtainPrestige();
            }
            GUILayout.EndHorizontal();
        }

        // Calls AirdropManager.AirdropEvent directly - the same public engine entry point a real
        // flare/call-in button ends up invoking (its buttonClickLastTime param matches that use:
        // a debounce timestamp, not something specific to the flare item itself). Exists because
        // the natural triggers (PlaneAirdropChance/StartMin/StartMax in a map's base.json, or the
        // in-raid flare item) are both slow/low-probability enough to make testing airdrop markers
        // impractical. EXPERIMENTAL - only verified to compile against the decompiled interop
        // stub, not yet confirmed in-game to actually spawn a crate; if it silently does nothing,
        // check the log for what AirdropEvent's native side actually requires (e.g. a signal
        // location/target position it may need set first).
        private static string _airdropStatus = "";

        private static void DrawAirdropSection()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("Airdrop (experimental - see code comment)", HeaderStyle);
            GUILayout.Label(
                "Calls AirdropManager.AirdropEvent directly instead of waiting on the map's "
                + "PlaneAirdropChance timer or using a flare in-raid. Not yet confirmed in-game.",
                DescriptionStyle);

            if (GUILayout.Button("Force Airdrop", GUILayout.Width(160f)))
            {
                TryForceAirdrop();
            }

            if (!string.IsNullOrEmpty(_airdropStatus))
            {
                GUILayout.Label(_airdropStatus, DescriptionStyle);
            }

            GUILayout.EndVertical();
        }

        private static void TryForceAirdrop()
        {
            try
            {
                // GameWorld is a UnityEngine.Object-derived (MonoBehaviour) - explicit ifs instead
                // of ?., see git history/memory ("?./?? bypasses Unity's fake-null override").
                // AirdropManager/SynchronizableObjectLogicProcessor are plain Il2CppSystem.Object,
                // not UnityEngine.Object, so ?. on those hops is fine.
                var gameWorld = Singleton<GameWorld>.Instance;
                if (gameWorld == null)
                {
                    _airdropStatus = "No GameWorld - not in a raid.";
                    return;
                }

                var airdropManager = gameWorld.SynchronizableObjectLogicProcessor?.AirdropManager;
                if (airdropManager == null)
                {
                    _airdropStatus = "No AirdropManager available on this map/raid.";
                    return;
                }

                airdropManager.AirdropEvent(0L);
                _airdropStatus = $"AirdropEvent invoked at {DateTime.Now:HH:mm:ss} - watch the map/sky.";
                Plugin.Log.LogInfo("QuestDebugPanel: AirdropManager.AirdropEvent invoked (debug force-airdrop)");
            }
            catch (Exception e)
            {
                _airdropStatus = $"AirdropEvent threw: {e.Message}";
                Plugin.Log.LogError($"QuestDebugPanel: force-airdrop threw: {e}");
            }
        }

        // Writes body-part max HP / hydration / energy / temperature / skill progress back to the
        // live server profile (Server/DbPostPatcher/Routing/CharacterDebugRouter.cs), persisted via
        // SaveServer.SaveProfileAsync - so it survives a relog, not just the current raid. A raid
        // already in progress keeps its own live health snapshot and pushes it back to the profile
        // at raid end, which would overwrite these edits - apply them from the main menu, or expect
        // to see them from the *next* raid rather than the current one.
        private static void ApplyCharacterState(DbPostPatcherClient.CharacterState state)
        {
            if (state == null)
            {
                return;
            }

            if (state.BodyParts != null)
            {
                foreach (var part in BodyPartNames)
                {
                    if (state.BodyParts.TryGetValue(part, out var vital) && vital != null)
                    {
                        _bodyPartDisplay[part] = $"{vital.Current:0.#} / {vital.Maximum:0.#}";
                        _bodyPartInput[part] = vital.Maximum.ToString("0.#");
                    }
                }
            }

            void ApplyVital(string name, DbPostPatcherClient.VitalState vital)
            {
                if (vital == null)
                {
                    return;
                }
                _vitalDisplay[name] = $"{vital.Current:0.#} / {vital.Maximum:0.#}";
                _vitalInput[name] = vital.Maximum.ToString("0.#");
            }

            ApplyVital("hydration", state.Hydration);
            ApplyVital("energy", state.Energy);
            ApplyVital("temperature", state.Temperature);

            if (state.Skills != null)
            {
                foreach (var skill in state.Skills)
                {
                    if (skill?.Id == null)
                    {
                        continue;
                    }
                    _skillDisplay[skill.Id] = skill.Progress.ToString("0.#");
                    _skillInput[skill.Id] = skill.Progress.ToString("0.#");
                }
            }
        }

        private static void RefreshCharacterState()
        {
            _characterLoadedOnce = true;
            _characterStatus = "Loading...";
            DbPostPatcherClient.FetchCharacterStateAsync((state, status) =>
            {
                _characterStatus = state != null ? "Loaded current profile values." : status;
                ApplyCharacterState(state);
            });
        }

        private static void DrawCharacterSection()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("Character (writes back to your server profile - see status line below)", HeaderStyle);

            if (GUILayout.Button("Refresh from server", GUILayout.Width(160f)))
            {
                RefreshCharacterState();
            }

            GUILayout.Space(4f);
            GUILayout.Label("Body part max HP", HeaderStyle);
            foreach (var part in BodyPartNames)
            {
                _bodyPartDisplay.TryAdd(part, "?");
                _bodyPartInput.TryAdd(part, "");

                GUILayout.BeginHorizontal();
                GUILayout.Label(part, GUILayout.Width(80f));
                GUILayout.Label(_bodyPartDisplay[part], DescriptionStyle, GUILayout.Width(90f));
                _bodyPartInput[part] = GUILayout.TextField(_bodyPartInput[part], GUILayout.Width(70f));
                if (GUILayout.Button("Set", GUILayout.Width(50f))
                    && double.TryParse(_bodyPartInput[part], out var value))
                {
                    _characterStatus = $"Setting {part}...";
                    DbPostPatcherClient.SetBodyPartAsync(part, value, (state, status) =>
                    {
                        _characterStatus = state != null ? $"{part} max HP set to {value:0.#}." : status;
                        ApplyCharacterState(state);
                    });
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(4f);
            GUILayout.Label("Vitals", HeaderStyle);
            foreach (var vitalName in VitalNames)
            {
                _vitalDisplay.TryAdd(vitalName, "?");
                _vitalInput.TryAdd(vitalName, "");

                GUILayout.BeginHorizontal();
                GUILayout.Label(vitalName, GUILayout.Width(80f));
                GUILayout.Label(_vitalDisplay[vitalName], DescriptionStyle, GUILayout.Width(90f));
                _vitalInput[vitalName] = GUILayout.TextField(_vitalInput[vitalName], GUILayout.Width(70f));
                if (GUILayout.Button("Set", GUILayout.Width(50f))
                    && double.TryParse(_vitalInput[vitalName], out var value))
                {
                    _characterStatus = $"Setting {vitalName}...";
                    DbPostPatcherClient.SetVitalAsync(vitalName, value, (state, status) =>
                    {
                        _characterStatus = state != null ? $"{vitalName} max set to {value:0.#}." : status;
                        ApplyCharacterState(state);
                    });
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(4f);
            GUILayout.Label("Skills (progress, 0-5100 - level is roughly progress / 100)", HeaderStyle);
            _skillScroll = GUILayout.BeginScrollView(_skillScroll, GUI.skin.box, GUILayout.Height(180f));
            foreach (var skillId in AllSkillNames)
            {
                _skillDisplay.TryAdd(skillId, "?");
                _skillInput.TryAdd(skillId, "");

                GUILayout.BeginHorizontal();
                GUILayout.Label(skillId, GUILayout.Width(150f));
                GUILayout.Label(_skillDisplay[skillId], DescriptionStyle, GUILayout.Width(60f));
                _skillInput[skillId] = GUILayout.TextField(_skillInput[skillId], GUILayout.Width(70f));
                if (GUILayout.Button("Set", GUILayout.Width(50f))
                    && double.TryParse(_skillInput[skillId], out var progress))
                {
                    _characterStatus = $"Setting skill '{skillId}'...";
                    DbPostPatcherClient.SetSkillAsync(skillId, progress, (state, status) =>
                    {
                        _characterStatus = state != null ? $"Skill '{skillId}' progress set to {progress:0.#}." : status;
                        ApplyCharacterState(state);
                    });
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            GUILayout.Space(4f);
            GUILayout.Label(_characterStatus, DescriptionStyle);

            GUILayout.EndVertical();
        }

        // Delivers the picked item as an in-game mailed attachment (DbPostPatcher's
        // /dbpostpatcher/give-item route + MailSendService) rather than trying to splice it
        // straight into the live inventory - see DbPostPatcherClient's header comment for why.
        // DbPostPatcherClient.BackendAvailable is null until the first ping response lands
        // (fired from HandleInput on panel open), so there's a brief "checking..." state.
        private static void DrawGiveItemSection()
        {
            // Refresh the snapshot at most once per Unity frame - see the fields' comment above for
            // why a mid-frame refresh (between this frame's Layout and Repaint OnGUI passes) would
            // reintroduce the mismatched-layout crash.
            if (_giveItemSnapshotFrame != Time.frameCount)
            {
                _giveItemSnapshotFrame = Time.frameCount;
                _giveItemSnapshotBackendAvailable = DbPostPatcherClient.BackendAvailable;
                _giveItemSnapshotLastStatus = DbPostPatcherClient.LastStatus;
                _giveItemSnapshotCatalog = DbPostPatcherClient.Catalog;
                _giveItemSnapshotResult = _giveItemResult;
            }

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("Give Item (via DbPostPatcher mail route)", HeaderStyle);

            switch (_giveItemSnapshotBackendAvailable)
            {
                case null:
                    GUILayout.Label("Checking backend...");
                    break;
                case false:
                    GUILayout.Label(
                        "DbPostPatcher not detected on this backend - Give Item is unavailable. "
                        + (string.IsNullOrEmpty(_giveItemSnapshotLastStatus) ? "" : _giveItemSnapshotLastStatus),
                        DescriptionStyle);
                    break;
                case true:
                    var catalog = _giveItemSnapshotCatalog;
                    if (catalog.Length == 0)
                    {
                        GUILayout.Label("Backend detected, but its item catalog is empty.");
                    }
                    foreach (var entry in catalog)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(entry.Label);
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Add", GUILayout.Width(60f)))
                        {
                            var label = entry.Label;
                            _giveItemResult = $"Sending '{label}'...";
                            DbPostPatcherClient.GiveItemAsync(entry.ItemTemplateId, label, result => _giveItemResult = result);
                        }
                        GUILayout.EndHorizontal();
                    }
                    break;
            }

            if (!string.IsNullOrEmpty(_giveItemSnapshotResult))
            {
                GUILayout.Label(_giveItemSnapshotResult, DescriptionStyle);
            }

            GUILayout.EndVertical();
        }

        // AcceptQuest is the same entry point the native Tasks/trader "Accept" button ends up
        // calling - it returns Il2CppSystem.Threading.Tasks.Task<OperationResult<QuestAcceptResult>>
        // (a real client-server round trip), but the result is intentionally not awaited/polled here
        // - see PrestigeDebugPatches.Postfix's comment for why awaiting/ContinueWith on an Il2Cpp
        // Task is unverified in this project (delegate marshaling risk). Fire-and-forget, same as
        // TryFinish/TryForceFinish below - the quest list will simply show the new status once the
        // server round trip lands and the native quest-list refresh picks it up.
        private static void TryAccept(QuestController questController, Quest quest)
        {
            try
            {
                if (quest.QuestStatus != EQuestStatus.AvailableForStart
                    && quest.QuestStatus != EQuestStatus.AutoStart
                    && quest.QuestStatus != EQuestStatus.AvailableAfter)
                {
                    return;
                }

                questController.AcceptQuest(quest, false);
                Plugin.Log.LogInfo($"QuestDebugPanel: AcceptQuest invoked for '{quest.Name}' ({quest.Id})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"QuestDebugPanel: AcceptQuest threw for '{quest?.Name}': {e}");
            }
        }

        private static void TryFinish(QuestController questController, Quest quest)
        {
            try
            {
                if (quest.QuestStatus == EQuestStatus.Success)
                {
                    // already went through on an earlier click (list hasn't repainted yet this
                    // frame) - re-invoking just produces a scary "Conditional ... status: Success"
                    // engine error for no reason.
                    return;
                }

                // TryInstantFinishQuest on its own refuses with "conditional is not available for
                // finish" unless the quest has already naturally reached AvailableForFinish - i.e.
                // every hand-in condition (item found, zone visited, ...) already fired for real.
                // That's exactly the trigger a buggy/dev-incomplete quest can't produce. Force each
                // still-open condition through CompleteConditionGeneric first - the same engine call
                // a real trigger (pickup, zone enter, ...) ends up making - so the quest's own
                // condition/status bookkeeping advances it to AvailableForFinish itself, then finish
                // it for real.
                ForceCompleteConditions(questController, quest);

                if (quest.QuestStatus != EQuestStatus.AvailableForFinish)
                {
                    // one or more conditions didn't resolve (see the "Can't find condition for
                    // target ..." engine warning) - usually a quest-data issue (a target item/zone
                    // id the current SPT quest DB doesn't have), not something this bypass can push
                    // past. Calling TryInstantFinishQuest anyway would just log another engine error.
                    Plugin.Log.LogWarning(
                        $"QuestDebugPanel: '{quest.Name}' ({quest.Id}) still not AvailableForFinish "
                        + $"after forcing conditions (status: {quest.QuestStatus}) - likely a quest-data "
                        + "issue (missing target item/zone), not fixable from here. Skipping finish.");
                    return;
                }

                questController.TryInstantFinishQuest(quest);
                Plugin.Log.LogInfo($"QuestDebugPanel: TryInstantFinishQuest invoked for '{quest.Name}' ({quest.Id})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"QuestDebugPanel: TryInstantFinishQuest threw for '{quest?.Name}': {e}");
            }
        }

        // For testing whether a quest's own native completion trigger (the in-raid zone/item/kill
        // event, or the Tasks-screen "Complete quest" button) actually works - some SPT bugs live in
        // that path itself, not in reaching AvailableForFinish, so TryFinish's auto-complete would
        // paper over exactly the thing being tested. Only forces the hand-in conditions through
        // CompleteConditionGeneric (same as TryFinish's first step) and stops - the quest is left at
        // AvailableForFinish for the player to complete for real via the native UI.
        private static void TrySatisfyConditions(QuestController questController, Quest quest)
        {
            try
            {
                if (quest.QuestStatus != EQuestStatus.Started)
                {
                    return;
                }

                ForceCompleteConditions(questController, quest);

                if (quest.QuestStatus != EQuestStatus.AvailableForFinish)
                {
                    Plugin.Log.LogWarning(
                        $"QuestDebugPanel: '{quest.Name}' ({quest.Id}) still not AvailableForFinish "
                        + $"after forcing conditions (status: {quest.QuestStatus}) - likely a quest-data "
                        + "issue (missing target item/zone), not fixable from here.");
                    return;
                }

                Plugin.Log.LogInfo(
                    $"QuestDebugPanel: conditions satisfied for '{quest.Name}' ({quest.Id}) - "
                    + "complete it via the native Tasks UI to test the real completion trigger.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"QuestDebugPanel: satisfy-conditions threw for '{quest?.Name}': {e}");
            }
        }

        // Last resort for quests whose data is genuinely broken (duplicate/colliding condition or
        // counter ids across quest templates, a missing alternate condition branch, etc - see the
        // "Can't find condition for target/Counter with id ... can't be found" engine warnings) so
        // CompleteConditionGeneric can never legitimately walk them to AvailableForFinish. Skips
        // straight past the condition check by setting status directly to AvailableForFinish via
        // SetConditionalStatus - the same real status-change entry point Reset uses - then still
        // finishes through TryInstantFinishQuest so reward granting/backend sync/quest-chain
        // unlocks fire for real; only the "did the conditions actually happen" gate is bypassed.
        private static void TryForceFinish(QuestController questController, Quest quest)
        {
            try
            {
                if (quest.QuestStatus == EQuestStatus.Success)
                {
                    return;
                }

                if (quest.QuestStatus != EQuestStatus.AvailableForFinish)
                {
                    questController.SetConditionalStatus(quest, EQuestStatus.AvailableForFinish);
                }

                questController.TryInstantFinishQuest(quest);
                Plugin.Log.LogInfo($"QuestDebugPanel: force-finished '{quest.Name}' ({quest.Id})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"QuestDebugPanel: force-finish threw for '{quest?.Name}': {e}");
            }
        }

        private static void TryReset(QuestController questController, Quest quest)
        {
            try
            {
                if (quest.QuestStatus != EQuestStatus.Success)
                {
                    return;
                }

                // SetConditionalStatus is the engine's own general-purpose status-change entry
                // point (ConditionalController<T>, shared by every conditional-status transition
                // the game makes) - it fires the same OnConditionalStatusChanged notification path
                // as a real transition, rather than poking the private QuestStatus backing field
                // directly.
                questController.SetConditionalStatus(quest, EQuestStatus.Started);
                Plugin.Log.LogInfo($"QuestDebugPanel: reset '{quest.Name}' ({quest.Id}) back to Started");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"QuestDebugPanel: reset threw for '{quest?.Name}': {e}");
            }
        }

        // condition.id doubles as a locale key for the hand-in hint text (e.g. "在立交桥、中心区、
        // 森林或海关消灭 Scav") - clearer than an empty/missing quest.Description for quests whose
        // locale entry only fills in the condition strings, not the quest name/description fields.
        private static string GetConditionHint(Quest quest)
        {
            var conditions = GetAvailableForFinishConditions(quest);
            if (conditions == null)
            {
                return null;
            }

            foreach (var condition in conditions)
            {
                var hint = condition?.id.ToString().BSGLocalized();
                if (!string.IsNullOrEmpty(hint))
                {
                    return hint;
                }
            }

            return null;
        }

        private static Il2CppSystem.Collections.Generic.List<Condition> GetAvailableForFinishConditions(Quest quest)
        {
            var conditionsByStatus = quest.Template?.Conditions;
            if (conditionsByStatus == null
                || !conditionsByStatus.TryGetValue(EQuestStatus.AvailableForFinish, out var conditions)
                || conditions?.List == null)
            {
                return null;
            }

            return conditions.List;
        }

        private static void ForceCompleteConditions(QuestController questController, Quest quest)
        {
            var conditions = GetAvailableForFinishConditions(quest);
            if (conditions == null)
            {
                return;
            }

            foreach (var condition in conditions)
            {
                if (condition == null)
                {
                    continue;
                }

                if (quest.IsConditionDone(condition))
                {
                    continue;
                }

                try
                {
                    questController.CompleteConditionGeneric(quest, condition);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning(
                        $"QuestDebugPanel: CompleteConditionGeneric failed for a '{condition.GetType().Name}' "
                        + $"condition on '{quest.Name}': {e}");
                }
            }
        }
    }
}
