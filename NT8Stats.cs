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
#endregion

// Declared outside the Indicators namespace so NinjaTrader's generated wrapper code can resolve it.
public enum NT8StatsInputData
{
	Volume,          // every print is one trade
	AggregateTrades  // consecutive prints with same timestamp + aggressor side are merged into one trade
}

//==================================================================================================
//  NT8Stats – NinjaTrader 8
//  Multi-row order-flow statistics table drawn per bar in its own panel (NT8Charts "NT8 Stats" style).
//
//  Rows: Tot Vol, Bid Vol, Ask Vol, Delta Vol, Max Delta, Min Delta, Tot NT, Delta NT, Delta HL,
//        Delta %, Delta Dly (session CVD), Vol(s), COT H, COT L, COT Bar, Time(s), Bar Ratio,
//        High Ratio, Low Ratio, Total Effort, Delta Effort.
//
//  DATA: trades are classified in OnMarketData (price >= ask -> buy, price <= bid -> sell, inside
//        spread -> tick rule). TICK REPLAY must be enabled on the data series for historical bars;
//        without it only realtime bars are populated.
//
//  Ticks are queued in OnMarketData and committed in OnBarUpdate. This avoids the Tick Replay quirk
//  where the opening tick of a new bar reaches OnMarketData while CurrentBar still points at the old bar.
//==================================================================================================
namespace NinjaTrader.NinjaScript.Indicators
{
	public class NT8Stats : Indicator
	{
		#region Internal types
		private enum Row
		{
			TotVol, BidVol, AskVol, DeltaVol, MaxDelta, MinDelta, TotNT, DeltaNT, DeltaHL, DeltaPct,
			DeltaDly, VolPerSec, CotHigh, CotLow, CotBar, TimeSec, BarRatio, HighRatio, LowRatio,
			TotalEffort, DeltaEffort
		}

		private enum Kind { Volume, Bid, Ask, Signed, Neutral }

		private struct Print
		{
			public double   Price;
			public long     Size;
			public int      Side;   // +1 aggressive buy (ask), -1 aggressive sell (bid)
			public DateTime Time;
		}

		private class BarStats
		{
			public bool     HasPrints;
			public bool     HasDelta;
			public long     AskVol, BidVol;
			public long     AskTrades, BidTrades;
			public long     RunDelta, MaxDelta, MinDelta;
			public long     DeltaAtHigh, DeltaAtLow;
			public double   HighPx = double.MinValue;
			public double   LowPx  = double.MaxValue;
			public DateTime FirstTime, LastTime;
			public double   CvdStart;
			public double   HighRatio = double.NaN;
			public double   LowRatio  = double.NaN;
			public Dictionary<long, long[]> Levels;   // only alive while bar is forming; [0]=bid vol, [1]=ask vol
		}
		#endregion

		#region Row metadata
		private const int RowCount = 21;

		private static readonly string[] RowNames =
		{
			"Tot Vol", "Bid Vol", "Ask Vol", "Delta Vol", "Max Delta", "Min Delta", "Tot NT", "Delta NT",
			"Delta HL", "Delta %", "Delta Dly", "Vol(s)", "COT H", "COT L", "COT Bar", "Time(s)",
			"Bar Ratio", "High Ratio", "Low Ratio", "Total Effort", "Delta Effort"
		};

		private static readonly Kind[] RowKinds =
		{
			Kind.Volume, Kind.Bid, Kind.Ask, Kind.Signed, Kind.Signed, Kind.Signed, Kind.Volume, Kind.Signed,
			Kind.Neutral, Kind.Signed, Kind.Signed, Kind.Volume, Kind.Signed, Kind.Signed, Kind.Signed, Kind.Neutral,
			Kind.Neutral, Kind.Neutral, Kind.Neutral, Kind.Volume, Kind.Signed
		};
		#endregion

		#region State
		private readonly object         sync    = new object();
		private readonly List<BarStats> stats   = new List<BarStats>();
		private readonly List<Print>    pending = new List<Print>();
		private readonly List<Print>    agg     = new List<Print>();
		private long     aggSize;
		private int      aggSide;
		private DateTime aggTime;

		private BarStats cur;
		private int      workingBar = -1;
		private double   lastTradePx;
		private int      lastSide = 1;
		private bool     tickReplay;

		private bool[]   showRow;
		private double[] thrRow;

		private SharpDX.Direct2D1.Brush dxVolume, dxBid, dxAsk, dxPos, dxNeg, dxNeutral, dxCellBg, dxText,
		                                dxGrid, dxHeaderBg, dxHeaderText, dxThr, dxThrPos, dxThrNeg;
		private SharpDX.DirectWrite.TextFormat cellFormat, headerFormat;
		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description              = "Per-bar order-flow statistics table (volume, bid/ask, delta, CVD, COT, ratios, effort) in a separate panel.";
				Name                     = "NT8Stats";
				Calculate                = Calculate.OnEachTick;
				IsOverlay                = false;
				DisplayInDataBox         = true;
				DrawOnPricePanel         = false;
				PaintPriceMarkers        = false;
				IsSuspendedWhileInactive = false;
				BarsRequiredToPlot       = 0;
				MaximumBarsLookBack      = MaximumBarsLookBack.Infinite;

