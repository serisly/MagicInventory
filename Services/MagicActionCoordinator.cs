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

    private sealed record OpenStationDispatch(
        MagicActionIntent Intent,
        string[] Addons,
        System.Action Dispatch);

    private sealed record EObjStationRule(
        StationKind Kind,
        Func<MagicActionIntent, bool> MatchesIntent,
        HashSet<uint> Ids,
        string LocalizedName);

    private sealed class StationDescriptor
    {
        public required StationKind Kind { get; init; }
        public required string ReadyAddon { get; init; }
        public required Action<PendingStationRequest, DateTime> OnReady { get; init; }
        public bool AdvanceTalk { get; init; }
        public Func<PendingStationRequest, string>? FormatTalkTimeout { get; init; }
        public Func<string, bool>? SubMenuMatcher { get; init; }
        public bool IncludeIconSelectMenu { get; init; }
        public Func<PendingStationRequest, string>? FormatSubMenuFallback { get; init; }
        public Action<PendingStationRequest, DateTime>? OnPendingTick { get; init; }
    }

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

    private readonly EObjStationRule[] eobjStationRules;
    private readonly OpenStationDispatch[] openStationDispatches;
    private readonly Dictionary<StationKind, StationDescriptor> stationDescriptors;

    private PendingStationRequest? pendingStation;

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

        eobjStationRules =
        [
            BuildEObjRule(dataManager, StationKind.SummoningBell, static i => i is MagicActionIntent.Sort or MagicActionIntent.Sell, SummoningBellCanonicalEObjId, "Summoning Bell"),
            BuildEObjRule(dataManager, StationKind.Armoire, static i => i == MagicActionIntent.Sort, ArmoireCanonicalEObjId, "Armoire"),
            BuildEObjRule(dataManager, StationKind.GlamourDresser, static i => i == MagicActionIntent.Sort, GlamourDresserCanonicalEObjId, "Glamour Dresser"),
            BuildEObjRule(dataManager, StationKind.MarketBoard, static i => i == MagicActionIntent.Buy, MarketBoardCanonicalEObjId, "Market Board"),
        ];

        openStationDispatches = BuildOpenStationDispatches();
        stationDescriptors = BuildStationDescriptors();

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
            magicSort.ExecuteAction(StationActionId.UseAll);
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
                magicSort.ExecuteAction(StationActionId.InventorySort);
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

    private OpenStationDispatch[] BuildOpenStationDispatches() =>
    [
        new(MagicActionIntent.Sort, ["RetainerList"], () => bellSweep.StartBellSweep(marketSellOnly: false)),
        new(MagicActionIntent.Sort, ["InventoryRetainerLarge", "InventoryRetainer"], () => magicSort.ExecuteAction(StationActionId.RetainerSort)),
        new(MagicActionIntent.Sort, ["Materialize"], () => magicSort.ExecuteAction(StationActionId.MateriaExtract)),
        new(MagicActionIntent.Sort, ["Cabinet"], () => magicSort.ExecuteAction(StationActionId.ArmoireStore)),
        new(MagicActionIntent.Sort, ["MiragePrismPrismBoxCrystallize"], () => magicSort.ExecuteAction(StationActionId.GlamourStore)),
        new(MagicActionIntent.Sell, ["RetainerSellList"], () => magicSort.ExecuteAction(StationActionId.MarketSell)),
        new(MagicActionIntent.Sell, ["RetainerList"], () => bellSweep.StartBellSweep(marketSellOnly: true)),
        new(MagicActionIntent.Sell, ["Shop"], () => magicSort.ExecuteAction(StationActionId.VendorSell)),
        new(MagicActionIntent.Sell, ["GrandCompanySupplyList"], () => magicSort.ExecuteAction(StationActionId.GrandCompany)),
        new(MagicActionIntent.Buy, ["ItemSearchResult"], () => marketBoardBuy.ExecuteBuyBestListing()),
        new(MagicActionIntent.Buy, ["ItemSearch"], () => marketBoardBuy.ToggleAutoBuy()),
    ];

    private Dictionary<StationKind, StationDescriptor> BuildStationDescriptors()
    {
        var descriptors = new StationDescriptor[]
        {
            new()
            {
                Kind = StationKind.SummoningBell,
                ReadyAddon = "RetainerList",
                OnReady = (req, _) =>
                {
                    pendingStation = null;
                    bellSweep.StartBellSweep(marketSellOnly: req.Intent == MagicActionIntent.Sell);
                },
            },
            new()
            {
                Kind = StationKind.Armoire,
                ReadyAddon = "Cabinet",
                OnReady = (_, _) =>
                {
                    pendingStation = null;
                    magicSort.ExecuteAction(StationActionId.ArmoireStore);
                },
                SubMenuMatcher = MatchesArmoirePlaceMenu,
                FormatSubMenuFallback = static _ => "Select 'Place items in the armoire' and click Magic Store.",
            },
            new()
            {
                Kind = StationKind.GlamourDresser,
                ReadyAddon = "MiragePrismPrismBoxCrystallize",
                OnReady = (_, _) =>
                {
                    pendingStation = null;
                    magicSort.ExecuteAction(StationActionId.GlamourStore);
                },
                OnPendingTick = (req, now) =>
                {
                    if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, "MiragePrismPrismBox"))
                    {
                        return;
                    }

                    if (req.PrismBoxSeenUtc == DateTime.MinValue)
                    {
                        req.PrismBoxSeenUtc = now;
                    }
                    else if ((now - req.PrismBoxSeenUtc).TotalSeconds >= GlamourCrystallizeSettleGraceSeconds)
                    {
                        pendingStation = null;
                        chatGui.Print("Open the Glamour Creation list inside the Glamour Dresser, then click Magic Store.");
                    }
                },
            },
            new()
            {
                Kind = StationKind.MarketBoard,
                ReadyAddon = "ItemSearch",
                OnReady = (_, _) =>
                {
                    pendingStation = null;
                    marketBoardBuy.ToggleAutoBuy();
                },
            },
            new()
            {
                Kind = StationKind.VendorNpc,
                ReadyAddon = "Shop",
                OnReady = (_, _) =>
                {
                    pendingStation = null;
                    magicSort.ExecuteAction(StationActionId.VendorSell);
                },
                AdvanceTalk = true,
                FormatTalkTimeout = static req => $"Stopped: {req.StationName} dialogue did not open a shop window.",
                SubMenuMatcher = MatchesShopSubMenu,
                IncludeIconSelectMenu = true,
                FormatSubMenuFallback = static req => $"{req.StationName} has multiple menus. Open a shop window, then click Magic Sell.",
            },
            new()
            {
                Kind = StationKind.GrandCompanyNpc,
                ReadyAddon = "GrandCompanySupplyList",
                OnReady = (req, now) =>
                {
                    var gcAddon = (AddonGrandCompanySupplyList*)NativeUiHelpers.GetReadyAddon(gameGui, "GrandCompanySupplyList");
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
                            magicSort.ExecuteAction(StationActionId.GrandCompany, fromOrchestrator: true);
                            req.ReadyAtUtc = DateTime.MinValue;
                            req.StartedUtc = now;
                        }
                    }
                    else
                    {
                        pendingStation = null;
                        magicSort.ExecuteAction(StationActionId.GrandCompany);
                    }
                },
                AdvanceTalk = true,
                FormatTalkTimeout = static _ => "Stopped: Grand Company dialogue did not open supply menu.",
                SubMenuMatcher = MatchesGcSupplyMenu,
                FormatSubMenuFallback = static _ => "Open the Grand Company Expert Delivery tab, then click Magic Sell.",
            },
        };

        return descriptors.ToDictionary(static d => d.Kind);
    }

    private bool TryDispatchOpenStation(MagicActionIntent intent)
    {
        foreach (var entry in openStationDispatches)
        {
            if (entry.Intent != intent)
            {
                continue;
            }

            foreach (var addon in entry.Addons)
            {
                if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, addon))
                {
                    entry.Dispatch();
                    return true;
                }
            }
        }

        return false;
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

        if (!stationDescriptors.TryGetValue(req.Station, out var descriptor))
        {
            pendingStation = null;
            return;
        }

        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, descriptor.ReadyAddon))
        {
            if (WaitHumanBeat(req, now))
            {
                descriptor.OnReady(req, now);
            }

            return;
        }

        if (descriptor.AdvanceTalk && NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Talk"))
        {
            if (WaitHumanBeat(req, now))
            {
                if (req.TalkAdvances >= MaxTalkAdvances)
                {
                    pendingStation = null;
                    chatGui.Print(descriptor.FormatTalkTimeout!(req));
                }
                else
                {
                    req.TalkAdvances++;
                    TryAdvanceTalkDialog();
                    req.ReadyAtUtc = DateTime.MinValue;
                }
            }

            return;
        }

        if (descriptor.SubMenuMatcher != null
            && !req.ClickedSubMenu
            && ((descriptor.IncludeIconSelectMenu && NativeUiHelpers.IsAddonOpenAndReady(gameGui, "SelectIconString"))
                || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "SelectString")))
        {
            if (WaitHumanBeat(req, now))
            {
                if (TryClickPopupMenuEntry(descriptor.SubMenuMatcher, descriptor.IncludeIconSelectMenu))
                {
                    req.ClickedSubMenu = true;
                    req.ReadyAtUtc = DateTime.MinValue;
                    req.StartedUtc = now;
                }
                else
                {
                    pendingStation = null;
                    chatGui.Print(descriptor.FormatSubMenuFallback!(req));
                }
            }

            return;
        }

        descriptor.OnPendingTick?.Invoke(req, now);
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

    internal static bool MatchesShopSubMenu(string text)
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

    internal static bool MatchesArmoirePlaceMenu(string text)
        => !string.IsNullOrWhiteSpace(text)
            && (text.Contains("Place items in the armoire", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Place", StringComparison.OrdinalIgnoreCase)
                || text.Contains("einlagern", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Déposer", StringComparison.OrdinalIgnoreCase)
                || text.Contains("しまう", StringComparison.Ordinal));

    internal static bool MatchesGcSupplyMenu(string text)
        => !string.IsNullOrWhiteSpace(text)
            && (text.Contains("supply and provisioning", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Liefereinsätze", StringComparison.OrdinalIgnoreCase)
                || text.Contains("ravitaillement", StringComparison.OrdinalIgnoreCase)
                || text.Contains("調達任務", StringComparison.Ordinal));

    private bool TryClickPopupMenuEntry(Func<string, bool> matchesEntry, bool includeIconSelect = false)
    {
        if (includeIconSelect)
        {
            var iconSelect = (AddonSelectIconString*)NativeUiHelpers.GetReadyAddon(gameGui, "SelectIconString");
            if (iconSelect != null && TryClickPopupMenu((AtkUnitBase*)iconSelect, &iconSelect->PopupMenu.PopupMenu, matchesEntry))
            {
                return true;
            }
        }

        var strSelect = (AddonSelectString*)NativeUiHelpers.GetReadyAddon(gameGui, "SelectString");
        return strSelect != null && TryClickPopupMenu((AtkUnitBase*)strSelect, &strSelect->PopupMenu.PopupMenu, matchesEntry);
    }

    private static bool TryClickPopupMenu(AtkUnitBase* addon, PopupMenu* menu, Func<string, bool> matchesEntry)
    {
        if (menu == null || menu->EntryNames == null)
        {
            return false;
        }

        var count = menu->EntryCount;
        for (var i = 0; i < count; i++)
        {
            var ptr = menu->EntryNames[i].Value;
            if (ptr == null)
            {
                continue;
            }

            var text = MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue.Trim();
            if (matchesEntry(text))
            {
                var args = stackalloc AtkValue[1];
                args[0] = NativeUiHelpers.IntValue(i);
                return NativeUiHelpers.FireCallback(addon, true, new ReadOnlySpan<AtkValue>(args, 1));
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
            foreach (var rule in eobjStationRules)
            {
                if (rule.MatchesIntent(intent)
                    && (rule.Ids.Contains(obj.BaseId)
                        || string.Equals(name, rule.LocalizedName, StringComparison.OrdinalIgnoreCase)))
                {
                    kind = rule.Kind;
                    maxRange = EventObjectInteractRangeYalms;
                    return true;
                }
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

    private static EObjStationRule BuildEObjRule(
        IDataManager dataManager,
        StationKind kind,
        Func<MagicActionIntent, bool> matchesIntent,
        uint canonicalRowId,
        string fallbackName)
    {
        var ids = new HashSet<uint> { canonicalRowId };
        var eobjSheet = dataManager.GetExcelSheet<EObjName>();
        if (eobjSheet == null)
        {
            return new EObjStationRule(kind, matchesIntent, ids, fallbackName);
        }

        var canonicalName = eobjSheet.GetRowOrDefault(canonicalRowId)?.Singular.ExtractText()?.Trim();
        if (string.IsNullOrWhiteSpace(canonicalName))
        {
            return new EObjStationRule(kind, matchesIntent, ids, fallbackName);
        }

        foreach (var row in eobjSheet)
        {
            if (row.RowId != 0
                && string.Equals(row.Singular.ExtractText().Trim(), canonicalName, StringComparison.OrdinalIgnoreCase))
            {
                ids.Add(row.RowId);
            }
        }

        return new EObjStationRule(kind, matchesIntent, ids, canonicalName);
    }

    private bool HasGilShopHandler(uint enpcId)
    {
        var row = dataManager.GetExcelSheet<ENpcBase>()?.GetRowOrDefault(enpcId);
        var gilShopSheet = dataManager.GetExcelSheet<GilShop>();
        if (row is null || gilShopSheet == null)
        {
            return false;
        }

        var topicSheet = dataManager.GetExcelSheet<TopicSelect>();
        var preHandlerSheet = dataManager.GetExcelSheet<PreHandler>();

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

            return preHandlerSheet != null
                && preHandlerSheet.TryGetRow(handlerId, out var pre)
                && pre.Target.RowId != 0
                && gilShopSheet.HasRow(pre.Target.RowId);
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
