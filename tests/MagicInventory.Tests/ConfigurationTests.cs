using System.Collections.Generic;
using Xunit;

namespace MagicInventory.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void OverridesAndPriorities_AreIsolatedPerCharacter()
    {
        var cfg = new Configuration();
        const ulong mainCid = 1001UL;
        const ulong altCid = 2002UL;

        cfg.GetManualRouteOverrides(mainCid)[12345] = "Vendor";
        cfg.GetMarketListingPriorities(mainCid)[12345] = 1;

        Assert.Equal("Vendor", cfg.GetManualRouteOverrides(mainCid)[12345]);
        Assert.Equal(1, cfg.GetMarketListingPriorities(mainCid)[12345]);
        Assert.False(cfg.GetManualRouteOverrides(altCid).ContainsKey(12345));
        Assert.False(cfg.GetMarketListingPriorities(altCid).ContainsKey(12345));
    }

    [Fact]
    public void DailyMarketActions_AreTrackedIndependentlyPerCharacter()
    {
        var cfg = new Configuration { MarketDailyActionLimit = 3 };
        const ulong mainCid = 1001UL;
        const ulong altCid = 2002UL;

        cfg.RecordMarketAction(mainCid);
        cfg.RecordMarketAction(mainCid);
        cfg.RecordMarketAction(mainCid);

        Assert.Equal(3, cfg.GetUsedMarketActionsToday(mainCid));
        Assert.Equal(0, cfg.GetRemainingMarketActionsToday(mainCid));

        Assert.Equal(0, cfg.GetUsedMarketActionsToday(altCid));
        Assert.Equal(3, cfg.GetRemainingMarketActionsToday(altCid));
    }

    [Fact]
    public void EnsureMigrated_MigratesLegacyLocksGlobalOverridesAndStringSpecialties()
    {
        var cfg = new Configuration
        {
            Version = 2,
            MarketActionsDay = System.DateTime.Now.ToString("yyyy-MM-dd"),
            MarketActionsToday = 4,
        };
        cfg.ManualRouteOverrides[5000] = "Market";
        cfg.ManualRouteOverrides[6000] = "Retainer:999";
        cfg.MarketListingPriorities[5000] = 1;

        var profile = new CharacterProfile();
        profile.LockedItemIds.Add(7777);
        profile.RetainerSpecialties[999UL] = new HashSet<string> { "Ingredient", "Lumber" };
        cfg.Characters[42UL] = profile;

        var catMap = new Dictionary<string, uint>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["Ingredient"] = 45,
            ["Lumber"] = 50,
        };

        var loaded = cfg.GetOrCreateCharacter(42UL);
        var migratedSpecs = Configuration.MigrateLegacyRetainerSpecialties(
            loaded,
            name => catMap.TryGetValue(name, out var id) ? id : null);

        Assert.True(migratedSpecs);
        Assert.Equal(3, cfg.Version);
        Assert.Empty(loaded.LockedItemIds);
        Assert.Equal("Keep", loaded.ManualRouteOverrides[7777]);
        Assert.Equal("Market", loaded.ManualRouteOverrides[5000]);
        Assert.Equal("Retainer:999", loaded.ManualRouteOverrides[6000]);
        Assert.Equal(1, loaded.MarketListingPriorities[5000]);
        Assert.Contains(45u, loaded.RetainerSpecialtyCategoryIds[999UL]);
        Assert.Contains(50u, loaded.RetainerSpecialtyCategoryIds[999UL]);
        Assert.Equal(4, cfg.GetUsedMarketActionsToday(42UL));
    }
}
