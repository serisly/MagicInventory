using System;

namespace MagicInventory.Core;

public enum RouteKind
{
    Keep = 0,
    Market,
    Vendor,
    GrandCompany,
    GoldSaucer,
    Materia,
    Use,
    Armoire,
    Glamour,
    Tracked,
    Armoury,
    Retainer,
}

public readonly record struct ItemRoute(RouteKind Kind, ulong RetainerId = 0)
{
    public const string RetainerPrefix = "Retainer:";

    public static readonly ItemRoute Keep = new(RouteKind.Keep);
    public static readonly ItemRoute Market = new(RouteKind.Market);
    public static readonly ItemRoute Vendor = new(RouteKind.Vendor);
    public static readonly ItemRoute GrandCompany = new(RouteKind.GrandCompany);
    public static readonly ItemRoute GoldSaucer = new(RouteKind.GoldSaucer);
    public static readonly ItemRoute Materia = new(RouteKind.Materia);
    public static readonly ItemRoute Use = new(RouteKind.Use);
    public static readonly ItemRoute Armoire = new(RouteKind.Armoire);
    public static readonly ItemRoute Glamour = new(RouteKind.Glamour);
    public static readonly ItemRoute Tracked = new(RouteKind.Tracked);
    public static readonly ItemRoute Armoury = new(RouteKind.Armoury);
    public static readonly ItemRoute AnyRetainer = new(RouteKind.Retainer);

    public static ItemRoute ForRetainer(ulong retainerId) => new(RouteKind.Retainer, retainerId);

    public bool IsKeep => Kind == RouteKind.Keep;
    public bool IsRetainer => Kind == RouteKind.Retainer;
    public bool IsSpecificRetainer => Kind == RouteKind.Retainer && RetainerId != 0;
    public bool IsDisposal => Kind is RouteKind.Market or RouteKind.Vendor or RouteKind.GrandCompany;
    public bool IsSell => Kind is RouteKind.Market or RouteKind.Vendor or RouteKind.GrandCompany or RouteKind.GoldSaucer;

    public string ToConfigString() => Kind switch
    {
        RouteKind.Keep => "Keep",
        RouteKind.Market => "Market",
        RouteKind.Vendor => "Vendor",
        RouteKind.GrandCompany => "GrandCompany",
        RouteKind.GoldSaucer => "GoldSaucer",
        RouteKind.Materia => "Materia",
        RouteKind.Use => "Use",
        RouteKind.Armoire => "Armoire",
        RouteKind.Glamour => "Glamour",
        RouteKind.Tracked => "Tracked",
        RouteKind.Armoury => "Armoury",
        RouteKind.Retainer => RetainerId != 0 ? $"{RetainerPrefix}{RetainerId}" : "Retainer",
        _ => "Keep",
    };

    public override string ToString() => ToConfigString();

    public static bool TryParse(string? value, out ItemRoute route)
    {
        route = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.AsSpan().Trim();
        if (span.StartsWith(RetainerPrefix.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            var idSpan = span[RetainerPrefix.Length..];
            if (ulong.TryParse(idSpan, out var retainerId) && retainerId != 0)
            {
                route = ForRetainer(retainerId);
                return true;
            }

            return false;
        }

        if (span.Equals("Keep", StringComparison.OrdinalIgnoreCase)) { route = Keep; return true; }
        if (span.Equals("Market", StringComparison.OrdinalIgnoreCase)) { route = Market; return true; }
        if (span.Equals("Vendor", StringComparison.OrdinalIgnoreCase)) { route = Vendor; return true; }
        if (span.Equals("GrandCompany", StringComparison.OrdinalIgnoreCase)) { route = GrandCompany; return true; }
        if (span.Equals("GoldSaucer", StringComparison.OrdinalIgnoreCase)) { route = GoldSaucer; return true; }
        if (span.Equals("Materia", StringComparison.OrdinalIgnoreCase)) { route = Materia; return true; }
        if (span.Equals("Use", StringComparison.OrdinalIgnoreCase)) { route = Use; return true; }
        if (span.Equals("Armoire", StringComparison.OrdinalIgnoreCase)) { route = Armoire; return true; }
        if (span.Equals("Glamour", StringComparison.OrdinalIgnoreCase)) { route = Glamour; return true; }
        if (span.Equals("Tracked", StringComparison.OrdinalIgnoreCase)) { route = Tracked; return true; }
        if (span.Equals("Armoury", StringComparison.OrdinalIgnoreCase)) { route = Armoury; return true; }
        if (span.Equals("Retainer", StringComparison.OrdinalIgnoreCase)) { route = AnyRetainer; return true; }

        return false;
    }

    public static ItemRoute ParseOrKeep(string? value)
        => TryParse(value, out var route) ? route : Keep;
}

public readonly record struct RouteFilter(ItemRoute Route, bool MatchAnySell = false)
{
    public static readonly RouteFilter AnySell = new(ItemRoute.Market, MatchAnySell: true);

    public static RouteFilter Exact(ItemRoute route) => new(route, MatchAnySell: false);

    public bool Matches(ItemRoute candidate)
    {
        if (MatchAnySell)
        {
            return candidate.IsSell;
        }

        if (Route.Kind != candidate.Kind)
        {
            return false;
        }

        return Route.Kind != RouteKind.Retainer
            || Route.RetainerId == 0
            || candidate.RetainerId == Route.RetainerId;
    }
}
