using MagicInventory.Services.Triage;
using Xunit;

namespace MagicInventory.Tests;

public sealed class AutoUsePolicyTests
{
    [Fact]
    public void UntradableRaidGearCoffer_IsNeverAutoUsed()
    {
        var allowed = ItemTriageEngine.EvaluateAutoUsePolicy(
            isUntradable: true,
            isMarketable: false,
            hasItemAction: true,
            isEquipment: false,
            isProtectedConsumable: false,
            isProtectedUtility: false,
            isMgpCard: false,
            isCofferOrContainer: true,
            unlockStatus: 0,
            out _);

        Assert.False(allowed);
    }

    [Fact]
    public void TradableUnownedMountOrMinion_IsNeverAutoUsed()
    {
        var allowed = ItemTriageEngine.EvaluateAutoUsePolicy(
            isUntradable: false,
            isMarketable: true,
            hasItemAction: true,
            isEquipment: false,
            isProtectedConsumable: false,
            isProtectedUtility: false,
            isMgpCard: false,
            isCofferOrContainer: false,
            unlockStatus: 2,
            out _);

        Assert.False(allowed);
    }

    [Fact]
    public void UntradableUnownedMinion_IsAllowedForAutoUse()
    {
        var allowed = ItemTriageEngine.EvaluateAutoUsePolicy(
            isUntradable: true,
            isMarketable: false,
            hasItemAction: true,
            isEquipment: false,
            isProtectedConsumable: false,
            isProtectedUtility: false,
            isMgpCard: false,
            isCofferOrContainer: false,
            unlockStatus: 2,
            out var reason);

        Assert.True(allowed);
        Assert.Equal("Unlockable", reason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void UnlockedOrUnloadedUnlockStatus_IsBlocked(long status)
    {
        var allowed = ItemTriageEngine.EvaluateAutoUsePolicy(
            isUntradable: true,
            isMarketable: false,
            hasItemAction: true,
            isEquipment: false,
            isProtectedConsumable: false,
            isProtectedUtility: false,
            isMgpCard: false,
            isCofferOrContainer: false,
            unlockStatus: status,
            out _);

        Assert.False(allowed);
    }

    [Fact]
    public void VerifiedMgpCard_IsAllowedForAutoUse()
    {
        var allowed = ItemTriageEngine.EvaluateAutoUsePolicy(
            isUntradable: true,
            isMarketable: false,
            hasItemAction: true,
            isEquipment: false,
            isProtectedConsumable: false,
            isProtectedUtility: false,
            isMgpCard: true,
            isCofferOrContainer: false,
            unlockStatus: 0,
            out var reason);

        Assert.True(allowed);
        Assert.Equal("MGP Card", reason);
    }
}
