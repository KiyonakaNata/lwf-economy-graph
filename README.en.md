[日本語](README.md)

# LWF Economy Graph

Draws income and expense graphs into the Statistics screen of **Lazy Witch's Factory**

![Income breakdown](img/composition.png)

**[Download the latest release](https://github.com/KiyonakaNata/lwf-economy-graph/releases/latest)** / **[Thunderstore](https://thunderstore.io/c/lazy-witchs-factory/p/KiyonakaNata/LwfEconomyGraph/)** (one-click install with a mod manager)

---

## What it shows

Four views, switched from the toolbar under the graph

### Balance per repayment

- One bar per repayment
- Stacked by what paid for it
- Hover a bar for the breakdown of that repayment

![Breakdown on hover](img/composition-hover.png)

Resources work the same way, not just cash (left: income, right: expense)

<p>
  <img src="img/composition-fuel-income.png" width="49%" alt="Fuel income">
  <img src="img/composition-fuel-expense.png" width="49%" alt="Fuel expense">
</p>

### Factory balance

- The per-minute change of each resource
- Counts **`Production` and `Sell Off` only**
- One-off changes (relics, wands, land purchases, penalties) are left out

<p>
  <img src="img/net.png" width="49%" alt="Factory running">
  <img src="img/net-red.png" width="49%" alt="Gone into the red">
</p>

(left: nothing wrong / right: heading for a fuel shortage)

### Balance over time

- The stock of each resource
- A vertical line marks every repayment
- **Click the icon at the right end of a line to hide it**
  - The resource icons on the toolbar do the same, and bring hidden lines back

<p>
  <img src="img/balance-all.png" width="49%" alt="All lines">
  <img src="img/balance-hidden.png" width="49%" alt="Some lines hidden">
</p>

(left: all / right: some hidden)

### Rate

Set the range to `Past 1 Minute` for a rate chart

![Rate over the past minute](img/rate.png)

---

## Install (by hand)

1. Install **BepInEx 5** — [downloads](https://github.com/BepInEx/BepInEx/releases)
   - Get `BepInEx_win_x64_5.4.x.zip`
   - Extract it into the game folder (the one that holds `LazyWitchsFactory.exe`)

     > **Where the game folder is** (Steam)
     > Right-click the game in your library → Manage → Browse local files

2. Run the game once and quit, so that `BepInEx/plugins` and the rest are created
3. Take `LwfEconomyGraph.dll` out of this mod's zip and put it in **`BepInEx/plugins/`**
4. Start the game, open the Statistics screen, and check that the graph is there

## Uninstall

**This mod only**

- `BepInEx/plugins/LwfEconomyGraph.dll`
- To clear its settings and CSVs as well: `BepInEx/config/kiyonakanata.lwfeconomygraph.cfg` and `BepInEx/LwfEconomy/`

**BepInEx along with it** (every other mod stops too)

- The `BepInEx` folder
- `winhttp.dll`, `doorstop_config.ini` and `.doorstop_version` in the game folder

---

## Usage

![Toolbar](img/toolbar.png)

| Toolbar button | What it does |
|---|---|
| Overlaid lines | All 8 resource balances at once |
| Resource icon | Look at that resource (while overlaid: show / hide it) |
| Hammer | Factory balance of the selected resource |
| Triangle | Income ↔ Expenses |
| Bar | Whole Game ↔ Past 1 Minute |
| Stack | Breakdown per repayment |

The toolbar and the game's own Statistics tabs stay in sync

| Key | Action |
|---|---|
| **F5** | Cycle the view |
| **F8** | Write the record out as CSV (`BepInEx/LwfEconomy/`) |

The record is gone when you quit the game or start the next run

---

## Settings

`BepInEx/config/kiyonakanata.lwfeconomygraph.cfg` (created once the game has run)

| Key | Default | Values |
|---|---|---|
| `Enabled` | `true` | `false` stops both recording and drawing |
| `EmbedInStatsWindow` | `true` | `false` draws in a separate panel |
| `StartMode` | `0` | 0 = factory balance / 1 = balance over time / 2 = balance per repayment |
| `FontName` | empty | Set this only if text renders as □ (e.g. `Yu Gothic UI`) |
| `IconOutlineWidth` | `0.05` | Width of the white outline on icons (`0` for none) |

---

## Requirements

| | |
|---|---|
| Lazy Witch's Factory | built and checked on **ver 0.24.1** |
| BepInEx | checked with **5.4.23.5** (any 5.4.x should do) |

If a game update stops it from working, that is the end of this mod's life — remove it

## When it doesn't work

Look at `BepInEx/LogOutput.log` first. The mod logs in Japanese, so the lines below are quoted as they appear

| Log | Where you stand |
|---|---|
| no `[boot] LWF Economy Graph ...` line | **Not loaded** — check where the DLL ended up |
| `統計窓の ... が見つからない` | **The graph works** — the toolbar won't stay in sync with the game's window |
| `土地購入を挟めなかった` | **The graph works** — land purchases land under "Other" |
| `例外が 20 回続いたので、このMODを止めました` | **The mod stopped itself** — restart the game |

**A bug report** needs these three

- a screenshot
- `BepInEx/LogOutput.log`
- the CSV from F8

---

## Disclaimer

- **Unofficial mod** — not supported by the developer
- Bugs, crashes and broken saves with the mod installed are at your own risk
- Built in line with the [official stance on mods](https://store.steampowered.com/news/app/3971650/view/699897618302503133) (Japanese)

The source is MIT licensed. The screenshots under `img/` are captures of the game and belong to its developer
