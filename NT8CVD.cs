//
// NT8CVD.cs
// NinjaTrader 8 Cumulative Volume Delta indicator (NT8Charts CVD style)
//
// Features:
// - Tick-by-tick cumulative delta (Ask-aggressor volume minus Bid-aggressor volume)
// - Reset modes: Session (trading hours template), Day (calendar day), Custom Time, None
// - Display: CVD as Candlesticks or Heikin Ashi candles (custom SharpDX rendering)
// - Optional Filtered CVD (per-print min/max size filter) as a line, colored by sign
// - Optional cumulative Ask / Bid volume lines
// - Zero line
//
// Requires Tick Replay for historical bars. Without it, the indicator plots from
// real-time data onward only.
//

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// Global namespace so the generated code in every NinjaScript namespace can resolve them.
public enum NT8CvdResetMode
{
	Session,		// first bar of each trading-hours session
	Day,			// calendar day change (chart time zone)
	CustomTime,		// every day at Reset Time (chart time zone)
	None			// never reset: cumulative from first loaded bar
}

public enum NT8CvdDisplayStyle
{
	Candlestick,
	HeikinAshi
}

namespace NinjaTrader.NinjaScript.Indicators
{
	public class NT8CVD : Indicator
	{
		// Running totals since last reset
		private double			cvd;
		private double			filteredCvd;
		private double			cumAsk;
		private double			cumBid;

		// Current bar raw OHLC of CVD
		private double			barOpen;
		private double			barHigh;
		private double			barLow;

		private int				activeBar = -1;
		private bool			tickReplay;
		private bool			warned;

		private Series<double>	rawCvd;
		private Series<bool>	resetBar;

		// SharpDX resources
		private SharpDX.Direct2D1.Brush upDx;
		private SharpDX.Direct2D1.Brush downDx;
		private SharpDX.Direct2D1.Brush outlineDx;

		private const int PlotOpen		= 0;
		private const int PlotHigh		= 1;
		private const int PlotLow		= 2;
		private const int PlotClose		= 3;
		private const int PlotFiltered	= 4;
		private const int PlotAsk		= 5;
		private const int PlotBid		= 6;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= @"Cumulative Volume Delta displayed as Candlesticks or Heikin Ashi, with Session / Day / Custom Time reset.";
				Name						= "NT8CVD";
				Calculate					= Calculate.OnEachTick;
				IsOverlay					= false;
				DisplayInDataBox			= true;
				DrawOnPricePanel			= false;
				PaintPriceMarkers			= true;
				IsAutoScale					= true;
				ScaleJustification			= ScaleJustification.Right;
				IsSuspendedWhileInactive	= false; // OnMarketData accumulation must never pause

				// 1. General
				ResetMode					= NT8CvdResetMode.Session;
				ResetTime					= new DateTime(2000, 1, 1, 9, 30, 0);
				DisplayStyle				= NT8CvdDisplayStyle.Candlestick;

				// 2. Candles
				UpBrush						= Brushes.LimeGreen;
				DownBrush					= Brushes.Crimson;
				ShowOutline					= false;
				OutlineBrush				= Brushes.Black;
				BodyWidthPercent			= 70;
				WickWidth					= 1;

				// 3. Filtered CVD
				ShowFilteredCVD				= false;
				MinTradeSize				= 10;
				MaxTradeSize				= 0;
				FilteredPosBrush			= Brushes.DeepSkyBlue;
				FilteredNegBrush			= Brushes.Orange;

				// 4. Bid / Ask volume
				ShowBidAskVolume			= false;

