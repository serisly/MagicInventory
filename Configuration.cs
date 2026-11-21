using Dalamud.Configuration;
using Dalamud.Plugin;

namespace MagicInventory;

public enum SellMode
{
    Safe = 0,
    Balanced = 1,
    Purge = 2,
}

[Serializable]
public sealed class CharacterProfile
{
    public string CharacterName { get; set; } = string.Empty;

    public string HomeWorld { get; set; } = string.Empty;

    public uint TrackedRecipeId { get; set; }

    public string TrackedTitle { get; set; } = string.Empty;

    public int TrackedCraftCount { get; set; } = 1;

    public bool TrackWithdrawAllStacks { get; set; }

    public Dictionary<uint, int> TrackedItems { get; set; } = new();

    public HashSet<uint> LockedItemIds { get; set; } = new();

    /// <summary>
    /// Optional per-retainer specialty UI categories (e.g. "Ingredient", "Lumber", "Materia").
    /// Empty means auto-learned from existing retainer contents + sensible defaults.
    /// </summary>
    public Dictionary<ulong, HashSet<string>> RetainerSpecialties { get; set; } = new();
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    // UI placement (shared across characters)
    public float TrackerX { get; set; } = 24f;
    public float TrackerY { get; set; } = 120f;
    public bool EnableHoverHighlights { get; set; } = true;

    // Sell rules (account-wide, editable in Settings)
    public SellMode SellMode { get; set; } = SellMode.Safe;
    public int MarketMinStackValue { get; set; } = 2500;
    public int MarketMinUnitPrice { get; set; } = 1000;
    public int MarketMinFloorUnitPrice { get; set; } = 50;
    public float MarketMinVelocity { get; set; } = 0.5f;
    public int MarketMaxDaysSinceLastSale { get; set; } = 14;
    public int MarketMaxCompetitionDays { get; set; } = 30;
    public int MarketUndercutGil { get; set; } = 1;

    public int VendorMaxStackValue { get; set; } = 400;
    public float VendorMaxVelocity { get; set; } = 0.5f;

    public void ApplySellModePresets(SellMode mode)
    {
        SellMode = mode;
        switch (mode)
        {
            case SellMode.Safe:
                MarketMinStackValue = 2500;
                MarketMinUnitPrice = 1000;
                MarketMinFloorUnitPrice = 50;
                MarketMinVelocity = 0.5f;
                MarketMaxDaysSinceLastSale = 14;
                MarketMaxCompetitionDays = 30;
                VendorMaxStackValue = 400;
                VendorMaxVelocity = 0.5f;
                break;
            case SellMode.Balanced:
                MarketMinStackValue = 1000;
                MarketMinUnitPrice = 500;
                MarketMinFloorUnitPrice = 50;
                MarketMinVelocity = 0.35f;
                MarketMaxDaysSinceLastSale = 21;
                MarketMaxCompetitionDays = 45;
                VendorMaxStackValue = 600;
                VendorMaxVelocity = 1.0f;
                break;
            case SellMode.Purge:
                MarketMinStackValue = 500;
                MarketMinUnitPrice = 250;
                MarketMinFloorUnitPrice = 30;
                MarketMinVelocity = 0.2f;
                MarketMaxDaysSinceLastSale = 30;
                MarketMaxCompetitionDays = 60;
                VendorMaxStackValue = 1000;
                VendorMaxVelocity = 5.0f;
                break;
        }
    }

    // Magic Buy safety bounds
    public int BuyMaxGilPerPurchase { get; set; } = 50_000;
    public float BuyOutlierMultiplier { get; set; } = 2.5f;
    public bool EnableLifestreamWorldHopPrompt { get; set; } = false;
    public int WorldHopMinSavingsGil { get; set; } = 5_000;

    // Market Board selling workflow & safety limits
    public bool MarketReviewBeforeConfirm { get; set; } = true;
    public bool MarketAutoComparePricesInReview { get; set; } = true;
    public bool MarketAutoFillRetainerSell { get; set; } = true;
    public bool MarketPromptStaleListings { get; set; } = true;
    public int StaleListingUndercutPct { get; set; } = 25;
    public bool MarketOneListingPerClick { get; set; } = true;
    public int MarketDailyActionLimit { get; set; } = 20;
    public string MarketActionsDay { get; set; } = string.Empty;
    public int MarketActionsToday { get; set; }

    [NonSerialized]
    private CharacterProfile? fallbackProfile;

