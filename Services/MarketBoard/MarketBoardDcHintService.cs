using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using MagicInventory.Services.Triage;

namespace MagicInventory.Services.MarketBoard;

internal sealed class MarketBoardDcHintService : IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly IChatGui chatGui;
    private readonly Configuration configuration;
    private readonly UniversalisClient universalis;
    private readonly Action<string, string, string, DcCheapestListing, int>? onWorldHopOffer;

    private readonly Dictionary<uint, DateTime> lastDcHintUtcByItem = new();
    private DateTime lastLifestreamCheckUtc = DateTime.MinValue;
    private bool cachedLifestreamLoaded;
    private bool isDisposed;

    public MarketBoardDcHintService(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IObjectTable objectTable,
        IChatGui chatGui,
        Configuration configuration,
        UniversalisClient universalis,
        Action<string, string, string, DcCheapestListing, int>? onWorldHopOffer)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.objectTable = objectTable;
        this.chatGui = chatGui;
        this.configuration = configuration;
        this.universalis = universalis;
        this.onWorldHopOffer = onWorldHopOffer;
    }

    public void TriggerHintAsync(uint baseItemId, string itemName, int missingNeeded, long maxGilPerPurchase)
    {
        var now = DateTime.UtcNow;
        if (lastDcHintUtcByItem.TryGetValue(baseItemId, out var lastHint)
            && (now - lastHint).TotalSeconds < 30.0)
        {
            return;
        }

        var player = objectTable.LocalPlayer;
        var worldRow = player?.CurrentWorld.ValueNullable;
        var currentWorld = worldRow?.Name.ExtractText() ?? string.Empty;
        var dcName = worldRow?.DataCenter.ValueNullable?.Name.ExtractText() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dcName) || string.IsNullOrWhiteSpace(currentWorld))
        {
            return;
        }

        var currentGil = GetCurrentGil();
        long currentWorldUnit = 0;
        if (universalis.TryGetAnyCachedPrice(currentWorld, baseItemId, out var localSnap))
        {
            currentWorldUnit = localSnap.MinPriceNq > 0 ? localSnap.MinPriceNq : localSnap.MinPriceAny;
        }

        lastDcHintUtcByItem[baseItemId] = now;
        var promptTravel = configuration.EnableLifestreamWorldHopPrompt && IsLifestreamLoaded();
        var clampedNeeded = Math.Max(1, missingNeeded);
        var minSavingsGil = Math.Max(0, configuration.WorldHopMinSavingsGil);

        _ = Task.Run(async () =>
        {
            var best = await universalis.FetchDataCenterCheapestAsync(dcName, baseItemId, excludeWorld: currentWorld).ConfigureAwait(false);
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

                if (best == null)
                {
                    chatGui.Print($"Magic Inventory: No other listings for {itemName} found across {dcName}.");
                    return;
                }

                var hqTag = best.IsHq ? " HQ" : string.Empty;
                var subtotal = (long)best.UnitPrice * Math.Max(1, best.Quantity);
                var offerTotalWithTax = subtotal + (subtotal * 5L / 100L);
                chatGui.Print(
                    $"Magic Inventory ({dcName} hint): {itemName} cheapest on {best.WorldName} at {best.UnitPrice:N0}g ea x{best.Quantity}{hqTag} (~{offerTotalWithTax:N0}g incl. tax).");

                if (promptTravel && onWorldHopOffer != null)
                {
                    var affordable = offerTotalWithTax <= maxGilPerPurchase && offerTotalWithTax <= currentGil;
                    var savingsOk = currentWorldUnit <= 0
                        || (currentWorldUnit - best.UnitPrice) * clampedNeeded >= minSavingsGil;
                    if (affordable && savingsOk)
                    {
                        onWorldHopOffer(itemName, currentWorld, dcName, best, clampedNeeded);
                    }
                }
            }).ConfigureAwait(false);
        });
    }

    private static unsafe long GetCurrentGil()
    {
        var inv = InventoryManager.Instance();
        return inv != null ? (long)inv->GetGil() : 0L;
    }

    private bool IsLifestreamLoaded()
    {
        var now = DateTime.UtcNow;
        if ((now - lastLifestreamCheckUtc).TotalSeconds < 15.0)
        {
            return cachedLifestreamLoaded;
        }

        lastLifestreamCheckUtc = now;
        cachedLifestreamLoaded = false;
        try
        {
            foreach (var p in pluginInterface.InstalledPlugins)
            {
                if (p.IsLoaded && string.Equals(p.InternalName, "Lifestream", StringComparison.OrdinalIgnoreCase))
                {
                    cachedLifestreamLoaded = true;
                    break;
                }
            }
        }
        catch
        {
        }

        return cachedLifestreamLoaded;
    }

    public void Dispose()
    {
        isDisposed = true;
        lastDcHintUtcByItem.Clear();
    }
}
