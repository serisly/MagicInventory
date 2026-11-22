using System;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
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
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    private readonly WindowSystem windowSystem = new("MagicInventory");
    private readonly SettingsWindow settingsWindow;
    private readonly WorldHopPromptWindow worldHopPromptWindow;
    private readonly ItemTooltipController itemTooltipController;
    private readonly InventoryRouteOverlay inventoryRouteOverlay;
    private readonly ItemContextMenuController itemContextMenuController;
    private readonly ActiveListingAuditor activeListingAuditor;
    private readonly RetainerSellPriceController retainerSellPriceController;
    private readonly StaleListingsOverlay staleListingsOverlay;
    private readonly object nativeUiGate = new();
    private RecipeNoteController? recipeNoteController;
    private RetainerController? retainerController;
    private MarketBoardController? marketBoardController;
    private MagicSortControllers? magicSortControllers;
    private MaterialTrackerOverlay? trackerOverlay;
    private bool kamiInitialized;
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
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.log = log;

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Holdings = new HoldingsService(pluginInterface, dataManager, log);
        TransactionQueue = new SafeTransactionQueue(framework, clientState, objectTable, condition, gameGui, chatGui, log);
        MaterialTracker = new MaterialTrackerService(
            pluginInterface,
            framework,
            Configuration,
            Holdings,
            dataManager,
            gameGui,
            condition,
            chatGui);
        Universalis = new UniversalisClient(pluginInterface, log);
        Safety = new InventorySafetyEvaluator(dataManager, Configuration, MaterialTracker.IsItemTracked);
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
        worldHopPromptWindow = new WorldHopPromptWindow(framework, commandManager, chatGui, gameGui);
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
            Holdings,
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
        MaterialTracker.TrackerChanged += OnTrackerChanged;
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
            MarketBoardBuy,
            MaterialTracker);

        itemTooltipController = new ItemTooltipController(gameInteropProvider, gameGui, log, MagicSort);
        itemTooltipController.Enable();

        inventoryRouteOverlay = new InventoryRouteOverlay(gameGui, Configuration, Safety, MagicSort);
        itemContextMenuController = new ItemContextMenuController(
            contextMenu,
            gameGui,
            dataManager,
            chatGui,
            Holdings,
            Safety,
            TriageEngine,
            MagicSort,
            MaterialTracker,
            inventoryRouteOverlay);
        activeListingAuditor = new ActiveListingAuditor(
            dataManager,
            gameGui,
            chatGui,
            Configuration,
            Universalis,
            TriageEngine,
            TransactionQueue);
        retainerSellPriceController = new RetainerSellPriceController(
            dataManager,
            gameGui,
            Configuration,
            Holdings,
            Universalis,
            TriageEngine,
            TransactionQueue);
        staleListingsOverlay = new StaleListingsOverlay(gameGui, Configuration, activeListingAuditor);

        settingsWindow = new SettingsWindow(
            pluginInterface,
            dataManager,
            Configuration,
            Holdings,
            Universalis,
            TriageEngine,
            MagicSort,
            MaterialTracker,
            ResetTrackerPosition);
        windowSystem.AddWindow(settingsWindow);
        windowSystem.AddWindow(worldHopPromptWindow);

        this.pluginInterface.UiBuilder.Draw += DrawUi;
        this.pluginInterface.UiBuilder.OpenConfigUi += ToggleSettingsWindow;
        this.pluginInterface.UiBuilder.OpenMainUi += ToggleSettingsWindow;

        this.commandManager.AddHandler(MagicCommandName, new CommandInfo(OnMagicCommand)
        {
            HelpMessage = "Magic Inventory: /magic (settings) | sort | sell | buy | track <recipe> [qty] | use | stop | scan | status",
        });
        this.commandManager.AddHandler(MagicShortCommandName, new CommandInfo(OnMagicCommand)
        {
            HelpMessage = "Shortcut for /magic: /m | sort | sell | buy | track <recipe> [qty] | use | stop | scan | status",
        });

        _ = InitializeNativeUiAsync(pluginInterface, framework, clientState, condition, gameGui);
    }

    private async Task InitializeNativeUiAsync(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IGameGui gameGui)
    {
        try
        {
            if (isDisposed)
            {
                return;
            }

            await KamiToolKitLibrary.InitializeAsync(pluginInterface, "Magic Inventory").ConfigureAwait(false);

            var disposeKamiImmediately = false;
            lock (nativeUiGate)
            {
                if (isDisposed)
                {
                    disposeKamiImmediately = true;
                }
                else
                {
                    kamiInitialized = true;
                }
            }

            if (disposeKamiImmediately)
            {
                try
                {
                    await framework.RunOnFrameworkThread(KamiToolKitLibrary.Dispose).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "Cleanup warning during deferred KamiToolKitLibrary disposal.");
                }

                return;
            }

            await framework.RunOnFrameworkThread(() =>
            {
                lock (nativeUiGate)
                {
                    if (isDisposed)
                    {
                        return;
                    }

                    recipeNoteController = new RecipeNoteController(gameGui, MaterialTracker);
                    recipeNoteController.Enable();

                    retainerController = new RetainerController(MagicSort, inventoryRouteOverlay);
                    retainerController.Enable();

                    marketBoardController = new MarketBoardController(MarketBoardBuy);
                    marketBoardController.Enable();

                    magicSortControllers = new MagicSortControllers(MagicSort, BellSweep, inventoryRouteOverlay);
                    magicSortControllers.Enable();

                    trackerOverlay = new MaterialTrackerOverlay(
                        MaterialTracker,
                        Configuration,
                        pluginInterface,
                        clientState,
                        condition,
                        chatGui);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to initialize Magic Inventory native UI controllers.");
        }
    }

    private void OnTrackerChanged()
    {
        MagicSort.InvalidateButtonCache();
        BellSweep.InvalidateButtonCaches();
    }

    private void ResetTrackerPosition()
    {
        MaterialTrackerOverlay? overlay;
        lock (nativeUiGate)
        {
            overlay = trackerOverlay;
        }

        overlay?.ResetPosition();
    }

    private void DrawUi()
    {
        windowSystem.Draw();
        inventoryRouteOverlay.Draw();
        retainerSellPriceController.Draw();
        staleListingsOverlay.Draw();
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
                MagicActions.Execute(MagicActionIntent.Use);
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
                MagicActions.Execute(MagicActionIntent.Stop);
                break;

            default:
                chatGui.Print("Usage: /m [sort | sell | buy | track <recipe> [qty] | use | stop | scan | status]");
                break;
        }
    }

    public void Dispose()
    {
        MaterialTrackerOverlay? localTracker;
        MagicSortControllers? localMagicSort;
        MarketBoardController? localMarketBoard;
        RetainerController? localRetainer;
        RecipeNoteController? localRecipeNote;
        bool shouldDisposeKami;

        lock (nativeUiGate)
        {
            isDisposed = true;
            localTracker = trackerOverlay;
            trackerOverlay = null;
            localMagicSort = magicSortControllers;
            magicSortControllers = null;
            localMarketBoard = marketBoardController;
            marketBoardController = null;
            localRetainer = retainerController;
            retainerController = null;
            localRecipeNote = recipeNoteController;
            recipeNoteController = null;
            shouldDisposeKami = kamiInitialized;
            kamiInitialized = false;
        }

        MaterialTracker.TrackerChanged -= OnTrackerChanged;
        pluginInterface.UiBuilder.Draw -= DrawUi;
        pluginInterface.UiBuilder.OpenConfigUi -= ToggleSettingsWindow;
        pluginInterface.UiBuilder.OpenMainUi -= ToggleSettingsWindow;
        windowSystem.RemoveAllWindows();
        commandManager.RemoveHandler(MagicCommandName);
        commandManager.RemoveHandler(MagicShortCommandName);
        itemContextMenuController.Dispose();

        DisposeQuietly(localTracker, nameof(MaterialTrackerOverlay));
        DisposeQuietly(localMagicSort, nameof(MagicSortControllers));
        DisposeQuietly(localMarketBoard, nameof(MarketBoardController));
        DisposeQuietly(localRetainer, nameof(RetainerController));
        DisposeQuietly(localRecipeNote, nameof(RecipeNoteController));

        inventoryRouteOverlay.Dispose();
        itemTooltipController.Dispose();
        worldHopPromptWindow.Dispose();
        MagicActions.Dispose();
        MarketBoardBuy.Dispose();
        BellSweep.Dispose();
        MagicSort.Dispose();
        TriageEngine.Dispose();
        MaterialTracker.Dispose();
        Holdings.Dispose();
        Universalis.Dispose();
        TransactionQueue.Dispose();

        if (shouldDisposeKami)
        {
            try
            {
                KamiToolKitLibrary.Dispose();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Cleanup warning disposing KamiToolKitLibrary.");
            }
        }
    }

    private void DisposeQuietly(IDisposable? disposable, string name)
    {
        if (disposable == null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Cleanup warning disposing {ControllerName}.", name);
        }
    }
}
