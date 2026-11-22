using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MagicInventory.Core;
using MagicInventory.Services;
using MagicInventory.Services.Triage;
using MagicInventory.UI.Settings;

namespace MagicInventory.UI;

public sealed class SettingsWindow : Window
{
    private readonly SettingsOverviewTab overviewTab;
    private readonly SettingsItemsTab itemsTab;
    private readonly SettingsRetainersTab retainersTab;
    private readonly SettingsConfigTab configTab;

    private ulong lastContentId;

    public SettingsWindow(
        IDalamudPluginInterface pluginInterface,
        IDataManager dataManager,
        Configuration config,
        HoldingsService holdings,
        UniversalisClient universalis,
        ItemTriageEngine triageEngine,
        MagicSortService magicSort,
        MaterialTrackerService materialTracker,
        Action? onResetTrackerPosition = null)
        : base("Magic Inventory###MagicInventorySettings")
    {
        SettingsItemsTab? itemsTabRef = null;
        var ctx = new SettingsUiContext
        {
            PluginInterface = pluginInterface,
            DataManager = dataManager,
            Config = config,
            Holdings = holdings,
            Universalis = universalis,
            TriageEngine = triageEngine,
            MagicSort = magicSort,
            MaterialTracker = materialTracker,
            OnResetTrackerPosition = onResetTrackerPosition,
            InvalidateLocalCaches = () => itemsTabRef?.InvalidateCaches(),
        };

        overviewTab = new SettingsOverviewTab(ctx);
        itemsTab = new SettingsItemsTab(ctx);
        itemsTabRef = itemsTab;
        retainersTab = new SettingsRetainersTab(ctx);
        configTab = new SettingsConfigTab(ctx);

        Size = new Vector2(800, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void PreDraw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640f * scale, 440f * scale),
            MaximumSize = new Vector2(1450f * scale, 980f * scale),
        };
    }

    public override void Draw()
    {
        var cid = InventorySafetyEvaluator.GetCurrentContentId();
        if (cid != lastContentId)
        {
            lastContentId = cid;
            itemsTab.ResetForCharacterChange();
        }

        if (!ImGui.BeginTabBar("MagicInventoryTabs"))
        {
            return;
        }

        if (ImGui.BeginTabItem("Overview"))
        {
            overviewTab.Draw();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Items"))
        {
            itemsTab.Draw(cid);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Retainers"))
        {
            retainersTab.Draw(cid);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Settings"))
        {
            configTab.Draw(cid);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }
}
