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

//This namespace holds Indicators in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Indicators
{
	/// <summary>
	/// Anchored VWAP with volume-weighted standard deviation bands (1SD / 2SD by default)
	/// and semi-transparent fills between the bands.
	///
	/// Plots: [0] VWAP, [1] +SD1, [2] -SD1, [3] +SD2, [4] -SD2
	/// SD = sqrt( sum(v*p^2)/sum(v) - VWAP^2 ),  p = (H+L+C)/3
	/// </summary>
	public class AVWAP : Indicator
	{
		private DateTime		anchorDateTime;
		private Series<double>	cumVol;
		private Series<double>	cumPV;
		private Series<double>	cumPV2;
		private Series<double>	sdSeries;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= @"Anchored Volume Weighted Average Price with standard deviation bands";
				Name						= "AVWAP";
				Calculate					= Calculate.OnBarClose;
				IsOverlay					= true;
				DisplayInDataBox			= true;
				DrawOnPricePanel			= true;
				DrawHorizontalGridLines		= true;
				DrawVerticalGridLines		= true;
				PaintPriceMarkers			= true;
				ScaleJustification			= NinjaTrader.Gui.Chart.ScaleJustification.Right;
				IsSuspendedWhileInactive	= true;
				MaximumBarsLookBack			= MaximumBarsLookBack.Infinite;
				// Line styling is driven by the inputs below, so hide the standard Plots section
				ArePlotsConfigurable		= false;

				// Anchor
				AnchorYear					= 2024;
				AnchorMonth					= 4;
				AnchorDay					= 1;
				AnchorTime					= DateTime.Parse("08:30", System.Globalization.CultureInfo.InvariantCulture);

				// Bands
				SD1Multiplier				= 1.0;
				SD2Multiplier				= 2.0;
				ShowSD1						= true;
				ShowSD2						= true;

				// Line styles
				VWAPColor					= Brushes.Purple;
				VWAPDashStyle				= DashStyleHelper.Solid;
				VWAPWidth					= 2;

				SD1Color					= Brushes.DodgerBlue;
				SD1DashStyle				= DashStyleHelper.Dash;
				SD1Width					= 1;

				SD2Color					= Brushes.OrangeRed;
				SD2DashStyle				= DashStyleHelper.Dash;
				SD2Width					= 1;

				// Fills
				ShowFills					= true;
				InnerFillColor				= Brushes.MediumPurple;
				InnerFillOpacity			= 10;
				UpperFillColor				= Brushes.Red;
				UpperFillOpacity			= 12;
				LowerFillColor				= Brushes.LimeGreen;
				LowerFillOpacity			= 12;

				AddPlot(new Stroke(Brushes.Purple,		DashStyleHelper.Solid,	2), PlotStyle.Line, "PlotVWAP");
				AddPlot(new Stroke(Brushes.DodgerBlue,	DashStyleHelper.Dash,	1), PlotStyle.Line, "Upper1SD");
				AddPlot(new Stroke(Brushes.DodgerBlue,	DashStyleHelper.Dash,	1), PlotStyle.Line, "Lower1SD");
				AddPlot(new Stroke(Brushes.OrangeRed,	DashStyleHelper.Dash,	1), PlotStyle.Line, "Upper2SD");
				AddPlot(new Stroke(Brushes.OrangeRed,	DashStyleHelper.Dash,	1), PlotStyle.Line, "Lower2SD");
			}
			else if (State == State.Configure)
			{
				ApplyPlotStyle(0, VWAPColor,	VWAPDashStyle,	VWAPWidth,	true);
				ApplyPlotStyle(1, SD1Color,		SD1DashStyle,	SD1Width,	ShowSD1);
				ApplyPlotStyle(2, SD1Color,		SD1DashStyle,	SD1Width,	ShowSD1);
				ApplyPlotStyle(3, SD2Color,		SD2DashStyle,	SD2Width,	ShowSD2);
				ApplyPlotStyle(4, SD2Color,		SD2DashStyle,	SD2Width,	ShowSD2);
			}
			else if (State == State.DataLoaded)
			{
				// Bar-indexed accumulators: correct under OnBarClose, OnPriceChange and OnEachTick
				cumVol		= new Series<double>(this, MaximumBarsLookBack.Infinite);
				cumPV		= new Series<double>(this, MaximumBarsLookBack.Infinite);
				cumPV2		= new Series<double>(this, MaximumBarsLookBack.Infinite);
				sdSeries	= new Series<double>(this, MaximumBarsLookBack.Infinite);

				// Build the anchor once (clamped so e.g. Feb 30 doesn't throw)
				int year	= Math.Max(1900, Math.Min(2100, AnchorYear));
				int month	= Math.Max(1, Math.Min(12, AnchorMonth));
				int day		= Math.Max(1, Math.Min(DateTime.DaysInMonth(year, month), AnchorDay));
				anchorDateTime = new DateTime(year, month, day, AnchorTime.Hour, AnchorTime.Minute, 0);
			}
		}

		private void ApplyPlotStyle(int idx, Brush brush, DashStyleHelper dash, int width, bool visible)
		{
			Plots[idx].Brush			= visible && brush != null ? brush : Brushes.Transparent;
			Plots[idx].DashStyleHelper	= dash;
			Plots[idx].Width			= width;
		}

		protected override void OnBarUpdate()
		{
			if (Time[0] < anchorDateTime)
				return;

			double price	= (High[0] + Low[0] + Close[0]) / 3.0;
			double vol		= Volume[0];
			bool isFirstBar	= CurrentBar == 0 || Time[1] < anchorDateTime;

			if (isFirstBar)
			{
				cumVol[0]	= vol;
				cumPV[0]	= vol * price;
				cumPV2[0]	= vol * price * price;
			}
			else
			{
				cumVol[0]	= cumVol[1] + vol;
				cumPV[0]	= cumPV[1]  + vol * price;
				cumPV2[0]	= cumPV2[1] + vol * price * price;
			}

			if (cumVol[0] <= 0)
				return;

			double vwap		= cumPV[0] / cumVol[0];
			double variance	= cumPV2[0] / cumVol[0] - vwap * vwap;
			double sd		= variance > 0 ? Math.Sqrt(variance) : 0;

			sdSeries[0]		= sd;
			Values[0][0]	= vwap;
			Values[1][0]	= vwap + SD1Multiplier * sd;
			Values[2][0]	= vwap - SD1Multiplier * sd;
			Values[3][0]	= vwap + SD2Multiplier * sd;
			Values[4][0]	= vwap - SD2Multiplier * sd;
		}

		#region Rendering (band fills)
		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (ShowFills && ChartBars != null && RenderTarget != null && Bars != null && Bars.Count > 0)
			{
				SharpDX.Direct2D1.AntialiasMode oldMode = RenderTarget.AntialiasMode;
				RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;

				if (ShowSD2)
				{
					FillBand(chartControl, chartScale, Values[3], Values[1], UpperFillColor, UpperFillOpacity);
					FillBand(chartControl, chartScale, Values[2], Values[4], LowerFillColor, LowerFillOpacity);
				}
				if (ShowSD1)
					FillBand(chartControl, chartScale, Values[1], Values[2], InnerFillColor, InnerFillOpacity);

				RenderTarget.AntialiasMode = oldMode;
			}

			// Draw the plot lines on top of the fills
			base.OnRender(chartControl, chartScale);
		}

		private void FillBand(ChartControl chartControl, ChartScale chartScale, Series<double> upper, Series<double> lower, Brush wpfBrush, int opacity)
		{
			if (wpfBrush == null || opacity <= 0)
				return;

			int from	= Math.Max(0, ChartBars.FromIndex - 1);
			int to		= Math.Min(ChartBars.ToIndex, Bars.Count - 1);

			List<SharpDX.Vector2> top = new List<SharpDX.Vector2>();
			List<SharpDX.Vector2> bot = new List<SharpDX.Vector2>();

			using (SharpDX.Direct2D1.Brush dxBrush = wpfBrush.ToDxBrush(RenderTarget))
			{
				dxBrush.Opacity = opacity / 100f;

				for (int i = from; i <= to; i++)
				{
					if (upper.IsValidDataPointAt(i) && lower.IsValidDataPointAt(i))
					{
						float x = chartControl.GetXByBarIndex(ChartBars, i);
						top.Add(new SharpDX.Vector2(x, chartScale.GetYByValue(upper.GetValueAt(i))));
						bot.Add(new SharpDX.Vector2(x, chartScale.GetYByValue(lower.GetValueAt(i))));
					}
					else
					{
						FillPolygon(top, bot, dxBrush);
						top.Clear();
						bot.Clear();
					}
				}
				FillPolygon(top, bot, dxBrush);
			}
		}

		private void FillPolygon(List<SharpDX.Vector2> top, List<SharpDX.Vector2> bot, SharpDX.Direct2D1.Brush brush)
		{
			if (top.Count < 2)
				return;

			using (SharpDX.Direct2D1.PathGeometry geo = new SharpDX.Direct2D1.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
			{
				using (SharpDX.Direct2D1.GeometrySink sink = geo.Open())
				{
					sink.BeginFigure(top[0], SharpDX.Direct2D1.FigureBegin.Filled);
					for (int i = 1; i < top.Count; i++)
						sink.AddLine(top[i]);
					for (int i = bot.Count - 1; i >= 0; i--)
						sink.AddLine(bot[i]);
					sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
					sink.Close();
				}
				RenderTarget.FillGeometry(geo, brush);
			}
		}
		#endregion

		#region Properties
		// ---------- Anchor (constructor parameters — signature unchanged from original) ----------
		[NinjaScriptProperty]
		[Range(1900, 2100)]
		[Display(Name="AnchorYear", Order=1, GroupName="Parameters")]
		public int AnchorYear
		{ get; set; }

		[NinjaScriptProperty]
		[Range(1, 12)]
		[Display(Name="AnchorMonth", Order=2, GroupName="Parameters")]
		public int AnchorMonth
		{ get; set; }

		[NinjaScriptProperty]
		[Range(1, 31)]
		[Display(Name="AnchorDay", Order=3, GroupName="Parameters")]
		public int AnchorDay
		{ get; set; }

		[NinjaScriptProperty]
		[PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
		[Display(Name="AnchorTime", Order=4, GroupName="Parameters")]
		public DateTime AnchorTime
		{ get; set; }

		// ---------- Bands ----------
		[Range(0.1, double.MaxValue)]
		[Display(Name="Inner band multiplier", Order=1, GroupName="1. Bands")]
		public double SD1Multiplier
		{ get; set; }

		[Range(0.1, double.MaxValue)]
		[Display(Name="Outer band multiplier", Order=2, GroupName="1. Bands")]
		public double SD2Multiplier
		{ get; set; }

		[Display(Name="Show 1SD lines", Order=3, GroupName="1. Bands")]
		public bool ShowSD1
		{ get; set; }

		[Display(Name="Show 2SD lines", Order=4, GroupName="1. Bands")]
		public bool ShowSD2
		{ get; set; }

		// ---------- VWAP line ----------
		[XmlIgnore]
		[Display(Name="Color", Order=1, GroupName="2. VWAP Line")]
		public Brush VWAPColor
		{ get; set; }

		[Browsable(false)]
		public string VWAPColorSerializable
		{
			get { return Serialize.BrushToString(VWAPColor); }
			set { VWAPColor = Serialize.StringToBrush(value); }
		}

		[Display(Name="Line style", Order=2, GroupName="2. VWAP Line")]
		public DashStyleHelper VWAPDashStyle
		{ get; set; }

		[Range(1, 10)]
		[Display(Name="Thickness", Order=3, GroupName="2. VWAP Line")]
		public int VWAPWidth
		{ get; set; }

		// ---------- 1SD lines ----------
		[XmlIgnore]
		[Display(Name="Color", Order=1, GroupName="3. 1SD Lines")]
		public Brush SD1Color
		{ get; set; }

		[Browsable(false)]
		public string SD1ColorSerializable
		{
			get { return Serialize.BrushToString(SD1Color); }
			set { SD1Color = Serialize.StringToBrush(value); }
		}

		[Display(Name="Line style", Order=2, GroupName="3. 1SD Lines")]
		public DashStyleHelper SD1DashStyle
		{ get; set; }

		[Range(1, 10)]
		[Display(Name="Thickness", Order=3, GroupName="3. 1SD Lines")]
		public int SD1Width
		{ get; set; }

		// ---------- 2SD lines ----------
		[XmlIgnore]
		[Display(Name="Color", Order=1, GroupName="4. 2SD Lines")]
		public Brush SD2Color
		{ get; set; }

		[Browsable(false)]
		public string SD2ColorSerializable
		{
			get { return Serialize.BrushToString(SD2Color); }
			set { SD2Color = Serialize.StringToBrush(value); }
		}

		[Display(Name="Line style", Order=2, GroupName="4. 2SD Lines")]
		public DashStyleHelper SD2DashStyle
		{ get; set; }

		[Range(1, 10)]
		[Display(Name="Thickness", Order=3, GroupName="4. 2SD Lines")]
		public int SD2Width
		{ get; set; }

		// ---------- Fills ----------
		[Display(Name="Show fills", Order=1, GroupName="5. Region Fills")]
		public bool ShowFills
		{ get; set; }

		[XmlIgnore]
		[Display(Name="Inner (-1SD to +1SD) color", Order=2, GroupName="5. Region Fills")]
		public Brush InnerFillColor
		{ get; set; }

		[Browsable(false)]
		public string InnerFillColorSerializable
		{
			get { return Serialize.BrushToString(InnerFillColor); }
			set { InnerFillColor = Serialize.StringToBrush(value); }
		}

		[Range(0, 100)]
		[Display(Name="Inner opacity %", Order=3, GroupName="5. Region Fills")]
		public int InnerFillOpacity
		{ get; set; }

		[XmlIgnore]
		[Display(Name="Upper (+1SD to +2SD) color", Order=4, GroupName="5. Region Fills")]
		public Brush UpperFillColor
		{ get; set; }

		[Browsable(false)]
		public string UpperFillColorSerializable
		{
			get { return Serialize.BrushToString(UpperFillColor); }
			set { UpperFillColor = Serialize.StringToBrush(value); }
		}

		[Range(0, 100)]
		[Display(Name="Upper opacity %", Order=5, GroupName="5. Region Fills")]
		public int UpperFillOpacity
		{ get; set; }

		[XmlIgnore]
		[Display(Name="Lower (-1SD to -2SD) color", Order=6, GroupName="5. Region Fills")]
		public Brush LowerFillColor
		{ get; set; }

		[Browsable(false)]
		public string LowerFillColorSerializable
		{
			get { return Serialize.BrushToString(LowerFillColor); }
			set { LowerFillColor = Serialize.StringToBrush(value); }
		}

		[Range(0, 100)]
		[Display(Name="Lower opacity %", Order=7, GroupName="5. Region Fills")]
		public int LowerFillOpacity
		{ get; set; }

		// ---------- Exposed series (for strategies / other indicators) ----------
		[Browsable(false)]
		[XmlIgnore]
		public Series<double> AVWAPLine
		{
			get { return Values[0]; }
		}

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Upper1SD
		{
			get { return Values[1]; }
		}

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Lower1SD
		{
			get { return Values[2]; }
		}

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Upper2SD
		{
			get { return Values[3]; }
		}

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Lower2SD
		{
			get { return Values[4]; }
		}

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> SDValue
		{
			get { Update(); return sdSeries; }
		}
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private AVWAP[] cacheAVWAP;
		public AVWAP AVWAP(int anchorYear, int anchorMonth, int anchorDay, DateTime anchorTime)
		{
			return AVWAP(Input, anchorYear, anchorMonth, anchorDay, anchorTime);
		}

		public AVWAP AVWAP(ISeries<double> input, int anchorYear, int anchorMonth, int anchorDay, DateTime anchorTime)
		{
			if (cacheAVWAP != null)
				for (int idx = 0; idx < cacheAVWAP.Length; idx++)
					if (cacheAVWAP[idx] != null && cacheAVWAP[idx].AnchorYear == anchorYear && cacheAVWAP[idx].AnchorMonth == anchorMonth && cacheAVWAP[idx].AnchorDay == anchorDay && cacheAVWAP[idx].AnchorTime == anchorTime && cacheAVWAP[idx].EqualsInput(input))
						return cacheAVWAP[idx];
			return CacheIndicator<AVWAP>(new AVWAP(){ AnchorYear = anchorYear, AnchorMonth = anchorMonth, AnchorDay = anchorDay, AnchorTime = anchorTime }, input, ref cacheAVWAP);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.AVWAP AVWAP(int anchorYear, int anchorMonth, int anchorDay, DateTime anchorTime)
		{
			return indicator.AVWAP(Input, anchorYear, anchorMonth, anchorDay, anchorTime);
		}

		public Indicators.AVWAP AVWAP(ISeries<double> input , int anchorYear, int anchorMonth, int anchorDay, DateTime anchorTime)
		{
			return indicator.AVWAP(input, anchorYear, anchorMonth, anchorDay, anchorTime);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.AVWAP AVWAP(int anchorYear, int anchorMonth, int anchorDay, DateTime anchorTime)
		{
			return indicator.AVWAP(Input, anchorYear, anchorMonth, anchorDay, anchorTime);
		}

		public Indicators.AVWAP AVWAP(ISeries<double> input , int anchorYear, int anchorMonth, int anchorDay, DateTime anchorTime)
		{
			return indicator.AVWAP(input, anchorYear, anchorMonth, anchorDay, anchorTime);
		}
	}
}

#endregion
