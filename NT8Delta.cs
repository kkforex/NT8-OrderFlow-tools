//
// NT8Delta.cs
// NinjaTrader 8 Indicator inspired by NT8Charts NT8 Delta
//
// Features matching NT8Charts style:
// - Input Data selection (Volume / Aggregate / Trades / Order - simplified)
// - Delta Mode: Classic or Multi-Range
// - 4 configurable Ranges (Enable, Min, Max, Bid/Ask colors)
// - Threshold horizontal lines
// - Markers for extreme mixed/absorption conditions
// - Subgraph thickness & shadow colors
//
// Requires Tick Replay for accurate historical Bid/Ask classification.
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

namespace NinjaTrader.NinjaScript.Indicators
{
	public class NT8Delta : Indicator
	{
		private double buyVolume;
		private double sellVolume;
		private int lastBar = -1;
		private double barMaxDelta;
		private double barMinDelta;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"NT8 Delta - Multi-range filtered Delta Bars (NT8Charts style).";
				Name										= "NT8Delta";
				Calculate									= Calculate.OnEachTick;
				IsOverlay									= false;
				DisplayInDataBox							= true;
				DrawOnPricePanel							= false;
				PaintPriceMarkers							= true;
				ScaleJustification							= ScaleJustification.Right;
				IsSuspendedWhileInactive					= true;

				// === General / Input ===
				InputData									= InputDataEnum.Volume;
				DeltaMode									= DeltaModeEnum.MultiRange;
				LineThickness								= 2;

				// === Thresholds ===
				ShowThresholds								= true;
				PositiveThreshold							= 500;
				NegativeThreshold							= -500;
				ThresholdBrush								= Brushes.DimGray;
				ThresholdDash								= DashStyleHelper.Dash;

				// === Markers ===
				ShowMarkers									= false;
				MarkerThreshold								= 400;
				MarkerBrush									= Brushes.Gold;

				// === Shadows ===
				ShowShadows									= true;
				ShadowPosBrush								= Brushes.DarkBlue;
				ShadowNegBrush								= Brushes.DarkRed;

				// === Range 1 ===
				Range1Enabled								= true;
				Range1Min									= 0;
				Range1Max									= 100;
				Range1BidBrush								= Brushes.LightGreen;
				Range1AskBrush								= Brushes.LightCoral;

				// === Range 2 ===
				Range2Enabled								= true;
				Range2Min									= 100;
				Range2Max									= 300;
				Range2BidBrush								= Brushes.Green;
				Range2AskBrush								= Brushes.Salmon;

				// === Range 3 ===
				Range3Enabled								= true;
				Range3Min									= 300;
				Range3Max									= 600;
				Range3BidBrush								= Brushes.DarkGreen;
				Range3AskBrush								= Brushes.Red;

				// === Range 4 ===
				Range4Enabled								= true;
				Range4Min									= 600;
				Range4Max									= 0;
				Range4BidBrush								= Brushes.DodgerBlue;
				Range4AskBrush								= Brushes.DeepPink;

