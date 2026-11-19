using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit;
using Lumina.Excel.Sheets;
using MagicInventory.Core;
using MagicInventory.Services;
using MagicInventory.Services.MagicSort;
using MagicInventory.Services.Triage;
using MagicInventory.UI;

namespace MagicInventory;

public sealed class Plugin : IDalamudPlugin
{
    private const string MagicCommandName = "/magic";
    private const string MagicShortCommandName = "/m";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IContextMenu contextMenu;
    private readonly ICommandManager commandManager;
    private readonly IDataManager dataManager;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    private readonly WindowSystem windowSystem = new("MagicInventory");
    private readonly SettingsWindow settingsWindow;
    private readonly WorldHopPromptWindow worldHopPromptWindow;
    private readonly ItemTooltipController itemTooltipController;
    private readonly InventoryHighlightController inventoryHighlightController;
    private RecipeNoteController? recipeNoteController;
    private RetainerController? retainerController;
    private MarketBoardController? marketBoardController;
    private MagicSortControllers? magicSortControllers;
    private MaterialTrackerOverlay? trackerOverlay;
    private bool isDisposed;

    public Configuration Configuration { get; }
    public HoldingsService Holdings { get; }
    public SafeTransactionQueue TransactionQueue { get; }
    public MaterialTrackerService MaterialTracker { get; }
    public UniversalisClient Universalis { get; }
    public InventorySafetyEvaluator Safety { get; }
    public ItemTriageEngine TriageEngine { get; }
    public MarketBoardBuyPlanner MarketBoardBuy { get; }
    public MagicSortService MagicSort { get; }
    public BellSweepService BellSweep { get; }
    public MagicActionCoordinator MagicActions { get; }

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        IGameGui gameGui,
        IGameInteropProvider gameInteropProvider,
        ICommandManager commandManager,
        IContextMenu contextMenu,
        IDataManager dataManager,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.contextMenu = contextMenu;
        this.commandManager = commandManager;
        this.dataManager = dataManager;
        this.chatGui = chatGui;
        this.log = log;

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Holdings = new HoldingsService(pluginInterface, dataManager, log);
        TransactionQueue = new SafeTransactionQueue(framework, clientState, objectTable, condition, gameGui, chatGui, log);
        MaterialTracker = new MaterialTrackerService(
            pluginInterface,
            Configuration,
            Holdings,
            dataManager,
            gameGui);
        Universalis = new UniversalisClient(pluginInterface, log);
        Safety = new InventorySafetyEvaluator(dataManager, Configuration, MaterialTracker.IsItemInActiveTracker);
        TriageEngine = new ItemTriageEngine(
            clientState,
            objectTable,
            dataManager,
            Configuration,
            Holdings,
            Safety,
            Universalis);
        MagicSort = new MagicSortService(
            pluginInterface,
            dataManager,
            gameGui,
            condition,
            chatGui,
            Configuration,
            Holdings,
            TransactionQueue,
            Safety,
            TriageEngine,
            MaterialTracker.GetMissingTrackedForRetainerWithdraw);
        worldHopPromptWindow = new WorldHopPromptWindow(commandManager, chatGui);
        MarketBoardBuy = new MarketBoardBuyPlanner(
            pluginInterface,
            framework,
            clientState,
            objectTable,
            condition,
            dataManager,
            gameGui,
            chatGui,
            Configuration,
            TransactionQueue,
            MaterialTracker,
            MagicSort,
            Universalis,
            worldHopPromptWindow.ShowOffer);
        BellSweep = new BellSweepService(
            framework,
            clientState,
            objectTable,
            condition,
            dataManager,
            gameGui,
            chatGui,
            TransactionQueue,
            MagicSort);
        MagicActions = new MagicActionCoordinator(
            framework,
            clientState,
            objectTable,
            condition,
            dataManager,
            gameGui,
            chatGui,
            TransactionQueue,
            MagicSort,
            BellSweep,
            MarketBoardBuy);

        itemTooltipController = new ItemTooltipController(gameInteropProvider, gameGui, log, MagicSort);
        itemTooltipController.Enable();

