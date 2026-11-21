using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using MagicInventory.Core;
using MagicInventory.Services.Triage;

namespace MagicInventory.UI;

/// <summary>
/// Optional on-demand confirmation modal (only opened when EnableLifestreamWorldHopPrompt is ON
/// and Lifestream is installed) offering a 1-click user-confirmed world hop when a tracked item
/// has no safe listings on the current world. Never auto-travels without explicit Yes click.
/// </summary>
public sealed unsafe class WorldHopPromptWindow : Window
{
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IGameGui gameGui;

    private string itemName = string.Empty;
    private string currentWorld = string.Empty;
    private string dataCenterName = string.Empty;
    private DcCheapestListing? pendingOffer;

    public WorldHopPromptWindow(ICommandManager commandManager, IChatGui chatGui, IGameGui gameGui)
        : base("Magic Inventory - World Travel###MagicInventoryWorldHop", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.gameGui = gameGui;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 120),
            MaximumSize = new Vector2(540, 260),
        };
    }

    public void ShowOffer(string itemName, string currentWorld, string dataCenterName, DcCheapestListing offer)
    {
        // Do not overwrite an offer the user is currently inspecting
        if (IsOpen && pendingOffer != null)
        {
            return;
        }

        this.itemName = itemName;
        this.currentWorld = currentWorld;
        this.dataCenterName = dataCenterName;
        pendingOffer = offer;
        IsOpen = true;
    }

    public override void Draw()
    {
        var offer = pendingOffer;
        if (offer == null)
        {
            IsOpen = false;
            return;
        }

        ImGui.TextWrapped($"No safe listings for {itemName} on {currentWorld}.");
        ImGui.Spacing();
        var hqTag = offer.IsHq ? " HQ" : string.Empty;
        var totalCost = (long)offer.UnitPrice * Math.Max(1, offer.Quantity);
        ImGui.TextColored(
            new Vector4(0.60f, 0.90f, 0.65f, 1.0f),
            $"Cheapest on {dataCenterName}: {offer.WorldName} at {offer.UnitPrice:N0}g ea x{offer.Quantity}{hqTag} ({totalCost:N0}g total)");
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
                // Close Market Board windows cleanly before handing off to Lifestream
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

                if (commandManager.ProcessCommand($"/li {world}"))
                {
                    chatGui.Print($"Magic Inventory: requesting Lifestream travel to {world}...");
                }
                else
                {
                    chatGui.PrintError($"Magic Inventory: Lifestream did not accept '/li {world}'. Travel manually or check Lifestream.");
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Stay Here"))
        {
            IsOpen = false;
            pendingOffer = null;
        }
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
