using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MagicInventory.Core;
using MagicInventory.Services.MagicSort;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MagicInventory.Services;

public enum MagicActionIntent
{
    Sort,
    Sell,
    Buy,
    Stop,
}

/// <summary>
/// Single-owner command and station coordinator for /magic and /m commands.
/// Resolves already-open station windows first, then safely performs a single-shot
/// in-range interaction (zero movement, zero retry) with the nearest matching station.
/// Also owns the master StopAll() kill-switch across all active orchestrators.
/// </summary>
public sealed unsafe class MagicActionCoordinator : IDisposable
{
    private const float EventObjectInteractRangeYalms = 3.5f;
    private const float NpcInteractRangeYalms = 5.0f;
    private const float MovementToleranceSq = 0.04f;
    private const double StationOpenTimeoutSeconds = 4.5;
    private const uint GilShopHighWord = 4u;
    private const uint TopicSelectHighWord = 50u;

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly IChatGui chatGui;
    private readonly SafeTransactionQueue transactionQueue;
    private readonly MagicSortService magicSort;
    private readonly BellSweepService bellSweep;
    private readonly MarketBoardBuyPlanner marketBoardBuy;

    private PendingStationRequest? pendingStation;

    private enum StationKind
    {
        SummoningBell,
        Armoire,
        MarketBoard,
        VendorNpc,
        GrandCompanyNpc,
    }

    private sealed class PendingStationRequest
    {
        public required StationKind Station { get; init; }
        public required MagicActionIntent Intent { get; init; }
        public required string StationName { get; init; }
        public required Vector3 OriginPosition { get; init; }
        public required DateTime StartedUtc { get; init; }
        public DateTime ReadyAtUtc { get; set; } = DateTime.MinValue;
        public bool ClickedSubMenu { get; set; }
    }

    public MagicActionCoordinator(
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        IDataManager dataManager,
        IGameGui gameGui,
        IChatGui chatGui,
        SafeTransactionQueue transactionQueue,
        MagicSortService magicSort,
        BellSweepService bellSweep,
        MarketBoardBuyPlanner marketBoardBuy)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.chatGui = chatGui;
        this.transactionQueue = transactionQueue;
        this.magicSort = magicSort;
        this.bellSweep = bellSweep;
        this.marketBoardBuy = marketBoardBuy;

        this.magicSort.IsOrchestratorActive = () => IsAnyOrchestratorActive;
        this.magicSort.OnUserStopRequested = () => StopAll("Stopped.");

