using MagicInventory.Core;
using Xunit;

namespace MagicInventory.Tests;

public sealed class ItemRouteTests
{
    [Theory]
    [InlineData("Keep", RouteKind.Keep, 0UL)]
    [InlineData("keep", RouteKind.Keep, 0UL)]
    [InlineData("Market", RouteKind.Market, 0UL)]
    [InlineData("Vendor", RouteKind.Vendor, 0UL)]
    [InlineData("GrandCompany", RouteKind.GrandCompany, 0UL)]
    [InlineData("GoldSaucer", RouteKind.GoldSaucer, 0UL)]
    [InlineData("Materia", RouteKind.Materia, 0UL)]
    [InlineData("Use", RouteKind.Use, 0UL)]
    [InlineData("Armoire", RouteKind.Armoire, 0UL)]
    [InlineData("Glamour", RouteKind.Glamour, 0UL)]
    [InlineData("Tracked", RouteKind.Tracked, 0UL)]
    [InlineData("Armoury", RouteKind.Armoury, 0UL)]
    [InlineData("Retainer", RouteKind.Retainer, 0UL)]
    [InlineData("Retainer:123456789012345", RouteKind.Retainer, 123456789012345UL)]
    [InlineData("  retainer:42  ", RouteKind.Retainer, 42UL)]
    public void TryParse_ParsesAllLegacyStrings(string raw, RouteKind expectedKind, ulong expectedRetainerId)
    {
        Assert.True(ItemRoute.TryParse(raw, out var parsed));
        Assert.Equal(expectedKind, parsed.Kind);
        Assert.Equal(expectedRetainerId, parsed.RetainerId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unknown")]
    [InlineData("Retainer:")]
    [InlineData("Retainer:0")]
    [InlineData("Retainer:abc")]
    [InlineData("Sell")]
    public void TryParse_RejectsInvalidValues(string? raw)
    {
        Assert.False(ItemRoute.TryParse(raw, out _));
        Assert.Equal(ItemRoute.Keep, ItemRoute.ParseOrKeep(raw));
    }

    [Fact]
    public void ToConfigString_RoundTripsExactLegacyFormat()
    {
        var routes = new[]
        {
            (ItemRoute.Keep, "Keep"),
            (ItemRoute.Market, "Market"),
            (ItemRoute.Vendor, "Vendor"),
            (ItemRoute.GrandCompany, "GrandCompany"),
            (ItemRoute.GoldSaucer, "GoldSaucer"),
            (ItemRoute.Materia, "Materia"),
            (ItemRoute.Use, "Use"),
            (ItemRoute.Armoire, "Armoire"),
            (ItemRoute.Glamour, "Glamour"),
            (ItemRoute.Tracked, "Tracked"),
            (ItemRoute.Armoury, "Armoury"),
            (ItemRoute.AnyRetainer, "Retainer"),
            (ItemRoute.ForRetainer(987654321UL), "Retainer:987654321"),
        };

        foreach (var (route, expected) in routes)
        {
            var text = route.ToConfigString();
            Assert.Equal(expected, text);
            Assert.True(ItemRoute.TryParse(text, out var parsed));
            Assert.Equal(route, parsed);
        }
    }

    [Fact]
    public void RouteFilter_MatchesExactAndWildcardRetainerAndAnySell()
    {
        var anyRetainer = RouteFilter.Exact(ItemRoute.AnyRetainer);
        Assert.True(anyRetainer.Matches(ItemRoute.ForRetainer(10)));
        Assert.True(anyRetainer.Matches(ItemRoute.ForRetainer(20)));
        Assert.False(anyRetainer.Matches(ItemRoute.Market));

        var specificRetainer = RouteFilter.Exact(ItemRoute.ForRetainer(10));
        Assert.True(specificRetainer.Matches(ItemRoute.ForRetainer(10)));
        Assert.False(specificRetainer.Matches(ItemRoute.ForRetainer(20)));
        Assert.False(specificRetainer.Matches(ItemRoute.AnyRetainer));

        Assert.True(RouteFilter.AnySell.Matches(ItemRoute.Market));
        Assert.True(RouteFilter.AnySell.Matches(ItemRoute.Vendor));
        Assert.True(RouteFilter.AnySell.Matches(ItemRoute.GrandCompany));
        Assert.True(RouteFilter.AnySell.Matches(ItemRoute.GoldSaucer));
        Assert.False(RouteFilter.AnySell.Matches(ItemRoute.Keep));
        Assert.False(RouteFilter.AnySell.Matches(ItemRoute.ForRetainer(10)));
    }

    [Fact]
    public void Configuration_TryGetManualRoute_ReadsLegacyStringOverrides()
    {
        var cfg = new Configuration();
        var overrides = cfg.GetManualRouteOverrides(100UL);
        overrides[10] = "Market";
        overrides[20] = "Retainer:777";
        overrides[30] = "NotARoute";

        Assert.True(cfg.TryGetManualRoute(100UL, 10, out var r1));
        Assert.Equal(ItemRoute.Market, r1);

        Assert.True(cfg.TryGetManualRoute(100UL, 20, out var r2));
        Assert.Equal(ItemRoute.ForRetainer(777UL), r2);

        Assert.False(cfg.TryGetManualRoute(100UL, 30, out _));
        Assert.False(cfg.TryGetManualRoute(100UL, 999, out _));
    }

    [Fact]
    public void RoutePresentation_FormatsOverrideSpecsAndPriorities()
    {
        string? Resolve(ulong id) => id == 55UL ? "Alisaie" : null;

        Assert.Equal("Auto", RoutePresentation.FormatOverrideSpec(null, Resolve));
        Assert.Equal("Alisaie", RoutePresentation.FormatOverrideSpec("Retainer:55", Resolve));
        Assert.Equal("Retainer", RoutePresentation.FormatOverrideSpec("Retainer:99", Resolve));
        Assert.Equal("Market (High)", RoutePresentation.FormatOverrideSpec("Market", Resolve, 1));
        Assert.Equal("Market", RoutePresentation.FormatOverrideSpec("Market", Resolve, 0));
        Assert.Equal("Market (Low)", RoutePresentation.FormatOverrideSpec("Market", Resolve, -1));
        Assert.Equal("GC Seals", RoutePresentation.FormatOverrideSpec("GrandCompany", Resolve));
    }
}
