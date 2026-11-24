// =====================================================================================
//  OrderFlow_SnR  —  Order-flow Support & Resistance bands (NinjaTrader 8 indicator)
//
//  Rule
//   * Every completed bar builds a single-bar volume profile (Tick Replay prints) and
//     computes POC, Value Area High and Value Area Low (ValueAreaPct, default 70%).
//   * A band is drawn from VAL to VAH of that bar. Colour from the bar's close:
//       Close > Open  -> Support (green)      Close < Open -> Resistance (red)
//       Close == Open -> Support if Close >= POC, else Resistance
//   * Each following bar is tested. A touch (range overlapping [VAL, VAH]) does NOT
//     remove the band; it only raises TouchSignal / the touch alert once.
//   * The band is removed only when a bar CLOSES beyond its opposite side:
//       Support    -> Close < VAL          Resistance -> Close > VAH
//     ("Keep broken bands" leaves it frozen at the break bar with broken opacity.)
//   * Each band is drawn Minimum Length bars to the right of its own origin bar
//     (fixed length, Deep-M style). 'Extend to right margin' runs active bands to the edge.
//   * Smoothed EMA 20 is plotted as visual confluence only (it does not filter bands).
//
//  Data:  enable Tick Replay on the data series for historical bars. Without Tick
//         Replay, historical bars use an OHLC fallback value area (middle 70% of range).
//  Calc:  Calculate = On each tick (forced in defaults).
// =====================================================================================
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// Enum lives in the global namespace so NinjaTrader's auto-generated wrappers
// (Indicators / MarketAnalyzerColumns / Strategies) can all resolve it.
public enum OrderFlowSnRDrawMode
{
	EveryValueArea,
	HighVolumeBars,
	DeltaConfirmed,
	SwingPivots      // Deep-M style: only swing-high (resistance) / swing-low (support) bars
}

namespace NinjaTrader.NinjaScript.Indicators
{
	public class OrderFlow_SnR : Indicator
	{
		#region Private types

		private struct Print
		{
			public int Tick;
			public double Vol;
			public int Side;    // +1 buy aggressor, -1 sell aggressor
		}

		private class Band
		{
			public int OriginBar;
			public int EndBar = -1;   // -1 = active
			public double Vah;
			public double Val;
			public double Poc;
			public bool IsSupport;
			public bool Touched;
		}

		#endregion

		#region Private fields

		private readonly object bandLock = new object();
		private List<Band> bands;

		// current-bar profile
		private Dictionary<int, double> curVol;
		private double curDelta;
		private int profileBar = -1;

		// prints received in OnMarketData, committed in OnBarUpdate
		private Print[] pend = new Print[256];
		private int pendN;
		private double lastTradePrice;
		private int lastSide = 1;

		private Series<double> barVah;
		private Series<double> barVal;
		private Series<double> barPoc;
		private Series<double> barDelta;
		private Series<int> touchSignal;

		private bool warned;

		// smoothed EMA + slope
		private EMA emaBase;
		private EMA emaSmooth;
		private Series<double> slopeTicks;

		private SharpDX.Direct2D1.Brush supDx, supBrokenDx, supBorderDx;
		private SharpDX.Direct2D1.Brush resDx, resBrokenDx, resBorderDx;

		#endregion

		#region State

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Order-flow support & resistance bands drawn from each bar's value area (VAL-VAH), extended right until a bar closes beyond the opposite side.";
				Name = "OrderFlow_SnR";
				Calculate = Calculate.OnEachTick;
				IsOverlay = true;
				DisplayInDataBox = false;
				DrawOnPricePanel = true;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = false;
				ScaleJustification = ScaleJustification.Right;
				MaximumBarsLookBack = MaximumBarsLookBack.Infinite;

				ShowBands = true;
				DrawMode = OrderFlowSnRDrawMode.SwingPivots;
				MinimumLength = 4;
				DrawToEdge = false;
				ValueAreaPct = 70;
				HighVolumeMult = 1.2;
				HighVolumeLookback = 20;
				SwingStrength = 5;

