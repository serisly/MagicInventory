using System;
using System.Collections.Generic;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using MagicInventory.Core;
using MagicInventory.Services;
using MagicInventory.Services.Triage;

namespace MagicInventory.UI;

/// <summary>
/// Owns all right-click "Magic Inventory" context menu injection and submenus
/// (Auto/Keep/Use/Store/Sell/Glamour/Armoire/Track) across inventories, retainers,
/// quests, journals, shops, loot, and chat links.
/// </summary>
public sealed unsafe class ItemContextMenuController : IDisposable
{
    private readonly IContextMenu contextMenu;
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly IChatGui chatGui;
    private readonly HoldingsService holdings;
    private readonly InventorySafetyEvaluator safety;
    private readonly ItemTriageEngine triageEngine;
    private readonly MagicSortService magicSort;
    private readonly MaterialTrackerService materialTracker;
    private readonly InventoryHighlightController highlightController;

    public ItemContextMenuController(
        IContextMenu contextMenu,
        IGameGui gameGui,
        IDataManager dataManager,
        IChatGui chatGui,
        HoldingsService holdings,
        InventorySafetyEvaluator safety,
        ItemTriageEngine triageEngine,
        MagicSortService magicSort,
        MaterialTrackerService materialTracker,
        InventoryHighlightController highlightController)
    {
        this.contextMenu = contextMenu;
        this.gameGui = gameGui;
        this.dataManager = dataManager;
        this.chatGui = chatGui;
        this.holdings = holdings;
        this.safety = safety;
        this.triageEngine = triageEngine;
        this.magicSort = magicSort;
        this.materialTracker = materialTracker;
        this.highlightController = highlightController;

        this.contextMenu.OnMenuOpened += OnContextMenuOpened;
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        uint baseItemId = 0;

        if (args.Target is MenuTargetInventory invTarget && invTarget.TargetItem is { } invItem)
        {
            baseItemId = ItemIdentity.NormalizeBaseItemId(invItem.ItemId);
            if (baseItemId == 0)
            {
                baseItemId = invItem.BaseItemId;
            }
        }
        else if (args.Target is MenuTargetDefault defaultTarget)
        {
            // Never attach item menus to right-clicks on players, NPCs, or world objects
            var isActorTarget = defaultTarget.TargetObjectId != 0
                || defaultTarget.TargetContentId != 0
                || !string.IsNullOrEmpty(defaultTarget.TargetName);

            if (!isActorTarget)
            {
                uint rawId = 0;
                var detail = AgentItemDetail.Instance();
                if (detail != null
                    && detail->ItemId != 0
                    && detail->DetailKind < DetailKind.Action)
                {
                    rawId = detail->ItemId;
                }
                else if (ItemIdentity.IsValidItemId(gameGui.HoveredItem))
                {
                    rawId = (uint)gameGui.HoveredItem;
                }

                if (rawId != 0)
                {
                    var normalized = ItemIdentity.NormalizeBaseItemId(rawId);
                    if (normalized != 0 && dataManager.GetExcelSheet<Item>()?.TryGetRow(normalized, out _) == true)
                    {
                        baseItemId = normalized;
                    }
                }
            }
        }

        if (baseItemId == 0)
        {
            return;
        }

        var capturedId = baseItemId;
        var itemName = magicSort.GetItemDisplayName(capturedId);
        var openedFromRetainer = string.Equals(args.AddonName, "InventoryRetainer", StringComparison.Ordinal)
            || string.Equals(args.AddonName, "InventoryRetainerLarge", StringComparison.Ordinal);

        args.AddMenuItem(new MenuItem
        {
            Name = "Magic Inventory",
            UseDefaultPrefix = true,
            IsSubmenu = true,
            OnClicked = clickedArgs => OpenItemRuleSubmenu(clickedArgs, capturedId, itemName, openedFromRetainer),
        });
    }

