# LWF Economy Graph

Draws income and expense graphs into the stats screen of **Lazy Witch's Factory**.

The stats window already has an empty frame that says `under development` — this mod draws there. Nothing about the game's look or progression is touched, and it only reads numbers the game is already tracking.

![The stats screen with the graph in place](https://raw.githubusercontent.com/KiyonakaNata/lwf-economy-graph/main/img/composition.png)

## What it shows

Four views, switched from the toolbar under the graph.

**Balance per repayment** — one bar per repayment, stacked by what actually paid for it. Hover a bar for the full breakdown of that repayment.

![Breakdown per repayment](https://raw.githubusercontent.com/KiyonakaNata/lwf-economy-graph/main/img/composition-hover.png)

**Factory balance** — the per-minute change of a resource, counting the factory only: mining, production and delivery. One-off changes (relics, staves, land purchases, penalties) are left out, so the line stays about the factory. The moment you go into the red shows up as shape.

![Factory balance](https://raw.githubusercontent.com/KiyonakaNata/lwf-economy-graph/main/img/net-red.png)

**Balance over time** — the stock of each resource, with a vertical line at every repayment. Click the icon at the right end of a line to hide it.

![Balance over time](https://raw.githubusercontent.com/KiyonakaNata/lwf-economy-graph/main/img/balance-all.png)

**Rate** — set the range to the last minute and it turns into a rate chart. It counts the way the game counts (sum over the last 60 seconds), so the right edge of a line matches the number on the game's own card.

![Rate](https://raw.githubusercontent.com/KiyonakaNata/lwf-economy-graph/main/img/rate.png)

## Usage

Just open the stats screen — the graph is already there, and there is nothing to do on each launch.

| Toolbar button | What it does |
|---|---|
| Overlaid lines | All 8 resource balances at once |
| Resource icon | Look at that resource (while overlaid: show / hide it) |
| Hammer | Factory balance of the selected resource |
| Triangle | Income ↔ expense |
| Bar | Whole run ↔ last minute |
| Stack | Breakdown per repayment |

Resource, side and range stay in sync with the game's own stats window.

| Key | Action |
|---|---|
| **F5** | Cycle the view (same as the toolbar) |
| **F8** | Write the record out as CSV (`BepInEx/LwfEconomy/`), for bug reports |

The record is kept in memory only and is gone when you quit the game. Nothing is written to your save.

## Settings

You should not need to touch these. They live in `BepInEx/config/kiyonakanata.lwfeconomygraph.cfg`, created once the game has run.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | `false` stops both recording and drawing |
| `EmbedInStatsWindow` | `true` | `false` draws in a separate panel instead |
| `StartMode` | `0` | View to open with (0 = factory balance, 1 = balance over time, 2 = balance per repayment) |
| `FontName` | empty | Set a font name only if text renders as □ (e.g. `Yu Gothic UI`) |
| `IconOutlineWidth` | `0.05` | Width of the white outline on icons (`0` for none) |

## Requirements

Built and checked on **Lazy Witch's Factory ver 0.24.1** with **BepInEx 5.4.23.5**. A game update can stop the graph from showing up; if it stops working, remove the mod.

## When it doesn't work

Look at `BepInEx/LogOutput.log` first. The mod logs in Japanese, so the lines below are quoted as they appear.

| Log | Where you stand |
|---|---|
| no `[boot] LWF Economy Graph ...` line | **Not loaded** — check where the DLL ended up |
| `統計窓の ... が見つからない` | **The graph works** — but the toolbar won't stay in sync with the game's window |
| `土地購入を挟めなかった` | **The graph works** — but land purchases land under "other" |
| `例外が 20 回続いたので、このMODを止めました` | **Stopped itself** — the game behaves as if the mod were absent |

A bug report is most useful with a screenshot, `BepInEx/LogOutput.log`, and the CSV from F8. Please open an issue on [GitHub](https://github.com/KiyonakaNata/lwf-economy-graph).

## Disclaimer

Unofficial mod, not supported by the developer. Anything that happens with it installed — bugs, crashes, broken saves — is at your own risk, and a game update can break it.

Built in line with the [official stance on mods](https://store.steampowered.com/news/app/3971650/view/699897618302503133) (Japanese).

The source is MIT licensed and lives on [GitHub](https://github.com/KiyonakaNata/lwf-economy-graph). The screenshots are captures of the game and belong to its developer.