    public int GetUsedMarketActionsToday()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        return string.Equals(MarketActionsDay, today, StringComparison.Ordinal)
            ? MarketActionsToday
            : 0;
    }

    public int GetRemainingMarketActionsToday()
    {
        // 0 = unlimited
        if (MarketDailyActionLimit <= 0)
        {
            return int.MaxValue;
        }

        return Math.Max(0, MarketDailyActionLimit - GetUsedMarketActionsToday());
    }

    public void RecordMarketAction()
    {
        RollMarketDayIfNeeded();
        MarketActionsToday++;
    }

    private void RollMarketDayIfNeeded()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (!string.Equals(MarketActionsDay, today, StringComparison.Ordinal))
        {
            MarketActionsDay = today;
            MarketActionsToday = 0;
        }
    }

    public static HashSet<string> GetOrCreateRetainerSpecialtySet(CharacterProfile profile, ulong retainerId)
    {
        if (!profile.RetainerSpecialties.TryGetValue(retainerId, out var set) || set == null)
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            profile.RetainerSpecialties[retainerId] = set;
            return set;
        }

        if (!ReferenceEquals(set.Comparer, StringComparer.OrdinalIgnoreCase))
        {
            set = new HashSet<string>(set, StringComparer.OrdinalIgnoreCase);
            profile.RetainerSpecialties[retainerId] = set;
        }

        return set;
    }

    /// <summary>
    /// Manual per-item route overrides ("Keep", "Use", "Market", "Vendor", "GrandCompany", or "Retainer:&lt;id&gt;").
    /// Absence means automatic rules. Never cleared by automatic settings reset.
    /// </summary>
    public Dictionary<uint, string> ManualRouteOverrides { get; set; } = new();

    /// <summary>
    /// Optional per-item Market Board listing priority (1 = High, 0 = Normal, -1 = Low).
    /// High-priority items are listed first when retainer sale slots are limited.
    /// </summary>
    public Dictionary<uint, int> MarketListingPriorities { get; set; } = new();

    /// <summary>
    /// Per-character state keyed by PlayerState.ContentId (isolates alts cleanly).
    /// </summary>
    public Dictionary<ulong, CharacterProfile> Characters { get; set; } = new();

    // Legacy single-character fallback fields (migrated lazily into Characters[contentId])
    public uint TrackedRecipeId { get; set; }
    public string TrackedTitle { get; set; } = string.Empty;
    public int TrackedCraftCount { get; set; } = 1;
    public bool TrackWithdrawAllStacks { get; set; }
    public Dictionary<uint, int> TrackedItems { get; set; } = new();
    public HashSet<uint> LockedItemIds { get; set; } = new();

    public CharacterProfile GetOrCreateCharacter(ulong contentId, string? name = null, string? homeWorld = null)
    {
        if (contentId == 0)
        {
            fallbackProfile ??= new CharacterProfile
            {
                TrackedRecipeId = TrackedRecipeId,
                TrackedTitle = TrackedTitle,
                TrackedCraftCount = Math.Max(1, TrackedCraftCount),
                TrackWithdrawAllStacks = TrackWithdrawAllStacks,
                TrackedItems = TrackedItems,
                LockedItemIds = LockedItemIds,
            };
            return fallbackProfile;
        }

        if (!Characters.TryGetValue(contentId, out var profile))
        {
            profile = new CharacterProfile();

            // Migrate legacy top-level fields once on first character created
            if (Characters.Count == 0 && (TrackedItems.Count > 0 || LockedItemIds.Count > 0))
            {
                profile.TrackedRecipeId = TrackedRecipeId;
                profile.TrackedTitle = TrackedTitle;
                profile.TrackedCraftCount = Math.Max(1, TrackedCraftCount);
                profile.TrackWithdrawAllStacks = TrackWithdrawAllStacks;
                profile.TrackedItems = new Dictionary<uint, int>(TrackedItems);
                profile.LockedItemIds = new HashSet<uint>(LockedItemIds);
                TrackedItems.Clear();
                LockedItemIds.Clear();
            }

            Characters[contentId] = profile;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            profile.CharacterName = name;
        }

        if (!string.IsNullOrWhiteSpace(homeWorld))
        {
            profile.HomeWorld = homeWorld;
        }

        if (profile.TrackedCraftCount < 1)
        {
            profile.TrackedCraftCount = 1;
        }

        return profile;
    }

    public void Save(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.SavePluginConfig(this);
    }
}
