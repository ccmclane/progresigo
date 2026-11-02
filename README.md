# progresigo — RiskEntryPanel for NinjaTrader 8

A NinjaTrader 8 chart add-on (NinjaScript indicator) that adds order-entry buttons to the chart's Chart Trader panel.
Every Buy/Sell button sends a **market order** with an **OCO stop loss and profit target**. The size is calculated
so that a loss can never exceed a **fixed dollar limit**. Every button can also be triggered with a hotkey.

Source: [`src/Indicators/RiskEntryPanel.cs`](src/Indicators/RiskEntryPanel.cs)

## Install

1. Copy `RiskEntryPanel.cs` to `Documents\NinjaTrader 8\bin\Custom\Indicators\`.
2. In NinjaTrader: **New → NinjaScript Editor**, then press **F5** to compile.
3. Open a chart, go to **Indicators…**, add **RiskEntryPanel** and set its properties.

The buttons appear at the bottom of **Chart Trader**, with Buy buttons on the left and Sell buttons on the right.
Chart Trader must be set to **Visible** or **Hidden** (right-click the chart → Chart Trader). If it is **Disabled**, there is
nowhere to put the buttons, but the hotkeys still work. If you turn Chart Trader on after adding the indicator, switch tabs
or reload the chart (F5) to show the buttons. The buttons only show, and the hotkeys only work, while the indicator's
chart tab is selected.

Hover over a button to see what it does, its stop offset, its target multiple and its hotkey.

## Buttons

| Buy button (left) | Sell button (right) | Long stop | Short stop | Default buy / sell hotkey |
|---|---|---|---|---|
| Buy CL | Sell CH | current candle low − offset | current candle high + offset | `Ctrl+1` / `Ctrl+Shift+1` |
| Buy PL | Sell PH | previous candle low − offset | previous candle high + offset | `Ctrl+2` / `Ctrl+Shift+2` |
| Buy PO | Sell PO | previous candle open − offset | previous candle open + offset | `Ctrl+3` / `Ctrl+Shift+3` |
| Buy -N | Sell +N | entry − N ticks | entry + N ticks | `Ctrl+4…8` / `Ctrl+Shift+4…8` |
| Close *instrument* | Flatten All | Close cancels orders and closes positions for the chart instrument | Flatten All cancels all orders and closes all positions on every instrument | `Ctrl+0` / `Ctrl+Alt+F` |

N is the stop size in ticks of each of the five fixed-stop buttons (for example `Buy -10`, `Sell +10`).

The profit target is set at **multiple × stop size** (in ticks) from the entry. Each button has its own offset or stop ticks,
target multiple and hotkeys. You can turn off any of the five fixed-stop buttons. Hotkeys use the format `Ctrl+Shift+B`,
`Alt+F5` or `F9`. Leave a hotkey empty to disable it.

## Position sizing

```
stop ticks  = ceil(|reference price − stop price| / tick size)
contracts   = floor(max loss $ / (stop ticks × tick size × point value))   (capped by "Max contracts")
target      = reference price ± round(stop ticks × multiple) ticks
```

The reference price is the best ask for a buy and the best bid for a sell. If there is no quote, the last price is used.
Example for ES (tick $12.50) with a $500 limit:

- 10-tick stop → 4 contracts (risk $500).
- 41-tick stop → rejected, because one contract would risk $512.50.

## What happens when you click

1. The panel calculates the stop, the size and the target. It rejects the order, and shows the reason in the status line and the
   Output window, if:
   - there is no real-time data;
   - the stop would be on the wrong side of the price (for example, a buy where the previous bar's open is above the current price);
   - even 1 contract would exceed the loss limit;
   - a position in the opposite direction is already open.
2. A market order goes to the account selected in **Chart Trader**. If Chart Trader is not available, it goes to the
   **Fallback account** property.
3. When the entry fills, the panel submits an OCO pair: a stop-market order and a limit target, both GTC.
   - Candle-based stops stay at the candle price. The target is re-measured from the actual fill price.
   - Fixed-tick stops are measured from the actual fill price.
   - If a partial fill happens, each filled part gets its own bracket.
   - If the fill slipped past the candle stop, the position is closed at market straight away.

Because the size is calculated before the fill, slippage on the market order can make the actual loss slightly larger than the limit.

## Settings

- **Risk:**
  - max loss per trade ($);
  - max contracts;
  - fallback account;
  - confirmation dialogs for entries, Close and Flatten All (Flatten All asks for confirmation by default);
  - whether Close and Flatten All act on the selected account only (the default) or on all accounts.
- **Current bar / Previous bar / Previous open:** offset ticks, target multiple, buy hotkey, sell hotkey.
- **Fixed stops 1–5:** enabled, stop ticks, target multiple, buy hotkey, sell hotkey.
- **Close / flatten:** hotkeys.

## Test checklist (use the Sim101 account first)

- [ ] The buttons appear in Chart Trader, Buy on the left and Sell on the right, and their tooltips show the hotkeys.
- [ ] With the indicator on charts in two tabs of one window, switching tabs shows the buttons for the selected tab's instrument.
- [ ] Every Buy and Sell button places a market entry, then a stop and a target at the expected prices and size.
- [ ] Each hotkey triggers the same action as its button. Hotkeys do nothing while you type in a Chart Trader text box,
      or when another chart tab is selected.
- [ ] A stop that is too wide for the loss limit, or on the wrong side of the price, is rejected and no order is sent.
- [ ] **Close** only affects the chart's instrument.
- [ ] **Flatten All** closes positions and cancels orders on every instrument.

Notes:

- On non-time-based bar types such as Renko, the "open" and "low" values may not match real traded prices.
- If a hotkey clashes with a NinjaTrader hotkey, change it in the indicator properties.
