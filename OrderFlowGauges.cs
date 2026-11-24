#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using OrderFlowGaugesTypes;
using D2D = SharpDX.Direct2D1;
using DW  = SharpDX.DirectWrite;
#endregion

// ============================================================================
//  OrderFlowGauges  —  on-chart speed / volume gauges for NinjaTrader 8
//
//  Gauges (each toggleable):
//    • Tick Speed  – prints in a rolling time window (optionally per second)
//    • Bid Volume  – volume traded at/below the bid in the window
//    • Ask Volume  – volume traded at/above the ask in the window
//    • Delta       – Ask Vol − Bid Vol (bipolar, centred needle)
//    • Bid|Ask     – optional dual gauge replacing the two separate ones
//
//  Engine:  OnMarketData(Last) aggressor classification vs e.Bid/e.Ask at print
//           time → growable ring buffer → O(1) running sums for two windows.
//           A DispatcherTimer expires the window when the tape is quiet and
//           drives repaint (render rate is capped, not per-tick).
//  Live only: gauges ignore historical / Tick-Replay data by design.
// ============================================================================

namespace OrderFlowGaugesTypes
{
    public enum OFGMidPrintMode { Ignore, Split, LastSide }
    public enum OFGAnchor       { TopLeft, TopRight, BottomLeft, BottomRight }
    public enum OFGLayout       { Horizontal, Vertical }
    public enum OFGBidAskStyle  { Separate, Dual }
    public enum OFGScaleMode    { Auto, Fixed }
}

namespace NinjaTrader.NinjaScript.Indicators
{
    public class OrderFlowGauges : Indicator
    {
        #region Internal types
        private enum GaugeKind { TickSpeed, BidVol, AskVol, BidAskDual, Delta }

        private sealed class RollingWindow
        {
            public long   SpanTicks;
            public long   TailSeq;
            public double Count, Bid, Ask;
        }

        private sealed class ScaleTracker
        {
            private readonly double[] buf;
            private readonly double[] work;
            private int idx, count;
            public double Max;

            public ScaleTracker(int size, double initial)
            {
                buf  = new double[Math.Max(16, size)];
                work = new double[buf.Length];
                Max  = initial;
            }

            public void Add(double v)
            {
                buf[idx] = v;
                idx = (idx + 1) % buf.Length;
                if (count < buf.Length) count++;
            }

            // Rolling percentile with fast-rise / slow-fall smoothing so one spike
            // doesn't flatten the gauge and a quiet spell doesn't make it twitchy.
            public void Recompute(double pct, int warmupSamples, double floor)
            {
                if (count == 0) { Max = Math.Max(Max, floor); return; }
                Array.Copy(buf, work, count);
                Array.Sort(work, 0, count);

                double p;
                if (count < warmupSamples)
                    p = work[count - 1];                         // warm-up: max seen so far
                else
                {
                    int k = (int)Math.Ceiling(pct / 100.0 * count) - 1;
                    k = Math.Max(0, Math.Min(count - 1, k));
                    p = work[k];
                }

                double target = Math.Max(floor, p * 1.10);       // 10 % headroom
                double rate   = target > Max ? 0.50 : 0.15;
                Max += (target - Max) * rate;
            }
        }

        private sealed class GaugeState
        {
            public double   Raw, Disp, PeakHi, PeakLo;
            public DateTime PeakHiTime, PeakLoTime;
            public double[] Hist;
            public int      HistIdx, HistCount;
            public ScaleTracker Scale;
        }

        private sealed class GaugeVisual
        {
            public float  Kx = float.NaN, Ky, Kr, Kt;
            public bool   Kbip;
            public D2D.PathGeometry Track;
            public readonly List<KeyValuePair<D2D.PathGeometry, int>> Zones = new List<KeyValuePair<D2D.PathGeometry, int>>();

            public void Dispose()
            {
                if (Track != null) { Track.Dispose(); Track = null; }
                foreach (var z in Zones) z.Key.Dispose();
                Zones.Clear();
                Kx = float.NaN;
            }
        }
        #endregion

        #region Engine fields
        private readonly object sync = new object();
        private long[]   bufTime  = new long[4096];
        private int[]    bufCount = new int[4096];
        private double[] bufBid   = new double[4096];
        private double[] bufAsk   = new double[4096];
        private long     headSeq;
        private RollingWindow tickWin, volWin;

        private long     lastPrintTimeTicks = long.MinValue;
        private double   lastPrintPrice;
        private int      lastPrintSide;          // +1 ask, -1 bid, 0 mid
        private int      lastAggressorSide;      // for LastSide mid-print mode
        private DateTime lastExchTime = DateTime.MinValue;
        private DateTime lastLocalUtc;
        private volatile bool hasLive;
        #endregion

        #region UI-side fields
        private DispatcherTimer timer;
        private Dispatcher      timerDispatcher;
        private GaugeState gTick, gBid, gAsk, gDelta;
        private ScaleTracker scaleTick, scaleVol, scaleDelta;
        private int scaleCounter, scaleRecalcEvery, warmupSamples;

        private readonly List<GaugeKind>   gauges  = new List<GaugeKind>();
        private readonly List<GaugeVisual> visuals = new List<GaugeVisual>();

        private bool tickAlertArmed = true, deltaUpArmed = true, deltaDnArmed = true;

        // SharpDX resources
        private D2D.Brush dxBg, dxText, dxTrack, dxNormal, dxElevated, dxExtreme, dxBid, dxAsk, dxNeedle, dxHistory;
        private DW.TextFormat fmtValue, fmtLabel, fmtSmall;
        private float fmtForSize = -1f;
        #endregion

        #region Lifecycle
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                 = "On-chart gauges: tick speed, bid volume, ask volume and delta over rolling time windows (live data).";
                Name                        = "OrderFlowGauges";
                Calculate                   = Calculate.OnEachTick;
                IsOverlay                   = true;
                DisplayInDataBox            = false;
                DrawOnPricePanel            = true;
                PaintPriceMarkers           = false;
                IsSuspendedWhileInactive    = false;
                ScaleJustification          = ScaleJustification.Right;

                // Data
                TickIntervalSec       = 3;
                VolIntervalSec        = 10;
                TickSpeedPerSecond    = true;
                MidPrintMode          = OFGMidPrintMode.Split;
                MergeBundledPrints    = false;
                RefreshMs             = 250;