        inventoryHighlightController = new InventoryHighlightController(framework, gameGui, Configuration, MagicSort);

        settingsWindow = new SettingsWindow(
            pluginInterface,
            dataManager,
            Configuration,
            Holdings,
            Universalis,
            TriageEngine,
            MagicSort);
        windowSystem.AddWindow(settingsWindow);
        windowSystem.AddWindow(worldHopPromptWindow);

        this.pluginInterface.UiBuilder.Draw += DrawUi;
        this.pluginInterface.UiBuilder.OpenConfigUi += ToggleSettingsWindow;
        this.pluginInterface.UiBuilder.OpenMainUi += ToggleSettingsWindow;

        this.commandManager.AddHandler(MagicCommandName, new CommandInfo(OnMagicCommand)
        {
            HelpMessage = "Magic Inventory: /magic (settings) | sort | sell | buy | track <recipe> [qty] | use | stop | scan",
        });
        this.commandManager.AddHandler(MagicShortCommandName, new CommandInfo(OnMagicCommand)
        {
            HelpMessage = "Shortcut for /magic: /m | sort | sell | buy | track <recipe> [qty] | use | stop | scan",
        });
        this.contextMenu.OnMenuOpened += OnContextMenuOpened;

        _ = InitializeNativeUiAsync(pluginInterface, framework, clientState, condition);
    }

    private async Task InitializeNativeUiAsync(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IClientState clientState,
        ICondition condition)
    {
        try
        {
            await KamiToolKitLibrary.InitializeAsync(pluginInterface, "Magic Inventory").ConfigureAwait(false);
            if (isDisposed)
            {
                return;
            }

            await framework.RunOnFrameworkThread(() =>
            {
                if (isDisposed)
                {
                    return;
                }

                recipeNoteController = new RecipeNoteController(MaterialTracker);
                recipeNoteController.Enable();

                retainerController = new RetainerController(MagicSort, inventoryHighlightController);
                retainerController.Enable();

                marketBoardController = new MarketBoardController(MaterialTracker, MarketBoardBuy);
                marketBoardController.Enable();

                magicSortControllers = new MagicSortControllers(MagicSort, BellSweep, inventoryHighlightController);
                magicSortControllers.Enable();

                trackerOverlay = new MaterialTrackerOverlay(
                    MaterialTracker,
                    Configuration,
                    pluginInterface,
                    clientState,
                    condition,
                    chatGui);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to initialize Magic Inventory native UI controllers.");
        }
    }

    private void DrawUi() => windowSystem.Draw();

    private void ToggleSettingsWindow() => settingsWindow.IsOpen = !settingsWindow.IsOpen;

    private void OnMagicCommand(string command, string args)
    {
        var raw = (args ?? string.Empty).Trim();
        var spaceIdx = raw.IndexOf(' ');
        var verb = (spaceIdx >= 0 ? raw[..spaceIdx] : raw).ToLowerInvariant();
        var rest = spaceIdx >= 0 ? raw[(spaceIdx + 1)..].Trim() : string.Empty;

        switch (verb)
        {
            case "":
            case "config":
            case "settings":
                ToggleSettingsWindow();
                break;

            case "status":
            {
                var world = TriageEngine.ActiveWorldName;
                var scanNote = Universalis.IsScanning
                    ? $", checking prices ({Universalis.CompletedInScan}/{Universalis.TotalQueuedInScan})"
                    : string.Empty;
                chatGui.Print(
                    $"Magic Inventory ({(string.IsNullOrEmpty(world) ? "offline" : world)}): " +
                    $"{MagicSort.ManifestItemCount} items, {MagicSort.LockedItemCount} locked, " +
                    $"{MaterialTracker.TrackedItemCount} tracked{scanNote}.");
                break;
            }

            case "scan":
            {
                var queued = TriageEngine.TriggerFullScan(forceRefreshPrices: true);
                chatGui.Print(queued > 0
                    ? $"Magic Inventory: checking {TriageEngine.ActiveWorldName} prices for {queued} item(s)..."
                    : "Magic Inventory: bag rules refreshed.");
                break;
            }

            case "sort":
                MagicActions.Execute(MagicActionIntent.Sort);
                break;

            case "sell":
                MagicActions.Execute(MagicActionIntent.Sell);
                break;

            case "buy":
                MagicActions.Execute(MagicActionIntent.Buy);
                break;

            case "use":
                MagicSort.ExecuteUseAll();
                break;

            case "track":
            {
                MaterialTracker.TrackRecipeByQuery(rest, out var trackMsg);
                if (!string.IsNullOrWhiteSpace(trackMsg))
                {
                    chatGui.Print(trackMsg);
                }

                break;
            }

            case "stop":
                MaterialTracker.StopPendingCraft();
                MagicActions.Execute(MagicActionIntent.Stop);
                break;

            default:
                chatGui.Print("Usage: /m [sort | sell | buy | track <recipe> [qty] | use | stop | scan | status]");
                break;
        }
    }

    private unsafe void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        uint baseItemId = 0;
        if (args.Target is MenuTargetInventory invTarget && invTarget.TargetItem is { } invItem)
        {
            baseItemId = invItem.BaseItemId;
        }
        else if (args.Target is MenuTargetDefault)
        {
            // Support right-clicking items in Market Board, Crafting Log, chat links, etc. via AgentItemDetail
            var detail = AgentItemDetail.Instance();
            if (detail != null && detail->ItemId != 0)
            {
                var addon = args.AddonName ?? string.Empty;
                if (addon is "ItemSearch" or "ItemSearchResult" or "RecipeNote" or "ChatLog" or "RetainerSellList" or "Shop" or "GrandCompanySupplyList" or "ArmouryBoard")
                {
                    baseItemId = HoldingsService.NormalizeBaseItemId(detail->ItemId);
                }
            }
        }

        if (baseItemId == 0)
        {
            return;
        }

        var capturedId = baseItemId;
        var itemName = MagicSort.GetItemDisplayName(capturedId);
        var isPinned = MagicSort.IsItemOverwritten(capturedId);

        args.AddMenuItem(new MenuItem
        {
            Name = isPinned ? "Magic Inventory (Pinned)" : "Magic Inventory",
            UseDefaultPrefix = true,
            IsSubmenu = true,
            OnClicked = clickedArgs => OpenItemRuleSubmenu(clickedArgs, capturedId, itemName),
        });
    }

    private void OpenItemRuleSubmenu(IMenuItemClickedArgs clickedArgs, uint baseItemId, string itemName)
    {
        var items = new List<IMenuItem>();
        var itemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(baseItemId);
        var canTag = MagicSortService.CanLockOrTagBaseId(baseItemId);
        var isPinned = MagicSort.IsItemOverwritten(baseItemId);

        if (canTag)
        {
            var entry = TriageEngine.ClassifySingleItemOnDemand(baseItemId, queuePriceIfMissing: false);
            var currentDest = entry == null
                ? "Keep"
                : (entry.Route == "Retainer" && !string.IsNullOrEmpty(entry.TargetRetainerName)
                    ? entry.TargetRetainerName
                    : (entry.Route == "GrandCompany" ? "Grand Company" : entry.Route));
            if (entry != null && string.Equals(entry.Route, "Market", StringComparison.OrdinalIgnoreCase))
            {
                var prio = Safety.GetMarketPriority(baseItemId);
                if (prio > 0)
                {
                    currentDest += " (High)";
                }
                else if (prio < 0)
                {
                    currentDest += " (Low)";
                }
            }

            items.Add(new MenuItem
            {
                Name = isPinned ? $"Current: {currentDest} [Pinned]" : $"Current: {currentDest} [Auto]",
                IsEnabled = false,
            });

            items.Add(new MenuItem
            {
                Name = "Keep in Bag",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Keep", null, "Keep in Bag"),
            });

            if (itemRow is { ItemAction.RowId: > 0 })
            {
                items.Add(new MenuItem
                {
                    Name = "Use (Use All)",
                    OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Use", null, "Use"),
                });
            }

            if (itemRow is null || !itemRow.Value.IsUntradable)
            {
                items.Add(new MenuItem
                {
                    Name = "Sell on Market (High Priority)",
                    OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Market", 1, "Market (High Priority)"),
                });
                items.Add(new MenuItem
                {
                    Name = "Sell on Market",
                    OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Market", 0, "Market"),
                });
                items.Add(new MenuItem
                {
                    Name = "Sell on Market (Low Priority)",
                    OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Market", -1, "Market (Low Priority)"),
                });
            }

            if (itemRow is null || (itemRow.Value.PriceLow > 0 && !itemRow.Value.IsUnique))
            {
                items.Add(new MenuItem
                {
                    Name = "Sell to Vendor",
                    OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Vendor", null, "Vendor"),
                });
            }

            if (itemRow is { EquipSlotCategory.RowId: > 0, IsUntradable: true, Rarity: 2 or 3 or 7 })
            {
                items.Add(new MenuItem
                {
                    Name = "Grand Company Seals",
                    OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "GrandCompany", null, "Grand Company"),
                });
            }

            foreach (var ret in Holdings.GetKnownRetainersForCurrentCharacter())
            {
                var retId = ret.RetainerId;
                var retName = ret.Name;
                items.Add(new MenuItem
                {
                    Name = $"Store: {retName}",
                    OnClicked = _ => ApplyContextOverride(baseItemId, itemName, $"Retainer:{retId}", null, retName),
                });
            }

            var isLocked = MagicSort.IsItemLocked(baseItemId);
            items.Add(new MenuItem
            {
                Name = isLocked ? "Unlock Item" : "Lock Item",
                OnClicked = _ =>
                {
                    if (MagicSort.ToggleItemLock(baseItemId, out var locked))
                    {
                        chatGui.Print(locked ? $"Locked {itemName}." : $"Unlocked {itemName}.");
                    }
                },
            });

            if (isPinned)
            {
                items.Add(new MenuItem
                {
                    Name = "Reset to Auto",
                    OnClicked = _ =>
                    {
                        MagicSort.SetItemOverride(baseItemId, string.Empty);
                        chatGui.Print($"Reset {itemName} back to automatic rules.");
                    },
                });
            }
        }

        var isTracked = MaterialTracker.IsItemTracked(baseItemId);
        items.Add(new MenuItem
        {
            Name = isTracked ? "Untrack Item" : "Track Item",
            OnClicked = _ => MaterialTracker.ToggleSingleItem(baseItemId),
        });

        clickedArgs.OpenSubmenu("Magic Inventory", items);
    }

    private void ApplyContextOverride(uint baseItemId, string itemName, string routeSpec, int? marketPriority, string displayLabel)
    {
        MagicSort.SetItemOverride(baseItemId, routeSpec, marketPriority);
        chatGui.Print($"Pinned {itemName} -> {displayLabel}.");
    }

    public void Dispose()
    {
        isDisposed = true;
        pluginInterface.UiBuilder.Draw -= DrawUi;
        pluginInterface.UiBuilder.OpenConfigUi -= ToggleSettingsWindow;
        pluginInterface.UiBuilder.OpenMainUi -= ToggleSettingsWindow;
        windowSystem.RemoveAllWindows();
        commandManager.RemoveHandler(MagicCommandName);
        commandManager.RemoveHandler(MagicShortCommandName);
        contextMenu.OnMenuOpened -= OnContextMenuOpened;
        inventoryHighlightController.Dispose();
        itemTooltipController.Dispose();
        MagicActions.Dispose();
        MarketBoardBuy.Dispose();
        BellSweep.Dispose();
        MagicSort.Dispose();
        TriageEngine.Dispose();
        Universalis.Dispose();
        TransactionQueue.Dispose();

        try
        {
            trackerOverlay?.Dispose();
            trackerOverlay = null;

            magicSortControllers?.Dispose();
            magicSortControllers = null;

            marketBoardController?.Dispose();
            marketBoardController = null;

            retainerController?.Dispose();
            retainerController = null;

            recipeNoteController?.Dispose();
            recipeNoteController = null;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Cleanup warning during UI controller disposal.");
        }

        KamiToolKitLibrary.Dispose();
    }
}
