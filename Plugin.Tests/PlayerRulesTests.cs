using System.Collections.Generic;
using EFT;
using SPTMap.Utils;
using Xunit;

namespace SPTMap.Tests
{
    public class PlayerRulesTests
    {
        private static readonly HashSet<WildSpawnType> TrackedBosses = new()
        {
            WildSpawnType.bossKilla,
            WildSpawnType.bossTagilla,
        };

        [Theory]
        [InlineData(EPlayerSide.Bear, WildSpawnType.assault, true)]
        [InlineData(EPlayerSide.Usec, WildSpawnType.assault, true)]
        [InlineData(EPlayerSide.Savage, WildSpawnType.assault, false)]
        [InlineData(EPlayerSide.Savage, WildSpawnType.pmcBEAR, true)]
        [InlineData(EPlayerSide.Savage, WildSpawnType.pmcUSEC, true)]
        public void IsPMC_MatchesBearAndUsecOrPmcBotRole(EPlayerSide side, WildSpawnType role, bool expected)
        {
            Assert.Equal(expected, PlayerRules.IsPMC(side, role));
        }

        [Theory]
        [InlineData(EPlayerSide.Savage, WildSpawnType.assault, true)]
        [InlineData(EPlayerSide.Bear, WildSpawnType.assault, false)]
        [InlineData(EPlayerSide.Savage, WildSpawnType.pmcBEAR, false)]
        [InlineData(EPlayerSide.Savage, WildSpawnType.pmcUSEC, false)]
        public void IsScav_MatchesSavageExcludingPmcBotRoles(EPlayerSide side, WildSpawnType role, bool expected)
        {
            Assert.Equal(expected, PlayerRules.IsScav(side, role));
        }

        [Fact]
        public void IsTrackedBoss_SavageWithTrackedRole_ReturnsTrue()
        {
            Assert.True(PlayerRules.IsTrackedBoss(EPlayerSide.Savage, WildSpawnType.bossKilla, TrackedBosses));
        }

        [Fact]
        public void IsTrackedBoss_PmcSideEvenWithTrackedRole_ReturnsFalse()
        {
            // a boss role can never actually show up on a PMC side, but the rule should still be
            // side-gated defensively rather than trusting role alone.
            Assert.False(PlayerRules.IsTrackedBoss(EPlayerSide.Bear, WildSpawnType.bossKilla, TrackedBosses));
        }

        [Fact]
        public void IsTrackedBoss_SavageWithUntrackedRole_ReturnsFalse()
        {
            Assert.False(PlayerRules.IsTrackedBoss(EPlayerSide.Savage, WildSpawnType.assault, TrackedBosses));
        }

        [Fact]
        public void IsBTRShooter_SavageShooterBTR_ReturnsTrue()
        {
            Assert.True(PlayerRules.IsBTRShooter(EPlayerSide.Savage, WildSpawnType.shooterBTR));
        }

        [Fact]
        public void IsBTRShooter_OtherRole_ReturnsFalse()
        {
            Assert.False(PlayerRules.IsBTRShooter(EPlayerSide.Savage, WildSpawnType.assault));
        }

        [Fact]
        public void IsHeadlessClient_UnitTestCategory_ReturnsTrue()
        {
            Assert.True(PlayerRules.IsHeadlessClient(EMemberCategory.UnitTest));
        }

        [Fact]
        public void IsHeadlessClient_NormalCategory_ReturnsFalse()
        {
            Assert.False(PlayerRules.IsHeadlessClient(EMemberCategory.Default));
        }

        [Fact]
        public void DidKill_AggressorMatchesKiller_ReturnsTrue()
        {
            Assert.True(PlayerRules.DidKill("p1", "p1"));
        }

        [Fact]
        public void DidKill_DifferentProfiles_ReturnsFalse()
        {
            Assert.False(PlayerRules.DidKill("p1", "p2"));
        }

        [Fact]
        public void DidKill_EmptyAggressor_ReturnsFalse()
        {
            Assert.False(PlayerRules.DidKill("", "p2"));
        }

        [Fact]
        public void DidTeammateKill_SameGroupDifferentPlayer_ReturnsTrue()
        {
            Assert.True(PlayerRules.DidTeammateKill("teammate", "group1", "main", "group1"));
        }

        [Fact]
        public void DidTeammateKill_MainPlayerIsAggressor_ReturnsFalse()
        {
            // that's a self-kill / main-player-did-the-killing case, not a teammate kill.
            Assert.False(PlayerRules.DidTeammateKill("main", "group1", "main", "group1"));
        }

        [Fact]
        public void DidTeammateKill_DifferentGroups_ReturnsFalse()
        {
            Assert.False(PlayerRules.DidTeammateKill("stranger", "group2", "main", "group1"));
        }

        [Fact]
        public void DidTeammateKill_NoGroup_ReturnsFalse()
        {
            Assert.False(PlayerRules.DidTeammateKill("solo", "", "main", ""));
        }
    }
}
