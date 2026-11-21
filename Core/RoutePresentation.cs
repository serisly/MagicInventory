using System.Numerics;
using MagicInventory.Services;

namespace MagicInventory.Core;

/// <summary>
/// Single source of truth for Magic Sort route names, short overlay badges,
/// context-menu labels, and ImGui color palette.
/// </summary>
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

    public static Vector4 GetRouteColor(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return ColorKeep;
        }

        if (route.StartsWith("Retainer", StringComparison.OrdinalIgnoreCase))
        {
            return ColorRetainer;
        }

        return route.ToLowerInvariant() switch
        {
            "market" => ColorMarket,
            "vendor" => ColorVendor,
            "grandcompany" => ColorGrandCompany,
            "goldsaucer" => ColorGoldSaucer,
            "use" => ColorUse,
            "materia" => ColorMateria,
            "armoire" => ColorArmoire,
            "glamour" => ColorGlamour,
            "armoury" => ColorArmoury,
            "tracked" => ColorTracked,
            _ => ColorKeep,
        };
    }

    public static string GetDisplayLabel(string? route, string? targetRetainerName = null)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return "Keep";
        }

        if (route.StartsWith("Retainer", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(targetRetainerName) ? "Retainer" : targetRetainerName;
        }

        return route.ToLowerInvariant() switch
        {
            "keep" => "Keep",
            "use" => "Use",
            "market" => "Market",
            "vendor" => "Vendor",
            "grandcompany" => "GC Seals",
            "goldsaucer" => "Gold Saucer",
            "materia" => "Extract Materia",
            "armoire" => "Armoire",
            "glamour" => "Glamour Dresser",
            "armoury" => "Armoury",
            "tracked" => "Tracked",
            _ => route,
        };
    }

    public static string GetShortBadgeLabel(string? route, string? targetRetainerName = null)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return string.Empty;
        }

        if (route.StartsWith("Retainer", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(targetRetainerName) ? "Retainer" : targetRetainerName;
        }

        return route.ToLowerInvariant() switch
        {
            "market" => "Market",
            "vendor" => "Vendor",
            "grandcompany" => "GC",
            "goldsaucer" => "Saucer",
            "use" => "Use",
            "materia" => "Materia",
            "armoire" => "Armoire",
            "glamour" => "Glamour",
            "armoury" => "Armoury",
            "tracked" => "Track",
            _ => string.Empty,
        };
    }

    public static string FormatAutoPredictedLabel(MagicSortEntry? autoEntry, out string routeFilter)
    {
        if (autoEntry == null || string.Equals(autoEntry.Route, "Keep", StringComparison.OrdinalIgnoreCase))
        {
            routeFilter = "Keep";
            return "Keep";
        }

        if (string.Equals(autoEntry.Route, "Retainer", StringComparison.OrdinalIgnoreCase))
        {
            routeFilter = autoEntry.TargetRetainerId != 0
                ? $"Retainer:{autoEntry.TargetRetainerId}"
                : "Retainer";
            return GetDisplayLabel("Retainer", autoEntry.TargetRetainerName);
        }

        routeFilter = autoEntry.Route;
        return GetDisplayLabel(autoEntry.Route, autoEntry.TargetRetainerName);
    }

    public static string FormatDestinationLabel(MagicSortEntry entry)
        => GetDisplayLabel(entry.Route, entry.TargetRetainerName);

    public static string FormatOverrideSpec(
        string? overrideSpec,
        Func<ulong, string?> resolveRetainerName,
        int marketPriority = 0)
    {
        if (string.IsNullOrWhiteSpace(overrideSpec))
        {
            return "Auto";
        }

        if (overrideSpec.StartsWith("Retainer:", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(overrideSpec["Retainer:".Length..], out var retId))
        {
            var retName = resolveRetainerName(retId);
            return string.IsNullOrWhiteSpace(retName) ? "Retainer" : retName;
        }

        if (string.Equals(overrideSpec, "Market", StringComparison.OrdinalIgnoreCase))
        {
            return marketPriority > 0
                ? "Market (High)"
                : (marketPriority < 0 ? "Market (Low)" : "Market");
        }

        return GetDisplayLabel(overrideSpec);
    }
}