				// Candle OHLC plots: transparent (candles are drawn in OnRender) but they
				// drive autoscale, data box and strategy access.
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "CVD Open");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "CVD High");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "CVD Low");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "CVD Close");
				AddPlot(new Stroke(Brushes.DeepSkyBlue, 2), PlotStyle.Line, "Filtered CVD");
				AddPlot(new Stroke(Brushes.SeaGreen, 1), PlotStyle.Line, "Ask Volume");
				AddPlot(new Stroke(Brushes.IndianRed, 1), PlotStyle.Line, "Bid Volume");

				AddLine(new Stroke(Brushes.DimGray, DashStyleHelper.Dash, 1), 0, "Zero");
			}
			else if (State == State.Configure)
			{
				// Intra-bar high/low of CVD requires tick-by-tick processing.
				Calculate = Calculate.OnEachTick;
			}
			else if (State == State.DataLoaded)
			{
				rawCvd		= new Series<double>(this, MaximumBarsLookBack.Infinite);
				resetBar	= new Series<bool>(this, MaximumBarsLookBack.Infinite);

				cvd = filteredCvd = cumAsk = cumBid = 0;
				barOpen = barHigh = barLow = 0;
				activeBar	= -1;
				warned		= false;
				tickReplay	= Bars.IsTickReplay;
			}
			else if (State == State.Terminated)
			{
				DisposeDx();
			}
		}

		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (e.MarketDataType != MarketDataType.Last || CurrentBar < 0 || activeBar < 0)
				return;

			int side;
			if (e.Price >= e.Ask)
				side = 1;
			else if (e.Price <= e.Bid)
				side = -1;
			else
				return; // print inside the spread: unclassified

			double vol = Instrument.MasterInstrument.InstrumentType == InstrumentType.CryptoCurrency
				? Core.Globals.ToCryptocurrencyVolume(e.Volume)
				: e.Volume;

			if (side > 0)
				cumAsk += vol;
			else
				cumBid += vol;

			cvd += side * vol;
			if (cvd > barHigh) barHigh = cvd;
			if (cvd < barLow)  barLow  = cvd;

			if (ShowFilteredCVD && vol >= MinTradeSize && (MaxTradeSize <= 0 || vol <= MaxTradeSize))
				filteredCvd += side * vol;
		}

		protected override void OnBarUpdate()
		{
			// No Tick Replay: OnMarketData is not called historically, so skip those bars.
			if (State == State.Historical && !tickReplay)
			{
				if (!warned)
				{
					Draw.TextFixed(this, "NT8CVDWarning",
						"NT8CVD: enable Tick Replay to see historical CVD. Plotting real-time only.",
						TextPosition.BottomLeft);
					warned = true;
				}
				return;
			}

			if (CurrentBar < activeBar)
				return;

			if (CurrentBar != activeBar)
			{
				// Finalize the previous bar with everything accumulated for it
				if (activeBar >= 0 && CurrentBar > 0)
					WriteBar(1);

				bool doReset = activeBar < 0 || IsResetBar();
				if (doReset)
				{
					cvd			= 0;
					filteredCvd	= 0;
					cumAsk		= 0;
					cumBid		= 0;
				}

				resetBar[0]	= doReset;
				barOpen		= cvd;
				barHigh		= cvd;
				barLow		= cvd;
				activeBar	= CurrentBar;
			}

			WriteBar(0);
		}

		private bool IsResetBar()
		{
			if (CurrentBar < 1)
				return true;

			switch (ResetMode)
			{
				case NT8CvdResetMode.Session:
					return Bars.IsFirstBarOfSession;

				case NT8CvdResetMode.Day:
					return Time[0].Date != Time[1].Date;

				case NT8CvdResetMode.CustomTime:
				{
					// Most recent reset instant at or before this bar's time
					DateTime resetPoint = Time[0].Date + ResetTime.TimeOfDay;
					if (resetPoint > Time[0])
						resetPoint = resetPoint.AddDays(-1);
					// Reset if that instant falls after the previous bar's time
					return Time[1] < resetPoint;
				}

				default:
					return false;
			}
		}

		private void WriteBar(int b)
		{
			double o = barOpen, h = barHigh, l = barLow, c = cvd;
			rawCvd[b] = c;

			if (DisplayStyle == NT8CvdDisplayStyle.HeikinAshi)
			{
				double haClose = (o + h + l + c) / 4.0;
				double haOpen;

				bool hasPrev = CurrentBar > b && !resetBar[b]
					&& Values[PlotOpen].IsValidDataPoint(b + 1)
					&& Values[PlotClose].IsValidDataPoint(b + 1);

				if (hasPrev)
					haOpen = (Values[PlotOpen][b + 1] + Values[PlotClose][b + 1]) / 2.0;
				else
					haOpen = (o + c) / 2.0; // first bar after a reset

				o = haOpen;
				c = haClose;
				h = Math.Max(h, Math.Max(haOpen, haClose));
				l = Math.Min(l, Math.Min(haOpen, haClose));
			}

			Values[PlotOpen][b]		= o;
			Values[PlotHigh][b]		= h;
			Values[PlotLow][b]		= l;
			Values[PlotClose][b]	= c;

			if (ShowFilteredCVD)
			{
				Values[PlotFiltered][b]			= filteredCvd;
				PlotBrushes[PlotFiltered][b]	= filteredCvd >= 0 ? FilteredPosBrush : FilteredNegBrush;
			}
			else
				Values[PlotFiltered].Reset(b);

			if (ShowBidAskVolume)
			{
				Values[PlotAsk][b] = cumAsk;
				Values[PlotBid][b] = -cumBid; // drawn below zero, mirroring the CVD sign
			}
			else
			{
				Values[PlotAsk].Reset(b);
				Values[PlotBid].Reset(b);
			}
		}

		#region Rendering

		public override void OnRenderTargetChanged()
		{
			DisposeDx();

			if (RenderTarget != null)
			{
				upDx		= UpBrush.ToDxBrush(RenderTarget);
				downDx		= DownBrush.ToDxBrush(RenderTarget);
				outlineDx	= OutlineBrush.ToDxBrush(RenderTarget);
			}
		}

		private void DisposeDx()
		{
			if (upDx != null)		{ upDx.Dispose();		upDx = null; }
			if (downDx != null)		{ downDx.Dispose();		downDx = null; }
			if (outlineDx != null)	{ outlineDx.Dispose();	outlineDx = null; }
		}

		// The four candle plots are Transparent, and NinjaTrader leaves transparent plots
		// out of autoscale. Without this override the panel scale ignores the candles
		// (fitting only the zero line or the optional lines), so the candles are drawn
		// off-panel or squashed flat. Here the scale is built from the candle high/low.
		public override void OnCalculateMinMax()
		{
			if (ChartBars == null)
				return;

			double tmpMin = double.MaxValue;
			double tmpMax = double.MinValue;

			for (int idx = ChartBars.FromIndex; idx <= ChartBars.ToIndex; idx++)
			{
				if (Values[PlotHigh].IsValidDataPointAt(idx) && Values[PlotLow].IsValidDataPointAt(idx))
				{
					tmpMax = Math.Max(tmpMax, Values[PlotHigh].GetValueAt(idx));
					tmpMin = Math.Min(tmpMin, Values[PlotLow].GetValueAt(idx));
				}

				if (ShowFilteredCVD && Values[PlotFiltered].IsValidDataPointAt(idx))
				{
					double f = Values[PlotFiltered].GetValueAt(idx);
					tmpMax = Math.Max(tmpMax, f);
					tmpMin = Math.Min(tmpMin, f);
				}

				if (ShowBidAskVolume && Values[PlotAsk].IsValidDataPointAt(idx) && Values[PlotBid].IsValidDataPointAt(idx))
				{
					tmpMax = Math.Max(tmpMax, Values[PlotAsk].GetValueAt(idx));
					tmpMin = Math.Min(tmpMin, Values[PlotBid].GetValueAt(idx));
				}
			}

			if (tmpMin > tmpMax)	// nothing valid in view
				return;

			if (tmpMax - tmpMin < 1)	// avoid a zero-height scale on a flat stretch
			{
				tmpMax += 1;
				tmpMin -= 1;
			}

			MinValue = tmpMin;
			MaxValue = tmpMax;
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			// Draws the line plots (filtered CVD, bid/ask volume) and the zero line
			base.OnRender(chartControl, chartScale);

			if (ChartBars == null || RenderTarget == null)
				return;

			// OnRenderTargetChanged is not guaranteed to have run before the first render
			if (upDx == null || downDx == null || outlineDx == null)
				OnRenderTargetChanged();

			if (upDx == null || downDx == null)
				return;

			SharpDX.Direct2D1.AntialiasMode oldMode = RenderTarget.AntialiasMode;
			RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;

			float bodyWidth = Math.Max(1f, chartControl.GetBarPaintWidth(ChartBars) * BodyWidthPercent / 100f);

			for (int idx = ChartBars.FromIndex; idx <= ChartBars.ToIndex; idx++)
			{
				if (!Values[PlotClose].IsValidDataPointAt(idx) || !Values[PlotOpen].IsValidDataPointAt(idx))
					continue;

				double o = Values[PlotOpen].GetValueAt(idx);
				double h = Values[PlotHigh].GetValueAt(idx);
				double l = Values[PlotLow].GetValueAt(idx);
				double c = Values[PlotClose].GetValueAt(idx);

				float x		= chartControl.GetXByBarIndex(ChartBars, idx);
				float yO	= chartScale.GetYByValue(o);
				float yH	= chartScale.GetYByValue(h);
				float yL	= chartScale.GetYByValue(l);
				float yC	= chartScale.GetYByValue(c);

				SharpDX.Direct2D1.Brush brush = c >= o ? upDx : downDx;

				// Wick
				RenderTarget.DrawLine(new SharpDX.Vector2(x, yH), new SharpDX.Vector2(x, yL), brush, WickWidth);

				// Body
				float top		= Math.Min(yO, yC);
				float height	= Math.Max(1f, Math.Abs(yC - yO));
				SharpDX.RectangleF body = new SharpDX.RectangleF(x - bodyWidth / 2f, top, bodyWidth, height);

				RenderTarget.FillRectangle(body, brush);
				if (ShowOutline && outlineDx != null)
					RenderTarget.DrawRectangle(body, outlineDx, 1f);
			}

			RenderTarget.AntialiasMode = oldMode;
		}

		#endregion

		#region Properties

		// ========== 1. General ==========
		[NinjaScriptProperty]
		[Display(Name = "Reset Mode", Description = "Session = trading hours session start. Day = calendar day change. Custom Time = daily at Reset Time. None = never reset.", Order = 1, GroupName = "1. General")]
		public NT8CvdResetMode ResetMode { get; set; }

		[NinjaScriptProperty]
		[PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
		[Display(Name = "Reset Time", Description = "Used when Reset Mode = Custom Time. Chart time zone.", Order = 2, GroupName = "1. General")]
		public DateTime ResetTime { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Display Style", Order = 3, GroupName = "1. General")]
		public NT8CvdDisplayStyle DisplayStyle { get; set; }

		// ========== 2. Candles ==========
		[XmlIgnore]
		[Display(Name = "Up Color", Order = 1, GroupName = "2. Candles")]
		public Brush UpBrush { get; set; }

		[Browsable(false)]
		public string UpBrushSerializable
		{
			get { return Serialize.BrushToString(UpBrush); }
			set { UpBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Down Color", Order = 2, GroupName = "2. Candles")]
		public Brush DownBrush { get; set; }

		[Browsable(false)]
		public string DownBrushSerializable
		{
			get { return Serialize.BrushToString(DownBrush); }
			set { DownBrush = Serialize.StringToBrush(value); }
		}

		[Display(Name = "Show Outline", Order = 3, GroupName = "2. Candles")]
		public bool ShowOutline { get; set; }

		[XmlIgnore]
		[Display(Name = "Outline Color", Order = 4, GroupName = "2. Candles")]
		public Brush OutlineBrush { get; set; }

		[Browsable(false)]
		public string OutlineBrushSerializable
		{
			get { return Serialize.BrushToString(OutlineBrush); }
			set { OutlineBrush = Serialize.StringToBrush(value); }
		}

		[Range(10, 100)]
		[Display(Name = "Body Width %", Order = 5, GroupName = "2. Candles")]
		public int BodyWidthPercent { get; set; }

		[Range(1, 5)]
		[Display(Name = "Wick Width", Order = 6, GroupName = "2. Candles")]
		public int WickWidth { get; set; }

		// ========== 3. Filtered CVD ==========
		[NinjaScriptProperty]
		[Display(Name = "Show Filtered CVD", Description = "Second CVD line counting only prints within the size filter (isolates larger participants).", Order = 1, GroupName = "3. Filtered CVD")]
		public bool ShowFilteredCVD { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Min Trade Size", Description = "Only prints with volume >= this are counted.", Order = 2, GroupName = "3. Filtered CVD")]
		public double MinTradeSize { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Max Trade Size (0 = no limit)", Description = "Only prints with volume <= this are counted.", Order = 3, GroupName = "3. Filtered CVD")]
		public double MaxTradeSize { get; set; }

		[XmlIgnore]
		[Display(Name = "Positive Color", Order = 4, GroupName = "3. Filtered CVD")]
		public Brush FilteredPosBrush { get; set; }

		[Browsable(false)]
		public string FilteredPosBrushSerializable
		{
			get { return Serialize.BrushToString(FilteredPosBrush); }
			set { FilteredPosBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Negative Color", Order = 5, GroupName = "3. Filtered CVD")]
		public Brush FilteredNegBrush { get; set; }

		[Browsable(false)]
		public string FilteredNegBrushSerializable
		{
			get { return Serialize.BrushToString(FilteredNegBrush); }
			set { FilteredNegBrush = Serialize.StringToBrush(value); }
		}

		// ========== 4. Bid / Ask Volume ==========
		[NinjaScriptProperty]
		[Display(Name = "Show Bid/Ask Volume", Description = "Cumulative Ask volume (above zero) and Bid volume (below zero) since reset. Expands the panel scale.", Order = 1, GroupName = "4. Bid/Ask Volume")]
		public bool ShowBidAskVolume { get; set; }

		// ========== Data access for strategies ==========
		[Browsable(false)]
		[XmlIgnore]
		public Series<double> CumulativeDelta
		{
			get { Update(); return rawCvd; } // raw CVD close, regardless of display style
		}

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> FilteredCumulativeDelta
		{
			get { Update(); return Values[PlotFiltered]; }
		}

		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private NT8CVD[] cacheNT8CVD;
		public NT8CVD NT8CVD(NT8CvdResetMode resetMode, DateTime resetTime, NT8CvdDisplayStyle displayStyle, bool showFilteredCVD, double minTradeSize, double maxTradeSize, bool showBidAskVolume)
		{
			return NT8CVD(Input, resetMode, resetTime, displayStyle, showFilteredCVD, minTradeSize, maxTradeSize, showBidAskVolume);
		}

		public NT8CVD NT8CVD(ISeries<double> input, NT8CvdResetMode resetMode, DateTime resetTime, NT8CvdDisplayStyle displayStyle, bool showFilteredCVD, double minTradeSize, double maxTradeSize, bool showBidAskVolume)
		{
			if (cacheNT8CVD != null)
				for (int idx = 0; idx < cacheNT8CVD.Length; idx++)
					if (cacheNT8CVD[idx] != null && cacheNT8CVD[idx].ResetMode == resetMode && cacheNT8CVD[idx].ResetTime == resetTime && cacheNT8CVD[idx].DisplayStyle == displayStyle && cacheNT8CVD[idx].ShowFilteredCVD == showFilteredCVD && cacheNT8CVD[idx].MinTradeSize == minTradeSize && cacheNT8CVD[idx].MaxTradeSize == maxTradeSize && cacheNT8CVD[idx].ShowBidAskVolume == showBidAskVolume && cacheNT8CVD[idx].EqualsInput(input))
						return cacheNT8CVD[idx];
			return CacheIndicator<NT8CVD>(new NT8CVD(){ ResetMode = resetMode, ResetTime = resetTime, DisplayStyle = displayStyle, ShowFilteredCVD = showFilteredCVD, MinTradeSize = minTradeSize, MaxTradeSize = maxTradeSize, ShowBidAskVolume = showBidAskVolume }, input, ref cacheNT8CVD);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.NT8CVD NT8CVD(NT8CvdResetMode resetMode, DateTime resetTime, NT8CvdDisplayStyle displayStyle, bool showFilteredCVD, double minTradeSize, double maxTradeSize, bool showBidAskVolume)
		{
			return indicator.NT8CVD(Input, resetMode, resetTime, displayStyle, showFilteredCVD, minTradeSize, maxTradeSize, showBidAskVolume);
		}

		public Indicators.NT8CVD NT8CVD(ISeries<double> input , NT8CvdResetMode resetMode, DateTime resetTime, NT8CvdDisplayStyle displayStyle, bool showFilteredCVD, double minTradeSize, double maxTradeSize, bool showBidAskVolume)
		{
			return indicator.NT8CVD(input, resetMode, resetTime, displayStyle, showFilteredCVD, minTradeSize, maxTradeSize, showBidAskVolume);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.NT8CVD NT8CVD(NT8CvdResetMode resetMode, DateTime resetTime, NT8CvdDisplayStyle displayStyle, bool showFilteredCVD, double minTradeSize, double maxTradeSize, bool showBidAskVolume)
		{
			return indicator.NT8CVD(Input, resetMode, resetTime, displayStyle, showFilteredCVD, minTradeSize, maxTradeSize, showBidAskVolume);
		}

		public Indicators.NT8CVD NT8CVD(ISeries<double> input , NT8CvdResetMode resetMode, DateTime resetTime, NT8CvdDisplayStyle displayStyle, bool showFilteredCVD, double minTradeSize, double maxTradeSize, bool showBidAskVolume)
		{
			return indicator.NT8CVD(input, resetMode, resetTime, displayStyle, showFilteredCVD, minTradeSize, maxTradeSize, showBidAskVolume);
		}
	}
}

#endregion
