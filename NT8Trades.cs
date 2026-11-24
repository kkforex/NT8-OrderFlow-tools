// =====================================================================================
//  NT8Trades for NinjaTrader 8  —  aggressive/passive interaction classifier
// -------------------------------------------------------------------------------------
//  Re-implementation of the NT8Charts "NT8 Trades" concept on NT8 tick + L2 data.
//
//  Marker semantics (same as NT8Charts):
//    Hollow Circle  : large cluster built from many SMALL aggressive orders
//    Filled Circle  : large cluster containing BIG individual aggressive order(s)
//    Square         : cluster hit a BIG resting (passive) order / wall
//    Diamond        : BIG aggressive order hit directly into a BIG resting order ("battle")
//
//  NT8 has no public Market-By-Order feed, so the two sides are reconstructed:
//    * Aggressive order  = consecutive same-side prints sharing a timestamp (± tolerance)
//                          whose prices move monotonically (one sweep = one order).
//    * Big trade cluster = consecutive same-side aggressive orders inside the
//                          Aggregation Window.
//    * Passive size      = max of (a) peak resting depth at the hit price seen via
//                          OnMarketDepth during Depth Memory ms before the hit (real-time),
//                          and (b) volume executed at a single price inside the cluster
//                          (lower bound on resting liquidity; works on history/refills).
//
//  Requirements:
//    * Calculate = OnEachTick (set automatically).
//    * Historical markers: enable Tick Replay on the data series (bid/ask at print time).
//    * Squares/Diamonds from resting depth: Level 2 feed (e.g. Rithmic). Historical depth
//      is not available in NT8, so history uses the executed-at-level estimate only.
// =====================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// Enums live outside any namespace so NT8's generated wrapper code resolves them.
public enum NT8TradesFilterMode    { Manual, Automatic }
public enum NT8TradesIntensity     { Low, Medium, Strong }
public enum NT8TradesPassiveSource { DepthAndExecuted, DepthOnly, ExecutedOnly }
public enum NT8TradesAlertFilter   { AllMarkers, PassiveOnly, BattleOnly }
public enum NT8TradesMarkerType    { HollowCircle, FilledCircle, Square, Diamond }

namespace NinjaTrader.NinjaScript.Indicators
{
    public class NT8Trades : Indicator
    {
        #region Internal types
        private sealed class Marker
        {
            public int                  BarIndex;
            public DateTime             Time;
            public double               Price;
            public long                 Volume;     // cluster total (aggressive)
            public long                 MaxOrder;   // largest single aggressive order
            public long                 Passive;    // estimated passive size at hit level
            public int                  Side;       // +1 aggressive buy (ask), -1 aggressive sell (bid)
            public NT8TradesMarkerType Type;
            public float                Strength;   // 0..1 → size / opacity
        }

        private sealed class Cluster
        {
            public bool     Active;
            public int      Side, StartBar, StartTick, Orders, Prints;
            public DateTime StartTime, LastTime, OrderLastTime;
            public double   OrderLastPrice, PV;
            public long     Total, CurOrderVol, MaxOrder;
            public readonly Dictionary<int, long> VolAtTick   = new Dictionary<int, long>();
            public readonly Dictionary<int, long> DepthAtTick = new Dictionary<int, long>();

            public void Start(int side, int bar, DateTime t, int tick)
            {
                Active = true; Side = side; StartBar = bar; StartTick = tick;
                StartTime = LastTime = OrderLastTime = t;
                OrderLastPrice = 0; PV = 0; Total = CurOrderVol = MaxOrder = 0; Orders = Prints = 0;
                VolAtTick.Clear(); DepthAtTick.Clear();
            }

            public void CloseOrder()
            {
                if (CurOrderVol <= 0) return;
                if (CurOrderVol > MaxOrder) MaxOrder = CurOrderVol;
                Orders++;
                CurOrderVol = 0;
            }
        }

        private sealed class DepthLevel { public double Price; public long Volume; }
        private struct Peak { public long Volume; public DateTime Utc; }
        #endregion

        #region Fields
        private const string GroupData   = "1. Data Settings";
        private const string GroupPlot   = "2. Plot Settings";
        private const string GroupMarker = "3. Marker Settings";
        private const string GroupAlert  = "4. Alert Settings";
        private const string GroupEngine = "5. Reconstruction Engine (NT8)";

