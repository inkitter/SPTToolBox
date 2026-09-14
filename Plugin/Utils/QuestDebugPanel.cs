using System;
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

        private static bool _visible;
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

        public static void HandleInput()
        {
            if (Input.GetKeyDown(ToggleKey))
            {
                _visible = !_visible;
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

            GUILayout.Label("Quest Debug Panel  (F9 to close)", HeaderStyle);

            var questController = ItemUiContext.Instance?.QuestController;
            var quests = questController?.Quests?.List;
            if (questController == null || quests == null)
            {
                GUILayout.Label("No quest data available yet (still loading into a profile?)");
                GUILayout.EndArea();
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
                    GUILayout.Label($"[{quest.QuestStatus}] {quest.Name}");
                    GUILayout.FlexibleSpace();
                    if (tab == 1)
                    {
                        // no button - accepting a quest for real needs the actual trader/task UI
                        // flow, not a bypass.
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
                    }
                    GUILayout.EndHorizontal();

                    // main-story quests all share the same title ("塔科夫之旅" etc covers every
                    // step of that chain) - the description is the only thing on screen that tells
                    // one step apart from the next, so it's shown for every quest, not just main.
                    if (!string.IsNullOrEmpty(quest.Description))
                    {
                        GUILayout.Label(quest.Description, DescriptionStyle);
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
            GUILayout.EndArea();
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

        private static void ForceCompleteConditions(QuestController questController, Quest quest)
        {
            var conditionsByStatus = quest.Template?.Conditions;
            if (conditionsByStatus == null
                || !conditionsByStatus.TryGetValue(EQuestStatus.AvailableForFinish, out var conditions)
                || conditions?.List == null)
            {
                return;
            }

            foreach (var condition in conditions.List)
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
