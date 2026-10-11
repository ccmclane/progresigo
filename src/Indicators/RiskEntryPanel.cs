#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;
#endregion

// RiskEntryPanel
// ---------------------------------------------------------------------------
// Chart-attached order entry panel. Each Buy/Sell button sends a market order
// sized so that (stop distance in ticks * dollars per tick * contracts) never
// exceeds the configured dollar loss limit, then attaches an OCO stop loss and
// profit target as soon as the entry fills. Every button has a configurable
// hotkey. Orders go to the account selected in the chart's Chart Trader, or to
// the fallback account when Chart Trader is not available.
// ---------------------------------------------------------------------------

namespace NinjaTrader.NinjaScript.Indicators
{
	public class RiskEntryPanel : Indicator
	{
		#region Nested types

		public enum StopAnchor
		{
			CurrentBarExtreme,	// below current low (buy) / above current high (sell)
			PreviousBarExtreme,	// below previous low (buy) / above previous high (sell)
			PreviousBarOpen,	// below / above previous bar open
			FixedTicks			// fixed number of ticks from entry
		}

		public struct BarSnapshot
		{
			public double CurHigh;
			public double CurLow;
			public double PrevHigh;
			public double PrevLow;
			public double PrevOpen;
			public double LastClose;
			public bool Valid;
		}

		public class TradePlan
		{
			public string Error;
			public double ReferencePrice;
			public double StopPrice;
			public double TargetPrice;
			public int StopTicks;
			public int TargetTicks;
			public int Quantity;
			public double RiskDollars;
		}

		// Pure sizing / price math, kept free of NinjaTrader state so it is easy to reason about.
		public static class RiskMath
		{
			public static double RoundToTick(double price, double tickSize)
			{
				return Math.Round(Math.Round(price / tickSize, MidpointRounding.AwayFromZero) * tickSize, 10);
			}

			public static TradePlan BuildPlan(bool isLong, StopAnchor anchor, int ticks, double multiple, BarSnapshot bars,
				double referencePrice, double tickSize, double tickValue, double maxLossDollars, int maxContracts)
			{
				TradePlan plan = new TradePlan { ReferencePrice = referencePrice };

				if (tickSize <= 0 || tickValue <= 0)
					return Fail(plan, "Instrument tick size / point value unavailable");
				if (referencePrice <= 0)
					return Fail(plan, "No current price available");
				if (multiple <= 0)
					return Fail(plan, "Profit target multiple must be greater than zero");
				if (ticks < 0)
					return Fail(plan, "Tick offset cannot be negative");
				if (maxLossDollars <= 0)
					return Fail(plan, "Loss limit must be greater than zero");
				if (anchor != StopAnchor.FixedTicks && !bars.Valid)
					return Fail(plan, "Need at least two bars on the chart");

				double offset = ticks * tickSize;
				double stop;
				switch (anchor)
				{
					case StopAnchor.CurrentBarExtreme:
						stop = isLong ? bars.CurLow - offset : bars.CurHigh + offset;
						break;
					case StopAnchor.PreviousBarExtreme:
						stop = isLong ? bars.PrevLow - offset : bars.PrevHigh + offset;
						break;
					case StopAnchor.PreviousBarOpen:
						stop = isLong ? bars.PrevOpen - offset : bars.PrevOpen + offset;
						break;
					default:
						stop = isLong ? referencePrice - offset : referencePrice + offset;
						break;
				}
				stop = RoundToTick(stop, tickSize);

				double distance = isLong ? referencePrice - stop : stop - referencePrice;
				// Ceiling so a fractional tick (e.g. reference between ticks) is counted against the risk budget.
				int stopTicks = (int)Math.Ceiling(distance / tickSize - 1e-6);
				if (stopTicks < 1)
					return Fail(plan, string.Format(CultureInfo.InvariantCulture, "Stop {0} is not {1} entry price {2}",
						stop, isLong ? "below" : "above", referencePrice));

				double riskPerContract = stopTicks * tickValue;
				int quantity = (int)Math.Floor(maxLossDollars / riskPerContract + 1e-9);
				if (maxContracts > 0)
					quantity = Math.Min(quantity, maxContracts);
				if (quantity < 1)
					return Fail(plan, string.Format(CultureInfo.InvariantCulture,
						"{0}-tick stop risks ${1:0.00} per contract, above the ${2:0.00} limit", stopTicks, riskPerContract, maxLossDollars));

				int targetTicks = Math.Max(1, (int)Math.Round(stopTicks * multiple, MidpointRounding.AwayFromZero));

				plan.StopPrice = stop;
				plan.StopTicks = stopTicks;
				plan.TargetTicks = targetTicks;
				plan.TargetPrice = RoundToTick(isLong ? referencePrice + targetTicks * tickSize : referencePrice - targetTicks * tickSize, tickSize);
				plan.Quantity = quantity;
				plan.RiskDollars = quantity * riskPerContract;
				return plan;
			}

			private static TradePlan Fail(TradePlan plan, string error)
			{
				plan.Error = error;
				return plan;
			}
		}

		private class EntrySpec
		{
			public string Label;
			public StopAnchor Anchor;
			public int Ticks;
			public double Multiple;
			public string BuyHotkey;
			public string SellHotkey;
		}

