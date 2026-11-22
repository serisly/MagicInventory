# Magic Inventory

Personal FFXIV plugin (Dalamud API 15) I made to cut down on bag and retainer cleanup. Everything works inside the game's own windows and stops if you move or close them.

## Install
1. Open `/xlsettings` -> **Experimental**.
2. Add this URL under **Custom Plugin Repositories**:
   ```
   https://raw.githubusercontent.com/serisly/MagicInventory/main/repo.json
   ```
3. Save, open `/xlplugins`, and install **Magic Inventory**.

## Features
Type `/m` to pick a sell preset (**Safe**, **Balanced**, or **Clear Space**) and adjust thresholds. Right-click any item in your bags or retainers to pin a custom rule for it.

- **Retainer sorting:** Deposits bag items to matching retainers and pulls mats you need for tracked recipes.
- **Selling & Market Board:** Sells junk to vendors, turns in outgeared drops for GC Seals, and checks Universalis sale history before prepping Market Board listings. Review mode is on by default so you check the price before confirming, and stale or dead listings show a prompt next to your active listings.
- **Storage & unlockables:** Stores eligible gear in your Armoire or Glamour Dresser, extracts 100% spiritbond materia, and adds a `Use All` button for untradable unowned unlockables and MGP cards.
- **Recipe tracker:** Track recipes from `/m track` or the Crafting Log, start Quick Synthesis from the overlay, and buy missing mats one click at a time at a Market Board.

## Commands
- `/m` or `/magic`: open settings and item browser
- `/m sort`: sort bags, retainers, or extract materia
- `/m sell`: sell at a vendor, Grand Company, or Summoning Bell
- `/m buy`: buy tracked recipe mats at a Market Board
- `/m track <recipe> [qty]`: track a recipe (e.g. `/m track fish soup 5`, `/m track clear`)
- `/m use`: use untradable unowned unlockables and MGP cards
- `/m scan`: refresh Home World Market Board prices
- `/m stop`: stop whatever is running