				// 1. Data
				InputData         = NT8StatsInputData.Volume;
				MinFilter         = 0;
				MaxFilter         = 0;
				ResetCvdOnSession = true;

				// 2. Plot
				StdDevForColoring   = 2.0;
				MinOpacity          = 10;
				TextFont            = new SimpleFont("Arial", 10);
				AutoTextFormat      = true;
				InvertOrder         = false;
				MinCellWidthForText = 22;
				GridLineWidth       = 1;

				// 3. Header
				ShowHeader       = true;
				HeaderWidth      = 80;
				HeaderFont       = new SimpleFont("Arial", 10);
				HeaderBackground = MakeFrozen(Color.FromRgb(38, 38, 44));
				HeaderTextColor  = Brushes.Gainsboro;

				// 4. Colors
				VolumeColor    = Brushes.SteelBlue;
				BidColor       = Brushes.Crimson;
				AskColor       = Brushes.LimeGreen;
				PositiveColor  = Brushes.LimeGreen;
				NegativeColor  = Brushes.Red;
				NeutralColor   = Brushes.Goldenrod;
				CellBackground = Brushes.Transparent;
				TextColor      = Brushes.White;
				GridColor      = MakeFrozen(Color.FromRgb(70, 70, 70));

				// 5. Thresholds
				ThresholdColor         = Brushes.DarkOrange;
				PositiveThresholdColor = Brushes.DeepSkyBlue;
				NegativeThresholdColor = Brushes.Magenta;

				// 6. Rows (default set)
				ShowTotVol = ShowBidVol = ShowAskVol = ShowDeltaVol = ShowMaxDelta = ShowMinDelta = true;
				ShowDeltaPct = ShowDeltaDly = ShowTotNT = ShowTimeSec = true;