				// Plots
				AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Bar, "Delta");
				AddPlot(new Stroke(Brushes.DarkBlue, 1), PlotStyle.Line, "MaxPos");
				AddPlot(new Stroke(Brushes.DarkRed, 1), PlotStyle.Line, "MaxNeg");
			}
			else if (State == State.DataLoaded)
			{
				buyVolume = 0;
				sellVolume = 0;
				lastBar = -1;
				barMaxDelta = 0;
				barMinDelta = 0;

				// Apply thickness
				Plots[0].Width = LineThickness;
				Plots[1].Width = 1;
				Plots[2].Width = 1;
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 1)
				return;

			if (CurrentBar != lastBar)
			{
				// Finalize previous bar extremes if needed
				buyVolume = 0;
				sellVolume = 0;
				barMaxDelta = 0;
				barMinDelta = 0;
				lastBar = CurrentBar;
			}

			double delta = buyVolume - sellVolume;

			// Track intra-bar extremes
			if (delta > barMaxDelta) barMaxDelta = delta;
			if (delta < barMinDelta) barMinDelta = delta;

			Values[0][0] = delta;

			if (ShowShadows)
			{
				Values[1][0] = barMaxDelta;
				Values[2][0] = barMinDelta;
				PlotBrushes[1][0] = ShadowPosBrush;
				PlotBrushes[2][0] = ShadowNegBrush;
			}
			else
			{
				Values[1][0] = double.NaN;
				Values[2][0] = double.NaN;
			}

			// Color main delta bar
			Brush brush = Brushes.Gray;

			if (DeltaMode == DeltaModeEnum.Classic)
			{
				brush = delta >= 0 ? Brushes.DodgerBlue : Brushes.Crimson;
			}
			else
			{
				double abs = Math.Abs(delta);
				bool isPositive = delta >= 0;

				if (Range1Enabled && abs >= Range1Min && (Range1Max == 0 || abs <= Range1Max))
					brush = isPositive ? Range1BidBrush : Range1AskBrush;
				else if (Range2Enabled && abs >= Range2Min && (Range2Max == 0 || abs <= Range2Max))
					brush = isPositive ? Range2BidBrush : Range2AskBrush;
				else if (Range3Enabled && abs >= Range3Min && (Range3Max == 0 || abs <= Range3Max))
					brush = isPositive ? Range3BidBrush : Range3AskBrush;
				else if (Range4Enabled && abs >= Range4Min && (Range4Max == 0 || abs <= Range4Max))
					brush = isPositive ? Range4BidBrush : Range4AskBrush;
			}

			PlotBrushes[0][0] = brush;

			// Threshold lines (drawn on last bar only to avoid duplicates)
			if (ShowThresholds && CurrentBar == Count - 1)
			{
				Draw.HorizontalLine(this, "PosThresh", PositiveThreshold, ThresholdBrush, ThresholdDash, 1);
				Draw.HorizontalLine(this, "NegThresh", NegativeThreshold, ThresholdBrush, ThresholdDash, 1);
			}

			// Markers – highlight when both extremes are large (absorption / mixed aggression)
			if (ShowMarkers && Math.Abs(barMaxDelta) >= MarkerThreshold && Math.Abs(barMinDelta) >= MarkerThreshold)
			{
				Draw.Diamond(this, "Marker" + CurrentBar, false, 0, delta, MarkerBrush);
			}
		}

		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (e.MarketDataType != MarketDataType.Last || CurrentBar < 0)
				return;

			// Simple Bid/Ask classification (Volume mode)
			// Note: Aggregate Trades / Trades / Order modes would require additional size filtering
			if (e.Price >= e.Ask)
				buyVolume += e.Volume;
			else if (e.Price <= e.Bid)
				sellVolume += e.Volume;
		}

		#region Properties

		// ========== 1. General ==========
		[NinjaScriptProperty]
		[Display(Name = "Input Data", Description = "Volume = total traded volume (primary). Aggregate/Trades/Order are simplified to Volume in this version.", Order = 1, GroupName = "1. General")]
		public InputDataEnum InputData { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Delta Mode", Description = "Classic = unfiltered Delta Bars. Multi-Range = color by magnitude ranges.", Order = 2, GroupName = "1. General")]
		public DeltaModeEnum DeltaMode { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name = "Line Thickness", Order = 3, GroupName = "1. General")]
		public int LineThickness { get; set; }

		// ========== 2. Thresholds (Level Settings) ==========
		[NinjaScriptProperty]
		[Display(Name = "Show Thresholds", Order = 1, GroupName = "2. Thresholds")]
		public bool ShowThresholds { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Positive Threshold", Order = 2, GroupName = "2. Thresholds")]
		public double PositiveThreshold { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Negative Threshold", Order = 3, GroupName = "2. Thresholds")]
		public double NegativeThreshold { get; set; }

		[XmlIgnore]
		[Display(Name = "Threshold Color", Order = 4, GroupName = "2. Thresholds")]
		public Brush ThresholdBrush { get; set; }

		[Browsable(false)]
		public string ThresholdBrushSerializable
		{
			get { return Serialize.BrushToString(ThresholdBrush); }
			set { ThresholdBrush = Serialize.StringToBrush(value); }
		}

		[NinjaScriptProperty]
		[Display(Name = "Threshold Style", Order = 5, GroupName = "2. Thresholds")]
		public DashStyleHelper ThresholdDash { get; set; }

		// ========== 3. Markers ==========
		[NinjaScriptProperty]
		[Display(Name = "Show Markers", Description = "Vertical markers when both max positive and max negative delta extremes reach the threshold (absorption / mixed range).", Order = 1, GroupName = "3. Markers")]
		public bool ShowMarkers { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Marker Threshold", Order = 2, GroupName = "3. Markers")]
		public double MarkerThreshold { get; set; }

		[XmlIgnore]
		[Display(Name = "Marker Color", Order = 3, GroupName = "3. Markers")]
		public Brush MarkerBrush { get; set; }

		[Browsable(false)]
		public string MarkerBrushSerializable
		{
			get { return Serialize.BrushToString(MarkerBrush); }
			set { MarkerBrush = Serialize.StringToBrush(value); }
		}

		// ========== 4. Shadows ==========
		[NinjaScriptProperty]
		[Display(Name = "Show Shadows", Description = "Plot maximum positive and negative delta extremes.", Order = 1, GroupName = "4. Shadows")]
		public bool ShowShadows { get; set; }

		[XmlIgnore]
		[Display(Name = "Positive Shadow Color", Order = 2, GroupName = "4. Shadows")]
		public Brush ShadowPosBrush { get; set; }

		[Browsable(false)]
		public string ShadowPosBrushSerializable
		{
			get { return Serialize.BrushToString(ShadowPosBrush); }
			set { ShadowPosBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Negative Shadow Color", Order = 3, GroupName = "4. Shadows")]
		public Brush ShadowNegBrush { get; set; }

		[Browsable(false)]
		public string ShadowNegBrushSerializable
		{
			get { return Serialize.BrushToString(ShadowNegBrush); }
			set { ShadowNegBrush = Serialize.StringToBrush(value); }
		}

		// ========== 5. Range 1 ==========
		[NinjaScriptProperty]
		[Display(Name = "Enabled", Order = 1, GroupName = "5. Range 1")]
		public bool Range1Enabled { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Min", Order = 2, GroupName = "5. Range 1")]
		public double Range1Min { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Max (0 = no limit)", Order = 3, GroupName = "5. Range 1")]
		public double Range1Max { get; set; }

		[XmlIgnore]
		[Display(Name = "Bid Color (Positive)", Order = 4, GroupName = "5. Range 1")]
		public Brush Range1BidBrush { get; set; }

		[Browsable(false)]
		public string Range1BidBrushSerializable
		{
			get { return Serialize.BrushToString(Range1BidBrush); }
			set { Range1BidBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Ask Color (Negative)", Order = 5, GroupName = "5. Range 1")]
		public Brush Range1AskBrush { get; set; }

		[Browsable(false)]
		public string Range1AskBrushSerializable
		{
			get { return Serialize.BrushToString(Range1AskBrush); }
			set { Range1AskBrush = Serialize.StringToBrush(value); }
		}

		// ========== 6. Range 2 ==========
		[NinjaScriptProperty]
		[Display(Name = "Enabled", Order = 1, GroupName = "6. Range 2")]
		public bool Range2Enabled { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Min", Order = 2, GroupName = "6. Range 2")]
		public double Range2Min { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Max (0 = no limit)", Order = 3, GroupName = "6. Range 2")]
		public double Range2Max { get; set; }

		[XmlIgnore]
		[Display(Name = "Bid Color (Positive)", Order = 4, GroupName = "6. Range 2")]
		public Brush Range2BidBrush { get; set; }

		[Browsable(false)]
		public string Range2BidBrushSerializable
		{
			get { return Serialize.BrushToString(Range2BidBrush); }
			set { Range2BidBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Ask Color (Negative)", Order = 5, GroupName = "6. Range 2")]
		public Brush Range2AskBrush { get; set; }

		[Browsable(false)]
		public string Range2AskBrushSerializable
		{
			get { return Serialize.BrushToString(Range2AskBrush); }
			set { Range2AskBrush = Serialize.StringToBrush(value); }
		}

		// ========== 7. Range 3 ==========
		[NinjaScriptProperty]
		[Display(Name = "Enabled", Order = 1, GroupName = "7. Range 3")]
		public bool Range3Enabled { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Min", Order = 2, GroupName = "7. Range 3")]
		public double Range3Min { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Max (0 = no limit)", Order = 3, GroupName = "7. Range 3")]
		public double Range3Max { get; set; }

		[XmlIgnore]
		[Display(Name = "Bid Color (Positive)", Order = 4, GroupName = "7. Range 3")]
		public Brush Range3BidBrush { get; set; }

		[Browsable(false)]
		public string Range3BidBrushSerializable
		{
			get { return Serialize.BrushToString(Range3BidBrush); }
			set { Range3BidBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Ask Color (Negative)", Order = 5, GroupName = "7. Range 3")]
		public Brush Range3AskBrush { get; set; }

		[Browsable(false)]
		public string Range3AskBrushSerializable
		{
			get { return Serialize.BrushToString(Range3AskBrush); }
			set { Range3AskBrush = Serialize.StringToBrush(value); }
		}

		// ========== 8. Range 4 ==========
		[NinjaScriptProperty]
		[Display(Name = "Enabled", Order = 1, GroupName = "8. Range 4")]
		public bool Range4Enabled { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Min", Order = 2, GroupName = "8. Range 4")]
		public double Range4Min { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Max (0 = no limit)", Order = 3, GroupName = "8. Range 4")]
		public double Range4Max { get; set; }

		[XmlIgnore]
		[Display(Name = "Bid Color (Positive)", Order = 4, GroupName = "8. Range 4")]
		public Brush Range4BidBrush { get; set; }

		[Browsable(false)]
		public string Range4BidBrushSerializable
		{
			get { return Serialize.BrushToString(Range4BidBrush); }
			set { Range4BidBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Ask Color (Negative)", Order = 5, GroupName = "8. Range 4")]
		public Brush Range4AskBrush { get; set; }

		[Browsable(false)]
		public string Range4AskBrushSerializable
		{
			get { return Serialize.BrushToString(Range4AskBrush); }
			set { Range4AskBrush = Serialize.StringToBrush(value); }
		}

		#endregion
	}

	public enum DeltaModeEnum
	{
		Classic,
		MultiRange
	}

	public enum InputDataEnum
	{
		Volume,
		AggregateTrades,
		Trades,
		Order
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private NT8Delta[] cacheNT8Delta;
		public NT8Delta NT8Delta()
		{
			return NT8Delta(Input);
		}

		public NT8Delta NT8Delta(ISeries<double> input)
		{
			if (cacheNT8Delta != null)
				for (int idx = 0; idx < cacheNT8Delta.Length; idx++)
					if (cacheNT8Delta[idx] != null && cacheNT8Delta[idx].EqualsInput(input))
						return cacheNT8Delta[idx];
			return CacheIndicator<NT8Delta>(new NT8Delta(), input, ref cacheNT8Delta);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.NT8Delta NT8Delta()
		{
			return indicator.NT8Delta(Input);
		}

		public Indicators.NT8Delta NT8Delta(ISeries<double> input)
		{
			return indicator.NT8Delta(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.NT8Delta NT8Delta()
		{
			return indicator.NT8Delta(Input);
		}

		public Indicators.NT8Delta NT8Delta(ISeries<double> input)
		{
			return indicator.NT8Delta(input);
		}
	}
}

#endregion
