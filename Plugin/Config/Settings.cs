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
        public static ConfigEntry<bool> ShowMarkerLabels;

        public static ConfigEntry<float> EspDistance;

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

            ShowMarkerLabels = config.Bind(
                MarkersTitle,
                "Show marker labels",
                true,
                "Whether markers that opt into it (e.g. extracts) show their name as a small "
                + "always-on label instead of only on hover");

            EspDistance = config.Bind(
                EspTitle,
                "ESP distance (m)",
                0f,
                new ConfigDescription(
                    "Draws an outline + chest HP readout over enemies within this distance, through "
                    + "walls. 0 disables the overlay entirely.",
                    new AcceptableValueRange<float>(0f, 300f)));
        }
    }
}