    private void OpenItemRuleSubmenu(IMenuItemClickedArgs clickedArgs, uint baseItemId, string itemName, bool openedFromRetainer)
    {
        var items = new List<IMenuItem>();
        var routeFilters = new List<string?>();

        var itemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(baseItemId);
        var canTag = ItemIdentity.CanLockOrTagItem(baseItemId, itemRow);
        var isManual = magicSort.IsItemOverwritten(baseItemId);

        if (canTag)
        {
            var currentEntry = triageEngine.ClassifySingleItemOnDemand(baseItemId, queuePriceIfMissing: false);
            var autoEntry = isManual
                ? triageEngine.ClassifySingleItemOnDemand(baseItemId, queuePriceIfMissing: false, ignoreManualOverride: true)
                : currentEntry;

            var currentRoute = currentEntry?.Route ?? "Keep";
            var autoLabel = RoutePresentation.FormatAutoPredictedLabel(autoEntry, out var autoRouteFilter);
            if (openedFromRetainer && string.Equals(autoRouteFilter, "Keep", StringComparison.OrdinalIgnoreCase))
            {
                var activeRetId = HoldingsService.GetActiveRetainerId();
                var activeRetName = HoldingsService.GetActiveRetainerName();
                if (activeRetId != 0 && !string.IsNullOrWhiteSpace(activeRetName))
                {
                    autoLabel = activeRetName;
                    autoRouteFilter = $"Retainer:{activeRetId}";
                }
            }

            items.Add(new MenuItem
            {
                Name = isManual ? $"Auto ({autoLabel})" : $"• Auto ({autoLabel})",
                OnClicked = _ =>
                {
                    magicSort.SetItemOverride(baseItemId, string.Empty);
                    chatGui.Print($"Set {itemName} -> Auto ({autoLabel}).");
                },
            });
            routeFilters.Add(autoRouteFilter);

            var isKeepActive = isManual && string.Equals(currentRoute, "Keep", StringComparison.OrdinalIgnoreCase);
            items.Add(new MenuItem
            {
                Name = isKeepActive ? "• Keep" : "Keep",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Keep", null, "Keep"),
            });
            routeFilters.Add("Keep");

            var eligibility = ItemRouteEligibility.Compute(itemRow, triageEngine.IsCabinetItem(baseItemId));

            if (eligibility.CanUse)
            {
                var isUseActive = isManual && string.Equals(currentRoute, "Use", StringComparison.OrdinalIgnoreCase);
                items.Add(new MenuItem
                {
                    Name = isUseActive ? "• Use" : "Use",
                    OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Use", null, "Use"),
                });
                routeFilters.Add("Use");
            }

            var retainers = holdings.GetKnownRetainersForCurrentCharacter();
            if (retainers.Count > 0)
            {
                var isStoreActive = isManual && string.Equals(currentRoute, "Retainer", StringComparison.OrdinalIgnoreCase);
                items.Add(new MenuItem
                {
                    Name = isStoreActive ? "• Store" : "Store",
                    IsSubmenu = true,
                    OnClicked = subArgs => OpenStoreSubmenu(subArgs, baseItemId, itemName, isManual, currentEntry?.TargetRetainerId ?? 0UL),
                });
                routeFilters.Add("Retainer");
            }

            if (eligibility.CanSellAny)
            {
                var isSellActive = isManual
                    && (string.Equals(currentRoute, "Market", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(currentRoute, "Vendor", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(currentRoute, "GrandCompany", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(currentRoute, "GoldSaucer", StringComparison.OrdinalIgnoreCase));
                items.Add(new MenuItem
                {
                    Name = isSellActive ? "• Sell" : "Sell",
                    IsSubmenu = true,
                    OnClicked = subArgs => OpenSellSubmenu(
                        subArgs,
                        baseItemId,
                        itemName,
                        isManual,
                        currentRoute,
                        eligibility),
                });
                routeFilters.Add("Sell");
            }

            if (eligibility.CanGlamour)
            {
                var isGlamActive = isManual && string.Equals(currentRoute, "Glamour", StringComparison.OrdinalIgnoreCase);
                items.Add(new MenuItem
                {
                    Name = isGlamActive ? "• Glamour Dresser" : "Glamour Dresser",
                    OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Glamour", null, "Glamour Dresser"),
                });
                routeFilters.Add("Glamour");
            }

            if (eligibility.CanArmoire)
            {
                var isArmoireActive = isManual && string.Equals(currentRoute, "Armoire", StringComparison.OrdinalIgnoreCase);
                items.Add(new MenuItem
                {
                    Name = isArmoireActive ? "• Armoire" : "Armoire",
                    OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Armoire", null, "Armoire"),
                });
                routeFilters.Add("Armoire");
            }
        }

        items.Add(new MenuItem
        {
            Name = "Track",
            IsSubmenu = true,
            OnClicked = subArgs => OpenTrackSubmenu(subArgs, baseItemId),
        });
        routeFilters.Add(null);

        highlightController.SetActiveContextSubmenuRoutes("Magic Inventory", routeFilters);
        clickedArgs.OpenSubmenu("Magic Inventory", items);
    }

    private void OpenStoreSubmenu(IMenuItemClickedArgs clickedArgs, uint baseItemId, string itemName, bool isManual, ulong currentRetainerId)
    {
        var items = new List<IMenuItem>();
        var routeFilters = new List<string?>();

        foreach (var ret in holdings.GetKnownRetainersForCurrentCharacter())
        {
            var retId = ret.RetainerId;
            var retName = ret.Name;
            var isActive = isManual && currentRetainerId == retId;
            items.Add(new MenuItem
            {
                Name = isActive ? $"• {retName}" : retName,
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, $"Retainer:{retId}", null, retName),
            });
            routeFilters.Add($"Retainer:{retId}");
        }

        highlightController.SetActiveContextSubmenuRoutes("Store", routeFilters);
        clickedArgs.OpenSubmenu("Store", items);
    }

    private void OpenSellSubmenu(
        IMenuItemClickedArgs clickedArgs,
        uint baseItemId,
        string itemName,
        bool isManual,
        string currentRoute,
        ItemRouteEligibility eligibility)
    {
        var items = new List<IMenuItem>();
        var routeFilters = new List<string?>();

        if (eligibility.CanMarket)
        {
            var isMarket = isManual && string.Equals(currentRoute, "Market", StringComparison.OrdinalIgnoreCase);
            var prio = safety.GetMarketPriority(baseItemId);

            items.Add(new MenuItem
            {
                Name = isMarket && prio > 0 ? "• Market (High Priority)" : "Market (High Priority)",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Market", 1, "Market (High Priority)"),
            });
            routeFilters.Add("Market");

            items.Add(new MenuItem
            {
                Name = isMarket && prio == 0 ? "• Market" : "Market",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Market", 0, "Market"),
            });
            routeFilters.Add("Market");

            items.Add(new MenuItem
            {
                Name = isMarket && prio < 0 ? "• Market (Low Priority)" : "Market (Low Priority)",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Market", -1, "Market (Low Priority)"),
            });
            routeFilters.Add("Market");
        }

        if (eligibility.CanVendor)
        {
            var isVendor = isManual && string.Equals(currentRoute, "Vendor", StringComparison.OrdinalIgnoreCase);
            items.Add(new MenuItem
            {
                Name = isVendor ? "• Vendor" : "Vendor",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "Vendor", null, "Vendor"),
            });
            routeFilters.Add("Vendor");
        }

        if (eligibility.CanGrandCompany)
        {
            var isGc = isManual && string.Equals(currentRoute, "GrandCompany", StringComparison.OrdinalIgnoreCase);
            items.Add(new MenuItem
            {
                Name = isGc ? "• GC Seals" : "GC Seals",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "GrandCompany", null, "GC Seals"),
            });
            routeFilters.Add("GrandCompany");
        }

        if (eligibility.CanGoldSaucer)
        {
            var isGs = isManual && string.Equals(currentRoute, "GoldSaucer", StringComparison.OrdinalIgnoreCase);
            items.Add(new MenuItem
            {
                Name = isGs ? "• Gold Saucer" : "Gold Saucer",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, "GoldSaucer", null, "Gold Saucer"),
            });
            routeFilters.Add("GoldSaucer");
        }

