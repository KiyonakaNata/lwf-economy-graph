# LWF Economy Graph

Draws income and expense graphs into the Statistics screen of **Lazy Witch's Factory**.

![The Statistics screen with the graph in place](https://raw.githubusercontent.com/KiyonakaNata/lwf-economy-graph/main/img/composition.png)

## What it shows

Four views, switched from the toolbar under the graph.

**Balance per repayment** — one bar per repayment, stacked by what actually paid for it. Hover a bar for the full breakdown of that repayment.

![Breakdown per repayment](https://raw.githubusercontent.com/KiyonakaNata/lwf-economy-graph/main/img/composition-hover.png)

**Factory balance** — the per-minute change of a resource, counting mining, production and deliveries only. One-off changes (relics, wands, land purchases, penalties) are left out. The moment you go into the red shows up as shape.

![Factory balance](https://raw.githubusercontent.com/KiyonakaNata/lwf-economy-graph/main/img/net-red.png)

**Balance over time** — the stock of each resource, with a vertical line at every repayment. Click the icon at the right end of a line to hide it.

![Balance over time](https://raw.githubusercontent.com/KiyonakaNata/lwf-economy-graph/main/img/balance-all.png)

**Rate** — set the range to Past 1 Minute and it turns into a rate chart.

![Rate](https://raw.githubusercontent.com/KiyonakaNata/lwf-economy-graph/main/img/rate.png)

## Usage

| Toolbar button | What it does |
|---|---|
| Overlaid lines | All 8 resource balances at once |
| Resource icon | Look at that resource (while overlaid: show / hide it) |
| Hammer | Factory balance of the selected resource |
| Triangle | Income ↔ Expenses |
| Bar | Whole run ↔ Past 1 Minute |
| Stack | Breakdown per repayment |

Resource, side and range stay in sync with the game's own Statistics tabs.

## Settings

You should not need to touch these. They live in `BepInEx/config/kiyonakanata.lwfeconomygraph.cfg`, created once the game has run.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | `false` stops both recording and drawing |
| `EmbedInStatsWindow` | `true` | `false` draws in a separate panel instead |
| `StartMode` | `0` | View to open with (0 = factory balance, 1 = balance over time, 2 = balance per repayment) |
| `FontName` | empty | Set a font name only if text renders as □ (e.g. `Yu Gothic UI`) |
| `IconOutlineWidth` | `0.05` | Width of the white outline on icons (`0` for none) |
