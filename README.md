# Magic Inventory

Automatic inventory organizer for Final Fantasy XIV (Dalamud API 15). Zero external scripts or companion plugins required.

## Install (Dalamud Custom Repository)
1. Open `/xlsettings` in FFXIV → **Experimental** tab.
2. Under **Custom Plugin Repositories**, add:
   ```
   https://raw.githubusercontent.com/serisly/MagicInventory/main/repo.json
   ```
3. Click **Save**, open `/xlplugins`, search **Magic Inventory**, and install.

## Features
- **Retainer Sorting (`/m sort`):** Deposits bag items to the right retainer, funnels misplaced specialty items across retainers, pulls exact quantities for tracked recipes, and overflows seamlessly when a retainer is full.
- **Selling (`/m sell`):** Sells junk at Vendors, turns in outgeared dungeon gear for Grand Company Seals, and lists profitable surplus on the Market Board with priority ordering and live price checks.
- **3 Sell Modes:** Choose **Safe**, **Balanced**, or **Clear Space** in `/m` Settings. Rare items with 0 Market Board listings, food, potions, gearsets, glamour, housing, relics, and locked items stay safe.
- **Right-Click Overrides:** Right-click any item to pin it to Bag, Use, Market (High / Normal / Low priority), Vendor, Grand Company, or a specific Retainer. Pinned overrides are never wiped by resetting settings.
- **Use All (`/m use`):** Adds a `Use All` button in your Inventory for unowned unlockables, coffers, and MGP cards.
- **Recipe Tracking & Market Buying (`/m track <recipe> [qty]`, `/m buy`):** Track recipes by command or from the Crafting Log, switch to the required crafter job or start Quick Synthesis from the tracker overlay, and buy missing materials one click at a time at the Market Board.

## Commands
- `/m` or `/magic` — Open settings & item browser
- `/m sort` — Sort retainers at a Summoning Bell or extract Materia
- `/m sell` — Sell at a Vendor, Grand Company, or Summoning Bell
- `/m buy` — Buy tracked recipe materials at a Market Board (1 item per click)
- `/m track <recipe> [qty]` — Track a crafting recipe (e.g. `/m track fish soup 5`, `/m track clear`)
- `/m use` — Use unlockables and coffers in your bags
- `/m scan` — Refresh Home World Market Board prices
- `/m stop` — Stop any active action immediately