		private class PendingEntry
		{
			public Account Account;
			public Instrument Instrument;
			public bool IsLong;
			public double StopPrice;	// fixed at click time from candle prices and ask/bid
			public double TargetPrice;	// fixed at click time; the fill price never moves it
			public int BracketedQuantity;
		}

		private class HotkeyBinding
		{
			public Key Key;
			public ModifierKeys Modifiers;
			public Action Action;
		}

		// Lists account names in the property grid.
		public class AccountNameListConverter : StringConverter
		{
			public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
			public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return false; }

			public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
			{
				List<string> names = new List<string>();
				lock (Account.All)
					names.AddRange(Account.All.Select(a => a.Name));
				return new StandardValuesCollection(names);
			}
		}

		#endregion

		#region Fields

		private readonly object barLock = new object();
		private BarSnapshot bars;
		private double lastAsk;
		private double lastBid;
		private volatile bool isRealtime;

		private readonly object pendingLock = new object();
		private readonly Dictionary<Order, PendingEntry> pendingEntries = new Dictionary<Order, PendingEntry>();
		private readonly HashSet<Order> bracketOrders = new HashSet<Order>();
		private readonly HashSet<Account> subscribedAccounts = new HashSet<Account>();

		private Chart chartWindow;
		private Grid chartGrid;
		private ColumnDefinition panelColumn;
		private Border panelRoot;
		private TextBlock statusText;
		private bool panelAdded;
		private readonly List<HotkeyBinding> hotkeys = new List<HotkeyBinding>();

		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Order entry buttons with candle-based stop loss, R-multiple target and fixed dollar risk sizing.";
				Name = "RiskEntryPanel";
				Calculate = Calculate.OnPriceChange;
				IsOverlay = true;
				DisplayInDataBox = false;
				DrawOnPricePanel = true;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = false;
				ScaleJustification = ScaleJustification.Right;

				MaxLossDollars = 500;
				MaxContracts = 20;
				FallbackAccountName = "Sim101";
				ConfirmEntries = false;
				ConfirmClose = false;
				ConfirmFlattenAll = true;
				CloseInAllAccounts = false;
				PanelWidth = 190;

				CurBarOffsetTicks = 1;
				CurBarMultiple = 2;
				CurBarBuyHotkey = "Ctrl+1";
				CurBarSellHotkey = "Ctrl+Shift+1";

				PrevBarOffsetTicks = 1;
				PrevBarMultiple = 2;
				PrevBarBuyHotkey = "Ctrl+2";
				PrevBarSellHotkey = "Ctrl+Shift+2";

				PrevOpenOffsetTicks = 1;
				PrevOpenMultiple = 2;
				PrevOpenBuyHotkey = "Ctrl+3";
				PrevOpenSellHotkey = "Ctrl+Shift+3";

				Fixed1Enabled = true; Fixed1StopTicks = 4;  Fixed1Multiple = 2; Fixed1BuyHotkey = "Ctrl+4"; Fixed1SellHotkey = "Ctrl+Shift+4";
				Fixed2Enabled = true; Fixed2StopTicks = 8;  Fixed2Multiple = 2; Fixed2BuyHotkey = "Ctrl+5"; Fixed2SellHotkey = "Ctrl+Shift+5";
				Fixed3Enabled = true; Fixed3StopTicks = 12; Fixed3Multiple = 2; Fixed3BuyHotkey = "Ctrl+6"; Fixed3SellHotkey = "Ctrl+Shift+6";
				Fixed4Enabled = true; Fixed4StopTicks = 16; Fixed4Multiple = 2; Fixed4BuyHotkey = "Ctrl+7"; Fixed4SellHotkey = "Ctrl+Shift+7";
				Fixed5Enabled = true; Fixed5StopTicks = 20; Fixed5Multiple = 2; Fixed5BuyHotkey = "Ctrl+8"; Fixed5SellHotkey = "Ctrl+Shift+8";