                // Gauges
                ShowTickSpeed   = true;
                ShowBidVol      = true;
                ShowAskVol      = true;
                ShowDelta       = true;
                BidAskStyle     = OFGBidAskStyle.Separate;
                ShowHistory     = true;
                HistoryLength   = 120;
                PeakHoldSec     = 3;
                NeedleSmoothing = 0.35;

                // Scale
                ScaleMode            = OFGScaleMode.Auto;
                AutoScalePercentile  = 97;
                AutoScaleLookbackMin = 15;
                FixedMaxTicks        = 50;
                FixedMaxVolume       = 500;
                FixedMaxDelta        = 300;
                ElevatedPct          = 50;
                ExtremePct           = 80;

                // Layout
                GaugeAnchor       = OFGAnchor.TopRight;
                GaugeLayout       = OFGLayout.Horizontal;
                GaugeSize         = 150;
                OffsetX           = 10;
                OffsetY           = 10;
                Spacing           = 8;
                BackgroundOpacity = 60;

                // Colors
                BackgroundBrush   = Brushes.Black;
                TextBrush         = Brushes.WhiteSmoke;
                TrackBrush        = Brushes.DimGray;
                ZoneNormalBrush   = Brushes.SeaGreen;
                ZoneElevatedBrush = Brushes.Goldenrod;
                ZoneExtremeBrush  = Brushes.Crimson;
                BidBrush          = Brushes.IndianRed;
                AskBrush          = Brushes.MediumSeaGreen;
                NeedleBrush       = Brushes.White;
                HistoryBrush      = Brushes.DeepSkyBlue;

