//
// SpreadCandlesticks — NinjaTrader 8 port
// Original NT7 version: SpreadCandlesticks (2012). Ported to NinjaScript 8 (OnStateChange / SharpDX OnRender).
//
// Plots a synthetic spread  Qty1 * Instrument1 + Qty2 * Instrument2  as candlesticks in its own panel.
// Instrument1 = the chart's primary series. Instrument2 = Symbol2 (e.g. "NQ 12-26", "SPY", "EURUSD").
//

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// This namespace holds Indicators in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Indicators
{
	public class SpreadCandlesticks : Indicator
	{
		#region Variables
		private string	symbol2					= string.Empty;
		private bool	secondSeriesAdded		= false;
		private bool	warningDrawn			= false;

		// SharpDX device resources (recreated whenever the render target changes)
		private SharpDX.Direct2D1.Brush dxBrushUp;
		private SharpDX.Direct2D1.Brush dxBrushDown;
		private SharpDX.Direct2D1.Brush dxBrushShadow;
		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "Spread of two instruments (Qty1 * Inst1 + Qty2 * Inst2) drawn as candlesticks.";
				Name						= "SpreadCandlesticks";
				Calculate					= Calculate.OnPriceChange;
				IsOverlay					= false;
				DisplayInDataBox			= true;
				DrawOnPricePanel			= false;
				PaintPriceMarkers			= false;
				IsAutoScale					= true;
				ScaleJustification			= NinjaTrader.Gui.Chart.ScaleJustification.Right;
				IsSuspendedWhileInactive	= true;
				BarsRequiredToPlot			= 0;		// OK to plot on first bar

				Qty1						= 1;
				Symbol2						= string.Empty;
				Qty2						= -1;

				BarBrushUp					= Brushes.Lime;
				BarBrushDown				= Brushes.Red;
				ShadowBrush					= Brushes.Black;
				ShadowWidth					= 1;

				// Plots hold the OHLC values (data box, price marker, access from other scripts).
				// They are transparent because the candles are drawn in OnRender.
				AddPlot(Brushes.Transparent, "SpreadOpen");
				AddPlot(Brushes.Transparent, "SpreadHigh");
				AddPlot(Brushes.Transparent, "SpreadLow");
				AddPlot(Brushes.Transparent, "SpreadClose");
			}
			else if (State == State.Configure)
			{
				// NT8 cannot read the chart's other data series during Configure, so Symbol2 must be set explicitly.
				// Second series uses the same bars period as the primary (works for time, tick, range, etc.).
				secondSeriesAdded = false;
				if (!string.IsNullOrWhiteSpace(Symbol2))
				{
					if (Instrument.GetInstrument(Symbol2) != null)
					{
						AddDataSeries(Symbol2);
						secondSeriesAdded = true;
					}
					else
						Log("SpreadCandlesticks: instrument '" + Symbol2 + "' not found. Check the name (e.g. 'ES 12-26').", LogLevel.Error);
				}
			}
			else if (State == State.Terminated)
			{
				DisposeDxResources();
			}
		}

		protected override void OnBarUpdate()
		{
			if (!secondSeriesAdded || BarsArray.Length < 2)
			{
				if (!warningDrawn && BarsInProgress == 0)
				{
					Draw.TextFixed(this, "SpreadCandlesticksWarning",
						"SpreadCandlesticks: set 'Symbol 2' to a valid instrument (e.g. NQ 12-26).",
						TextPosition.Center);
					warningDrawn = true;
				}
				return;
			}

			// Plot values are set from the primary series only; both series need at least one bar.
			if (BarsInProgress != 0 || CurrentBars[0] < 0 || CurrentBars[1] < 0)
				return;

			double o1 = Opens[0][0],  h1 = Highs[0][0],  l1 = Lows[0][0],  c1 = Closes[0][0];
			double o2 = Opens[1][0],  h2 = Highs[1][0],  l2 = Lows[1][0],  c2 = Closes[1][0];

			SpreadOpen[0]	= Qty1 * o1 + Qty2 * o2;
			SpreadClose[0]	= Qty1 * c1 + Qty2 * c2;

			// Widest possible range: each leg contributes its extreme in the direction of its quantity sign.
			// (Same logic as the NT7 original — legs' highs/lows need not occur at the same moment.)
			SpreadHigh[0]	= Math.Max(Qty1 * h1, Qty1 * l1) + Math.Max(Qty2 * h2, Qty2 * l2);
			SpreadLow[0]	= Math.Min(Qty1 * h1, Qty1 * l1) + Math.Min(Qty2 * h2, Qty2 * l2);
		}

		#region Rendering
		public override void OnRenderTargetChanged()
		{
			DisposeDxResources();

			if (RenderTarget == null)
				return;

			dxBrushUp		= BarBrushUp.ToDxBrush(RenderTarget);
			dxBrushDown		= BarBrushDown.ToDxBrush(RenderTarget);
			dxBrushShadow	= ShadowBrush.ToDxBrush(RenderTarget);
		}

		private void DisposeDxResources()
		{
			if (dxBrushUp != null)		{ dxBrushUp.Dispose();		dxBrushUp = null; }
			if (dxBrushDown != null)	{ dxBrushDown.Dispose();	dxBrushDown = null; }
			if (dxBrushShadow != null)	{ dxBrushShadow.Dispose();	dxBrushShadow = null; }
		}

		public override void OnCalculateMinMax()
		{
			if (ChartBars == null)
				return;

			double min = double.MaxValue;
			double max = double.MinValue;

			for (int idx = ChartBars.FromIndex; idx <= ChartBars.ToIndex; idx++)
			{
				if (!SpreadHigh.IsValidDataPointAt(idx) || !SpreadLow.IsValidDataPointAt(idx))
					continue;

				max = Math.Max(max, SpreadHigh.GetValueAt(idx));
				min = Math.Min(min, SpreadLow.GetValueAt(idx));
			}

			if (max >= min)
			{
				MinValue = min;
				MaxValue = max;
			}
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (Bars == null || ChartBars == null || RenderTarget == null || !secondSeriesAdded)
				return;
			if (dxBrushUp == null || dxBrushDown == null || dxBrushShadow == null)
				return;

			SharpDX.Direct2D1.AntialiasMode oldMode = RenderTarget.AntialiasMode;
			RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;

			float barWidth	= Math.Max(3f, chartControl.GetBarPaintWidth(ChartBars));
			float halfWidth	= barWidth / 2f;
			float penWidth	= ShadowWidth;

			for (int idx = ChartBars.FromIndex; idx <= ChartBars.ToIndex; idx++)
			{
				if (!SpreadOpen.IsValidDataPointAt(idx) || !SpreadHigh.IsValidDataPointAt(idx)
					|| !SpreadLow.IsValidDataPointAt(idx) || !SpreadClose.IsValidDataPointAt(idx))
					continue;

				float x		= chartControl.GetXByBarIndex(ChartBars, idx);
				float yO	= chartScale.GetYByValue(SpreadOpen.GetValueAt(idx));
				float yH	= chartScale.GetYByValue(SpreadHigh.GetValueAt(idx));
				float yL	= chartScale.GetYByValue(SpreadLow.GetValueAt(idx));
				float yC	= chartScale.GetYByValue(SpreadClose.GetValueAt(idx));

				// Wick
				RenderTarget.DrawLine(new SharpDX.Vector2(x, yH), new SharpDX.Vector2(x, yL), dxBrushShadow, penWidth);

				if (Math.Abs(yC - yO) < 1f)
				{
					// Doji: horizontal line
					RenderTarget.DrawLine(new SharpDX.Vector2(x - halfWidth, yO), new SharpDX.Vector2(x + halfWidth, yO), dxBrushShadow, penWidth);
				}
				else
				{
					float top		= Math.Min(yO, yC);
					float height	= Math.Abs(yC - yO);
					SharpDX.RectangleF body = new SharpDX.RectangleF(x - halfWidth, top, barWidth, height);

					// Screen Y grows downward: close below open on screen = down bar
					RenderTarget.FillRectangle(body, yC > yO ? dxBrushDown : dxBrushUp);
					RenderTarget.DrawRectangle(body, dxBrushShadow, penWidth);
				}
			}

			RenderTarget.AntialiasMode = oldMode;
		}
		#endregion

		#region Properties
		[NinjaScriptProperty]
		[Display(Name = "Quantity 1", Description = "Quantity / weight for the chart's primary instrument", Order = 1, GroupName = "Parameters")]
		public double Qty1
		{ get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Symbol 2", Description = "Second instrument full name, e.g. SPY or ES 12-26", Order = 2, GroupName = "Parameters")]
		public string Symbol2
		{
			get { return symbol2; }
			set { symbol2 = value == null ? string.Empty : value.Trim().ToUpper(); }
		}

		[NinjaScriptProperty]
		[Display(Name = "Quantity 2", Description = "Quantity / weight for Symbol 2 (negative = short leg)", Order = 3, GroupName = "Parameters")]
		public double Qty2
		{ get; set; }

		[XmlIgnore]
		[Display(Name = "Up color", Description = "Color of up bars", Order = 1, GroupName = "Visual")]
		public Brush BarBrushUp
		{ get; set; }

		[Browsable(false)]
		public string BarBrushUpSerializable
		{
			get { return Serialize.BrushToString(BarBrushUp); }
			set { BarBrushUp = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Down color", Description = "Color of down bars", Order = 2, GroupName = "Visual")]
		public Brush BarBrushDown
		{ get; set; }

		[Browsable(false)]
		public string BarBrushDownSerializable
		{
			get { return Serialize.BrushToString(BarBrushDown); }
			set { BarBrushDown = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Shadow color", Description = "Color of wick and body outline", Order = 3, GroupName = "Visual")]
		public Brush ShadowBrush
		{ get; set; }

		[Browsable(false)]
		public string ShadowBrushSerializable
		{
			get { return Serialize.BrushToString(ShadowBrush); }
			set { ShadowBrush = Serialize.StringToBrush(value); }
		}

		[Range(1, int.MaxValue)]
		[Display(Name = "Shadow width", Description = "Width of wick and body outline", Order = 4, GroupName = "Visual")]
		public int ShadowWidth
		{ get; set; }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> SpreadOpen
		{
			get { return Values[0]; }
		}

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> SpreadHigh
		{
			get { return Values[1]; }
		}

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> SpreadLow
		{
			get { return Values[2]; }
		}

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> SpreadClose
		{
			get { return Values[3]; }
		}
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SpreadCandlesticks[] cacheSpreadCandlesticks;
		public SpreadCandlesticks SpreadCandlesticks(double qty1, string symbol2, double qty2)
		{
			return SpreadCandlesticks(Input, qty1, symbol2, qty2);
		}

		public SpreadCandlesticks SpreadCandlesticks(ISeries<double> input, double qty1, string symbol2, double qty2)
		{
			if (cacheSpreadCandlesticks != null)
				for (int idx = 0; idx < cacheSpreadCandlesticks.Length; idx++)
					if (cacheSpreadCandlesticks[idx] != null && cacheSpreadCandlesticks[idx].Qty1 == qty1 && cacheSpreadCandlesticks[idx].Symbol2 == symbol2 && cacheSpreadCandlesticks[idx].Qty2 == qty2 && cacheSpreadCandlesticks[idx].EqualsInput(input))
						return cacheSpreadCandlesticks[idx];
			return CacheIndicator<SpreadCandlesticks>(new SpreadCandlesticks(){ Qty1 = qty1, Symbol2 = symbol2, Qty2 = qty2 }, input, ref cacheSpreadCandlesticks);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SpreadCandlesticks SpreadCandlesticks(double qty1, string symbol2, double qty2)
		{
			return indicator.SpreadCandlesticks(Input, qty1, symbol2, qty2);
		}

		public Indicators.SpreadCandlesticks SpreadCandlesticks(ISeries<double> input , double qty1, string symbol2, double qty2)
		{
			return indicator.SpreadCandlesticks(input, qty1, symbol2, qty2);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SpreadCandlesticks SpreadCandlesticks(double qty1, string symbol2, double qty2)
		{
			return indicator.SpreadCandlesticks(Input, qty1, symbol2, qty2);
		}

		public Indicators.SpreadCandlesticks SpreadCandlesticks(ISeries<double> input , double qty1, string symbol2, double qty2)
		{
			return indicator.SpreadCandlesticks(input, qty1, symbol2, qty2);
		}
	}
}

#endregion
