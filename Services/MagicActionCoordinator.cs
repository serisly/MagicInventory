using System;
using System.Collections.Generic;
using System.Linq;
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
    Use,
    Stop,
}

public sealed unsafe class MagicActionCoordinator : IDisposable
{
    private const float EventObjectInteractRangeYalms = 3.5f;
    private const float NpcInteractRangeYalms = 5.0f;
    private const float MovementToleranceSq = 0.04f;
    private const double StationOpenTimeoutSeconds = 4.5;
    private const double GlamourCrystallizeSettleGraceSeconds = 1.2;
    private const int MaxTalkAdvances = 3;

    private const uint SummoningBellCanonicalEObjId = 2000401;
    private const uint ArmoireCanonicalEObjId = 2001407;
    private const uint GlamourDresserCanonicalEObjId = 2009439;
    private const uint MarketBoardCanonicalEObjId = 2000402;

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
    private readonly MaterialTrackerService materialTracker;

    private readonly HashSet<uint> summoningBellIds;
    private readonly HashSet<uint> armoireIds;
    private readonly HashSet<uint> glamourDresserIds;
    private readonly HashSet<uint> marketBoardIds;
    private readonly string summoningBellLocalizedName;
    private readonly string armoireLocalizedName;
    private readonly string glamourDresserLocalizedName;
    private readonly string marketBoardLocalizedName;

    private PendingStationRequest? pendingStation;

    private enum StationKind
    {
        SummoningBell,
        Armoire,
        GlamourDresser,
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
        public required DateTime StartedUtc { get; set; }
        public DateTime ReadyAtUtc { get; set; } = DateTime.MinValue;
        public DateTime PrismBoxSeenUtc { get; set; } = DateTime.MinValue;
        public int TalkAdvances { get; set; }
        public bool ClickedSubMenu { get; set; }
        public bool SwitchedGcTab { get; set; }
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
        MarketBoardBuyPlanner marketBoardBuy,
        MaterialTrackerService materialTracker)
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
        this.materialTracker = materialTracker;

        (summoningBellIds, summoningBellLocalizedName) = BuildEObjStationLookup(dataManager, SummoningBellCanonicalEObjId, "Summoning Bell");
        (armoireIds, armoireLocalizedName) = BuildEObjStationLookup(dataManager, ArmoireCanonicalEObjId, "Armoire");
        (glamourDresserIds, glamourDresserLocalizedName) = BuildEObjStationLookup(dataManager, GlamourDresserCanonicalEObjId, "Glamour Dresser");
        (marketBoardIds, marketBoardLocalizedName) = BuildEObjStationLookup(dataManager, MarketBoardCanonicalEObjId, "Market Board");

        this.magicSort.IsOrchestratorActive = () => IsAnyOrchestratorActive;
        this.magicSort.OnUserStopRequested = () => StopAll("Stopped.");