                // Alerts
                EnableAlerts        = false;
                AlertTickThreshold  = 0;
                AlertDeltaThreshold = 0;
                AlertRearmSec       = 30;
                AlertSound          = "Alert2.wav";
            }
            else if (State == State.DataLoaded)
            {
                tickWin = new RollingWindow { SpanTicks = TimeSpan.FromSeconds(TickIntervalSec).Ticks };
                volWin  = new RollingWindow { SpanTicks = TimeSpan.FromSeconds(VolIntervalSec).Ticks };

                int perSec        = Math.Max(1, 1000 / RefreshMs);
                int perLookback   = Math.Max(16, AutoScaleLookbackMin * 60 * perSec);
                scaleRecalcEvery  = perSec;               // ~1 s
                warmupSamples     = 30 * perSec;          // ~30 s

                bool fixedMode = ScaleMode == OFGScaleMode.Fixed;
                scaleTick  = new ScaleTracker(perLookback,     fixedMode ? FixedMaxTicks  : 1);
                scaleVol   = new ScaleTracker(perLookback * 2, fixedMode ? FixedMaxVolume : 10);
                scaleDelta = new ScaleTracker(perLookback,     fixedMode ? FixedMaxDelta  : 10);

                gTick  = NewState(scaleTick);
                gBid   = NewState(scaleVol);
                gAsk   = NewState(scaleVol);
                gDelta = NewState(scaleDelta);

                gauges.Clear();
                if (ShowTickSpeed) gauges.Add(GaugeKind.TickSpeed);
                if (BidAskStyle == OFGBidAskStyle.Dual)
                {
                    if (ShowBidVol || ShowAskVol) gauges.Add(GaugeKind.BidAskDual);
                }
                else
                {
                    if (ShowBidVol) gauges.Add(GaugeKind.BidVol);
                    if (ShowAskVol) gauges.Add(GaugeKind.AskVol);
                }
                if (ShowDelta) gauges.Add(GaugeKind.Delta);

                foreach (var v in visuals) v.Dispose();
                visuals.Clear();
                for (int i = 0; i < gauges.Count; i++) visuals.Add(new GaugeVisual());
            }
            else if (State == State.Historical)
            {
                if (ChartControl != null && timer == null)
                {
                    timerDispatcher = ChartControl.Dispatcher;
                    timerDispatcher.InvokeAsync(() =>
                    {
                        timer = new DispatcherTimer(DispatcherPriority.Background, timerDispatcher)
                        {
                            Interval = TimeSpan.FromMilliseconds(RefreshMs)
                        };
                        timer.Tick += OnTimerTick;
                        timer.Start();
                    });
                }
            }
            else if (State == State.Terminated)
            {
                if (timer != null && timerDispatcher != null)
                {
                    var t = timer;
                    timer = null;
                    timerDispatcher.InvokeAsync(() => { t.Stop(); t.Tick -= OnTimerTick; });
                }
                DisposeDx();
                DisposeFormats();
                foreach (var v in visuals) v.Dispose();
                visuals.Clear();
            }
        }

        private GaugeState NewState(ScaleTracker s)
        {
            return new GaugeState { Scale = s, Hist = new double[Math.Max(2, HistoryLength)] };
        }

        protected override void OnBarUpdate() { }
        #endregion

        #region Engine (data thread)
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last || State != State.Realtime || tickWin == null)
                return;

            long vol = e.Volume;
            if (vol <= 0) return;

            double price = e.Price, bid = e.Bid, ask = e.Ask;

            int side;
            if (ask > 0 && price >= ask)      side = 1;
            else if (bid > 0 && price <= bid) side = -1;
            else                              side = 0;
            if (side != 0) lastAggressorSide = side;

            double bv = 0, av = 0;
            if (side > 0)      av = vol;
            else if (side < 0) bv = vol;
            else
            {
                switch (MidPrintMode)
                {
                    case OFGMidPrintMode.Split:
                        bv = av = vol * 0.5;
                        break;
                    case OFGMidPrintMode.LastSide:
                        if (lastAggressorSide > 0) av = vol;
                        else if (lastAggressorSide < 0) bv = vol;
                        break;
                    // Ignore: counts as a tick, contributes no bid/ask volume
                }
            }

            lock (sync)
            {
                long t = e.Time.Ticks;
                lastExchTime = e.Time;
                lastLocalUtc = DateTime.UtcNow;
                hasLive      = true;

                ExpireLocked(t);

                bool merge = MergeBundledPrints && headSeq > 0
                             && t == lastPrintTimeTicks && price == lastPrintPrice && side == lastPrintSide;

                if (merge) MergeIntoLastLocked(bv, av);
                else       AddPrintLocked(t, 1, bv, av);

                lastPrintTimeTicks = t;
                lastPrintPrice     = price;
                lastPrintSide      = side;
            }
        }

        private void AddPrintLocked(long t, int count, double bid, double ask)
        {
            EnsureCapacityLocked();
            int i = (int)(headSeq % bufTime.Length);
            bufTime[i] = t; bufCount[i] = count; bufBid[i] = bid; bufAsk[i] = ask;
            headSeq++;

            tickWin.Count += count; tickWin.Bid += bid; tickWin.Ask += ask;
            volWin.Count  += count; volWin.Bid  += bid; volWin.Ask  += ask;
        }

        private void MergeIntoLastLocked(double bid, double ask)
        {
            long s = headSeq - 1;
            int  i = (int)(s % bufTime.Length);
            bufBid[i] += bid; bufAsk[i] += ask;
            if (tickWin.TailSeq <= s) { tickWin.Bid += bid; tickWin.Ask += ask; }
            if (volWin.TailSeq  <= s) { volWin.Bid  += bid; volWin.Ask  += ask; }
        }

        private void EnsureCapacityLocked()
        {
            long oldest = Math.Min(tickWin.TailSeq, volWin.TailSeq);
            int  cap    = bufTime.Length;
            if (headSeq - oldest < cap) return;

            int newCap = cap * 2;
            var nt = new long[newCap]; var nc = new int[newCap]; var nb = new double[newCap]; var na = new double[newCap];
            for (long s = oldest; s < headSeq; s++)
            {
                int o = (int)(s % cap), n = (int)(s % newCap);
                nt[n] = bufTime[o]; nc[n] = bufCount[o]; nb[n] = bufBid[o]; na[n] = bufAsk[o];
            }
            bufTime = nt; bufCount = nc; bufBid = nb; bufAsk = na;
        }

        private void ExpireLocked(long nowTicks)
        {
            ExpireWindowLocked(tickWin, nowTicks);
            ExpireWindowLocked(volWin,  nowTicks);
        }

        private void ExpireWindowLocked(RollingWindow w, long nowTicks)
        {
            long cutoff = nowTicks - w.SpanTicks;
            int  cap    = bufTime.Length;
            while (w.TailSeq < headSeq)
            {
                int i = (int)(w.TailSeq % cap);
                if (bufTime[i] > cutoff) break;
                w.Count -= bufCount[i]; w.Bid -= bufBid[i]; w.Ask -= bufAsk[i];
                w.TailSeq++;
            }
            if (w.TailSeq == headSeq) { w.Count = 0; w.Bid = 0; w.Ask = 0; }
        }

        // Exchange clock extrapolated by local elapsed time since the last print,
        // so the window keeps sliding (and draining) when the tape goes quiet.
        private long NowTicksLocked()
        {
            if (lastExchTime == DateTime.MinValue) return long.MinValue;
            return lastExchTime.Ticks + (DateTime.UtcNow - lastLocalUtc).Ticks;
        }

        private void ExpireNowLocked()
        {
            if (tickWin == null) return;
            long now = NowTicksLocked();
            if (now != long.MinValue) ExpireLocked(now);
        }
        #endregion

        #region Timer (UI thread)
        private void OnTimerTick(object sender, EventArgs args)
        {
            if (tickWin == null || State != State.Realtime) return;

            double ticks, bid, ask;
            lock (sync)
            {
                ExpireNowLocked();
                ticks = tickWin.Count;
                bid   = volWin.Bid;
                ask   = volWin.Ask;
            }
            if (TickSpeedPerSecond) ticks /= TickIntervalSec;
            double delta = ask - bid;

            DateTime now = DateTime.UtcNow;
            UpdateGauge(gTick,  ticks, now);
            UpdateGauge(gBid,   bid,   now);
            UpdateGauge(gAsk,   ask,   now);
            UpdateGauge(gDelta, delta, now);

            if (ScaleMode == OFGScaleMode.Auto && hasLive)
            {
                scaleTick.Add(ticks);
                scaleVol.Add(bid);
                scaleVol.Add(ask);
                scaleDelta.Add(Math.Abs(delta));

                if (++scaleCounter >= scaleRecalcEvery)
                {
                    scaleCounter = 0;
                    scaleTick.Recompute (AutoScalePercentile, warmupSamples,     1);
                    scaleVol.Recompute  (AutoScalePercentile, warmupSamples * 2, 10);
                    scaleDelta.Recompute(AutoScalePercentile, warmupSamples,     10);
                }
            }

            if (EnableAlerts && hasLive) CheckAlerts(ticks, delta);

            ForceRefresh();
        }

        private void UpdateGauge(GaugeState g, double v, DateTime now)
        {
            g.Raw   = v;
            g.Disp += (v - g.Disp) * Math.Max(0.01, Math.Min(1.0, NeedleSmoothing));

            if (v >= g.PeakHi) { g.PeakHi = v; g.PeakHiTime = now; }
            else if ((now - g.PeakHiTime).TotalSeconds > PeakHoldSec)
                g.PeakHi = Math.Max(v, g.PeakHi - (g.PeakHi - v) * 0.15);

            if (v <= g.PeakLo) { g.PeakLo = v; g.PeakLoTime = now; }
            else if ((now - g.PeakLoTime).TotalSeconds > PeakHoldSec)
                g.PeakLo = Math.Min(v, g.PeakLo - (g.PeakLo - v) * 0.15);

            g.Hist[g.HistIdx] = v;
            g.HistIdx = (g.HistIdx + 1) % g.Hist.Length;
            if (g.HistCount < g.Hist.Length) g.HistCount++;
        }

        private void CheckAlerts(double ticks, double delta)
        {
            double tThr = AlertTickThreshold  > 0 ? AlertTickThreshold  : gTick.Scale.Max  * ExtremePct / 100.0;
            double dThr = AlertDeltaThreshold > 0 ? AlertDeltaThreshold : gDelta.Scale.Max * ExtremePct / 100.0;

            if (tickAlertArmed && ticks >= tThr)
            {
                tickAlertArmed = false;
                FireAlert("OFG_Tick", string.Format("Tick speed {0:0.#} ≥ {1:0.#}", ticks, tThr), Brushes.DarkOrange);
            }
            else if (ticks < tThr * 0.8) tickAlertArmed = true;

            if (deltaUpArmed && delta >= dThr)
            {
                deltaUpArmed = false;
                FireAlert("OFG_DeltaUp", string.Format("Delta +{0:#,0} ≥ {1:#,0}", delta, dThr), Brushes.SeaGreen);
            }
            else if (delta < dThr * 0.8) deltaUpArmed = true;

            if (deltaDnArmed && delta <= -dThr)
            {
                deltaDnArmed = false;
                FireAlert("OFG_DeltaDn", string.Format("Delta {0:#,0} ≤ -{1:#,0}", delta, dThr), Brushes.Firebrick);
            }
            else if (delta > -dThr * 0.8) deltaDnArmed = true;
        }

        private void FireAlert(string id, string msg, Brush back)
        {
            string sound = string.IsNullOrWhiteSpace(AlertSound) ? string.Empty
                         : System.IO.Path.Combine(NinjaTrader.Core.Globals.InstallDir, "sounds", AlertSound);
            TriggerCustomEvent(o =>
            {
                Alert(id, Priority.Medium, Instrument.FullName + "  " + msg, sound, AlertRearmSec, back, Brushes.White);
            }, null);
        }
        #endregion

        #region Rendering
        public override void OnRenderTargetChanged()
        {
            DisposeDx();
            if (RenderTarget == null) return;

            dxBg       = MakeDx(BackgroundBrush,   BackgroundOpacity / 100f);
            dxText     = MakeDx(TextBrush,         1f);
            dxTrack    = MakeDx(TrackBrush,        0.35f);
            dxNormal   = MakeDx(ZoneNormalBrush,   0.9f);
            dxElevated = MakeDx(ZoneElevatedBrush, 0.9f);
            dxExtreme  = MakeDx(ZoneExtremeBrush,  0.9f);
            dxBid      = MakeDx(BidBrush,          1f);
            dxAsk      = MakeDx(AskBrush,          1f);
            dxNeedle   = MakeDx(NeedleBrush,       1f);
            dxHistory  = MakeDx(HistoryBrush,      1f);
        }

        private D2D.Brush MakeDx(Brush b, float opacity)
        {
            var dx = b.ToDxBrush(RenderTarget);
            dx.Opacity = opacity;
            return dx;
        }

        private void DisposeDx()
        {
            D2D.Brush[] all = { dxBg, dxText, dxTrack, dxNormal, dxElevated, dxExtreme, dxBid, dxAsk, dxNeedle, dxHistory };
            foreach (var b in all) if (b != null) b.Dispose();
            dxBg = dxText = dxTrack = dxNormal = dxElevated = dxExtreme = dxBid = dxAsk = dxNeedle = dxHistory = null;
        }

        private void EnsureFormats(float s)
        {
            if (fmtValue != null && Math.Abs(fmtForSize - s) < 0.5f) return;
            DisposeFormats();
            var f = NinjaTrader.Core.Globals.DirectWriteFactory;
            fmtValue = new DW.TextFormat(f, "Segoe UI", DW.FontWeight.SemiBold, DW.FontStyle.Normal, Math.Max(10f, s * 0.13f)) { WordWrapping = DW.WordWrapping.NoWrap };
            fmtLabel = new DW.TextFormat(f, "Segoe UI", DW.FontWeight.Normal,   DW.FontStyle.Normal, Math.Max(8f,  s * 0.07f)) { WordWrapping = DW.WordWrapping.NoWrap };
            fmtSmall = new DW.TextFormat(f, "Segoe UI", DW.FontWeight.Normal,   DW.FontStyle.Normal, Math.Max(7f,  s * 0.06f)) { WordWrapping = DW.WordWrapping.NoWrap };
            fmtForSize = s;
        }

        private void DisposeFormats()
        {
            if (fmtValue != null) { fmtValue.Dispose(); fmtValue = null; }
            if (fmtLabel != null) { fmtLabel.Dispose(); fmtLabel = null; }
            if (fmtSmall != null) { fmtSmall.Dispose(); fmtSmall = null; }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (RenderTarget == null || dxText == null || gauges.Count == 0 || gTick == null) return;

            float S = GaugeSize;
            EnsureFormats(S);

            // ---- metrics -------------------------------------------------------
            float pad    = 6f;
            float rOuter = S / 2f - pad;
            float zoneW  = Math.Max(2f, S * 0.02f);
            float thick  = Math.Max(6f, S * 0.075f);
            float rZone  = rOuter - zoneW / 2f;
            float rFill  = rOuter - zoneW - 2f - thick / 2f;
            float valH   = fmtValue.FontSize * 1.35f;
            float lblH   = fmtLabel.FontSize * 1.45f;
            float sparkH = ShowHistory ? S * 0.22f : 0f;
            float cellW  = S;
            float cellH  = pad + rOuter + 4f + valH + lblH + (ShowHistory ? 4f + sparkH : 0f) + pad;

            int   n      = gauges.Count;
            bool  horiz  = GaugeLayout == OFGLayout.Horizontal;
            float totalW = horiz ? n * cellW + (n - 1) * Spacing : cellW;
            float totalH = horiz ? cellH : n * cellH + (n - 1) * Spacing;

            bool left = GaugeAnchor == OFGAnchor.TopLeft || GaugeAnchor == OFGAnchor.BottomLeft;
            bool top  = GaugeAnchor == OFGAnchor.TopLeft || GaugeAnchor == OFGAnchor.TopRight;
            float x0  = left ? ChartPanel.X + OffsetX : ChartPanel.X + ChartPanel.W - OffsetX - totalW;
            float y0  = top  ? ChartPanel.Y + OffsetY : ChartPanel.Y + ChartPanel.H - OffsetY - totalH;

            var oldAA = RenderTarget.AntialiasMode;
            RenderTarget.AntialiasMode = D2D.AntialiasMode.PerPrimitive;

            RenderTarget.FillRoundedRectangle(new D2D.RoundedRectangle
            {
                Rect = new SharpDX.RectangleF(x0 - 4f, y0 - 4f, totalW + 8f, totalH + 8f),
                RadiusX = 6f, RadiusY = 6f
            }, dxBg);

            bool live = hasLive && State == State.Realtime;

            for (int i = 0; i < n; i++)
            {
                float cx0 = horiz ? x0 + i * (cellW + Spacing) : x0;
                float cy0 = horiz ? y0 : y0 + i * (cellH + Spacing);
                DrawGauge(i, gauges[i], cx0, cy0, S, pad, rOuter, rZone, zoneW, rFill, thick, valH, lblH, sparkH, live);
            }

            RenderTarget.AntialiasMode = oldAA;
        }

        private void DrawGauge(int index, GaugeKind kind, float left, float top, float S, float pad,
                               float rOuter, float rZone, float zoneW, float rFill, float thick,
                               float valH, float lblH, float sparkH, bool live)
        {
            float cx = left + S / 2f;
            float cy = top + pad + rOuter;
            bool bipolar = kind == GaugeKind.Delta || kind == GaugeKind.BidAskDual;

            // ---- static geometry (cached) --------------------------------------
            var vis = visuals[index];
            if (vis.Kx != cx || vis.Ky != cy || vis.Kr != rZone || vis.Kt != rFill || vis.Kbip != bipolar)
            {
                vis.Dispose();
                vis.Kx = cx; vis.Ky = cy; vis.Kr = rZone; vis.Kt = rFill; vis.Kbip = bipolar;
                vis.Track = BuildArc(cx, cy, rFill, 0, 1);

                double e1 = ElevatedPct / 100.0, e2 = Math.Max(ElevatedPct, ExtremePct) / 100.0;
                if (!bipolar)
                {
                    AddZone(vis, cx, cy, rZone, 0,  e1, 0);
                    AddZone(vis, cx, cy, rZone, e1, e2, 1);
                    AddZone(vis, cx, cy, rZone, e2, 1,  2);
                }
                else
                {
                    AddZone(vis, cx, cy, rZone, 0.5,               0.5 + 0.5 * e1, 0);
                    AddZone(vis, cx, cy, rZone, 0.5 + 0.5 * e1,    0.5 + 0.5 * e2, 1);
                    AddZone(vis, cx, cy, rZone, 0.5 + 0.5 * e2,    1,              2);
                    AddZone(vis, cx, cy, rZone, 0.5 - 0.5 * e1,    0.5,            0);
                    AddZone(vis, cx, cy, rZone, 0.5 - 0.5 * e2,    0.5 - 0.5 * e1, 1);
                    AddZone(vis, cx, cy, rZone, 0,                 0.5 - 0.5 * e2, 2);
                }
            }

            RenderTarget.DrawGeometry(vis.Track, dxTrack, thick);
            foreach (var z in vis.Zones)
                RenderTarget.DrawGeometry(z.Key, z.Value == 0 ? dxNormal : z.Value == 1 ? dxElevated : dxExtreme, zoneW);

            float needleW = Math.Max(2f, S * 0.015f);
            float peakR0  = rFill - thick / 2f - 2f;
            float peakR1  = rOuter;
            float textY   = cy + 4f;
            string valueText, label;
            string iv = VolIntervalSec + "s";

            // ---- dynamic: fill, needle, peaks ----------------------------------
            switch (kind)
            {
                case GaugeKind.TickSpeed:
                case GaugeKind.BidVol:
                case GaugeKind.AskVol:
                {
                    GaugeState g = kind == GaugeKind.TickSpeed ? gTick : kind == GaugeKind.BidVol ? gBid : gAsk;
                    double max = g.Scale.Max;
                    double f   = Clamp(g.Disp / max, 0, 1);
                    D2D.Brush fill = kind == GaugeKind.BidVol ? dxBid
                                   : kind == GaugeKind.AskVol ? dxAsk
                                   : ZoneBrush(f);
                    if (live) FillArc(cx, cy, rFill, 0, f, fill, thick);
                    if (live && g.PeakHi > 0) RadialTick(cx, cy, peakR0, peakR1, Clamp(g.PeakHi / max, 0, 1), dxText, 2f);
                    Needle(cx, cy, rFill, live ? f : 0, needleW);

                    if (kind == GaugeKind.TickSpeed)
                    {
                        valueText = live ? g.Raw.ToString(TickSpeedPerSecond ? "0.0" : "0") : "--";
                        label     = TickSpeedPerSecond ? "TICKS/s  (" + TickIntervalSec + "s)" : "TICKS  " + TickIntervalSec + "s";
                    }
                    else
                    {
                        valueText = live ? FmtVol(g.Raw) : "--";
                        label     = (kind == GaugeKind.BidVol ? "BID VOL  " : "ASK VOL  ") + iv;
                    }

                    DrawScaleEnds(left, S, pad, textY, "0", kind == GaugeKind.TickSpeed ? FmtTick(max) : FmtVol(max));
                    DrawText(valueText, fmtValue, left, textY, S, valH, kind == GaugeKind.BidVol ? dxBid : kind == GaugeKind.AskVol ? dxAsk : dxText, DW.TextAlignment.Center);
                    DrawText(label, fmtLabel, left, textY + valH, S, lblH, dxText, DW.TextAlignment.Center);

                    if (ShowHistory)
                        DrawHistory(g, left + pad, textY + valH + lblH + 4f, S - 2 * pad, sparkH, max, false,
                                    kind == GaugeKind.BidVol ? dxBid : kind == GaugeKind.AskVol ? dxAsk : dxHistory);
                    break;
                }

                case GaugeKind.Delta:
                {
                    GaugeState g = gDelta;
                    double max = g.Scale.Max;
                    double f   = Clamp(0.5 + 0.5 * g.Disp / max, 0, 1);
                    if (live) FillArc(cx, cy, rFill, 0.5, f, g.Disp >= 0 ? dxAsk : dxBid, thick);
                    if (live && g.PeakHi > 0) RadialTick(cx, cy, peakR0, peakR1, Clamp(0.5 + 0.5 * g.PeakHi / max, 0, 1), dxText, 2f);
                    if (live && g.PeakLo < 0) RadialTick(cx, cy, peakR0, peakR1, Clamp(0.5 + 0.5 * g.PeakLo / max, 0, 1), dxText, 2f);
                    RadialTick(cx, cy, rFill - thick / 2f, rFill + thick / 2f, 0.5, dxTrack, 1f);
                    Needle(cx, cy, rFill, live ? f : 0.5, needleW);

                    valueText = live ? FmtDelta(g.Raw) : "--";
                    label     = "DELTA  " + iv;
                    D2D.Brush vb = !live ? dxText : g.Raw > 0 ? dxAsk : g.Raw < 0 ? dxBid : dxText;

                    DrawScaleEnds(left, S, pad, textY, "-" + FmtVol(max), "+" + FmtVol(max));
                    DrawText(valueText, fmtValue, left, textY, S, valH, vb, DW.TextAlignment.Center);
                    DrawText(label, fmtLabel, left, textY + valH, S, lblH, dxText, DW.TextAlignment.Center);

                    if (ShowHistory)
                        DrawHistory(g, left + pad, textY + valH + lblH + 4f, S - 2 * pad, sparkH, max, true, null);
                    break;
                }

                case GaugeKind.BidAskDual:
                {
                    double max = gBid.Scale.Max;  // shared with ask
                    double fb  = Clamp(gBid.Disp / max, 0, 1) * 0.5;
                    double fa  = Clamp(gAsk.Disp / max, 0, 1) * 0.5;
                    if (live)
                    {
                        FillArc(cx, cy, rFill, 0.5 - fb, 0.5, dxBid, thick);
                        FillArc(cx, cy, rFill, 0.5, 0.5 + fa, dxAsk, thick);
                        if (gBid.PeakHi > 0) RadialTick(cx, cy, peakR0, peakR1, 0.5 - Clamp(gBid.PeakHi / max, 0, 1) * 0.5, dxText, 2f);
                        if (gAsk.PeakHi > 0) RadialTick(cx, cy, peakR0, peakR1, 0.5 + Clamp(gAsk.PeakHi / max, 0, 1) * 0.5, dxText, 2f);
                    }
                    RadialTick(cx, cy, rFill - thick / 2f - 2f, rOuter, 0.5, dxText, 1.5f);

                    float half = S / 2f;
                    DrawText(live ? FmtVol(gBid.Raw) + " " : "-- ", fmtValue, left, textY, half, valH, dxBid, DW.TextAlignment.Trailing);
                    DrawText(live ? " " + FmtVol(gAsk.Raw) : " --", fmtValue, left + half, textY, half, valH, dxAsk, DW.TextAlignment.Leading);
                    DrawText("BID | ASK  " + iv, fmtLabel, left, textY + valH, S, lblH, dxText, DW.TextAlignment.Center);
                    DrawScaleEnds(left, S, pad, textY - fmtSmall.FontSize * 1.4f, FmtVol(max), FmtVol(max));

                    if (ShowHistory)
                    {
                        float hy = textY + valH + lblH + 4f;
                        DrawHistory(gBid, left + pad, hy, S - 2 * pad, sparkH, max, false, dxBid);
                        DrawHistory(gAsk, left + pad, hy, S - 2 * pad, sparkH, max, false, dxAsk);
                    }
                    break;
                }
            }
        }

        private void AddZone(GaugeVisual vis, float cx, float cy, float r, double f0, double f1, int brushIdx)
        {
            if (f1 - f0 < 1e-4) return;
            vis.Zones.Add(new KeyValuePair<D2D.PathGeometry, int>(BuildArc(cx, cy, r, f0, f1), brushIdx));
        }

        private D2D.Brush ZoneBrush(double f)
        {
            if (f < ElevatedPct / 100.0) return dxNormal;
            if (f < ExtremePct  / 100.0) return dxElevated;
            return dxExtreme;
        }

        private void FillArc(float cx, float cy, float r, double f0, double f1, D2D.Brush b, float width)
        {
            if (f1 < f0) { double t = f0; f0 = f1; f1 = t; }
            if (f1 - f0 < 1e-4) return;
            using (var g = BuildArc(cx, cy, r, f0, f1))
                RenderTarget.DrawGeometry(g, b, width);
        }

        // f: 0 = left end, 0.5 = top, 1 = right end of the semicircle.
        private D2D.PathGeometry BuildArc(float cx, float cy, float r, double f0, double f1)
        {
            var g = new D2D.PathGeometry(NinjaTrader.Core.Globals.D2DFactory);
            using (var sink = g.Open())
            {
                sink.BeginFigure(ArcPoint(cx, cy, r, f0), D2D.FigureBegin.Hollow);
                int segs = Math.Max(1, (int)Math.Ceiling((f1 - f0) / 0.25)); // ≤ 45° per segment
                for (int k = 1; k <= segs; k++)
                {
                    double f = f0 + (f1 - f0) * k / segs;
                    sink.AddArc(new D2D.ArcSegment
                    {
                        Point          = ArcPoint(cx, cy, r, f),
                        Size           = new SharpDX.Size2F(r, r),
                        RotationAngle  = 0f,
                        SweepDirection = D2D.SweepDirection.Clockwise,
                        ArcSize        = D2D.ArcSize.Small
                    });
                }
                sink.EndFigure(D2D.FigureEnd.Open);
                sink.Close();
            }
            return g;
        }

        private static SharpDX.Vector2 ArcPoint(float cx, float cy, float r, double f)
        {
            double a = Math.PI * (1.0 - f);
            return new SharpDX.Vector2((float)(cx + r * Math.Cos(a)), (float)(cy - r * Math.Sin(a)));
        }

        private void RadialTick(float cx, float cy, float r0, float r1, double f, D2D.Brush b, float w)
        {
            RenderTarget.DrawLine(ArcPoint(cx, cy, r0, f), ArcPoint(cx, cy, r1, f), b, w);
        }

        private void Needle(float cx, float cy, float len, double f, float w)
        {
            RenderTarget.DrawLine(new SharpDX.Vector2(cx, cy), ArcPoint(cx, cy, len, f), dxNeedle, w);
            RenderTarget.FillEllipse(new D2D.Ellipse(new SharpDX.Vector2(cx, cy), w * 1.8f, w * 1.8f), dxNeedle);
        }

        private void DrawScaleEnds(float left, float S, float pad, float y, string lo, string hi)
        {
            float w = S * 0.3f, h = fmtSmall.FontSize * 1.4f;
            DrawText(lo, fmtSmall, left + pad,         y, w, h, dxTrack, DW.TextAlignment.Leading);
            DrawText(hi, fmtSmall, left + S - pad - w, y, w, h, dxTrack, DW.TextAlignment.Trailing);
        }

        private void DrawHistory(GaugeState g, float x, float y, float w, float h, double max, bool bipolar, D2D.Brush fixedBrush)
        {
            float zeroY = bipolar ? y + h / 2f : y + h;
            RenderTarget.DrawLine(new SharpDX.Vector2(x, zeroY), new SharpDX.Vector2(x + w, zeroY), dxTrack, 1f);

            int n = g.HistCount, cap = g.Hist.Length;
            if (n < 2 || max <= 0) return;

            int   start  = (g.HistIdx - n + cap) % cap;
            float step   = w / (cap - 1);
            float xStart = x + w - (n - 1) * step;      // newest sample pinned to the right edge
            var   prev   = new SharpDX.Vector2();

            for (int k = 0; k < n; k++)
            {
                double v    = g.Hist[(start + k) % cap];
                double norm = v / max;
                float  py   = bipolar ? (float)(zeroY - Clamp(norm, -1, 1) * h / 2f)
                                      : (float)(zeroY - Clamp(norm,  0, 1) * h);
                var p = new SharpDX.Vector2(xStart + k * step, py);
                if (k > 0)
                    RenderTarget.DrawLine(prev, p, fixedBrush ?? (v >= 0 ? dxAsk : dxBid), 1.5f);
                prev = p;
            }
        }

        private void DrawText(string s, DW.TextFormat fmt, float x, float y, float w, float h, D2D.Brush b, DW.TextAlignment align)
        {
            fmt.TextAlignment = align;
            using (var tl = new DW.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, s, fmt, Math.Max(1f, w), Math.Max(1f, h)))
                RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, y), tl, b, D2D.DrawTextOptions.NoSnap);
        }

        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }

        private static string FmtVol(double v)
        {
            double a = Math.Abs(v);
            if (a >= 1e6) return (v / 1e6).ToString("0.00") + "M";
            if (a >= 1e4) return (v / 1e3).ToString("0.0") + "k";
            return v.ToString("#,0");
        }

        private static string FmtDelta(double v) { return (v > 0 ? "+" : "") + FmtVol(v); }

        private string FmtTick(double v) { return v.ToString(TickSpeedPerSecond ? "0.#" : "0"); }
        #endregion

        #region Public outputs (thread-safe, current on read — usable from strategies)
        [Browsable(false), XmlIgnore]
        public double WindowTickSpeed
        {
            get
            {
                lock (sync)
                {
                    if (tickWin == null) return 0;
                    ExpireNowLocked();
                    return TickSpeedPerSecond ? tickWin.Count / TickIntervalSec : tickWin.Count;
                }
            }
        }

        [Browsable(false), XmlIgnore]
        public double WindowBidVolume { get { lock (sync) { if (volWin == null) return 0; ExpireNowLocked(); return volWin.Bid; } } }

        [Browsable(false), XmlIgnore]
        public double WindowAskVolume { get { lock (sync) { if (volWin == null) return 0; ExpireNowLocked(); return volWin.Ask; } } }

        [Browsable(false), XmlIgnore]
        public double WindowDelta { get { lock (sync) { if (volWin == null) return 0; ExpireNowLocked(); return volWin.Ask - volWin.Bid; } } }

        /// <summary>Current auto-scale max for tick speed (useful as an adaptive "fast market" threshold).</summary>
        [Browsable(false), XmlIgnore]
        public double TickSpeedScaleMax { get { return scaleTick != null ? scaleTick.Max : 0; } }
        #endregion

        #region Properties
        // ---- 1. Data ----------------------------------------------------------
        [NinjaScriptProperty]
        [Range(1, 600)]
        [Display(Name = "Tick speed interval (sec)", GroupName = "1. Data", Order = 1)]
        public int TickIntervalSec { get; set; }

        [NinjaScriptProperty]
        [Range(1, 600)]
        [Display(Name = "Volume / delta interval (sec)", GroupName = "1. Data", Order = 2)]
        public int VolIntervalSec { get; set; }

        [Display(Name = "Tick speed as per-second rate", GroupName = "1. Data", Order = 3)]
        public bool TickSpeedPerSecond { get; set; }

        [Display(Name = "Prints between bid/ask", Description = "How to treat prints strictly inside the spread.", GroupName = "1. Data", Order = 4)]
        public OFGMidPrintMode MidPrintMode { get; set; }

        [Display(Name = "Merge bundled prints", Description = "Same timestamp + price + side counts as one tick.", GroupName = "1. Data", Order = 5)]
        public bool MergeBundledPrints { get; set; }

        [Range(50, 2000)]
        [Display(Name = "Refresh (ms)", GroupName = "1. Data", Order = 6)]
        public int RefreshMs { get; set; }

        // ---- 2. Gauges --------------------------------------------------------
        [Display(Name = "Show tick speed", GroupName = "2. Gauges", Order = 1)]
        public bool ShowTickSpeed { get; set; }

        [Display(Name = "Show bid volume", GroupName = "2. Gauges", Order = 2)]
        public bool ShowBidVol { get; set; }

        [Display(Name = "Show ask volume", GroupName = "2. Gauges", Order = 3)]
        public bool ShowAskVol { get; set; }

        [Display(Name = "Show delta", GroupName = "2. Gauges", Order = 4)]
        public bool ShowDelta { get; set; }

        [Display(Name = "Bid/Ask style", GroupName = "2. Gauges", Order = 5)]
        public OFGBidAskStyle BidAskStyle { get; set; }

        [Display(Name = "Show history sparkline", GroupName = "2. Gauges", Order = 6)]
        public bool ShowHistory { get; set; }

        [Range(10, 2000)]
        [Display(Name = "History length (samples)", GroupName = "2. Gauges", Order = 7)]
        public int HistoryLength { get; set; }

        [Range(0, 60)]
        [Display(Name = "Peak hold (sec)", GroupName = "2. Gauges", Order = 8)]
        public int PeakHoldSec { get; set; }

        [Range(0.01, 1.0)]
        [Display(Name = "Needle smoothing (1 = none)", GroupName = "2. Gauges", Order = 9)]
        public double NeedleSmoothing { get; set; }

        // ---- 3. Scale ---------------------------------------------------------
        [Display(Name = "Scale mode", GroupName = "3. Scale", Order = 1)]
        public OFGScaleMode ScaleMode { get; set; }

        [Range(50, 100)]
        [Display(Name = "Auto-scale percentile", GroupName = "3. Scale", Order = 2)]
        public int AutoScalePercentile { get; set; }

        [Range(1, 240)]
        [Display(Name = "Auto-scale lookback (min)", GroupName = "3. Scale", Order = 3)]
        public int AutoScaleLookbackMin { get; set; }

        [Range(1, double.MaxValue)]
        [Display(Name = "Fixed max: tick speed", GroupName = "3. Scale", Order = 4)]
        public double FixedMaxTicks { get; set; }

        [Range(1, double.MaxValue)]
        [Display(Name = "Fixed max: bid/ask volume", GroupName = "3. Scale", Order = 5)]
        public double FixedMaxVolume { get; set; }

        [Range(1, double.MaxValue)]
        [Display(Name = "Fixed max: |delta|", GroupName = "3. Scale", Order = 6)]
        public double FixedMaxDelta { get; set; }

        [Range(1, 99)]
        [Display(Name = "Elevated zone from (%)", GroupName = "3. Scale", Order = 7)]
        public int ElevatedPct { get; set; }

        [Range(1, 99)]
        [Display(Name = "Extreme zone from (%)", GroupName = "3. Scale", Order = 8)]
        public int ExtremePct { get; set; }

        // ---- 4. Layout --------------------------------------------------------
        [Display(Name = "Anchor", GroupName = "4. Layout", Order = 1)]
        public OFGAnchor GaugeAnchor { get; set; }

        [Display(Name = "Layout", GroupName = "4. Layout", Order = 2)]
        public OFGLayout GaugeLayout { get; set; }

        [Range(80, 600)]
        [Display(Name = "Gauge size (px)", GroupName = "4. Layout", Order = 3)]
        public int GaugeSize { get; set; }

        [Range(0, 4000)]
        [Display(Name = "Offset X (px)", GroupName = "4. Layout", Order = 4)]
        public int OffsetX { get; set; }

        [Range(0, 4000)]
        [Display(Name = "Offset Y (px)", GroupName = "4. Layout", Order = 5)]
        public int OffsetY { get; set; }

        [Range(0, 200)]
        [Display(Name = "Spacing (px)", GroupName = "4. Layout", Order = 6)]
        public int Spacing { get; set; }

        [Range(0, 100)]
        [Display(Name = "Background opacity (%)", GroupName = "4. Layout", Order = 7)]
        public int BackgroundOpacity { get; set; }

        // ---- 5. Colors --------------------------------------------------------
        [XmlIgnore][Display(Name = "Background", GroupName = "5. Colors", Order = 1)]
        public Brush BackgroundBrush { get; set; }
        [Browsable(false)] public string BackgroundBrushSerialize { get { return Serialize.BrushToString(BackgroundBrush); } set { BackgroundBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Text", GroupName = "5. Colors", Order = 2)]
        public Brush TextBrush { get; set; }
        [Browsable(false)] public string TextBrushSerialize { get { return Serialize.BrushToString(TextBrush); } set { TextBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Track", GroupName = "5. Colors", Order = 3)]
        public Brush TrackBrush { get; set; }
        [Browsable(false)] public string TrackBrushSerialize { get { return Serialize.BrushToString(TrackBrush); } set { TrackBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Zone: normal", GroupName = "5. Colors", Order = 4)]
        public Brush ZoneNormalBrush { get; set; }
        [Browsable(false)] public string ZoneNormalBrushSerialize { get { return Serialize.BrushToString(ZoneNormalBrush); } set { ZoneNormalBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Zone: elevated", GroupName = "5. Colors", Order = 5)]
        public Brush ZoneElevatedBrush { get; set; }
        [Browsable(false)] public string ZoneElevatedBrushSerialize { get { return Serialize.BrushToString(ZoneElevatedBrush); } set { ZoneElevatedBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Zone: extreme", GroupName = "5. Colors", Order = 6)]
        public Brush ZoneExtremeBrush { get; set; }
        [Browsable(false)] public string ZoneExtremeBrushSerialize { get { return Serialize.BrushToString(ZoneExtremeBrush); } set { ZoneExtremeBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Bid", GroupName = "5. Colors", Order = 7)]
        public Brush BidBrush { get; set; }
        [Browsable(false)] public string BidBrushSerialize { get { return Serialize.BrushToString(BidBrush); } set { BidBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Ask", GroupName = "5. Colors", Order = 8)]
        public Brush AskBrush { get; set; }
        [Browsable(false)] public string AskBrushSerialize { get { return Serialize.BrushToString(AskBrush); } set { AskBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Needle", GroupName = "5. Colors", Order = 9)]
        public Brush NeedleBrush { get; set; }
        [Browsable(false)] public string NeedleBrushSerialize { get { return Serialize.BrushToString(NeedleBrush); } set { NeedleBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "History (tick speed)", GroupName = "5. Colors", Order = 10)]
        public Brush HistoryBrush { get; set; }
        [Browsable(false)] public string HistoryBrushSerialize { get { return Serialize.BrushToString(HistoryBrush); } set { HistoryBrush = Serialize.StringToBrush(value); } }

        // ---- 6. Alerts --------------------------------------------------------
        [Display(Name = "Enable alerts", GroupName = "6. Alerts", Order = 1)]
        public bool EnableAlerts { get; set; }

        [Range(0, double.MaxValue)]
        [Display(Name = "Tick speed threshold (0 = extreme zone)", GroupName = "6. Alerts", Order = 2)]
        public double AlertTickThreshold { get; set; }

        [Range(0, double.MaxValue)]
        [Display(Name = "|Delta| threshold (0 = extreme zone)", GroupName = "6. Alerts", Order = 3)]
        public double AlertDeltaThreshold { get; set; }

        [Range(0, 3600)]
        [Display(Name = "Re-arm (sec)", GroupName = "6. Alerts", Order = 4)]
        public int AlertRearmSec { get; set; }

        [Display(Name = "Sound file (in NT sounds folder)", GroupName = "6. Alerts", Order = 5)]
        public string AlertSound { get; set; }
        #endregion
    }
}