				AddPlot(Brushes.Transparent, "Delta");
				AddPlot(Brushes.Transparent, "CVD");
				AddPlot(Brushes.Transparent, "TotalVolume");
			}
			else if (State == State.Configure)
			{
				// Ticks are committed in OnBarUpdate, so it has to run on every tick.
				Calculate = Calculate.OnEachTick;
			}
			else if (State == State.DataLoaded)
			{
				tickReplay = Bars != null && Bars.IsTickReplay;
				BuildRowConfig();

				lock (sync)
				{
					stats.Clear();
					pending.Clear();
					agg.Clear();
					cur        = null;
					workingBar = -1;
				}

				DisposeText();
				cellFormat = TextFont.ToDirectWriteTextFormat();
				cellFormat.TextAlignment      = SharpDX.DirectWrite.TextAlignment.Center;
				cellFormat.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
				cellFormat.WordWrapping       = SharpDX.DirectWrite.WordWrapping.NoWrap;

				headerFormat = HeaderFont.ToDirectWriteTextFormat();
				headerFormat.TextAlignment      = SharpDX.DirectWrite.TextAlignment.Leading;
				headerFormat.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
				headerFormat.WordWrapping       = SharpDX.DirectWrite.WordWrapping.NoWrap;
			}
			else if (State == State.Terminated)
			{
				DisposeDx();
				DisposeText();
			}
		}

		#region Data collection
		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (e.MarketDataType != MarketDataType.Last || e.Volume <= 0)
				return;

			int side;
			if (e.Ask > 0 && e.Price >= e.Ask)       side = 1;
			else if (e.Bid > 0 && e.Price <= e.Bid)  side = -1;
			else if (e.Price > lastTradePx)          side = 1;   // inside spread: tick rule
			else if (e.Price < lastTradePx)          side = -1;
			else                                     side = lastSide;

			lastTradePx = e.Price;
			lastSide    = side;

			Print p;
			p.Price = e.Price;
			p.Size  = e.Volume;
			p.Side  = side;
			p.Time  = e.Time;
			pending.Add(p);
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0)
				return;

			lock (sync)
			{
				if (CurrentBar != workingBar)
				{
					FlushAggregate();   // pending aggregate belongs to the bar that is closing
					FinalizeBar();
					StartBar(CurrentBar);
				}

				for (int k = 0; k < pending.Count; k++)
					Route(pending[k]);
				pending.Clear();

				UpdateRatios(cur);
			}

			long delta = cur.AskVol - cur.BidVol;
			Values[0][0] = delta;
			Values[1][0] = cur.CvdStart + delta;
			Values[2][0] = cur.AskVol + cur.BidVol;
		}

		private void StartBar(int idx)
		{
			while (stats.Count <= idx)
				stats.Add(new BarStats());

			cur        = stats[idx];
			cur.Levels = new Dictionary<long, long[]>();
			workingBar = idx;

			BarStats prev = idx > 0 ? stats[idx - 1] : null;
			if (prev == null || (ResetCvdOnSession && Bars.IsFirstBarOfSession))
				cur.CvdStart = 0;
			else
				cur.CvdStart = prev.CvdStart + (prev.AskVol - prev.BidVol);
		}

		private void FinalizeBar()
		{
			if (cur == null)
				return;
			UpdateRatios(cur);
			cur.Levels = null;   // release per-price map once the bar is complete
		}

		private void Route(Print p)
		{
			if (InputData == NT8StatsInputData.Volume)
			{
				bool pass = PassFilter(p.Size);
				ApplyPrint(p, pass);
				if (pass)
					CountTrade(p.Side);
				return;
			}

			if (agg.Count > 0 && p.Time == aggTime && p.Side == aggSide)
			{
				agg.Add(p);
				aggSize += p.Size;
			}
			else
			{
				FlushAggregate();
				agg.Add(p);
				aggSize = p.Size;
				aggSide = p.Side;
				aggTime = p.Time;
			}
		}

		private void FlushAggregate()
		{
			if (agg.Count == 0)
				return;

			bool pass = PassFilter(aggSize);
			for (int k = 0; k < agg.Count; k++)
				ApplyPrint(agg[k], pass);
			if (pass)
				CountTrade(aggSide);

			agg.Clear();
			aggSize = 0;
		}

		private bool PassFilter(long size)
		{
			if (MinFilter > 0 && size < MinFilter) return false;
			if (MaxFilter > 0 && size > MaxFilter) return false;
			return true;
		}

		private void CountTrade(int side)
		{
			if (cur == null) return;
			if (side > 0) cur.AskTrades++;
			else          cur.BidTrades++;
		}

		private void ApplyPrint(Print p, bool counted)
		{
			BarStats s = cur;
			if (s == null)
				return;

			if (!s.HasPrints)
			{
				s.HasPrints = true;
				s.FirstTime = p.Time;
			}
			s.LastTime = p.Time;

			// Extremes use every print (filters only affect volume statistics).
			// Delta snapshot is taken BEFORE this print so COT includes the trade at the extreme.
			if (p.Price >= s.HighPx) { s.HighPx = p.Price; s.DeltaAtHigh = s.RunDelta; }
			if (p.Price <= s.LowPx)  { s.LowPx  = p.Price; s.DeltaAtLow  = s.RunDelta; }

			if (!counted)
				return;

			if (p.Side > 0) s.AskVol += p.Size;
			else            s.BidVol += p.Size;

			s.RunDelta += p.Side > 0 ? p.Size : -p.Size;
			if (!s.HasDelta)
			{
				s.MaxDelta = s.MinDelta = s.RunDelta;
				s.HasDelta = true;
			}
			else
			{
				if (s.RunDelta > s.MaxDelta) s.MaxDelta = s.RunDelta;
				if (s.RunDelta < s.MinDelta) s.MinDelta = s.RunDelta;
			}

			if (s.Levels != null)
			{
				long   key = PriceKey(p.Price);
				long[] lv;
				if (!s.Levels.TryGetValue(key, out lv))
				{
					lv = new long[2];
					s.Levels[key] = lv;
				}
				lv[p.Side > 0 ? 1 : 0] += p.Size;
			}
		}

		// High Ratio = ask vol at (High - 1 tick) / ask vol at High   -> buyer exhaustion
		// Low Ratio  = bid vol at (Low + 1 tick)  / bid vol at Low    -> seller exhaustion
		private void UpdateRatios(BarStats s)
		{
			if (s == null || s.Levels == null || !s.HasPrints)
				return;

			long hk = PriceKey(s.HighPx);
			long lk = PriceKey(s.LowPx);
			s.HighRatio = SafeRatio(LevelVol(s, hk - 1, 1), LevelVol(s, hk, 1));
			s.LowRatio  = SafeRatio(LevelVol(s, lk + 1, 0), LevelVol(s, lk, 0));
		}

		private static long LevelVol(BarStats s, long key, int idx)
		{
			long[] lv;
			return s.Levels.TryGetValue(key, out lv) ? lv[idx] : 0;
		}

		private static double SafeRatio(long num, long den)
		{
			if (den == 0)
				return num == 0 ? double.NaN : double.PositiveInfinity;
			return (double)num / den;
		}

		private long PriceKey(double price)
		{
			return (long)Math.Round(price / TickSize);
		}
		#endregion

		#region Metric calculation
		private double GetValue(BarStats s, int barIdx, Row r)
		{
			long   tot      = s.AskVol + s.BidVol;
			long   delta    = s.AskVol - s.BidVol;
			double hlTicks  = Math.Round((Bars.GetHigh(barIdx) - Bars.GetLow(barIdx)) / TickSize);
			double width    = Math.Max(1.0, hlTicks);
			double duration = s.HasPrints ? (s.LastTime - s.FirstTime).TotalSeconds : 0;
			bool   bullish  = Bars.GetClose(barIdx) >= Bars.GetOpen(barIdx);
			double cotHigh  = s.HasPrints ? s.RunDelta - s.DeltaAtHigh : 0;
			double cotLow   = s.HasPrints ? s.RunDelta - s.DeltaAtLow  : 0;

			switch (r)
			{
				case Row.TotVol:      return tot;
				case Row.BidVol:      return s.BidVol;
				case Row.AskVol:      return s.AskVol;
				case Row.DeltaVol:    return delta;
				case Row.MaxDelta:    return s.HasDelta ? s.MaxDelta : 0;
				case Row.MinDelta:    return s.HasDelta ? s.MinDelta : 0;
				case Row.TotNT:       return s.AskTrades + s.BidTrades;
				case Row.DeltaNT:     return s.AskTrades - s.BidTrades;
				case Row.DeltaHL:     return hlTicks;
				case Row.DeltaPct:    return tot > 0 ? 100.0 * delta / tot : 0;
				case Row.DeltaDly:    return s.CvdStart + delta;
				case Row.VolPerSec:   return tot / Math.Max(1.0, duration);
				case Row.CotHigh:     return cotHigh;
				case Row.CotLow:      return cotLow;
				case Row.CotBar:      return bullish ? cotHigh : cotLow;
				case Row.TimeSec:     return duration;
				case Row.BarRatio:    return bullish ? s.LowRatio : s.HighRatio;
				case Row.HighRatio:   return s.HighRatio;
				case Row.LowRatio:    return s.LowRatio;
				case Row.TotalEffort: return tot / width;
				case Row.DeltaEffort: return delta / width;
			}
			return 0;
		}

		private string FormatValue(Row r, double v)
		{
			if (double.IsNaN(v))      return "-";
			if (double.IsInfinity(v)) return "\u221E";

			switch (r)
			{
				case Row.DeltaPct:
					return v.ToString("0.0") + "%";
				case Row.HighRatio:
				case Row.LowRatio:
				case Row.BarRatio:
					return v.ToString("0.00");
				case Row.TimeSec:
					return v < 100 ? v.ToString("0.0") : (AutoTextFormat ? Compact(v) : v.ToString("0"));
				case Row.VolPerSec:
				case Row.TotalEffort:
				case Row.DeltaEffort:
					if (AutoTextFormat) return Compact(v);
					return Math.Abs(v) < 100 ? v.ToString("0.0") : v.ToString("0");
				default:
					return AutoTextFormat ? Compact(v) : v.ToString("0");
			}
		}

		private static string Compact(double v)
		{
			double a = Math.Abs(v);
			if (a >= 1e6) return (v / 1e6).ToString("0.0#") + "M";
			if (a >= 1e4) return (v / 1e3).ToString("0") + "K";
			if (a >= 1e3) return (v / 1e3).ToString("0.0") + "K";
			if (a < 100 && v != Math.Floor(v)) return v.ToString("0.0");
			return v.ToString("0");
		}
		#endregion

		#region Rendering
		public override void OnRenderTargetChanged()
		{
			DisposeDx();
			if (RenderTarget == null)
				return;

			dxVolume     = ToDx(VolumeColor,            Brushes.SteelBlue);
			dxBid        = ToDx(BidColor,               Brushes.Crimson);
			dxAsk        = ToDx(AskColor,               Brushes.LimeGreen);
			dxPos        = ToDx(PositiveColor,          Brushes.LimeGreen);
			dxNeg        = ToDx(NegativeColor,          Brushes.Red);
			dxNeutral    = ToDx(NeutralColor,           Brushes.Goldenrod);
			dxCellBg     = ToDx(CellBackground,         Brushes.Transparent);
			dxText       = ToDx(TextColor,              Brushes.White);
			dxGrid       = ToDx(GridColor,              Brushes.DimGray);
			dxHeaderBg   = ToDx(HeaderBackground,       Brushes.Black);
			dxHeaderText = ToDx(HeaderTextColor,        Brushes.Gainsboro);
			dxThr        = ToDx(ThresholdColor,         Brushes.DarkOrange);
			dxThrPos     = ToDx(PositiveThresholdColor, Brushes.DeepSkyBlue);
			dxThrNeg     = ToDx(NegativeThresholdColor, Brushes.Magenta);
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (ChartBars == null || RenderTarget == null || IsInHitTest || cellFormat == null || showRow == null)
				return;
			if (dxText == null)
				OnRenderTargetChanged();

			List<int> rows = new List<int>();
			for (int r = 0; r < RowCount; r++)
				if (showRow[r]) rows.Add(r);
			if (InvertOrder)
				rows.Reverse();

			int n = rows.Count;
			if (n == 0)
				return;

			float panelX  = ChartPanel.X;
			float panelY  = ChartPanel.Y;
			float panelW  = ChartPanel.W;
			float panelH  = ChartPanel.H;
			float rowH    = panelH / n;
			float cellW   = (float)chartControl.Properties.BarDistance;
			float headerW = ShowHeader ? HeaderWidth : 0f;
			bool  drawTxt = cellW >= MinCellWidthForText && rowH >= 6f;

			SharpDX.Direct2D1.AntialiasMode oldAA = RenderTarget.AntialiasMode;
			RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;

			lock (sync)
			{
				int from = Math.Max(0, ChartBars.FromIndex);
				int to   = Math.Min(ChartBars.ToIndex, stats.Count - 1);

				if (to >= from)
				{
					double[] scale = ComputeScales(rows, from, to);

					for (int i = from; i <= to; i++)
					{
						BarStats s = stats[i];
						if (s == null)
							continue;

						float xc   = chartControl.GetXByBarIndex(ChartBars, i);
						float left = xc - cellW / 2f;
						if (left + cellW < panelX + headerW || left > panelX + panelW)
							continue;

						for (int j = 0; j < n; j++)
						{
							int r = rows[j];
							SharpDX.RectangleF rect = new SharpDX.RectangleF(left, panelY + j * rowH, cellW, rowH);
							RenderTarget.FillRectangle(rect, dxCellBg);

							if (s.HasPrints)
							{
								double v = GetValue(s, i, (Row)r);

								SharpDX.Direct2D1.Brush fill;
								float alpha;
								ResolveFill(r, v, scale[j], out fill, out alpha);
								if (fill != null && alpha > 0f)
								{
									float baseOpacity = fill.Opacity;
									fill.Opacity = baseOpacity * alpha;
									RenderTarget.FillRectangle(rect, fill);
									fill.Opacity = baseOpacity;
								}

								if (drawTxt)
									RenderTarget.DrawText(FormatValue((Row)r, v), cellFormat, rect, dxText,
										SharpDX.Direct2D1.DrawTextOptions.Clip);
							}

							if (GridLineWidth > 0)
								RenderTarget.DrawRectangle(rect, dxGrid, GridLineWidth);
						}
					}
				}
			}

			if (ShowHeader)
			{
				for (int j = 0; j < n; j++)
				{
					SharpDX.RectangleF rect = new SharpDX.RectangleF(panelX, panelY + j * rowH, headerW, rowH);
					RenderTarget.FillRectangle(rect, dxHeaderBg);
					if (GridLineWidth > 0)
						RenderTarget.DrawRectangle(rect, dxGrid, GridLineWidth);
					SharpDX.RectangleF txt = new SharpDX.RectangleF(panelX + 4f, rect.Top, headerW - 6f, rowH);
					RenderTarget.DrawText(RowNames[rows[j]], headerFormat, txt, dxHeaderText,
						SharpDX.Direct2D1.DrawTextOptions.Clip);
				}
			}

			if (!tickReplay)
			{
				SharpDX.RectangleF warn = new SharpDX.RectangleF(panelX + headerW + 6f, panelY + 2f, Math.Max(10f, panelW - headerW - 12f), 16f);
				RenderTarget.DrawText("NT8Stats: Tick Replay is off - historical bars are empty, realtime only.",
					headerFormat, warn, dxThr, SharpDX.Direct2D1.DrawTextOptions.Clip);
			}

			RenderTarget.AntialiasMode = oldAA;
		}

		// Signed rows: scale = k * RMS around zero. Unsigned rows: scale = mean + k * stdev.
		// Intensity = |value| / scale, clamped to [MinOpacity, 1]. Computed over visible bars.
		private double[] ComputeScales(List<int> rows, int from, int to)
		{
			int      n  = rows.Count;
			double[] sc = new double[n];
			double   k  = Math.Max(0.1, StdDevForColoring);

			for (int j = 0; j < n; j++)
			{
				Row  r      = (Row)rows[j];
				bool signed = RowKinds[rows[j]] == Kind.Signed;
				double sum = 0, sumSq = 0;
				int    cnt = 0;

				for (int i = from; i <= to; i++)
				{
					BarStats s = stats[i];
					if (s == null || !s.HasPrints)
						continue;
					double v = GetValue(s, i, r);
					if (double.IsNaN(v) || double.IsInfinity(v))
						continue;
					sum   += v;
					sumSq += v * v;
					cnt++;
				}

				if (cnt == 0) { sc[j] = 0; continue; }

				if (signed)
					sc[j] = k * Math.Sqrt(sumSq / cnt);
				else
				{
					double mean     = sum / cnt;
					double variance = Math.Max(0, sumSq / cnt - mean * mean);
					sc[j] = mean + k * Math.Sqrt(variance);
				}
			}
			return sc;
		}

		private void ResolveFill(int r, double v, double scale, out SharpDX.Direct2D1.Brush fill, out float alpha)
		{
			fill  = null;
			alpha = 0f;
			Kind kind = RowKinds[r];

			// Threshold highlight (absolute value compare; 0 = disabled)
			double thr = thrRow[r];
			if (thr > 0 && !double.IsNaN(v) && Math.Abs(v) >= thr)
			{
				if (kind == Kind.Signed) fill = v >= 0 ? dxThrPos : dxThrNeg;
				else                     fill = dxThr;
				alpha = 1f;
				return;
			}

			if (double.IsNaN(v) || v == 0)
				return;

			switch (kind)
			{
				case Kind.Volume:  fill = dxVolume;  break;
				case Kind.Bid:     fill = dxBid;     break;
				case Kind.Ask:     fill = dxAsk;     break;
				case Kind.Neutral: fill = dxNeutral; break;
				default:           fill = v > 0 ? dxPos : dxNeg; break;
			}

			double intensity = double.IsInfinity(v) || scale <= 0 ? 1.0 : Math.Min(1.0, Math.Abs(v) / scale);
			float  floor     = Math.Max(0, Math.Min(100, MinOpacity)) / 100f;
			alpha = floor + (1f - floor) * (float)intensity;
		}

		private SharpDX.Direct2D1.Brush ToDx(Brush b, Brush fallback)
		{
			return (b ?? fallback).ToDxBrush(RenderTarget);
		}

		private void DisposeDx()
		{
			Release(ref dxVolume);   Release(ref dxBid);      Release(ref dxAsk);   Release(ref dxPos);
			Release(ref dxNeg);      Release(ref dxNeutral);  Release(ref dxCellBg); Release(ref dxText);
			Release(ref dxGrid);     Release(ref dxHeaderBg); Release(ref dxHeaderText);
			Release(ref dxThr);      Release(ref dxThrPos);   Release(ref dxThrNeg);
		}

		private static void Release(ref SharpDX.Direct2D1.Brush b)
		{
			if (b != null) { b.Dispose(); b = null; }
		}

		private void DisposeText()
		{
			if (cellFormat != null)   { cellFormat.Dispose();   cellFormat = null; }
			if (headerFormat != null) { headerFormat.Dispose(); headerFormat = null; }
		}

		private static Brush MakeFrozen(Color c)
		{
			SolidColorBrush b = new SolidColorBrush(c);
			b.Freeze();
			return b;
		}

		private void BuildRowConfig()
		{
			showRow = new bool[]
			{
				ShowTotVol, ShowBidVol, ShowAskVol, ShowDeltaVol, ShowMaxDelta, ShowMinDelta, ShowTotNT, ShowDeltaNT,
				ShowDeltaHL, ShowDeltaPct, ShowDeltaDly, ShowVolPerSec, ShowCotH, ShowCotL, ShowCotBar, ShowTimeSec,
				ShowBarRatio, ShowHighRatio, ShowLowRatio, ShowTotalEffort, ShowDeltaEffort
			};
			thrRow = new double[]
			{
				ThrTotVol, ThrBidVol, ThrAskVol, ThrDeltaVol, ThrMaxDelta, ThrMinDelta, ThrTotNT, ThrDeltaNT,
				ThrDeltaHL, ThrDeltaPct, ThrDeltaDly, ThrVolPerSec, ThrCotH, ThrCotL, ThrCotBar, ThrTimeSec,
				ThrBarRatio, ThrHighRatio, ThrLowRatio, ThrTotalEffort, ThrDeltaEffort
			};
		}
		#endregion

		#region Properties

		// ---------- Public series (for strategies / Market Analyzer) ----------
		[Browsable(false)][XmlIgnore]
		public Series<double> BarDelta { get { return Values[0]; } }

		[Browsable(false)][XmlIgnore]
		public Series<double> CumulativeDelta { get { return Values[1]; } }

		[Browsable(false)][XmlIgnore]
		public Series<double> TotalVol { get { return Values[2]; } }

		// ---------- 1. Data ----------
		[NinjaScriptProperty]
		[Display(Name = "Input Data", Description = "Volume = each print is a trade; AggregateTrades = merge prints with same timestamp and side.", Order = 1, GroupName = "1. Data")]
		public NT8StatsInputData InputData { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Min Filter", Description = "Ignore trades smaller than this size (0 = off).", Order = 2, GroupName = "1. Data")]
		public int MinFilter { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Max Filter", Description = "Ignore trades larger than this size (0 = no max).", Order = 3, GroupName = "1. Data")]
		public int MaxFilter { get; set; }

		[Display(Name = "Reset Delta Dly on session", Order = 4, GroupName = "1. Data")]
		public bool ResetCvdOnSession { get; set; }

		// ---------- 2. Plot ----------
		[Range(0.1, 10)]
		[Display(Name = "Dev. std. for Coloring", Description = "Std-dev multiple at which a cell reaches full color intensity.", Order = 1, GroupName = "2. Plot")]
		public double StdDevForColoring { get; set; }

		[Range(0, 100)]
		[Display(Name = "Min Opacity %", Order = 2, GroupName = "2. Plot")]
		public int MinOpacity { get; set; }

		[Display(Name = "Text Font", Order = 3, GroupName = "2. Plot")]
		public SimpleFont TextFont { get; set; }

		[Display(Name = "Auto Text Format", Description = "Compact numbers (1.2K, 3.4M).", Order = 4, GroupName = "2. Plot")]
		public bool AutoTextFormat { get; set; }

		[Display(Name = "Invert Order", Order = 5, GroupName = "2. Plot")]
		public bool InvertOrder { get; set; }

		[Range(1, 500)]
		[Display(Name = "Min cell width for text (px)", Order = 6, GroupName = "2. Plot")]
		public int MinCellWidthForText { get; set; }

		[Range(0, 5)]
		[Display(Name = "Line Thickness", Order = 7, GroupName = "2. Plot")]
		public int GridLineWidth { get; set; }

		// ---------- 3. Header ----------
		[Display(Name = "Show Header", Order = 1, GroupName = "3. Header")]
		public bool ShowHeader { get; set; }

		[Range(20, 400)]
		[Display(Name = "Header Width (px)", Order = 2, GroupName = "3. Header")]
		public int HeaderWidth { get; set; }

		[Display(Name = "Header Font", Order = 3, GroupName = "3. Header")]
		public SimpleFont HeaderFont { get; set; }

		[XmlIgnore]
		[Display(Name = "Header Background", Order = 4, GroupName = "3. Header")]
		public Brush HeaderBackground { get; set; }
		[Browsable(false)]
		public string HeaderBackgroundSerializable { get { return Serialize.BrushToString(HeaderBackground); } set { HeaderBackground = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Header Text", Order = 5, GroupName = "3. Header")]
		public Brush HeaderTextColor { get; set; }
		[Browsable(false)]
		public string HeaderTextColorSerializable { get { return Serialize.BrushToString(HeaderTextColor); } set { HeaderTextColor = Serialize.StringToBrush(value); } }

		// ---------- 4. Colors ----------
		[XmlIgnore]
		[Display(Name = "Volume (Tot Vol / NT / Vol(s) / Effort)", Order = 1, GroupName = "4. Colors")]
		public Brush VolumeColor { get; set; }
		[Browsable(false)]
		public string VolumeColorSerializable { get { return Serialize.BrushToString(VolumeColor); } set { VolumeColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Bid Volume", Order = 2, GroupName = "4. Colors")]
		public Brush BidColor { get; set; }
		[Browsable(false)]
		public string BidColorSerializable { get { return Serialize.BrushToString(BidColor); } set { BidColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Ask Volume", Order = 3, GroupName = "4. Colors")]
		public Brush AskColor { get; set; }
		[Browsable(false)]
		public string AskColorSerializable { get { return Serialize.BrushToString(AskColor); } set { AskColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Positive Delta", Order = 4, GroupName = "4. Colors")]
		public Brush PositiveColor { get; set; }
		[Browsable(false)]
		public string PositiveColorSerializable { get { return Serialize.BrushToString(PositiveColor); } set { PositiveColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Negative Delta", Order = 5, GroupName = "4. Colors")]
		public Brush NegativeColor { get; set; }
		[Browsable(false)]
		public string NegativeColorSerializable { get { return Serialize.BrushToString(NegativeColor); } set { NegativeColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Neutral (HL / Time / Ratios)", Order = 6, GroupName = "4. Colors")]
		public Brush NeutralColor { get; set; }
		[Browsable(false)]
		public string NeutralColorSerializable { get { return Serialize.BrushToString(NeutralColor); } set { NeutralColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Cell Background", Order = 7, GroupName = "4. Colors")]
		public Brush CellBackground { get; set; }
		[Browsable(false)]
		public string CellBackgroundSerializable { get { return Serialize.BrushToString(CellBackground); } set { CellBackground = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Text", Order = 8, GroupName = "4. Colors")]
		public Brush TextColor { get; set; }
		[Browsable(false)]
		public string TextColorSerializable { get { return Serialize.BrushToString(TextColor); } set { TextColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Grid / Outline", Order = 9, GroupName = "4. Colors")]
		public Brush GridColor { get; set; }
		[Browsable(false)]
		public string GridColorSerializable { get { return Serialize.BrushToString(GridColor); } set { GridColor = Serialize.StringToBrush(value); } }

		// ---------- 5. Thresholds ----------
		[XmlIgnore]
		[Display(Name = "Threshold color (unsigned rows)", Order = 1, GroupName = "5. Thresholds (0 = off)")]
		public Brush ThresholdColor { get; set; }
		[Browsable(false)]
		public string ThresholdColorSerializable { get { return Serialize.BrushToString(ThresholdColor); } set { ThresholdColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Threshold color (positive)", Order = 2, GroupName = "5. Thresholds (0 = off)")]
		public Brush PositiveThresholdColor { get; set; }
		[Browsable(false)]
		public string PositiveThresholdColorSerializable { get { return Serialize.BrushToString(PositiveThresholdColor); } set { PositiveThresholdColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Threshold color (negative)", Order = 3, GroupName = "5. Thresholds (0 = off)")]
		public Brush NegativeThresholdColor { get; set; }
		[Browsable(false)]
		public string NegativeThresholdColorSerializable { get { return Serialize.BrushToString(NegativeThresholdColor); } set { NegativeThresholdColor = Serialize.StringToBrush(value); } }

		[Range(0, double.MaxValue)][Display(Name = "Tot Vol",      Order = 10, GroupName = "5. Thresholds (0 = off)")] public double ThrTotVol      { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "Bid Vol",      Order = 11, GroupName = "5. Thresholds (0 = off)")] public double ThrBidVol      { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "Ask Vol",      Order = 12, GroupName = "5. Thresholds (0 = off)")] public double ThrAskVol      { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "Delta Vol",    Order = 13, GroupName = "5. Thresholds (0 = off)")] public double ThrDeltaVol    { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "Max Delta",    Order = 14, GroupName = "5. Thresholds (0 = off)")] public double ThrMaxDelta    { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "Min Delta",    Order = 15, GroupName = "5. Thresholds (0 = off)")] public double ThrMinDelta    { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "Tot NT",       Order = 16, GroupName = "5. Thresholds (0 = off)")] public double ThrTotNT       { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "Delta NT",     Order = 17, GroupName = "5. Thresholds (0 = off)")] public double ThrDeltaNT     { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "Delta HL",     Order = 18, GroupName = "5. Thresholds (0 = off)")] public double ThrDeltaHL     { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "Delta %",      Order = 19, GroupName = "5. Thresholds (0 = off)")] public double ThrDeltaPct    { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "Delta Dly",    Order = 20, GroupName = "5. Thresholds (0 = off)")] public double ThrDeltaDly    { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "Vol(s)",       Order = 21, GroupName = "5. Thresholds (0 = off)")] public double ThrVolPerSec   { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "COT H",        Order = 22, GroupName = "5. Thresholds (0 = off)")] public double ThrCotH        { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "COT L",        Order = 23, GroupName = "5. Thresholds (0 = off)")] public double ThrCotL        { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "COT Bar",      Order = 24, GroupName = "5. Thresholds (0 = off)")] public double ThrCotBar      { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "Time(s)",      Order = 25, GroupName = "5. Thresholds (0 = off)")] public double ThrTimeSec     { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "Bar Ratio",    Order = 26, GroupName = "5. Thresholds (0 = off)")] public double ThrBarRatio    { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "High Ratio",   Order = 27, GroupName = "5. Thresholds (0 = off)")] public double ThrHighRatio   { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "Low Ratio",    Order = 28, GroupName = "5. Thresholds (0 = off)")] public double ThrLowRatio    { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "Total Effort", Order = 29, GroupName = "5. Thresholds (0 = off)")] public double ThrTotalEffort { get; set; }
		[Range(0, double.MaxValue)][Display(Name = "Delta Effort", Order = 30, GroupName = "5. Thresholds (0 = off)")] public double ThrDeltaEffort { get; set; }

		// ---------- 6. Rows ----------
		[Display(Name = "Tot Vol",      Order = 1,  GroupName = "6. Rows")] public bool ShowTotVol      { get; set; }
		[Display(Name = "Bid Vol",      Order = 2,  GroupName = "6. Rows")] public bool ShowBidVol      { get; set; }
		[Display(Name = "Ask Vol",      Order = 3,  GroupName = "6. Rows")] public bool ShowAskVol      { get; set; }
		[Display(Name = "Delta Vol",    Order = 4,  GroupName = "6. Rows")] public bool ShowDeltaVol    { get; set; }
		[Display(Name = "Max Delta",    Order = 5,  GroupName = "6. Rows")] public bool ShowMaxDelta    { get; set; }
		[Display(Name = "Min Delta",    Order = 6,  GroupName = "6. Rows")] public bool ShowMinDelta    { get; set; }
		[Display(Name = "Tot NT",       Order = 7,  GroupName = "6. Rows")] public bool ShowTotNT       { get; set; }
		[Display(Name = "Delta NT",     Order = 8,  GroupName = "6. Rows")] public bool ShowDeltaNT     { get; set; }
		[Display(Name = "Delta HL",     Order = 9,  GroupName = "6. Rows")] public bool ShowDeltaHL     { get; set; }
		[Display(Name = "Delta %",      Order = 10, GroupName = "6. Rows")] public bool ShowDeltaPct    { get; set; }
		[Display(Name = "Delta Dly",    Order = 11, GroupName = "6. Rows")] public bool ShowDeltaDly    { get; set; }
		[Display(Name = "Vol(s)",       Order = 12, GroupName = "6. Rows")] public bool ShowVolPerSec   { get; set; }
		[Display(Name = "COT H",        Order = 13, GroupName = "6. Rows")] public bool ShowCotH        { get; set; }
		[Display(Name = "COT L",        Order = 14, GroupName = "6. Rows")] public bool ShowCotL        { get; set; }
		[Display(Name = "COT Bar",      Order = 15, GroupName = "6. Rows")] public bool ShowCotBar      { get; set; }
		[Display(Name = "Time(s)",      Order = 16, GroupName = "6. Rows")] public bool ShowTimeSec     { get; set; }
		[Display(Name = "Bar Ratio",    Order = 17, GroupName = "6. Rows")] public bool ShowBarRatio    { get; set; }
		[Display(Name = "High Ratio",   Order = 18, GroupName = "6. Rows")] public bool ShowHighRatio   { get; set; }
		[Display(Name = "Low Ratio",    Order = 19, GroupName = "6. Rows")] public bool ShowLowRatio    { get; set; }
		[Display(Name = "Total Effort", Order = 20, GroupName = "6. Rows")] public bool ShowTotalEffort { get; set; }
		[Display(Name = "Delta Effort", Order = 21, GroupName = "6. Rows")] public bool ShowDeltaEffort { get; set; }

		#endregion
	}
}
