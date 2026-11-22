using System;
using System.Collections.Generic;

namespace MagicInventory.Core;

public sealed class RetainerCapacityState
{
    public ulong RetainerId { get; init; }
    public int BaseFreeSlots { get; init; }
    public int ProjectedWithdrawalFreedSlots { get; set; }
    public int RemainingFreeSlots { get; set; }
    public Func<uint, bool> HasBaseItem { get; init; } = static _ => false;
    public Func<uint, bool, bool> HasPartialStack { get; init; } = static (_, _) => false;
    public Func<uint, bool, int, bool> IsKnownFullFor { get; init; } = static (_, _, _) => false;

    public int EffectiveFreeSlots => Math.Max(0, RemainingFreeSlots);
}

public readonly record struct RetainerDepositDecision(
    ulong TargetRetainerId,
    bool IsOverflow,
    bool MergesIntoPartialStack)
{
    public static readonly RetainerDepositDecision None = new(0, false, false);
}

public readonly record struct RetainerFunnelDecision(
    bool CanFunnel,
    bool ConsumesFreeSlot)
{
    public static readonly RetainerFunnelDecision Blocked = new(false, false);
}

public static class RetainerTransferPolicy
{
    public static void InitializeProjectedBudgets(
        IEnumerable<RetainerCapacityState> retainers,
        int playerEmptyBagSlots = int.MaxValue)
    {
        var maxWithdrawalsPossible = Math.Max(0, playerEmptyBagSlots);
        foreach (var ret in retainers)
        {
            var baseFree = Math.Max(0, ret.BaseFreeSlots);
            var projected = Math.Max(0, ret.ProjectedWithdrawalFreedSlots);
            var effectiveProjected = baseFree == 0
                ? Math.Min(projected, maxWithdrawalsPossible)
                : projected;
            ret.RemainingFreeSlots = baseFree + effectiveProjected;
        }
    }

    public static RetainerDepositDecision ResolveDepositTarget(
        ulong primaryRetainerId,
        uint baseItemId,
        bool isHq,
        bool isUnique,
        int stackSize,
        IReadOnlyList<RetainerCapacityState> orderedRetainers,
        IReadOnlyDictionary<ulong, RetainerCapacityState> byId,
        bool consumeBudget = true)
    {
        if (primaryRetainerId == 0 || baseItemId == 0)
        {
            return RetainerDepositDecision.None;
        }

        if (byId.TryGetValue(primaryRetainerId, out var primary))
        {
            var hasUniqueConflict = isUnique && primary.HasBaseItem(baseItemId);
            if (!hasUniqueConflict)
            {
                var mergesPartial = stackSize > 1 && primary.HasPartialStack(baseItemId, isHq);
                if (mergesPartial)
                {
                    return new RetainerDepositDecision(primaryRetainerId, IsOverflow: false, MergesIntoPartialStack: true);
                }

                var hasSpace = primary.RemainingFreeSlots > 0
                    && (primary.ProjectedWithdrawalFreedSlots > 0 || !primary.IsKnownFullFor(baseItemId, isHq, stackSize));
                if (hasSpace)
                {
                    if (consumeBudget)
                    {
                        primary.RemainingFreeSlots = Math.Max(0, primary.RemainingFreeSlots - 1);
                    }

                    return new RetainerDepositDecision(primaryRetainerId, IsOverflow: false, MergesIntoPartialStack: false);
                }
            }
        }

        foreach (var candidate in orderedRetainers)
        {
            if (candidate.RetainerId == 0 || candidate.RetainerId == primaryRetainerId)
            {
                continue;
            }

            if (isUnique && candidate.HasBaseItem(baseItemId))
            {
                continue;
            }

            var mergesPartial = stackSize > 1 && candidate.HasPartialStack(baseItemId, isHq);
            if (mergesPartial)
            {
                return new RetainerDepositDecision(candidate.RetainerId, IsOverflow: true, MergesIntoPartialStack: true);
            }

            var hasSpace = candidate.RemainingFreeSlots > 0
                && (candidate.ProjectedWithdrawalFreedSlots > 0 || !candidate.IsKnownFullFor(baseItemId, isHq, stackSize));
            if (hasSpace)
            {
                if (consumeBudget)
                {
                    candidate.RemainingFreeSlots = Math.Max(0, candidate.RemainingFreeSlots - 1);
                }

                return new RetainerDepositDecision(candidate.RetainerId, IsOverflow: true, MergesIntoPartialStack: false);
            }
        }

        return RetainerDepositDecision.None;
    }

    public static RetainerFunnelDecision EvaluateCrossRetainerFunnel(
        ulong sourceRetainerId,
        ulong destRetainerId,
        uint baseItemId,
        bool hasNq,
        bool hasHq,
        bool isUnique,
        int stackSize,
        IReadOnlyDictionary<ulong, RetainerCapacityState> byId,
        bool consumeBudget = true)
    {
        if (destRetainerId == 0
            || destRetainerId == sourceRetainerId
            || baseItemId == 0
            || (!hasNq && !hasHq)
            || !byId.TryGetValue(destRetainerId, out var dest))
        {
            return RetainerFunnelDecision.Blocked;
        }

        if (isUnique && dest.HasBaseItem(baseItemId))
        {
            return RetainerFunnelDecision.Blocked;
        }

        var mergesPartial = stackSize > 1
            && ((hasNq && dest.HasPartialStack(baseItemId, false))
                || (hasHq && dest.HasPartialStack(baseItemId, true)));
        if (mergesPartial)
        {
            return new RetainerFunnelDecision(CanFunnel: true, ConsumesFreeSlot: false);
        }

        if (dest.RemainingFreeSlots > 0)
        {
            if (consumeBudget)
            {
                dest.RemainingFreeSlots = Math.Max(0, dest.RemainingFreeSlots - 1);
            }

            return new RetainerFunnelDecision(CanFunnel: true, ConsumesFreeSlot: true);
        }

        return RetainerFunnelDecision.Blocked;
    }
}
