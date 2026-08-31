# LWF Economy Graph

Draws income and expense graphs into the Statistics screen of **Lazy Witch's Factory**.

![The Statistics screen with the graph in place](https://raw.githubusercontent.com/KiyonakaNata/lwf-economy-graph/main/img/composition.png)

## What it shows

Four views, switched from the toolbar under the graph.

**Balance per repayment** — one bar per repayment, stacked by what paid for it. Hover a bar for the full breakdown of that repayment.

![Breakdown per repayment](https://raw.githubusercontent.com/KiyonakaNata/lwf-economy-graph/main/img/composition-hover.png)

**Factory balance** — the per-minute change of a resource, counting `Production` and `Sell Off` only. One-off changes (relics, wands, land purchases, penalties) are left out.

![Factory balance](https://raw.githubusercontent.com/KiyonakaNata/lwf-economy-graph/main/img/net-red.png)

**Balance over time** — the stock of each resource, with a vertical line at every repayment. Click the icon at the right end of a line to hide it.

![Balance over time](https://raw.githubusercontent.com/KiyonakaNata/lwf-economy-graph/main/img/balance-all.png)

**Rate** — set the range to `Past 1 Minute` for a rate chart.

![Rate](https://raw.githubusercontent.com/KiyonakaNata/lwf-economy-graph/main/img/rate.png)

## Usage

| Toolbar button | What it does |
|---|---|
| Overlaid lines | All 8 resource balances at once |
| Resource icon | Look at that resource (while overlaid: show / hide it) |
| Hammer | Factory balance of the selected resource |
| Triangle | Income ↔ Expenses |
| Bar | Whole Game ↔ Past 1 Minute |
| Stack | Breakdown per repayment |

The toolbar and the game's own tabs stay in sync — change either one.

If the graph shows □ instead of text, set `FontName` (e.g. `Yu Gothic UI`) in `BepInEx/config/kiyonakanata.lwfeconomygraph.cfg`.
