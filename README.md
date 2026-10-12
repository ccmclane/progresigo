# progresigo — RiskEntryPanel for NinjaTrader 8

A NinjaTrader 8 chart add-on (NinjaScript indicator) that adds an order-entry panel to the chart window.
Every Buy/Sell button sends a **market order** with an **OCO stop loss and profit target**. The size is calculated
so that a loss can never exceed a **fixed dollar limit**. Every button can also be triggered with a hotkey.

Source: [`src/Indicators/RiskEntryPanel.cs`](src/Indicators/RiskEntryPanel.cs)

## Install

1. Copy `RiskEntryPanel.cs` to `Documents\NinjaTrader 8\bin\Custom\Indicators\`.
2. In NinjaTrader: **New → NinjaScript Editor**, then press **F5** to compile.
3. Open a chart, go to **Indicators…**, add **RiskEntryPanel** and set its properties.

The panel appears on the left side of the chart. It only shows, and only responds to hotkeys, while its chart tab is selected.

## Buttons

| Button | Long stop | Short stop | Default buy / sell hotkey |
|---|---|---|---|
| Cur Low / Cur High | current bar low − offset | current bar high + offset | `Ctrl+1` / `Ctrl+Shift+1` |
| Prev Low / Prev High | previous bar low − offset | previous bar high + offset | `Ctrl+2` / `Ctrl+Shift+2` |
| Prev Open | previous bar open − offset | previous bar open + offset | `Ctrl+3` / `Ctrl+Shift+3` |
| Fixed 1–5 | entry − N ticks | entry + N ticks | `Ctrl+4…8` / `Ctrl+Shift+4…8` |
| Close *instrument* | cancels orders and closes positions for the chart instrument | | `Ctrl+0` |
| Flatten All | cancels all orders and closes all positions on every instrument | | `Ctrl+Alt+F` |

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

1. The panel calculates the stop, the size and the target. It rejects the order, and shows the reason in the panel and the
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
  - whether Close and Flatten All act on the selected account only (the default) or on all accounts;
  - panel width.
- **Current bar / Previous bar / Previous open:** offset ticks, target multiple, buy hotkey, sell hotkey.
- **Fixed stops 1–5:** enabled, stop ticks, target multiple, buy hotkey, sell hotkey.
- **Close / flatten:** hotkeys.

## Test checklist (use the Sim101 account first)

- [ ] Every Buy and Sell button places a market entry, then a stop and a target at the expected prices and size.
- [ ] Each hotkey triggers the same action as its button. Hotkeys do nothing while you type in a Chart Trader text box,
      or when another chart tab is selected.
- [ ] A stop that is too wide for the loss limit, or on the wrong side of the price, is rejected and no order is sent.
- [ ] **Close** only affects the chart's instrument.
- [ ] **Flatten All** closes positions and cancels orders on every instrument.

Notes:

- On non-time-based bar types such as Renko, the "open" and "low" values may not match real traded prices.
- If a hotkey clashes with a NinjaTrader hotkey, change it in the indicator properties.
