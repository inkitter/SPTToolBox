using System.Collections.Generic;
using EFT;

namespace SPTMap.Utils
{
    // Best-effort localized display names for map markers, in the game's current language.
    // EFT's Localized() hands back the key itself when a locale entry is missing, so "result ==
    // key" (or blank) is treated as "no translation" and the caller falls back to the next source.
    public static class LocalizationUtils
    {
        public static string TryLocalize(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            string value;
            try
            {
                value = key.Localized();
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(value) || value == key)
            {
                return null;
            }

            return value;
        }

        public static bool IsBlank(string s) => string.IsNullOrWhiteSpace(s);

        // --- bots ---------------------------------------------------------------------------

        private const string BotRoleKeyPrefix = "QuestCondition/Elimination/Kill/BotRole/";

        // Boss names the locale doesn't carry under any key (checked against en/ch.json
        // 2026-09-22). The Chinese locale keeps boss names in English too, so English is used.
        private static readonly Dictionary<string, string> BossFallbackNames = new()
        {
            ["bossBoar"] = "Kaban",
            ["bossKolontay"] = "Kollontay",
            ["bossZryachiy"] = "Zryachiy",
            ["bossPartisan"] = "Partisan",
            ["bossKnight"] = "Knight",
            ["followerBigPipe"] = "Big Pipe",
            ["followerBirdEye"] = "Birdeye",
            ["gifter"] = "Santa",
            ["bossTagillaAgro"] = "Tagilla",
            ["bossKillaAgro"] = "Killa",
            ["tagillaHelperAgro"] = "Tagilla's helper",
            ["199"] = "Legion",
            ["801"] = "Punisher",
        };

        // role name -> ScavRole/* locale key (localized role labels, e.g. Raider / 掠夺者)
        private static readonly Dictionary<string, string> RoleLocaleKeys = new()
        {
            ["pmcBot"] = "ScavRole/PmcBot",
            ["exUsec"] = "ScavRole/ExUsec",
            ["marksman"] = "ScavRole/Marksman",
            ["arenaFighter"] = "ScavRole/ArenaFighter",
            ["arenaFighterEvent"] = "ScavRole/ArenaFighterEvent",
            ["civilian"] = "SCAVROLE/CIVILIAN",
            ["blackDivision"] = "ScavRole/BlackDivision",
            ["sentry"] = "ScavRole/Sentry",
            ["vsrf"] = "ScavRole/VSRF",
            ["sectantWarrior"] = BotRoleKeyPrefix + "cursedAssault",
            ["cursedAssault"] = BotRoleKeyPrefix + "cursedAssault",
            ["sectantPriest"] = BotRoleKeyPrefix + "sectantPriest",
        };

        // Bot nicknames are raw Russian strings from the server's bot name pools with no locale
        // entry, so bots are labelled by (localized) role instead; PMCs keep their nickname
        // (player-style Latin names), and the nickname is the last resort for anything unmapped.
        public static string GetPlayerDisplayName(Player player, bool isPmc, bool isBoss)
        {
            var nickname = player.Profile?.Info?.Nickname;
            if (isPmc)
            {
                return nickname;
            }

            var role = player.Profile?.Info?.Settings != null ? player.Profile.Info.Settings.Role.ToString() : null;
            if (IsBlank(role))
            {
                return nickname;
            }

            if (isBoss)
            {
                var baseRole = role.EndsWith("Agro") ? role.Substring(0, role.Length - 4) : role;
                return TryLocalize(BotRoleKeyPrefix + baseRole)
                    ?? (BossFallbackNames.TryGetValue(role, out var fallback) ? fallback : null)
                    ?? nickname;
            }

            string localized = null;
            if (RoleLocaleKeys.TryGetValue(role, out var key))
            {
                localized = TryLocalize(key);
            }
            else if (role.StartsWith("infected"))
            {
                localized = TryLocalize("ScavRole/" + role);
            }
            else if (role.StartsWith("follower"))
            {
                localized = TryLocalize("ScavRole/Follower");
            }

            return localized
                ?? TryLocalize(BotRoleKeyPrefix + role)
                ?? TryLocalize(BotRoleKeyPrefix + "assault")
                ?? nickname;
        }
    }
}
