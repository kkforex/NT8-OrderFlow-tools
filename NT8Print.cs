// =====================================================================================
//  NT8Print  -  standalone bid/ask footprint for NinjaTrader 8
//
//  Settings modelled on the NT8Charts "NT8 Print (Footprint)" help article:
//  Typology (Type / Mode), Data Settings (Input Type, Min/Max filter), Tick Grouping
//  (Auto / Manual, factor, Fixed / Open-Close anchor), Bar Style, Background coloration
//  (None / Fixed / Fading / More Colors x Volume / Delta / Imbalance / Dominant /
//  Dominant Delta), Colors, Range, Text, POC + Delta POC, Value Area, Alerts,
//  Single Print, Ratio, Vol Cluster, Pull Back and Multi-Column sizing.
//
//  Data:   Tick Replay (Data Series > Tick Replay) + OnMarketData Last prints, classified
//          by the bid/ask at the time of each print. Real-time works without Tick Replay.
//  Memory: one compact bid[] + ask[] per bar at 1-tick resolution (trimmed when the bar
//          closes), optional cap on stored bars. Grouping, colouring, POC / VA etc. are
//          computed at render time for visible bars only, into reused scratch buffers.
//  Render: cached DirectX brushes and DirectWrite text formats, cached number strings,
//          no per-cell TextLayout, rows outside the panel are culled.
// =====================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using DXBrush = SharpDX.Direct2D1.Brush;
using DWTextFormat = SharpDX.DirectWrite.TextFormat;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	[NinjaTrader.Gui.CategoryOrder("Typology", 1)]
	[NinjaTrader.Gui.CategoryOrder("Data Settings", 2)]
	[NinjaTrader.Gui.CategoryOrder("Tick Grouping", 3)]
	[NinjaTrader.Gui.CategoryOrder("Bar Style", 4)]
	[NinjaTrader.Gui.CategoryOrder("Background", 5)]
	[NinjaTrader.Gui.CategoryOrder("Color", 6)]
	[NinjaTrader.Gui.CategoryOrder("Range", 7)]
	[NinjaTrader.Gui.CategoryOrder("Text", 8)]
	[NinjaTrader.Gui.CategoryOrder("POC", 9)]
	[NinjaTrader.Gui.CategoryOrder("Value Area", 10)]
	[NinjaTrader.Gui.CategoryOrder("Alert Settings", 11)]
	[NinjaTrader.Gui.CategoryOrder("Single Print", 12)]
	[NinjaTrader.Gui.CategoryOrder("Ratio", 13)]
	[NinjaTrader.Gui.CategoryOrder("Vol Cluster", 14)]
	[NinjaTrader.Gui.CategoryOrder("Pull Back", 15)]
	[NinjaTrader.Gui.CategoryOrder("Bar Stats", 16)]
	[NinjaTrader.Gui.CategoryOrder("Sizing", 17)]
	public class NT8Print : Indicator
	{
		#region Data structures

		/// one bar's footprint at 1-tick resolution. Data lives in [Start, Start + N) of the
		/// arrays, Lo is the tick index (price / TickSize) of element Start.
		private sealed class FPBar
		{
			public double[] Bid;
			public double[] Ask;
			public int Start;
			public int N;
			public int Lo;

			public FPBar(int tick)
			{
				Bid = new double[16];
				Ask = new double[16];
				Start = 8;
				N = 1;
				Lo = tick;
			}

			/// array index for a tick, growing the range if needed
			public int Slot(int tick)
			{
				if (tick < Lo)
				{
					int s = Lo - tick;
					if (Start >= s) { Start -= s; N += s; Lo = tick; }
					else Grow(s, 0);
				}
				else if (tick >= Lo + N)
				{
					int add = tick - (Lo + N) + 1;
					if (Start + N + add <= Bid.Length) N += add;
					else Grow(0, add);
				}

				return Start + (tick - Lo);
			}

			private void Grow(int below, int above)
			{
				int newN = N + below + above;
				int cap = newN + 16;

				double[] nb = new double[cap];
				double[] na = new double[cap];

				Array.Copy(Bid, Start, nb, 8 + below, N);
				Array.Copy(Ask, Start, na, 8 + below, N);

				Bid = nb;
				Ask = na;
				Start = 8;
				N = newN;
				Lo -= below;
			}

			/// trims the arrays to the used size (called when the bar closes)
			public void Compact()
			{
				if (Bid.Length == N) { return; }

				double[] nb = new double[N];
				double[] na = new double[N];

				Array.Copy(Bid, Start, nb, 0, N);
				Array.Copy(Ask, Start, na, 0, N);

				Bid = nb;
				Ask = na;
				Start = 0;
			}
		}

		/// per-bar statistics from every trade (unfiltered), independent of Input Type
		private struct BarStat
		{
			public double Vol;
			public double Delta;
			public double BigBuyVol;
			public double BigSellVol;
			public int BigBuyN;
			public int BigSellN;
			public bool Has;
		}

		private struct Trade
		{
			public int Tick;
			public double Vol;
			public int Side;
			public long Time;
		}

		#endregion

		#region Constants

		private const int K_BID = 0;
		private const int K_ASK = 1;
		private const int K_VOL = 2;
		private const int K_DELTA = 3;

		// DirectX brush slots (order must match EnsureResources)
		private const int C_UP = 0;
		private const int C_DN = 1;
		private const int C_WICK = 2;
		private const int C_INF = 3;
		private const int C_VOL = 4;
		private const int C_BID = 5;
		private const int C_ASK = 6;
		private const int C_L1 = 7;   // .. C_L1 + 4
		private const int C_TXT = 12;
		private const int C_VPOC = 13;
		private const int C_DPA = 14;
		private const int C_DPB = 15;
		private const int C_VAO = 16;
		private const int C_VAL = 17;
		private const int C_SP = 18;
		private const int C_RAT = 19;
		private const int C_VC = 20;
		private const int C_PB = 21;

		private const float MinFont = 6f;

		private static readonly int[] NiceGroups = { 1, 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 30, 40, 50, 60, 80, 100, 125, 150, 200, 250, 400, 500, 1000 };

		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
		private static readonly string[] PosStr = new string[10000];
		private static readonly string[] NegStr = new string[10000];

		#endregion

		#region Variables

		// --- data (written on the data thread, read on the render thread under dataLock)
		private readonly object dataLock = new object();
		private List<FPBar> bars;
		private int curBar = -1;
		private int pruneIdx = 0;

		// prints received in OnMarketData, committed to CurrentBar in OnBarUpdate
		private Trade[] pend = new Trade[32];
		private int pendN = 0;
		private double lastTradePrice = 0.0;
		private int lastSide = 0;

		// aggregated-trade builder
		private Trade[] agg = new Trade[32];
		private int[] aggBar = new int[32];
		private int aggN = 0;
		private int aggSide = 0;
		private long aggTime = 0;
		private double aggSum = 0.0;

		// alerts
		private readonly HashSet<int> alerted = new HashSet<int>();
		private int alertTick = int.MinValue;

		private bool warned = false;

		// bar stats (under dataLock) + big-trade builder (data thread)
		private BarStat[] stats = new BarStat[16];
		private BarStat[] statScratch = new BarStat[256];
		private int btN = 0;
		private int btSide = 0;
		private long btTime = 0;
		private double btSum = 0.0;
		private int btBar = 0;

		// --- render scratch (render thread only)
		private double[] gB = new double[256];
		private double[] gA = new double[256];
		private bool[] gAI = new bool[256];
		private bool[] gBI = new bool[256];
		private int gN = 0;
		private int gLo = 0;

		private double sMaxSide, sMaxVol, sMaxAbsD, sTotal;
		private int sPoc, sDPoc, sVah, sVal;
		private double vMaxSide, vMaxVol, vMaxAbsD;
		private double mSide, mVol, mAbsD;

		private DXBrush[] dx;
		private bool infEnabled = false;
		private readonly Dictionary<int, DWTextFormat> fmtCache = new Dictionary<int, DWTextFormat>();
		private float cw = 0f;  // Consolas glyph width per font unit
		private float ch = 0f;  // line height per font unit
		private bool renderErrLogged = false;

		#endregion

		#region OnStateChange

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = @"NT8Print - bid/ask footprint with profile/box modes, tick grouping, imbalances, POC, value area and signals.";
				Name = "NT8Print";
				Calculate = Calculate.OnEachTick;
				IsOverlay = true;
				IsAutoScale = false;
				DisplayInDataBox = false;
				DrawOnPricePanel = true;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = false;
				ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;

				// Typology
				FootprintType = DPType.AskBidSplit;
				FootprintMode = DPMode.Box;
				ShowFootprint = true;
				ShowNumbers = true;

				// Data
				InputType = DPInput.Volume;
				MinFilter = 0;
				MaxFilter = 0;
				MaxBarsStored = 3000;

				// Tick grouping
				TickGrouping = DPGrouping.Automatic;
				AutoGroupFactor = 1.0;
				ManualTicks = 1;
				GroupMode = DPGroupMode.Fixed;

				// Bar style
				ShowBarMarker = true;
				BarMarkerWidth = 4;
				OutsideBarStyle = DPOutsideBar.Bar;
				MarkerAlignment = DPAlign.Center;
				OuterEdgeMode = false;
				OuterEdgeWidth = 1;
				BarUpColor = Brushes.MediumSeaGreen;
				BarDownColor = Brushes.IndianRed;
				WickColor = Brushes.Gray;
				HideChartBars = true;

				// Background
				ColorMode = DPColorMode.Fading;
				ColorCalculation = DPColorCalc.Volume;
				ImbalanceRatio = 300;
				MinVolDiffImbalance = 0;
				IncludeZeroOnImbalance = false;
				ColorOnlyDominantSide = false;
				InferiorColor = Brushes.Transparent;
				BorderWidth = 0;
				BackgroundOpacity = 55;
				Normalize = DPNormalize.PerBar;

				// Colors
				VolumeColor = Brushes.SteelBlue;
				BidColor = Brushes.Crimson;
				AskColor = Brushes.SeaGreen;

				// Range
				RangeDefinition = DPRange.Automatic;
				Level1Value = 50;
				Level2Value = 100;
				Level3Value = 200;
				Level4Value = 400;
				Level5Value = 800;
				Level1Color = Brushes.SteelBlue;
				Level2Color = Brushes.Teal;
				Level3Color = Brushes.Gold;
				Level4Color = Brushes.DarkOrange;
				Level5Color = Brushes.Red;

				// Text
				FontSize = 11;
				NumberFormat = DPNumberFormat.Automatic;
				TextColoration = DPTextColor.ByImbalance;
				TextColor = Brushes.WhiteSmoke;
				DynamicTextSize = false;
				IncreaseDynamicTextSize = 3;
				ShowZeros = false;

				// POC
				VolumePocEnable = true;
				VolPocColor = Brushes.Gold;
				VolPocWidth = 1;
				DeltaPocEnable = false;
				DeltaPocMin = 0;
				DeltaPocAskColor = Brushes.Lime;
				DeltaPocBidColor = Brushes.Red;
				DeltaPocWidth = 1;

				// Value area
				ValueAreaEnable = false;
				ValueAreaPercent = 70;
				ValueAreaHighlight = true;
				VaOutsideColor = Brushes.Black;
				ValueAreaShowLine = true;
				VaLineColor = Brushes.Silver;
				ValueAreaLineWidth = 1;

				// Alerts
				AlertSound = false;
				AlertSoundFile = System.IO.Path.Combine(NinjaTrader.Core.Globals.InstallDir, @"sounds\Alert2.wav");
				AlertMessageEnable = false;
				AlertMessage = "NT8Print volume alert";
				AlertMinVolume = 500;

				// Single print
				SinglePrintEnable = false;
				SinglePrintMinValue = 1;
				SinglePrintOnlyHighLow = true;
				SinglePrintWidth = 1;
				SinglePrintColor = Brushes.Yellow;

				// Ratio
				RatioEnable = false;
				RatioTickOffset = 1;
				RatioTextColor = Brushes.Silver;
				RatioTextSize = 10;
				RatioMin = 0;
				RatioMax = 0;

				// Vol cluster
				VolClusterEnable = false;
				VolClusterMinVolume = 500;
				VolClusterScaleByGroup = true;
				VolClusterLineWidth = 1;
				VolClusterColor = Brushes.Gold;
				VolClusterOpacity = 30;

				// Pull back
				PullbackEnable = false;
				PullbackMinSourceTicks = 8;
				PullbackMinPullbackTicks = 4;
				PullbackColor = Brushes.Orange;

				// Sizing
				ColumnCount = 1;
				ColumnPosition = 1;

				// Bar stats
				ShowBarStats = true;
				StatsTotalVolume = true;
				StatsDelta = true;
				StatsDeltaPercent = true;
				StatsBigTrades = true;
				StatsPosition = DPStatsPosition.BelowBar;
				StatsFontSize = 9;
				StatsShowLabels = true;
				StatsColored = true;
				BigTradeMin = 50;
				BigTradeAggregate = true;
			}
			else if (State == State.DataLoaded)
			{
				int cap = Math.Min(BarsArray[0].Count + 16, 100000);
				stats = new BarStat[Math.Max(cap, 16)];
				btN = 0;
				btSum = 0.0;
				bars = new List<FPBar>(Math.Max(cap, 16));
				curBar = -1;
				pruneIdx = 0;
				pendN = 0;
				aggN = 0;
				aggSum = 0.0;
				warned = false;
			}
			else if (State == State.Historical)
			{
				if (ChartControl != null)
				{
					ChartControl.Dispatcher.InvokeAsync(new Action(InsertMenu));
				}
			}
			else if (State == State.Terminated)
			{
				if (ChartControl != null)
				{
					ChartControl.Dispatcher.InvokeAsync(new Action(RemoveMenu));
				}

				DisposeDx();

				foreach (DWTextFormat tf in fmtCache.Values)
				{
					if (tf != null) tf.Dispose();
				}
				fmtCache.Clear();

				lock (dataLock)
				{
					if (bars != null) bars.Clear();
					bars = null;
				}
			}
		}

		public override string DisplayName
		{
			get { return "NT8Print"; }
		}

		#endregion

		#region Data collection

		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (bars == null || e.MarketDataType != MarketDataType.Last) { return; }

			double vol = e.Volume;
			if (vol <= 0) { return; }

			double price = e.Price;
			double ask = e.Ask;
			double bid = e.Bid;

			// aggressor: at/above ask = buy, at/below bid = sell, inside spread = tick rule
			int side;
			if (ask > bid && price >= ask) side = 1;
			else if (ask > bid && price <= bid) side = -1;
			else if (price > lastTradePrice) side = 1;
			else if (price < lastTradePrice) side = -1;
			else side = (lastSide != 0) ? lastSide : 1;

			lastTradePrice = price;
			lastSide = side;

			if (pendN == pend.Length) Array.Resize(ref pend, pendN * 2);

			pend[pendN].Tick = ToTick(price);
			pend[pendN].Vol = vol;
			pend[pendN].Side = side;
			pend[pendN].Time = e.Time.Ticks;
			pendN++;
		}

		protected override void OnMarketDepth(MarketDepthEventArgs e)
		{
			// "Order" input only: largest resting size seen at each price inside the bar range (real-time)
			if (InputType != DPInput.Order || bars == null || State != State.Realtime || CurrentBar < 0) { return; }
			if (e.Operation == Operation.Remove) { return; }
			if (e.MarketDataType != MarketDataType.Ask && e.MarketDataType != MarketDataType.Bid) { return; }

			double vol = e.Volume;
			if (vol <= 0 || !Passes(vol)) { return; }

			int bar = CurrentBar;
			double half = TickSize * 0.5;

			if (e.Price > Bars.GetHigh(bar) + half || e.Price < Bars.GetLow(bar) - half) { return; }

			lock (dataLock)
			{
				Add(bar, ToTick(e.Price), vol, e.MarketDataType == MarketDataType.Ask ? 1 : -1, true);
			}

			FireAlert();
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0 || bars == null) { return; }

			if (!warned)
			{
				warned = true;

				if (Calculate != Calculate.OnEachTick)
				{
					Draw.TextFixed(this, "dpWarnCalc", "NT8Print: set Calculate to 'On each tick'.", TextPosition.BottomLeft);
				}
				else if (InputType != DPInput.Order && !BarsArray[0].IsTickReplay)
				{
					Draw.TextFixed(this, "dpWarnReplay", "NT8Print: enable Tick Replay on the data series for historical footprints.\nWithout it only real-time bars are built.", TextPosition.BottomLeft);
				}
			}

			if (CurrentBar != curBar) { StartBar(CurrentBar); }

			if (HideChartBars && IsFirstTickOfBar)
			{
				BarBrush = Brushes.Transparent;
				CandleOutlineBrush = Brushes.Transparent;
			}

			if (pendN > 0)
			{
				int bar = CurrentBar;

				lock (dataLock)
				{
					for (int i = 0; i < pendN; i++)
					{
						Trade p = pend[i];

						AddStat(bar, p);

						switch (InputType)
						{
							case DPInput.Volume:
								if (Passes(p.Vol)) Add(bar, p.Tick, p.Vol, p.Side, false);
								break;

							case DPInput.NumTrades:
								if (Passes(p.Vol)) Add(bar, p.Tick, 1.0, p.Side, false);
								break;

							case DPInput.AggregatedVolume:
								if (aggN > 0 && (p.Side != aggSide || p.Time != aggTime)) FlushAggregate();

								if (aggN == agg.Length)
								{
									Array.Resize(ref agg, aggN * 2);
									Array.Resize(ref aggBar, aggN * 2);
								}

								agg[aggN] = p;
								aggBar[aggN] = bar;
								aggN++;
								aggSide = p.Side;
								aggTime = p.Time;
								aggSum += p.Vol;
								break;
						}
					}
				}

				pendN = 0;
			}

			FireAlert();
		}

		/// new bar: trim the previous one, drop bars beyond the storage cap
		private void StartBar(int newIdx)
		{
			lock (dataLock)
			{
				if (curBar >= 0 && curBar < bars.Count && bars[curBar] != null) bars[curBar].Compact();

				while (bars.Count <= newIdx) bars.Add(null);

				if (MaxBarsStored > 0)
				{
					int lim = newIdx - MaxBarsStored;

					while (pruneIdx <= lim)
					{
						bars[pruneIdx] = null;
						pruneIdx++;
					}
				}
			}

			curBar = newIdx;
			alerted.Clear();
		}

		/// bar stats: total / delta from every print, big trades from (optionally aggregated) orders. Caller holds dataLock.
		private void AddStat(int bar, Trade p)
		{
			if (bar >= stats.Length)
			{
				int cap = stats.Length;
				while (cap <= bar) cap *= 2;
				Array.Resize(ref stats, cap);
			}

			stats[bar].Vol += p.Vol;
			stats[bar].Delta += p.Side * p.Vol;
			stats[bar].Has = true;

			if (BigTradeMin <= 0) { return; }

			if (btN > 0 && (!BigTradeAggregate || p.Side != btSide || p.Time != btTime)) FlushBigTrade();

			if (btN == 0) btBar = bar;

			btSide = p.Side;
			btTime = p.Time;
			btSum += p.Vol;
			btN++;

			if (!BigTradeAggregate) FlushBigTrade();
		}

		private void FlushBigTrade()
		{
			if (btN > 0 && btSum >= BigTradeMin && btBar < stats.Length)
			{
				if (btSide > 0) { stats[btBar].BigBuyN++; stats[btBar].BigBuyVol += btSum; }
				else { stats[btBar].BigSellN++; stats[btBar].BigSellVol += btSum; }
			}

			btN = 0;
			btSum = 0.0;
		}

		/// aggregated trade complete: keep it only if its total size passes the filter (caller holds dataLock)
		private void FlushAggregate()
		{
			if (Passes(aggSum))
			{
				for (int k = 0; k < aggN; k++)
				{
					Add(aggBar[k], agg[k].Tick, agg[k].Vol, agg[k].Side, false);
				}
			}

			aggN = 0;
			aggSum = 0.0;
		}

		/// deposits a value into a bar (caller holds dataLock)
		private void Add(int bar, int tick, double val, int side, bool takeMax)
		{
			while (bars.Count <= bar) bars.Add(null);

			FPBar fb = bars[bar];

			if (fb == null)
			{
				if (MaxBarsStored > 0 && bar < pruneIdx) { return; }

				fb = new FPBar(tick);
				bars[bar] = fb;
			}

			int k = fb.Slot(tick);
			double before = fb.Bid[k] + fb.Ask[k];
			double[] arr = (side > 0) ? fb.Ask : fb.Bid;

			if (takeMax) { if (val > arr[k]) arr[k] = val; }
			else arr[k] += val;

			if (AlertMinVolume > 0 && bar == curBar && (AlertSound || AlertMessageEnable) && State == State.Realtime)
			{
				double after = fb.Bid[k] + fb.Ask[k];

				if (before < AlertMinVolume && after >= AlertMinVolume && alerted.Add(tick))
				{
					alertTick = tick;
				}
			}
		}

		private void FireAlert()
		{
			if (alertTick == int.MinValue) { return; }

			int t = alertTick;
			alertTick = int.MinValue;

			try
			{
				string msg = (string.IsNullOrEmpty(AlertMessage) ? "NT8Print" : AlertMessage) + " @ " + Instrument.MasterInstrument.FormatPrice(t * TickSize);

				if (AlertMessageEnable)
				{
					Alert("NT8Print_" + CurrentBar + "_" + t, Priority.High, msg, AlertSound ? AlertSoundFile : string.Empty, 0, Brushes.Black, Brushes.Gold);
				}
				else if (AlertSound && !string.IsNullOrEmpty(AlertSoundFile))
				{
					PlaySound(AlertSoundFile);
				}
			}
			catch { }
		}

		private bool Passes(double size)
		{
			return (MinFilter <= 0 || size >= MinFilter) && (MaxFilter <= 0 || size <= MaxFilter);
		}

		private int ToTick(double price)
		{
			return (int)Math.Round(price / TickSize);
		}

		private static int FloorDiv(int a, int b)
		{
			int q = a / b;
			if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
			return q;
		}

		#endregion

		#region Grouping and statistics (render thread)

		private void EnsureScratch(int n)
		{
			if (gB.Length >= n) { return; }

			int cap = gB.Length;
			while (cap < n) cap *= 2;

			gB = new double[cap];
			gA = new double[cap];
			gAI = new bool[cap];
			gBI = new bool[cap];
		}

		/// copies bar i into the scratch rows, grouped by G ticks. false when the bar has no data.
		private bool GroupBar(int i, int G)
		{
			lock (dataLock)
			{
				if (bars == null || i < 0 || i >= bars.Count) { return false; }

				FPBar fb = bars[i];
				if (fb == null || fb.N <= 0) { return false; }

				int offset = 0;

				if (GroupMode == DPGroupMode.OpenClose && G > 1)
				{
					double o = Bars.GetOpen(i);
					double c = Bars.GetClose(i);
					int ot = ToTick(o);

					// up bar: open sits at the bottom of a row, down bar: at the top of a row
					offset = (c >= o) ? ot : ot - G + 1;
				}

				int lo = fb.Lo;
				int n = fb.N;
				int st = fb.Start;

				int r0 = FloorDiv(lo - offset, G);
				int r1 = FloorDiv(lo + n - 1 - offset, G);
				int cnt = r1 - r0 + 1;

				if (cnt <= 0 || cnt > 50000) { return false; }

				EnsureScratch(cnt);
				Array.Clear(gB, 0, cnt);
				Array.Clear(gA, 0, cnt);

				double[] B = fb.Bid;
				double[] A = fb.Ask;
				int rel = lo - offset;

				for (int k = 0; k < n; k++)
				{
					double bb = B[st + k];
					double aa = A[st + k];

					if (bb == 0.0 && aa == 0.0) { continue; }

					int r = FloorDiv(rel + k, G) - r0;
					gB[r] += bb;
					gA[r] += aa;
				}

				gN = cnt;
				gLo = offset + r0 * G;
				return true;
			}
		}

		/// maxima, POC, delta POC, diagonal imbalances and value area for the scratch rows
		private void ComputeStats(bool valueArea)
		{
			double ms = 0, mv = 0, md = 0, tot = 0, bestV = -1, bestD = -1;
			int poc = 0, dpoc = -1;

			for (int r = 0; r < gN; r++)
			{
				double b = gB[r], a = gA[r];
				double v = a + b;
				double ad = Math.Abs(a - b);

				if (b > ms) ms = b;
				if (a > ms) ms = a;
				if (v > mv) mv = v;
				if (ad > md) md = ad;

				tot += v;

				if (v > bestV) { bestV = v; poc = r; }
				if (ad > bestD) { bestD = ad; dpoc = r; }
			}

			// diagonal imbalance: ask(row) vs bid(row - 1), bid(row) vs ask(row + 1)
			double ratio = Math.Max(1.0, ImbalanceRatio / 100.0);

			for (int r = 0; r < gN; r++)
			{
				double a = gA[r];
				double bb = (r > 0) ? gB[r - 1] : 0.0;
				gAI[r] = a > 0 && (a - bb) >= MinVolDiffImbalance && (bb > 0 ? a >= bb * ratio : IncludeZeroOnImbalance);

				double b = gB[r];
				double aa = (r < gN - 1) ? gA[r + 1] : 0.0;
				gBI[r] = b > 0 && (b - aa) >= MinVolDiffImbalance && (aa > 0 ? b >= aa * ratio : IncludeZeroOnImbalance);
			}

			sMaxSide = ms;
			sMaxVol = mv;
			sMaxAbsD = md;
			sTotal = tot;
			sPoc = poc;
			sDPoc = dpoc;
			sVah = gN - 1;
			sVal = 0;

			if (valueArea && tot > 0)
			{
				double target = tot * Math.Min(100.0, Math.Max(1.0, ValueAreaPercent)) / 100.0;
				int lo = poc, hi = poc;
				double acc = gA[poc] + gB[poc];

				while (acc < target && (lo > 0 || hi < gN - 1))
				{
					double up = (hi < gN - 1) ? gA[hi + 1] + gB[hi + 1] : -1.0;
					double dn = (lo > 0) ? gA[lo - 1] + gB[lo - 1] : -1.0;

					if (up >= dn) { hi++; acc += up; }
					else { lo--; acc += dn; }
				}

				sVah = hi;
				sVal = lo;
			}
		}

		private int GroupTicks(float pxTick)
		{
			if (TickGrouping == DPGrouping.Manual) { return Math.Max(1, ManualTicks); }

			double raw = (FontSize + 3.0) * AutoGroupFactor / pxTick;

			if (raw <= 1.0) { return 1; }

			foreach (int v in NiceGroups)
			{
				if (v >= raw) return v;
			}

			return (int)Math.Ceiling(raw);
		}

		#endregion

		#region Cell values and colors

		private double CellValue(int kind, int r)
		{
			switch (kind)
			{
				case K_BID: return gB[r];
				case K_ASK: return gA[r];
				case K_VOL: return gA[r] + gB[r];
				default: return gA[r] - gB[r];
			}
		}

		private double CellMax(int kind)
		{
			switch (kind)
			{
				case K_BID:
				case K_ASK: return mSide;
				case K_VOL: return mVol;
				default: return mAbsD;
			}
		}

		/// background colour of a cell: brush slot + opacity. false = no fill.
		private bool CellColor(int kind, int r, float op, out int ci, out float alpha)
		{
			ci = C_VOL;
			alpha = 0f;

			if (ColorMode == DPColorMode.None) { return false; }

			double b = gB[r], a = gA[r];
			double v = a + b, d = a - b;
			double metric, max;
			int hue;
			bool inferior = false;

			switch (ColorCalculation)
			{
				case DPColorCalc.Volume:
					if (kind == K_BID) { metric = b; max = mSide; hue = C_BID; inferior = ColorOnlyDominantSide && b < a; }
					else if (kind == K_ASK) { metric = a; max = mSide; hue = C_ASK; inferior = ColorOnlyDominantSide && a < b; }
					else if (kind == K_VOL) { metric = v; max = mVol; hue = C_VOL; }
					else { metric = v; max = mVol; hue = d > 0 ? C_ASK : d < 0 ? C_BID : C_VOL; }
					break;

				case DPColorCalc.Delta:
					metric = Math.Abs(d);
					max = mAbsD;
					hue = d > 0 ? C_ASK : d < 0 ? C_BID : C_VOL;
					if (ColorOnlyDominantSide && ((kind == K_BID && d >= 0) || (kind == K_ASK && d <= 0))) inferior = true;
					break;

				case DPColorCalc.Imbalance:
				{
					bool ai = gAI[r], bi = gBI[r];
					max = mSide;

					if (kind == K_BID) { metric = b; hue = C_BID; inferior = !bi; }
					else if (kind == K_ASK) { metric = a; hue = C_ASK; inferior = !ai; }
					else
					{
						metric = ai ? a : b;
						hue = (ai && bi) ? C_VOL : ai ? C_ASK : C_BID;
						inferior = !(ai || bi);
					}
					break;
				}

				case DPColorCalc.DominantVolume:
					metric = Math.Max(a, b);
					max = mSide;
					hue = a > b ? C_ASK : b > a ? C_BID : C_VOL;
					if (kind == K_BID) inferior = !(b > a);
					else if (kind == K_ASK) inferior = !(a > b);
					break;

				default: // DominantVolumeDelta
					metric = Math.Abs(d);
					max = mAbsD;
					hue = a > b ? C_ASK : b > a ? C_BID : C_VOL;
					if (kind == K_BID) inferior = !(b > a);
					else if (kind == K_ASK) inferior = !(a > b);
					break;
			}

			if (inferior)
			{
				if (!infEnabled) { return false; }
				ci = C_INF;
				alpha = op;
				return true;
			}

			if (metric <= 0) { return false; }

			switch (ColorMode)
			{
				case DPColorMode.Fixed:
					ci = hue;
					alpha = op;
					return true;

				case DPColorMode.Fading:
				{
					double norm = (RangeDefinition == DPRange.User && Level5Value > 0)
						? Math.Min(1.0, metric / Level5Value)
						: (max > 0 ? metric / max : 0.0);

					ci = hue;
					alpha = op * (0.12f + 0.88f * (float)norm);
					return true;
				}

				default: // MoreColors
				{
					double t1, t2, t3, t4, t5;

					if (RangeDefinition == DPRange.User)
					{
						t1 = Level1Value; t2 = Level2Value; t3 = Level3Value; t4 = Level4Value; t5 = Level5Value;
					}
					else
					{
						t1 = double.Epsilon; t2 = max * 0.2; t3 = max * 0.4; t4 = max * 0.6; t5 = max * 0.8;
					}

					int lvl = metric >= t5 ? 4 : metric >= t4 ? 3 : metric >= t3 ? 2 : metric >= t2 ? 1 : metric >= t1 ? 0 : -1;

					if (lvl < 0) { return false; }

					ci = C_L1 + lvl;
					alpha = op;
					return true;
				}
			}
		}

		private int TextColorIndex(int kind, int r, out bool bold)
		{
			bold = VolumePocEnable && r == sPoc;

			switch (TextColoration)
			{
				case DPTextColor.BySide:
					if (kind == K_BID) return C_BID;
					if (kind == K_ASK) return C_ASK;
					if (kind == K_DELTA)
					{
						double d = gA[r] - gB[r];
						return d > 0 ? C_ASK : d < 0 ? C_BID : C_TXT;
					}
					return C_TXT;

				case DPTextColor.ByImbalance:
					if (kind == K_BID && gBI[r]) { bold = true; return C_BID; }
					if (kind == K_ASK && gAI[r]) { bold = true; return C_ASK; }
					if (kind == K_VOL || kind == K_DELTA)
					{
						if (gAI[r] && !gBI[r]) { bold = true; return C_ASK; }
						if (gBI[r] && !gAI[r]) { bold = true; return C_BID; }
					}
					return C_TXT;

				default:
					return C_TXT;
			}
		}

		#endregion

		#region Text helpers

		private static string IntStr(long v)
		{
			if (v >= 0 && v < 10000)
			{
				string s = PosStr[v];
				if (s == null) { s = v.ToString(Inv); PosStr[v] = s; }
				return s;
			}

			if (v < 0 && v > -10000)
			{
				string s = NegStr[-v];
				if (s == null) { s = v.ToString(Inv); NegStr[-v] = s; }
				return s;
			}

			return v.ToString(Inv);
		}

		private static string FormatValue(double v, bool compact)
		{
			double av = Math.Abs(v);

			if (compact && av >= 1000.0)
			{
				if (av >= 1000000.0) return (v / 1000000.0).ToString("0.#", Inv) + "M";
				return (v / 1000.0).ToString("0.#", Inv) + "K";
			}

			if (av < 1e15 && v == Math.Floor(v)) return IntStr((long)v);

			return v.ToString("0.##", Inv);
		}

		/// align: 0 = leading, 1 = trailing, 2 = center
		private DWTextFormat GetFormat(int size, bool bold, int align)
		{
			int key = (size << 3) | (bold ? 4 : 0) | align;

			DWTextFormat tf;
			if (fmtCache.TryGetValue(key, out tf)) { return tf; }

			tf = new DWTextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas",
				bold ? SharpDX.DirectWrite.FontWeight.Bold : SharpDX.DirectWrite.FontWeight.Normal,
				SharpDX.DirectWrite.FontStyle.Normal, SharpDX.DirectWrite.FontStretch.Normal, (float)size);

			tf.TextAlignment = (align == 1) ? SharpDX.DirectWrite.TextAlignment.Trailing
				: (align == 2) ? SharpDX.DirectWrite.TextAlignment.Center
				: SharpDX.DirectWrite.TextAlignment.Leading;
			tf.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
			tf.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;

			fmtCache[key] = tf;
			return tf;
		}

		/// measures Consolas once (monospace), so text never needs a TextLayout per cell
		private void EnsureMetrics()
		{
			if (cw > 0f) { return; }

			try
			{
				using (DWTextFormat tf = new DWTextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas",
					SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, SharpDX.DirectWrite.FontStretch.Normal, 100f))
				using (SharpDX.DirectWrite.TextLayout tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, "0000000000", tf, 4000f, 400f))
				{
					cw = tl.Metrics.WidthIncludingTrailingWhitespace / 1000f;
					ch = tl.Metrics.Height / 100f;
				}
			}
			catch { }

			if (cw <= 0f) cw = 0.6f;
			if (ch <= 0f) ch = 1.2f;
		}

		#endregion

		#region Resources

		private void EnsureResources()
		{
			if (dx != null) { return; }

			Brush[] src =
			{
				BarUpColor, BarDownColor, WickColor, InferiorColor, VolumeColor, BidColor, AskColor,
				Level1Color, Level2Color, Level3Color, Level4Color, Level5Color,
				TextColor, VolPocColor, DeltaPocAskColor, DeltaPocBidColor, VaOutsideColor, VaLineColor,
				SinglePrintColor, RatioTextColor, VolClusterColor, PullbackColor
			};

			dx = new DXBrush[src.Length];

			for (int k = 0; k < src.Length; k++)
			{
				dx[k] = (src[k] ?? Brushes.Gray).ToDxBrush(RenderTarget);
			}

			SolidColorBrush inf = InferiorColor as SolidColorBrush;
			infEnabled = inf != null && inf.Color.A > 0;
		}

		private void DisposeDx()
		{
			if (dx == null) { return; }

			for (int k = 0; k < dx.Length; k++)
			{
				if (dx[k] != null) { dx[k].Dispose(); dx[k] = null; }
			}

			dx = null;
		}

		public override void OnRenderTargetChanged()
		{
			DisposeDx();
		}

		#endregion

		#region Rendering

		// per-bar geometry (set in the render loop)
		private float c1L, c1R, c2L, c2R, cL, cR, spanL, spanR;

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (IsInHitTest || ChartBars == null || RenderTarget == null || bars == null) { return; }
			if (ChartBars.FromIndex < 0 || ChartBars.ToIndex < ChartBars.FromIndex) { return; }

			SharpDX.Direct2D1.AntialiasMode oldAA = RenderTarget.AntialiasMode;

			try
			{
				EnsureResources();
				EnsureMetrics();
				RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;
				RenderAll(chartControl, chartScale);
			}
			catch (Exception ex)
			{
				if (!renderErrLogged)
				{
					renderErrLogged = true;
					Print("NT8Print render error: " + ex);
				}
			}
			finally
			{
				RenderTarget.AntialiasMode = oldAA;
			}
		}

		private void RenderAll(ChartControl cc, ChartScale cs)
		{
			double tick = TickSize;

			double midPrice = (cs.MaxValue + cs.MinValue) * 0.5;
			float pxTick = Math.Abs(cs.GetYByValue(midPrice) - cs.GetYByValue(midPrice + tick * 100.0)) / 100f;
			if (pxTick <= 0f) { return; }

			int G = GroupTicks(pxTick);
			float rowPx = pxTick * G;
			float slot = cc.Properties.BarDistance;

			float panelTop = ChartPanel.Y;
			float panelBot = ChartPanel.Y + ChartPanel.H;

			bool twoCells = FootprintType == DPType.AskBidSplit || FootprintType == DPType.DeltaVolume;
			int kind1 = (FootprintType == DPType.DeltaVolume) ? K_DELTA : K_BID;
			int kind2 = (FootprintType == DPType.DeltaVolume) ? K_VOL : K_ASK;
			int kindS = (FootprintType == DPType.Delta) ? K_DELTA : K_VOL;

			bool multi = ColumnCount > 1;
			bool center = MarkerAlignment == DPAlign.Center && !multi;

			float mW = 0f;
			if (BarMarkerWidth > 0 && (ShowBarMarker || multi))
			{
				mW = Math.Min(BarMarkerWidth, Math.Max(1f, slot * 0.5f));
			}

			// text size from row height (numbers hide automatically when rows are too small)
			float fsBase = 0f, fsCap = 0f;

			if (ShowNumbers)
			{
				fsBase = Math.Min(FontSize, (float)Math.Floor((rowPx - 1f) / ch));

				if (fsBase < MinFont) fsBase = 0f;
				else fsCap = Math.Max(fsBase, (float)Math.Floor(rowPx * 1.25f / ch));
			}

			float op = BackgroundOpacity / 100f;

			int from = ChartBars.FromIndex;
			int to = ChartBars.ToIndex;

			if (Normalize == DPNormalize.Visible)
			{
				vMaxSide = 0; vMaxVol = 0; vMaxAbsD = 0;

				for (int i = from; i <= to; i++)
				{
					if (!GroupBar(i, G)) { continue; }

					ComputeStats(false);
					if (sMaxSide > vMaxSide) vMaxSide = sMaxSide;
					if (sMaxVol > vMaxVol) vMaxVol = sMaxVol;
					if (sMaxAbsD > vMaxAbsD) vMaxAbsD = sMaxAbsD;
				}
			}

			for (int i = from; i <= to; i++)
			{
				float xc = cc.GetXByBarIndex(ChartBars, i);
				float half = Math.Max(1f, slot * 0.5f - 1f);
				float sL = xc - half;
				float sR = xc + half;
				float mL, mR;

				#region geometry

				if (center)
				{
					mL = xc - mW * 0.5f;
					mR = xc + mW * 0.5f;
					float gap = (mW > 0f) ? 1f : 0f;

					c1L = sL; c1R = mL - gap;
					c2L = mR + gap; c2R = sR;
					cL = c2L; cR = c2R;

					spanL = twoCells ? c1L : cL;
					spanR = sR;
				}
				else
				{
					mR = sR;
					mL = sR - mW;

					float fR = mL - ((mW > 0f) ? 1f : 0f);
					int cols = Math.Max(1, ColumnCount);
					int pos = Math.Max(1, Math.Min(ColumnPosition, cols)) - 1;
					float colW = (fR - sL) / cols;
					float colL = sL + colW * pos;
					float colR = colL + colW - (multi ? 1f : 0f);
					float mid = (colL + colR) * 0.5f;

					c1L = colL; c1R = mid - 0.5f;
					c2L = mid + 0.5f; c2R = colR;
					cL = colL; cR = colR;
					spanL = colL; spanR = colR;
				}

				#endregion

				if (ShowFootprint && spanR - spanL >= 2f && GroupBar(i, G))
				{
					ComputeStats(ValueAreaEnable);

					if (Normalize == DPNormalize.Visible) { mSide = vMaxSide; mVol = vMaxVol; mAbsD = vMaxAbsD; }
					else { mSide = sMaxSide; mVol = sMaxVol; mAbsD = sMaxAbsD; }

					DrawFootprint(cs, i, G, tick, pxTick, panelTop, panelBot, fsBase, fsCap, op, twoCells, kind1, kind2, kindS);
				}

				if (ShowBarMarker && mW > 0f)
				{
					DrawMarker(cs, i, mL, mR);
				}
			}

			if (ShowBarStats) DrawBarStats(cc, cs, G, tick, slot, panelBot, op);
		}

		#region Bar stats

		private int StatRows()
		{
			return (StatsTotalVolume ? 1 : 0) + (StatsDelta ? 1 : 0) + (StatsDeltaPercent ? 1 : 0) + (StatsBigTrades ? 1 : 0);
		}

		/// lowest tick of the footprint grid row that contains the bar's low (so stats sit under the footprint)
		private int GridLowTick(int i, int G)
		{
			int lt = ToTick(Bars.GetLow(i));
			if (G <= 1) { return lt; }

			int offset = 0;

			if (GroupMode == DPGroupMode.OpenClose)
			{
				double o = Bars.GetOpen(i), c = Bars.GetClose(i);
				int ot = ToTick(o);
				offset = (c >= o) ? ot : ot - G + 1;
			}

			return offset + FloorDiv(lt - offset, G) * G;
		}

		private void DrawBarStats(ChartControl cc, ChartScale cs, int G, double tick, float slot, float panelBot, float op)
		{
			int rows = StatRows();
			if (rows == 0) { return; }

			int from = ChartBars.FromIndex;
			int to = ChartBars.ToIndex;
			int cnt = to - from + 1;

			if (statScratch.Length < cnt) statScratch = new BarStat[Math.Max(cnt, statScratch.Length * 2)];

			lock (dataLock)
			{
				for (int i = from; i <= to; i++)
				{
					statScratch[i - from] = (stats != null && i < stats.Length) ? stats[i] : default(BarStat);
				}
			}

			// visible maxima for the fade intensity
			double mxV = 0, mxD = 0;

			for (int i = from; i <= to; i++)
			{
				BarStat st = statScratch[i - from];
				double v = st.Has ? st.Vol : (double)Bars.GetVolume(i);
				if (v > mxV) mxV = v;
				if (st.Has && Math.Abs(st.Delta) > mxD) mxD = Math.Abs(st.Delta);
			}

			float fs = StatsFontSize;
			float rh = (float)Math.Ceiling(fs * ch) + 3f;
			float half = Math.Max(1f, slot * 0.5f - 1f);
			float xLast = 0f, yLast = 0f;

			for (int i = from; i <= to; i++)
			{
				BarStat st = statScratch[i - from];

				float xc = cc.GetXByBarIndex(ChartBars, i);
				float x1 = xc - half;
				float w = half * 2f;

				float y0 = (StatsPosition == DPStatsPosition.BelowBar)
					? cs.GetYByValue((GridLowTick(i, G) - 0.5) * tick) + 3f
					: panelBot - rows * rh - 2f;

				if (i == to) { xLast = xc + half + 4f; yLast = y0; }

				if (y0 > panelBot) { continue; }

				int k = 0;
				double vol = st.Has ? st.Vol : (double)Bars.GetVolume(i);

				if (StatsTotalVolume)
				{
					StatCell(x1, y0 + k * rh, w, rh, 0, vol, null, C_VOL, mxV > 0 ? vol / mxV : 0, op, fs);
					k++;
				}

				if (StatsDelta)
				{
					if (st.Has)
						StatCell(x1, y0 + k * rh, w, rh, 1, st.Delta, null, st.Delta > 0 ? C_ASK : st.Delta < 0 ? C_BID : C_VOL, mxD > 0 ? Math.Abs(st.Delta) / mxD : 0, op, fs);
					k++;
				}

				if (StatsDeltaPercent)
				{
					if (st.Has && st.Vol > 0)
					{
						double pct = st.Delta / st.Vol * 100.0;
						StatCell(x1, y0 + k * rh, w, rh, 2, pct, null, pct > 0 ? C_ASK : pct < 0 ? C_BID : C_VOL, Math.Min(1.0, Math.Abs(pct) / 100.0), op, fs);
					}
					k++;
				}

				if (StatsBigTrades)
				{
					if (st.BigBuyN + st.BigSellN > 0)
					{
						string txt = (st.BigBuyN > 0 && st.BigSellN > 0) ? IntStr(st.BigBuyN) + "B " + IntStr(st.BigSellN) + "S"
							: (st.BigBuyN > 0) ? IntStr(st.BigBuyN) + "B" : IntStr(st.BigSellN) + "S";

						int ci = (st.BigBuyVol > st.BigSellVol) ? C_ASK : (st.BigSellVol > st.BigBuyVol) ? C_BID : C_VOL;
						StatCell(x1, y0 + k * rh, w, rh, 3, 0, txt, ci, 1.0, op, fs);
					}
					k++;
				}
			}

			// row labels to the right of the last visible bar

			if (StatsShowLabels && yLast <= panelBot)
			{
				DWTextFormat tf = GetFormat((int)fs, false, 0);
				DXBrush b = dx[C_TXT];
				b.Opacity = 0.7f;
				int k = 0;

				if (StatsTotalVolume) { RenderTarget.DrawText("Volume", tf, new SharpDX.RectangleF(xLast, yLast + k * rh, 120f, rh), b); k++; }
				if (StatsDelta) { RenderTarget.DrawText("Delta", tf, new SharpDX.RectangleF(xLast, yLast + k * rh, 120f, rh), b); k++; }
				if (StatsDeltaPercent) { RenderTarget.DrawText("Delta %", tf, new SharpDX.RectangleF(xLast, yLast + k * rh, 120f, rh), b); k++; }
				if (StatsBigTrades) { RenderTarget.DrawText("Big Trades >=" + FormatValue(BigTradeMin, true), tf, new SharpDX.RectangleF(xLast, yLast + k * rh, 160f, rh), b); k++; }
			}
		}

		/// mode: 0 = number, 1 = signed number, 2 = percent, 3 = preformatted text
		private static string StatText(int mode, double v, string txt, bool compact)
		{
			if (mode == 3) return txt;
			if (mode == 2) return v.ToString(compact ? "0" : "0.#", Inv) + "%";

			string s = FormatValue(v, compact);
			return (mode == 1 && v > 0) ? "+" + s : s;
		}

		private void StatCell(float x, float y, float w, float h, int mode, double v, string txt, int ci, double norm, float op, float fs)
		{
			if (w < 2f) { return; }

			if (StatsColored)
			{
				Fill(x, y, w, h - 1f, ci, Math.Max(0.15f, op) * (0.2f + 0.8f * (float)norm));
			}
			else
			{
				DXBrush bb = dx[C_WICK];
				bb.Opacity = 0.5f;
				RenderTarget.DrawRectangle(new SharpDX.RectangleF(x, y, w, h - 1f), bb, 1f);
			}

			float avail = w - 2f;
			string s = StatText(mode, v, txt, false);

			if (s.Length * cw * fs > avail)
			{
				s = StatText(mode, v, txt, true);

				if (s.Length * cw * fs > avail)
				{
					fs = (float)Math.Floor(avail / (s.Length * cw));
					if (fs < MinFont) { return; }
				}
			}

			DXBrush tb = dx[C_TXT];
			tb.Opacity = 1f;
			RenderTarget.DrawText(s, GetFormat((int)fs, false, 2), new SharpDX.RectangleF(x, y, w, h - 1f), tb);
		}

		#endregion

		private bool RowY(ChartScale cs, int r, int G, double tick, float panelTop, float panelBot, out float yT, out float h)
		{
			int loT = gLo + r * G;

			yT = cs.GetYByValue((loT + G - 0.5) * tick);
			float yB = cs.GetYByValue((loT - 0.5) * tick);

			h = yB - yT;

			if (yB < panelTop || yT > panelBot) { return false; }

			if (h >= 4f) h -= 1f;
			if (h < 1f) h = 1f;

			return true;
		}

		private void DrawFootprint(ChartScale cs, int i, int G, double tick, float pxTick, float panelTop, float panelBot,
			float fsBase, float fsCap, float op, bool twoCells, int kind1, int kind2, int kindS)
		{
			float yT, h;
			float spanW = spanR - spanL;

			// 1. cell backgrounds / profile bars

			for (int r = 0; r < gN; r++)
			{
				if (!RowY(cs, r, G, tick, panelTop, panelBot, out yT, out h)) { continue; }

				if (twoCells)
				{
					DrawCellBg(kind1, r, c1L, c1R, true, yT, h, op);
					DrawCellBg(kind2, r, c2L, c2R, false, yT, h, op);
				}
				else
				{
					DrawCellBg(kindS, r, cL, cR, false, yT, h, op);
				}
			}

			// 2. overlays: volume clusters, outside value area

			if (VolClusterEnable)
			{
				double thr = VolClusterMinVolume * (VolClusterScaleByGroup ? G : 1);
				float ca = VolClusterOpacity / 100f;

				for (int r = 0; r < gN; r++)
				{
					if (gA[r] + gB[r] < thr) { continue; }
					if (!RowY(cs, r, G, tick, panelTop, panelBot, out yT, out h)) { continue; }

					Fill(spanL, yT, spanW, h, C_VC, ca);

					if (VolClusterLineWidth > 0)
					{
						dx[C_VC].Opacity = 1f;
						RenderTarget.DrawRectangle(new SharpDX.RectangleF(spanL, yT, spanW, h), dx[C_VC], VolClusterLineWidth);
					}
				}
			}

			if (ValueAreaEnable && ValueAreaHighlight)
			{
				for (int r = 0; r < gN; r++)
				{
					if (r >= sVal && r <= sVah) { continue; }
					if (!RowY(cs, r, G, tick, panelTop, panelBot, out yT, out h)) { continue; }

					Fill(spanL, yT, spanW, h, C_VAO, 0.45f);
				}
			}

			// 3. numbers

			if (ShowNumbers && fsBase > 0f)
			{
				for (int r = 0; r < gN; r++)
				{
					if (!RowY(cs, r, G, tick, panelTop, panelBot, out yT, out h)) { continue; }

					if (twoCells)
					{
						DrawCellText(kind1, r, c1L, c1R, true, yT, h, fsBase, fsCap);
						DrawCellText(kind2, r, c2L, c2R, false, yT, h, fsBase, fsCap);
					}
					else
					{
						DrawCellText(kindS, r, cL, cR, false, yT, h, fsBase, fsCap);
					}
				}
			}

			// 4. borders: single prints, POC, delta POC, value area lines

			if (SinglePrintEnable && SinglePrintWidth > 0)
			{
				dx[C_SP].Opacity = 1f;

				for (int r = 0; r < gN; r++)
				{
					if (SinglePrintOnlyHighLow && r != 0 && r != gN - 1) { continue; }

					double b = gB[r], a = gA[r];
					bool single = (b == 0.0) != (a == 0.0) && Math.Max(a, b) >= SinglePrintMinValue;

					if (!single) { continue; }
					if (!RowY(cs, r, G, tick, panelTop, panelBot, out yT, out h)) { continue; }

					RenderTarget.DrawRectangle(new SharpDX.RectangleF(spanL + 1f, yT + 1f, spanW - 2f, Math.Max(1f, h - 2f)), dx[C_SP], SinglePrintWidth);
				}
			}

			if (VolumePocEnable && VolPocWidth > 0 && sTotal > 0 && RowY(cs, sPoc, G, tick, panelTop, panelBot, out yT, out h))
			{
				dx[C_VPOC].Opacity = 1f;
				RenderTarget.DrawRectangle(new SharpDX.RectangleF(spanL, yT, spanW, h), dx[C_VPOC], VolPocWidth);
			}

			if (DeltaPocEnable && DeltaPocWidth > 0 && sDPoc >= 0)
			{
				double d = gA[sDPoc] - gB[sDPoc];

				if (d != 0.0 && Math.Abs(d) >= DeltaPocMin && RowY(cs, sDPoc, G, tick, panelTop, panelBot, out yT, out h))
				{
					int ci = (d > 0) ? C_DPA : C_DPB;
					float inset = (VolumePocEnable && sDPoc == sPoc) ? VolPocWidth + 1f : 0f;

					dx[ci].Opacity = 1f;
					RenderTarget.DrawRectangle(new SharpDX.RectangleF(spanL + inset, yT + inset, Math.Max(1f, spanW - inset * 2f), Math.Max(1f, h - inset * 2f)), dx[ci], DeltaPocWidth);
				}
			}

			if (ValueAreaEnable && ValueAreaShowLine && ValueAreaLineWidth > 0)
			{
				float yVah = cs.GetYByValue((gLo + sVah * G + G - 0.5) * tick);
				float yVal = cs.GetYByValue((gLo + sVal * G - 0.5) * tick);

				dx[C_VAL].Opacity = 1f;
				RenderTarget.DrawLine(new SharpDX.Vector2(spanL, yVah), new SharpDX.Vector2(spanR, yVah), dx[C_VAL], ValueAreaLineWidth);
				RenderTarget.DrawLine(new SharpDX.Vector2(spanL, yVal), new SharpDX.Vector2(spanR, yVal), dx[C_VAL], ValueAreaLineWidth);
			}

			// 5. outer edge around the body range

			if (OuterEdgeMode && OuterEdgeWidth > 0)
			{
				double o = Bars.GetOpen(i), c = Bars.GetClose(i);
				int rO = Math.Max(0, Math.Min(gN - 1, FloorDiv(ToTick(o) - gLo, G)));
				int rC = Math.Max(0, Math.Min(gN - 1, FloorDiv(ToTick(c) - gLo, G)));
				int rTop = Math.Max(rO, rC), rBot = Math.Min(rO, rC);

				float yTop = cs.GetYByValue((gLo + rTop * G + G - 0.5) * tick);
				float yBot = cs.GetYByValue((gLo + rBot * G - 0.5) * tick);
				int ci = (c > o) ? C_UP : (c < o) ? C_DN : C_WICK;

				dx[ci].Opacity = 1f;
				RenderTarget.DrawRectangle(new SharpDX.RectangleF(spanL - 1f, yTop, spanW + 2f, Math.Max(1f, yBot - yTop)), dx[ci], OuterEdgeWidth);
			}

			// 6. ratio at the bar extremes

			if (RatioEnable && gN >= 2)
			{
				DWTextFormat tf = GetFormat(RatioTextSize, false, 2);
				float th = RatioTextSize * ch + 2f;
				float yLow = cs.GetYByValue((gLo - 0.5) * tick) + RatioTickOffset * pxTick;
				float yHigh = cs.GetYByValue((gLo + gN * G - 0.5) * tick) - RatioTickOffset * pxTick;

				dx[C_RAT].Opacity = 1f;

				if (gB[0] > 0)
				{
					double rt = gB[1] / gB[0];
					if (RatioOk(rt)) RenderTarget.DrawText(rt.ToString("0.00", Inv), tf, new SharpDX.RectangleF(spanL, yLow, spanW, th), dx[C_RAT]);
				}

				if (gA[gN - 1] > 0)
				{
					double rt = gA[gN - 2] / gA[gN - 1];
					if (RatioOk(rt)) RenderTarget.DrawText(rt.ToString("0.00", Inv), tf, new SharpDX.RectangleF(spanL, yHigh - th, spanW, th), dx[C_RAT]);
				}
			}
		}

		private bool RatioOk(double rt)
		{
			return rt >= RatioMin && (RatioMax <= 0 || rt <= RatioMax);
		}

		private void Fill(float x, float y, float w, float h, int ci, float alpha)
		{
			if (w <= 0f || h <= 0f) { return; }

			DXBrush b = dx[ci];
			b.Opacity = Math.Max(0f, Math.Min(1f, alpha));
			RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, w, h), b);
		}

		private void DrawCellBg(int kind, int r, float x1, float x2, bool anchorRight, float yT, float h, float op)
		{
			float w = x2 - x1;
			if (w < 1f) { return; }

			int ci;
			float alpha;
			bool colored = CellColor(kind, r, op, out ci, out alpha);

			float x = x1;

			if (FootprintMode == DPMode.Box)
			{
				if (!colored) { return; }
			}
			else
			{
				double av = Math.Abs(CellValue(kind, r));
				double mx = CellMax(kind);

				if (av <= 0.0 || mx <= 0.0) { return; }

				float len = Math.Max(1f, (float)(av / mx) * w);

				if (anchorRight) x = x2 - len;
				w = len;

				if (ColorMode == DPColorMode.None) { ci = C_VOL; alpha = op; }
				else if (!colored) { ci = C_VOL; alpha = op * 0.5f; }
			}

			Fill(x, yT, w, h, ci, alpha);

			if (BorderWidth > 0)
			{
				DXBrush b = dx[ci];
				b.Opacity = Math.Min(1f, alpha + 0.3f);
				RenderTarget.DrawRectangle(new SharpDX.RectangleF(x, yT, w, h), b, BorderWidth);
			}
		}

		private void DrawCellText(int kind, int r, float x1, float x2, bool alignRight, float yT, float h, float fsBase, float fsCap)
		{
			double val = CellValue(kind, r);
			if (val == 0.0 && !ShowZeros) { return; }

			float w = x2 - x1 - 3f;
			if (w < 4f) { return; }

			float fs = fsBase;

			if (DynamicTextSize && IncreaseDynamicTextSize > 0)
			{
				double mx = CellMax(kind);
				if (mx > 0) fs = Math.Min(fsCap, fsBase + (float)Math.Round(IncreaseDynamicTextSize * Math.Abs(val) / mx));
			}

			string s = FormatValue(val, NumberFormat == DPNumberFormat.K);

			if (s.Length * cw * fs > w)
			{
				if (NumberFormat == DPNumberFormat.Automatic) s = FormatValue(val, true);

				if (s.Length * cw * fs > w)
				{
					fs = (float)Math.Floor(w / (s.Length * cw));
					if (fs < MinFont) { return; }
				}
			}

			bool bold;
			int ci = TextColorIndex(kind, r, out bold);

			DWTextFormat tf = GetFormat((int)fs, bold, alignRight ? 1 : 0);
			DXBrush b = dx[ci];
			b.Opacity = 1f;

			RenderTarget.DrawText(s, tf, new SharpDX.RectangleF(x1 + 1.5f, yT, w, h), b);
		}

		private void DrawMarker(ChartScale cs, int i, float mL, float mR)
		{
			double o = Bars.GetOpen(i), c = Bars.GetClose(i), hi = Bars.GetHigh(i), lo = Bars.GetLow(i);

			int ci = (c > o) ? C_UP : (c < o) ? C_DN : C_WICK;
			bool pb = PullbackEnable && IsPullback(o, c, hi, lo);
			if (pb) ci = C_PB;

			float yO = cs.GetYByValue(o);
			float yC = cs.GetYByValue(c);
			float yH = cs.GetYByValue(hi);
			float yL = cs.GetYByValue(lo);

			if (OutsideBarStyle == DPOutsideBar.Bar)
			{
				DXBrush wb = pb ? dx[C_PB] : dx[C_WICK];
				wb.Opacity = 1f;

				float xm = (float)Math.Floor((mL + mR) * 0.5f) + 0.5f;
				RenderTarget.DrawLine(new SharpDX.Vector2(xm, yH), new SharpDX.Vector2(xm, yL), wb, 1f);
			}

			float top = Math.Min(yO, yC);
			float bh = Math.Max(1f, Math.Abs(yO - yC));

			Fill(mL, top, Math.Max(1f, mR - mL), bh, ci, 1f);
		}

		private bool IsPullback(double o, double c, double hi, double lo)
		{
			double rangeTicks = (hi - lo) / TickSize;
			if (rangeTicks + 1e-9 < PullbackMinSourceTicks) { return false; }

			double pbTicks = (c >= o) ? (hi - c) / TickSize : (c - lo) / TickSize;
			return pbTicks + 1e-9 >= PullbackMinPullbackTicks;
		}

		#endregion

		#region Chart toolbar menu

		private System.Windows.Controls.Menu dpMenu;
		private NinjaTrader.Gui.Chart.Chart chartWindow;
		private NTMenuItem dpTop;
		private NTMenuItem dataInfoItem;
		private NTMenuItem bigTradeInfoItem;

		private void InsertMenu()
		{
			try
			{
				if (dpMenu != null || ChartControl == null) { return; }

				chartWindow = System.Windows.Window.GetWindow(ChartControl.Parent) as NinjaTrader.Gui.Chart.Chart;
				if (chartWindow == null) { return; }

				dpMenu = new System.Windows.Controls.Menu();
				dpMenu.VerticalAlignment = System.Windows.VerticalAlignment.Center;
				dpMenu.Style = System.Windows.Application.Current.TryFindResource("SystemMenuStyle") as System.Windows.Style;

				string header = (ColumnCount > 1) ? "NT8Print " + ColumnPosition + "/" + ColumnCount : "NT8Print";

				dpTop = MI(header);
				dpTop.Icon = System.Windows.Media.Geometry.Parse("M2,5H10V2H12V22H10V18H6V15H10V13H4V10H10V8H2V5M14,5H17V8H14V5M14,10H19V13H14V10M14,15H22V18H14V15Z");
				dpTop.SubmenuOpened += OnSubmenuOpened;
				dpMenu.Items.Add(dpTop);

				BuildMenu(dpTop);

				chartWindow.MainMenu.Add(dpMenu);
				chartWindow.MainTabControl.SelectionChanged += OnTabChanged;
			}
			catch (Exception ex)
			{
				Print("NT8Print toolbar: " + ex.Message);
			}
		}

		private void RemoveMenu()
		{
			try
			{
				if (chartWindow != null)
				{
					if (dpMenu != null) chartWindow.MainMenu.Remove(dpMenu);
					chartWindow.MainTabControl.SelectionChanged -= OnTabChanged;
				}
			}
			catch { }

			dpMenu = null;
			dpTop = null;
			dataInfoItem = null;
			chartWindow = null;
		}

		/// only show the menu while this chart's tab is selected
		private void OnTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
		{
			if (dpMenu == null || e.AddedItems.Count <= 0) { return; }

			System.Windows.Controls.TabItem ti = e.AddedItems[0] as System.Windows.Controls.TabItem;
			if (ti == null) { return; }

			NinjaTrader.Gui.Chart.ChartTab tab = ti.Content as NinjaTrader.Gui.Chart.ChartTab;
			if (tab == null) { return; }

			dpMenu.Visibility = (tab.ChartControl == ChartControl) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
		}

		private void BuildMenu(NTMenuItem top)
		{
			NTMenuItem m, s;

			Toggle(top, "Show Footprint", () => ShowFootprint, v => ShowFootprint = v);
			Toggle(top, "Show Numbers", () => ShowNumbers, v => ShowNumbers = v);

			m = Sub(top, "Type");
			EnumChoices(m, () => FootprintType, v => FootprintType = v);

			m = Sub(top, "Mode");
			EnumChoices(m, () => FootprintMode, v => FootprintMode = v);

			top.Items.Add(new System.Windows.Controls.Separator());

			// tick grouping
			m = Sub(top, "Tick Grouping");
			EnumChoices(m, () => TickGrouping, v => TickGrouping = v);
			m.Items.Add(new System.Windows.Controls.Separator());

			s = Sub(m, "Manual Ticks");
			IntChoices(s, new int[] { 1, 2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 25, 50, 100 }, "{0} ticks",
				() => TickGrouping == DPGrouping.Manual ? ManualTicks : int.MinValue,
				v => { ManualTicks = v; TickGrouping = DPGrouping.Manual; });

			s = Sub(m, "Auto Group Factor");
			DoubleChoices(s, new double[] { 0.5, 0.75, 1.0, 1.5, 2.0, 3.0 }, "x{0}",
				() => TickGrouping == DPGrouping.Automatic ? AutoGroupFactor : double.NaN,
				v => { AutoGroupFactor = v; TickGrouping = DPGrouping.Automatic; });

			s = Sub(m, "Group Mode");
			EnumChoices(s, () => GroupMode, v => GroupMode = v);

			// coloration
			m = Sub(top, "Coloration");

			s = Sub(m, "Color Mode");
			EnumChoices(s, () => ColorMode, v => ColorMode = v);

			s = Sub(m, "Color Calculation");
			EnumChoices(s, () => ColorCalculation, v => ColorCalculation = v);

			s = Sub(m, "Imbalance Ratio");
			DoubleChoices(s, new double[] { 150, 200, 250, 300, 400, 500 }, "{0}%", () => ImbalanceRatio, v => ImbalanceRatio = v);

			Toggle(m, "Include Zero on Imbalance", () => IncludeZeroOnImbalance, v => IncludeZeroOnImbalance = v);
			Toggle(m, "Color Only Dominant Side", () => ColorOnlyDominantSide, v => ColorOnlyDominantSide = v);

			s = Sub(m, "Opacity");
			IntChoices(s, new int[] { 20, 30, 40, 55, 70, 85, 100 }, "{0}%", () => BackgroundOpacity, v => BackgroundOpacity = v);

			s = Sub(m, "Border Width");
			IntChoices(s, new int[] { 0, 1, 2 }, "{0}", () => BorderWidth, v => BorderWidth = v);

			s = Sub(m, "Normalize");
			EnumChoices(s, () => Normalize, v => Normalize = v);

			s = Sub(m, "Range Definition");
			EnumChoices(s, () => RangeDefinition, v => RangeDefinition = v);

			// text
			m = Sub(top, "Text");

			s = Sub(m, "Font Size");
			IntChoices(s, new int[] { 8, 9, 10, 11, 12, 13, 14, 16, 18 }, "{0}", () => FontSize, v => FontSize = v);

			s = Sub(m, "Text Format");
			EnumChoices(s, () => NumberFormat, v => NumberFormat = v);

			s = Sub(m, "Coloration");
			EnumChoices(s, () => TextColoration, v => TextColoration = v);

			Toggle(m, "Dynamic Text Size", () => DynamicTextSize, v => DynamicTextSize = v);

			s = Sub(m, "Increase Dynamic Text Size");
			IntChoices(s, new int[] { 1, 2, 3, 4, 6, 8 }, "+{0}", () => IncreaseDynamicTextSize, v => IncreaseDynamicTextSize = v);

			Toggle(m, "Show Texts-by-Zeros", () => ShowZeros, v => ShowZeros = v);

			// bar style
			m = Sub(top, "Bar Style");

			Toggle(m, "Show Bar Marker", () => ShowBarMarker, v => ShowBarMarker = v);

			s = Sub(m, "Bar Marker Width");
			IntChoices(s, new int[] { 2, 3, 4, 6, 8, 10 }, "{0} px", () => BarMarkerWidth, v => BarMarkerWidth = v);

			s = Sub(m, "Outside Bar Style");
			EnumChoices(s, () => OutsideBarStyle, v => OutsideBarStyle = v);

			s = Sub(m, "Marker Alignment");
			EnumChoices(s, () => MarkerAlignment, v => MarkerAlignment = v);

			Toggle(m, "Outer Edge Mode", () => OuterEdgeMode, v => OuterEdgeMode = v);

			// POC / value area
			m = Sub(top, "POC / Value Area");

			Toggle(m, "Volume POC", () => VolumePocEnable, v => VolumePocEnable = v);
			Toggle(m, "Delta POC", () => DeltaPocEnable, v => DeltaPocEnable = v);
			m.Items.Add(new System.Windows.Controls.Separator());
			Toggle(m, "Value Area", () => ValueAreaEnable, v => ValueAreaEnable = v);

			s = Sub(m, "% Value Area");
			DoubleChoices(s, new double[] { 60, 68, 70, 75, 80 }, "{0}%", () => ValueAreaPercent, v => ValueAreaPercent = v);

			Toggle(m, "Highlight Outside VA", () => ValueAreaHighlight, v => ValueAreaHighlight = v);
			Toggle(m, "Value Area Lines", () => ValueAreaShowLine, v => ValueAreaShowLine = v);

			// signals
			m = Sub(top, "Signals");

			Toggle(m, "Single Print", () => SinglePrintEnable, v => SinglePrintEnable = v);
			Toggle(m, "Single Print Only On High/Low", () => SinglePrintOnlyHighLow, v => SinglePrintOnlyHighLow = v);
			Toggle(m, "Ratio", () => RatioEnable, v => RatioEnable = v);
			Toggle(m, "Vol Cluster", () => VolClusterEnable, v => VolClusterEnable = v);

			s = Sub(m, "Vol Cluster Min. Volume");
			DoubleChoices(s, new double[] { 100, 250, 500, 1000, 2500, 5000 }, "{0}", () => VolClusterMinVolume, v => VolClusterMinVolume = v);

			Toggle(m, "Pull Back", () => PullbackEnable, v => PullbackEnable = v);

			// alerts
			m = Sub(top, "Alerts");

			Toggle(m, "Enable Sound", () => AlertSound, v => AlertSound = v);
			Toggle(m, "Enable Message", () => AlertMessageEnable, v => AlertMessageEnable = v);

			s = Sub(m, "Alert Minimum Volume");
			DoubleChoices(s, new double[] { 100, 250, 500, 1000, 2500, 5000 }, "{0}", () => AlertMinVolume, v => AlertMinVolume = v);

			// bar stats
			m = Sub(top, "Bar Stats");

			Toggle(m, "Show Bar Stats", () => ShowBarStats, v => ShowBarStats = v);
			m.Items.Add(new System.Windows.Controls.Separator());
			Toggle(m, "Total Volume", () => StatsTotalVolume, v => StatsTotalVolume = v);
			Toggle(m, "Delta Volume", () => StatsDelta, v => StatsDelta = v);
			Toggle(m, "Delta %", () => StatsDeltaPercent, v => StatsDeltaPercent = v);
			Toggle(m, "Big Trades", () => StatsBigTrades, v => StatsBigTrades = v);
			m.Items.Add(new System.Windows.Controls.Separator());

			s = Sub(m, "Position");
			EnumChoices(s, () => StatsPosition, v => StatsPosition = v);

			s = Sub(m, "Font Size");
			IntChoices(s, new int[] { 7, 8, 9, 10, 11, 12 }, "{0}", () => StatsFontSize, v => StatsFontSize = v);

			Toggle(m, "Row Labels", () => StatsShowLabels, v => StatsShowLabels = v);
			Toggle(m, "Colored Cells", () => StatsColored, v => StatsColored = v);

			bigTradeInfoItem = MI("");
			bigTradeInfoItem.IsEnabled = false;
			m.Items.Add(bigTradeInfoItem);

			// columns
			m = Sub(top, "Multi Column");

			s = Sub(m, "Columns");
			IntChoices(s, new int[] { 1, 2, 3, 4 }, "{0}", () => ColumnCount, v => { ColumnCount = v; if (ColumnPosition > v) ColumnPosition = v; UpdateTopHeader(); });

			s = Sub(m, "Position");
			IntChoices(s, new int[] { 1, 2, 3, 4 }, "{0}", () => ColumnPosition, v => { ColumnPosition = Math.Min(v, ColumnCount); UpdateTopHeader(); });

			// data settings need a reload - shown for reference only
			top.Items.Add(new System.Windows.Controls.Separator());

			dataInfoItem = MI("");
			dataInfoItem.IsEnabled = false;
			top.Items.Add(dataInfoItem);
			UpdateDataInfo();
		}

		private void UpdateTopHeader()
		{
			if (dpTop != null) dpTop.Header = (ColumnCount > 1) ? "NT8Print " + ColumnPosition + "/" + ColumnCount : "NT8Print";
		}

		private void UpdateDataInfo()
		{
			if (bigTradeInfoItem != null)
			{
				bigTradeInfoItem.Header = "Big trade >= " + BigTradeMin.ToString(Inv) + (BigTradeAggregate ? " (aggregated)" : " (single print)") + "  - change in properties";
			}

			if (dataInfoItem == null) { return; }

			string max = MaxFilter > 0 ? MaxFilter.ToString(Inv) : "none";
			dataInfoItem.Header = "Data: " + Pretty(InputType.ToString()) + ", filter " + MinFilter.ToString(Inv) + " - " + max + "  (change in Indicator properties)";
		}

		private NTMenuItem MI(string header)
		{
			NTMenuItem m = new NTMenuItem();
			m.Header = header;
			m.Style = System.Windows.Application.Current.TryFindResource("MainMenuItem") as System.Windows.Style;
			return m;
		}

		private NTMenuItem Sub(NTMenuItem parent, string header)
		{
			NTMenuItem m = MI(header);
			m.SubmenuOpened += OnSubmenuOpened;
			parent.Items.Add(m);
			return m;
		}

		private void Toggle(NTMenuItem parent, string header, Func<bool> get, Action<bool> set)
		{
			NTMenuItem m = MI(header);
			m.IsCheckable = true;
			m.StaysOpenOnClick = true;
			m.IsChecked = get();
			m.Tag = get;
			m.Click += (sender, e) =>
			{
				set(!get());
				m.IsChecked = get();
				Redraw();
			};
			parent.Items.Add(m);
		}

		private void Choice<T>(NTMenuItem parent, T value, string header, Func<T> get, Action<T> set)
		{
			NTMenuItem m = MI(header);
			Func<bool> isOn = () => EqualityComparer<T>.Default.Equals(get(), value);

			m.IsCheckable = true;
			m.StaysOpenOnClick = true;
			m.IsChecked = isOn();
			m.Tag = isOn;
			m.Click += (sender, e) =>
			{
				set(value);
				SyncChecks(parent);
				Redraw();
			};
			parent.Items.Add(m);
		}

		private void EnumChoices<T>(NTMenuItem parent, Func<T> get, Action<T> set)
		{
			foreach (T v in Enum.GetValues(typeof(T)))
			{
				Choice(parent, v, Pretty(v.ToString()), get, set);
			}
		}

		private void IntChoices(NTMenuItem parent, int[] values, string fmt, Func<int> get, Action<int> set)
		{
			foreach (int v in values)
			{
				Choice(parent, v, string.Format(Inv, fmt, v), get, set);
			}
		}

		private void DoubleChoices(NTMenuItem parent, double[] values, string fmt, Func<double> get, Action<double> set)
		{
			foreach (double v in values)
			{
				Choice(parent, v, string.Format(Inv, fmt, v), get, set);
			}
		}

		/// re-reads the current values whenever a submenu opens (values may change in the properties dialog)
		private void OnSubmenuOpened(object sender, System.Windows.RoutedEventArgs e)
		{
			NTMenuItem m = sender as NTMenuItem;
			if (m != null) SyncChecks(m);
			UpdateDataInfo();
		}

		private static void SyncChecks(NTMenuItem parent)
		{
			foreach (object o in parent.Items)
			{
				NTMenuItem mi = o as NTMenuItem;
				if (mi == null) { continue; }

				Func<bool> f = mi.Tag as Func<bool>;
				if (f != null) mi.IsChecked = f();
			}
		}

		private void Redraw()
		{
			if (ChartControl != null) ChartControl.InvalidateVisual();
		}

		private static string Pretty(string name)
		{
			switch (name)
			{
				case "AskBidSplit": return "Ask/Bid Split";
				case "DeltaVolume": return "Delta + Total Volume";
				case "AggregatedVolume": return "Aggregate Volume";
				case "NumTrades": return "Num Trades";
				case "OpenClose": return "Based on Open/Close";
				case "MoreColors": return "More Colors";
				case "DominantVolume": return "Dominant Volume";
				case "DominantVolumeDelta": return "Dominant Volume Delta";
				case "PerBar": return "Per Bar";
				case "Visible": return "Visible Bars";
				case "BelowBar": return "Below Each Bar";
				case "PanelBottom": return "Bottom of Panel";
				case "K": return "K (thousands)";
				case "BySide": return "By Side";
				case "ByImbalance": return "By Imbalance";
				default: return name;
			}
		}

		#endregion

		#region Properties

		// --- Typology

		[Display(Name = "Type", Description = "Data shown in each cell", GroupName = "Typology", Order = 1)]
		public DPType FootprintType { get; set; }

		[Display(Name = "Mode", Description = "Profile = histogram, Box = compact boxes", GroupName = "Typology", Order = 2)]
		public DPMode FootprintMode { get; set; }

		[Display(Name = "Show Footprint", Description = "Off = bar markers only", GroupName = "Typology", Order = 0)]
		public bool ShowFootprint { get; set; }

		[Display(Name = "Show Numbers", Description = "Show the values on the boxes / profile bars", GroupName = "Typology", Order = 3)]
		public bool ShowNumbers { get; set; }

		// --- Data Settings (data-affecting => NinjaScriptProperty)

		[NinjaScriptProperty]
		[Display(Name = "Input Type", Description = "Volume, Aggregated trades volume, Order book (real-time) or Number of trades", GroupName = "Data Settings", Order = 1)]
		public DPInput InputType { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Min Filter", Description = "Ignore trades (or book sizes) smaller than this. 0 = off", GroupName = "Data Settings", Order = 2)]
		public double MinFilter { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Max Filter", Description = "Ignore trades (or book sizes) larger than this. 0 = off", GroupName = "Data Settings", Order = 3)]
		public double MaxFilter { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Max Bars Stored", Description = "Footprint data kept for the last N bars (memory cap). 0 = all", GroupName = "Data Settings", Order = 4)]
		public int MaxBarsStored { get; set; }

		// --- Tick Grouping

		[Display(Name = "Auto Grouping", GroupName = "Tick Grouping", Order = 1)]
		public DPGrouping TickGrouping { get; set; }

		[Range(0.25, 10.0)]
		[Display(Name = "Auto Group Factor", Description = "Higher = more ticks per row when zoomed out", GroupName = "Tick Grouping", Order = 2)]
		public double AutoGroupFactor { get; set; }

		[Range(1, 1000)]
		[Display(Name = "Manual Ticks", Description = "Ticks per row when Auto Grouping = Manual", GroupName = "Tick Grouping", Order = 3)]
		public int ManualTicks { get; set; }

		[Display(Name = "Group Mode", Description = "Fixed price grid, or rows anchored on the bar's open", GroupName = "Tick Grouping", Order = 4)]
		public DPGroupMode GroupMode { get; set; }

		// --- Bar Style

		[Display(Name = "Show Bar Marker", GroupName = "Bar Style", Order = 1)]
		public bool ShowBarMarker { get; set; }

		[Range(0, 50)]
		[Display(Name = "Bar Marker Width", GroupName = "Bar Style", Order = 2)]
		public int BarMarkerWidth { get; set; }

		[Display(Name = "Outside Bar Style", Description = "Bar = body + wick, Body = body only", GroupName = "Bar Style", Order = 3)]
		public DPOutsideBar OutsideBarStyle { get; set; }

		[Display(Name = "Marker Alignment", GroupName = "Bar Style", Order = 4)]
		public DPAlign MarkerAlignment { get; set; }

		[Display(Name = "Outer Edge Mode", Description = "Outline around the body range of the footprint", GroupName = "Bar Style", Order = 5)]
		public bool OuterEdgeMode { get; set; }

		[Range(1, 10)]
		[Display(Name = "Outer Edge Width", GroupName = "Bar Style", Order = 6)]
		public int OuterEdgeWidth { get; set; }

		[XmlIgnore]
		[Display(Name = "Bar Up Color", GroupName = "Bar Style", Order = 7)]
		public Brush BarUpColor { get; set; }
		[Browsable(false)]
		public string BarUpColorSerialize { get { return Serialize.BrushToString(BarUpColor); } set { BarUpColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Bar Down Color", GroupName = "Bar Style", Order = 8)]
		public Brush BarDownColor { get; set; }
		[Browsable(false)]
		public string BarDownColorSerialize { get { return Serialize.BrushToString(BarDownColor); } set { BarDownColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Wick / Doji Color", GroupName = "Bar Style", Order = 9)]
		public Brush WickColor { get; set; }
		[Browsable(false)]
		public string WickColorSerialize { get { return Serialize.BrushToString(WickColor); } set { WickColor = Serialize.StringToBrush(value); } }

		[Display(Name = "Hide Chart Bars", Description = "Make the chart's own candles transparent", GroupName = "Bar Style", Order = 10)]
		public bool HideChartBars { get; set; }

		// --- Background

		[Display(Name = "Color Mode", GroupName = "Background", Order = 1)]
		public DPColorMode ColorMode { get; set; }

		[Display(Name = "Color Calculation", GroupName = "Background", Order = 2)]
		public DPColorCalc ColorCalculation { get; set; }

		[Range(100, 100000)]
		[Display(Name = "Imbalance Ratio %", Description = "300 = one side at least 3x the diagonal opposite", GroupName = "Background", Order = 3)]
		public double ImbalanceRatio { get; set; }

		[Range(0, double.MaxValue)]
		[Display(Name = "Min. Vol. Diff. For Imbalance", GroupName = "Background", Order = 4)]
		public double MinVolDiffImbalance { get; set; }

		[Display(Name = "Include Zero on Imbalance", GroupName = "Background", Order = 5)]
		public bool IncludeZeroOnImbalance { get; set; }

		[Display(Name = "Color Only Dominant Side", GroupName = "Background", Order = 6)]
		public bool ColorOnlyDominantSide { get; set; }

		[XmlIgnore]
		[Display(Name = "Background Inferior Side", Description = "Fill for the non-dominant side (Transparent = none)", GroupName = "Background", Order = 7)]
		public Brush InferiorColor { get; set; }
		[Browsable(false)]
		public string InferiorColorSerialize { get { return Serialize.BrushToString(InferiorColor); } set { InferiorColor = Serialize.StringToBrush(value); } }

		[Range(0, 5)]
		[Display(Name = "Border Width", GroupName = "Background", Order = 8)]
		public int BorderWidth { get; set; }

		[Range(0, 100)]
		[Display(Name = "Opacity", GroupName = "Background", Order = 9)]
		public int BackgroundOpacity { get; set; }

		[Display(Name = "Normalize", Description = "Scale colours / profile per bar or across visible bars", GroupName = "Background", Order = 10)]
		public DPNormalize Normalize { get; set; }

		// --- Color

		[XmlIgnore]
		[Display(Name = "Fixed-Volume Color", GroupName = "Color", Order = 1)]
		public Brush VolumeColor { get; set; }
		[Browsable(false)]
		public string VolumeColorSerialize { get { return Serialize.BrushToString(VolumeColor); } set { VolumeColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Fixed-Bid Color", GroupName = "Color", Order = 2)]
		public Brush BidColor { get; set; }
		[Browsable(false)]
		public string BidColorSerialize { get { return Serialize.BrushToString(BidColor); } set { BidColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Fixed-Ask Color", GroupName = "Color", Order = 3)]
		public Brush AskColor { get; set; }
		[Browsable(false)]
		public string AskColorSerialize { get { return Serialize.BrushToString(AskColor); } set { AskColor = Serialize.StringToBrush(value); } }

		// --- Range

		[Display(Name = "Range Definition", Description = "Automatic = relative to max, User = the level values below", GroupName = "Range", Order = 1)]
		public DPRange RangeDefinition { get; set; }

		[Range(0, double.MaxValue)]
		[Display(Name = "Level 1 Value", GroupName = "Range", Order = 2)]
		public double Level1Value { get; set; }

		[XmlIgnore]
		[Display(Name = "Level 1 Color", GroupName = "Range", Order = 3)]
		public Brush Level1Color { get; set; }
		[Browsable(false)]
		public string Level1ColorSerialize { get { return Serialize.BrushToString(Level1Color); } set { Level1Color = Serialize.StringToBrush(value); } }

		[Range(0, double.MaxValue)]
		[Display(Name = "Level 2 Value", GroupName = "Range", Order = 4)]
		public double Level2Value { get; set; }

		[XmlIgnore]
		[Display(Name = "Level 2 Color", GroupName = "Range", Order = 5)]
		public Brush Level2Color { get; set; }
		[Browsable(false)]
		public string Level2ColorSerialize { get { return Serialize.BrushToString(Level2Color); } set { Level2Color = Serialize.StringToBrush(value); } }

		[Range(0, double.MaxValue)]
		[Display(Name = "Level 3 Value", GroupName = "Range", Order = 6)]
		public double Level3Value { get; set; }

		[XmlIgnore]
		[Display(Name = "Level 3 Color", GroupName = "Range", Order = 7)]
		public Brush Level3Color { get; set; }
		[Browsable(false)]
		public string Level3ColorSerialize { get { return Serialize.BrushToString(Level3Color); } set { Level3Color = Serialize.StringToBrush(value); } }

		[Range(0, double.MaxValue)]
		[Display(Name = "Level 4 Value", GroupName = "Range", Order = 8)]
		public double Level4Value { get; set; }

		[XmlIgnore]
		[Display(Name = "Level 4 Color", GroupName = "Range", Order = 9)]
		public Brush Level4Color { get; set; }
		[Browsable(false)]
		public string Level4ColorSerialize { get { return Serialize.BrushToString(Level4Color); } set { Level4Color = Serialize.StringToBrush(value); } }

		[Range(0, double.MaxValue)]
		[Display(Name = "Level 5 Value", Description = "Also the full-intensity cap for Fading with User range", GroupName = "Range", Order = 10)]
		public double Level5Value { get; set; }

		[XmlIgnore]
		[Display(Name = "Level 5 Color", GroupName = "Range", Order = 11)]
		public Brush Level5Color { get; set; }
		[Browsable(false)]
		public string Level5ColorSerialize { get { return Serialize.BrushToString(Level5Color); } set { Level5Color = Serialize.StringToBrush(value); } }

		// --- Text

		[Range(6, 40)]
		[Display(Name = "Font Size", Description = "Maximum size; shrinks automatically to fit the row", GroupName = "Text", Order = 1)]
		public int FontSize { get; set; }

		[Display(Name = "Text Format", Description = "Automatic = full number, K/M when it doesn't fit", GroupName = "Text", Order = 2)]
		public DPNumberFormat NumberFormat { get; set; }

		[Display(Name = "Coloration", GroupName = "Text", Order = 3)]
		public DPTextColor TextColoration { get; set; }

		[XmlIgnore]
		[Display(Name = "Text Color", GroupName = "Text", Order = 4)]
		public Brush TextColor { get; set; }
		[Browsable(false)]
		public string TextColorSerialize { get { return Serialize.BrushToString(TextColor); } set { TextColor = Serialize.StringToBrush(value); } }

		[Display(Name = "Dynamic Text Size", Description = "Larger values get larger text", GroupName = "Text", Order = 5)]
		public bool DynamicTextSize { get; set; }

		[Range(0, 20)]
		[Display(Name = "Increase Dynamic Text Size", GroupName = "Text", Order = 6)]
		public int IncreaseDynamicTextSize { get; set; }

		[Display(Name = "Show Texts-by-Zeros", GroupName = "Text", Order = 7)]
		public bool ShowZeros { get; set; }

		// --- POC

		[Display(Name = "Enable (Volume POC)", GroupName = "POC", Order = 1)]
		public bool VolumePocEnable { get; set; }

		[XmlIgnore]
		[Display(Name = "Border Color (Volume POC)", GroupName = "POC", Order = 2)]
		public Brush VolPocColor { get; set; }
		[Browsable(false)]
		public string VolPocColorSerialize { get { return Serialize.BrushToString(VolPocColor); } set { VolPocColor = Serialize.StringToBrush(value); } }

		[Range(1, 10)]
		[Display(Name = "Border Width (Volume POC)", GroupName = "POC", Order = 3)]
		public int VolPocWidth { get; set; }

		[Display(Name = "Enable (Delta POC)", GroupName = "POC", Order = 4)]
		public bool DeltaPocEnable { get; set; }

		[Range(0, double.MaxValue)]
		[Display(Name = "Minimum Delta (Delta POC)", GroupName = "POC", Order = 5)]
		public double DeltaPocMin { get; set; }

		[XmlIgnore]
		[Display(Name = "Ask Border Color (Delta POC)", GroupName = "POC", Order = 6)]
		public Brush DeltaPocAskColor { get; set; }
		[Browsable(false)]
		public string DeltaPocAskColorSerialize { get { return Serialize.BrushToString(DeltaPocAskColor); } set { DeltaPocAskColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Bid Border Color (Delta POC)", GroupName = "POC", Order = 7)]
		public Brush DeltaPocBidColor { get; set; }
		[Browsable(false)]
		public string DeltaPocBidColorSerialize { get { return Serialize.BrushToString(DeltaPocBidColor); } set { DeltaPocBidColor = Serialize.StringToBrush(value); } }

		[Range(1, 10)]
		[Display(Name = "Border Width (Delta POC)", GroupName = "POC", Order = 8)]
		public int DeltaPocWidth { get; set; }

		// --- Value Area

		[Display(Name = "Enable (Value Area)", GroupName = "Value Area", Order = 1)]
		public bool ValueAreaEnable { get; set; }

		[Range(1, 100)]
		[Display(Name = "% Value Area", GroupName = "Value Area", Order = 2)]
		public double ValueAreaPercent { get; set; }

		[Display(Name = "Highlight", Description = "Shade the rows outside the value area", GroupName = "Value Area", Order = 3)]
		public bool ValueAreaHighlight { get; set; }

		[XmlIgnore]
		[Display(Name = "OutSide Color", GroupName = "Value Area", Order = 4)]
		public Brush VaOutsideColor { get; set; }
		[Browsable(false)]
		public string VaOutsideColorSerialize { get { return Serialize.BrushToString(VaOutsideColor); } set { VaOutsideColor = Serialize.StringToBrush(value); } }

		[Display(Name = "Show Line", GroupName = "Value Area", Order = 5)]
		public bool ValueAreaShowLine { get; set; }

		[XmlIgnore]
		[Display(Name = "Line Color", GroupName = "Value Area", Order = 6)]
		public Brush VaLineColor { get; set; }
		[Browsable(false)]
		public string VaLineColorSerialize { get { return Serialize.BrushToString(VaLineColor); } set { VaLineColor = Serialize.StringToBrush(value); } }

		[Range(1, 10)]
		[Display(Name = "Line Width", GroupName = "Value Area", Order = 7)]
		public int ValueAreaLineWidth { get; set; }

		// --- Alert Settings

		[Display(Name = "Enable Sound", GroupName = "Alert Settings", Order = 1)]
		public bool AlertSound { get; set; }

		[Display(Name = "Alert Sound", GroupName = "Alert Settings", Order = 2)]
		[PropertyEditor("NinjaTrader.Gui.Tools.FilePathPicker", Filter = "Wav Files (*.wav)|*.wav")]
		public string AlertSoundFile { get; set; }

		[Display(Name = "Enable Message", GroupName = "Alert Settings", Order = 3)]
		public bool AlertMessageEnable { get; set; }

		[Display(Name = "Alert Message", GroupName = "Alert Settings", Order = 4)]
		public string AlertMessage { get; set; }

		[Range(0, double.MaxValue)]
		[Display(Name = "Alert Minimum Volume", Description = "Alert when a price level in the live bar reaches this value", GroupName = "Alert Settings", Order = 5)]
		public double AlertMinVolume { get; set; }

		// --- Single Print

		[Display(Name = "Enable", GroupName = "Single Print", Order = 1)]
		public bool SinglePrintEnable { get; set; }

		[Range(0, double.MaxValue)]
		[Display(Name = "Single Print Min Value", Description = "Level traded on one side only, with at least this value", GroupName = "Single Print", Order = 2)]
		public double SinglePrintMinValue { get; set; }

		[Display(Name = "Enable Only On High Or Low", GroupName = "Single Print", Order = 3)]
		public bool SinglePrintOnlyHighLow { get; set; }

		[Range(1, 10)]
		[Display(Name = "Border Width", GroupName = "Single Print", Order = 4)]
		public int SinglePrintWidth { get; set; }

		[XmlIgnore]
		[Display(Name = "Border Color", GroupName = "Single Print", Order = 5)]
		public Brush SinglePrintColor { get; set; }
		[Browsable(false)]
		public string SinglePrintColorSerialize { get { return Serialize.BrushToString(SinglePrintColor); } set { SinglePrintColor = Serialize.StringToBrush(value); } }

		// --- Ratio

		[Display(Name = "Enabled", Description = "Low: bid(low+1)/bid(low). High: ask(high-1)/ask(high)", GroupName = "Ratio", Order = 1)]
		public bool RatioEnable { get; set; }

		[Range(0, 50)]
		[Display(Name = "Tick Offset", GroupName = "Ratio", Order = 2)]
		public int RatioTickOffset { get; set; }

		[XmlIgnore]
		[Display(Name = "Text Color", GroupName = "Ratio", Order = 3)]
		public Brush RatioTextColor { get; set; }
		[Browsable(false)]
		public string RatioTextColorSerialize { get { return Serialize.BrushToString(RatioTextColor); } set { RatioTextColor = Serialize.StringToBrush(value); } }

		[Range(6, 40)]
		[Display(Name = "Text Size", GroupName = "Ratio", Order = 4)]
		public int RatioTextSize { get; set; }

		[Range(0, double.MaxValue)]
		[Display(Name = "Min Ratio", GroupName = "Ratio", Order = 5)]
		public double RatioMin { get; set; }

		[Range(0, double.MaxValue)]
		[Display(Name = "Maximum Ratio", Description = "0 = no maximum", GroupName = "Ratio", Order = 6)]
		public double RatioMax { get; set; }

		// --- Vol Cluster

		[Display(Name = "Enable", GroupName = "Vol Cluster", Order = 1)]
		public bool VolClusterEnable { get; set; }

		[Range(0, double.MaxValue)]
		[Display(Name = "Min. Volume", GroupName = "Vol Cluster", Order = 2)]
		public double VolClusterMinVolume { get; set; }

		[Display(Name = "Increase Volume Based on Tick Grouped", Description = "Min. Volume x ticks per row", GroupName = "Vol Cluster", Order = 3)]
		public bool VolClusterScaleByGroup { get; set; }

		[Range(0, 10)]
		[Display(Name = "Line Width", GroupName = "Vol Cluster", Order = 4)]
		public int VolClusterLineWidth { get; set; }

		[XmlIgnore]
		[Display(Name = "Back Color", GroupName = "Vol Cluster", Order = 5)]
		public Brush VolClusterColor { get; set; }
		[Browsable(false)]
		public string VolClusterColorSerialize { get { return Serialize.BrushToString(VolClusterColor); } set { VolClusterColor = Serialize.StringToBrush(value); } }

		[Range(0, 100)]
		[Display(Name = "Opacity", GroupName = "Vol Cluster", Order = 6)]
		public int VolClusterOpacity { get; set; }

		// --- Pull Back

		[Display(Name = "Enable", GroupName = "Pull Back", Order = 1)]
		public bool PullbackEnable { get; set; }

		[Range(1, 10000)]
		[Display(Name = "Pullback Min Source Bar Ticks", GroupName = "Pull Back", Order = 2)]
		public int PullbackMinSourceTicks { get; set; }

		[Range(1, 10000)]
		[Display(Name = "Pullback Min Pullback Ticks", GroupName = "Pull Back", Order = 3)]
		public int PullbackMinPullbackTicks { get; set; }

		[XmlIgnore]
		[Display(Name = "Bar Color", GroupName = "Pull Back", Order = 4)]
		public Brush PullbackColor { get; set; }
		[Browsable(false)]
		public string PullbackColorSerialize { get { return Serialize.BrushToString(PullbackColor); } set { PullbackColor = Serialize.StringToBrush(value); } }

		// --- Bar Stats

		[Display(Name = "Show Bar Stats", GroupName = "Bar Stats", Order = 1)]
		public bool ShowBarStats { get; set; }

		[Display(Name = "Total Volume", GroupName = "Bar Stats", Order = 2)]
		public bool StatsTotalVolume { get; set; }

		[Display(Name = "Delta Volume", GroupName = "Bar Stats", Order = 3)]
		public bool StatsDelta { get; set; }

		[Display(Name = "Delta %", Description = "Delta / total volume x 100", GroupName = "Bar Stats", Order = 4)]
		public bool StatsDeltaPercent { get; set; }

		[Display(Name = "Big Trades", Description = "Count of big buy (B) / sell (S) trades in the bar", GroupName = "Bar Stats", Order = 5)]
		public bool StatsBigTrades { get; set; }

		[Display(Name = "Position", Description = "Under each bar, or a strip at the bottom of the panel", GroupName = "Bar Stats", Order = 6)]
		public DPStatsPosition StatsPosition { get; set; }

		[Range(6, 20)]
		[Display(Name = "Font Size", GroupName = "Bar Stats", Order = 7)]
		public int StatsFontSize { get; set; }

		[Display(Name = "Row Labels", Description = "Row names to the right of the last bar", GroupName = "Bar Stats", Order = 8)]
		public bool StatsShowLabels { get; set; }

		[Display(Name = "Colored Cells", Description = "Shade cells by value (volume / delta side)", GroupName = "Bar Stats", Order = 9)]
		public bool StatsColored { get; set; }

		[Range(0, double.MaxValue)]
		[Display(Name = "Big Trade Min Size", Description = "Trade size counted as a big trade. 0 = off. Applies when the bars are built (reload)", GroupName = "Bar Stats", Order = 10)]
		public double BigTradeMin { get; set; }

		[Display(Name = "Big Trade Aggregated", Description = "Merge prints with the same timestamp and side into one order before testing the size", GroupName = "Bar Stats", Order = 11)]
		public bool BigTradeAggregate { get; set; }

		// --- Sizing (multi column)

		[Range(1, 4)]
		[Display(Name = "Multi Column", Description = "Number of NT8Print columns sharing each bar (add one NT8Print per column)", GroupName = "Sizing", Order = 1)]
		public int ColumnCount { get; set; }

		[Range(1, 4)]
		[Display(Name = "Column Position", Description = "This instance's column, 1 = left", GroupName = "Sizing", Order = 2)]
		public int ColumnPosition { get; set; }

		#endregion
	}
}

#region Enums

public enum DPType { Volume, AskBidSplit, Delta, DeltaVolume }
public enum DPMode { Profile, Box }
public enum DPInput { Volume, AggregatedVolume, Order, NumTrades }
public enum DPGrouping { Automatic, Manual }
public enum DPGroupMode { Fixed, OpenClose }
public enum DPOutsideBar { Bar, Body }
public enum DPAlign { Right, Center }
public enum DPColorMode { None, Fixed, Fading, MoreColors }
public enum DPColorCalc { Volume, Delta, Imbalance, DominantVolume, DominantVolumeDelta }
public enum DPRange { Automatic, User }
public enum DPNumberFormat { Automatic, Normal, K }
public enum DPTextColor { Fixed, BySide, ByImbalance }
public enum DPNormalize { PerBar, Visible }
public enum DPStatsPosition { BelowBar, PanelBottom }

#endregion

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private NT8Print[] cacheNT8Print;
		public NT8Print NT8Print(DPInput inputType, double minFilter, double maxFilter, int maxBarsStored)
		{
			return NT8Print(Input, inputType, minFilter, maxFilter, maxBarsStored);
		}

		public NT8Print NT8Print(ISeries<double> input, DPInput inputType, double minFilter, double maxFilter, int maxBarsStored)
		{
			if (cacheNT8Print != null)
				for (int idx = 0; idx < cacheNT8Print.Length; idx++)
					if (cacheNT8Print[idx] != null && cacheNT8Print[idx].InputType == inputType && cacheNT8Print[idx].MinFilter == minFilter && cacheNT8Print[idx].MaxFilter == maxFilter && cacheNT8Print[idx].MaxBarsStored == maxBarsStored && cacheNT8Print[idx].EqualsInput(input))
						return cacheNT8Print[idx];
			return CacheIndicator<NT8Print>(new NT8Print(){ InputType = inputType, MinFilter = minFilter, MaxFilter = maxFilter, MaxBarsStored = maxBarsStored }, input, ref cacheNT8Print);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.NT8Print NT8Print(DPInput inputType, double minFilter, double maxFilter, int maxBarsStored)
		{
			return indicator.NT8Print(Input, inputType, minFilter, maxFilter, maxBarsStored);
		}

		public Indicators.NT8Print NT8Print(ISeries<double> input , DPInput inputType, double minFilter, double maxFilter, int maxBarsStored)
		{
			return indicator.NT8Print(input, inputType, minFilter, maxFilter, maxBarsStored);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.NT8Print NT8Print(DPInput inputType, double minFilter, double maxFilter, int maxBarsStored)
		{
			return indicator.NT8Print(Input, inputType, minFilter, maxFilter, maxBarsStored);
		}

		public Indicators.NT8Print NT8Print(ISeries<double> input , DPInput inputType, double minFilter, double maxFilter, int maxBarsStored)
		{
			return indicator.NT8Print(input, inputType, minFilter, maxFilter, maxBarsStored);
		}
	}
}

#endregion
