#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows;
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

// =====================================================================================
//  NT8DeltaSwing  -  Order-flow swing delta indicator for NinjaTrader 8
//
//  * Builds price swings (zig-zag) using a reversal size in ticks.
//  * Accumulates aggressive volume delta (Ask-traded minus Bid-traded) for every swing.
//    - With Tick Replay enabled: every trade is classified at bid/ask (tick rule fallback).
//    - Without Tick Replay: delta is estimated per bar from its close location (CLV).
//  * Three signal algorithms, evaluated when a swing is confirmed:
//    1. Rising / Falling Delta - delta changes in one direction over 3 consecutive
//       same-side swings while price keeps extending (exhaustion).
//    2. Pullback - strong-delta impulse followed by a weak-delta, shallow pullback.
//    3. Imbalance (absorption) - a swing whose delta strongly opposes its price direction.
//  * BUY / SELL markers, swing lines, delta labels, live developing-swing label, alerts,
//    and a "Signal" plot (+1 / -1 / 0) for use in strategies / Market Analyzer.
//
//  Best on Range charts (signals confirm as soon as the reversal size is reached).
// =====================================================================================
namespace NinjaTrader.NinjaScript.Indicators
{
	public class NT8DeltaSwing : Indicator
	{
		private class SwingInfo
		{
			public int      Dir;          // +1 up swing, -1 down swing
			public double   StartPrice;
			public double   EndPrice;
			public DateTime StartTime;
			public DateTime EndTime;
			public double   Delta;
			public double   Volume;
			public int      Id;
		}

		// ---- swing engine state ----
		private int      dir;                 // 0 = undetermined, +1 up, -1 down
		private double   startPrice, startCD, startCV;
		private DateTime startTime;
		private double   extPrice,   extCD,   extCV;
		private DateTime extTime;

		private double   initHi = double.MinValue, initLo = double.MaxValue;
		private DateTime initHiT, initLoT;
		private double   initHiCD, initHiCV, initLoCD, initLoCV;

		private double   cumDelta, cumVol;
		private double   lastTradePrice;
		private int      lastTickDir;
		private bool     useTickData;
		private double   swingSize;
		private int      swingCounter;

		private List<SwingInfo> swings;
		private Queue<int>      drawnSwingIds;

		// ---- signal hand-off to the Signal plot ----
		private int      pendingSignal;
		private int      pendingSignalBar = -1;
		private DateTime lastLiveDraw     = DateTime.MinValue;

		private SimpleFont labelFont;
		private SimpleFont signalFont;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                 = "NT8DeltaSwing - swing-based order-flow delta with Rising/Falling Delta, Pullback and Imbalance signals.";
				Name                        = "NT8DeltaSwing";
				Calculate                   = Calculate.OnEachTick;
				IsOverlay                   = true;
				IsAutoScale                 = false;   // hidden 0/±1 Signal plot must not squash the price scale
				DisplayInDataBox            = true;
				DrawOnPricePanel            = true;
				PaintPriceMarkers           = false;
				IsSuspendedWhileInactive    = true;
				ScaleJustification          = ScaleJustification.Right;