        private readonly object        markersLock = new object();
        private readonly List<Marker>  markers     = new List<Marker>();
        private readonly Cluster       cluster     = new Cluster();

        private readonly List<DepthLevel>    askBook  = new List<DepthLevel>();
        private readonly List<DepthLevel>    bidBook  = new List<DepthLevel>();
        private readonly Dictionary<int, Peak> askPeaks = new Dictionary<int, Peak>();
        private readonly Dictionary<int, Peak> bidPeaks = new Dictionary<int, Peak>();
        private long depthEvents;

        // Automatic-mode statistics (histograms of cluster totals and passive estimates)
        private readonly Dictionary<long, long> aggHist  = new Dictionary<long, long>();
        private readonly Dictionary<long, long> passHist = new Dictionary<long, long>();
        private long aggSamples, passSamples, samplesSinceRecalc;
        private long autoAggThr, autoPassThr;

        // Size scaling statistics (Welford on log volume of displayed markers)
        private long   scaleN;
        private double scaleMean, scaleM2;

        private double   tickSize;
        private DateTime cutoff = DateTime.MinValue;
        private DateTime lastPrintUtc;
        private double   lastTradePrice;
        private int      lastSide;
        private long     alertSeq;
        private bool     warnedTickReplay;

        // SharpDX resources
        private SharpDX.Direct2D1.Brush dxAsk, dxAskBattle, dxBid, dxBidBattle, dxOutline, dxText;
        private SharpDX.DirectWrite.TextFormat textFormat;
        #endregion

        #region State
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "NT8 Trades: large aggressive orders classified by the passive liquidity they hit (Hollow / Filled / Square / Diamond).";
                Name                     = "NT8Trades";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;   // keep collecting depth/prints when chart is hidden
                ScaleJustification       = ScaleJustification.Right;

                // Data Settings
                DaysToLoad          = 3;
                FilterMode          = NT8TradesFilterMode.Manual;
                AutomaticIntensity  = NT8TradesIntensity.Medium;
                ManualFilter        = 30;
                ManualPassiveFilter = 150;

                // Plot Settings
                StandardDev    = 2.0;
                MinimumOpacity = 35;
                MaximumOpacity = 90;
                AskColor       = MakeBrush(0, 200, 120);
                AskColorBattle = MakeBrush(0, 190, 255);
                BidColor       = MakeBrush(230, 50, 70);
                BidColorBattle = MakeBrush(255, 150, 0);

                // Marker Settings
                MinimumSize    = 8;
                MaximumSize    = 48;
                ShowVolumeText = true;

                // Alert Settings
                EnableAlertSound = false;
                BidAlert         = "Alert2.wav";
                AskAlert         = "Alert1.wav";
                AlertFilter      = NT8TradesAlertFilter.AllMarkers;