				SupportColor = Brushes.ForestGreen;
				SupportOpacity = 0.22;
				SupportBrokenOpacity = 0.22;
				ResistanceColor = Brushes.Crimson;
				ResistanceOpacity = 0.22;
				ResistanceBrokenOpacity = 0.22;
				BorderOpacity = 0.45;

				ShowBrokenBands = false;
				MaxActiveBands = 300;
				MaxBrokenBands = 100;

				AlertOnTouch = false;
				AlertSound = "Alert2.wav";

				EmaPeriod = 20;
				EmaSmoothing = 5;
				SlopeLookback = 3;
				SlopeThresholdTicks = 1.0;
				ShowEma = true;
				EmaUpColor = Brushes.LimeGreen;
				EmaDownColor = Brushes.MediumPurple;
				EmaFlatColor = Brushes.Gray;

				AddPlot(new Stroke(Brushes.Gray, 2), PlotStyle.Line, "SmoothEMA");
			}
			else if (State == State.DataLoaded)
			{
				bands = new List<Band>();
				curVol = new Dictionary<int, double>();
				barVah = new Series<double>(this, MaximumBarsLookBack.Infinite);
				barVal = new Series<double>(this, MaximumBarsLookBack.Infinite);
				barPoc = new Series<double>(this, MaximumBarsLookBack.Infinite);
				barDelta = new Series<double>(this, MaximumBarsLookBack.Infinite);
				touchSignal = new Series<int>(this, MaximumBarsLookBack.Infinite);
				slopeTicks = new Series<double>(this, MaximumBarsLookBack.Infinite);

				emaBase = EMA(Close, EmaPeriod);
				emaSmooth = EmaSmoothing > 1 ? EMA(emaBase, EmaSmoothing) : null;
			}
			else if (State == State.Terminated)
			{
				DisposeDx();
			}
		}

		#endregion

		#region Data collection

		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (curVol == null || e.MarketDataType != MarketDataType.Last) return;

			double vol = e.Volume;
			if (vol <= 0) return;

			double price = e.Price;
			double ask = e.Ask;
			double bid = e.Bid;

			int side;
			if (ask > bid && price >= ask) side = 1;
			else if (ask > bid && price <= bid) side = -1;
			else if (price > lastTradePrice) side = 1;
			else if (price < lastTradePrice) side = -1;
			else side = lastSide;

			lastTradePrice = price;
			lastSide = side;

			if (pendN == pend.Length) Array.Resize(ref pend, pendN * 2);
			pend[pendN].Tick = ToTick(price);
			pend[pendN].Vol = vol;
			pend[pendN].Side = side;
			pendN++;
		}

		private void CommitPending()
		{
			for (int i = 0; i < pendN; i++)
			{
				double v;
				curVol.TryGetValue(pend[i].Tick, out v);
				curVol[pend[i].Tick] = v + pend[i].Vol;
				curDelta += pend[i].Vol * pend[i].Side;
			}
			pendN = 0;
		}

		#endregion

		#region Bar update

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0) return;

			if (!warned)
			{
				warned = true;
				if (Calculate != Calculate.OnEachTick)
					Draw.TextFixed(this, "snrWarnCalc", "OrderFlow_SnR: set Calculate to 'On each tick'.", TextPosition.BottomLeft);
				else if (!BarsArray[0].IsTickReplay)
					Draw.TextFixed(this, "snrWarnReplay", "OrderFlow_SnR: enable Tick Replay for order-flow value areas on historical bars.\nHistorical bars use an OHLC fallback.", TextPosition.BottomLeft);
			}

			UpdateEma();

			if (CurrentBar != profileBar)
			{
				// a new bar started: the previous profile belongs to the bar that just closed
				if (profileBar >= 0 && profileBar == CurrentBar - 1)
					FinalizeBar(profileBar);

				curVol.Clear();
				curDelta = 0;
				profileBar = CurrentBar;
				touchSignal[0] = 0;
			}

			// ticks queued since the last update belong to the current bar
			CommitPending();
		}

		// Smoothed EMA (EMA of EMA) and its slope in ticks per bar, coloured by slope
		private void UpdateEma()
		{
			double ema = emaSmooth != null ? emaSmooth[0] : emaBase[0];
			Value[0] = ema;

			int lb = Math.Max(1, SlopeLookback);
			double slope = CurrentBar >= lb ? (ema - Value[lb]) / (TickSize * lb) : 0;
			slopeTicks[0] = slope;

			if (!ShowEma)
				PlotBrushes[0][0] = Brushes.Transparent;
			else if (slope >= SlopeThresholdTicks)
				PlotBrushes[0][0] = EmaUpColor;
			else if (slope <= -SlopeThresholdTicks)
				PlotBrushes[0][0] = EmaDownColor;
			else
				PlotBrushes[0][0] = EmaFlatColor;
		}

		private void FinalizeBar(int c)
		{
			int ago = CurrentBar - c;   // 1
			double hi = High[ago];
			double lo = Low[ago];
			double op = Open[ago];
			double cl = Close[ago];

			double vah, val, poc;
			ComputeValueArea(hi, lo, cl, out vah, out val, out poc);

			barVah[ago] = vah;
			barVal[ago] = val;
			barPoc[ago] = poc;
			barDelta[ago] = curDelta;

			lock (bandLock)
			{
				// 1) test existing active bands against the bar that just closed
				int brokenCount = 0;
				for (int i = bands.Count - 1; i >= 0; i--)
				{
					Band b = bands[i];
					if (b.EndBar >= 0) { brokenCount++; continue; }

					// a touch (range overlaps VAL..VAH) only raises the touch signal/alert once
					bool touched = lo <= b.Vah && hi >= b.Val;
					if (touched && !b.Touched)
					{
						b.Touched = true;
						touchSignal[ago] = b.IsSupport ? 1 : -1;

						if (AlertOnTouch && State == State.Realtime)
							Alert("snrTouch" + b.OriginBar, Priority.Medium,
								"OrderFlow_SnR: " + (b.IsSupport ? "support" : "resistance") + " band touched " +
								Instrument.MasterInstrument.FormatPrice(b.Val) + " - " + Instrument.MasterInstrument.FormatPrice(b.Vah),
								NinjaTrader.Core.Globals.InstallDir + @"\sounds\" + AlertSound, 10, Brushes.Black, Brushes.Yellow);
					}

					// the band is broken only when a bar CLOSES beyond its opposite side:
					//   support    -> close below VAL
					//   resistance -> close above VAH
					bool broken = b.IsSupport ? cl < b.Val : cl > b.Vah;
					if (!broken) continue;

					b.EndBar = c;
					if (!ShowBrokenBands) bands.RemoveAt(i);
					else brokenCount++;
				}

				// 2) the closed bar spawns its own band (after the test, so it never erases itself)
				if (ShowBands && PassesDrawMode(c, ago, op, cl, poc))
				{
					bool isSupport = cl > op || (cl == op && cl >= poc);
					if (DrawMode == OrderFlowSnRDrawMode.DeltaConfirmed)
						isSupport = curDelta > 0;

					bands.Add(new Band { OriginBar = c, Vah = vah, Val = val, Poc = poc, IsSupport = isSupport });
				}

				// 2b) SwingPivots mode: confirm a pivot SwingStrength bars back and use THAT bar's value area
				if (ShowBands && DrawMode == OrderFlowSnRDrawMode.SwingPivots)
					AddPivotBand(c);

				// 3) caps
				TrimBands(brokenCount);
			}
		}

		private bool PassesDrawMode(int c, int ago, double op, double cl, double poc)
		{
			switch (DrawMode)
			{
				case OrderFlowSnRDrawMode.HighVolumeBars:
				{
					int n = Math.Min(HighVolumeLookback, c);
					if (n < 1) return true;
					double sum = 0;
					for (int k = 1; k <= n; k++) sum += Volume[ago + k];
					return Volume[ago] >= HighVolumeMult * (sum / n);
				}
				case OrderFlowSnRDrawMode.DeltaConfirmed:
					// delta must agree with the close direction
					if (curDelta > 0) return cl >= op;
					if (curDelta < 0) return cl <= op;
					return false;
				case OrderFlowSnRDrawMode.SwingPivots:
					return false;   // handled by AddPivotBand
				default:
					return true;
			}
		}

		// Pivot bar p = c - S is a swing high if its High beats the S bars on each side
		// (strictly on the left, >= on the right); swing low likewise on Low.
		// Swing high -> resistance zone, swing low -> support zone, from p's VAL..VAH.
		private void AddPivotBand(int c)
		{
			int s = Math.Max(1, SwingStrength);
			int p = c - s;
			if (p - s < 0) return;

			int agoP = CurrentBar - p;   // = 1 + s
			double hP = High[agoP], lP = Low[agoP];
			bool isHigh = true, isLow = true;

			for (int k = 1; k <= s && (isHigh || isLow); k++)
			{
				if (!(hP > High[agoP + k] && hP >= High[agoP - k])) isHigh = false;
				if (!(lP < Low[agoP + k] && lP <= Low[agoP - k])) isLow = false;
			}
			if (isHigh == isLow) return;   // neither, or an outside bar that is both

			double vah = barVah[agoP], val = barVal[agoP], poc = barPoc[agoP];

			// skip if price has already closed beyond the far side since the pivot
			for (int k = agoP - 1; k >= 1; k--)
			{
				if (isLow && Close[k] < val) return;
				if (isHigh && Close[k] > vah) return;
			}

			bands.Add(new Band { OriginBar = p, Vah = vah, Val = val, Poc = poc, IsSupport = isLow });
		}

		private void TrimBands(int brokenCount)
		{
			// drop oldest broken bands beyond the cap
			if (brokenCount > MaxBrokenBands)
			{
				int toDrop = brokenCount - MaxBrokenBands;
				for (int i = 0; i < bands.Count && toDrop > 0; )
				{
					if (bands[i].EndBar >= 0) { bands.RemoveAt(i); toDrop--; }
					else i++;
				}
			}

			int active = 0;
			for (int i = 0; i < bands.Count; i++) if (bands[i].EndBar < 0) active++;
			if (active > MaxActiveBands)
			{
				int toDrop = active - MaxActiveBands;
				for (int i = 0; i < bands.Count && toDrop > 0; )
				{
					if (bands[i].EndBar < 0) { bands.RemoveAt(i); toDrop--; }
					else i++;
				}
			}
		}

		#endregion

		#region Value area

		private void ComputeValueArea(double hi, double lo, double cl, out double vah, out double val, out double poc)
		{
			int hiT = ToTick(hi);
			int loT = ToTick(lo);
			if (hiT < loT) { int t = hiT; hiT = loT; loT = t; }

			double total = 0;
			foreach (double v in curVol.Values) total += v;

			if (total <= 0 || hiT == loT)
			{
				// OHLC fallback: middle ValueAreaPct of the range, POC at the close side of the mid
				double range = hi - lo;
				double trim = range * (1.0 - ValueAreaPct / 100.0) / 2.0;
				vah = Round(hi - trim);
				val = Round(lo + trim);
				if (val > vah) { double m = Round((hi + lo) / 2.0); vah = m; val = m; }
				poc = Round((hi + lo + cl) / 3.0);
				return;
			}

			// include ticks printed outside the reported bar range (rare, but keep them)
			foreach (int t in curVol.Keys)
			{
				if (t > hiT) hiT = t;
				if (t < loT) loT = t;
			}

			int n = hiT - loT + 1;
			double[] vol = new double[n];
			foreach (KeyValuePair<int, double> kv in curVol) vol[kv.Key - loT] += kv.Value;

			// POC: max volume, tie -> nearest to bar middle
			int pocIdx = 0;
			double mid = (n - 1) / 2.0;
			for (int i = 1; i < n; i++)
			{
				if (vol[i] > vol[pocIdx] || (vol[i] == vol[pocIdx] && Math.Abs(i - mid) < Math.Abs(pocIdx - mid)))
					pocIdx = i;
			}

			double target = total * ValueAreaPct / 100.0;
			double acc = vol[pocIdx];
			int up = pocIdx, dn = pocIdx;

			// classic expansion: compare the next two levels above vs below
			while (acc < target && (up < n - 1 || dn > 0))
			{
				double vUp = 0, vDn = 0;
				int upSteps = 0, dnSteps = 0;
				for (int k = 1; k <= 2 && up + k <= n - 1; k++) { vUp += vol[up + k]; upSteps = k; }
				for (int k = 1; k <= 2 && dn - k >= 0; k++) { vDn += vol[dn - k]; dnSteps = k; }

				if (upSteps > 0 && (dnSteps == 0 || vUp >= vDn)) { up += upSteps; acc += vUp; }
				else if (dnSteps > 0) { dn -= dnSteps; acc += vDn; }
				else break;
			}

			vah = Round((loT + up) * TickSize);
			val = Round((loT + dn) * TickSize);
			poc = Round((loT + pocIdx) * TickSize);
		}

		private int ToTick(double price)
		{
			return (int)Math.Round(price / TickSize, MidpointRounding.AwayFromZero);
		}

		private double Round(double price)
		{
			return Instrument.MasterInstrument.RoundToTickSize(price);
		}

		#endregion

		#region Rendering

		public override void OnRenderTargetChanged()
		{
			DisposeDx();
			if (RenderTarget == null) return;

			supDx = MakeBrush(SupportColor, SupportOpacity);
			supBrokenDx = MakeBrush(SupportColor, SupportBrokenOpacity);
			supBorderDx = MakeBrush(SupportColor, BorderOpacity);
			resDx = MakeBrush(ResistanceColor, ResistanceOpacity);
			resBrokenDx = MakeBrush(ResistanceColor, ResistanceBrokenOpacity);
			resBorderDx = MakeBrush(ResistanceColor, BorderOpacity);
		}

		private SharpDX.Direct2D1.Brush MakeBrush(Brush b, double opacity)
		{
			SharpDX.Direct2D1.Brush dx = (b ?? Brushes.Gray).ToDxBrush(RenderTarget);
			dx.Opacity = (float)Math.Max(0, Math.Min(1, opacity));
			return dx;
		}

		private void DisposeDx()
		{
			if (supDx != null) { supDx.Dispose(); supDx = null; }
			if (supBrokenDx != null) { supBrokenDx.Dispose(); supBrokenDx = null; }
			if (supBorderDx != null) { supBorderDx.Dispose(); supBorderDx = null; }
			if (resDx != null) { resDx.Dispose(); resDx = null; }
			if (resBrokenDx != null) { resBrokenDx.Dispose(); resBrokenDx = null; }
			if (resBorderDx != null) { resBorderDx.Dispose(); resBorderDx = null; }
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (!ShowBands || bands == null || ChartBars == null || supDx == null) return;

			int lastIdx = ChartBars.Bars.Count - 1;
			if (lastIdx < 0) return;

			float barDist = (float)chartControl.Properties.BarDistance;
			float halfW = Math.Max(1f, chartControl.GetBarPaintWidth(ChartBars) / 2f);
			float lastX = chartControl.GetXByBarIndex(ChartBars, lastIdx);
			float panelL = ChartPanel.X;
			float panelR = ChartPanel.X + ChartPanel.W;

			SharpDX.Direct2D1.AntialiasMode oldAA = RenderTarget.AntialiasMode;
			RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;

			lock (bandLock)
			{
				for (int i = 0; i < bands.Count; i++)
				{
					Band b = bands[i];
					bool broken = b.EndBar >= 0;
					if (broken && !ShowBrokenBands) continue;
					if (b.OriginBar > lastIdx) continue;

					float x1 = chartControl.GetXByBarIndex(ChartBars, b.OriginBar) - halfW;
					// Fixed-length zone (Deep-M style): origin bar + MinimumLength bars to the right,
					// measured from the zone's own origin bar, not from the current bar.
					int fixedEnd = b.OriginBar + Math.Max(1, MinimumLength);
					float x2;
					if (broken)
					{
						// broken zones stop at the break bar if that came before the fixed length
						x2 = XOf(chartControl, Math.Min(b.EndBar, fixedEnd), lastIdx, lastX, barDist) + halfW;
					}
					else if (DrawToEdge)
					{
						x2 = panelR;
					}
					else
					{
						// may reach into the future for recent zones; clipped below to the right margin
						x2 = XOf(chartControl, fixedEnd, lastIdx, lastX, barDist) + halfW;
					}

					if (x2 < panelL || x1 > panelR) continue;
					x1 = Math.Max(x1, panelL);
					x2 = Math.Min(x2, panelR);

					float yTop = chartScale.GetYByValue(b.Vah + TickSize * 0.5);
					float yBot = chartScale.GetYByValue(b.Val - TickSize * 0.5);
					if (yBot < ChartPanel.Y || yTop > ChartPanel.Y + ChartPanel.H) continue;

					SharpDX.RectangleF rect = new SharpDX.RectangleF(x1, yTop, Math.Max(1f, x2 - x1), Math.Max(1f, yBot - yTop));

					SharpDX.Direct2D1.Brush fill = b.IsSupport ? (broken ? supBrokenDx : supDx) : (broken ? resBrokenDx : resDx);
					SharpDX.Direct2D1.Brush edge = b.IsSupport ? supBorderDx : resBorderDx;

					RenderTarget.FillRectangle(rect, fill);
					RenderTarget.DrawRectangle(rect, edge, 1f);
				}
			}

			RenderTarget.AntialiasMode = oldAA;
		}

		private float XOf(ChartControl cc, int idx, int lastIdx, float lastX, float barDist)
		{
			if (idx <= lastIdx) return cc.GetXByBarIndex(ChartBars, idx);
			return lastX + (idx - lastIdx) * barDist;
		}

		#endregion

		#region Public series (for strategies / Market Analyzer)

		[Browsable(false), XmlIgnore]
		public Series<double> BarVAH { get { Update(); return barVah; } }

		[Browsable(false), XmlIgnore]
		public Series<double> BarVAL { get { Update(); return barVal; } }

		[Browsable(false), XmlIgnore]
		public Series<double> BarPOC { get { Update(); return barPoc; } }

		[Browsable(false), XmlIgnore]
		public Series<double> BarDelta { get { Update(); return barDelta; } }

		/// <summary>+1 = an active support band was first touched on this bar, -1 = resistance band touched, 0 = none.</summary>
		[Browsable(false), XmlIgnore]
		public Series<int> TouchSignal { get { Update(); return touchSignal; } }

		/// <summary>Smoothed EMA value (the plotted line).</summary>
		[Browsable(false), XmlIgnore]
		public Series<double> SmoothEMA { get { return Values[0]; } }

		/// <summary>Smoothed EMA slope in ticks per bar over SlopeLookback bars.</summary>
		[Browsable(false), XmlIgnore]
		public Series<double> EmaSlope { get { Update(); return slopeTicks; } }

		#endregion

		#region Properties

		[NinjaScriptProperty]
		[Display(Name = "Show S/R bands", Order = 1, GroupName = "Support & Resistance Bands")]
		public bool ShowBands { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Draw S/R band for", Order = 2, GroupName = "Support & Resistance Bands")]
		public OrderFlowSnRDrawMode DrawMode { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Minimum length (in bars)", Description = "Each band is drawn this many bars to the right of its own origin bar.", Order = 3, GroupName = "Support & Resistance Bands")]
		public int MinimumLength { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Extend to right margin?", Description = "Unchecked: every band is 'Minimum length' bars long from its origin bar. Checked: active bands extend to the chart's right-hand edge.", Order = 4, GroupName = "Support & Resistance Bands")]
		public bool DrawToEdge { get; set; }

		[XmlIgnore]
		[Display(Name = "Support band color", Order = 5, GroupName = "Support & Resistance Bands")]
		public Brush SupportColor { get; set; }

		[Browsable(false)]
		public string SupportColorSerializable
		{
			get { return Serialize.BrushToString(SupportColor); }
			set { SupportColor = Serialize.StringToBrush(value); }
		}

		[Range(0, 1)]
		[Display(Name = "Opacity (0-1)", Order = 6, GroupName = "Support & Resistance Bands")]
		public double SupportOpacity { get; set; }

		[Range(0, 1)]
		[Display(Name = "Opacity (0-1) (broken band)", Order = 7, GroupName = "Support & Resistance Bands")]
		public double SupportBrokenOpacity { get; set; }

		[XmlIgnore]
		[Display(Name = "Resistance band color", Order = 8, GroupName = "Support & Resistance Bands")]
		public Brush ResistanceColor { get; set; }

		[Browsable(false)]
		public string ResistanceColorSerializable
		{
			get { return Serialize.BrushToString(ResistanceColor); }
			set { ResistanceColor = Serialize.StringToBrush(value); }
		}

		[Range(0, 1)]
		[Display(Name = "Opacity (0-1) ", Order = 9, GroupName = "Support & Resistance Bands")]
		public double ResistanceOpacity { get; set; }

		[Range(0, 1)]
		[Display(Name = "Opacity (0-1) (broken band) ", Order = 10, GroupName = "Support & Resistance Bands")]
		public double ResistanceBrokenOpacity { get; set; }

		[Range(0, 1)]
		[Display(Name = "Border opacity (0-1)", Order = 11, GroupName = "Support & Resistance Bands")]
		public double BorderOpacity { get; set; }

		[Display(Name = "Keep broken bands", Description = "Off = a band disappears when a bar closes beyond its opposite side. On = it stays, ending at that bar, with broken-band opacity.", Order = 12, GroupName = "Support & Resistance Bands")]
		public bool ShowBrokenBands { get; set; }

		[NinjaScriptProperty]
		[Range(50, 95)]
		[Display(Name = "Value area %", Order = 1, GroupName = "Calculation")]
		public double ValueAreaPct { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "High volume multiplier", Description = "HighVolumeBars mode: bar volume >= multiplier x average volume.", Order = 2, GroupName = "Calculation")]
		public double HighVolumeMult { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "High volume lookback", Order = 3, GroupName = "Calculation")]
		public int HighVolumeLookback { get; set; }

		[Range(1, 50)]
		[Display(Name = "Swing strength (bars each side)", Description = "SwingPivots mode: a pivot must be the highest high / lowest low of this many bars on each side. Higher = fewer, stronger zones. The zone appears once these right-side bars have closed.", Order = 4, GroupName = "Calculation")]
		public int SwingStrength { get; set; }

		[Range(1, int.MaxValue)]
		[Display(Name = "Max active bands", Order = 4, GroupName = "Calculation")]
		public int MaxActiveBands { get; set; }

		[Range(0, int.MaxValue)]
		[Display(Name = "Max broken bands kept", Order = 5, GroupName = "Calculation")]
		public int MaxBrokenBands { get; set; }

		[Range(1, int.MaxValue)]
		[Display(Name = "EMA period", Order = 2, GroupName = "EMA Slope")]
		public int EmaPeriod { get; set; }

		[Range(1, int.MaxValue)]
		[Display(Name = "Smoothing (EMA of EMA, 1 = off)", Order = 3, GroupName = "EMA Slope")]
		public int EmaSmoothing { get; set; }

		[Range(1, int.MaxValue)]
		[Display(Name = "Slope lookback (bars)", Order = 4, GroupName = "EMA Slope")]
		public int SlopeLookback { get; set; }

		[Range(0, double.MaxValue)]
		[Display(Name = "Slope threshold (ticks/bar)", Description = "Slope inside +/- this value counts as flat.", Order = 5, GroupName = "EMA Slope")]
		public double SlopeThresholdTicks { get; set; }

		[Display(Name = "Show EMA", Order = 6, GroupName = "EMA Slope")]
		public bool ShowEma { get; set; }

		[XmlIgnore]
		[Display(Name = "EMA rising color", Order = 7, GroupName = "EMA Slope")]
		public Brush EmaUpColor { get; set; }

		[Browsable(false)]
		public string EmaUpColorSerializable
		{
			get { return Serialize.BrushToString(EmaUpColor); }
			set { EmaUpColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "EMA falling color", Order = 8, GroupName = "EMA Slope")]
		public Brush EmaDownColor { get; set; }

		[Browsable(false)]
		public string EmaDownColorSerializable
		{
			get { return Serialize.BrushToString(EmaDownColor); }
			set { EmaDownColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "EMA flat color", Order = 9, GroupName = "EMA Slope")]
		public Brush EmaFlatColor { get; set; }

		[Browsable(false)]
		public string EmaFlatColorSerializable
		{
			get { return Serialize.BrushToString(EmaFlatColor); }
			set { EmaFlatColor = Serialize.StringToBrush(value); }
		}

		[Display(Name = "Alert on band touch", Order = 1, GroupName = "Alerts")]
		public bool AlertOnTouch { get; set; }

		[Display(Name = "Alert sound file", Order = 2, GroupName = "Alerts")]
		public string AlertSound { get; set; }

		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private OrderFlow_SnR[] cacheOrderFlow_SnR;
		public OrderFlow_SnR OrderFlow_SnR(bool showBands, OrderFlowSnRDrawMode drawMode, int minimumLength, bool drawToEdge, double valueAreaPct, double highVolumeMult, int highVolumeLookback)
		{
			return OrderFlow_SnR(Input, showBands, drawMode, minimumLength, drawToEdge, valueAreaPct, highVolumeMult, highVolumeLookback);
		}

		public OrderFlow_SnR OrderFlow_SnR(ISeries<double> input, bool showBands, OrderFlowSnRDrawMode drawMode, int minimumLength, bool drawToEdge, double valueAreaPct, double highVolumeMult, int highVolumeLookback)
		{
			if (cacheOrderFlow_SnR != null)
				for (int idx = 0; idx < cacheOrderFlow_SnR.Length; idx++)
					if (cacheOrderFlow_SnR[idx] != null && cacheOrderFlow_SnR[idx].ShowBands == showBands && cacheOrderFlow_SnR[idx].DrawMode == drawMode && cacheOrderFlow_SnR[idx].MinimumLength == minimumLength && cacheOrderFlow_SnR[idx].DrawToEdge == drawToEdge && cacheOrderFlow_SnR[idx].ValueAreaPct == valueAreaPct && cacheOrderFlow_SnR[idx].HighVolumeMult == highVolumeMult && cacheOrderFlow_SnR[idx].HighVolumeLookback == highVolumeLookback && cacheOrderFlow_SnR[idx].EqualsInput(input))
						return cacheOrderFlow_SnR[idx];
			return CacheIndicator<OrderFlow_SnR>(new OrderFlow_SnR(){ ShowBands = showBands, DrawMode = drawMode, MinimumLength = minimumLength, DrawToEdge = drawToEdge, ValueAreaPct = valueAreaPct, HighVolumeMult = highVolumeMult, HighVolumeLookback = highVolumeLookback }, input, ref cacheOrderFlow_SnR);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.OrderFlow_SnR OrderFlow_SnR(bool showBands, OrderFlowSnRDrawMode drawMode, int minimumLength, bool drawToEdge, double valueAreaPct, double highVolumeMult, int highVolumeLookback)
		{
			return indicator.OrderFlow_SnR(Input, showBands, drawMode, minimumLength, drawToEdge, valueAreaPct, highVolumeMult, highVolumeLookback);
		}

		public Indicators.OrderFlow_SnR OrderFlow_SnR(ISeries<double> input , bool showBands, OrderFlowSnRDrawMode drawMode, int minimumLength, bool drawToEdge, double valueAreaPct, double highVolumeMult, int highVolumeLookback)
		{
			return indicator.OrderFlow_SnR(input, showBands, drawMode, minimumLength, drawToEdge, valueAreaPct, highVolumeMult, highVolumeLookback);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.OrderFlow_SnR OrderFlow_SnR(bool showBands, OrderFlowSnRDrawMode drawMode, int minimumLength, bool drawToEdge, double valueAreaPct, double highVolumeMult, int highVolumeLookback)
		{
			return indicator.OrderFlow_SnR(Input, showBands, drawMode, minimumLength, drawToEdge, valueAreaPct, highVolumeMult, highVolumeLookback);
		}

		public Indicators.OrderFlow_SnR OrderFlow_SnR(ISeries<double> input , bool showBands, OrderFlowSnRDrawMode drawMode, int minimumLength, bool drawToEdge, double valueAreaPct, double highVolumeMult, int highVolumeLookback)
		{
			return indicator.OrderFlow_SnR(input, showBands, drawMode, minimumLength, drawToEdge, valueAreaPct, highVolumeMult, highVolumeLookback);
		}
	}
}

#endregion
