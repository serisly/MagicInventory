using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using MagicInventory.Core;
using MagicInventory.Services.Triage;

namespace MagicInventory.UI;

public sealed unsafe class WorldHopPromptWindow : Window, IDisposable
{
    private readonly IFramework framework;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IGameGui gameGui;

    private string itemName = string.Empty;
    private string currentWorld = string.Empty;
    private string dataCenterName = string.Empty;
    private int neededQuantity = 1;
    private DcCheapestListing? pendingOffer;

    private string? pendingTravelWorld;
    private DateTime pendingTravelRequestedUtc = DateTime.MinValue;

    public WorldHopPromptWindow(
        IFramework framework,
        ICommandManager commandManager,
        IChatGui chatGui,
        IGameGui gameGui)
        : base("Magic Inventory - World Travel###MagicInventoryWorldHop", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.framework = framework;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.gameGui = gameGui;
        this.framework.Update += OnFrameworkUpdate;
    }

    public void ShowOffer(string itemName, string currentWorld, string dataCenterName, DcCheapestListing offer, int neededQuantity = 1)
    {
        if (IsOpen && pendingOffer != null)
        {
            return;
        }

        this.itemName = itemName;
        this.currentWorld = currentWorld;
        this.dataCenterName = dataCenterName;
        this.neededQuantity = Math.Max(1, neededQuantity);
        pendingOffer = offer;
        IsOpen = true;
    }

    public override void PreDraw()
    {
        var s = ImGuiHelpers.GlobalScale;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360f * s, 120f * s),
            MaximumSize = new Vector2(560f * s, 300f * s),
        };
    }

    public override void Draw()
    {
        var offer = pendingOffer;
        if (offer == null)
        {
            IsOpen = false;
            return;
        }

        ImGui.TextWrapped($"No affordable local listing for {itemName} (need {neededQuantity}) on {currentWorld}.");
        ImGui.Spacing();
        var hqTag = offer.IsHq ? " HQ" : string.Empty;
        var subtotal = (long)offer.UnitPrice * Math.Max(1, offer.Quantity);
        var totalWithTax = subtotal + (subtotal * 5L / 100L);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.60f, 0.90f, 0.65f, 1.0f));
        ImGui.TextWrapped(
            $"Cheapest on {dataCenterName}: {offer.WorldName} at {offer.UnitPrice:N0}g ea x{offer.Quantity}{hqTag} (~{totalWithTax:N0}g incl. 5% tax)");
        ImGui.PopStyleColor();
        ImGui.Spacing();
        ImGui.TextDisabled("Only travels if you click Travel below.");
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button($"Travel to {offer.WorldName} (/li)"))
        {
            var world = SanitizeWorldName(offer.WorldName);
            IsOpen = false;
            pendingOffer = null;
            if (!string.IsNullOrEmpty(world))
            {
                if (!commandManager.Commands.ContainsKey("/li"))
                {
                    chatGui.PrintError($"Magic Inventory: Lifestream '/li' command is unavailable. Travel to {world} manually.");
                    return;
                }

                var resultAddon = NativeUiHelpers.GetReadyAddon(gameGui, "ItemSearchResult");
                if (resultAddon != null)
                {
                    resultAddon->Close(true);
                }

                var searchAddon = NativeUiHelpers.GetReadyAddon(gameGui, "ItemSearch");
                if (searchAddon != null)
                {
                    searchAddon->Close(true);
                }

                pendingTravelWorld = world;
                pendingTravelRequestedUtc = DateTime.UtcNow;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Stay Here"))
        {
            IsOpen = false;
            pendingOffer = null;
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (string.IsNullOrEmpty(pendingTravelWorld))
        {
            return;
        }

        var resultOpen = NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearchResult");
        var searchOpen = NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearch");
        var timedOut = (DateTime.UtcNow - pendingTravelRequestedUtc).TotalMilliseconds >= 1500.0;
        if ((resultOpen || searchOpen) && !timedOut)
        {
            return;
        }

        var world = pendingTravelWorld;
        pendingTravelWorld = null;

        if (commandManager.ProcessCommand($"/li {world}"))
        {
            chatGui.Print($"Magic Inventory: requesting Lifestream travel to {world}...");
        }
        else
        {
            chatGui.PrintError($"Magic Inventory: Lifestream did not accept '/li {world}'. Travel manually or check Lifestream.");
        }
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }

    private static string SanitizeWorldName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.Trim();
        foreach (var ch in trimmed)
        {
            if (!char.IsLetter(ch))
            {
                return string.Empty;
            }
        }

        return trimmed;
    }
}
