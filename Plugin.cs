using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit;
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
    private readonly ContextFlyoutWindow contextFlyoutWindow;
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
        contextFlyoutWindow = new ContextFlyoutWindow(
            gameGui,
            dataManager,
            chatGui,
            Holdings,
            Safety,
            TriageEngine,
            MagicSort,
            MaterialTracker,
            inventoryHighlightController);

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

    private void DrawUi()
    {
        windowSystem.Draw();
        contextFlyoutWindow.Draw();
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
        var fromArmoury = string.Equals(args.AddonName, "ArmouryBoard", StringComparison.Ordinal);

        if (args.Target is MenuTargetInventory invTarget && invTarget.TargetItem is { } invItem)
        {
            baseItemId = invItem.BaseItemId;
        }
        else if (args.Target is MenuTargetDefault)
        {
            // Support right-clicking items in Market Board, Crafting Log, chat links, Armoury Chest, etc. via AgentItemDetail
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
        contextFlyoutWindow.ArmForItem(capturedId, itemName, fromArmoury);

        args.AddMenuItem(new MenuItem
        {
            Name = "Magic Inventory",
            UseDefaultPrefix = true,
            IsSubmenu = true,
            OnClicked = _ => contextFlyoutWindow.PinOpenFromClick(),
        });
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
        contextFlyoutWindow.Dispose();
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
