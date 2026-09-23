using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.Quests;
using Il2CppInterop.Runtime;
using SPTMap.Data;
using UnityEngine;

namespace SPTMap.Utils
{
    // Ported from the old SPT-DynamicMaps project's QuestUtils (itself adapted from Prop's GTFO
    // mod, https://github.com/dvize/GTFO, MIT licensed).
    public static class QuestUtils
    {
        private const string Category = "Quest";
        private const string ImagePath = "Markers/quest.png";
        private static readonly Color MarkerColor = Color.green;

        private static List<TriggerWithId> _triggersWithIds;
        private static List<LootItem> _questItems;

        // Not an error case - most incomplete quests target a zone on a *different* map than the
        // one currently loaded, so "zone not found here" is the common case, not the exception.
        // QuestMarkerProvider re-derives every quest's markers on a 3s Tick for the whole raid, so
        // logging this unconditionally would repeat the same "not on this map" line for the same
        // zoneId every 3 seconds for the entire raid. Once per distinct zoneId per raid is enough to
        // still catch a genuine typo'd/renamed zone id on the *current* map.
        private static readonly HashSet<string> _warnedMissingZoneIds = new();

        internal static void TryCaptureQuestData()
        {
            _triggersWithIds ??= Object.FindObjectsOfType<TriggerWithId>().ToList();

            // Unlike triggers (static geometry, fine to cache once), quest item loot isn't
            // reliably present in the world the instant raid start fires - re-derive every call
            // (QuestMarkerProvider calls this on each map-open refresh) instead of capturing once
            // and going stale. Reads LootScanCache.Items rather than scanning GameWorld.LootList
            // itself - SPTMapController rescans that cache once per refresh, shared with
            // ItemMarkerProvider, so the whole loot list isn't walked twice for the same event.
            _questItems = new List<LootItem>();
            foreach (var loot in LootScanCache.Items)
            {
                if (loot.Item.QuestItem)
                {
                    _questItems.Add(loot);
                }
            }
        }

        internal static void DiscardQuestData()
        {
            _triggersWithIds?.Clear();
            _triggersWithIds = null;

            _questItems?.Clear();
            _questItems = null;

            _warnedMissingZoneIds.Clear();
        }

        internal static IEnumerable<MapMarker> GetMarkersForPlayer(Player player)
        {
            if (_triggersWithIds == null || _questItems == null)
            {
                Plugin.Log.LogWarning($"QuestUtils: quest data not captured yet (triggers null: {_triggersWithIds == null}, items null: {_questItems == null})");
                yield break;
            }

            foreach (var quest in GetIncompleteQuests(player))
            {
                foreach (var marker in GetMarkersForQuest(player, quest))
                {
                    yield return marker;
                }
            }
        }

        private static IEnumerable<MapMarker> GetMarkersForQuest(Player player, Quest quest)
        {
            var seenPositions = new List<Vector2>();

            foreach (var condition in GetIncompleteQuestConditions(player, quest))
            {
                var questName = GetQuestDisplayName(quest);

                foreach (var worldPosition in GetPositionsForCondition(condition))
                {
                    var position = MathUtils.ConvertToMapPosition(worldPosition);

                    var alreadySeen = false;
                    foreach (var seen in seenPositions)
                    {
                        if (MathUtils.ApproxEquals(seen.x, position.x) && MathUtils.ApproxEquals(seen.y, position.y))
                        {
                            alreadySeen = true;
                            break;
                        }
                    }

                    if (alreadySeen)
                    {
                        continue;
                    }

                    seenPositions.Add(position);
                    yield return new MapMarker
                    {
                        Category = Category,
                        ImagePath = ImagePath,
                        Text = questName,
                        ShowLabel = true,
                        Color = MarkerColor,
                        GetPosition = () => position,
                        GetWorldPosition = () => worldPosition,
                    };
                }
            }
        }

        // Some quests have no localized name - fall back to the description, then the quest id,
        // so the hover label is never blank. null, "" and whitespace-only all count as empty.
        private static string GetQuestDisplayName(Quest quest)
        {
            string name = null;
            try { name = quest.Template.NameLocaleKey.BSGLocalized(); } catch { }
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            string description = null;
            try { description = quest.Description; } catch { }
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }

            return quest.Id;
        }

