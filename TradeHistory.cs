// ====================================================================================
//  TradeHistory — NinjaTrader 8
//  Renders account trade executions on the chart:
//    ▲/▼  Directional arrow at entry (up = long, down = short)
//    ⟶   Dashed line from entry to exit / current price
//    ■    Rounded P&L label (Buy/Sell ±Xp) at mid-line
//    ◆    Diamond dot at exit or current price
//    [TH] Toolbar button to toggle visibility
//
//  Requirements:
//    • IsOverlay = true  (price panel)
//    • No Tick Replay required — reads from Account.Executions
//    • AccountName dropdown auto-populates from all connected accounts
// ====================================================================================

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
using NinjaTrader.NinjaScript;
#endregion

// Declared outside namespace so NT8's generated wrapper can resolve it.
public class TradeHistoryAccountConverter : System.ComponentModel.TypeConverter
{
    public override bool GetStandardValuesSupported(System.ComponentModel.ITypeDescriptorContext context) => true;
    public override bool GetStandardValuesExclusive(System.ComponentModel.ITypeDescriptorContext context) => false; // allow typing a custom name

    public override StandardValuesCollection GetStandardValues(System.ComponentModel.ITypeDescriptorContext context)
    {
        try
        {
            // Mirror what NT8's Accounts tab shows: only accounts with an active connection
            var names = NinjaTrader.Cbi.Account.All
                .Where(a => a.Connection != null
                         && (a.Connection.Status == NinjaTrader.Cbi.ConnectionStatus.Connected
                          || a.Connection.Status == NinjaTrader.Cbi.ConnectionStatus.Connecting))
                .Select(a => a.Name)
                .OrderBy(n => n)
                .ToList();
            return new StandardValuesCollection(names.Count > 0 ? names : new[] { "Sim101" });
        }
        catch
        {
            return new StandardValuesCollection(new[] { "Sim101" });
        }
    }
}

namespace NinjaTrader.NinjaScript.Indicators
{
    public class TradeHistory : Indicator
    {
        #region Inner types
        private sealed class TradeRecord
        {
            public DateTime EntryTime;
            public DateTime ExitTime;      // MinValue = open trade
            public double   EntryPrice;
            public double   ExitPrice;
            public int      Direction;     // +1 = long, -1 = short
            public bool     IsOpen => ExitTime == DateTime.MinValue;
        }
        #endregion

        #region Constants
        private const string GrpDisplay = "1. Display";
        private const string GrpColors  = "2. Colors";
        private const string GrpData    = "3. Data";
        #endregion

        #region Fields
        private readonly object             sync   = new object();
        private readonly List<TradeRecord>  trades = new List<TradeRecord>();

        // Position tracking
        private int      netPos;
        private double   entryPriceSum;
        private long     entryQty;
        private DateTime entryTime;
        private int      entryDir;
        private DateTime cutoff;
        private double   currentPrice = double.NaN;
        private double   tickSize;
        private bool     isVisible    = true;

        private Account  subscribed;

        // Toolbar button
        private System.Windows.Controls.Button     chartButton;
        private System.Windows.Controls.ToolBar    chartToolBar;
        private System.Windows.Controls.StackPanel chartButtonPanel;

        // SharpDX resources
        private SharpDX.Direct2D1.Brush        dxLong, dxShort, dxWin, dxLoss, dxWhite, dxBlack;
        private SharpDX.Direct2D1.StrokeStyle  dashStyle;
        private SharpDX.DirectWrite.TextFormat textFmt;
        #endregion

        #region State
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Plots account trade executions: directional entry arrow, dashed P&L line, exit diamond, label.";
                Name                     = "TradeHistory";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;

                ArrowSize       = 10;
                ExitDiamondSize = 7;
                LineThickness   = 1.5;
                ShowLabel       = true;
                LabelFontSize   = 10;
                DaysToLoad      = 5;
                AccountName     = "Sim101";

