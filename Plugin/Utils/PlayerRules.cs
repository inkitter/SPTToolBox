using System.Collections.Generic;
using EFT;

namespace SPTMap.Utils
{
    // Pure classification/kill-attribution rules, factored out of GameUtils' Player extension
    // methods so they can be unit tested without an IL2CPP Player instance or the GetMainPlayer()
    // singleton - each function takes only the plain data it decides on.
    public static class PlayerRules
    {
        public static bool IsPMC(EPlayerSide side, WildSpawnType role)
        {
            // Real human PMCs report Side Bear/Usec directly. AI-controlled "PMC" bots instead
            // report Side Savage (same as scavs) and are only distinguishable via Role - without
            // this they'd be misclassified as scavs and drawn in scav color/icon.
            return side == EPlayerSide.Bear || side == EPlayerSide.Usec
                || role == WildSpawnType.pmcBEAR || role == WildSpawnType.pmcUSEC;
        }

        public static bool IsScav(EPlayerSide side, WildSpawnType role)
        {
            return side == EPlayerSide.Savage && role != WildSpawnType.pmcBEAR && role != WildSpawnType.pmcUSEC;
        }

        public static bool IsTrackedBoss(EPlayerSide side, WildSpawnType role, ISet<WildSpawnType> trackedBosses)
        {
            return side == EPlayerSide.Savage && trackedBosses.Contains(role);
        }

        public static bool IsBTRShooter(EPlayerSide side, WildSpawnType role)
        {
            return side == EPlayerSide.Savage && role == WildSpawnType.shooterBTR;
        }

        public static bool IsHeadlessClient(EMemberCategory category)
        {
            return category == EMemberCategory.UnitTest;
        }

        public static bool DidKill(string aggressorProfileId, string victimKillerProfileId)
        {
            return !string.IsNullOrEmpty(aggressorProfileId) && aggressorProfileId == victimKillerProfileId;
        }

        public static bool DidTeammateKill(
            string aggressorProfileId, string aggressorGroupId, string mainProfileId, string mainGroupId)
        {
            if (string.IsNullOrEmpty(aggressorProfileId) || string.IsNullOrEmpty(aggressorGroupId)
                || string.IsNullOrEmpty(mainGroupId))
            {
                return false;
            }

            return aggressorProfileId != mainProfileId && aggressorGroupId == mainGroupId;
        }
    }
}
