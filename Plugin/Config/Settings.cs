using BepInEx.Configuration;

namespace SPTMap.Config
{
    public enum MiniMapAnchor
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    internal static class Settings
    {
        private const string MiniMapTitle = "Mini-map";
        private const string MarkersTitle = "Markers";
        private const string EspTitle = "Enemy ESP";

        public static ConfigEntry<int> MiniMapWidth;
        public static ConfigEntry<float> ZoomSpeed;
        public static ConfigEntry<MiniMapAnchor> Anchor;
        public static ConfigEntry<int> PaddingX;
        public static ConfigEntry<int> PaddingY;

        public static ConfigEntry<bool> ShowPmc;
        public static ConfigEntry<bool> ShowScav;
        public static ConfigEntry<bool> ShowBoss;
        public static ConfigEntry<float> OtherPlayersPollIntervalMs;
        public static ConfigEntry<bool> ShowFriendlyPlayerLabels;
        public static ConfigEntry<bool> ShowEnemyPlayerLabels;
        public static ConfigEntry<bool> ShowExtractLabels;
        public static ConfigEntry<bool> ShowTransitLabels;
        public static ConfigEntry<bool> ShowOtherMarkerLabels;
        public static ConfigEntry<bool> ShowLootableContainers;
        public static ConfigEntry<bool> ShowWishlist;

        public static ConfigEntry<float> EspDistance;
        public static ConfigEntry<bool> ShowBodyPartHealth;
        public static ConfigEntry<bool> ShowHitDamageNumbers;
        public static ConfigEntry<bool> ShowHitDamageNumbersV2;
        public static ConfigEntry<bool> ShowAiInfo;

        public static void Init(ConfigFile config)
        {
            MiniMapWidth = config.Bind(
                MiniMapTitle,
                "Mini-map width",
                220,
                new ConfigDescription(
                    "Width of the mini-map in pixels; height follows the current map's aspect ratio",
                    new AcceptableValueRange<int>(200, 800)));

            ZoomSpeed = config.Bind(
                MiniMapTitle,
                "Zoom speed",
                2f,
                new ConfigDescription(
                    "Multiplicative zoom rate per second when holding keypad 8/5",
                    new AcceptableValueRange<float>(1.2f, 6f)));

            Anchor = config.Bind(
                MiniMapTitle,
                "Anchor corner",
                MiniMapAnchor.TopRight,
                "Which screen corner the mini-map is anchored to");

            PaddingX = config.Bind(
                MiniMapTitle,
                "Horizontal padding",
                10,
                new ConfigDescription(
                    "Horizontal distance in pixels from the anchored screen edge",
                    new AcceptableValueRange<int>(0, 400)));

            PaddingY = config.Bind(
                MiniMapTitle,
                "Vertical padding",
                10,
                new ConfigDescription(
                    "Vertical distance in pixels from the anchored screen edge",
                    new AcceptableValueRange<int>(0, 400)));

            ShowPmc = config.Bind(
                MarkersTitle,
                "Show PMCs",
                true,
                "Whether to render markers for enemy PMC players");

            ShowScav = config.Bind(
                MarkersTitle,
                "Show scavs",
                true,
                "Whether to render markers for scav players");

            ShowBoss = config.Bind(
                MarkersTitle,
                "Show bosses",
                true,
                "Whether to render markers for tracked boss players");

            OtherPlayersPollIntervalMs = config.Bind(
                MarkersTitle,
                "Other-players poll interval (ms)",
                150f,
                new ConfigDescription(
                    "How often (in milliseconds) other players'/corpses' tracked state is refreshed. "
                    + "Lower = smoother but more overhead; higher = cheaper but marker positions lag "
                    + "more between updates.",
                    new AcceptableValueRange<float>(50f, 1000f)));

            ShowFriendlyPlayerLabels = config.Bind(
                MarkersTitle,
                "Show friendly player name labels",
                true,
                "Whether teammates show their nickname as a small always-on label instead of only "
                + "on hover");

            ShowEnemyPlayerLabels = config.Bind(
                MarkersTitle,
                "Show enemy player name labels",
                true,
                "Whether enemy PMCs/scavs/bosses show their nickname as a small always-on label "
                + "instead of only on hover");

            ShowExtractLabels = config.Bind(
                MarkersTitle,
                "Show extract name labels",
                true,
                "Whether extract points (including secret extracts) show their name as a small "
                + "always-on label instead of only on hover");

            ShowTransitLabels = config.Bind(
                MarkersTitle,
                "Show transit point name labels",
                true,
                "Whether transit points show their name as a small always-on label instead of only "
                + "on hover");

            ShowOtherMarkerLabels = config.Bind(
                MarkersTitle,
                "Show other marker labels",
                true,
                "Whether remaining markers that opt into it (airdrops, BTR, dropped backpack) show "
                + "their name as a small always-on label instead of only on hover");

            ShowLootableContainers = config.Bind(
                MarkersTitle,
                "Show lootable containers",
                false,
                "Whether to render markers for every lootable container on the map (ammo boxes, "
                + "weapon crates, medbags, safes, etc. - not a curated subset). Off by default - "
                + "spoiler-y for players who don't want loot locations highlighted, and one map can "
                + "have dozens of these");

            ShowWishlist = config.Bind(
                MarkersTitle,
                "Show wishlist items",
                false,
                "Whether to render markers for loose loot matching the profile's wishlist (off by "
                + "default)");

            EspDistance = config.Bind(
                EspTitle,
                "ESP distance (m)",
                0f,
                new ConfigDescription(
                    "Draws an outline + chest HP readout over enemies within this distance, through "
                    + "walls. 0 disables the overlay entirely.",
                    new AcceptableValueRange<float>(0f, 300f)));

            ShowBodyPartHealth = config.Bind(
                EspTitle,
                "Show per-body-part health",
                false,
                "Replaces the single chest HP readout with a health number at each body part's "
                + "position on the ESP box (head above the box, chest/stomach inside it, arms on "
                + "the sides, legs at the bottom corners)");

            ShowHitDamageNumbers = config.Bind(
                EspTitle,
                "Show hit damage numbers",
                true,
                "Pops a floating number over an enemy's head whenever you damage them");

            ShowHitDamageNumbersV2 = config.Bind(
                EspTitle,
                "Show hit damage numbers (event-based, experimental)",
                false,
                "Alternative to 'Show hit damage numbers' - spawns popups from Player.OnDamageReceived "
                + "hit events instead of polling every enemy's HP every frame. Independent of the option "
                + "above; enable to compare, disable if it misbehaves");

            ShowAiInfo = config.Bind(
                EspTitle,
                "Show AI info",
                false,
                "Shows each bot's difficulty and current behavior (peaceful/searching/has seen "
                + "you/etc) next to its ESP box. No effect on real players.");
        }
    }
}
