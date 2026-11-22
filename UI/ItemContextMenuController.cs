using System;
using System.Collections.Frozen;
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
    private readonly InventoryRouteOverlay routeOverlay;

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
        InventoryRouteOverlay routeOverlay)
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
        this.routeOverlay = routeOverlay;

        this.contextMenu.OnMenuOpened += OnContextMenuOpened;
    }

    private static readonly FrozenSet<string> ItemBearingContextAddons = new string[]
    {
        "ChatLog",
        "RecipeNote",
        "GatheringNote",
        "ItemSearch",
        "ItemSearchResult",
        "Shop",
        "ShopExchangeItem",
        "ShopExchangeCurrency",
        "GrandCompanyExchange",
        "GrandCompanySupplyList",
        "NeedGreed",
        "Journal",
        "JournalAccept",
        "JournalResult",
        "ContentsInfoDetail",
        "MiragePrismPrismBox",
        "MiragePrismPrismBoxCrystallize",
        "Cabinet",
        "CabinetWithdraw",
        "RetainerSellList",
        "RetainerHistory",
        "Craft",
        "ArmouryBoard",
        "Character",
        "CharacterInspect",
        "Inventory",
        "InventoryLarge",
        "InventoryExpansion",
        "InventoryRetainer",
        "InventoryRetainerLarge",
        "InventoryBuddy",
        "InventoryBuddy2",
        "FreeCompanyChest",
        "HousingGoods",
        "ItemFinder",
        "SubmarinePartsMenu",
        "CompanyCraftRecipeNoteBook",
        "CollectablesShop",
        "SatisfactionSupply",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        uint baseItemId = 0;

        if (args.Target is MenuTargetInventory invTarget && invTarget.TargetItem is { } invItem)
        {
            baseItemId = ItemIdentity.NormalizeBaseItemId(invItem.ItemId);
        }
        else if (args.Target is MenuTargetDefault defaultTarget)
        {
            var isActorTarget = defaultTarget.TargetObjectId != 0
                || defaultTarget.TargetContentId != 0
                || !string.IsNullOrEmpty(defaultTarget.TargetName);
            var isAllowedAddon = !string.IsNullOrEmpty(args.AddonName)
                && ItemBearingContextAddons.Contains(args.AddonName);

            if (!isActorTarget && isAllowedAddon)
            {
                uint rawId = 0;
                var detail = AgentItemDetail.Instance();
                if (detail != null
                    && detail->IsAddonShown()
                    && ItemIdentity.IsValidItemId(detail->ItemId)
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

        var rowCheck = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(baseItemId);
        if (rowCheck is null
            || (!(baseItemId is >= 2 and <= 19) && ItemIdentity.IsCurrencyOrCrystal(baseItemId, rowCheck.Value)))
        {
            return;
        }

        var capturedId = baseItemId;
        var itemName = magicSort.GetItemDisplayName(capturedId);

        var invCtx = args.Target is MenuTargetInventory ? AgentInventoryContext.Instance() : null;
        var openedFromRetainer = (invCtx != null && InventorySafetyEvaluator.IsRetainerContainer(invCtx->TargetInventoryId))
            || string.Equals(args.AddonName, "InventoryRetainer", StringComparison.Ordinal)
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
        var itemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(baseItemId);
        var canTag = ItemIdentity.CanLockOrTagItem(baseItemId, itemRow);

        if (!canTag)
        {
            OpenTrackSubmenu(clickedArgs, baseItemId, "Magic Inventory");
            return;
        }

        var items = new List<IMenuItem>();
        var routeFilters = new List<RouteFilter?>();
        var isManual = magicSort.IsItemOverwritten(baseItemId);

        var currentEntry = triageEngine.ClassifySingleItemOnDemand(baseItemId, queuePriceIfMissing: false);
        var autoEntry = isManual
            ? triageEngine.ClassifySingleItemOnDemand(baseItemId, queuePriceIfMissing: false, ignoreManualOverride: true)
            : currentEntry;

        var currentRoute = currentEntry?.Route ?? ItemRoute.Keep;
        var autoLabel = RoutePresentation.FormatAutoPredictedLabel(autoEntry, out var autoRoute);
        if (openedFromRetainer && materialTracker.IsItemTracked(baseItemId)
            && materialTracker.GetMissingTrackedForRetainerWithdraw().GetValueOrDefault(baseItemId) > 0)
        {
            autoLabel = "Withdraw (Tracked)";
            autoRoute = ItemRoute.Tracked;
        }
        else if (openedFromRetainer && autoRoute.IsKeep)
        {
            var activeRetId = HoldingsService.GetActiveRetainerId();
            var activeRetName = HoldingsService.GetActiveRetainerName();
            if (activeRetId != 0 && !string.IsNullOrWhiteSpace(activeRetName))
            {
                autoLabel = activeRetName;
                autoRoute = ItemRoute.ForRetainer(activeRetId);
            }
        }

        items.Add(new MenuItem
        {
            Name = isManual ? $"Auto ({autoLabel})" : $"• Auto ({autoLabel})",
            OnClicked = _ =>
            {
                magicSort.SetItemOverride(baseItemId, null);
                chatGui.Print($"Set {itemName} -> Auto ({autoLabel}).");
            },
        });
        routeFilters.Add(RouteFilter.Exact(autoRoute));

        var isKeepActive = isManual && currentRoute.IsKeep;
        items.Add(new MenuItem
        {
            Name = isKeepActive ? "• Keep" : "Keep",
            OnClicked = _ => ApplyContextOverride(baseItemId, itemName, ItemRoute.Keep, null, "Keep"),
        });
        routeFilters.Add(RouteFilter.Exact(ItemRoute.Keep));

        var eligibility = ItemRouteEligibility.Compute(itemRow, triageEngine.IsCabinetItem(baseItemId));

        if (eligibility.CanUse)
        {
            var isUseActive = isManual && currentRoute.Kind == RouteKind.Use;
            items.Add(new MenuItem
            {
                Name = isUseActive ? "• Use" : "Use",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, ItemRoute.Use, null, "Use"),
            });
            routeFilters.Add(RouteFilter.Exact(ItemRoute.Use));
        }

        var retainers = holdings.GetKnownRetainersForCurrentCharacter();
        if (retainers.Count > 0)
        {
            var isStoreActive = isManual && currentRoute.IsRetainer;
            items.Add(new MenuItem
            {
                Name = isStoreActive ? "• Store" : "Store",
                IsSubmenu = true,
                OnClicked = subArgs => OpenStoreSubmenu(subArgs, baseItemId, itemName, isManual, currentRoute.RetainerId),
            });
            routeFilters.Add(RouteFilter.Exact(ItemRoute.AnyRetainer));
        }

        if (eligibility.CanSellAny)
        {
            var isSellActive = isManual && currentRoute.IsSell;
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
            routeFilters.Add(RouteFilter.AnySell);
        }

        if (eligibility.CanGlamour)
        {
            var isGlamActive = isManual && currentRoute.Kind == RouteKind.Glamour;
            items.Add(new MenuItem
            {
                Name = isGlamActive ? "• Glamour Dresser" : "Glamour Dresser",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, ItemRoute.Glamour, null, "Glamour Dresser"),
            });
            routeFilters.Add(RouteFilter.Exact(ItemRoute.Glamour));
        }

        if (eligibility.CanArmoire)
        {
            var isArmoireActive = isManual && currentRoute.Kind == RouteKind.Armoire;
            items.Add(new MenuItem
            {
                Name = isArmoireActive ? "• Armoire" : "Armoire",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, ItemRoute.Armoire, null, "Armoire"),
            });
            routeFilters.Add(RouteFilter.Exact(ItemRoute.Armoire));
        }

        items.Add(new MenuItem
        {
            Name = "Track",
            IsSubmenu = true,
            OnClicked = subArgs => OpenTrackSubmenu(subArgs, baseItemId, "Track"),
        });
        routeFilters.Add(null);

        routeOverlay.SetActiveContextSubmenuRoutes("Magic Inventory", routeFilters);
        clickedArgs.OpenSubmenu("Magic Inventory", items);
    }

    private void OpenStoreSubmenu(IMenuItemClickedArgs clickedArgs, uint baseItemId, string itemName, bool isManual, ulong currentRetainerId)
    {
        var items = new List<IMenuItem>();
        var routeFilters = new List<RouteFilter?>();

        foreach (var ret in holdings.GetKnownRetainersForCurrentCharacter())
        {
            var retId = ret.RetainerId;
            var retName = ret.Name;
            var retRoute = ItemRoute.ForRetainer(retId);
            var isActive = isManual && currentRetainerId == retId;
            items.Add(new MenuItem
            {
                Name = isActive ? $"• {retName}" : retName,
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, retRoute, null, retName),
            });
            routeFilters.Add(RouteFilter.Exact(retRoute));
        }

        routeOverlay.SetActiveContextSubmenuRoutes("Store", routeFilters);
        clickedArgs.OpenSubmenu("Store", items);
    }

    private void OpenSellSubmenu(
        IMenuItemClickedArgs clickedArgs,
        uint baseItemId,
        string itemName,
        bool isManual,
        ItemRoute currentRoute,
        ItemRouteEligibility eligibility)
    {
        var items = new List<IMenuItem>();
        var routeFilters = new List<RouteFilter?>();

        if (eligibility.CanMarket)
        {
            var isMarket = isManual && currentRoute.Kind == RouteKind.Market;
            var prio = safety.GetMarketPriority(baseItemId);

            items.Add(new MenuItem
            {
                Name = isMarket && prio > 0 ? "• Market (High Priority)" : "Market (High Priority)",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, ItemRoute.Market, 1, "Market (High Priority)"),
            });
            routeFilters.Add(RouteFilter.Exact(ItemRoute.Market));

            items.Add(new MenuItem
            {
                Name = isMarket && prio == 0 ? "• Market" : "Market",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, ItemRoute.Market, 0, "Market"),
            });
            routeFilters.Add(RouteFilter.Exact(ItemRoute.Market));

            items.Add(new MenuItem
            {
                Name = isMarket && prio < 0 ? "• Market (Low Priority)" : "Market (Low Priority)",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, ItemRoute.Market, -1, "Market (Low Priority)"),
            });
            routeFilters.Add(RouteFilter.Exact(ItemRoute.Market));
        }

        if (eligibility.CanVendor)
        {
            var isVendor = isManual && currentRoute.Kind == RouteKind.Vendor;
            items.Add(new MenuItem
            {
                Name = isVendor ? "• Vendor" : "Vendor",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, ItemRoute.Vendor, null, "Vendor"),
            });
            routeFilters.Add(RouteFilter.Exact(ItemRoute.Vendor));
        }

        if (eligibility.CanGrandCompany)
        {
            var isGc = isManual && currentRoute.Kind == RouteKind.GrandCompany;
            items.Add(new MenuItem
            {
                Name = isGc ? "• GC Seals" : "GC Seals",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, ItemRoute.GrandCompany, null, "GC Seals"),
            });
            routeFilters.Add(RouteFilter.Exact(ItemRoute.GrandCompany));
        }

        if (eligibility.CanGoldSaucer)
        {
            var isGs = isManual && currentRoute.Kind == RouteKind.GoldSaucer;
            items.Add(new MenuItem
            {
                Name = isGs ? "• Gold Saucer" : "Gold Saucer",
                OnClicked = _ => ApplyContextOverride(baseItemId, itemName, ItemRoute.GoldSaucer, null, "Gold Saucer"),
            });
            routeFilters.Add(RouteFilter.Exact(ItemRoute.GoldSaucer));
        }

        routeOverlay.SetActiveContextSubmenuRoutes("Sell", routeFilters);
        clickedArgs.OpenSubmenu("Sell", items);
    }

    private void OpenTrackSubmenu(IMenuItemClickedArgs clickedArgs, uint baseItemId, string submenuTitle)
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

        var showUntrackRecipe = false;
        if (materialTracker.HasRecipeForItem(baseItemId))
        {
            if (materialTracker.IsTrackedRecipeResultItem(baseItemId))
            {
                showUntrackRecipe = true;
                items.Add(new MenuItem
                {
                    Name = "Untrack Recipe",
                    OnClicked = _ =>
                    {
                        materialTracker.ClearAll();
                        chatGui.Print("Cleared tracked recipe.");
                    },
                });
            }
            else
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
        }

        if (hasTrackedList && !showUntrackRecipe)
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

        routeOverlay.SetActiveContextSubmenuRoutes(submenuTitle, Array.Empty<RouteFilter?>());
        clickedArgs.OpenSubmenu(submenuTitle, items);
    }

    private void PrintIfNonEmpty(Func<string> action)
    {
        var msg = action();
        if (!string.IsNullOrWhiteSpace(msg))
        {
            chatGui.Print(msg);
        }
    }

    private void ApplyContextOverride(uint baseItemId, string itemName, ItemRoute route, int? marketPriority, string displayLabel)
    {
        magicSort.SetItemOverride(baseItemId, route, marketPriority);
        chatGui.Print($"Set {itemName} -> {displayLabel}.");
    }

    public void Dispose()
    {
        contextMenu.OnMenuOpened -= OnContextMenuOpened;
    }
}