				CloseHotkey = "Ctrl+0";
				FlattenAllHotkey = "Ctrl+Alt+F";
			}
			else if (State == State.Historical)
			{
				if (ChartControl != null && !panelAdded)
					ChartControl.Dispatcher.InvokeAsync(CreatePanel);
			}
			else if (State == State.Realtime)
			{
				isRealtime = true;
				SetStatus("Ready");
			}
			else if (State == State.Terminated)
			{
				isRealtime = false;

				lock (pendingLock)
				{
					foreach (Account account in subscribedAccounts)
						account.OrderUpdate -= OnAccountOrderUpdate;
					subscribedAccounts.Clear();
					pendingEntries.Clear();
					bracketOrders.Clear();
				}

				if (ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(RemovePanel);
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0 || CurrentBar < 1)
				return;

			lock (barLock)
			{
				bars.CurHigh = High[0];
				bars.CurLow = Low[0];
				bars.PrevHigh = High[1];
				bars.PrevLow = Low[1];
				bars.PrevOpen = Open[1];
				bars.LastClose = Close[0];
				bars.Valid = true;
			}
		}

		protected override void OnMarketData(MarketDataEventArgs marketDataUpdate)
		{
			lock (barLock)
			{
				if (marketDataUpdate.MarketDataType == MarketDataType.Ask)
					lastAsk = marketDataUpdate.Price;
				else if (marketDataUpdate.MarketDataType == MarketDataType.Bid)
					lastBid = marketDataUpdate.Price;
			}
		}

		#region Entries

		private IEnumerable<EntrySpec> BuildEntrySpecs()
		{
			yield return new EntrySpec { Label = "Cur Bar", Anchor = StopAnchor.CurrentBarExtreme, Ticks = CurBarOffsetTicks, Multiple = CurBarMultiple, BuyHotkey = CurBarBuyHotkey, SellHotkey = CurBarSellHotkey };
			yield return new EntrySpec { Label = "Prev Bar", Anchor = StopAnchor.PreviousBarExtreme, Ticks = PrevBarOffsetTicks, Multiple = PrevBarMultiple, BuyHotkey = PrevBarBuyHotkey, SellHotkey = PrevBarSellHotkey };
			yield return new EntrySpec { Label = "Prev Open", Anchor = StopAnchor.PreviousBarOpen, Ticks = PrevOpenOffsetTicks, Multiple = PrevOpenMultiple, BuyHotkey = PrevOpenBuyHotkey, SellHotkey = PrevOpenSellHotkey };

			if (Fixed1Enabled) yield return FixedSpec(Fixed1StopTicks, Fixed1Multiple, Fixed1BuyHotkey, Fixed1SellHotkey);
			if (Fixed2Enabled) yield return FixedSpec(Fixed2StopTicks, Fixed2Multiple, Fixed2BuyHotkey, Fixed2SellHotkey);
			if (Fixed3Enabled) yield return FixedSpec(Fixed3StopTicks, Fixed3Multiple, Fixed3BuyHotkey, Fixed3SellHotkey);
			if (Fixed4Enabled) yield return FixedSpec(Fixed4StopTicks, Fixed4Multiple, Fixed4BuyHotkey, Fixed4SellHotkey);
			if (Fixed5Enabled) yield return FixedSpec(Fixed5StopTicks, Fixed5Multiple, Fixed5BuyHotkey, Fixed5SellHotkey);
		}

		private static EntrySpec FixedSpec(int stopTicks, double multiple, string buyHotkey, string sellHotkey)
		{
			return new EntrySpec { Label = stopTicks + "t", Anchor = StopAnchor.FixedTicks, Ticks = stopTicks, Multiple = multiple, BuyHotkey = buyHotkey, SellHotkey = sellHotkey };
		}

		// Runs on the UI thread (button click or hotkey).
		private void ExecuteEntry(EntrySpec spec, bool isLong)
		{
			string side = isLong ? "BUY" : "SELL";

			if (!isRealtime)
			{
				SetStatus("Waiting for real-time data");
				return;
			}

			BarSnapshot snapshot;
			double referencePrice;
			lock (barLock)
			{
				snapshot = bars;
				double quote = isLong ? lastAsk : lastBid;
				referencePrice = quote > 0 ? quote : bars.LastClose;
			}

			double tickSize = Instrument.MasterInstrument.TickSize;
			double tickValue = tickSize * Instrument.MasterInstrument.PointValue;

			TradePlan plan = RiskMath.BuildPlan(isLong, spec.Anchor, spec.Ticks, spec.Multiple, snapshot, referencePrice,
				tickSize, tickValue, MaxLossDollars, MaxContracts);
			if (plan.Error != null)
			{
				Report(string.Format("{0} {1} rejected: {2}", side, spec.Label, plan.Error), true);
				return;
			}

			Account account = ResolveAccount();
			if (account == null)
			{
				Report("No account: select one in Chart Trader or set the fallback account", true);
				return;
			}

			MarketPosition existing = GetMarketPosition(account, Instrument);
			if ((isLong && existing == MarketPosition.Short) || (!isLong && existing == MarketPosition.Long))
			{
				Report(string.Format("{0} rejected: an opposite {1} position is open in {2}; close it first", side, existing, account.Name), true);
				return;
			}

			string summary = string.Format(CultureInfo.InvariantCulture,
				"{0} {1} {2} @ ~{3}  stop {4} ({5}t)  target {6} ({7}t)  risk ${8:0.00}  [{9}]",
				side, plan.Quantity, Instrument.FullName, Format(plan.ReferencePrice), Format(plan.StopPrice), plan.StopTicks,
				Format(plan.TargetPrice), plan.TargetTicks, plan.RiskDollars, account.Name);

			if (ConfirmEntries && !Confirm(summary, "Confirm entry"))
				return;

			SubscribeAccount(account);

			Order entry = account.CreateOrder(Instrument, isLong ? OrderAction.Buy : OrderAction.SellShort, OrderType.Market,
				OrderEntry.Manual, TimeInForce.Day, plan.Quantity, 0, 0, string.Empty, "RiskEntry", Core.Globals.MaxDate, null);

			// Register before submitting so a fast fill cannot arrive before we are tracking it.
			lock (pendingLock)
				pendingEntries[entry] = new PendingEntry
				{
					Account = account,
					Instrument = Instrument,
					IsLong = isLong,
					StopPrice = plan.StopPrice,
					TargetPrice = plan.TargetPrice
				};

			account.Submit(new[] { entry });
			Report(summary, false);
		}

		// Runs on the account event thread.
		private void OnAccountOrderUpdate(object sender, OrderEventArgs e)
		{
			PendingEntry pending;
			int newlyFilled = 0;
			bool isBracketOrder;

			lock (pendingLock)
			{
				isBracketOrder = bracketOrders.Contains(e.Order);
				if (isBracketOrder && Order.IsTerminalState(e.OrderState))
					bracketOrders.Remove(e.Order);
			}

			if (isBracketOrder)
			{
				if (e.OrderState == OrderState.Rejected)
					Report(string.Format("{0} REJECTED ({1}) - position may be unprotected, manage it manually", e.Order.Name, e.Comment), true);
				return;
			}

			lock (pendingLock)
			{
				if (!pendingEntries.TryGetValue(e.Order, out pending))
					return;

				if (e.OrderState == OrderState.Filled || e.OrderState == OrderState.PartFilled)
				{
					newlyFilled = e.Filled - pending.BracketedQuantity;
					if (newlyFilled > 0)
						pending.BracketedQuantity = e.Filled;
				}

				if (Order.IsTerminalState(e.OrderState))
					pendingEntries.Remove(e.Order);
			}

			if (newlyFilled > 0)
				SubmitBracket(pending, newlyFilled, e.AverageFillPrice);
			else if (e.OrderState == OrderState.Rejected || e.OrderState == OrderState.Cancelled)
				Report(string.Format("Entry {0}: {1}", e.OrderState, e.Comment), true);
		}

		// Stop and target were fixed at click time; slippage on the entry fill does not move them
		// and never triggers an automatic exit. fillPrice is used for reporting only.
		private void SubmitBracket(PendingEntry pending, int quantity, double fillPrice)
		{
			try
			{
				OrderAction exitAction = pending.IsLong ? OrderAction.Sell : OrderAction.BuyToCover;
				string oco = "RE" + Guid.NewGuid().ToString("N");
				Order stop = pending.Account.CreateOrder(pending.Instrument, exitAction, OrderType.StopMarket, OrderEntry.Manual,
					TimeInForce.Gtc, quantity, 0, pending.StopPrice, oco, "RiskEntry Stop", Core.Globals.MaxDate, null);
				Order target = pending.Account.CreateOrder(pending.Instrument, exitAction, OrderType.Limit, OrderEntry.Manual,
					TimeInForce.Gtc, quantity, pending.TargetPrice, 0, oco, "RiskEntry Target", Core.Globals.MaxDate, null);

				lock (pendingLock)
				{
					bracketOrders.Add(stop);
					bracketOrders.Add(target);
				}
				pending.Account.Submit(new[] { stop, target });

				Report(string.Format("Filled {0} at {1}; stop {2}, target {3}", quantity, Format(fillPrice),
					Format(pending.StopPrice), Format(pending.TargetPrice)), false);
			}
			catch (Exception ex)
			{
				Report("Failed to place stop/target: " + ex.Message, true);
			}
		}

		#endregion

		#region Close / flatten

		private void CloseInstrument()
		{
			List<Account> accounts = TargetAccounts();
			if (accounts.Count == 0)
			{
				Report("No account to close", true);
				return;
			}
			if (ConfirmClose && !Confirm("Close all positions and orders for " + Instrument.FullName + "?", "Close instrument"))
				return;

			foreach (Account account in accounts)
			{
				try
				{
					account.Flatten(new[] { Instrument });
				}
				catch (Exception ex)
				{
					Report("Close failed in " + account.Name + ": " + ex.Message, true);
				}
			}
			ForgetPending(Instrument);
			Report("Closed " + Instrument.FullName + " in " + string.Join(", ", accounts.Select(a => a.Name)), false);
		}

		private void FlattenAll()
		{
			List<Account> accounts = TargetAccounts();
			if (accounts.Count == 0)
			{
				Report("No account to flatten", true);
				return;
			}
			if (ConfirmFlattenAll && !Confirm("Flatten ALL positions and cancel all orders in " + string.Join(", ", accounts.Select(a => a.Name)) + "?", "Flatten all"))
				return;

			foreach (Account account in accounts)
			{
				HashSet<Instrument> instruments = new HashSet<Instrument>();
				lock (account.Positions)
					foreach (Position position in account.Positions)
						if (position.MarketPosition != MarketPosition.Flat)
							instruments.Add(position.Instrument);
				lock (account.Orders)
					foreach (Order order in account.Orders)
						if (!Order.IsTerminalState(order.OrderState))
							instruments.Add(order.Instrument);

				if (instruments.Count == 0)
					continue;

				try
				{
					account.Flatten(instruments);
				}
				catch (Exception ex)
				{
					Report("Flatten failed in " + account.Name + ": " + ex.Message, true);
				}
			}
			ForgetPending(null);
			Report("Flattened all in " + string.Join(", ", accounts.Select(a => a.Name)), false);
		}

		// Stops tracking entries that were just flattened so a late fill does not re-attach a bracket.
		private void ForgetPending(Instrument instrument)
		{
			lock (pendingLock)
				foreach (Order order in pendingEntries.Where(p => instrument == null || p.Value.Instrument == instrument).Select(p => p.Key).ToList())
					pendingEntries.Remove(order);
		}

		private List<Account> TargetAccounts()
		{
			if (CloseInAllAccounts)
				lock (Account.All)
					return Account.All.ToList();

			Account account = ResolveAccount();
			return account == null ? new List<Account>() : new List<Account> { account };
		}

		#endregion

		#region Account helpers

		private Account ResolveAccount()
		{
			if (chartWindow != null)
			{
				AccountSelector selector = chartWindow.FindFirst("ChartTraderControlAccountSelector") as AccountSelector;
				if (selector != null && selector.SelectedAccount != null)
					return selector.SelectedAccount;
			}

			lock (Account.All)
				return Account.All.FirstOrDefault(a => a.Name == FallbackAccountName);
		}

		private void SubscribeAccount(Account account)
		{
			lock (pendingLock)
				if (subscribedAccounts.Add(account))
					account.OrderUpdate += OnAccountOrderUpdate;
		}

		private static MarketPosition GetMarketPosition(Account account, Instrument instrument)
		{
			lock (account.Positions)
			{
				Position position = account.Positions.FirstOrDefault(p => p.Instrument == instrument);
				return position == null ? MarketPosition.Flat : position.MarketPosition;
			}
		}

		#endregion

		#region UI

		private void CreatePanel()
		{
			if (panelAdded)
				return;

			chartWindow = Window.GetWindow(ChartControl.Parent) as Chart;
			if (chartWindow == null)
				return;

			chartGrid = chartWindow.MainTabControl.Parent as Grid;
			if (chartGrid == null)
				return;

			StackPanel stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(4) };

			stack.Children.Add(new TextBlock
			{
				Text = string.Format(CultureInfo.InvariantCulture, "Risk ${0:0.##}  max {1}", MaxLossDollars, MaxContracts),
				Foreground = Brushes.White,
				FontWeight = FontWeights.Bold,
				Margin = new Thickness(0, 0, 0, 4)
			});

			List<EntrySpec> specs = BuildEntrySpecs().ToList();
			Brush buyBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x7F, 0x3B));
			Brush sellBrush = new SolidColorBrush(Color.FromRgb(0xA8, 0x23, 0x23));
			Brush neutralBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x55));
			Brush dangerBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x00, 0x00));
			buyBrush.Freeze(); sellBrush.Freeze(); neutralBrush.Freeze(); dangerBrush.Freeze();

			hotkeys.Clear();
			AddSection(stack, "BUY");
			foreach (EntrySpec spec in specs)
			{
				EntrySpec captured = spec;
				string text = string.Format(CultureInfo.InvariantCulture, "Buy {0}  -{1}t  {2}R", BuyLabel(spec), spec.Ticks, spec.Multiple);
				stack.Children.Add(MakeButton(text, spec.BuyHotkey, buyBrush, () => ExecuteEntry(captured, true)));
			}

			AddSection(stack, "SELL");
			foreach (EntrySpec spec in specs)
			{
				EntrySpec captured = spec;
				string text = string.Format(CultureInfo.InvariantCulture, "Sell {0}  +{1}t  {2}R", SellLabel(spec), spec.Ticks, spec.Multiple);
				stack.Children.Add(MakeButton(text, spec.SellHotkey, sellBrush, () => ExecuteEntry(captured, false)));
			}

			AddSection(stack, "EXIT");
			stack.Children.Add(MakeButton("Close " + Instrument.MasterInstrument.Name, CloseHotkey, neutralBrush, CloseInstrument));
			stack.Children.Add(MakeButton("Flatten All", FlattenAllHotkey, dangerBrush, FlattenAll));

			statusText = new TextBlock
			{
				Text = isRealtime ? "Ready" : "Waiting for real-time data",
				Foreground = Brushes.LightGray,
				TextWrapping = TextWrapping.Wrap,
				FontSize = 11,
				Margin = new Thickness(0, 6, 0, 0)
			};
			stack.Children.Add(statusText);

			panelRoot = new Border
			{
				Width = PanelWidth,
				Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x24)),
				Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack }
			};

			// Insert a new column to the left of the chart tabs and shift existing content right.
			int tabColumn = Grid.GetColumn(chartWindow.MainTabControl);
			panelColumn = new ColumnDefinition { Width = GridLength.Auto };
			chartGrid.ColumnDefinitions.Insert(tabColumn, panelColumn);
			foreach (UIElement child in chartGrid.Children)
				if (Grid.GetColumn(child) >= tabColumn)
					Grid.SetColumn(child, Grid.GetColumn(child) + 1);

			Grid.SetColumn(panelRoot, tabColumn);
			Grid.SetRow(panelRoot, Grid.GetRow(chartWindow.MainTabControl));
			Grid.SetRowSpan(panelRoot, Math.Max(1, Grid.GetRowSpan(chartWindow.MainTabControl)));
			chartGrid.Children.Add(panelRoot);

			chartWindow.PreviewKeyDown += OnChartPreviewKeyDown;
			chartWindow.MainTabControl.SelectionChanged += OnTabSelectionChanged;
			panelAdded = true;
			UpdatePanelVisibility();
		}

		private void RemovePanel()
		{
			if (!panelAdded)
				return;

			if (chartWindow != null)
			{
				chartWindow.PreviewKeyDown -= OnChartPreviewKeyDown;
				chartWindow.MainTabControl.SelectionChanged -= OnTabSelectionChanged;
			}

			if (chartGrid != null)
			{
				chartGrid.Children.Remove(panelRoot);
				int index = chartGrid.ColumnDefinitions.IndexOf(panelColumn);
				if (index >= 0)
				{
					chartGrid.ColumnDefinitions.RemoveAt(index);
					foreach (UIElement child in chartGrid.Children)
						if (Grid.GetColumn(child) > index)
							Grid.SetColumn(child, Grid.GetColumn(child) - 1);
				}
			}

			hotkeys.Clear();
			panelRoot = null;
			statusText = null;
			chartGrid = null;
			chartWindow = null;
			panelAdded = false;
		}

		private static string BuyLabel(EntrySpec spec)
		{
			switch (spec.Anchor)
			{
				case StopAnchor.CurrentBarExtreme: return "Cur Low";
				case StopAnchor.PreviousBarExtreme: return "Prev Low";
				case StopAnchor.PreviousBarOpen: return "Prev Open";
				default: return "Fixed";
			}
		}

		private static string SellLabel(EntrySpec spec)
		{
			switch (spec.Anchor)
			{
				case StopAnchor.CurrentBarExtreme: return "Cur High";
				case StopAnchor.PreviousBarExtreme: return "Prev High";
				case StopAnchor.PreviousBarOpen: return "Prev Open";
				default: return "Fixed";
			}
		}

		private static void AddSection(StackPanel stack, string title)
		{
			stack.Children.Add(new TextBlock
			{
				Text = title,
				Foreground = Brushes.Gray,
				FontSize = 10,
				Margin = new Thickness(0, 6, 0, 2)
			});
		}

		private Button MakeButton(string text, string hotkeyText, Brush background, Action action)
		{
			HotkeyBinding binding = ParseHotkey(hotkeyText);
			if (binding == null && !string.IsNullOrWhiteSpace(hotkeyText))
				Print("RiskEntryPanel: ignoring unrecognized hotkey '" + hotkeyText + "' for " + text);
			if (binding != null)
			{
				binding.Action = action;
				hotkeys.Add(binding);
			}

			Button button = new Button
			{
				Content = binding != null ? text + "   [" + hotkeyText.Trim() + "]" : text,
				Background = background,
				Foreground = Brushes.White,
				HorizontalContentAlignment = HorizontalAlignment.Left,
				Padding = new Thickness(6, 3, 6, 3),
				Margin = new Thickness(0, 1, 0, 1),
				FontSize = 11,
				Focusable = false
			};
			button.Click += (s, e) => RunSafely(action);
			return button;
		}

		private void OnChartPreviewKeyDown(object sender, KeyEventArgs e)
		{
			if (e.IsRepeat || !IsOurTabSelected() || Keyboard.FocusedElement is TextBox)
				return;

			Key key = e.Key == Key.System ? e.SystemKey : e.Key;
			ModifierKeys modifiers = Keyboard.Modifiers;

			foreach (HotkeyBinding binding in hotkeys)
			{
				if (binding.Key == key && binding.Modifiers == modifiers)
				{
					e.Handled = true;
					RunSafely(binding.Action);
					return;
				}
			}
		}

		private void RunSafely(Action action)
		{
			try
			{
				action();
			}
			catch (Exception ex)
			{
				Report("Error: " + ex.Message, true);
			}
		}

		private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (e.Source == chartWindow.MainTabControl)
				UpdatePanelVisibility();
		}

		private void UpdatePanelVisibility()
		{
			if (panelRoot != null)
				panelRoot.Visibility = IsOurTabSelected() ? Visibility.Visible : Visibility.Collapsed;
		}

		private bool IsOurTabSelected()
		{
			if (chartWindow == null)
				return false;

			TabItem selected = chartWindow.MainTabControl.SelectedItem as TabItem;
			ChartTab chartTab = selected != null ? selected.Content as ChartTab : null;
			return chartTab != null && chartTab.ChartControl == ChartControl;
		}

		private bool Confirm(string message, string caption)
		{
			return NTMessageBoxSimple.Show(chartWindow, message, caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
		}

		// Accepts e.g. "Ctrl+1", "Ctrl+Shift+B", "Alt+F5", "F9". Empty disables the hotkey.
		private static HotkeyBinding ParseHotkey(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
				return null;

			ModifierKeys modifiers = ModifierKeys.None;
			Key key = Key.None;

			foreach (string rawToken in text.Split('+'))
			{
				string token = rawToken.Trim();
				switch (token.ToLowerInvariant())
				{
					case "ctrl":
					case "control":
						modifiers |= ModifierKeys.Control;
						continue;
					case "alt":
						modifiers |= ModifierKeys.Alt;
						continue;
					case "shift":
						modifiers |= ModifierKeys.Shift;
						continue;
					case "win":
					case "windows":
						modifiers |= ModifierKeys.Windows;
						continue;
				}

				if (token.Length == 1 && char.IsDigit(token[0]))
					token = "D" + token;
				if (!Enum.TryParse(token, true, out key))
					return null;
			}

			return key == Key.None ? null : new HotkeyBinding { Key = key, Modifiers = modifiers };
		}

		#endregion

		#region Reporting

		private string Format(double price)
		{
			return Instrument.MasterInstrument.FormatPrice(price);
		}

		private void Report(string message, bool isError)
		{
			Print(string.Format("{0} RiskEntryPanel [{1}]: {2}", DateTime.Now.ToString("HH:mm:ss"), Instrument != null ? Instrument.FullName : "", message));
			if (isError)
				Log("RiskEntryPanel: " + message, LogLevel.Warning);
			SetStatus(message);
		}

		private void SetStatus(string message)
		{
			if (ChartControl == null)
				return;

			ChartControl.Dispatcher.InvokeAsync(() =>
			{
				if (statusText != null)
					statusText.Text = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
			});
		}

		#endregion

		#region Properties

		[Range(1, double.MaxValue)]
		[Display(Name = "Max loss per trade ($)", Order = 1, GroupName = "1. Risk")]
		public double MaxLossDollars { get; set; }

		[Range(0, int.MaxValue)]
		[Display(Name = "Max contracts (0 = no cap)", Order = 2, GroupName = "1. Risk")]
		public int MaxContracts { get; set; }

		[TypeConverter(typeof(AccountNameListConverter))]
		[Display(Name = "Fallback account", Description = "Used when no account is selected in Chart Trader", Order = 3, GroupName = "1. Risk")]
		public string FallbackAccountName { get; set; }

		[Display(Name = "Confirm entries", Order = 4, GroupName = "1. Risk")]
		public bool ConfirmEntries { get; set; }

		[Display(Name = "Confirm close instrument", Order = 5, GroupName = "1. Risk")]
		public bool ConfirmClose { get; set; }

		[Display(Name = "Confirm flatten all", Order = 6, GroupName = "1. Risk")]
		public bool ConfirmFlattenAll { get; set; }

		[Display(Name = "Close / flatten in all accounts", Description = "When off, Close and Flatten All only act on the selected account", Order = 7, GroupName = "1. Risk")]
		public bool CloseInAllAccounts { get; set; }

		[Range(120, 400)]
		[Display(Name = "Panel width", Order = 8, GroupName = "1. Risk")]
		public int PanelWidth { get; set; }

		[Range(0, int.MaxValue)]
		[Display(Name = "Offset ticks", Order = 1, GroupName = "2. Current bar low / high")]
		public int CurBarOffsetTicks { get; set; }

		[Range(0.01, double.MaxValue)]
		[Display(Name = "Target multiple (R)", Order = 2, GroupName = "2. Current bar low / high")]
		public double CurBarMultiple { get; set; }

		[Display(Name = "Buy hotkey", Order = 3, GroupName = "2. Current bar low / high")]
		public string CurBarBuyHotkey { get; set; }

		[Display(Name = "Sell hotkey", Order = 4, GroupName = "2. Current bar low / high")]
		public string CurBarSellHotkey { get; set; }

		[Range(0, int.MaxValue)]
		[Display(Name = "Offset ticks", Order = 1, GroupName = "3. Previous bar low / high")]
		public int PrevBarOffsetTicks { get; set; }

		[Range(0.01, double.MaxValue)]
		[Display(Name = "Target multiple (R)", Order = 2, GroupName = "3. Previous bar low / high")]
		public double PrevBarMultiple { get; set; }

		[Display(Name = "Buy hotkey", Order = 3, GroupName = "3. Previous bar low / high")]
		public string PrevBarBuyHotkey { get; set; }

		[Display(Name = "Sell hotkey", Order = 4, GroupName = "3. Previous bar low / high")]
		public string PrevBarSellHotkey { get; set; }

		[Range(0, int.MaxValue)]
		[Display(Name = "Offset ticks", Order = 1, GroupName = "4. Previous bar open")]
		public int PrevOpenOffsetTicks { get; set; }

		[Range(0.01, double.MaxValue)]
		[Display(Name = "Target multiple (R)", Order = 2, GroupName = "4. Previous bar open")]
		public double PrevOpenMultiple { get; set; }

		[Display(Name = "Buy hotkey", Order = 3, GroupName = "4. Previous bar open")]
		public string PrevOpenBuyHotkey { get; set; }

		[Display(Name = "Sell hotkey", Order = 4, GroupName = "4. Previous bar open")]
		public string PrevOpenSellHotkey { get; set; }

		[Display(Name = "Fixed 1 enabled", Order = 1, GroupName = "5. Fixed stops")]
		public bool Fixed1Enabled { get; set; }
		[Range(1, int.MaxValue)]
		[Display(Name = "Fixed 1 stop ticks", Order = 2, GroupName = "5. Fixed stops")]
		public int Fixed1StopTicks { get; set; }
		[Range(0.01, double.MaxValue)]
		[Display(Name = "Fixed 1 target multiple (R)", Order = 3, GroupName = "5. Fixed stops")]
		public double Fixed1Multiple { get; set; }
		[Display(Name = "Fixed 1 buy hotkey", Order = 4, GroupName = "5. Fixed stops")]
		public string Fixed1BuyHotkey { get; set; }
		[Display(Name = "Fixed 1 sell hotkey", Order = 5, GroupName = "5. Fixed stops")]
		public string Fixed1SellHotkey { get; set; }

		[Display(Name = "Fixed 2 enabled", Order = 11, GroupName = "5. Fixed stops")]
		public bool Fixed2Enabled { get; set; }
		[Range(1, int.MaxValue)]
		[Display(Name = "Fixed 2 stop ticks", Order = 12, GroupName = "5. Fixed stops")]
		public int Fixed2StopTicks { get; set; }
		[Range(0.01, double.MaxValue)]
		[Display(Name = "Fixed 2 target multiple (R)", Order = 13, GroupName = "5. Fixed stops")]
		public double Fixed2Multiple { get; set; }
		[Display(Name = "Fixed 2 buy hotkey", Order = 14, GroupName = "5. Fixed stops")]
		public string Fixed2BuyHotkey { get; set; }
		[Display(Name = "Fixed 2 sell hotkey", Order = 15, GroupName = "5. Fixed stops")]
		public string Fixed2SellHotkey { get; set; }

		[Display(Name = "Fixed 3 enabled", Order = 21, GroupName = "5. Fixed stops")]
		public bool Fixed3Enabled { get; set; }
		[Range(1, int.MaxValue)]
		[Display(Name = "Fixed 3 stop ticks", Order = 22, GroupName = "5. Fixed stops")]
		public int Fixed3StopTicks { get; set; }
		[Range(0.01, double.MaxValue)]
		[Display(Name = "Fixed 3 target multiple (R)", Order = 23, GroupName = "5. Fixed stops")]
		public double Fixed3Multiple { get; set; }
		[Display(Name = "Fixed 3 buy hotkey", Order = 24, GroupName = "5. Fixed stops")]
		public string Fixed3BuyHotkey { get; set; }
		[Display(Name = "Fixed 3 sell hotkey", Order = 25, GroupName = "5. Fixed stops")]
		public string Fixed3SellHotkey { get; set; }

		[Display(Name = "Fixed 4 enabled", Order = 31, GroupName = "5. Fixed stops")]
		public bool Fixed4Enabled { get; set; }
		[Range(1, int.MaxValue)]
		[Display(Name = "Fixed 4 stop ticks", Order = 32, GroupName = "5. Fixed stops")]
		public int Fixed4StopTicks { get; set; }
		[Range(0.01, double.MaxValue)]
		[Display(Name = "Fixed 4 target multiple (R)", Order = 33, GroupName = "5. Fixed stops")]
		public double Fixed4Multiple { get; set; }
		[Display(Name = "Fixed 4 buy hotkey", Order = 34, GroupName = "5. Fixed stops")]
		public string Fixed4BuyHotkey { get; set; }
		[Display(Name = "Fixed 4 sell hotkey", Order = 35, GroupName = "5. Fixed stops")]
		public string Fixed4SellHotkey { get; set; }

		[Display(Name = "Fixed 5 enabled", Order = 41, GroupName = "5. Fixed stops")]
		public bool Fixed5Enabled { get; set; }
		[Range(1, int.MaxValue)]
		[Display(Name = "Fixed 5 stop ticks", Order = 42, GroupName = "5. Fixed stops")]
		public int Fixed5StopTicks { get; set; }
		[Range(0.01, double.MaxValue)]
		[Display(Name = "Fixed 5 target multiple (R)", Order = 43, GroupName = "5. Fixed stops")]
		public double Fixed5Multiple { get; set; }
		[Display(Name = "Fixed 5 buy hotkey", Order = 44, GroupName = "5. Fixed stops")]
		public string Fixed5BuyHotkey { get; set; }
		[Display(Name = "Fixed 5 sell hotkey", Order = 45, GroupName = "5. Fixed stops")]
		public string Fixed5SellHotkey { get; set; }

		[Display(Name = "Close instrument hotkey", Order = 1, GroupName = "6. Close / flatten")]
		public string CloseHotkey { get; set; }

		[Display(Name = "Flatten all hotkey", Order = 2, GroupName = "6. Close / flatten")]
		public string FlattenAllHotkey { get; set; }

		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private RiskEntryPanel[] cacheRiskEntryPanel;
		public RiskEntryPanel RiskEntryPanel()
		{
			return RiskEntryPanel(Input);
		}

		public RiskEntryPanel RiskEntryPanel(ISeries<double> input)
		{
			if (cacheRiskEntryPanel != null)
				for (int idx = 0; idx < cacheRiskEntryPanel.Length; idx++)
					if (cacheRiskEntryPanel[idx] != null &&  cacheRiskEntryPanel[idx].EqualsInput(input))
						return cacheRiskEntryPanel[idx];
			return CacheIndicator<RiskEntryPanel>(new RiskEntryPanel(), input, ref cacheRiskEntryPanel);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.RiskEntryPanel RiskEntryPanel()
		{
			return indicator.RiskEntryPanel(Input);
		}

		public Indicators.RiskEntryPanel RiskEntryPanel(ISeries<double> input )
		{
			return indicator.RiskEntryPanel(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.RiskEntryPanel RiskEntryPanel()
		{
			return indicator.RiskEntryPanel(Input);
		}

		public Indicators.RiskEntryPanel RiskEntryPanel(ISeries<double> input )
		{
			return indicator.RiskEntryPanel(input);
		}
	}
}

#endregion
