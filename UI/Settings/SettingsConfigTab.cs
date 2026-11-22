using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace MagicInventory.UI.Settings;

internal sealed class SettingsConfigTab
{
    private readonly SettingsUiContext ctx;

    private DateTime lastPluginCheckUtc = DateTime.MinValue;
    private bool cachedHasPennyPincher;
    private bool cachedHasLifestream;

    public SettingsConfigTab(SettingsUiContext ctx)
    {
        this.ctx = ctx;
    }

    public void Draw(ulong cid)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var inputWidth = 165f * scale;
        var indentWidth = 18f * scale;
        var triageChanged = false;
        var uiOnlyChanged = false;

        RefreshInstalledPluginsCache();

        ImGui.TextUnformatted("Sell Mode");
        ImGui.TextColored(SettingsUiContext.ColorMuted, "Controls how aggressively /m sell tags items for Vendors, Grand Company, and the Market Board.");
        ImGui.TextColored(SettingsUiContext.ColorMuted, "Note: switching Sell Mode resets the Advanced price thresholds below to that preset.");
        ImGui.Spacing();

        var mode = ctx.Config.SellMode;
        if (ImGui.RadioButton("Safe: junk and clear Market wins only", mode == SellMode.Safe))
        {
            ctx.Config.ApplySellModePresets(SellMode.Safe);
            triageChanged = true;
        }

        ImGui.TextColored(SettingsUiContext.ColorMuted, "    Sells Allagan pieces, old white gear, cheap city-vendor clutter, green dungeon gear, and high-value Market items.");

        if (ImGui.RadioButton("Balanced: clear low-value clutter and good Market flips", mode == SellMode.Balanced))
        {
            ctx.Config.ApplySellModePresets(SellMode.Balanced);
            triageChanged = true;
        }

        ImGui.TextColored(SettingsUiContext.ColorMuted, "    Also turns in outgeared blue gear and sells cheap common drops while keeping crafting mats and rare items.");

        if (ImGui.RadioButton("Clear Space: sell cheap items you can easily re-buy", mode == SellMode.Purge))
        {
            ctx.Config.ApplySellModePresets(SellMode.Purge);
            triageChanged = true;
        }

        ImGui.TextColored(SettingsUiContext.ColorMuted, "    Liquidates cheap city-vendor mats and abundant low-price Market items to free bag & retainer slots.");
        ImGui.Spacing();
        ImGui.TextColored(SettingsUiContext.ColorGood, "Always kept safe in every mode: tracked mats, gearsets, food, potions, glamour, housing, relics, and rare (0-listed) items.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Market Board");
        ImGui.SetNextItemWidth(inputWidth);
        var undercut = ctx.Config.MarketUndercutGil;
        if (ImGui.InputInt("Undercut by (Gil)", ref undercut, 1, 10))
        {
            ctx.Config.MarketUndercutGil = Math.Clamp(undercut, 0, 10_000);
            triageChanged = true;
        }

        ImGui.SetNextItemWidth(inputWidth);
        var maxBuy = ctx.Config.BuyMaxGilPerPurchase;
        if (ImGui.InputInt("Max Gil per Magic Buy purchase", ref maxBuy, 5_000, 25_000))
        {
            ctx.Config.BuyMaxGilPerPurchase = Math.Clamp(maxBuy, 1_000, 10_000_000);
            uiOnlyChanged = true;
        }

