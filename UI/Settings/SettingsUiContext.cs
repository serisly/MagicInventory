using System;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MagicInventory.Services;
using MagicInventory.Services.Triage;

namespace MagicInventory.UI.Settings;

internal sealed class SettingsUiContext
{
    public static readonly Vector4 ColorMuted = new(0.68f, 0.70f, 0.74f, 1f);
    public static readonly Vector4 ColorGood = new(0.55f, 0.88f, 0.58f, 1f);
    public static readonly Vector4 ColorGold = new(0.95f, 0.82f, 0.44f, 1f);
    public static readonly Vector4 ColorWarn = new(0.95f, 0.55f, 0.45f, 1f);

    public required IDalamudPluginInterface PluginInterface { get; init; }
    public required IDataManager DataManager { get; init; }
    public required Configuration Config { get; init; }
    public required HoldingsService Holdings { get; init; }
    public required UniversalisClient Universalis { get; init; }
    public required ItemTriageEngine TriageEngine { get; init; }
    public required MagicSortService MagicSort { get; init; }
    public required MaterialTrackerService MaterialTracker { get; init; }
    public Action? OnResetTrackerPosition { get; init; }
    public required Action InvalidateLocalCaches { get; init; }

    public static string FormatSellModeName(SellMode mode) => mode switch
    {
        SellMode.Safe => "Safe",
        SellMode.Balanced => "Balanced",
        SellMode.Purge => "Clear Space",
        _ => "Safe",
    };
}