        this.framework.Update += OnFrameworkUpdate;
    }

    public bool IsAnyOrchestratorActive
        => pendingStation != null
            || bellSweep.IsRunning
            || marketBoardBuy.IsAutoBuying
            || materialTracker.IsPendingCraftActive;

    public bool IsAnythingActive => IsAnyOrchestratorActive || transactionQueue.IsBusy;

    public void StopAll(string? echoReason = null)
    {
        var wasActive = IsAnythingActive;
        pendingStation = null;

        bellSweep.Stop(null);
        marketBoardBuy.StopAutoBuy(null);
        materialTracker.StopPendingCraft();
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

        if (IsAnyOrchestratorActive || transactionQueue.IsExecuting)
        {
            StopAll("Stopped active Magic action.");
            return;
        }

        if (transactionQueue.IsCoolingDown)
        {
            chatGui.Print("Finishing previous action, try again in a moment.");
            return;
        }

        if (intent == MagicActionIntent.Use)
        {
            magicSort.ExecuteUseAll();
            return;
        }

        if (TryDispatchOpenStation(intent))
        {
            return;
        }

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

                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "MiragePrismPrismBoxCrystallize"))
                {
                    magicSort.ExecuteGlamourDresserStore();
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
                            req.StartedUtc = now;
                        }
                        else
                        {
                            pendingStation = null;
                            chatGui.Print("Select 'Place items in the armoire' and click Magic Store.");
                        }
                    }
                }

                break;

            case StationKind.GlamourDresser:
                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "MiragePrismPrismBoxCrystallize"))
                {
                    if (WaitHumanBeat(req, now))
                    {
                        pendingStation = null;
                        magicSort.ExecuteGlamourDresserStore();
                    }
                }
                else if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "MiragePrismPrismBox"))
                {
                    if (req.PrismBoxSeenUtc == DateTime.MinValue)
                    {
                        req.PrismBoxSeenUtc = now;
                    }
                    else if ((now - req.PrismBoxSeenUtc).TotalSeconds >= GlamourCrystallizeSettleGraceSeconds)
                    {
                        pendingStation = null;
                        chatGui.Print("Open the Glamour Creation list inside the Glamour Dresser, then click Magic Store.");
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
                else if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Talk"))
                {
                    if (WaitHumanBeat(req, now))
                    {
                        if (req.TalkAdvances >= MaxTalkAdvances)
                        {
                            pendingStation = null;
                            chatGui.Print($"Stopped: {req.StationName} dialogue did not open a shop window.");
                        }
                        else
                        {
                            req.TalkAdvances++;
                            TryAdvanceTalkDialog();
                            req.ReadyAtUtc = DateTime.MinValue;
                        }
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
                            req.StartedUtc = now;
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
                        var gcAddon = (FFXIVClientStructs.FFXIV.Client.UI.AddonGrandCompanySupplyList*)NativeUiHelpers.GetReadyAddon(gameGui, "GrandCompanySupplyList");
                        if (gcAddon != null && gcAddon->SelectedTab != 2)
                        {
                            if (req.SwitchedGcTab)
                            {
                                pendingStation = null;
                                chatGui.Print("Expert Delivery tab is unavailable or locked on this character.");
                            }
                            else
                            {
                                req.SwitchedGcTab = true;
                                magicSort.ExecuteGrandCompanySort();
                                req.ReadyAtUtc = DateTime.MinValue;
                                req.StartedUtc = now;
                            }
                        }
                        else
                        {
                            pendingStation = null;
                            magicSort.ExecuteGrandCompanySort();
                        }
                    }
                }
                else if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Talk"))
                {
                    if (WaitHumanBeat(req, now))
                    {
                        if (req.TalkAdvances >= MaxTalkAdvances)
                        {
                            pendingStation = null;
                            chatGui.Print("Stopped: Grand Company dialogue did not open supply menu.");
                        }
                        else
                        {
                            req.TalkAdvances++;
                            TryAdvanceTalkDialog();
                            req.ReadyAtUtc = DateTime.MinValue;
                        }
                    }
                }
                else if (!req.ClickedSubMenu
                    && NativeUiHelpers.IsAddonOpenAndReady(gameGui, "SelectString"))
                {
                    if (WaitHumanBeat(req, now))
                    {
                        if (TryClickGcSupplyMenuEntry())
                        {
                            req.ClickedSubMenu = true;
                            req.ReadyAtUtc = DateTime.MinValue;
                            req.StartedUtc = now;
                        }
                        else
                        {
                            pendingStation = null;
                            chatGui.Print("Open the Grand Company Expert Delivery tab, then click Magic Sell.");
                        }
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

    private bool TryAdvanceTalkDialog()
    {
        var talk = NativeUiHelpers.GetReadyAddon(gameGui, "Talk");
        if (talk == null)
        {
            return false;
        }

        var args = stackalloc AtkValue[1];
        args[0] = NativeUiHelpers.IntValue(0);
        return NativeUiHelpers.FireCallback(talk, true, new ReadOnlySpan<AtkValue>(args, 1));
    }

    private static bool MatchesShopSubMenu(string text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || text.Contains("Buyback", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Rückkauf", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Racheter", StringComparison.OrdinalIgnoreCase)
            || text.Contains("買い戻", StringComparison.Ordinal))
        {
            return false;
        }

        return text.Contains("Purchase", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Shop", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Buy", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Kaufen", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Acheter", StringComparison.OrdinalIgnoreCase)
            || text.Contains("購入", StringComparison.Ordinal);
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
                if (MatchesShopSubMenu(text))
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
                if (MatchesShopSubMenu(text))
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
                || text.Contains("Place", StringComparison.OrdinalIgnoreCase)
                || text.Contains("einlagern", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Déposer", StringComparison.OrdinalIgnoreCase)
                || text.Contains("しまう", StringComparison.Ordinal))
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
            if (text.Contains("supply and provisioning", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Liefereinsätze", StringComparison.OrdinalIgnoreCase)
                || text.Contains("ravitaillement", StringComparison.OrdinalIgnoreCase)
                || text.Contains("調達任務", StringComparison.Ordinal))
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
            || condition[ConditionFlag.BoundByDuty]
            || condition[ConditionFlag.BetweenAreas]
            || condition[ConditionFlag.BetweenAreas51]
            || condition[ConditionFlag.OccupiedInCutSceneEvent]
            || condition[ConditionFlag.OccupiedInEvent]
            || condition[ConditionFlag.OccupiedInQuestEvent]
            || condition[ConditionFlag.Casting]
            || condition[ConditionFlag.Mounted]
            || condition[ConditionFlag.RidingPillion]
            || condition[ConditionFlag.Unconscious])
        {
            reason = "Cannot interact right now (busy, in duty, mounted, casting, or in combat).";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool HasEligibleWorkForStation(StationKind kind, MagicActionIntent intent)
    {
        if (intent == MagicActionIntent.Sell)
        {
            return kind switch
            {
                StationKind.SummoningBell => magicSort.CountPendingBagMarketItems() > 0,
                StationKind.VendorNpc => magicSort.CountBagVendorItems() > 0,
                StationKind.GrandCompanyNpc => magicSort.CountBagGcItems() > 0,
                _ => true,
            };
        }

        if (intent == MagicActionIntent.Sort)
        {
            return kind switch
            {
                StationKind.Armoire => magicSort.HasArmoireWork(),
                StationKind.GlamourDresser => magicSort.HasGlamourRouteItems(),
                StationKind.SummoningBell => magicSort.EvaluateAllBellWork().Values.Any(s => s.HasAnyWork),
                _ => true,
            };
        }

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
        var bestHasWork = false;
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
            if (distSq > maxRange * maxRange)
            {
                continue;
            }

            var hasWork = HasEligibleWorkForStation(kind, intent);
            if (bestObject == null
                || (hasWork && !bestHasWork)
                || (hasWork == bestHasWork && distSq < bestDistSq))
            {
                bestHasWork = hasWork;
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
                && (summoningBellIds.Contains(obj.BaseId)
                    || string.Equals(name, summoningBellLocalizedName, StringComparison.OrdinalIgnoreCase)))
            {
                kind = StationKind.SummoningBell;
                maxRange = EventObjectInteractRangeYalms;
                return true;
            }

            if (intent == MagicActionIntent.Sort
                && (armoireIds.Contains(obj.BaseId)
                    || string.Equals(name, armoireLocalizedName, StringComparison.OrdinalIgnoreCase)))
            {
                kind = StationKind.Armoire;
                maxRange = EventObjectInteractRangeYalms;
                return true;
            }

            if (intent == MagicActionIntent.Sort
                && (glamourDresserIds.Contains(obj.BaseId)
                    || string.Equals(name, glamourDresserLocalizedName, StringComparison.OrdinalIgnoreCase)))
            {
                kind = StationKind.GlamourDresser;
                maxRange = EventObjectInteractRangeYalms;
                return true;
            }

            if (intent == MagicActionIntent.Buy
                && (marketBoardIds.Contains(obj.BaseId)
                    || string.Equals(name, marketBoardLocalizedName, StringComparison.OrdinalIgnoreCase)))
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

    private static (HashSet<uint> Ids, string LocalizedName) BuildEObjStationLookup(
        IDataManager dataManager,
        uint canonicalRowId,
        string fallbackName)
    {
        var ids = new HashSet<uint> { canonicalRowId };
        var eobjSheet = dataManager.GetExcelSheet<EObjName>();
        if (eobjSheet == null)
        {
            return (ids, fallbackName);
        }

        var canonicalName = eobjSheet.GetRowOrDefault(canonicalRowId)?.Singular.ExtractText()?.Trim();
        if (string.IsNullOrWhiteSpace(canonicalName))
        {
            return (ids, fallbackName);
        }

        foreach (var row in eobjSheet)
        {
            if (row.RowId == 0)
            {
                continue;
            }

            if (string.Equals(row.Singular.ExtractText().Trim(), canonicalName, StringComparison.OrdinalIgnoreCase))
            {
                ids.Add(row.RowId);
            }
        }

        return (ids, canonicalName);
    }

    private bool HasGilShopHandler(uint enpcId)
    {
        var enpcSheet = dataManager.GetExcelSheet<ENpcBase>();
        var row = enpcSheet?.GetRowOrDefault(enpcId);
        if (row is null)
        {
            return false;
        }

        var gilShopSheet = dataManager.GetExcelSheet<GilShop>();
        var topicSheet = dataManager.GetExcelSheet<TopicSelect>();
        var preHandlerSheet = dataManager.GetExcelSheet<PreHandler>();
        if (gilShopSheet == null)
        {
            return false;
        }

        bool IsGilShopOrWrapped(uint handlerId)
        {
            if (handlerId == 0)
            {
                return false;
            }

            if (gilShopSheet.HasRow(handlerId))
            {
                return true;
            }

            if (preHandlerSheet != null && preHandlerSheet.TryGetRow(handlerId, out var pre))
            {
                var targetId = pre.Target.RowId;
                if (targetId != 0 && gilShopSheet.HasRow(targetId))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (var dataRef in row.Value.ENpcData)
        {
            var id = dataRef.RowId;
            if (id == 0)
            {
                continue;
            }

            if (IsGilShopOrWrapped(id))
            {
                return true;
            }

            if (topicSheet != null && topicSheet.TryGetRow(id, out var topic))
            {
                foreach (var shopRef in topic.Shop)
                {
                    if (IsGilShopOrWrapped(shopRef.RowId))
                    {
                        return true;
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
