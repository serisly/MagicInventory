using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MagicInventory.Core;
using MagicInventory.Services.MagicSort;
using MagicInventory.Services.Triage;

namespace MagicInventory.UI;

/// <summary>
/// Context-only docked prompt panel beside RetainerSellList when stale, illiquid,
/// below-vendor, or heavily overpriced active listings are detected.
/// Completely hidden during normal gameplay and whenever all listings look healthy.
/// </summary>
public sealed unsafe class StaleListingsOverlay
{
    private static readonly Vector4 ColorBad = new(0.96f, 0.44f, 0.44f, 1.0f);
    private static readonly Vector4 ColorWarn = new(0.98f, 0.78f, 0.34f, 1.0f);
    private static readonly Vector4 ColorMuted = new(0.68f, 0.72f, 0.76f, 1.0f);

    private readonly IGameGui gameGui;
    private readonly Configuration configuration;
    private readonly ActiveListingAuditor auditor;

    private ulong panelDismissedForRetainerId;

    public StaleListingsOverlay(
        IGameGui gameGui,
        Configuration configuration,
        ActiveListingAuditor auditor)
    {
        this.gameGui = gameGui;
        this.configuration = configuration;
        this.auditor = auditor;
    }

    public void Draw()
    {
        if (!configuration.MarketPromptStaleListings)
        {
            return;
        }

        var sellListAddon = NativeUiHelpers.GetReadyAddon(gameGui, "RetainerSellList");
        if (sellListAddon == null)
        {
            panelDismissedForRetainerId = 0;
            return;
        }

        var findings = auditor.GetCurrentRetainerFindings();
        if (findings.Count == 0)
        {
            return;
        }

        var activeRetainerId = findings[0].RetainerId;
        if (panelDismissedForRetainerId == activeRetainerId)
        {
            return;
        }

        var vp = ImGui.GetMainViewport();
        var scale = sellListAddon->Scale > 0.01f ? sellListAddon->Scale : 1f;
        var rootW = sellListAddon->RootNode != null ? sellListAddon->RootNode->Width * scale : 420f;
        const float panelW = 360f;

        var rightX = vp.Pos.X + sellListAddon->X + rootW + 6f;
        var leftX = vp.Pos.X + sellListAddon->X - panelW - 6f;
        var x = (rightX + panelW <= vp.Pos.X + vp.Size.X - 8f)
            ? rightX
            : Math.Max(vp.Pos.X + 8f, leftX);
        var y = Math.Clamp(vp.Pos.Y + sellListAddon->Y, vp.Pos.Y + 8f, vp.Pos.Y + vp.Size.Y - 220f);

        ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(panelW, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(new Vector2(panelW, 0f), new Vector2(panelW, 420f));
        ImGui.SetNextWindowBgAlpha(0.94f);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.AlwaysAutoResize;

        if (!ImGui.Begin("##MagicInventoryStaleListings", flags))
        {
            ImGui.End();
            return;
        }

        var unsellable = findings.Where(f => f.Severity == StaleListingSeverity.UnrealisticRemove).ToList();
        var busy = auditor.IsBusy;

        ImGui.TextColored(unsellable.Count > 0 ? ColorBad : ColorWarn, $"Stale / Unrealistic Listings ({findings.Count})");
        ImGui.SameLine(panelW - 32f);
        if (ImGui.SmallButton("X##CloseStale"))
        {
            panelDismissedForRetainerId = activeRetainerId;
        }

        if (unsellable.Count > 1)
        {
            ImGui.BeginDisabled(busy);
            if (ImGui.SmallButton($"Remove All Unsellable ({unsellable.Count})"))
            {
                auditor.QueueRemoveListings(unsellable);
            }

            ImGui.EndDisabled();
        }

        ImGui.Separator();

        var maxShown = Math.Min(findings.Count, 8);
        for (var i = 0; i < maxShown; i++)
        {
            var item = findings[i];
            ImGui.PushID(item.MarketSlot * 1000 + i);

            var hqTag = item.IsHq ? " HQ" : string.Empty;
            var qtyTag = item.Quantity > 1 ? $" x{item.Quantity}" : string.Empty;
            ImGui.TextUnformatted($"{item.Name}{hqTag}{qtyTag}");

            var sevColor = item.Severity == StaleListingSeverity.UnrealisticRemove ? ColorBad : ColorWarn;
            ImGui.TextColored(sevColor, $"  {item.Reason}");

            var mbStr = item.Viability.ServerCheapest > 0 ? $"{item.Viability.ServerCheapest:N0}g" : "none";
            var lastStr = MarketViabilityEvaluator.FormatAgeShort(item.Viability.DaysSinceLastSale);
            ImGui.TextColored(
                ColorMuted,
                $"  Listed: {item.ListedUnitPrice:N0}g | MB: {mbStr} | {item.Viability.EffectiveVelocity:0.00}/day ({lastStr})");

            ImGui.BeginDisabled(busy);
            if (ImGui.SmallButton("Remove"))
            {
                auditor.QueueRemoveListings([item]);
            }

            if (item.Severity == StaleListingSeverity.OverpricedReprice && item.Viability.SuggestedUndercutPrice >= 2)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Reprice (~{item.Viability.SuggestedUndercutPrice:N0}g)"))
                {
                    auditor.QueueRepriceReview(item);
                }
            }

            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.SmallButton("Keep"))
            {
                auditor.DismissForSession(item);
            }

            if (i + 1 < maxShown)
            {
                ImGui.Separator();
            }

            ImGui.PopID();
        }

        if (findings.Count > maxShown)
        {
            ImGui.TextColored(ColorMuted, $"... and {findings.Count - maxShown} more flagged listing(s).");
        }

        ImGui.End();
    }
}
