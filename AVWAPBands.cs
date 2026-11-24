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
#endregion

//This namespace holds Drawing tools in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.DrawingTools
{
	/// <summary>
	/// Click-to-anchor AVWAP drawing tool. Click a bar to anchor; the tool computes
	/// VWAP and volume-weighted SD bands from that bar to the latest bar (updates live)
	/// using the same math as the AVWAP indicator: p = (H+L+C)/3.
	/// Drag the anchor dot or any line to re-anchor.
	/// </summary>
	public class AVWAPBands : DrawingTool
	{
		private const double			CursorSensitivity	= 8;
		private readonly List<SharpDX.Vector2[]>	lineCache	= new List<SharpDX.Vector2[]>();
		private Point					markerPoint;
		private bool					hasMarker;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name				= "AVWAP Bands";
				Description			= @"Anchored VWAP with standard deviation bands and region fills";

				Anchor				= new ChartAnchor
				{
					DisplayName		= "Anchor",
					IsEditing		= true,
					IsBrowsable		= true
				};

				SD1Multiplier		= 1.0;
				SD2Multiplier		= 2.0;
				ShowSD1				= true;
				ShowSD2				= true;
				ShowAnchorMarker	= true;

				VWAPStroke			= new Stroke(Brushes.Purple,		DashStyleHelper.Solid,	2);
				SD1Stroke			= new Stroke(Brushes.DodgerBlue,	DashStyleHelper.Dash,	1);
				SD2Stroke			= new Stroke(Brushes.OrangeRed,		DashStyleHelper.Dash,	1);

				ShowFills			= true;
				InnerFillColor		= Brushes.MediumPurple;
				InnerFillOpacity	= 10;
				UpperFillColor		= Brushes.Red;
				UpperFillOpacity	= 12;
				LowerFillColor		= Brushes.LimeGreen;
				LowerFillOpacity	= 12;
			}
			else if (State == State.Terminated)
			{
				Dispose();
			}
		}

		public override IEnumerable<ChartAnchor> Anchors
		{
			get { return new[] { Anchor }; }
		}

		#region Mouse handling
		public override void OnMouseDown(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
		{
			switch (DrawingState)
			{
				case DrawingState.Building:
					dataPoint.CopyDataValues(Anchor);
					Anchor.IsEditing	= false;
					DrawingState		= DrawingState.Normal;
					IsSelected			= false;
					break;

				case DrawingState.Normal:
					Point p = dataPoint.GetPoint(chartControl, chartPanel, chartScale);
					if (!IsLocked && IsPointOnTool(p))
						DrawingState = DrawingState.Moving;
					else
						IsSelected = false;
					break;
			}
		}

		public override void OnMouseMove(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
		{
			// Live preview while placing, and re-anchoring while dragging
			if (DrawingState == DrawingState.Building)
				dataPoint.CopyDataValues(Anchor);
			else if (DrawingState == DrawingState.Moving && !IsLocked)
				dataPoint.CopyDataValues(Anchor);
		}

		public override void OnMouseUp(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
		{
			if (DrawingState == DrawingState.Moving || DrawingState == DrawingState.Editing)
				DrawingState = DrawingState.Normal;
		}

		public override Cursor GetCursor(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, Point point)
		{
			switch (DrawingState)
			{
				case DrawingState.Building:
					return Cursors.Pen;
				case DrawingState.Moving:
					return IsLocked ? Cursors.No : Cursors.SizeAll;
				default:
					if (IsPointOnTool(point))
						return IsLocked ? Cursors.Arrow : Cursors.SizeAll;
					return null;
			}
		}

		public override Point[] GetSelectionPoints(ChartControl chartControl, ChartScale chartScale)
		{
			return hasMarker ? new[] { markerPoint } : new Point[0];
		}

		public override bool IsVisibleOnChart(ChartControl chartControl, ChartScale chartScale, DateTime firstTimeOnChart, DateTime lastTimeOnChart)
		{
			if (DrawingState == DrawingState.Building)
				return true;
			return Anchor.Time <= lastTimeOnChart;
		}

		private bool IsPointOnTool(Point p)
		{
			if (hasMarker && Distance(p.X, p.Y, markerPoint.X, markerPoint.Y) <= CursorSensitivity)
				return true;

			foreach (SharpDX.Vector2[] line in lineCache)
				for (int i = 1; i < line.Length; i++)
					if (DistanceToSegment(p, line[i - 1], line[i]) <= CursorSensitivity)
						return true;

			return false;
		}

		private static double Distance(double x1, double y1, double x2, double y2)
		{
			double dx = x2 - x1, dy = y2 - y1;
			return Math.Sqrt(dx * dx + dy * dy);
		}

		private static double DistanceToSegment(Point p, SharpDX.Vector2 a, SharpDX.Vector2 b)
		{
			double dx = b.X - a.X, dy = b.Y - a.Y;
			double lenSq = dx * dx + dy * dy;
			if (lenSq <= 0)
				return Distance(p.X, p.Y, a.X, a.Y);
			double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
			t = Math.Max(0, Math.Min(1, t));
			return Distance(p.X, p.Y, a.X + t * dx, a.Y + t * dy);
		}
		#endregion

		#region Rendering
		public override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			lineCache.Clear();
			hasMarker = false;

			ChartBars chartBars = GetAttachedToChartBars();
			if (chartBars == null || chartBars.Bars == null || chartBars.Bars.Count == 0)
				return;

			Bars bars = chartBars.Bars;

			// Anchor not within loaded data -> don't draw a wrong VWAP
			if (Anchor.Time < bars.GetTime(0))
				return;

			int anchorIdx	= chartBars.GetBarIdxByTime(chartControl, Anchor.Time);
			int lastIdx		= Math.Min(chartBars.ToIndex, bars.Count - 1);
			if (anchorIdx < 0 || anchorIdx > lastIdx)
				return;

			int startIdx	= Math.Max(anchorIdx, chartBars.FromIndex - 1);
			int capacity	= Math.Max(1, lastIdx - startIdx + 1);

			List<SharpDX.Vector2> vwapPts	= new List<SharpDX.Vector2>(capacity);
			List<SharpDX.Vector2> up1Pts	= new List<SharpDX.Vector2>(capacity);
			List<SharpDX.Vector2> dn1Pts	= new List<SharpDX.Vector2>(capacity);
			List<SharpDX.Vector2> up2Pts	= new List<SharpDX.Vector2>(capacity);
			List<SharpDX.Vector2> dn2Pts	= new List<SharpDX.Vector2>(capacity);

			double cumVol = 0, cumPV = 0, cumPV2 = 0;

			for (int i = anchorIdx; i <= lastIdx; i++)
			{
				double price	= (bars.GetHigh(i) + bars.GetLow(i) + bars.GetClose(i)) / 3.0;
				double vol		= bars.GetVolume(i);

				cumVol	+= vol;
				cumPV	+= vol * price;
				cumPV2	+= vol * price * price;

				if (i < startIdx || cumVol <= 0)
					continue;

				double vwap		= cumPV / cumVol;
				double variance	= cumPV2 / cumVol - vwap * vwap;
				double sd		= variance > 0 ? Math.Sqrt(variance) : 0;

				float x = chartControl.GetXByBarIndex(chartBars, i);

				vwapPts.Add(new SharpDX.Vector2(x, chartScale.GetYByValue(vwap)));
				up1Pts.Add(new SharpDX.Vector2(x, chartScale.GetYByValue(vwap + SD1Multiplier * sd)));
				dn1Pts.Add(new SharpDX.Vector2(x, chartScale.GetYByValue(vwap - SD1Multiplier * sd)));
				up2Pts.Add(new SharpDX.Vector2(x, chartScale.GetYByValue(vwap + SD2Multiplier * sd)));
				dn2Pts.Add(new SharpDX.Vector2(x, chartScale.GetYByValue(vwap - SD2Multiplier * sd)));

				if (i == anchorIdx)
				{
					markerPoint	= new Point(x, chartScale.GetYByValue(vwap));
					hasMarker	= true;
				}
			}

			SharpDX.Direct2D1.AntialiasMode oldMode = RenderTarget.AntialiasMode;
			RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;

			// Fills first so lines sit on top (skipped during hit-test pass)
			if (ShowFills && !IsInHitTest)
			{
				if (ShowSD2)
				{
					FillBand(up2Pts, up1Pts, UpperFillColor, UpperFillOpacity);
					FillBand(dn1Pts, dn2Pts, LowerFillColor, LowerFillOpacity);
				}
				if (ShowSD1)
					FillBand(up1Pts, dn1Pts, InnerFillColor, InnerFillOpacity);
			}

			if (ShowSD2)
			{
				RenderPolyline(chartControl, up2Pts, SD2Stroke);
				RenderPolyline(chartControl, dn2Pts, SD2Stroke);
			}
			if (ShowSD1)
			{
				RenderPolyline(chartControl, up1Pts, SD1Stroke);
				RenderPolyline(chartControl, dn1Pts, SD1Stroke);
			}
			RenderPolyline(chartControl, vwapPts, VWAPStroke);

			if (ShowAnchorMarker && hasMarker && VWAPStroke != null)
			{
				VWAPStroke.RenderTarget = RenderTarget;
				SharpDX.Direct2D1.Brush mBrush = IsInHitTest ? chartControl.SelectionBrush : VWAPStroke.BrushDX;
				float r = Math.Max(3f, VWAPStroke.Width + 2f);
				RenderTarget.FillEllipse(
					new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2((float)markerPoint.X, (float)markerPoint.Y), r, r),
					mBrush);
			}

			RenderTarget.AntialiasMode = oldMode;
		}

		private void RenderPolyline(ChartControl chartControl, List<SharpDX.Vector2> pts, Stroke stroke)
		{
			if (stroke == null || pts.Count < 2)
				return;

			stroke.RenderTarget = RenderTarget;
			SharpDX.Direct2D1.Brush brush = IsInHitTest ? chartControl.SelectionBrush : stroke.BrushDX;
			if (brush == null)
				return;

			using (SharpDX.Direct2D1.PathGeometry geo = new SharpDX.Direct2D1.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
			{
				using (SharpDX.Direct2D1.GeometrySink sink = geo.Open())
				{
					sink.BeginFigure(pts[0], SharpDX.Direct2D1.FigureBegin.Hollow);
					for (int i = 1; i < pts.Count; i++)
						sink.AddLine(pts[i]);
					sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Open);
					sink.Close();
				}
				RenderTarget.DrawGeometry(geo, brush, stroke.Width, stroke.StrokeStyle);
			}

			lineCache.Add(pts.ToArray());
		}

		private void FillBand(List<SharpDX.Vector2> top, List<SharpDX.Vector2> bot, Brush wpfBrush, int opacity)
		{
			if (wpfBrush == null || opacity <= 0 || top.Count < 2 || top.Count != bot.Count)
				return;

			using (SharpDX.Direct2D1.Brush dxBrush = wpfBrush.ToDxBrush(RenderTarget))
			using (SharpDX.Direct2D1.PathGeometry geo = new SharpDX.Direct2D1.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
			{
				dxBrush.Opacity = opacity / 100f;

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
				RenderTarget.FillGeometry(geo, dxBrush);
			}
		}
		#endregion

		#region Properties
		[Display(Name="Anchor", Order=1, GroupName="Anchor")]
		public ChartAnchor Anchor
		{ get; set; }

		[Display(Name="Show anchor marker", Order=2, GroupName="Anchor")]
		public bool ShowAnchorMarker
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

		// ---------- Lines (Stroke editor = color, dash style, thickness, opacity) ----------
		[Display(Name="VWAP line", Order=1, GroupName="2. Lines")]
		public Stroke VWAPStroke
		{ get; set; }

		[Display(Name="1SD lines", Order=2, GroupName="2. Lines")]
		public Stroke SD1Stroke
		{ get; set; }

		[Display(Name="2SD lines", Order=3, GroupName="2. Lines")]
		public Stroke SD2Stroke
		{ get; set; }

		// ---------- Fills ----------
		[Display(Name="Show fills", Order=1, GroupName="3. Region Fills")]
		public bool ShowFills
		{ get; set; }

		[XmlIgnore]
		[Display(Name="Inner (-1SD to +1SD) color", Order=2, GroupName="3. Region Fills")]
		public Brush InnerFillColor
		{ get; set; }

		[Browsable(false)]
		public string InnerFillColorSerializable
		{
			get { return Serialize.BrushToString(InnerFillColor); }
			set { InnerFillColor = Serialize.StringToBrush(value); }
		}

		[Range(0, 100)]
		[Display(Name="Inner opacity %", Order=3, GroupName="3. Region Fills")]
		public int InnerFillOpacity
		{ get; set; }

		[XmlIgnore]
		[Display(Name="Upper (+1SD to +2SD) color", Order=4, GroupName="3. Region Fills")]
		public Brush UpperFillColor
		{ get; set; }

		[Browsable(false)]
		public string UpperFillColorSerializable
		{
			get { return Serialize.BrushToString(UpperFillColor); }
			set { UpperFillColor = Serialize.StringToBrush(value); }
		}

		[Range(0, 100)]
		[Display(Name="Upper opacity %", Order=5, GroupName="3. Region Fills")]
		public int UpperFillOpacity
		{ get; set; }

		[XmlIgnore]
		[Display(Name="Lower (-1SD to -2SD) color", Order=6, GroupName="3. Region Fills")]
		public Brush LowerFillColor
		{ get; set; }

		[Browsable(false)]
		public string LowerFillColorSerializable
		{
			get { return Serialize.BrushToString(LowerFillColor); }
			set { LowerFillColor = Serialize.StringToBrush(value); }
		}

		[Range(0, 100)]
		[Display(Name="Lower opacity %", Order=7, GroupName="3. Region Fills")]
		public int LowerFillOpacity
		{ get; set; }
		#endregion
	}
}