        ImGui.SetNextItemWidth(inputWidth);
        var outlier = ctx.Config.BuyOutlierMultiplier;
        if (ImGui.InputFloat("Skip listings above (x recent price)", ref outlier, 0.1f, 0.5f, "%.1f"))
        {
            ctx.Config.BuyOutlierMultiplier = Math.Clamp(outlier, 1.2f, 10f);
            uiOnlyChanged = true;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Market Listing Workflow");
        var workflowMode = ctx.Config.MarketReviewBeforeConfirm
            ? 0
            : (ctx.Config.MarketOneListingPerClick ? 1 : 2);

        if (ImGui.RadioButton("Review before confirming (Recommended)", workflowMode == 0))
        {
            ctx.Config.MarketReviewBeforeConfirm = true;
            uiOnlyChanged = true;
        }

        ImGui.TextColored(SettingsUiContext.ColorMuted, "    Prep Sell opens 1 item and fills the undercut price; you inspect and click Confirm yourself.");

        if (workflowMode == 0)
        {
            ImGui.Indent(indentWidth);
            var autoCompareInReview = ctx.Config.MarketAutoComparePricesInReview;
            if (ImGui.Checkbox("Auto-open Compare Prices in Review Mode", ref autoCompareInReview))
            {
                ctx.Config.MarketAutoComparePricesInReview = autoCompareInReview;
                uiOnlyChanged = true;
            }

            ImGui.Unindent(indentWidth);
        }

        if (ImGui.RadioButton("Auto-list 1 item per click", workflowMode == 1))
        {
            ctx.Config.MarketReviewBeforeConfirm = false;
            ctx.Config.MarketOneListingPerClick = true;
            uiOnlyChanged = true;
        }

        if (ImGui.RadioButton("Auto-list batch (up to 8 per click)", workflowMode == 2))
        {
            ctx.Config.MarketReviewBeforeConfirm = false;
            ctx.Config.MarketOneListingPerClick = false;
            uiOnlyChanged = true;
        }

        ImGui.Spacing();
        var autoFillSell = ctx.Config.MarketAutoFillRetainerSell;
        if (ImGui.Checkbox("Auto-fill undercut price when opening Retainer Sell / Compare Prices manually", ref autoFillSell))
        {
            ctx.Config.MarketAutoFillRetainerSell = autoFillSell;
            uiOnlyChanged = true;
        }

        ImGui.TextColored(SettingsUiContext.ColorMuted, "    Sets the asking price from live Compare Prices (excluding your own retainers) or world history.");

        if (cachedHasPennyPincher && ctx.Config.MarketAutoFillRetainerSell)
        {
            ImGui.TextColored(SettingsUiContext.ColorGold, "    PennyPincher is also loaded. Turn off auto-fill in one of them so they do not fight over prices.");
        }

        var promptStale = ctx.Config.MarketPromptStaleListings;
        if (ImGui.Checkbox("Prompt to remove or reprice stale / unrealistic active listings", ref promptStale))
        {
            ctx.Config.MarketPromptStaleListings = promptStale;
            uiOnlyChanged = true;
        }

        ImGui.TextColored(SettingsUiContext.ColorMuted, "    Flags dead, below-vendor, or heavily overpriced listings in Retainer Sell List.");

        ImGui.SetNextItemWidth(inputWidth);
        var dailyLimit = ctx.Config.MarketDailyActionLimit;
        if (ImGui.InputInt("Daily auto-list / buy limit (0 = Unlimited)", ref dailyLimit, 5, 10))
        {
            ctx.Config.MarketDailyActionLimit = Math.Clamp(dailyLimit, 0, 200);
            uiOnlyChanged = true;
        }

        var usedToday = ctx.Config.GetUsedMarketActionsToday(cid);
        var dailyLimitNote = ctx.Config.MarketDailyActionLimit <= 0
            ? $"    Used today (this character): {usedToday} (Unlimited). Review Mode never consumes daily quota."
            : $"    Used today (this character): {usedToday}/{ctx.Config.MarketDailyActionLimit}. Review Mode never consumes daily quota.";
        ImGui.TextColored(SettingsUiContext.ColorMuted, dailyLimitNote);

        var lifestreamOptIn = ctx.Config.EnableLifestreamWorldHopPrompt;
        if (ImGui.Checkbox("Ask to travel via Lifestream if an item isn't listed on my Current World", ref lifestreamOptIn))
        {
            ctx.Config.EnableLifestreamWorldHopPrompt = lifestreamOptIn;
            uiOnlyChanged = true;
        }

        ImGui.TextColored(
            cachedHasLifestream ? SettingsUiContext.ColorGood : SettingsUiContext.ColorMuted,
            cachedHasLifestream
                ? "    Lifestream installed (only travels after you click Travel)."
                : "    Lifestream not installed (shows cheapest DC world in chat instead).");

        ImGui.BeginDisabled(!ctx.Config.EnableLifestreamWorldHopPrompt);
        ImGui.Indent(indentWidth);
        ImGui.SetNextItemWidth(inputWidth);
        var minWorldHopSavings = ctx.Config.WorldHopMinSavingsGil;
        if (ImGui.InputInt("Min Gil savings to suggest world travel", ref minWorldHopSavings, 1_000, 5_000))
        {
            ctx.Config.WorldHopMinSavingsGil = Math.Clamp(minWorldHopSavings, 0, 1_000_000);
            uiOnlyChanged = true;
        }

        ImGui.Unindent(indentWidth);
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Display");
        var hoverHighlights = ctx.Config.EnableHoverHighlights;
        if (ImGui.Checkbox("Highlight matching items when hovering buttons or right-click options", ref hoverHighlights))
        {
            ctx.Config.EnableHoverHighlights = hoverHighlights;
            uiOnlyChanged = true;
        }

        if (ImGui.Button("Reset Tracker Position"))
        {
            ctx.Config.TrackerX = 24f;
            ctx.Config.TrackerY = 120f;
            ctx.OnResetTrackerPosition?.Invoke();
            uiOnlyChanged = true;
        }

        ImGui.SameLine();
        ImGui.TextColored(SettingsUiContext.ColorMuted, "Moves the Material Tracker back onto the screen.");

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Advanced price thresholds"))
        {
            DrawAdvancedThresholds(inputWidth, ref triageChanged, ref uiOnlyChanged);
        }

        ImGui.Spacing();
        if (ImGui.Button("Reset to Defaults"))
        {
            ctx.Config.ApplySellModePresets(SellMode.Safe);
            ctx.Config.MarketUndercutGil = 1;
            ctx.Config.MarketMinFloorUnitPrice = 50;
            ctx.Config.BuyMaxGilPerPurchase = 50_000;
            ctx.Config.BuyOutlierMultiplier = 2.5f;
            ctx.Config.EnableLifestreamWorldHopPrompt = false;
            ctx.Config.WorldHopMinSavingsGil = 5_000;
            ctx.Config.EnableHoverHighlights = true;
            ctx.Config.MarketReviewBeforeConfirm = true;
            ctx.Config.MarketAutoComparePricesInReview = true;
            ctx.Config.MarketAutoFillRetainerSell = true;
            ctx.Config.MarketPromptStaleListings = true;
            ctx.Config.StaleListingUndercutPct = 25;
            ctx.Config.MarketOneListingPerClick = true;
            ctx.Config.MarketDailyActionLimit = 20;
            triageChanged = true;
        }

        ImGui.SameLine();
        ImGui.TextColored(SettingsUiContext.ColorMuted, "Custom item overrides are never reset.");

        if (triageChanged)
        {
            ctx.Config.Save(ctx.PluginInterface);
            ctx.TriageEngine.Invalidate();
            ctx.MagicSort.InvalidateButtonCache();
            ctx.InvalidateLocalCaches();
        }
        else if (uiOnlyChanged)
        {
            ctx.Config.Save(ctx.PluginInterface);
            ctx.MagicSort.InvalidateButtonCache();
        }
    }

