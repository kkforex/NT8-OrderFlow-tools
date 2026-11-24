#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// ---------------------------------------------------------------------------------------------
//  BigTrades for NinjaTrader 8
//  Visualises large, aggressive market orders (DeepCharts "Big Trades" style).
//
//  Requirements:
//   - Tick Replay enabled on the chart's data series (for history). Without it, only
//     real-time trades are captured.
//   - Data Series "Days to load" >= the indicator's Days To Load.
//
//  Data foundation: OnMarketData(Last) with aggressor = price vs e.Bid / e.Ask at print time,
//  tick-rule fallback for prints between the spread.
// ---------------------------------------------------------------------------------------------

public enum BigTradesInputType   { RawPrints, AggregatedByTimestamp, AggregatedByTimeWindow }
public enum BigTradesFilterMode  { Manual, Automatic }
public enum BigTradesIntensity   { Low, Medium, Strong }
public enum BigTradesMarker      { Circle, Square }
public enum BigTradesZoneMode    { All, ReverseOnly, TrendOnly, None }

namespace NinjaTrader.NinjaScript.Indicators
{
	public class BigTrades : Indicator
	{
		#region Internal types / state
		private class BigTrade
		{
			public int      Bar;
			public DateTime Time;
			public double   Price;
			public long     Volume;
			public bool     IsBuy;
		}

		private class PendingAgg
		{
			public int      Bar;
			public DateTime StartTime;
			public DateTime LastTime;
			public double   PV;
			public long     Volume;
			public bool     IsBuy;
		}

		private readonly object          sync     = new object();
		private List<BigTrade>           trades;
		private PendingAgg               pending;
		private double                   lastPrice;
		private bool                     lastSide = true;
		private DateTime                 displayCutoff = DateTime.MinValue;
		private bool                     warnedNoTickReplay;

		// Big Trades Analysis (automatic thresholds)
		private const long               AutoMinSamples   = 5000;
		private const long               AutoRecalcEvery  = 1000;
		private Dictionary<long, long>   sizeHist;
		private long                     histSamples;
		private long                     samplesAtLastCalc;
		private bool                     autoReady;
		private long                     autoP97, autoLow, autoMed, autoStrong;
		private long                     loadedLow, loadedMed, loadedStrong;

		// SharpDX resources
		private SharpDX.Direct2D1.Brush  askDx, bidDx, zoneAskDx, zoneBidDx, textDx;
		private SharpDX.DirectWrite.TextFormat textFormat;
		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                 = "Visualises large aggressive market orders (bubbles + horizontal zones). Requires Tick Replay for history.";
				Name                        = "BigTrades";
				Calculate                   = Calculate.OnEachTick;
				IsOverlay                   = true;
				DisplayInDataBox            = false;
				DrawOnPricePanel            = true;
				PaintPriceMarkers           = false;
				IsSuspendedWhileInactive    = false;
				ScaleJustification          = ScaleJustification.Right;

				// 1. Data Settings
				DaysToLoad                  = 3;
				InputType                   = BigTradesInputType.AggregatedByTimestamp;
				AggregationWindowMs         = 50;
				FilterMode                  = BigTradesFilterMode.Manual;
				MinFilter                   = 30;
				MaxFilter                   = 0;
				Intensity                   = BigTradesIntensity.Medium;
				SaveAnalysis                = true;

				// 2. Plot Settings
				Marker                      = BigTradesMarker.Circle;
				HollowFill                  = false;
				ShowText                    = true;
				StdDeviation                = 2.0;
				MinMarkerSize               = 8;
				MaxMarkerSize               = 40;
				MinOpacity                  = 35;
				MaxOpacity                  = 90;
				AskColor                    = Brushes.LimeGreen;
				BidColor                    = Brushes.Red;
				TextOnlyInsideBar           = false;
				TextSize                    = 10;
				TextColor                   = Brushes.White;

