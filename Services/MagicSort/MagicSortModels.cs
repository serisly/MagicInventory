using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.Game;
using MagicInventory.Core;

namespace MagicInventory.Services
{
    public sealed record MagicSortEntry(
        uint ItemId,
        string Name,
        string Route,
        ulong TargetRetainerId,
        string TargetRetainerName,
        long TargetPriceNq,
        long TargetPriceHq,
        long NpcSellPrice,
        bool Untradable,
        string Reason);

    public sealed record MagicSortButtonState(
        bool IsVisible,
        bool IsEnabled,
        string Label,
        string? Tooltip = null)
    {
        public static readonly MagicSortButtonState Hidden = new(false, false, string.Empty, string.Empty);
    }
}

namespace MagicInventory.Services.MagicSort
{
    public readonly record struct RetainerSortPlan(
        List<TransactionStep> Steps,
        int DepositCount,
        int WithdrawCount,
        int TrackedWithdrawCount,
        int BlockedByFullRetainer,
        int BlockedByFullBag,
        bool HasMoreRemaining,
        IReadOnlyList<string> TrackedWithdrawSummary,
        IReadOnlyList<string> DepositSummary,
        IReadOnlyList<string> SortWithdrawSummary,
        IReadOnlyList<(InventoryType Container, int Slot)> DepositSlots,
        IReadOnlyList<(InventoryType Container, int Slot)> WithdrawSlots)
    {
        public int TotalTransferCount => DepositCount + WithdrawCount + TrackedWithdrawCount;
    }

    public sealed record MarketPlanItem(
        InventoryType Container,
        int Slot,
        uint RawItemId,
        InventoryItem.ItemFlags Flags,
        uint BaseItemId,
        string Name,
        int Quantity,
        long UnitPrice);

    public sealed record VendorPlanItem(
        InventoryType Container,
        int Slot,
        uint RawItemId,
        InventoryItem.ItemFlags Flags,
        uint BaseItemId,
        string Name,
        int Quantity,
        uint NpcUnitPrice);

    public sealed record GrandCompanyPlanItem(
        InventoryType Container,
        int Slot,
        uint RawItemId,
        uint BaseItemId,
        string Name,
        int SealReward);

    public sealed record MateriaExtractPlanItem(
        InventoryType Container,
        int Slot,
        uint RawItemId,
        uint BaseItemId,
        string Name);
}
