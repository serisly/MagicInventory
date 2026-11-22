using Dalamud.Configuration;
using Dalamud.Plugin;
using MagicInventory.Core;

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

    public Dictionary<uint, string> ManualRouteOverrides { get; set; } = new();

    public Dictionary<uint, int> MarketListingPriorities { get; set; } = new();

    public Dictionary<ulong, HashSet<uint>> RetainerSpecialtyCategoryIds { get; set; } = new();

    public string MarketActionsDay { get; set; } = string.Empty;

    public int MarketActionsToday { get; set; }

    public bool MigratedToV3 { get; set; }

    public HashSet<uint> LockedItemIds { get; set; } = new();
    public Dictionary<ulong, HashSet<string>> RetainerSpecialties { get; set; } = new();
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;

    public float TrackerX { get; set; } = 24f;
    public float TrackerY { get; set; } = 120f;
    public bool EnableHoverHighlights { get; set; } = true;

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

    public int BuyMaxGilPerPurchase { get; set; } = 50_000;
    public float BuyOutlierMultiplier { get; set; } = 2.5f;
    public bool EnableLifestreamWorldHopPrompt { get; set; } = false;
    public int WorldHopMinSavingsGil { get; set; } = 5_000;

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

    public Dictionary<uint, string> GetManualRouteOverrides(ulong contentId)
        => GetOrCreateCharacter(contentId).ManualRouteOverrides;

    public Dictionary<uint, int> GetMarketListingPriorities(ulong contentId)
        => GetOrCreateCharacter(contentId).MarketListingPriorities;

    public bool TryGetManualRouteOverride(ulong contentId, uint baseItemId, out string routeSpec)
    {
        if (baseItemId == 0)
        {
            routeSpec = string.Empty;
            return false;
        }

        return GetOrCreateCharacter(contentId).ManualRouteOverrides.TryGetValue(baseItemId, out routeSpec!)
            && !string.IsNullOrWhiteSpace(routeSpec);
    }

    public bool TryGetManualRoute(ulong contentId, uint baseItemId, out ItemRoute route)
    {
        route = default;
        return TryGetManualRouteOverride(contentId, baseItemId, out var spec)
            && ItemRoute.TryParse(spec, out route);
    }

    public int GetMarketListingPriority(ulong contentId, uint baseItemId)
        => baseItemId == 0 ? 0 : GetOrCreateCharacter(contentId).MarketListingPriorities.GetValueOrDefault(baseItemId, 0);

    public int GetUsedMarketActionsToday(ulong contentId)
    {
        var profile = GetOrCreateCharacter(contentId);
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        return string.Equals(profile.MarketActionsDay, today, StringComparison.Ordinal)
            ? profile.MarketActionsToday
            : 0;
    }

    public int GetRemainingMarketActionsToday(ulong contentId)
    {
        if (MarketDailyActionLimit <= 0)
        {
            return int.MaxValue;
        }

        return Math.Max(0, MarketDailyActionLimit - GetUsedMarketActionsToday(contentId));
    }

    public void RecordMarketAction(ulong contentId)
    {
        var profile = GetOrCreateCharacter(contentId);
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (!string.Equals(profile.MarketActionsDay, today, StringComparison.Ordinal))
        {
            profile.MarketActionsDay = today;
            profile.MarketActionsToday = 0;
        }

        profile.MarketActionsToday++;
    }

    public static HashSet<uint> GetOrCreateRetainerSpecialtyCategoryIds(CharacterProfile profile, ulong retainerId)
    {
        if (!profile.RetainerSpecialtyCategoryIds.TryGetValue(retainerId, out var set) || set == null)
        {
            set = new HashSet<uint>();
            profile.RetainerSpecialtyCategoryIds[retainerId] = set;
        }

        return set;
    }

    public static bool MigrateLegacyRetainerSpecialties(
        CharacterProfile profile,
        Func<string, uint?> resolveCategoryIdByName)
    {
        if (profile.RetainerSpecialties.Count == 0)
        {
            return false;
        }

        foreach (var (retainerId, legacyNames) in profile.RetainerSpecialties)
        {
            if (legacyNames == null || legacyNames.Count == 0)
            {
                continue;
            }

            var targetSet = GetOrCreateRetainerSpecialtyCategoryIds(profile, retainerId);
            foreach (var name in legacyNames)
            {
                var catId = resolveCategoryIdByName(name);
                if (catId.HasValue && catId.Value != 0)
                {
                    targetSet.Add(catId.Value);
                }
            }
        }

        profile.RetainerSpecialties.Clear();
        return true;
    }

    public Dictionary<uint, string> ManualRouteOverrides { get; set; } = new();
    public Dictionary<uint, int> MarketListingPriorities { get; set; } = new();

    public Dictionary<ulong, CharacterProfile> Characters { get; set; } = new();

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
                ManualRouteOverrides = ManualRouteOverrides,
                MarketListingPriorities = MarketListingPriorities,
                MarketActionsDay = MarketActionsDay,
                MarketActionsToday = MarketActionsToday,
                MigratedToV3 = true,
            };
            MigrateProfileLocks(fallbackProfile);
            return fallbackProfile;
        }

        if (!Characters.TryGetValue(contentId, out var profile))
        {
            profile = new CharacterProfile();

            if (Characters.Count == 0 && (TrackedItems.Count > 0 || LockedItemIds.Count > 0))
            {
                profile.TrackedRecipeId = TrackedRecipeId;
                profile.TrackedTitle = TrackedTitle;
                profile.TrackedCraftCount = Math.Max(1, TrackedCraftCount);
                profile.TrackWithdrawAllStacks = TrackWithdrawAllStacks;
                profile.TrackedItems = new Dictionary<uint, int>(TrackedItems);
                foreach (var id in LockedItemIds)
                {
                    if (id != 0)
                    {
                        profile.ManualRouteOverrides.TryAdd(id, "Keep");
                    }
                }

                TrackedItems.Clear();
                LockedItemIds.Clear();
            }

            Characters[contentId] = profile;
        }

        MigrateProfileLocks(profile);
        MigrateLegacyV2AccountState(profile);

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

    private static void MigrateProfileLocks(CharacterProfile profile)
    {
        if (profile.LockedItemIds.Count == 0)
        {
            return;
        }

        foreach (var id in profile.LockedItemIds)
        {
            if (id != 0)
            {
                profile.ManualRouteOverrides.TryAdd(id, "Keep");
            }
        }

        profile.LockedItemIds.Clear();
    }

    private void MigrateLegacyV2AccountState(CharacterProfile profile)
    {
        if (profile.MigratedToV3)
        {
            return;
        }

        foreach (var (itemId, routeSpec) in ManualRouteOverrides)
        {
            if (itemId != 0 && !string.IsNullOrWhiteSpace(routeSpec))
            {
                profile.ManualRouteOverrides.TryAdd(itemId, routeSpec);
            }
        }

        foreach (var (itemId, prio) in MarketListingPriorities)
        {
            if (itemId != 0 && prio != 0)
            {
                profile.MarketListingPriorities.TryAdd(itemId, prio);
            }
        }

        if (string.IsNullOrEmpty(profile.MarketActionsDay) && !string.IsNullOrEmpty(MarketActionsDay))
        {
            profile.MarketActionsDay = MarketActionsDay;
            profile.MarketActionsToday = MarketActionsToday;
        }

        profile.MigratedToV3 = true;
        Version = 3;
    }

    public void Save(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.SavePluginConfig(this);
    }
}