                LongColor      = MkBrush(30,  150, 255);
                ShortColor     = MkBrush(255,  70,  70);
                WinLabelColor  = MkBrush(0,   170,  90);
                LossLabelColor = MkBrush(195,  45,  45);
            }
            else if (State == State.DataLoaded)
            {
                tickSize     = Instrument.MasterInstrument.TickSize;
                cutoff       = ComputeCutoff(DaysToLoad);
                currentPrice = double.NaN;
                lock (sync) { trades.Clear(); netPos = 0; entryQty = 0; entryPriceSum = 0; }
                Subscribe();
            }
            else if (State == State.Historical)
            {
                if (ChartControl != null)
                    ChartControl.Dispatcher.BeginInvoke(
                        new Action(AddToolbarButton),
                        System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else if (State == State.Terminated)
            {
                Unsubscribe();
                RemoveToolbarButton();
                DisposeDx();
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            currentPrice = Close[0];
        }
        #endregion

        #region Account subscription
        private void Subscribe()
        {
            Account acct = Account.All.FirstOrDefault(a => a.Name == AccountName);
            if (acct == null) return;
            subscribed = acct;
            subscribed.ExecutionUpdate += OnExec;
            LoadHistory(acct);
        }

        private void Unsubscribe()
        {
            if (subscribed == null) return;
            subscribed.ExecutionUpdate -= OnExec;
            subscribed = null;
        }

        private void LoadHistory(Account acct)
        {
            var execs = acct.Executions
                .Where(e => e.Instrument?.FullName == Instrument.FullName && e.Time >= cutoff)
                .OrderBy(e => e.Time)
                .ToList();
            lock (sync)
            {
                netPos = 0; entryQty = 0; entryPriceSum = 0;
                foreach (var ex in execs)
                {
                    if (ex.Order == null) continue;
                    bool buy = ex.Order.OrderAction == OrderAction.Buy || ex.Order.OrderAction == OrderAction.BuyToCover;
                    Ingest(ex.Time, ex.Price, (int)ex.Quantity, buy);
                }
            }
        }

        private void OnExec(object sender, ExecutionEventArgs e)
        {
            Execution ex = e.Execution;
            if (ex?.Instrument == null || ex.Instrument.FullName != Instrument?.FullName) return;
            if (ex.Order == null) return;
            bool buy = ex.Order.OrderAction == OrderAction.Buy || ex.Order.OrderAction == OrderAction.BuyToCover;
            lock (sync) Ingest(ex.Time, ex.Price, (int)ex.Quantity, buy);
            ForceRefresh();
        }

        private void Ingest(DateTime time, double price, int qty, bool isBuy)
        {
            int fill = isBuy ? qty : -qty;
            int prev = netPos;
            netPos  += fill;

            if (prev == 0 && netPos != 0)
            {
                entryDir      = netPos > 0 ? 1 : -1;
                entryTime     = time;
                entryPriceSum = price * Math.Abs(fill);
                entryQty      = Math.Abs(fill);
            }
            else if (prev != 0 && netPos == 0)
            {
                FinalizePosition(time, price);
            }
            else if (prev != 0 && Math.Sign(prev) == Math.Sign(netPos))
            {
                entryPriceSum += price * Math.Abs(fill);
                entryQty      += Math.Abs(fill);
            }
            else
            {
                // Reversal — close old, open new
                FinalizePosition(time, price);
                entryDir      = netPos > 0 ? 1 : -1;
                entryTime     = time;
                entryPriceSum = price * Math.Abs(netPos);
                entryQty      = Math.Abs(netPos);
            }
        }

        private void FinalizePosition(DateTime time, double exitPrice)
        {
            if (entryQty <= 0) return;
            double ep = entryPriceSum / entryQty;
            if (entryTime >= cutoff)
                trades.Add(new TradeRecord
                {
                    EntryTime  = entryTime,
                    ExitTime   = time,
                    EntryPrice = ep,
                    ExitPrice  = exitPrice,
                    Direction  = entryDir
                });
            entryQty = 0; entryPriceSum = 0;
        }
        #endregion

        #region Toolbar button
        private void AddToolbarButton()
        {
            try
            {
                if (ChartControl == null) return;

                chartButton = new System.Windows.Controls.Button
                {
                    Content         = "TH",
                    Width           = 36,
                    Height          = 22,
                    FontSize        = 10,
                    FontWeight      = System.Windows.FontWeights.Bold,
                    ToolTip         = "Toggle Trade History",
                    Foreground      = Brushes.White,
                    Background      = new SolidColorBrush(Color.FromRgb(30, 100, 180)),
                    BorderThickness = new Thickness(1),
                    BorderBrush     = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                    Margin          = new Thickness(2, 0, 2, 0)
                };
                chartButton.Click += OnToolbarButtonClick;

                var root = System.Windows.Window.GetWindow(ChartControl);
                if (root == null) return;

                // Method 1: ToolBarTray (most common WPF pattern — holds one or more ToolBars)
                var tray = FindVisualChild<System.Windows.Controls.ToolBarTray>(root);
                if (tray != null && tray.ToolBars.Count > 0)
                {
                    chartToolBar = tray.ToolBars[tray.ToolBars.Count - 1];
                    chartToolBar.Items.Add(chartButton);
                    return;
                }

                // Method 2: standalone ToolBar
                chartToolBar = FindVisualChild<System.Windows.Controls.ToolBar>(root);
                if (chartToolBar != null)
                {
                    chartToolBar.Items.Add(chartButton);
                    return;
                }

                // Method 3: fallback — overlay at the top-right of the chart window root grid.
                //            Spans all rows/columns so it appears in the toolbar area.
                var grid = FindVisualChild<System.Windows.Controls.Grid>(root);
                if (grid != null)
                {
                    chartButtonPanel = new System.Windows.Controls.StackPanel
                    {
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                        VerticalAlignment   = System.Windows.VerticalAlignment.Top,
                        Margin              = new Thickness(0, 2, 10, 0),
                        IsHitTestVisible    = true
                    };
                    int cols = Math.Max(1, grid.ColumnDefinitions.Count);
                    int rows = Math.Max(1, grid.RowDefinitions.Count);
                    System.Windows.Controls.Grid.SetColumnSpan(chartButtonPanel, cols);
                    System.Windows.Controls.Grid.SetRowSpan(chartButtonPanel, rows);
                    System.Windows.Controls.Panel.SetZIndex(chartButtonPanel, 1000);
                    chartButtonPanel.Children.Add(chartButton);
                    grid.Children.Add(chartButtonPanel);
                }
            }
            catch { }
        }

        private void RemoveToolbarButton()
        {
            if (chartButton == null) return;
            try
            {
                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            chartToolBar?.Items.Remove(chartButton);
                            chartButtonPanel?.Children.Remove(chartButton);
                            if (chartButtonPanel?.Parent is System.Windows.Controls.Grid g)
                                g.Children.Remove(chartButtonPanel);
                        }
                        catch { }
                        chartButton      = null;
                        chartToolBar     = null;
                        chartButtonPanel = null;
                    });
            }
            catch { }
        }

        private void OnToolbarButtonClick(object sender, System.Windows.RoutedEventArgs e)
        {
            isVisible = !isVisible;
            if (chartButton != null)
                chartButton.Background = isVisible
                    ? new SolidColorBrush(Color.FromRgb(30, 100, 180))
                    : new SolidColorBrush(Color.FromRgb(70, 70, 70));
            ForceRefresh();
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }
        #endregion

        #region Rendering
        public override void OnRenderTargetChanged()
        {
            DisposeDx();
            if (RenderTarget == null) return;

            dxLong  = LongColor.ToDxBrush(RenderTarget);
            dxShort = ShortColor.ToDxBrush(RenderTarget);
            dxWin   = WinLabelColor.ToDxBrush(RenderTarget);
            dxLoss  = LossLabelColor.ToDxBrush(RenderTarget);
            dxWhite = Brushes.White.ToDxBrush(RenderTarget);
            dxBlack = Brushes.Black.ToDxBrush(RenderTarget);

            var sp = new SharpDX.Direct2D1.StrokeStyleProperties
            {
                DashStyle  = SharpDX.Direct2D1.DashStyle.Dash,
                DashOffset = 0f,
                StartCap   = SharpDX.Direct2D1.CapStyle.Round,
                EndCap     = SharpDX.Direct2D1.CapStyle.Round,
                DashCap    = SharpDX.Direct2D1.CapStyle.Round
            };
            dashStyle = new SharpDX.Direct2D1.StrokeStyle(RenderTarget.Factory, sp);

            textFmt = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
                SharpDX.DirectWrite.FontWeight.Bold,
                SharpDX.DirectWrite.FontStyle.Normal,
                (float)LabelFontSize)
            {
                TextAlignment      = SharpDX.DirectWrite.TextAlignment.Center,
                ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center,
                WordWrapping       = SharpDX.DirectWrite.WordWrapping.NoWrap
            };
        }

        protected override void OnRender(ChartControl cc, ChartScale cs)
        {
            if (!isVisible || RenderTarget == null || dxLong == null || ChartBars == null) return;

            List<TradeRecord> snap;
            lock (sync)
            {
                snap = new List<TradeRecord>(trades);
                if (netPos != 0 && entryQty > 0 && entryTime >= cutoff)
                {
                    double ep = entryPriceSum / entryQty;
                    snap.Add(new TradeRecord
                    {
                        EntryTime  = entryTime,
                        ExitTime   = DateTime.MinValue,   // open trade
                        EntryPrice = ep,
                        ExitPrice  = ep,                  // unused — no diamond/label on open trades
                        Direction  = entryDir
                    });
                }
            }

            if (snap.Count == 0) return;

            int lastBar = ChartBars.ToIndex;
            var oldAA   = RenderTarget.AntialiasMode;
            RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;

            foreach (var t in snap)
            {
                if (t.EntryTime < cutoff) continue;

                int entryBar = TimeToBar(t.EntryTime);
                int exitBar  = t.IsOpen ? lastBar : TimeToBar(t.ExitTime);
                exitBar      = Math.Min(exitBar, lastBar);

                if (entryBar < 0 || entryBar > lastBar) continue;

                float xE = cc.GetXByBarIndex(ChartBars, entryBar);
                float yE = cs.GetYByValue(t.EntryPrice);
                float xX = cc.GetXByBarIndex(ChartBars, exitBar);
                float yX;

                // Open trades: line extends horizontally to current bar at entry price level
                yX = t.IsOpen ? yE : cs.GetYByValue(t.ExitPrice);

                bool  isLong = t.Direction > 0;
                double pnl   = (t.ExitPrice - t.EntryPrice) * t.Direction;  // only used for closed trades
                bool   win   = pnl >= 0;

                SharpDX.Direct2D1.Brush lineBrush = isLong ? dxLong : dxShort;

                // 1. Dashed line entry → exit
                if (Math.Abs(xX - xE) > 2f && dashStyle != null)
                    RenderTarget.DrawLine(
                        new SharpDX.Vector2(xE, yE),
                        new SharpDX.Vector2(xX, yX),
                        lineBrush, (float)LineThickness, dashStyle);

                // 2. Entry arrow (▲ long / ▼ short)
                DrawArrow(xE, yE, ArrowSize, isLong, lineBrush, dxBlack);

                // 3. Exit diamond — only on closed trades (fixed price, never tracking live)
                if (!t.IsOpen)
                {
                    SharpDX.Direct2D1.Brush dotBrush = win ? dxWin : dxLoss;
                    DrawDiamond(xX, yX, ExitDiamondSize, dotBrush, dxWhite);
                }

                // 4. P&L label — only on closed (historical) trades
                if (!t.IsOpen && ShowLabel && textFmt != null && Math.Abs(xX - xE) > 55f)
                {
                    float xL = xE + (xX - xE) * 0.6f;
                    float yL = yE + (yX - yE) * 0.6f - 15f;

                    string sign  = pnl >= 0 ? "+" : "";
                    string label = string.Format("{0} {1}{2:0.##}p",
                        isLong ? "Buy" : "Sell", sign, pnl);

                    const float lw = 72f, lh = 18f;
                    var   lRect = new SharpDX.RectangleF(xL - lw / 2f, yL - lh / 2f, lw, lh);
                    var   rr    = new SharpDX.Direct2D1.RoundedRectangle { Rect = lRect, RadiusX = 4f, RadiusY = 4f };

                    SharpDX.Direct2D1.Brush bg = win ? dxWin : dxLoss;
                    float prevOp = bg.Opacity;
                    bg.Opacity = 0.82f;
                    RenderTarget.FillRoundedRectangle(rr, bg);
                    bg.Opacity = prevOp;
                    RenderTarget.DrawRoundedRectangle(rr, dxWhite, 0.6f);
                    RenderTarget.DrawText(label, textFmt, lRect, dxWhite,
                        SharpDX.Direct2D1.DrawTextOptions.Clip);
                }
            }

            RenderTarget.AntialiasMode = oldAA;
        }

        // ▲ / ▼ directional triangle
        private void DrawArrow(float cx, float cy, int size, bool upward,
            SharpDX.Direct2D1.Brush fill, SharpDX.Direct2D1.Brush outline)
        {
            float h = size;
            float w = size * 0.78f;
            using (var pg = new SharpDX.Direct2D1.PathGeometry(RenderTarget.Factory))
            {
                using (var gs = pg.Open())
                {
                    if (upward)
                    {
                        gs.BeginFigure(new SharpDX.Vector2(cx,     cy - h),     SharpDX.Direct2D1.FigureBegin.Filled);
                        gs.AddLine(    new SharpDX.Vector2(cx + w,  cy + h * 0.4f));
                        gs.AddLine(    new SharpDX.Vector2(cx - w,  cy + h * 0.4f));
                    }
                    else
                    {
                        gs.BeginFigure(new SharpDX.Vector2(cx,     cy + h),     SharpDX.Direct2D1.FigureBegin.Filled);
                        gs.AddLine(    new SharpDX.Vector2(cx + w,  cy - h * 0.4f));
                        gs.AddLine(    new SharpDX.Vector2(cx - w,  cy - h * 0.4f));
                    }
                    gs.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                    gs.Close();
                }
                float prevOp = fill.Opacity;
                fill.Opacity = 0.9f;
                RenderTarget.FillGeometry(pg, fill);
                fill.Opacity = prevOp;
                RenderTarget.DrawGeometry(pg, outline, 1.5f);
            }
        }

        // ◆ diamond exit marker
        private void DrawDiamond(float cx, float cy, int size,
            SharpDX.Direct2D1.Brush fill, SharpDX.Direct2D1.Brush outline)
        {
            float h = size;
            float w = size * 0.65f;
            using (var pg = new SharpDX.Direct2D1.PathGeometry(RenderTarget.Factory))
            {
                using (var gs = pg.Open())
                {
                    gs.BeginFigure(new SharpDX.Vector2(cx,     cy - h), SharpDX.Direct2D1.FigureBegin.Filled);
                    gs.AddLine(    new SharpDX.Vector2(cx + w,  cy));
                    gs.AddLine(    new SharpDX.Vector2(cx,     cy + h));
                    gs.AddLine(    new SharpDX.Vector2(cx - w,  cy));
                    gs.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                    gs.Close();
                }
                RenderTarget.FillGeometry(pg, fill);
                RenderTarget.DrawGeometry(pg, outline, 1f);
            }
        }
        #endregion

        #region Helpers
        private int TimeToBar(DateTime time)
        {
            if (CurrentBar < 0) return 0;
            int lo = 0, hi = CurrentBar;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (Bars.GetTime(mid) <= time) lo = mid;
                else                            hi = mid - 1;
            }
            return lo;
        }

        private void DisposeDx()
        {
            SafeDispose(ref dxLong);
            SafeDispose(ref dxShort);
            SafeDispose(ref dxWin);
            SafeDispose(ref dxLoss);
            SafeDispose(ref dxWhite);
            SafeDispose(ref dxBlack);
            if (dashStyle != null) { dashStyle.Dispose(); dashStyle = null; }
            if (textFmt   != null) { textFmt.Dispose();   textFmt   = null; }
        }

        private static void SafeDispose(ref SharpDX.Direct2D1.Brush b)
        {
            if (b != null) { b.Dispose(); b = null; }
        }

        private static Brush MkBrush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        private static DateTime ComputeCutoff(int days)
        {
            if (days <= 0) return DateTime.MinValue;
            DateTime d = DateTime.Now.Date;
            int count = 1;
            while (count < days)
            {
                d = d.AddDays(-1);
                if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday) count++;
            }
            return d.AddHours(-12);
        }
        #endregion

        #region Properties
        // ─── 1. Display ────────────────────────────────────────────────────────────
        [Range(4, 40)]
        [Display(Name = "Arrow Size", Description = "Entry arrow size (px).", Order = 1, GroupName = GrpDisplay)]
        public int ArrowSize { get; set; }

        [Range(2, 20)]
        [Display(Name = "Exit Diamond Size", Description = "Exit diamond marker size (px).", Order = 2, GroupName = GrpDisplay)]
        public int ExitDiamondSize { get; set; }

        [Range(0.5, 5.0)]
        [Display(Name = "Line Thickness", Description = "Dashed line stroke width.", Order = 3, GroupName = GrpDisplay)]
        public double LineThickness { get; set; }

        [Display(Name = "Show Label", Description = "Show Buy/Sell P&L label on each trade line.", Order = 4, GroupName = GrpDisplay)]
        public bool ShowLabel { get; set; }

        [Range(6, 20)]
        [Display(Name = "Label Font Size", Order = 5, GroupName = GrpDisplay)]
        public int LabelFontSize { get; set; }

        // ─── 2. Colors ─────────────────────────────────────────────────────────────
        [XmlIgnore]
        [Display(Name = "Long (Buy) Color", Order = 1, GroupName = GrpColors)]
        public Brush LongColor { get; set; }
        [Browsable(false)]
        public string LongColorSerializable
        { get { return Serialize.BrushToString(LongColor); } set { LongColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Short (Sell) Color", Order = 2, GroupName = GrpColors)]
        public Brush ShortColor { get; set; }
        [Browsable(false)]
        public string ShortColorSerializable
        { get { return Serialize.BrushToString(ShortColor); } set { ShortColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Win Label Color", Order = 3, GroupName = GrpColors)]
        public Brush WinLabelColor { get; set; }
        [Browsable(false)]
        public string WinLabelColorSerializable
        { get { return Serialize.BrushToString(WinLabelColor); } set { WinLabelColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Loss Label Color", Order = 4, GroupName = GrpColors)]
        public Brush LossLabelColor { get; set; }
        [Browsable(false)]
        public string LossLabelColorSerializable
        { get { return Serialize.BrushToString(LossLabelColor); } set { LossLabelColor = Serialize.StringToBrush(value); } }

        // ─── 3. Data ────────────────────────────────────────────────────────────────
        [Range(0, 365)]
        [Display(Name = "Days to Load", Description = "Weekdays of history to show (0 = all loaded data).", Order = 1, GroupName = GrpData)]
        public int DaysToLoad { get; set; }

        [TypeConverter(typeof(TradeHistoryAccountConverter))]
        [Display(Name = "Account Name", Description = "Select from connected accounts or type a custom name.", Order = 2, GroupName = GrpData)]
        public string AccountName { get; set; }
        #endregion
    }
}