				// Swing
				SwingTicks                  = 12;
				// Algorithms
				UseRisingFallingDelta       = true;
				UsePullback                 = true;
				UseImbalance                = true;
				RequirePriceExtension       = true;
				PullbackDeltaRatio          = 0.5;
				MaxRetracePct               = 61.8;
				MinImpulseDelta             = 0;
				ImbalanceRatio              = 0.25;
				MinSwingVolume              = 500;
				// Display
				ShowSwingLines              = true;
				ShowDeltaLabels             = true;
				ShowVolumeInLabel           = false;
				ShowLiveSwing               = true;
				MaxSwingsDrawn              = 100;
				SignalOffsetTicks           = 4;
				LabelFontSize               = 10;
				// Alerts
				EnableAlerts                = true;
				AlertSound                  = "Alert2.wav";
				// Colors
				UpSwingBrush                = Brushes.DodgerBlue;
				DownSwingBrush              = Brushes.OrangeRed;
				PositiveDeltaBrush          = Brushes.LimeGreen;
				NegativeDeltaBrush          = Brushes.Red;
				ImbalanceBackBrush          = Brushes.Gold;
				BuySignalBrush              = Brushes.Lime;
				SellSignalBrush             = Brushes.Magenta;

				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Dot, "Signal");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Dot, "LastSwingDelta");
			}
			else if (State == State.DataLoaded)
			{
				useTickData     = Bars != null && Bars.IsTickReplay;
				swingSize       = SwingTicks * TickSize;
				swings          = new List<SwingInfo>();
				drawnSwingIds   = new Queue<int>();
				labelFont       = new SimpleFont("Arial", LabelFontSize);
				signalFont      = new SimpleFont("Arial", LabelFontSize + 1) { Bold = true };

				if (!useTickData)
					Print(Name + ": Tick Replay is OFF - delta is ESTIMATED from bar data. Enable Tick Replay in the Data Series for true bid/ask delta.");
			}
		}

		#region Data feeds

		// True order-flow path: every trade (historical via Tick Replay, and real-time).
		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (!useTickData || e.MarketDataType != MarketDataType.Last || CurrentBar < 0)
				return;

			double price = e.Price;
			double vol   = e.Volume;
			int    side;

			if (e.Ask > 0 && price >= e.Ask)       side = 1;
			else if (e.Bid > 0 && price <= e.Bid)  side = -1;
			else if (price > lastTradePrice)       side = 1;      // tick rule fallback
			else if (price < lastTradePrice)       side = -1;
			else                                   side = lastTickDir;

			if (side != 0) lastTickDir = side;
			lastTradePrice = price;

			cumDelta += side * vol;
			cumVol   += vol;

			Step(e.Time, price, price);

			if (ShowLiveSwing && State == State.Realtime && (DateTime.Now - lastLiveDraw).TotalMilliseconds > 250)
			{
				DrawLiveSwing();
				lastLiveDraw = DateTime.Now;
			}
		}

		protected override void OnBarUpdate()
		{
			// Estimated path (no Tick Replay): process each bar once it has closed.
			if (!useTickData)
			{
				bool barClosed = Calculate == Calculate.OnBarClose ? CurrentBar >= 0 : (IsFirstTickOfBar && CurrentBar > 0);
				if (barClosed)
				{
					int    ago   = Calculate == Calculate.OnBarClose ? 0 : 1;
					double h     = High[ago], l = Low[ago], c = Close[ago];
					double v     = Volume[ago];
					double range = h - l;
					double clv   = range > 0 ? ((c - l) - (h - c)) / range : 0;   // -1 .. +1

					cumDelta += clv * v;
					cumVol   += v;
					Step(Time[ago], h, l);

					if (ShowLiveSwing && State == State.Realtime)
						DrawLiveSwing();
				}
			}

			Values[0][0] = (pendingSignalBar == CurrentBar) ? pendingSignal : 0;
			Values[1][0] = swings != null && swings.Count > 0 ? swings[swings.Count - 1].Delta : 0;
		}

		#endregion

		#region Swing engine

		private void Step(DateTime t, double hi, double lo)
		{
			if (dir == 0)
			{
				if (hi > initHi) { initHi = hi; initHiT = t; initHiCD = cumDelta; initHiCV = cumVol; }
				if (lo < initLo) { initLo = lo; initLoT = t; initLoCD = cumDelta; initLoCV = cumVol; }

				if (initHi - initLo >= swingSize)
				{
					if (initHiT >= initLoT)   // moved up from the low -> up swing in progress
					{
						dir = 1;
						SetStart(initLo, initLoT, initLoCD, initLoCV);
						SetExtreme(initHi, initHiT);
					}
					else
					{
						dir = -1;
						SetStart(initHi, initHiT, initHiCD, initHiCV);
						SetExtreme(initLo, initLoT);
					}
				}
				return;
			}

			if (dir == 1)
			{
				if (hi >= extPrice)
					SetExtreme(hi, t);
				else if (extPrice - lo >= swingSize)
				{
					CompleteSwing(t, lo);
					dir = -1;
					SetStart(extPrice, extTime, extCD, extCV);
					SetExtreme(lo, t);
				}
			}
			else
			{
				if (lo <= extPrice)
					SetExtreme(lo, t);
				else if (hi - extPrice >= swingSize)
				{
					CompleteSwing(t, hi);
					dir = 1;
					SetStart(extPrice, extTime, extCD, extCV);
					SetExtreme(hi, t);
				}
			}
		}

		private void SetStart(double p, DateTime t, double cd, double cv)
		{
			startPrice = p; startTime = t; startCD = cd; startCV = cv;
		}

		private void SetExtreme(double p, DateTime t)
		{
			extPrice = p; extTime = t; extCD = cumDelta; extCV = cumVol;
		}

		private void CompleteSwing(DateTime confirmTime, double confirmPrice)
		{
			SwingInfo s = new SwingInfo
			{
				Dir        = dir,
				StartPrice = startPrice,
				EndPrice   = extPrice,
				StartTime  = startTime,
				EndTime    = extTime,
				Delta      = extCD - startCD,
				Volume     = extCV - startCV,
				Id         = ++swingCounter
			};

			swings.Add(s);
			if (swings.Count > 500)
				swings.RemoveAt(0);

			DrawSwing(s);
			EvaluateSignals(s, confirmTime, confirmPrice);
		}

		#endregion

		#region Signals

		private void EvaluateSignals(SwingInfo s, DateTime confirmTime, double confirmPrice)
		{
			// A completed DOWN swing means price just reversed up -> BUY candidates.
			// A completed UP swing means price just reversed down -> SELL candidates.
			int    side    = -s.Dir;
			string reasons = string.Empty;

			if (UseRisingFallingDelta && RisingFallingDelta(s))
				reasons += side == 1 ? "RD " : "FD ";

			if (UsePullback && Pullback(s))
				reasons += "PB ";

			if (UseImbalance && Imbalance(s))
				reasons += "IMB ";

			if (reasons.Length == 0)
				return;

			reasons = reasons.Trim();
			pendingSignal    = side;
			pendingSignalBar = CurrentBar;

			string tag = "DDS_sig_" + s.Id;
			if (side == 1)
			{
				double y = s.EndPrice - SignalOffsetTicks * TickSize;
				Draw.ArrowUp(this, tag + "_a", false, confirmTime, y, BuySignalBrush);
				Draw.Text(this, tag + "_t", false, "BUY\n" + reasons, confirmTime, y, -22, BuySignalBrush, signalFont,
					TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			}
			else
			{
				double y = s.EndPrice + SignalOffsetTicks * TickSize;
				Draw.ArrowDown(this, tag + "_a", false, confirmTime, y, SellSignalBrush);
				Draw.Text(this, tag + "_t", false, "SELL\n" + reasons, confirmTime, y, 22, SellSignalBrush, signalFont,
					TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			}

			if (EnableAlerts && State == State.Realtime)
			{
				string msg = string.Format("NT8DeltaSwing {0} {1} [{2}] @ {3}", Instrument.FullName,
					side == 1 ? "BUY" : "SELL", reasons, Instrument.MasterInstrument.FormatPrice(confirmPrice));
				Alert("DDS_" + s.Id, Priority.High, msg,
					NinjaTrader.Core.Globals.InstallDir + @"\sounds\" + AlertSound, 0,
					side == 1 ? Brushes.DarkGreen : Brushes.DarkRed, Brushes.White);
			}
		}

		// Last three same-direction swings (newest last). Null if not enough history.
		private List<SwingInfo> LastSameSide(int sideDir, int count)
		{
			List<SwingInfo> res = new List<SwingInfo>();
			for (int i = swings.Count - 1; i >= 0 && res.Count < count; i--)
				if (swings[i].Dir == sideDir)
					res.Insert(0, swings[i]);
			return res.Count == count ? res : null;
		}

		// Rising Delta (BUY): 3 down swings, selling delta weakening each time (delta rising),
		//                     while price prints lower lows (optional).
		// Falling Delta (SELL): 3 up swings, buying delta weakening each time (delta falling),
		//                     while price prints higher highs (optional).
		private bool RisingFallingDelta(SwingInfo s)
		{
			List<SwingInfo> l = LastSameSide(s.Dir, 3);
			if (l == null) return false;

			if (s.Dir == -1)
			{
				bool deltaRising = l[0].Delta < l[1].Delta && l[1].Delta < l[2].Delta;
				bool lowerLows   = l[0].EndPrice > l[1].EndPrice && l[1].EndPrice > l[2].EndPrice;
				return deltaRising && (!RequirePriceExtension || lowerLows);
			}
			else
			{
				bool deltaFalling = l[0].Delta > l[1].Delta && l[1].Delta > l[2].Delta;
				bool higherHighs  = l[0].EndPrice < l[1].EndPrice && l[1].EndPrice < l[2].EndPrice;
				return deltaFalling && (!RequirePriceExtension || higherHighs);
			}
		}

		// Pullback: previous swing is a strong-delta impulse; this swing is a shallow,
		// low-delta retracement that holds the impulse origin.
		private bool Pullback(SwingInfo s)
		{
			if (swings.Count < 2) return false;
			SwingInfo imp = swings[swings.Count - 2];
			if (imp.Dir != -s.Dir) return false;

			double impSize = Math.Abs(imp.EndPrice - imp.StartPrice);
			double pbSize  = Math.Abs(s.EndPrice - s.StartPrice);
			if (impSize <= 0) return false;

			bool shallow = pbSize / impSize * 100.0 <= MaxRetracePct;

			if (s.Dir == -1)   // down pullback after up impulse -> BUY
			{
				bool strongImpulse = imp.Delta > 0 && imp.Delta >= MinImpulseDelta;
				bool weakPullback  = s.Delta > -PullbackDeltaRatio * imp.Delta;   // selling delta small vs impulse buying
				bool holds         = s.EndPrice > imp.StartPrice;
				return strongImpulse && weakPullback && holds && shallow;
			}
			else               // up pullback after down impulse -> SELL
			{
				bool strongImpulse = imp.Delta < 0 && -imp.Delta >= MinImpulseDelta;
				bool weakPullback  = s.Delta < PullbackDeltaRatio * -imp.Delta;
				bool holds         = s.EndPrice < imp.StartPrice;
				return strongImpulse && weakPullback && holds && shallow;
			}
		}

		// Imbalance / absorption: delta strongly opposite to the swing's price direction.
		// e.g. price fell but aggressive buyers dominated -> sellers absorbed -> BUY.
		private bool Imbalance(SwingInfo s)
		{
			if (s.Volume < MinSwingVolume || s.Volume <= 0) return false;
			double ratio = s.Delta / s.Volume;
			return s.Dir == -1 ? ratio >= ImbalanceRatio : ratio <= -ImbalanceRatio;
		}

		private bool IsImbalanced(SwingInfo s)
		{
			return s.Volume > 0 && s.Volume >= MinSwingVolume && Math.Abs(s.Delta / s.Volume) >= ImbalanceRatio;
		}

		#endregion

		#region Drawing

		private void DrawSwing(SwingInfo s)
		{
			string tag = "DDS_sw_" + s.Id;

			if (ShowSwingLines)
				Draw.Line(this, tag + "_l", false, s.StartTime, s.StartPrice, s.EndTime, s.EndPrice,
					s.Dir == 1 ? UpSwingBrush : DownSwingBrush, DashStyleHelper.Solid, 2);

			if (ShowDeltaLabels)
			{
				string text = FormatDelta(s.Delta);
				if (ShowVolumeInLabel)
					text += "\nV " + FormatNum(s.Volume);

				bool imb = IsImbalanced(s);
				Draw.Text(this, tag + "_d", false, text, s.EndTime, s.EndPrice, s.Dir == 1 ? 14 : -14,
					s.Delta >= 0 ? PositiveDeltaBrush : NegativeDeltaBrush, labelFont, TextAlignment.Center,
					imb ? ImbalanceBackBrush : Brushes.Transparent,
					imb ? ImbalanceBackBrush : Brushes.Transparent, imb ? 25 : 0);
			}

			drawnSwingIds.Enqueue(s.Id);
			while (drawnSwingIds.Count > MaxSwingsDrawn)
			{
				int old = drawnSwingIds.Dequeue();
				RemoveDrawObject("DDS_sw_" + old + "_l");
				RemoveDrawObject("DDS_sw_" + old + "_d");
			}
		}

		private void DrawLiveSwing()
		{
			if (dir == 0) return;

			double liveDelta = cumDelta - startCD;
			double liveVol   = cumVol   - startCV;
			string text      = "(" + FormatDelta(liveDelta) + ")";
			if (ShowVolumeInLabel)
				text += "\nV " + FormatNum(liveVol);

			if (ShowSwingLines)
				Draw.Line(this, "DDS_live_l", false, startTime, startPrice, extTime, extPrice,
					dir == 1 ? UpSwingBrush : DownSwingBrush, DashStyleHelper.Dash, 1);

			if (ShowDeltaLabels)
				Draw.Text(this, "DDS_live_d", false, text, extTime, extPrice, dir == 1 ? 14 : -14,
					liveDelta >= 0 ? PositiveDeltaBrush : NegativeDeltaBrush, labelFont, TextAlignment.Center,
					Brushes.Transparent, Brushes.Transparent, 0);
		}

		private static string FormatDelta(double d)
		{
			return (d >= 0 ? "+" : "-") + FormatNum(Math.Abs(d));
		}

		private static string FormatNum(double v)
		{
			double a = Math.Abs(v);
			if (a >= 1000000) return (v / 1000000.0).ToString("0.##") + "M";
			if (a >= 1000)    return (v / 1000.0).ToString("0.#") + "K";
			return v.ToString("0");
		}

		#endregion

		#region Properties

		// ---------- 1. Swing ----------
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Swing reversal (ticks)", Description = "Ticks price must reverse from the swing extreme to confirm a new swing. On Range charts match or exceed the range size.", Order = 1, GroupName = "1. Swing")]
		public int SwingTicks { get; set; }

		// ---------- 2. Algorithms ----------
		[NinjaScriptProperty]
		[Display(Name = "Rising / Falling Delta", Order = 1, GroupName = "2. Algorithms")]
		public bool UseRisingFallingDelta { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Pullback", Order = 2, GroupName = "2. Algorithms")]
		public bool UsePullback { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Imbalance (absorption)", Order = 3, GroupName = "2. Algorithms")]
		public bool UseImbalance { get; set; }

		[Display(Name = "RFD: require HH / LL", Description = "Rising/Falling Delta only fires when price keeps making new extremes across the 3 swings.", Order = 4, GroupName = "2. Algorithms")]
		public bool RequirePriceExtension { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, 10.0)]
		[Display(Name = "Pullback max delta ratio", Description = "Pullback |delta| must be below this fraction of the impulse delta.", Order = 5, GroupName = "2. Algorithms")]
		public double PullbackDeltaRatio { get; set; }

		[NinjaScriptProperty]
		[Range(1.0, 100.0)]
		[Display(Name = "Pullback max retrace %", Order = 6, GroupName = "2. Algorithms")]
		public double MaxRetracePct { get; set; }

		[Range(0.0, double.MaxValue)]
		[Display(Name = "Pullback min impulse delta", Order = 7, GroupName = "2. Algorithms")]
		public double MinImpulseDelta { get; set; }

		[NinjaScriptProperty]
		[Range(0.01, 1.0)]
		[Display(Name = "Imbalance ratio |delta|/volume", Order = 8, GroupName = "2. Algorithms")]
		public double ImbalanceRatio { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Imbalance min swing volume", Order = 9, GroupName = "2. Algorithms")]
		public double MinSwingVolume { get; set; }

		// ---------- 3. Display ----------
		[Display(Name = "Show swing lines", Order = 1, GroupName = "3. Display")]
		public bool ShowSwingLines { get; set; }

		[Display(Name = "Show delta labels", Order = 2, GroupName = "3. Display")]
		public bool ShowDeltaLabels { get; set; }

		[Display(Name = "Show volume in label", Order = 3, GroupName = "3. Display")]
		public bool ShowVolumeInLabel { get; set; }

		[Display(Name = "Show live (developing) swing", Order = 4, GroupName = "3. Display")]
		public bool ShowLiveSwing { get; set; }

		[Range(1, 5000)]
		[Display(Name = "Max swings drawn", Order = 5, GroupName = "3. Display")]
		public int MaxSwingsDrawn { get; set; }

		[Range(0, 100)]
		[Display(Name = "Signal offset (ticks)", Order = 6, GroupName = "3. Display")]
		public int SignalOffsetTicks { get; set; }

		[Range(6, 30)]
		[Display(Name = "Label font size", Order = 7, GroupName = "3. Display")]
		public int LabelFontSize { get; set; }

		// ---------- 4. Alerts ----------
		[Display(Name = "Enable alerts", Order = 1, GroupName = "4. Alerts")]
		public bool EnableAlerts { get; set; }

		[Display(Name = "Alert sound file", Description = "File in NinjaTrader 8\\sounds", Order = 2, GroupName = "4. Alerts")]
		public string AlertSound { get; set; }

		// ---------- 5. Colors ----------
		[XmlIgnore]
		[Display(Name = "Up swing", Order = 1, GroupName = "5. Colors")]
		public Brush UpSwingBrush { get; set; }
		[Browsable(false)]
		public string UpSwingBrushSerializable { get { return Serialize.BrushToString(UpSwingBrush); } set { UpSwingBrush = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Down swing", Order = 2, GroupName = "5. Colors")]
		public Brush DownSwingBrush { get; set; }
		[Browsable(false)]
		public string DownSwingBrushSerializable { get { return Serialize.BrushToString(DownSwingBrush); } set { DownSwingBrush = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Positive delta", Order = 3, GroupName = "5. Colors")]
		public Brush PositiveDeltaBrush { get; set; }
		[Browsable(false)]
		public string PositiveDeltaBrushSerializable { get { return Serialize.BrushToString(PositiveDeltaBrush); } set { PositiveDeltaBrush = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Negative delta", Order = 4, GroupName = "5. Colors")]
		public Brush NegativeDeltaBrush { get; set; }
		[Browsable(false)]
		public string NegativeDeltaBrushSerializable { get { return Serialize.BrushToString(NegativeDeltaBrush); } set { NegativeDeltaBrush = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Imbalance highlight", Order = 5, GroupName = "5. Colors")]
		public Brush ImbalanceBackBrush { get; set; }
		[Browsable(false)]
		public string ImbalanceBackBrushSerializable { get { return Serialize.BrushToString(ImbalanceBackBrush); } set { ImbalanceBackBrush = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "BUY signal", Order = 6, GroupName = "5. Colors")]
		public Brush BuySignalBrush { get; set; }
		[Browsable(false)]
		public string BuySignalBrushSerializable { get { return Serialize.BrushToString(BuySignalBrush); } set { BuySignalBrush = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "SELL signal", Order = 7, GroupName = "5. Colors")]
		public Brush SellSignalBrush { get; set; }
		[Browsable(false)]
		public string SellSignalBrushSerializable { get { return Serialize.BrushToString(SellSignalBrush); } set { SellSignalBrush = Serialize.StringToBrush(value); } }

		// ---------- Output series ----------
		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Signal { get { return Values[0]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> LastSwingDelta { get { return Values[1]; } }

		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private NT8DeltaSwing[] cacheNT8DeltaSwing;
		public NT8DeltaSwing NT8DeltaSwing(int swingTicks, bool useRisingFallingDelta, bool usePullback, bool useImbalance, double pullbackDeltaRatio, double maxRetracePct, double imbalanceRatio, double minSwingVolume)
		{
			return NT8DeltaSwing(Input, swingTicks, useRisingFallingDelta, usePullback, useImbalance, pullbackDeltaRatio, maxRetracePct, imbalanceRatio, minSwingVolume);
		}

		public NT8DeltaSwing NT8DeltaSwing(ISeries<double> input, int swingTicks, bool useRisingFallingDelta, bool usePullback, bool useImbalance, double pullbackDeltaRatio, double maxRetracePct, double imbalanceRatio, double minSwingVolume)
		{
			if (cacheNT8DeltaSwing != null)
				for (int idx = 0; idx < cacheNT8DeltaSwing.Length; idx++)
					if (cacheNT8DeltaSwing[idx] != null && cacheNT8DeltaSwing[idx].SwingTicks == swingTicks && cacheNT8DeltaSwing[idx].UseRisingFallingDelta == useRisingFallingDelta && cacheNT8DeltaSwing[idx].UsePullback == usePullback && cacheNT8DeltaSwing[idx].UseImbalance == useImbalance && cacheNT8DeltaSwing[idx].PullbackDeltaRatio == pullbackDeltaRatio && cacheNT8DeltaSwing[idx].MaxRetracePct == maxRetracePct && cacheNT8DeltaSwing[idx].ImbalanceRatio == imbalanceRatio && cacheNT8DeltaSwing[idx].MinSwingVolume == minSwingVolume && cacheNT8DeltaSwing[idx].EqualsInput(input))
						return cacheNT8DeltaSwing[idx];
			return CacheIndicator<NT8DeltaSwing>(new NT8DeltaSwing(){ SwingTicks = swingTicks, UseRisingFallingDelta = useRisingFallingDelta, UsePullback = usePullback, UseImbalance = useImbalance, PullbackDeltaRatio = pullbackDeltaRatio, MaxRetracePct = maxRetracePct, ImbalanceRatio = imbalanceRatio, MinSwingVolume = minSwingVolume }, input, ref cacheNT8DeltaSwing);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.NT8DeltaSwing NT8DeltaSwing(int swingTicks, bool useRisingFallingDelta, bool usePullback, bool useImbalance, double pullbackDeltaRatio, double maxRetracePct, double imbalanceRatio, double minSwingVolume)
		{
			return indicator.NT8DeltaSwing(Input, swingTicks, useRisingFallingDelta, usePullback, useImbalance, pullbackDeltaRatio, maxRetracePct, imbalanceRatio, minSwingVolume);
		}

		public Indicators.NT8DeltaSwing NT8DeltaSwing(ISeries<double> input , int swingTicks, bool useRisingFallingDelta, bool usePullback, bool useImbalance, double pullbackDeltaRatio, double maxRetracePct, double imbalanceRatio, double minSwingVolume)
		{
			return indicator.NT8DeltaSwing(input, swingTicks, useRisingFallingDelta, usePullback, useImbalance, pullbackDeltaRatio, maxRetracePct, imbalanceRatio, minSwingVolume);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.NT8DeltaSwing NT8DeltaSwing(int swingTicks, bool useRisingFallingDelta, bool usePullback, bool useImbalance, double pullbackDeltaRatio, double maxRetracePct, double imbalanceRatio, double minSwingVolume)
		{
			return indicator.NT8DeltaSwing(Input, swingTicks, useRisingFallingDelta, usePullback, useImbalance, pullbackDeltaRatio, maxRetracePct, imbalanceRatio, minSwingVolume);
		}

		public Indicators.NT8DeltaSwing NT8DeltaSwing(ISeries<double> input , int swingTicks, bool useRisingFallingDelta, bool usePullback, bool useImbalance, double pullbackDeltaRatio, double maxRetracePct, double imbalanceRatio, double minSwingVolume)
		{
			return indicator.NT8DeltaSwing(input, swingTicks, useRisingFallingDelta, usePullback, useImbalance, pullbackDeltaRatio, maxRetracePct, imbalanceRatio, minSwingVolume);
		}
	}
}

#endregion
