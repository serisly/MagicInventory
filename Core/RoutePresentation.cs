using System;
using System.Numerics;
using MagicInventory.Services;

namespace MagicInventory.Core;

public static class RoutePresentation
{
    public static readonly Vector4 ColorRetainer = new(0.56f, 0.80f, 0.98f, 1.00f);
    public static readonly Vector4 ColorMarket = new(0.98f, 0.82f, 0.36f, 1.00f);
    public static readonly Vector4 ColorVendor = new(0.94f, 0.52f, 0.44f, 1.00f);
    public static readonly Vector4 ColorGrandCompany = new(0.45f, 0.86f, 0.56f, 1.00f);
    public static readonly Vector4 ColorGoldSaucer = new(0.96f, 0.72f, 0.32f, 1.00f);
    public static readonly Vector4 ColorUse = new(0.46f, 0.94f, 0.90f, 1.00f);
    public static readonly Vector4 ColorMateria = new(0.78f, 0.56f, 0.96f, 1.00f);
    public static readonly Vector4 ColorArmoire = new(0.68f, 0.76f, 0.96f, 1.00f);
    public static readonly Vector4 ColorGlamour = new(0.96f, 0.60f, 0.86f, 1.00f);
    public static readonly Vector4 ColorArmoury = new(0.72f, 0.84f, 0.96f, 1.00f);
    public static readonly Vector4 ColorKeep = new(0.62f, 0.65f, 0.70f, 1.00f);
    public static readonly Vector4 ColorTracked = new(0.50f, 0.92f, 0.64f, 1.00f);

    public static Vector4 GetRouteColor(ItemRoute route) => GetRouteColor(route.Kind);

    public static Vector4 GetRouteColor(RouteKind kind) => kind switch
    {
        RouteKind.Retainer => ColorRetainer,
        RouteKind.Market => ColorMarket,
        RouteKind.Vendor => ColorVendor,
        RouteKind.GrandCompany => ColorGrandCompany,
        RouteKind.GoldSaucer => ColorGoldSaucer,
        RouteKind.Use => ColorUse,
        RouteKind.Materia => ColorMateria,
        RouteKind.Armoire => ColorArmoire,
        RouteKind.Glamour => ColorGlamour,
        RouteKind.Armoury => ColorArmoury,
        RouteKind.Tracked => ColorTracked,
        _ => ColorKeep,
    };

    public static string GetDisplayLabel(ItemRoute route, string? targetRetainerName = null)
        => GetDisplayLabel(route.Kind, targetRetainerName);

    public static string GetDisplayLabel(RouteKind kind, string? targetRetainerName = null) => kind switch
    {
        RouteKind.Retainer => string.IsNullOrWhiteSpace(targetRetainerName) ? "Retainer" : targetRetainerName,
        RouteKind.Keep => "Keep",
        RouteKind.Use => "Use",
        RouteKind.Market => "Market",
        RouteKind.Vendor => "Vendor",
        RouteKind.GrandCompany => "GC Seals",
        RouteKind.GoldSaucer => "Gold Saucer",
        RouteKind.Materia => "Extract Materia",
        RouteKind.Armoire => "Armoire",
        RouteKind.Glamour => "Glamour Dresser",
        RouteKind.Armoury => "Armoury",
        RouteKind.Tracked => "Tracked",
        _ => "Keep",
    };

    public static string GetShortBadgeLabel(ItemRoute route, string? targetRetainerName = null)
        => GetShortBadgeLabel(route.Kind, targetRetainerName);

    public static string GetShortBadgeLabel(RouteKind kind, string? targetRetainerName = null) => kind switch
    {
        RouteKind.Retainer => string.IsNullOrWhiteSpace(targetRetainerName) ? "Retainer" : targetRetainerName,
        RouteKind.Market => "Market",
        RouteKind.Vendor => "Vendor",
        RouteKind.GrandCompany => "GC",
        RouteKind.GoldSaucer => "Saucer",
        RouteKind.Use => "Use",
        RouteKind.Materia => "Materia",
        RouteKind.Armoire => "Armoire",
        RouteKind.Glamour => "Glamour",
        RouteKind.Armoury => "Armoury",
        RouteKind.Tracked => "Track",
        _ => string.Empty,
    };

    public static string FormatAutoPredictedLabel(MagicSortEntry? autoEntry, out ItemRoute routeFilter)
    {
        if (autoEntry == null || autoEntry.Route.IsKeep)
        {
            routeFilter = ItemRoute.Keep;
            return "Keep";
        }

        routeFilter = autoEntry.Route;
        return GetDisplayLabel(autoEntry.Route, autoEntry.TargetRetainerName);
    }

    public static string FormatDestinationLabel(MagicSortEntry entry)
        => GetDisplayLabel(entry.Route, entry.TargetRetainerName);

    public static string FormatOverrideRoute(
        ItemRoute? overrideRoute,
        Func<ulong, string?> resolveRetainerName,
        int marketPriority = 0)
    {
        if (!overrideRoute.HasValue)
        {
            return "Auto";
        }

        var route = overrideRoute.Value;
        if (route.IsSpecificRetainer)
        {
            var retName = resolveRetainerName(route.RetainerId);
            return string.IsNullOrWhiteSpace(retName) ? "Retainer" : retName;
        }

        if (route.Kind == RouteKind.Market)
        {
            return marketPriority > 0
                ? "Market (High)"
                : (marketPriority < 0 ? "Market (Low)" : "Market");
        }

        return GetDisplayLabel(route);
    }

    public static string FormatOverrideSpec(
        string? overrideSpec,
        Func<ulong, string?> resolveRetainerName,
        int marketPriority = 0)
    {
        if (string.IsNullOrWhiteSpace(overrideSpec))
        {
            return "Auto";
        }

        return ItemRoute.TryParse(overrideSpec, out var parsed)
            ? FormatOverrideRoute(parsed, resolveRetainerName, marketPriority)
            : overrideSpec;
    }
}