        this.framework.Update += OnFrameworkUpdate;
    }

    public bool IsAnyOrchestratorActive
        => pendingStation != null
            || bellSweep.IsRunning
            || marketBoardBuy.IsAutoBuying;

    public bool IsAnythingActive => IsAnyOrchestratorActive || transactionQueue.IsBusy;

    public void StopAll(string? echoReason = null)
    {
        var wasActive = IsAnythingActive;
        pendingStation = null;

        bellSweep.Stop(null);
        marketBoardBuy.StopAutoBuy(null);
        transactionQueue.Cancel(null, QueueBatchOutcome.CancelledByUser);
        magicSort.InvalidateButtonCache();

        if (wasActive && !string.IsNullOrWhiteSpace(echoReason))
        {
            chatGui.Print(echoReason);
        }
    }

    public void Execute(MagicActionIntent intent)
    {
        if (intent == MagicActionIntent.Stop)
        {
            StopAll("Stopped.");
            return;
        }

        if (IsAnythingActive)
        {
            StopAll("Stopped active Magic action.");
            return;
        }

        // 1. Check already-open station windows first (zero world interaction needed)
        if (TryDispatchOpenStation(intent))
        {
            return;
        }

        // 2. Otherwise check for an in-range interactable station (zero movement, single-shot)
        if (!CanSafelyInteractWorld(out var player, out var blockReason))
        {
            chatGui.PrintError(blockReason);
            return;
        }

        if (!TryFindNearestInRangeStation(player!, intent, out var targetObj, out var stationKind))
        {
            if (intent == MagicActionIntent.Sort
                && (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryExpansion")
                    || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryLarge")
                    || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Inventory")
                    || magicSort.CountInventorySortMoves() > 0))
            {
                magicSort.ExecuteInventorySort();
                return;
            }

            chatGui.Print(intent switch
            {
                MagicActionIntent.Sort => "Stand next to a Summoning Bell (or open your Inventory) to run /m sort.",
                MagicActionIntent.Sell => "Stand next to a Summoning Bell, Vendor, or Grand Company Personnel Officer to run /m sell.",
                MagicActionIntent.Buy => "Stand next to a Market Board to run /m buy.",
                _ => "No compatible station in range.",
            });
            return;
        }

        if (!TrySingleShotInteract(targetObj!))
        {
            chatGui.PrintError($"Unable to interact with {targetObj!.Name.TextValue}.");
            return;
        }

        pendingStation = new PendingStationRequest
        {
            Station = stationKind,
            Intent = intent,
            StationName = targetObj!.Name.TextValue,
            OriginPosition = player!.Position,
            StartedUtc = DateTime.UtcNow,
        };
    }

    private bool TryDispatchOpenStation(MagicActionIntent intent)
    {
        switch (intent)
        {
            case MagicActionIntent.Sort:
                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerList"))
                {
                    bellSweep.StartBellSweep(marketSellOnly: false);
                    return true;
                }

                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryRetainerLarge")
                    || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryRetainer"))
                {
                    magicSort.ExecuteRetainerSort();
                    return true;
                }

                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Materialize"))
                {
                    magicSort.ExecuteMateriaExtractSort();
                    return true;
                }

                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Cabinet"))
                {
                    magicSort.ExecuteArmoireStore();
                    return true;
                }

                return false;

            case MagicActionIntent.Sell:
                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerSellList"))
                {
                    magicSort.ExecuteMarketSort();
                    return true;
                }

                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerList"))
                {
                    bellSweep.StartBellSweep(marketSellOnly: true);
                    return true;
                }

                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Shop"))
                {
                    magicSort.ExecuteVendorSort();
                    return true;
                }

                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "GrandCompanySupplyList"))
                {
                    magicSort.ExecuteGrandCompanySort();
                    return true;
                }

                return false;

            case MagicActionIntent.Buy:
                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearchResult"))
                {
                    marketBoardBuy.ExecuteBuyBestListing();
                    return true;
                }

                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearch"))
                {
                    marketBoardBuy.ToggleAutoBuy();
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var req = pendingStation;
        if (req == null)
        {
            return;
        }

        var player = objectTable.LocalPlayer;
        if (!clientState.IsLoggedIn
            || player == null
            || condition[ConditionFlag.InCombat]
            || condition[ConditionFlag.BetweenAreas]
            || condition[ConditionFlag.BetweenAreas51]
            || condition[ConditionFlag.OccupiedInCutSceneEvent]
            || condition[ConditionFlag.Unconscious])
        {
            pendingStation = null;
            return;
        }

        if (Vector3.DistanceSquared(player.Position, req.OriginPosition) > MovementToleranceSq)
        {
            pendingStation = null;
            chatGui.Print("Cancelled: movement detected.");
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - req.StartedUtc).TotalSeconds > StationOpenTimeoutSeconds)
        {
            pendingStation = null;
            chatGui.Print($"Timed out waiting for {req.StationName} to open.");
            return;
        }

        switch (req.Station)
        {
            case StationKind.SummoningBell:
                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerList"))
                {
                    if (WaitHumanBeat(req, now))
                    {
                        pendingStation = null;
                        bellSweep.StartBellSweep(marketSellOnly: req.Intent == MagicActionIntent.Sell);
                    }
                }

                break;

            case StationKind.Armoire:
                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Cabinet"))
                {
                    if (WaitHumanBeat(req, now))
                    {
                        pendingStation = null;
                        magicSort.ExecuteArmoireStore();
                    }
                }
                else if (!req.ClickedSubMenu && NativeUiHelpers.IsAddonOpenAndReady(gameGui, "SelectString"))
                {
                    if (WaitHumanBeat(req, now))
                    {
                        if (TryClickArmoirePlaceMenuEntry())
                        {
                            req.ClickedSubMenu = true;
                            req.ReadyAtUtc = DateTime.MinValue;
                        }
                        else
                        {
                            pendingStation = null;
                            chatGui.Print("Select 'Place items in the armoire' and click Magic Store.");
                        }
                    }
                }

                break;

            case StationKind.MarketBoard:
                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearch"))
                {
                    if (WaitHumanBeat(req, now))
                    {
                        pendingStation = null;
                        marketBoardBuy.ToggleAutoBuy();
                    }
                }

                break;

            case StationKind.VendorNpc:
                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Shop"))
                {
                    if (WaitHumanBeat(req, now))
                    {
                        pendingStation = null;
                        magicSort.ExecuteVendorSort();
                    }
                }
                else if (!req.ClickedSubMenu
                    && (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "SelectIconString")
                        || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "SelectString")))
                {
                    if (WaitHumanBeat(req, now))
                    {
                        if (TryClickShopPurchaseSubMenu())
                        {
                            req.ClickedSubMenu = true;
                            req.ReadyAtUtc = DateTime.MinValue;
                        }
                        else
                        {
                            pendingStation = null;
                            chatGui.Print($"{req.StationName} has multiple menus. Open a shop window, then click Magic Sell.");
                        }
                    }
                }

                break;

            case StationKind.GrandCompanyNpc:
                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "GrandCompanySupplyList"))
                {
                    if (WaitHumanBeat(req, now))
                    {
                        pendingStation = null;
                        magicSort.ExecuteGrandCompanySort();
                    }
                }
                else if (!req.ClickedSubMenu
                    && NativeUiHelpers.IsAddonOpenAndReady(gameGui, "SelectString"))
                {
                    if (WaitHumanBeat(req, now) && TryClickGcSupplyMenuEntry())
                    {
                        req.ClickedSubMenu = true;
                        req.ReadyAtUtc = DateTime.MinValue;
                    }
                }

                break;
        }
    }

    private static bool WaitHumanBeat(PendingStationRequest req, DateTime now)
    {
        if (req.ReadyAtUtc == DateTime.MinValue)
        {
            req.ReadyAtUtc = now.AddMilliseconds(Random.Shared.Next(450, 750));
            return false;
        }

        return now >= req.ReadyAtUtc;
    }

    private bool TryClickShopPurchaseSubMenu()
    {
        var iconSelect = (AddonSelectIconString*)NativeUiHelpers.GetReadyAddon(gameGui, "SelectIconString");
        if (iconSelect != null && iconSelect->PopupMenu.EntryNames != null)
        {
            var count = iconSelect->PopupMenu.EntryCount;
            for (var i = 0; i < count; i++)
            {
                var ptr = iconSelect->PopupMenu.EntryNames[i].Value;
                if (ptr == null)
                {
                    continue;
                }

                var text = MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue.Trim();
                if (text.Contains("Purchase", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Shop", StringComparison.OrdinalIgnoreCase))
                {
                    var args = stackalloc AtkValue[1];
                    args[0] = NativeUiHelpers.IntValue(i);
                    return NativeUiHelpers.FireCallback((AtkUnitBase*)iconSelect, true, new ReadOnlySpan<AtkValue>(args, 1));
                }
            }
        }

        var strSelect = (AddonSelectString*)NativeUiHelpers.GetReadyAddon(gameGui, "SelectString");
        if (strSelect != null && strSelect->PopupMenu.EntryNames != null)
        {
            var count = strSelect->PopupMenu.EntryCount;
            for (var i = 0; i < count; i++)
            {
                var ptr = strSelect->PopupMenu.EntryNames[i].Value;
                if (ptr == null)
                {
                    continue;
                }

                var text = MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue.Trim();
                if (text.Contains("Purchase", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Shop", StringComparison.OrdinalIgnoreCase))
                {
                    var args = stackalloc AtkValue[1];
                    args[0] = NativeUiHelpers.IntValue(i);
                    return NativeUiHelpers.FireCallback((AtkUnitBase*)strSelect, true, new ReadOnlySpan<AtkValue>(args, 1));
                }
            }
        }

        return false;
    }

    private bool TryClickArmoirePlaceMenuEntry()
    {
        var selectAddon = (AddonSelectString*)NativeUiHelpers.GetReadyAddon(gameGui, "SelectString");
        if (selectAddon == null || selectAddon->PopupMenu.EntryNames == null)
        {
            return false;
        }

        var count = selectAddon->PopupMenu.EntryCount;
        for (var i = 0; i < count; i++)
        {
            var ptr = selectAddon->PopupMenu.EntryNames[i].Value;
            if (ptr == null)
            {
                continue;
            }

            var text = MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue.Trim();
            if (text.Contains("Place items in the armoire", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Place", StringComparison.OrdinalIgnoreCase))
            {
                var args = stackalloc AtkValue[1];
                args[0] = NativeUiHelpers.IntValue(i);
                return NativeUiHelpers.FireCallback((AtkUnitBase*)selectAddon, true, new ReadOnlySpan<AtkValue>(args, 1));
            }
        }

        return false;
    }

    private bool TryClickGcSupplyMenuEntry()
    {
        var selectAddon = (AddonSelectString*)NativeUiHelpers.GetReadyAddon(gameGui, "SelectString");
        if (selectAddon == null || selectAddon->PopupMenu.EntryNames == null)
        {
            return false;
        }

        var count = selectAddon->PopupMenu.EntryCount;
        for (var i = 0; i < count; i++)
        {
            var ptr = selectAddon->PopupMenu.EntryNames[i].Value;
            if (ptr == null)
            {
                continue;
            }

            var text = MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue.Trim();
            if (text.Contains("supply and provisioning", StringComparison.OrdinalIgnoreCase))
            {
                var args = stackalloc AtkValue[1];
                args[0] = NativeUiHelpers.IntValue(i);
                return NativeUiHelpers.FireCallback((AtkUnitBase*)selectAddon, true, new ReadOnlySpan<AtkValue>(args, 1));
            }
        }

        return false;
    }

    private bool CanSafelyInteractWorld(out IGameObject? player, out string reason)
    {
        player = objectTable.LocalPlayer;
        if (!clientState.IsLoggedIn || player == null)
        {
            reason = "Character is not available.";
            return false;
        }

        if (condition[ConditionFlag.InCombat]
            || condition[ConditionFlag.BetweenAreas]
            || condition[ConditionFlag.BetweenAreas51]
            || condition[ConditionFlag.OccupiedInCutSceneEvent]
            || condition[ConditionFlag.OccupiedInEvent]
            || condition[ConditionFlag.OccupiedInQuestEvent]
            || condition[ConditionFlag.Casting]
            || condition[ConditionFlag.Mounted]
            || condition[ConditionFlag.Unconscious])
        {
            reason = "Cannot interact right now (busy, mounted, casting, or in combat).";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryFindNearestInRangeStation(
        IGameObject player,
        MagicActionIntent intent,
        out IGameObject? bestObject,
        out StationKind bestKind)
    {
        bestObject = null;
        bestKind = default;
        var bestDistSq = float.MaxValue;
        var playerPos = player.Position;

        foreach (var obj in objectTable)
        {
            if (obj == null || !obj.IsTargetable || obj.Address == nint.Zero)
            {
                continue;
            }

            if (!TryClassifyStation(obj, intent, out var kind, out var maxRange))
            {
                continue;
            }

            var distSq = Vector3.DistanceSquared(playerPos, obj.Position);
            if (distSq <= maxRange * maxRange && distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestObject = obj;
                bestKind = kind;
            }
        }

        return bestObject != null;
    }

    private bool TryClassifyStation(
        IGameObject obj,
        MagicActionIntent intent,
        out StationKind kind,
        out float maxRange)
    {
        kind = default;
        maxRange = 0f;
        var name = obj.Name.TextValue;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (obj.ObjectKind == ObjectKind.EventObj)
        {
            if ((intent is MagicActionIntent.Sort or MagicActionIntent.Sell)
                && (obj.BaseId is 2000401 or 2000403 or 196630 || string.Equals(name, "Summoning Bell", StringComparison.OrdinalIgnoreCase)))
            {
                kind = StationKind.SummoningBell;
                maxRange = EventObjectInteractRangeYalms;
                return true;
            }

            if (intent == MagicActionIntent.Sort
                && (obj.BaseId == 2001407 || string.Equals(name, "Armoire", StringComparison.OrdinalIgnoreCase)))
            {
                kind = StationKind.Armoire;
                maxRange = EventObjectInteractRangeYalms;
                return true;
            }

            if (intent == MagicActionIntent.Buy
                && (obj.BaseId is 2000402 or 2000440 || string.Equals(name, "Market Board", StringComparison.OrdinalIgnoreCase)))
            {
                kind = StationKind.MarketBoard;
                maxRange = EventObjectInteractRangeYalms;
                return true;
            }

            return false;
        }

        if (obj.ObjectKind == ObjectKind.EventNpc && intent == MagicActionIntent.Sell)
        {
            if (obj.BaseId is 1002388 or 1002391 or 1002394
                || name.Contains("Personnel Officer", StringComparison.OrdinalIgnoreCase))
            {
                kind = StationKind.GrandCompanyNpc;
                maxRange = NpcInteractRangeYalms;
                return true;
            }

            if (HasGilShopHandler(obj.BaseId))
            {
                kind = StationKind.VendorNpc;
                maxRange = NpcInteractRangeYalms;
                return true;
            }
        }

        return false;
    }

    private bool HasGilShopHandler(uint enpcId)
    {
        var enpcSheet = dataManager.GetExcelSheet<ENpcBase>();
        var row = enpcSheet?.GetRowOrDefault(enpcId);
        if (row is null)
        {
            return false;
        }

        var topicSheet = dataManager.GetExcelSheet<TopicSelect>();
        foreach (var dataRef in row.Value.ENpcData)
        {
            var id = dataRef.RowId;
            if (id == 0)
            {
                continue;
            }

            var high = id >> 16;
            if (high == GilShopHighWord)
            {
                return true;
            }

            if (high == TopicSelectHighWord && topicSheet != null)
            {
                var topic = topicSheet.GetRowOrDefault(id);
                if (topic is not null)
                {
                    foreach (var shopRef in topic.Value.Shop)
                    {
                        if ((shopRef.RowId >> 16) == GilShopHighWord)
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private static bool TrySingleShotInteract(IGameObject target)
    {
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null || target.Address == nint.Zero)
        {
            return false;
        }

        var nativeObj = (NativeGameObject*)target.Address;
        targetSystem->Target = nativeObj;
        targetSystem->InteractWithObject(nativeObj, false);
        return true;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        magicSort.IsOrchestratorActive = null;
        magicSort.OnUserStopRequested = null;
        pendingStation = null;
    }
}