				// 3. Zones Settings
				ShowZones                   = true;
				ZonesBiggestOnly            = false;
				ShadowMode                  = BigTradesZoneMode.All;
				BodyMode                    = BigTradesZoneMode.All;
				ZoneBidColor                = Brushes.Red;
				ZoneAskColor                = Brushes.LimeGreen;
				ZoneOpacity                 = 25;
				ZoneBars                    = 20;
			}
			else if (State == State.DataLoaded)
			{
				trades       = new List<BigTrade>(4096);
				sizeHist     = new Dictionary<long, long>();
				pending      = null;
				histSamples  = 0;
				autoReady    = false;
				displayCutoff = ComputeCutoff(DaysToLoad);
				if (FilterMode == BigTradesFilterMode.Automatic)
					LoadAnalysis();
			}
			else if (State == State.Terminated)
			{
				if (SaveAnalysis && FilterMode == BigTradesFilterMode.Automatic && autoReady)
					SaveAnalysisFile();
				DisposeDx();
				if (textFormat != null) { textFormat.Dispose(); textFormat = null; }
			}
		}

		protected override void OnBarUpdate()
		{
			if (!warnedNoTickReplay && CurrentBar == 0 && !Bars.IsTickReplay)
			{
				warnedNoTickReplay = true;
				Draw.TextFixed(this, "BigTradesWarn",
					"BigTrades: Tick Replay is OFF — only real-time trades will be shown.",
					TextPosition.BottomRight);
			}
		}

		#region Trade capture / aggregation
		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (e.MarketDataType != MarketDataType.Last || CurrentBar < 0 || e.Volume <= 0)
				return;

			double price = e.Price;
			bool isBuy;
			if (e.Ask > 0 && price >= e.Ask)       isBuy = true;
			else if (e.Bid > 0 && price <= e.Bid)  isBuy = false;
			else if (price > lastPrice)            isBuy = true;
			else if (price < lastPrice)            isBuy = false;
			else                                   isBuy = lastSide;
			lastPrice = price;
			lastSide  = isBuy;

			int bar = ResolveBar(e.Time);
			bool added = false;

			lock (sync)
			{
				if (InputType == BigTradesInputType.RawPrints)
				{
					added = Commit(bar, e.Time, price, e.Volume, isBuy);
				}
				else
				{
					bool cont = pending != null && pending.IsBuy == isBuy &&
						(InputType == BigTradesInputType.AggregatedByTimestamp
							? e.Time == pending.LastTime
							: (e.Time - pending.StartTime).TotalMilliseconds <= AggregationWindowMs);

					if (cont)
					{
						pending.PV      += price * e.Volume;
						pending.Volume  += e.Volume;
						pending.LastTime = e.Time;
					}
					else
					{
						if (pending != null)
							added = CommitPending();
						pending = new PendingAgg
						{
							Bar = bar, StartTime = e.Time, LastTime = e.Time,
							PV = price * e.Volume, Volume = e.Volume, IsBuy = isBuy
						};
					}
				}
			}

			if (added && State == State.Realtime)
				ForceRefresh();
		}

		private bool CommitPending()
		{
			double vwap = Instrument.MasterInstrument.RoundToTickSize(pending.PV / pending.Volume);
			bool r = Commit(pending.Bar, pending.StartTime, vwap, pending.Volume, pending.IsBuy);
			pending = null;
			return r;
		}

		// Adds the trade to the size histogram (analysis) and stores it if it is a candidate.
		private bool Commit(int bar, DateTime time, double price, long volume, bool isBuy)
		{
			long c;
			sizeHist.TryGetValue(volume, out c);
			sizeHist[volume] = c + 1;
			histSamples++;

			if (histSamples >= AutoMinSamples && histSamples - samplesAtLastCalc >= AutoRecalcEvery)
				RecalcAuto();

			if (DaysToLoad > 0 && time < displayCutoff)
				return false;

			if (volume < StoreFloor())
				return false;

			trades.Add(new BigTrade { Bar = bar, Time = time, Price = price, Volume = volume, IsBuy = isBuy });
			return true;
		}

		private long StoreFloor()
		{
			if (FilterMode == BigTradesFilterMode.Manual) return Math.Max(1, MinFilter);
			if (autoReady)     return Math.Max(2, autoP97);
			if (loadedLow > 0) return Math.Max(2, loadedLow / 2);
			return 2;
		}

		// OnMarketData can fire for the first tick of a new time-based bar before that bar exists.
		private int ResolveBar(DateTime t)
		{
			int b = CurrentBar;
			BarsPeriodType pt = BarsPeriod.BarsPeriodType;
			bool timeBased = pt == BarsPeriodType.Second || pt == BarsPeriodType.Minute ||
			                 pt == BarsPeriodType.Day    || pt == BarsPeriodType.Week   ||
			                 pt == BarsPeriodType.Month  || pt == BarsPeriodType.Year;
			if (timeBased && b >= 0 && t > Time[0])
				b++;
			return b;
		}

		private static DateTime ComputeCutoff(int days)
		{
			if (days <= 0) return DateTime.MinValue;
			DateTime d = DateTime.Now;
			int n = days;
			while (n > 0)
			{
				d = d.AddDays(-1);
				if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday)
					n--;
			}
			return d;
		}
		#endregion

		#region Big Trades Analysis (automatic thresholds)
		private void RecalcAuto()
		{
			List<long> keys = sizeHist.Keys.ToList();
			keys.Sort();
			autoP97    = Math.Max(2, Percentile(keys, 0.97));
			autoLow    = Math.Max(2, Percentile(keys, 0.990));
			autoMed    = Math.Max(2, Percentile(keys, 0.997));
			autoStrong = Math.Max(2, Percentile(keys, 0.9995));
			samplesAtLastCalc = histSamples;
			autoReady = true;
		}

		private long Percentile(List<long> sortedKeys, double p)
		{
			long target = (long)Math.Ceiling(p * histSamples);
			long cum = 0;
			foreach (long k in sortedKeys)
			{
				cum += sizeHist[k];
				if (cum >= target) return k;
			}
			return sortedKeys.Count > 0 ? sortedKeys[sortedKeys.Count - 1] : 0;
		}

		private void GetThresholds(out long lo, out long hi)
		{
			if (FilterMode == BigTradesFilterMode.Manual)
			{
				lo = Math.Max(1, MinFilter);
				hi = MaxFilter > 0 ? MaxFilter : long.MaxValue;
				return;
			}
			hi = long.MaxValue;
			if (autoReady)
				lo = Intensity == BigTradesIntensity.Low ? autoLow : Intensity == BigTradesIntensity.Medium ? autoMed : autoStrong;
			else if (loadedLow > 0)
				lo = Intensity == BigTradesIntensity.Low ? loadedLow : Intensity == BigTradesIntensity.Medium ? loadedMed : loadedStrong;
			else
				lo = Math.Max(1, MinFilter);
		}

		private string AnalysisPath()
		{
			string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "BigTradesAnalysis");
			string inp = InputType == BigTradesInputType.AggregatedByTimeWindow ? "Window" + AggregationWindowMs : InputType.ToString();
			return Path.Combine(dir, Instrument.MasterInstrument.Name + "_" + inp + ".txt");
		}

		private void LoadAnalysis()
		{
			try
			{
				string path = AnalysisPath();
				if (!File.Exists(path)) return;
				string[] parts = File.ReadAllText(path).Split(';');
				if (parts.Length < 3) return;
				loadedLow    = long.Parse(parts[0]);
				loadedMed    = long.Parse(parts[1]);
				loadedStrong = long.Parse(parts[2]);
			}
			catch (Exception ex) { Print("BigTrades: could not load analysis — " + ex.Message); }
		}

		private void SaveAnalysisFile()
		{
			try
			{
				if (Instrument == null) return;
				string path = AnalysisPath();
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.WriteAllText(path, autoLow + ";" + autoMed + ";" + autoStrong + ";" + histSamples + ";" + DateTime.Now.ToString("s"));
			}
			catch (Exception ex) { Print("BigTrades: could not save analysis — " + ex.Message); }
		}
		#endregion

		#region Rendering
		public override void OnRenderTargetChanged()
		{
			DisposeDx();
			if (RenderTarget == null) return;
			askDx     = AskColor.ToDxBrush(RenderTarget);
			bidDx     = BidColor.ToDxBrush(RenderTarget);
			zoneAskDx = ZoneAskColor.ToDxBrush(RenderTarget);
			zoneBidDx = ZoneBidColor.ToDxBrush(RenderTarget);
			textDx    = TextColor.ToDxBrush(RenderTarget);
		}

		private void DisposeDx()
		{
			if (askDx     != null) { askDx.Dispose();     askDx     = null; }
			if (bidDx     != null) { bidDx.Dispose();     bidDx     = null; }
			if (zoneAskDx != null) { zoneAskDx.Dispose(); zoneAskDx = null; }
			if (zoneBidDx != null) { zoneBidDx.Dispose(); zoneBidDx = null; }
			if (textDx    != null) { textDx.Dispose();    textDx    = null; }
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (Bars == null || ChartBars == null || RenderTarget == null || trades == null || askDx == null)
				return;

			if (textFormat == null)
				textFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
					SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, TextSize);

			SharpDX.Direct2D1.AntialiasMode oldAA = RenderTarget.AntialiasMode;
			RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;

			lock (sync)
			{
				if (trades.Count == 0) { RenderTarget.AntialiasMode = oldAA; return; }

				long lo, hi;
				GetThresholds(out lo, out hi);

				// Size statistics for scaling (all trades passing the filter)
				double sum = 0, sumSq = 0; int n = 0;
				foreach (BigTrade t in trades)
				{
					if (t.Volume < lo || t.Volume > hi) continue;
					sum += t.Volume; sumSq += (double)t.Volume * t.Volume; n++;
				}
				if (n == 0) { RenderTarget.AntialiasMode = oldAA; return; }
				double mean = sum / n;
				double sd   = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));

				int   fromIdx  = ChartBars.FromIndex;
				int   toIdx    = ChartBars.ToIndex;
				int   lastBar  = Bars.Count - 1;
				float barDist  = chartControl.Properties.BarDistance;
				double tick    = Instrument.MasterInstrument.TickSize;

				// ---------- Zones ----------
				if (ShowZones)
				{
					int zStart = LowerBound(fromIdx - ZoneBars);
					Dictionary<int, long> barMax = null;
					if (ZonesBiggestOnly)
					{
						barMax = new Dictionary<int, long>();
						for (int i = zStart; i < trades.Count && trades[i].Bar <= toIdx; i++)
						{
							BigTrade t = trades[i];
							if (t.Volume < lo || t.Volume > hi) continue;
							long m;
							if (!barMax.TryGetValue(t.Bar, out m) || t.Volume > m) barMax[t.Bar] = t.Volume;
						}
					}

					zoneAskDx.Opacity = ZoneOpacity / 100f;
					zoneBidDx.Opacity = ZoneOpacity / 100f;

					for (int i = zStart; i < trades.Count && trades[i].Bar <= toIdx; i++)
					{
						BigTrade t = trades[i];
						if (t.Volume < lo || t.Volume > hi || t.Bar > lastBar || t.Bar < 0) continue;
						if (barMax != null && barMax[t.Bar] != t.Volume) continue;
						if (!ZoneAllowed(t)) continue;

						float x0 = chartControl.GetXByBarIndex(ChartBars, t.Bar);
						float x1 = x0 + ZoneBars * barDist;
						float yT = chartScale.GetYByValue(t.Price + tick / 2);
						float yB = chartScale.GetYByValue(t.Price - tick / 2);
						float h  = Math.Max(2f, Math.Abs(yB - yT));
						float yc = chartScale.GetYByValue(t.Price);
						RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, yc - h / 2, x1 - x0, h), t.IsBuy ? zoneAskDx : zoneBidDx);
					}
				}

				// ---------- Markers ----------
				for (int i = LowerBound(fromIdx); i < trades.Count && trades[i].Bar <= toIdx; i++)
				{
					BigTrade t = trades[i];
					if (t.Volume < lo || t.Volume > hi || t.Bar > lastBar || t.Bar < 0) continue;

					double z = sd > 0 ? (t.Volume - mean) / sd : 0;
					double s = Math.Max(0.1, StdDeviation);
					double f = Math.Min(1, Math.Max(0, (z + s) / (2 * s)));

					float diameter = (float)(MinMarkerSize + f * (MaxMarkerSize - MinMarkerSize));
					float r        = diameter / 2f;
					float opacity  = (float)(MinOpacity + f * (MaxOpacity - MinOpacity)) / 100f;

					float x = chartControl.GetXByBarIndex(ChartBars, t.Bar);
					float y = chartScale.GetYByValue(t.Price);

					SharpDX.Direct2D1.Brush b = t.IsBuy ? askDx : bidDx;
					b.Opacity = opacity;

					if (Marker == BigTradesMarker.Circle)
					{
						var el = new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(x, y), r, r);
						if (HollowFill) RenderTarget.DrawEllipse(el, b, 2f);
						else            RenderTarget.FillEllipse(el, b);
					}
					else
					{
						var rect = new SharpDX.RectangleF(x - r, y - r, diameter, diameter);
						if (HollowFill) RenderTarget.DrawRectangle(rect, b, 2f);
						else            RenderTarget.FillRectangle(rect, b);
					}

					if (ShowText && (!TextOnlyInsideBar || InBody(t)))
					{
						string txt = t.Volume >= 10000 ? (t.Volume / 1000.0).ToString("0.#") + "K" : t.Volume.ToString();
						using (var tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, txt, textFormat, 200f, 50f))
						{
							float w = tl.Metrics.Width, h = tl.Metrics.Height;
							RenderTarget.DrawTextLayout(new SharpDX.Vector2(x - w / 2, y - h / 2), tl, textDx);
						}
					}
				}
			}

			RenderTarget.AntialiasMode = oldAA;
		}

		private bool InBody(BigTrade t)
		{
			double o = Bars.GetOpen(t.Bar), c = Bars.GetClose(t.Bar);
			double eps = Instrument.MasterInstrument.TickSize / 4;
			return t.Price >= Math.Min(o, c) - eps && t.Price <= Math.Max(o, c) + eps;
		}

		private bool ZoneAllowed(BigTrade t)
		{
			double o = Bars.GetOpen(t.Bar), c = Bars.GetClose(t.Bar);
			BigTradesZoneMode mode = InBody(t) ? BodyMode : ShadowMode;
			bool bearish = c < o, bullish = c > o;
			switch (mode)
			{
				case BigTradesZoneMode.All:         return true;
				case BigTradesZoneMode.None:        return false;
				case BigTradesZoneMode.ReverseOnly: return (bearish && t.IsBuy) || (bullish && !t.IsBuy);
				case BigTradesZoneMode.TrendOnly:   return (bearish && !t.IsBuy) || (bullish && t.IsBuy);
			}
			return false;
		}

		// First index whose Bar >= bar (trades are appended in bar order)
		private int LowerBound(int bar)
		{
			int l = 0, h = trades.Count;
			while (l < h)
			{
				int m = (l + h) >> 1;
				if (trades[m].Bar < bar) l = m + 1; else h = m;
			}
			return l;
		}
		#endregion

		#region Properties
		// ---------------- 1. Data Settings ----------------
		[NinjaScriptProperty, Range(0, int.MaxValue)]
		[Display(Name = "Days To Load", Description = "Historical days of big trades to display (0 = all loaded). Data Series days-to-load must cover this.", Order = 1, GroupName = "1. Data Settings")]
		public int DaysToLoad { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Input Type", Description = "Raw prints, or prints aggregated into one order (same side) by identical timestamp or by time window.", Order = 2, GroupName = "1. Data Settings")]
		public BigTradesInputType InputType { get; set; }

		[NinjaScriptProperty, Range(1, 5000)]
		[Display(Name = "Aggregation Window (ms)", Description = "Used only with AggregatedByTimeWindow.", Order = 3, GroupName = "1. Data Settings")]
		public int AggregationWindowMs { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Filter Mode", Description = "Manual = Min/Max. Automatic = Big Trades Analysis thresholds.", Order = 4, GroupName = "1. Data Settings")]
		public BigTradesFilterMode FilterMode { get; set; }

		[NinjaScriptProperty, Range(1, int.MaxValue)]
		[Display(Name = "Manual Filter Min", Description = "Minimum trade size (contracts) plotted.", Order = 5, GroupName = "1. Data Settings")]
		public int MinFilter { get; set; }

		[NinjaScriptProperty, Range(0, int.MaxValue)]
		[Display(Name = "Manual Filter Max", Description = "Maximum trade size plotted (0 = no maximum).", Order = 6, GroupName = "1. Data Settings")]
		public int MaxFilter { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Intensity Level", Description = "Automatic mode: Low ≈ 99th, Medium ≈ 99.7th, Strong ≈ 99.95th percentile of trade size.", Order = 7, GroupName = "1. Data Settings")]
		public BigTradesIntensity Intensity { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Save Analysis", Description = "Save automatic thresholds per instrument and reuse them on next load until enough new data is collected.", Order = 8, GroupName = "1. Data Settings")]
		public bool SaveAnalysis { get; set; }

		// ---------------- 2. Plot Settings ----------------
		[NinjaScriptProperty]
		[Display(Name = "Markers", Order = 1, GroupName = "2. Plot Settings")]
		public BigTradesMarker Marker { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Hollow Fill", Order = 2, GroupName = "2. Plot Settings")]
		public bool HollowFill { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Text", Order = 3, GroupName = "2. Plot Settings")]
		public bool ShowText { get; set; }

		[NinjaScriptProperty, Range(0.1, 10)]
		[Display(Name = "Standard Deviation", Description = "Size/opacity scale spans mean ± this many standard deviations (recommended default).", Order = 4, GroupName = "2. Plot Settings")]
		public double StdDeviation { get; set; }

		[NinjaScriptProperty, Range(2, 200)]
		[Display(Name = "Minimum Size", Description = "Smallest marker diameter (px).", Order = 5, GroupName = "2. Plot Settings")]
		public int MinMarkerSize { get; set; }

		[NinjaScriptProperty, Range(2, 300)]
		[Display(Name = "Maximum Size", Description = "Largest marker diameter (px).", Order = 6, GroupName = "2. Plot Settings")]
		public int MaxMarkerSize { get; set; }

		[NinjaScriptProperty, Range(0, 100)]
		[Display(Name = "Minimum Opacity", Order = 7, GroupName = "2. Plot Settings")]
		public int MinOpacity { get; set; }

		[NinjaScriptProperty, Range(0, 100)]
		[Display(Name = "Maximum Opacity", Order = 8, GroupName = "2. Plot Settings")]
		public int MaxOpacity { get; set; }

		[XmlIgnore]
		[Display(Name = "Ask Color (aggressive buyers)", Order = 9, GroupName = "2. Plot Settings")]
		public Brush AskColor { get; set; }
		[Browsable(false)]
		public string AskColorSerializable { get { return Serialize.BrushToString(AskColor); } set { AskColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Bid Color (aggressive sellers)", Order = 10, GroupName = "2. Plot Settings")]
		public Brush BidColor { get; set; }
		[Browsable(false)]
		public string BidColorSerializable { get { return Serialize.BrushToString(BidColor); } set { BidColor = Serialize.StringToBrush(value); } }

		[NinjaScriptProperty]
		[Display(Name = "Plot Text Only Inside Bar", Description = "Show text only for trades whose price is inside the candle body.", Order = 11, GroupName = "2. Plot Settings")]
		public bool TextOnlyInsideBar { get; set; }

		[NinjaScriptProperty, Range(6, 40)]
		[Display(Name = "Text Size", Order = 12, GroupName = "2. Plot Settings")]
		public int TextSize { get; set; }

		[XmlIgnore]
		[Display(Name = "Text Color", Order = 13, GroupName = "2. Plot Settings")]
		public Brush TextColor { get; set; }
		[Browsable(false)]
		public string TextColorSerializable { get { return Serialize.BrushToString(TextColor); } set { TextColor = Serialize.StringToBrush(value); } }

		// ---------------- 3. Zones Settings ----------------
		[NinjaScriptProperty]
		[Display(Name = "Show Zones", Order = 1, GroupName = "3. Zones Settings")]
		public bool ShowZones { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Biggest Only", Description = "Zone only for the largest big trade of each bar.", Order = 2, GroupName = "3. Zones Settings")]
		public bool ZonesBiggestOnly { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Shadow Mode (Wick)", Description = "All / ReverseOnly (bear→buyers, bull→sellers) / TrendOnly (bear→sellers, bull→buyers) / None.", Order = 3, GroupName = "3. Zones Settings")]
		public BigTradesZoneMode ShadowMode { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Body Mode", Description = "All / ReverseOnly (bear→buyers, bull→sellers) / TrendOnly (bear→sellers, bull→buyers) / None.", Order = 4, GroupName = "3. Zones Settings")]
		public BigTradesZoneMode BodyMode { get; set; }

		[XmlIgnore]
		[Display(Name = "Bid Color", Order = 5, GroupName = "3. Zones Settings")]
		public Brush ZoneBidColor { get; set; }
		[Browsable(false)]
		public string ZoneBidColorSerializable { get { return Serialize.BrushToString(ZoneBidColor); } set { ZoneBidColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Ask Color", Order = 6, GroupName = "3. Zones Settings")]
		public Brush ZoneAskColor { get; set; }
		[Browsable(false)]
		public string ZoneAskColorSerializable { get { return Serialize.BrushToString(ZoneAskColor); } set { ZoneAskColor = Serialize.StringToBrush(value); } }

		[NinjaScriptProperty, Range(0, 100)]
		[Display(Name = "Opacity", Order = 7, GroupName = "3. Zones Settings")]
		public int ZoneOpacity { get; set; }

		[NinjaScriptProperty, Range(1, 1000)]
		[Display(Name = "Number of Bars", Description = "How far zones extend to the right.", Order = 8, GroupName = "3. Zones Settings")]
		public int ZoneBars { get; set; }
		#endregion
	}
}
