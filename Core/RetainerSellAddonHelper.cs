using System;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MagicInventory.Core;

public static unsafe class RetainerSellAddonHelper
{
    public static string ReadDisplayedItemText(AddonRetainerSell* sellAddon)
    {
        if (sellAddon == null)
        {
            return string.Empty;
        }

        if (sellAddon->ItemName != null)
        {
            var text = NativeUiHelpers.ReadTextNodeValue(sellAddon->ItemName);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        var fallbackNode = sellAddon->AtkUnitBase.GetTextNodeById(7);
        return fallbackNode != null ? NativeUiHelpers.ReadTextNodeValue(fallbackNode) : string.Empty;
    }

    public static bool MatchesItem(AddonRetainerSell* sellAddon, string itemName, bool expectedHq)
    {
        var raw = ReadDisplayedItemText(sellAddon);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var displayedHq = raw.Contains('\uE03C');
        if (displayedHq != expectedHq)
        {
            return false;
        }

        var cleanDisplayed = NativeUiHelpers.NormalizeItemLabel(raw);
        var cleanExpected = NativeUiHelpers.NormalizeItemLabel(itemName);
        return !string.IsNullOrEmpty(cleanDisplayed)
            && string.Equals(cleanDisplayed, cleanExpected, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryClickComparePrices(AddonRetainerSell* sellAddon)
    {
        if (sellAddon == null)
        {
            return false;
        }

        var compareBtn = sellAddon->ComparePrices != null
            ? sellAddon->ComparePrices
            : sellAddon->AtkUnitBase.GetComponentButtonById(4);
        if (compareBtn == null || !compareBtn->IsEnabled)
        {
            return false;
        }

        return NativeUiHelpers.ClickAddonButton((AtkUnitBase*)sellAddon, compareBtn);
    }

    public static bool TryReadAskingPrice(AddonRetainerSell* sellAddon, out int askingPrice)
    {
        askingPrice = 0;
        if (sellAddon == null || sellAddon->AskingPrice == null)
        {
            return false;
        }

        askingPrice = sellAddon->AskingPrice->Value;
        return askingPrice > 0;
    }
}
