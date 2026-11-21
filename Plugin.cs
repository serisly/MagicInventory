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
    private readonly IGameGui gameGui;
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
    private readonly InventoryRouteOverlay inventoryRouteOverlay;
    private RecipeNoteController? recipeNoteController;
    private RetainerController? retainerController;
    private MarketBoardController? marketBoardController;
    private MagicSortControllers? magicSortControllers;
    private MaterialTrackerOverlay? trackerOverlay;
    private volatile bool isDisposed;

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
        this.gameGui = gameGui;
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
        inventoryRouteOverlay = new InventoryRouteOverlay(gameGui, dataManager, Configuration, Safety, MagicSort);

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

                retainerController = new RetainerController(MagicSort, inventoryHighlightController, inventoryRouteOverlay);
                retainerController.Enable();

                marketBoardController = new MarketBoardController(MaterialTracker, MarketBoardBuy);
                marketBoardController.Enable();

                magicSortControllers = new MagicSortControllers(MagicSort, BellSweep, inventoryHighlightController, inventoryRouteOverlay);
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

    private void DrawUi()
    {
        windowSystem.Draw();
        inventoryRouteOverlay.Draw();
    }

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
                    $"{MagicSort.ManifestItemCount} items, {MagicSort.CustomRuleCount} custom rules, " +
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
            baseItemId = ItemIdentity.NormalizeBaseItemId(invItem.ItemId);
            if (baseItemId == 0)
            {
                baseItemId = invItem.BaseItemId;
            }
        }
        else if (args.Target is MenuTargetDefault defaultTarget)
        {
            // Never attach item menus to right-clicks on players, NPCs, or world objects
            var isActorTarget = defaultTarget.TargetObjectId != 0
                || defaultTarget.TargetContentId != 0
                || !string.IsNullOrEmpty(defaultTarget.TargetName);

            if (!isActorTarget)
            {
                uint rawId = 0;
                var detail = AgentItemDetail.Instance();
                if (detail != null
                    && detail->ItemId != 0
                    && detail->DetailKind < FFXIVClientStructs.FFXIV.Client.Enums.DetailKind.Action)
                {
                    rawId = detail->ItemId;
                }
                else if (gameGui.HoveredItem is > 0 and < (ulong)ItemIdentity.EventItemIdThreshold)
                {
                    rawId = (uint)gameGui.HoveredItem;
                }

                if (rawId != 0)
                {
                    var normalized = ItemIdentity.NormalizeBaseItemId(rawId);
                    if (normalized != 0 && dataManager.GetExcelSheet<Item>()?.TryGetRow(normalized, out _) == true)
                    {
                        baseItemId = normalized;
                    }
                }
            }
        }

        if (baseItemId == 0)
        {
            return;
        }

        var capturedId = baseItemId;
        var itemName = MagicSort.GetItemDisplayName(capturedId);
        var openedFromRetainer = string.Equals(args.AddonName, "InventoryRetainer", StringComparison.Ordinal)
            || string.Equals(args.AddonName, "InventoryRetainerLarge", StringComparison.Ordinal);

        args.AddMenuItem(new MenuItem
        {
            Name = "Magic Inventory",
            UseDefaultPrefix = true,
            IsSubmenu = true,
            OnClicked = clickedArgs => OpenItemRuleSubmenu(clickedArgs, capturedId, itemName, openedFromRetainer),
        });
    }

    private void OpenItemRuleSubmenu(IMenuItemClickedArgs clickedArgs, uint baseItemId, string itemName, bool openedFromRetainer)
    {
        var items = new List<IMenuItem>();
        var routeFilters = new List<string?>();

        var itemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(baseItemId);
        var canTag = ItemIdentity.CanLockOrTagItem(baseItemId, itemRow);
        var isManual = MagicSort.IsItemOverwritten(baseItemId);

        if (canTag)
        {
            var currentEntry = TriageEngine.ClassifySingleItemOnDemand(baseItemId, queuePriceIfMissing: false);
            var autoEntry = isManual
                ? TriageEngine.ClassifySingleItemOnDemand(baseItemId, queuePriceIfMissing: false, ignoreManualOverride: true)
                : currentEntry;

            var currentRoute = currentEntry?.Route ?? "Keep";
            var autoLabel = RoutePresentation.FormatAutoPredictedLabel(autoEntry, out var autoRouteFilter);
            if (openedFromRetainer && string.Equals(autoRouteFilter, "Keep", StringComparison.OrdinalIgnoreCase))
            {
                var activeRetId = HoldingsService.GetActiveRetainerId();
                var activeRetName = HoldingsService.GetActiveRetainerName();
                if (activeRetId != 0 && !string.IsNullOrWhiteSpace(activeRetName))
                {
                    autoLabel = activeRetName;
                    autoRouteFilter = $"Retainer:{activeRetId}";
                }
            }

            items.Add(new MenuItem
            {
                Name = isManual ? $"Auto ({autoLabel})" : $"• Auto ({autoLabel})",
                OnClicked = _ =>
                {
                    MagicSort.SetItemOverride(baseItemId, string.Empty);
                    chatGui.Print($"Set {itemName} -> Auto ({autoLabel}).");
                },
            });
            routeFilters.Add(autoRouteFilter);

            var isKeepActive = isManual && string.Equals(currentRoute, "Keep", StringComparison.OrdinalIgnoreCase);
            items.Add(new MenuItem
            {
                Name = isKeepActive ? "• Keep" : "Keep",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Keep", null, "Keep"),
            });
            routeFilters.Add("Keep");

            var eligibility = ItemRouteEligibility.Compute(itemRow, TriageEngine.IsCabinetItem(baseItemId));

            if (eligibility.CanUse)
            {
                var isUseActive = isManual && string.Equals(currentRoute, "Use", StringComparison.OrdinalIgnoreCase);
                items.Add(new MenuItem
                {
                    Name = isUseActive ? "• Use" : "Use",
                    OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Use", null, "Use"),
                });
                routeFilters.Add("Use");
            }

            var retainers = Holdings.GetKnownRetainersForCurrentCharacter();
            if (retainers.Count > 0)
            {
                var isStoreActive = isManual && string.Equals(currentRoute, "Retainer", StringComparison.OrdinalIgnoreCase);
                items.Add(new MenuItem
                {
                    Name = isStoreActive ? "• Store" : "Store",
                    IsSubmenu = true,
                    OnClicked = subArgs => OpenStoreSubmenu(subArgs, baseItemId, itemName, isManual, currentEntry?.TargetRetainerId ?? 0UL),
                });
                routeFilters.Add("Retainer");
            }

            if (eligibility.CanSellAny)
            {
                var isSellActive = isManual
                    && (string.Equals(currentRoute, "Market", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(currentRoute, "Vendor", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(currentRoute, "GrandCompany", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(currentRoute, "GoldSaucer", StringComparison.OrdinalIgnoreCase));
                items.Add(new MenuItem
                {
                    Name = isSellActive ? "• Sell" : "Sell",
                    IsSubmenu = true,
                    OnClicked = subArgs => OpenSellSubmenu(
                        subArgs,
                        baseItemId,
                        itemName,
                        isManual,
                        currentRoute,
                        eligibility.CanMarket,
                        eligibility.CanVendor,
                        eligibility.CanGrandCompany,
                        eligibility.CanGoldSaucer),
                });
                routeFilters.Add("Sell");
            }

            if (eligibility.CanGlamour)
            {
                var isGlamActive = isManual && string.Equals(currentRoute, "Glamour", StringComparison.OrdinalIgnoreCase);
                items.Add(new MenuItem
                {
                    Name = isGlamActive ? "• Glamour Dresser" : "Glamour Dresser",
                    OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Glamour", null, "Glamour Dresser"),
                });
                routeFilters.Add("Glamour");
            }

            if (eligibility.CanArmoire)
            {
                var isArmoireActive = isManual && string.Equals(currentRoute, "Armoire", StringComparison.OrdinalIgnoreCase);
                items.Add(new MenuItem
                {
                    Name = isArmoireActive ? "• Armoire" : "Armoire",
                    OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Armoire", null, "Armoire"),
                });
                routeFilters.Add("Armoire");
            }
        }

        items.Add(new MenuItem
        {
            Name = "Track",
            IsSubmenu = true,
            OnClicked = subArgs => OpenTrackSubmenu(subArgs, baseItemId, itemName),
        });
        routeFilters.Add(null);

        inventoryHighlightController.SetActiveContextSubmenuRoutes("Magic Inventory", routeFilters);
        clickedArgs.OpenSubmenu("Magic Inventory", items);
    }

    private void OpenStoreSubmenu(IMenuItemClickedArgs clickedArgs, uint baseItemId, string itemName, bool isManual, ulong currentRetainerId)
    {
        var items = new List<IMenuItem>();
        var routeFilters = new List<string?>();

        foreach (var ret in Holdings.GetKnownRetainersForCurrentCharacter())
        {
            var retId = ret.RetainerId;
            var retName = ret.Name;
            var isActive = isManual && currentRetainerId == retId;
            items.Add(new MenuItem
            {
                Name = isActive ? $"• {retName}" : retName,
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, $"Retainer:{retId}", null, retName),
            });
            routeFilters.Add($"Retainer:{retId}");
        }

        inventoryHighlightController.SetActiveContextSubmenuRoutes("Store", routeFilters);
        clickedArgs.OpenSubmenu("Store", items);
    }

    private void OpenSellSubmenu(
        IMenuItemClickedArgs clickedArgs,
        uint baseItemId,
        string itemName,
        bool isManual,
        string currentRoute,
        bool canMarket,
        bool canVendor,
        bool canGc,
        bool canGoldSaucer)
    {
        var items = new List<IMenuItem>();
        var routeFilters = new List<string?>();

        if (canMarket)
        {
            var isMarket = isManual && string.Equals(currentRoute, "Market", StringComparison.OrdinalIgnoreCase);
            var prio = Safety.GetMarketPriority(baseItemId);

            items.Add(new MenuItem
            {
                Name = isMarket && prio > 0 ? "• Market (High Priority)" : "Market (High Priority)",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Market", 1, "Market (High Priority)"),
            });
            routeFilters.Add("Market");

            items.Add(new MenuItem
            {
                Name = isMarket && prio == 0 ? "• Market" : "Market",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Market", 0, "Market"),
            });
            routeFilters.Add("Market");

            items.Add(new MenuItem
            {
                Name = isMarket && prio < 0 ? "• Market (Low Priority)" : "Market (Low Priority)",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Market", -1, "Market (Low Priority)"),
            });
            routeFilters.Add("Market");
        }

        if (canVendor)
        {
            var isVendor = isManual && string.Equals(currentRoute, "Vendor", StringComparison.OrdinalIgnoreCase);
            items.Add(new MenuItem
            {
                Name = isVendor ? "• Vendor" : "Vendor",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Vendor", null, "Vendor"),
            });
            routeFilters.Add("Vendor");
        }

        if (canGc)
        {
            var isGc = isManual && string.Equals(currentRoute, "GrandCompany", StringComparison.OrdinalIgnoreCase);
            items.Add(new MenuItem
            {
                Name = isGc ? "• GC Seals" : "GC Seals",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "GrandCompany", null, "GC Seals"),
            });
            routeFilters.Add("GrandCompany");
        }

        if (canGoldSaucer)
        {
            var isGs = isManual && string.Equals(currentRoute, "GoldSaucer", StringComparison.OrdinalIgnoreCase);
            items.Add(new MenuItem
            {
                Name = isGs ? "• Gold Saucer" : "Gold Saucer",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "GoldSaucer", null, "Gold Saucer"),
            });
            routeFilters.Add("GoldSaucer");
        }

        inventoryHighlightController.SetActiveContextSubmenuRoutes("Sell", routeFilters);
        clickedArgs.OpenSubmenu("Sell", items);
    }

    private void OpenTrackSubmenu(IMenuItemClickedArgs clickedArgs, uint baseItemId, string itemName)
    {
        var items = new List<IMenuItem>();
        var isItemTracked = MaterialTracker.IsItemTracked(baseItemId);
        var hasTrackedList = MaterialTracker.HasTrackedItems;

        if (!isItemTracked)
        {
            items.Add(new MenuItem
            {
                Name = hasTrackedList ? "Add to List" : "Track Item",
                OnClicked = _ =>
                {
                    MaterialTracker.AddItemToList(baseItemId, out var msg);
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        chatGui.Print(msg);
                    }
                },
            });

            if (hasTrackedList)
            {
                items.Add(new MenuItem
                {
                    Name = "Track Only",
                    OnClicked = _ =>
                    {
                        MaterialTracker.TrackItemOnly(baseItemId, out var msg);
                        if (!string.IsNullOrWhiteSpace(msg))
                        {
                            chatGui.Print(msg);
                        }
                    },
                });
            }
        }
        else
        {
            items.Add(new MenuItem
            {
                Name = "Remove from List",
                OnClicked = _ =>
                {
                    MaterialTracker.RemoveItemFromList(baseItemId, out var msg);
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        chatGui.Print(msg);
                    }
                },
            });

            items.Add(new MenuItem
            {
                Name = "Add +1 to List",
                OnClicked = _ =>
                {
                    MaterialTracker.AddItemToList(baseItemId, out var msg);
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        chatGui.Print(msg);
                    }
                },
            });

            if (MaterialTracker.TrackedItemCount > 1)
            {
                items.Add(new MenuItem
                {
                    Name = "Track Only",
                    OnClicked = _ =>
                    {
                        MaterialTracker.TrackItemOnly(baseItemId, out var msg);
                        if (!string.IsNullOrWhiteSpace(msg))
                        {
                            chatGui.Print(msg);
                        }
                    },
                });
            }
        }

        if (MaterialTracker.HasRecipeForItem(baseItemId))
        {
            items.Add(new MenuItem
            {
                Name = hasTrackedList ? "Add Recipe to List" : "Track Recipe",
                OnClicked = _ =>
                {
                    MaterialTracker.AddRecipeToList(baseItemId, out var msg);
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        chatGui.Print(msg);
                    }
                },
            });

            if (hasTrackedList)
            {
                items.Add(new MenuItem
                {
                    Name = "Track Recipe Only",
                    OnClicked = _ =>
                    {
                        MaterialTracker.TrackRecipeForResultItem(baseItemId, out var msg);
                        if (!string.IsNullOrWhiteSpace(msg))
                        {
                            chatGui.Print(msg);
                        }
                    },
                });
            }
        }

        if (hasTrackedList)
        {
            items.Add(new MenuItem
            {
                Name = "Clear Tracked List",
                OnClicked = _ =>
                {
                    MaterialTracker.ClearAll();
                    chatGui.Print("Cleared tracked list.");
                },
            });
        }

        inventoryHighlightController.SetActiveContextSubmenuRoutes(null, null);
        clickedArgs.OpenSubmenu("Track", items);
    }

    private void ApplyContextOverride(uint baseItemId, string itemName, string routeSpec, int? marketPriority, string displayLabel)
    {
        MagicSort.SetItemOverride(baseItemId, routeSpec, marketPriority);
        chatGui.Print($"Set {itemName} -> {displayLabel}.");
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

        // Dispose native UI controllers BEFORE highlight/overlay/services so their DetachButton()
        // hover-clear callbacks never touch already-disposed services or re-tint cleared nodes.
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

        inventoryRouteOverlay.Dispose();
        inventoryHighlightController.Dispose();
        itemTooltipController.Dispose();
        MagicActions.Dispose();
        MarketBoardBuy.Dispose();
        BellSweep.Dispose();
        MagicSort.Dispose();
        TriageEngine.Dispose();
        Universalis.Dispose();
        TransactionQueue.Dispose();

        KamiToolKitLibrary.Dispose();
    }
}
