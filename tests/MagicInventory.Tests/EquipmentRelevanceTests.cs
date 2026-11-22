using MagicInventory.Core;
using Xunit;

namespace MagicInventory.Tests;

public sealed class EquipmentRelevanceTests
{
    [Fact]
    public void RingUpgradeOverWeakerEquippedRing_IsProtectedNotOutgeared()
    {
        var profile = new UnlockedJobGearProfile();
        profile.JobLevels[19] = 100;
        profile.JobBaselineIlvl[19] = 710;
        profile.JobSlotIlvl[(19, EquipSlotKey.Ring)] = 690;
        profile.SavedGearsetJobIds.Add(19);

        var rel = InventorySafetyEvaluator.EvaluateEquipmentRelevance(
            profile,
            EquipSlotKey.Ring,
            equipLevel: 100,
            itemIlvl: 700,
            isCrafterGathererTool: false,
            canJobUse: jobId => jobId == 19);

        Assert.True(rel.IsUpgradeOrLevelingForUnlockedJob);
        Assert.False(rel.IsConfirmedOutgearedByUnlockedJob);
    }

    [Fact]
    public void RingBelowWeakerEquippedRingByMoreThanMargin_IsConfirmedOutgeared()
    {
        var profile = new UnlockedJobGearProfile();
        profile.JobLevels[19] = 100;
        profile.JobBaselineIlvl[19] = 710;
        profile.JobSlotIlvl[(19, EquipSlotKey.Ring)] = 690;
        profile.SavedGearsetJobIds.Add(19);

        var rel = InventorySafetyEvaluator.EvaluateEquipmentRelevance(
            profile,
            EquipSlotKey.Ring,
            equipLevel: 100,
            itemIlvl: 680,
            isCrafterGathererTool: false,
            canJobUse: jobId => jobId == 19);

        Assert.False(rel.IsUpgradeOrLevelingForUnlockedJob);
        Assert.True(rel.IsConfirmedOutgearedByUnlockedJob);
    }

    [Fact]
    public void UntouchedLevel1Job_DoesNotProtectLowLevelGear()
    {
        var profile = new UnlockedJobGearProfile();
        profile.JobLevels[1] = 1;
        profile.JobLevels[19] = 100;
        profile.JobBaselineIlvl[19] = 720;
        profile.JobSlotIlvl[(19, EquipSlotKey.Body)] = 720;
        profile.SavedGearsetJobIds.Add(19);

        var rel = InventorySafetyEvaluator.EvaluateEquipmentRelevance(
            profile,
            EquipSlotKey.Body,
            equipLevel: 5,
            itemIlvl: 5,
            isCrafterGathererTool: false,
            canJobUse: jobId => jobId is 1 or 19);

        Assert.False(rel.IsUpgradeOrLevelingForUnlockedJob);
        Assert.True(rel.IsConfirmedOutgearedByUnlockedJob);
    }

    [Fact]
    public void LevelingGearWithinPlus4LevelsOfActiveJob_IsProtected()
    {
        var profile = new UnlockedJobGearProfile();
        profile.JobLevels[24] = 82;
        profile.JobBaselineIlvl[24] = 530;
        profile.SavedGearsetJobIds.Add(24);

        var rel = InventorySafetyEvaluator.EvaluateEquipmentRelevance(
            profile,
            EquipSlotKey.Head,
            equipLevel: 85,
            itemIlvl: 536,
            isCrafterGathererTool: false,
            canJobUse: jobId => jobId == 24);

        Assert.True(rel.IsUpgradeOrLevelingForUnlockedJob);
    }

    [Fact]
    public void CrafterToolAtCurrentIlvl_IsProtected()
    {
        var profile = new UnlockedJobGearProfile();
        profile.JobLevels[8] = 100;
        profile.JobBaselineIlvl[8] = 690;
        profile.JobSlotIlvl[(8, EquipSlotKey.MainHand)] = 690;
        profile.SavedGearsetJobIds.Add(8);

        var rel = InventorySafetyEvaluator.EvaluateEquipmentRelevance(
            profile,
            EquipSlotKey.MainHand,
            equipLevel: 100,
            itemIlvl: 690,
            isCrafterGathererTool: true,
            canJobUse: jobId => jobId == 8);

        Assert.True(rel.IsRelevantCrafterGathererTool);
        Assert.False(rel.IsConfirmedOutgearedByUnlockedJob);
    }
}