        // IL2CPP interop gotcha: elements pulled out of an Il2Cpp List<Condition> come back
        // wrapped as the declared element type (Condition), not the actual runtime subtype, so a
        // C# `switch`/`is` pattern match against ConditionZone/ConditionVisitPlace/etc. never
        // matches. TryCast<T> against the underlying il2cpp object is required instead - see
        // UnitMarkerProvider for the same pattern.
        private static IEnumerable<Vector3> GetPositionsForCondition(Condition condition)
        {
            // ConditionLeaveItemAtLocation and ConditionPlaceBeacon both derive from ConditionZone,
            // so they land here too. Their `target` holds the *item* template ids to place, not
            // zone ids - the zones live in `zoneIds`. Only fall back to `target` if zoneIds is empty.
            if (condition.TryCast<ConditionZone>() is { } zoneCondition)
            {
                var zoneIds = zoneCondition.zoneIds;
                var useZoneIds = zoneIds != null && zoneIds.Length > 0;
                foreach (var zoneId in useZoneIds ? zoneIds : zoneCondition.target)
                {
                    foreach (var position in GetPositionsForZoneId(zoneId))
                    {
                        yield return position;
                    }
                }
            }
            else if (condition.TryCast<ConditionLaunchFlare>() is { } flareCondition)
            {
                foreach (var position in GetPositionsForZoneId(flareCondition.zoneID))
                {
                    yield return position;
                }
            }
            else if (condition.TryCast<ConditionVisitPlace>() is { } place)
            {
                foreach (var position in GetPositionsForZoneId(place.target))
                {
                    yield return position;
                }
            }
            else if (condition.TryCast<ConditionInZone>() is { } zone)
            {
                foreach (var zoneId in zone.zoneIds)
                {
                    foreach (var position in GetPositionsForZoneId(zoneId))
                    {
                        yield return position;
                    }
                }
            }
            else if (condition.TryCast<ConditionFindItem>() is { } findItemCondition)
            {
                foreach (var position in GetPositionsForQuestItems(findItemCondition.target))
                {
                    yield return position;
                }
            }
            else if (condition.TryCast<ConditionExitName>() is { } exitCondition)
            {
                var exfils = Singleton<GameWorld>.Instance.ExfiltrationController.ExfiltrationPoints;
                foreach (var exit in exfils)
                {
                    if (exit.Settings.Name == exitCondition.exitName)
                    {
                        yield return exit.transform.position;
                        break;
                    }
                }
            }
            else if (condition.TryCast<ConditionCounterCreator>() is { } conditionCreator)
            {
                foreach (var position in GetPositionsForConditionCreator(conditionCreator))
                {
                    yield return position;
                }
            }
        }

        private static IEnumerable<Vector3> GetPositionsForConditionCreator(ConditionCounterCreator conditionCreator)
        {
            foreach (var condition in conditionCreator._templateConditions.Conditions.List)
            {
                foreach (var position in GetPositionsForCondition(condition))
                {
                    yield return position;
                }
            }
        }

        private static IEnumerable<Vector3> GetPositionsForZoneId(string zoneId)
        {
            var any = false;
            if (_triggersWithIds != null)
            {
                foreach (var trigger in _triggersWithIds)
                {
                    if (trigger.Id == zoneId)
                    {
                        any = true;
                        yield return trigger.transform.position;
                    }
                }
            }

            if (!any && _warnedMissingZoneIds.Add(zoneId))
            {
                var knownIds = _triggersWithIds == null ? "(null)" : string.Join(", ", _triggersWithIds.Select(t => t.Id));
                Plugin.Log.LogWarning($"QuestUtils: no TriggerWithId found for zoneId '{zoneId}'. Known ids ({_triggersWithIds?.Count}): {knownIds}");
            }
        }

        private static IEnumerable<Vector3> GetPositionsForQuestItems(IEnumerable<string> questItemIds)
        {
            foreach (var questItemId in questItemIds)
            {
                if (_questItems == null)
                {
                    continue;
                }

                foreach (var item in _questItems)
                {
                    if (item.TemplateId == questItemId)
                    {
                        yield return item.transform.position;
                    }
                }
            }
        }

        private static IEnumerable<Condition> GetIncompleteQuestConditions(Player player, Quest quest)
        {
            if (quest?.Template?.Conditions == null)
            {
                yield break;
            }

            if (!quest.Template.Conditions.TryGetValue(EQuestStatus.AvailableForFinish, out var conditions) || conditions == null)
            {
                yield break;
            }

            foreach (var condition in conditions.List)
            {
                if (condition == null || IsConditionCompleted(player, quest, condition))
                {
                    continue;
                }

                yield return condition;
            }
        }

        private static IEnumerable<Quest> GetIncompleteQuests(Player player)
        {
            var questsList = player.QuestController?.Quests?.List;
            if (questsList == null)
            {
                yield break;
            }

            foreach (var quest in questsList)
            {
                if (quest?.Template?.Conditions == null || quest.QuestStatus != EQuestStatus.Started)
                {
                    continue;
                }

                yield return quest;
            }
        }

        private static bool IsConditionCompleted(Player player, Quest questData, Condition condition)
        {
            // CompletedConditions is inaccurate (doesn't reset when some quests do on death, and
            // doesn't contain optional objectives) - it's just a cheap first-pass filter, followed
            // by the authoritative IsConditionDone check below.
            if (condition.IsNecessary && !questData.CompletedConditions.Contains(condition))
            {
                return false;
            }

            var quest = player.QuestController?.Quests?.GetConditional(questData.Id);
            return quest != null && quest.IsConditionDone(condition);
        }
    }
}