        highlightController.SetActiveContextSubmenuRoutes("Sell", routeFilters);
        clickedArgs.OpenSubmenu("Sell", items);
    }

    private void OpenTrackSubmenu(IMenuItemClickedArgs clickedArgs, uint baseItemId)
    {
        var items = new List<IMenuItem>();
        var isItemTracked = materialTracker.IsItemTracked(baseItemId);
        var hasTrackedList = materialTracker.HasTrackedItems;

        if (!isItemTracked)
        {
            items.Add(new MenuItem
            {
                Name = hasTrackedList ? "Add to List" : "Track Item",
                OnClicked = _ => PrintIfNonEmpty(() => { materialTracker.AddItemToList(baseItemId, out var msg); return msg; }),
            });

            if (hasTrackedList)
            {
                items.Add(new MenuItem
                {
                    Name = "Track Only",
                    OnClicked = _ => PrintIfNonEmpty(() => { materialTracker.TrackItemOnly(baseItemId, out var msg); return msg; }),
                });
            }
        }
        else
        {
            items.Add(new MenuItem
            {
                Name = "Remove from List",
                OnClicked = _ => PrintIfNonEmpty(() => { materialTracker.RemoveItemFromList(baseItemId, out var msg); return msg; }),
            });

            items.Add(new MenuItem
            {
                Name = "Add +1 to List",
                OnClicked = _ => PrintIfNonEmpty(() => { materialTracker.AddItemToList(baseItemId, out var msg); return msg; }),
            });

            if (materialTracker.TrackedItemCount > 1)
            {
                items.Add(new MenuItem
                {
                    Name = "Track Only",
                    OnClicked = _ => PrintIfNonEmpty(() => { materialTracker.TrackItemOnly(baseItemId, out var msg); return msg; }),
                });
            }
        }

        if (materialTracker.HasRecipeForItem(baseItemId))
        {
            items.Add(new MenuItem
            {
                Name = hasTrackedList ? "Add Recipe to List" : "Track Recipe",
                OnClicked = _ => PrintIfNonEmpty(() => { materialTracker.AddRecipeToList(baseItemId, out var msg); return msg; }),
            });

            if (hasTrackedList)
            {
                items.Add(new MenuItem
                {
                    Name = "Track Recipe Only",
                    OnClicked = _ => PrintIfNonEmpty(() => { materialTracker.TrackRecipeForResultItem(baseItemId, out var msg); return msg; }),
                });
            }
        }

        if (hasTrackedList)
        {
            items.Add(new MenuItem
            {
                Name = "Clear Tracked List",
                OnClicked = _ =>
                {
                    materialTracker.ClearAll();
                    chatGui.Print("Cleared tracked list.");
                },
            });
        }

        highlightController.SetActiveContextSubmenuRoutes(null, null);
        clickedArgs.OpenSubmenu("Track", items);
    }

    private void PrintIfNonEmpty(Func<string> action)
    {
        var msg = action();
        if (!string.IsNullOrWhiteSpace(msg))
        {
            chatGui.Print(msg);
        }
    }

    private void ApplyContextOverride(uint baseItemId, string itemName, string routeSpec, int? marketPriority, string displayLabel)
    {
        magicSort.SetItemOverride(baseItemId, routeSpec, marketPriority);
        chatGui.Print($"Set {itemName} -> {displayLabel}.");
    }

    public void Dispose()
    {
        contextMenu.OnMenuOpened -= OnContextMenuOpened;
    }
}