                // Engine
                OrderToleranceMs    = 0;
                AggregationWindowMs = 150;
                MaxClusterTicks     = 0;
                BigOrderPercent     = 50;
                PassiveSource       = NT8TradesPassiveSource.DepthAndExecuted;
                DepthMemoryMs       = 1500;
                UseTickRule         = false;
                AutoMinSamples      = 2000;
            }
            else if (State == State.DataLoaded)
            {
                tickSize = Instrument.MasterInstrument.TickSize;
                cutoff   = ComputeCutoff(DaysToLoad);
                lock (markersLock) markers.Clear();
                aggHist.Clear(); passHist.Clear();
                aggSamples = passSamples = samplesSinceRecalc = 0;
                autoAggThr = autoPassThr = 0;
                scaleN = 0; scaleMean = scaleM2 = 0;
                cluster.Active = false;
            }
            else if (State == State.Transition)
            {
                FinalizeCluster();   // flush last historical cluster
            }
            else if (State == State.Realtime)
            {
                askBook.Clear(); bidBook.Clear();
                askPeaks.Clear(); bidPeaks.Clear();
            }
            else if (State == State.Terminated)
            {
                DisposeDx();
            }
        }

        protected override void OnBarUpdate()
        {
            if (!warnedTickReplay && State == State.Historical && Bars != null && !Bars.IsTickReplay)
            {
                warnedTickReplay = true;
                Print("NT8Trades: Tick Replay is OFF — historical markers cannot be built. Enable Tick Replay in Data Series.");
                Draw.TextFixed(this, "NT8TradesTR", "NT8Trades: enable Tick Replay for historical markers", TextPosition.BottomLeft);
            }

            // Real-time flush when the market goes quiet
            if (State == State.Realtime && cluster.Active &&
                (DateTime.UtcNow - lastPrintUtc).TotalMilliseconds > AggregationWindowMs)
                FinalizeCluster();
        }
        #endregion

        #region Market data
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last)
            {
                // Quote updates arrive constantly in real-time → use them to close idle clusters
                if (State == State.Realtime && cluster.Active &&
                    (DateTime.UtcNow - lastPrintUtc).TotalMilliseconds > AggregationWindowMs)
                    FinalizeCluster();
                return;
            }

            if (e.Volume <= 0) return;

            int side = Classify(e.Price, e.Bid, e.Ask);
            if (side == 0) return;

            ProcessPrint(e.Price, e.Volume, e.Time, side);
        }

        private int Classify(double price, double bid, double ask)
        {
            int side = 0;
            if (ask > 0 && price >= ask)      side = 1;
            else if (bid > 0 && price <= bid) side = -1;
            else if (UseTickRule)
            {
                if (price > lastTradePrice)      side = 1;
                else if (price < lastTradePrice) side = -1;
                else                             side = lastSide;
            }
            lastTradePrice = price;
            if (side != 0) lastSide = side;
            return side;
        }

        private void ProcessPrint(double price, long vol, DateTime time, int side)
        {
            int tick = ToTicks(price);

            if (cluster.Active)
            {
                double gap       = (time - cluster.LastTime).TotalMilliseconds;
                bool   outOfRange = MaxClusterTicks > 0 && Math.Abs(tick - cluster.StartTick) > MaxClusterTicks;
                if (side != cluster.Side || gap > AggregationWindowMs || gap < 0 || outOfRange)
                    FinalizeCluster();
            }

            if (!cluster.Active)
                cluster.Start(side, CurrentBar, time, tick);

            // --- aggressive order reconstruction (one sweep = same timestamp, monotonic price)
            if (cluster.CurOrderVol > 0)
            {
                double og       = (time - cluster.OrderLastTime).TotalMilliseconds;
                bool   reversed = side > 0 ? price < cluster.OrderLastPrice : price > cluster.OrderLastPrice;
                if (og > OrderToleranceMs || reversed)
                    cluster.CloseOrder();
            }
            cluster.CurOrderVol   += vol;
            cluster.OrderLastTime  = time;
            cluster.OrderLastPrice = price;

            // --- cluster accumulation
            cluster.Total   += vol;
            cluster.PV      += price * vol;
            cluster.Prints++;
            cluster.LastTime = time;

            long v;
            cluster.VolAtTick.TryGetValue(tick, out v);
            cluster.VolAtTick[tick] = v + vol;

            // --- passive side from the book (real-time only; NT8 stores no historical depth)
            if (State == State.Realtime && PassiveSource != NT8TradesPassiveSource.ExecutedOnly)
            {
                long depth = LookupDepth(side, tick);
                long prev;
                if (!cluster.DepthAtTick.TryGetValue(tick, out prev) || depth > prev)
                    cluster.DepthAtTick[tick] = depth;
            }

            lastPrintUtc = DateTime.UtcNow;
        }

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            List<DepthLevel> book;
            Dictionary<int, Peak> peaks;
            if (e.MarketDataType == MarketDataType.Ask)      { book = askBook; peaks = askPeaks; }
            else if (e.MarketDataType == MarketDataType.Bid) { book = bidBook; peaks = bidPeaks; }
            else return;

            switch (e.Operation)
            {
                case Operation.Add:
                    if (e.Position <= book.Count)
                        book.Insert(e.Position, new DepthLevel { Price = e.Price, Volume = e.Volume });
                    break;
                case Operation.Update:
                    if (e.Position < book.Count)
                    {
                        book[e.Position].Price  = e.Price;
                        book[e.Position].Volume = e.Volume;
                    }
                    break;
                case Operation.Remove:
                    if (e.Position < book.Count)
                        book.RemoveAt(e.Position);
                    break;
            }

            // Track the recent PEAK resting size per price, so the wall is still known
            // after the trade has eaten it and the level was decremented/removed.
            if (e.Operation != Operation.Remove && e.Volume > 0)
            {
                int      tick = ToTicks(e.Price);
                DateTime now  = DateTime.UtcNow;
                Peak     p;
                if (!peaks.TryGetValue(tick, out p) || e.Volume >= p.Volume ||
                    (now - p.Utc).TotalMilliseconds > DepthMemoryMs)
                    peaks[tick] = new Peak { Volume = e.Volume, Utc = now };
            }

            if (++depthEvents % 20000 == 0) { PrunePeaks(askPeaks); PrunePeaks(bidPeaks); }
        }

        private long LookupDepth(int aggressorSide, int tick)
        {
            // Aggressive buyers lift the ask book; aggressive sellers hit the bid book.
            var  peaks = aggressorSide > 0 ? askPeaks : bidPeaks;
            var  book  = aggressorSide > 0 ? askBook  : bidBook;
            long v     = 0;

            Peak p;
            if (peaks.TryGetValue(tick, out p) && (DateTime.UtcNow - p.Utc).TotalMilliseconds <= DepthMemoryMs)
                v = p.Volume;

            for (int i = 0; i < book.Count; i++)
                if (ToTicks(book[i].Price) == tick && book[i].Volume > v)
                    v = book[i].Volume;

            return v;
        }

        private void PrunePeaks(Dictionary<int, Peak> peaks)
        {
            DateTime now  = DateTime.UtcNow;
            var      dead = peaks.Where(kv => (now - kv.Value.Utc).TotalMilliseconds > DepthMemoryMs * 4)
                                 .Select(kv => kv.Key).ToList();
            foreach (int k in dead) peaks.Remove(k);
        }
        #endregion

        #region Classification
        private void FinalizeCluster()
        {
            if (!cluster.Active) return;
            cluster.Active = false;
            cluster.CloseOrder();
            if (cluster.Total <= 0) return;

            // Executed-at-level: biggest single-price fill inside the cluster
            long execMax = 0; int execTick = cluster.StartTick;
            foreach (var kv in cluster.VolAtTick)
                if (kv.Value > execMax) { execMax = kv.Value; execTick = kv.Key; }

            // Resting depth seen at hit levels
            long depthMax = 0; int depthTick = cluster.StartTick;
            foreach (var kv in cluster.DepthAtTick)
                if (kv.Value > depthMax) { depthMax = kv.Value; depthTick = kv.Key; }

            long passive; int passTick;
            switch (PassiveSource)
            {
                case NT8TradesPassiveSource.DepthOnly:    passive = depthMax; passTick = depthTick; break;
                case NT8TradesPassiveSource.ExecutedOnly: passive = execMax;  passTick = execTick;  break;
                default:
                    if (depthMax >= execMax) { passive = depthMax; passTick = depthTick; }
                    else                     { passive = execMax;  passTick = execTick;  }
                    break;
            }

            UpdateStats(cluster.Total, passive);

            long aggThr, passThr;
            GetThresholds(out aggThr, out passThr);

            if (cluster.Total < aggThr) return;
            if (cluster.StartTime < cutoff) return;

            long bigOrderThr = Math.Max(1, (long)Math.Ceiling(aggThr * BigOrderPercent / 100.0));
            bool bigAgg  = cluster.MaxOrder >= bigOrderThr;
            bool bigPass = passive >= passThr;

            NT8TradesMarkerType type =
                bigAgg && bigPass ? NT8TradesMarkerType.Diamond :
                bigPass           ? NT8TradesMarkerType.Square  :
                bigAgg            ? NT8TradesMarkerType.FilledCircle :
                                    NT8TradesMarkerType.HollowCircle;

            // Passive interactions are anchored on the wall's price, the rest on cluster VWAP
            double price = bigPass ? passTick * tickSize : cluster.PV / cluster.Total;

            // Size / opacity scaling: z-score of log(volume) above the threshold, in StdDev units
            double lv = Math.Log(cluster.Total);
            double sd = scaleN >= 20 ? Math.Sqrt(scaleM2 / (scaleN - 1)) : 0.5;
            sd = Math.Max(sd, 0.05);
            double t = (lv - Math.Log(Math.Max(1, aggThr))) / (StandardDev * sd);
            float strength = (float)Math.Max(0.0, Math.Min(1.0, t));

            scaleN++;
            double d = lv - scaleMean;
            scaleMean += d / scaleN;
            scaleM2   += d * (lv - scaleMean);

            var m = new Marker
            {
                BarIndex = cluster.StartBar,
                Time     = cluster.StartTime,
                Price    = price,
                Volume   = cluster.Total,
                MaxOrder = cluster.MaxOrder,
                Passive  = passive,
                Side     = cluster.Side,
                Type     = type,
                Strength = strength
            };

            lock (markersLock) markers.Add(m);

            if (State == State.Realtime)
            {
                FireAlert(m);
                ForceRefresh();
            }
        }

        private void UpdateStats(long total, long passive)
        {
            if (FilterMode != NT8TradesFilterMode.Automatic) return;

            long c;
            aggHist.TryGetValue(total, out c); aggHist[total] = c + 1; aggSamples++;
            if (passive > 0) { passHist.TryGetValue(passive, out c); passHist[passive] = c + 1; passSamples++; }

            if (++samplesSinceRecalc >= 250 || autoAggThr == 0)
            {
                samplesSinceRecalc = 0;
                double p = IntensityPercentile();
                if (aggSamples  >= AutoMinSamples) autoAggThr  = Math.Max(2, Percentile(aggHist,  aggSamples,  p));
                if (passSamples >= AutoMinSamples) autoPassThr = Math.Max(2, Percentile(passHist, passSamples, p));
            }
        }

        private void GetThresholds(out long aggThr, out long passThr)
        {
            aggThr  = ManualFilter;
            passThr = ManualPassiveFilter;
            if (FilterMode == NT8TradesFilterMode.Automatic)
            {
                if (autoAggThr  > 0) aggThr  = autoAggThr;
                if (autoPassThr > 0) passThr = autoPassThr;
            }
        }

        private double IntensityPercentile()
        {
            switch (AutomaticIntensity)
            {
                case NT8TradesIntensity.Low:    return 0.985;
                case NT8TradesIntensity.Strong: return 0.999;
                default:                         return 0.995;
            }
        }

        private static long Percentile(Dictionary<long, long> hist, long n, double p)
        {
            var  keys   = hist.Keys.ToList(); keys.Sort();
            long target = (long)Math.Ceiling(p * n), cum = 0;
            foreach (long k in keys) { cum += hist[k]; if (cum >= target) return k; }
            return keys.Count > 0 ? keys[keys.Count - 1] : 0;
        }

        private void FireAlert(Marker m)
        {
            if (!EnableAlertSound) return;
            if (AlertFilter == NT8TradesAlertFilter.BattleOnly && m.Type != NT8TradesMarkerType.Diamond) return;
            if (AlertFilter == NT8TradesAlertFilter.PassiveOnly &&
                m.Type != NT8TradesMarkerType.Diamond && m.Type != NT8TradesMarkerType.Square) return;

            string file = m.Side > 0 ? AskAlert : BidAlert;
            if (string.IsNullOrWhiteSpace(file)) return;
            string path = System.IO.Path.IsPathRooted(file)
                ? file
                : System.IO.Path.Combine(NinjaTrader.Core.Globals.InstallDir, "sounds", file);

            string msg = string.Format("NT8Trades {0} {1} {2} @ {3} (max order {4}, passive {5})",
                m.Side > 0 ? "ASK" : "BID", m.Type, m.Volume,
                Instrument.MasterInstrument.FormatPrice(m.Price), m.MaxOrder, m.Passive);

            Alert("NT8Trades" + (alertSeq++), Priority.Medium, msg, path, 0,
                  m.Side > 0 ? AskColor : BidColor, Brushes.White);
        }
        #endregion

        #region Rendering
        public override void OnRenderTargetChanged()
        {
            DisposeDx();
            if (RenderTarget == null) return;

            dxAsk       = AskColor.ToDxBrush(RenderTarget);
            dxAskBattle = AskColorBattle.ToDxBrush(RenderTarget);
            dxBid       = BidColor.ToDxBrush(RenderTarget);
            dxBidBattle = BidColorBattle.ToDxBrush(RenderTarget);
            dxOutline   = Brushes.Black.ToDxBrush(RenderTarget);
            dxText      = Brushes.White.ToDxBrush(RenderTarget);

            textFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
                SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 10f)
            {
                TextAlignment      = SharpDX.DirectWrite.TextAlignment.Center,
                ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center,
                WordWrapping       = SharpDX.DirectWrite.WordWrapping.NoWrap
            };
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (ChartBars == null || RenderTarget == null || dxAsk == null) return;

            int   from   = ChartBars.FromIndex, to = ChartBars.ToIndex;
            float top    = ChartPanel.Y, bottom = ChartPanel.Y + ChartPanel.H;
            var   oldAA  = RenderTarget.AntialiasMode;
            RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;

            lock (markersLock)
            {
                for (int i = LowerBound(from); i < markers.Count; i++)
                {
                    Marker m = markers[i];
                    if (m.BarIndex > to) break;
                    if (m.BarIndex < from) continue;

                    float x = chartControl.GetXByBarIndex(ChartBars, m.BarIndex);
                    float y = chartScale.GetYByValue(m.Price);
                    float d = (float)(MinimumSize + m.Strength * (MaximumSize - MinimumSize));
                    if (y < top - d || y > bottom + d) continue;

                    DrawMarker(m, x, y, d);
                }
            }

            RenderTarget.AntialiasMode = oldAA;
        }

        private void DrawMarker(Marker m, float x, float y, float d)
        {
            float r  = d / 2f;
            float op = (float)((MinimumOpacity + m.Strength * (MaximumOpacity - MinimumOpacity)) / 100.0);

            SharpDX.Direct2D1.Brush b = m.Type == NT8TradesMarkerType.Diamond
                ? (m.Side > 0 ? dxAskBattle : dxBidBattle)
                : (m.Side > 0 ? dxAsk : dxBid);

            dxOutline.Opacity = op;
            var center = new SharpDX.Vector2(x, y);

            switch (m.Type)
            {
                case NT8TradesMarkerType.HollowCircle:
                {
                    var el = new SharpDX.Direct2D1.Ellipse(center, r, r);
                    b.Opacity = op * 0.2f;
                    RenderTarget.FillEllipse(el, b);
                    b.Opacity = op;
                    RenderTarget.DrawEllipse(el, b, Math.Max(1.5f, d * 0.12f));
                    break;
                }
                case NT8TradesMarkerType.FilledCircle:
                {
                    var el = new SharpDX.Direct2D1.Ellipse(center, r, r);
                    b.Opacity = op;
                    RenderTarget.FillEllipse(el, b);
                    RenderTarget.DrawEllipse(el, dxOutline, 1f);
                    break;
                }
                case NT8TradesMarkerType.Square:
                {
                    var rect = new SharpDX.RectangleF(x - r, y - r, d, d);
                    b.Opacity = op;
                    RenderTarget.FillRectangle(rect, b);
                    RenderTarget.DrawRectangle(rect, dxOutline, 1f);
                    break;
                }
                case NT8TradesMarkerType.Diamond:
                {
                    float s    = d * 0.78f;   // rotated square of equal visual height
                    var   rect = new SharpDX.RectangleF(x - s / 2f, y - s / 2f, s, s);
                    var   old  = RenderTarget.Transform;
                    RenderTarget.Transform = SharpDX.Matrix3x2.Rotation((float)(Math.PI / 4.0), center) * old;
                    b.Opacity = op;
                    RenderTarget.FillRectangle(rect, b);
                    RenderTarget.DrawRectangle(rect, dxOutline, 1.2f);
                    RenderTarget.Transform = old;
                    break;
                }
            }

            if (ShowVolumeText && d >= 18f)
            {
                dxText.Opacity = 1f;
                RenderTarget.DrawText(FormatVol(m.Volume), textFormat,
                    new SharpDX.RectangleF(x - r - 12f, y - r, d + 24f, d), dxText);
            }
        }

        private int LowerBound(int barIndex)
        {
            int lo = 0, hi = markers.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (markers[mid].BarIndex < barIndex) lo = mid + 1; else hi = mid;
            }
            return Math.Max(0, lo - 1);   // tolerate tiny out-of-order inserts at bar boundaries
        }

        private void DisposeDx()
        {
            if (dxAsk       != null) { dxAsk.Dispose();       dxAsk       = null; }
            if (dxAskBattle != null) { dxAskBattle.Dispose(); dxAskBattle = null; }
            if (dxBid       != null) { dxBid.Dispose();       dxBid       = null; }
            if (dxBidBattle != null) { dxBidBattle.Dispose(); dxBidBattle = null; }
            if (dxOutline   != null) { dxOutline.Dispose();   dxOutline   = null; }
            if (dxText      != null) { dxText.Dispose();      dxText      = null; }
            if (textFormat  != null) { textFormat.Dispose();  textFormat  = null; }
        }
        #endregion

        #region Helpers
        private int ToTicks(double price) { return (int)Math.Round(price / tickSize); }

        private static string FormatVol(long v)
        {
            return v >= 10000 ? (v / 1000.0).ToString("0") + "K"
                 : v >= 1000  ? (v / 1000.0).ToString("0.0") + "K"
                 : v.ToString();
        }

        private static SolidColorBrush MakeBrush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        // Days To Load counted in weekdays; the extra 12h keeps the prior-evening Globex open.
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
        // ---------------- 1. Data Settings ----------------
        [Range(0, 365)]
        [Display(Name = "Days to Load", Description = "Weekdays of markers to display (0 = everything loaded). Historical markers require Tick Replay.", Order = 1, GroupName = GroupData)]
        public int DaysToLoad { get; set; }

        [Display(Name = "Filter Mode", Description = "Manual: fixed thresholds. Automatic: thresholds from the instrument's own trade-size distribution (Big Trades Analysis equivalent).", Order = 2, GroupName = GroupData)]
        public NT8TradesFilterMode FilterMode { get; set; }

        [Display(Name = "Automatic - Intensity", Description = "Low ≈ top 1.5%, Medium ≈ top 0.5%, Strong ≈ top 0.1% of clusters / passive sizes.", Order = 3, GroupName = GroupData)]
        public NT8TradesIntensity AutomaticIntensity { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "Manual - Filter", Description = "Minimum aggressive cluster size (contracts) to display.", Order = 4, GroupName = GroupData)]
        public int ManualFilter { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "Manual - Passive Filter", Description = "Minimum resting size (contracts) at the hit level to count as a big passive order (Square / Diamond).", Order = 5, GroupName = GroupData)]
        public int ManualPassiveFilter { get; set; }

        // ---------------- 2. Plot Settings ----------------
        [Range(0.1, 10.0)]
        [Display(Name = "Standard Dev.", Description = "How many standard deviations of log-volume above the filter map to the maximum size/opacity.", Order = 1, GroupName = GroupPlot)]
        public double StandardDev { get; set; }

        [Range(0, 100)]
        [Display(Name = "Minimum Opacity", Order = 2, GroupName = GroupPlot)]
        public int MinimumOpacity { get; set; }

        [Range(0, 100)]
        [Display(Name = "Maximum Opacity", Order = 3, GroupName = GroupPlot)]
        public int MaximumOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Ask Color", Description = "Aggressive buyers (lifting the ask).", Order = 4, GroupName = GroupPlot)]
        public Brush AskColor { get; set; }
        [Browsable(false)]
        public string AskColorSerializable { get { return Serialize.BrushToString(AskColor); } set { AskColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Ask Color Battle", Description = "Ask-side Diamond: big aggressive buy into a big resting offer.", Order = 5, GroupName = GroupPlot)]
        public Brush AskColorBattle { get; set; }
        [Browsable(false)]
        public string AskColorBattleSerializable { get { return Serialize.BrushToString(AskColorBattle); } set { AskColorBattle = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Bid Color", Description = "Aggressive sellers (hitting the bid).", Order = 6, GroupName = GroupPlot)]
        public Brush BidColor { get; set; }
        [Browsable(false)]
        public string BidColorSerializable { get { return Serialize.BrushToString(BidColor); } set { BidColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Bid Color Battle", Description = "Bid-side Diamond: big aggressive sell into a big resting bid.", Order = 7, GroupName = GroupPlot)]
        public Brush BidColorBattle { get; set; }
        [Browsable(false)]
        public string BidColorBattleSerializable { get { return Serialize.BrushToString(BidColorBattle); } set { BidColorBattle = Serialize.StringToBrush(value); } }

        // ---------------- 3. Marker Settings ----------------
        [Range(2, 200)]
        [Display(Name = "Minimum Size", Description = "Smallest marker diameter (px).", Order = 1, GroupName = GroupMarker)]
        public int MinimumSize { get; set; }

        [Range(2, 300)]
        [Display(Name = "Maximum Size", Description = "Largest marker diameter (px).", Order = 2, GroupName = GroupMarker)]
        public int MaximumSize { get; set; }

        [Display(Name = "Show Volume Text", Description = "Print cluster volume inside markers large enough to hold it.", Order = 3, GroupName = GroupMarker)]
        public bool ShowVolumeText { get; set; }

        // ---------------- 4. Alert Settings ----------------
        [Display(Name = "Enable Alert Sound", Order = 1, GroupName = GroupAlert)]
        public bool EnableAlertSound { get; set; }

        [Display(Name = "Bid Alert", Description = "Sound file (NinjaTrader 8\\sounds folder or full path) for aggressive sellers.", Order = 2, GroupName = GroupAlert)]
        public string BidAlert { get; set; }

        [Display(Name = "Ask Alert", Description = "Sound file (NinjaTrader 8\\sounds folder or full path) for aggressive buyers.", Order = 3, GroupName = GroupAlert)]
        public string AskAlert { get; set; }

        [Display(Name = "Alert On", Description = "All markers, only passive interactions (Square + Diamond), or only battles (Diamond).", Order = 4, GroupName = GroupAlert)]
        public NT8TradesAlertFilter AlertFilter { get; set; }

        // ---------------- 5. Reconstruction Engine ----------------
        [Range(0, 1000)]
        [Display(Name = "Order Time Tolerance (ms)", Description = "Prints within this time of each other (same side, monotonic price) are one aggressive order. 0 = identical timestamp.", Order = 1, GroupName = GroupEngine)]
        public int OrderToleranceMs { get; set; }

        [Range(1, 10000)]
        [Display(Name = "Aggregation Window (ms)", Description = "Max gap between same-side aggressive orders merged into one big-trade cluster.", Order = 2, GroupName = GroupEngine)]
        public int AggregationWindowMs { get; set; }

        [Range(0, 1000)]
        [Display(Name = "Max Cluster Range (ticks)", Description = "Split a cluster if price travels further than this from its first print. 0 = unlimited.", Order = 3, GroupName = GroupEngine)]
        public int MaxClusterTicks { get; set; }

        [Range(1, 100)]
        [Display(Name = "Big Order % of Filter", Description = "A single aggressive order ≥ this % of the filter makes the cluster 'big individual' (Filled / Diamond).", Order = 4, GroupName = GroupEngine)]
        public int BigOrderPercent { get; set; }

        [Display(Name = "Passive Source", Description = "Depth = peak resting size from Level 2 (real-time). Executed = largest single-price fill in the cluster (works on history, catches refills/icebergs).", Order = 5, GroupName = GroupEngine)]
        public NT8TradesPassiveSource PassiveSource { get; set; }

        [Range(50, 60000)]
        [Display(Name = "Depth Memory (ms)", Description = "How long a resting peak size is remembered after the book updates.", Order = 6, GroupName = GroupEngine)]
        public int DepthMemoryMs { get; set; }

        [Display(Name = "Tick Rule For Mid Prints", Description = "Classify prints between bid and ask by tick direction instead of skipping them.", Order = 7, GroupName = GroupEngine)]
        public bool UseTickRule { get; set; }

        [Range(100, int.MaxValue)]
        [Display(Name = "Automatic - Min Samples", Description = "Clusters collected before Automatic thresholds replace the Manual values.", Order = 8, GroupName = GroupEngine)]
        public int AutoMinSamples { get; set; }
        #endregion
    }
}