    private void DrawAdvancedThresholds(float inputWidth, ref bool triageChanged, ref bool uiOnlyChanged)
    {
        ImGui.TextColored(
            SettingsUiContext.ColorMuted,
            "High-value items scale liquidity automatically: 10k-50k stacks allow slower sales (up to 21d); 50k+ allow 0.10/day (up to 30d).");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(inputWidth);
        var minStack = ctx.Config.MarketMinStackValue;
        if (ImGui.InputInt("Min stack value for Market (Gil)", ref minStack, 100, 500))
        {
            ctx.Config.MarketMinStackValue = Math.Clamp(minStack, 100, 1_000_000);
            triageChanged = true;
        }

        ImGui.SetNextItemWidth(inputWidth);
        var minUnit = ctx.Config.MarketMinUnitPrice;
        if (ImGui.InputInt("Min single-item price for Market (Gil)", ref minUnit, 100, 500))
        {
            ctx.Config.MarketMinUnitPrice = Math.Clamp(minUnit, 50, 1_000_000);
            triageChanged = true;
        }

        ImGui.SetNextItemWidth(inputWidth);
        var floorUnit = ctx.Config.MarketMinFloorUnitPrice;
        if (ImGui.InputInt("Min unit floor for small stacks (Gil)", ref floorUnit, 10, 50))
        {
            ctx.Config.MarketMinFloorUnitPrice = Math.Clamp(floorUnit, 1, 10_000);
            triageChanged = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Blocks cheap single items from wasting MB slots. Bulk stacks meeting 'Min stack value for Market' are still allowed.");
        }

        ImGui.SetNextItemWidth(inputWidth);
        var minVel = ctx.Config.MarketMinVelocity;
        if (ImGui.InputFloat("Min daily sales for Market", ref minVel, 0.05f, 0.25f, "%.2f"))
        {
            ctx.Config.MarketMinVelocity = Math.Clamp(minVel, 0.01f, 50f);
            triageChanged = true;
        }

        ImGui.SetNextItemWidth(inputWidth);
        var maxDays = ctx.Config.MarketMaxDaysSinceLastSale;
        if (ImGui.InputInt("Max days since last sale for Market", ref maxDays, 1, 7))
        {
            ctx.Config.MarketMaxDaysSinceLastSale = Math.Clamp(maxDays, 1, 90);
            triageChanged = true;
        }

        ImGui.SetNextItemWidth(inputWidth);
        var maxCompDays = ctx.Config.MarketMaxCompetitionDays;
        if (ImGui.InputInt("Max competitive backlog (days) before oversupplied", ref maxCompDays, 5, 15))
        {
            ctx.Config.MarketMaxCompetitionDays = Math.Clamp(maxCompDays, 5, 180);
            triageChanged = true;
        }

        ImGui.SetNextItemWidth(inputWidth);
        var stalePct = ctx.Config.StaleListingUndercutPct;
        if (ImGui.InputInt("Flag active listing if overpriced by (%)", ref stalePct, 5, 10))
        {
            ctx.Config.StaleListingUndercutPct = Math.Clamp(stalePct, 5, 200);
            uiOnlyChanged = true;
        }

        ImGui.SetNextItemWidth(inputWidth);
        var vendorMaxStack = ctx.Config.VendorMaxStackValue;
        if (ImGui.InputInt("Max stack value to sell to Vendor (Gil)", ref vendorMaxStack, 50, 200))
        {
            ctx.Config.VendorMaxStackValue = Math.Clamp(vendorMaxStack, 0, 50_000);
            triageChanged = true;
        }

        ImGui.SetNextItemWidth(inputWidth);
        var vendorMaxVel = ctx.Config.VendorMaxVelocity;
        if (ImGui.InputFloat("Max daily sales to sell to Vendor", ref vendorMaxVel, 0.05f, 0.25f, "%.2f"))
        {
            ctx.Config.VendorMaxVelocity = Math.Clamp(vendorMaxVel, 0f, 10f);
            triageChanged = true;
        }
    }

    private void RefreshInstalledPluginsCache()
    {
        var now = DateTime.UtcNow;
        if ((now - lastPluginCheckUtc).TotalSeconds < 10.0)
        {
            return;
        }

        lastPluginCheckUtc = now;
        cachedHasPennyPincher = false;
        cachedHasLifestream = false;
        foreach (var p in ctx.PluginInterface.InstalledPlugins)
        {
            if (!p.IsLoaded)
            {
                continue;
            }

            if (string.Equals(p.InternalName, "PennyPincher", StringComparison.OrdinalIgnoreCase))
            {
                cachedHasPennyPincher = true;
            }
            else if (string.Equals(p.InternalName, "Lifestream", StringComparison.OrdinalIgnoreCase))
            {
                cachedHasLifestream = true;
            }
        }
    }
}
