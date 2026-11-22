using System.Collections.Generic;
using MagicInventory.Core;
using Xunit;

namespace MagicInventory.Tests;

public sealed class RetainerTransferPolicyTests
{
    [Fact]
    public void ResolveDepositTarget_UsesProjectedWithdrawalSpaceInsteadOfPrematureOverflow()
    {
        var retA = new RetainerCapacityState
        {
            RetainerId = 1,
            BaseFreeSlots = 0,
            ProjectedWithdrawalFreedSlots = 2,
            IsKnownFullFor = (_, _, _) => true,
        };
        var retB = new RetainerCapacityState
        {
            RetainerId = 2,
            BaseFreeSlots = 50,
            ProjectedWithdrawalFreedSlots = 0,
        };

        var list = new List<RetainerCapacityState> { retA, retB };
        var byId = new Dictionary<ulong, RetainerCapacityState> { [1] = retA, [2] = retB };
        RetainerTransferPolicy.InitializeProjectedBudgets(list);

        var decision = RetainerTransferPolicy.ResolveDepositTarget(
            primaryRetainerId: 1,
            baseItemId: 5001,
            isHq: false,
            isUnique: false,
            stackSize: 999,
            orderedRetainers: list,
            byId: byId);

        Assert.Equal(1UL, decision.TargetRetainerId);
        Assert.False(decision.IsOverflow);
        Assert.Equal(1, retA.RemainingFreeSlots);
    }

    [Fact]
    public void ResolveDepositTarget_ReturnsZeroWhenEveryRetainerIsFull_NeverFallsOpen()
    {
        var retA = new RetainerCapacityState
        {
            RetainerId = 1,
            BaseFreeSlots = 0,
            IsKnownFullFor = (_, _, _) => true,
        };
        var retB = new RetainerCapacityState
        {
            RetainerId = 2,
            BaseFreeSlots = 0,
            IsKnownFullFor = (_, _, _) => true,
        };

        var list = new List<RetainerCapacityState> { retA, retB };
        var byId = new Dictionary<ulong, RetainerCapacityState> { [1] = retA, [2] = retB };
        RetainerTransferPolicy.InitializeProjectedBudgets(list);

        var decision = RetainerTransferPolicy.ResolveDepositTarget(
            primaryRetainerId: 1,
            baseItemId: 5002,
            isHq: false,
            isUnique: false,
            stackSize: 999,
            orderedRetainers: list,
            byId: byId);

        Assert.Equal(0UL, decision.TargetRetainerId);
    }

    [Fact]
    public void ResolveDepositTarget_SkipsRetainerWithIsUniqueConflictAndOverflowsCleanly()
    {
        var retA = new RetainerCapacityState
        {
            RetainerId = 1,
            BaseFreeSlots = 50,
            HasBaseItem = id => id == 9001,
        };
        var retB = new RetainerCapacityState
        {
            RetainerId = 2,
            BaseFreeSlots = 50,
            HasBaseItem = _ => false,
        };

        var list = new List<RetainerCapacityState> { retA, retB };
        var byId = new Dictionary<ulong, RetainerCapacityState> { [1] = retA, [2] = retB };
        RetainerTransferPolicy.InitializeProjectedBudgets(list);

        var decision = RetainerTransferPolicy.ResolveDepositTarget(
            primaryRetainerId: 1,
            baseItemId: 9001,
            isHq: false,
            isUnique: true,
            stackSize: 1,
            orderedRetainers: list,
            byId: byId);

        Assert.Equal(2UL, decision.TargetRetainerId);
        Assert.True(decision.IsOverflow);
    }

    [Fact]
    public void EvaluateCrossRetainerFunnel_BlocksIsUniqueConflictAndMatchesExactQualityPartialStack()
    {
        var dest = new RetainerCapacityState
        {
            RetainerId = 2,
            BaseFreeSlots = 0,
            HasBaseItem = id => id == 8888,
            HasPartialStack = (id, hq) => id == 7777 && hq,
        };
        var byId = new Dictionary<ulong, RetainerCapacityState> { [2] = dest };
        RetainerTransferPolicy.InitializeProjectedBudgets(byId.Values);

        var uniqueBlocked = RetainerTransferPolicy.EvaluateCrossRetainerFunnel(
            sourceRetainerId: 1,
            destRetainerId: 2,
            baseItemId: 8888,
            hasNq: true,
            hasHq: false,
            isUnique: true,
            stackSize: 1,
            byId: byId);
        Assert.False(uniqueBlocked.CanFunnel);

        var nqBlocked = RetainerTransferPolicy.EvaluateCrossRetainerFunnel(
            sourceRetainerId: 1,
            destRetainerId: 2,
            baseItemId: 7777,
            hasNq: true,
            hasHq: false,
            isUnique: false,
            stackSize: 999,
            byId: byId);
        Assert.False(nqBlocked.CanFunnel);

        var hqAllowed = RetainerTransferPolicy.EvaluateCrossRetainerFunnel(
            sourceRetainerId: 1,
            destRetainerId: 2,
            baseItemId: 7777,
            hasNq: false,
            hasHq: true,
            isUnique: false,
            stackSize: 999,
            byId: byId);
        Assert.True(hqAllowed.CanFunnel);
        Assert.False(hqAllowed.ConsumesFreeSlot);
    }

    [Fact]
    public void FullBagsAndFullDrainableRetainer_CapsProjectedSpaceToZeroAndOverflows()
    {
        var retA = new RetainerCapacityState
        {
            RetainerId = 1,
            BaseFreeSlots = 0,
            ProjectedWithdrawalFreedSlots = 3,
        };
        var retB = new RetainerCapacityState
        {
            RetainerId = 2,
            BaseFreeSlots = 15,
            ProjectedWithdrawalFreedSlots = 0,
        };
        var list = new List<RetainerCapacityState> { retA, retB };
        var byId = new Dictionary<ulong, RetainerCapacityState> { [1] = retA, [2] = retB };

        RetainerTransferPolicy.InitializeProjectedBudgets(list, playerEmptyBagSlots: 0);

        Assert.Equal(0, retA.RemainingFreeSlots);
        var decision = RetainerTransferPolicy.ResolveDepositTarget(
            primaryRetainerId: 1,
            baseItemId: 5000,
            isHq: false,
            isUnique: false,
            stackSize: 999,
            orderedRetainers: list,
            byId: byId,
            consumeBudget: true);

        Assert.Equal(2UL, decision.TargetRetainerId);
        Assert.True(decision.IsOverflow);
    }

    [Fact]
    public void UnvisitedRetainerWithDefaultMaxSlots_AcceptsPrimaryAndOverflowDeposits()
    {
        var visitedFull = new RetainerCapacityState { RetainerId = 1, BaseFreeSlots = 0 };
        var unvisited = new RetainerCapacityState { RetainerId = 2, BaseFreeSlots = 175 };
        var list = new List<RetainerCapacityState> { visitedFull, unvisited };
        var byId = new Dictionary<ulong, RetainerCapacityState> { [1] = visitedFull, [2] = unvisited };

        RetainerTransferPolicy.InitializeProjectedBudgets(list, playerEmptyBagSlots: 10);

        var decision = RetainerTransferPolicy.ResolveDepositTarget(
            primaryRetainerId: 1,
            baseItemId: 6000,
            isHq: false,
            isUnique: false,
            stackSize: 999,
            orderedRetainers: list,
            byId: byId,
            consumeBudget: true);

        Assert.Equal(2UL, decision.TargetRetainerId);
        Assert.True(decision.IsOverflow);
    }
}
