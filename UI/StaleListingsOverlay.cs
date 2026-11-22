using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MagicInventory.Core;
using MagicInventory.Services.MagicSort;
using MagicInventory.Services.Triage;

namespace MagicInventory.UI;

public sealed unsafe class StaleListingsOverlay
{
    private static readonly Vector4 ColorBad = new(0.96f, 0.44f, 0.44f, 1.0f);
    private static readonly Vector4 ColorWarn = new(0.98f, 0.78f, 0.34f, 1.0f);
    private static readonly Vector4 ColorMuted = new(0.68f, 0.72f, 0.76f, 1.0f);

    private readonly IGameGui gameGui;
    private readonly Configuration configuration;
    private readonly ActiveListingAuditor auditor;
    private readonly List<ActiveListingAuditItem> unsellableBuffer = new(20);

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

        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerSell"))
        {
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

        var uiScale = ImGuiHelpers.GlobalScale;
        var vp = ImGui.GetMainViewport();
        var scale = sellListAddon->Scale > 0.01f ? sellListAddon->Scale : 1f;
        var rootW = sellListAddon->RootNode != null ? sellListAddon->RootNode->Width * scale : 420f * uiScale;
        var panelW = 370f * uiScale;
        var maxPanelH = 440f * uiScale;

        var rightX = vp.Pos.X + sellListAddon->X + rootW + 6f;
        var leftX = vp.Pos.X + sellListAddon->X - panelW - 6f;
        var x = (rightX + panelW <= vp.Pos.X + vp.Size.X - 8f)
            ? rightX
            : Math.Max(vp.Pos.X + 8f, leftX);
        var maxPanelY = Math.Max(vp.Pos.Y + 8f, vp.Pos.Y + vp.Size.Y - maxPanelH - 8f);
        var y = Math.Clamp(vp.Pos.Y + sellListAddon->Y, vp.Pos.Y + 8f, maxPanelY);

        ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(panelW, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(new Vector2(panelW, 0f), new Vector2(panelW, maxPanelH));
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

        unsellableBuffer.Clear();
        for (var i = 0; i < findings.Count; i++)
        {
            if (findings[i].Severity == StaleListingSeverity.UnrealisticRemove)
            {
                unsellableBuffer.Add(findings[i]);
            }
        }

        var actionsBlocked = auditor.IsBusy;
        var closeBtnWidth = ImGui.CalcTextSize("X").X + ImGui.GetStyle().FramePadding.X * 2f;

        ImGui.TextColored(unsellableBuffer.Count > 0 ? ColorBad : ColorWarn, $"Stale / Unrealistic Listings ({findings.Count})");
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX() + 4f, ImGui.GetWindowContentRegionMax().X - closeBtnWidth));
        if (ImGui.SmallButton("X##CloseStale"))
        {
            panelDismissedForRetainerId = activeRetainerId;
        }

        if (unsellableBuffer.Count > 1)
        {
            var shiftHeld = ImGui.GetIO().KeyShift;
            ImGui.BeginDisabled(actionsBlocked || !shiftHeld);
            var btnLabel = shiftHeld
                ? $"Remove All Unsellable ({unsellableBuffer.Count})"
                : $"Hold Shift to Remove All ({unsellableBuffer.Count})";
            if (ImGui.SmallButton(btnLabel))
            {
                auditor.QueueRemoveListings(unsellableBuffer.ToArray());
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted($"Hold Shift + Click to remove {unsellableBuffer.Count} unrealistic listing(s):");
                var previewCount = Math.Min(unsellableBuffer.Count, 12);
                for (var uIdx = 0; uIdx < previewCount; uIdx++)
                {
                    var u = unsellableBuffer[uIdx];
                    var uHq = u.IsHq ? " HQ" : string.Empty;
                    ImGui.TextUnformatted($"  - {u.Name}{uHq} ({u.Reason})");
                }

                if (unsellableBuffer.Count > previewCount)
                {
                    ImGui.TextUnformatted($"  ... and {unsellableBuffer.Count - previewCount} more");
                }

                ImGui.EndTooltip();
            }

            ImGui.EndDisabled();
        }

        ImGui.Separator();

        var lineH = ImGui.GetTextLineHeightWithSpacing();
        var estRowH = lineH * 5.2f + 12f * uiScale;
        var listHeight = Math.Min(330f * uiScale, Math.Max(estRowH, findings.Count * estRowH));
        if (ImGui.BeginChild("##MagicInventoryStaleList", new Vector2(0f, listHeight), false))
        {
            ImGui.PushTextWrapPos(0f);
            for (var i = 0; i < findings.Count; i++)
            {
                var item = findings[i];
                ImGui.PushID(item.MarketSlot * 1000 + i);

                var hqTag = item.IsHq ? " HQ" : string.Empty;
                var qtyTag = item.Quantity > 1 ? $" x{item.Quantity}" : string.Empty;
                ImGui.TextUnformatted($"{item.Name}{hqTag}{qtyTag}");

                var sevColor = item.Severity == StaleListingSeverity.UnrealisticRemove ? ColorBad : ColorWarn;
                var ageMins = (int)Math.Round(item.SnapshotAge.TotalMinutes);
                var snapTag = ageMins < 60 ? $"{ageMins}m" : $"{ageMins / 60}h";
                ImGui.TextColored(sevColor, $"  {item.Reason} (Universalis {snapTag} ago)");

                var mbStr = item.Viability.ServerCheapest > 0 ? $"{item.Viability.ServerCheapest:N0}g" : "none";
                var medStr = item.Viability.SoldMedian > 0 ? $"{item.Viability.SoldMedian:N0}g" : "n/a";
                var lastStr = MarketViabilityEvaluator.FormatAgeShort(item.Viability.DaysSinceLastSale);
                ImGui.TextColored(
                    ColorMuted,
                    $"  Listed: {item.ListedUnitPrice:N0}g | Est. MB: {mbStr} | Med: {medStr}");
                ImGui.TextColored(
                    ColorMuted,
                    $"  Freq: {item.Viability.EffectiveVelocity:0.00}/day | Last sold: {lastStr}");

                ImGui.BeginDisabled(actionsBlocked);
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
                if (ImGui.SmallButton("Dismiss"))
                {
                    auditor.DismissForSession(item);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Hide this listing for the current session (does not change permanent item rules).");
                }

                if (i + 1 < findings.Count)
                {
                    ImGui.Separator();
                }

                ImGui.PopID();
            }

            ImGui.PopTextWrapPos();
        }

        ImGui.EndChild();
        ImGui.End();
    }
}
