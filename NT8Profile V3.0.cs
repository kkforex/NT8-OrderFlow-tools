// =====================================================================================
//  NT8Profile.cs  —  NinjaTrader 8 indicator
//  Volume / Ask-Bid / Delta / Delta+Total profile.
//
//  Design priorities: fast chart load + low memory.
//   * NO Tick Replay, NO 1-tick AddDataSeries. Historical ticks come from ONE async
//     BarsRequest (chart loads instantly, profile fills when the request returns).
//     The tick Bars object is disposed after processing, so steady-state memory is
//     only the compact per-bar ladders below.
//   * Real-time ticks come from OnMarketData (Last) with live bid/ask classification.
//   * Storage: one int[] per primary bar  ->  [0] = base tick index,
//     then interleaved (ask, bid) volume per 1-tick level. Trimmed after load.
//   * Any profile (Latest / Visible / Custom range) = sum of a bar range, cached and
//     rebuilt only when data inside that range changes.
//
//  Scopes: Latest (last N sessions or current N-minute block), Visible, Custom
//  (NT8ProfileRange drawing tool — see DrawingTools/NT8ProfileRange.cs).
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
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

public enum NT8ProfileType { Volume, AskBid, Delta, DeltaAndTotal }
public enum NT8ProfileScope { Latest, Visible, CustomOnly }
public enum NT8ProfileLength { Sessions, Minutes, ClockMinutes }   // Minutes = rolling last N minutes
public enum NT8ProfileGrouping { Automatic, Manual }
public enum NT8ProfileAskBidStyle { Stacked, Split }
public enum NT8ProfilePlacement { SessionStart, LeftMargin, RightMargin }
public enum NT8ProfileLayer { Background, Foreground }

namespace NinjaTrader.NinjaScript.Indicators
{
    public class NT8Profile : Indicator
    {
        #region Private types / fields

        private struct PendingTick
        {
            public long Time;
            public int Bar;
            public int Tick;
            public int Vol;
            public bool IsAsk;
        }

        private sealed class ProfileData
        {
            public int MinTick;
            public long[] Ask;
            public long[] Bid;
            public int HistVersion;
            public int RtVersion;
            public int LastFrame;
        }

        // --- data (guarded by sync) ---
        private readonly object sync = new object();
        private List<int[]> store = new List<int[]>();
        private readonly List<PendingTick> pending = new List<PendingTick>();
        private readonly List<int> sessionStarts = new List<int>();
        private volatile bool histReady;
        private int histVersion;
        private int rtVersion;
        private int firstRtBar = int.MaxValue;

        // --- load ---
        private BarsRequest tickRequest;
        private long[] barTimes;
        private long loadBoundaryTicks = long.MinValue;
        private volatile bool terminated;
        private volatile string status;
        private bool isTimeBased;
        private double tickSize;

        // --- real-time classification state (instrument thread) ---
        private double rtPrev;
        private bool rtLastAsk = true;

        // --- render (UI thread) ---
        private readonly Dictionary<long, ProfileData> cache = new Dictionary<long, ProfileData>();
        private int frame;
        private long[] rowAsk = new long[0];
        private long[] rowBid = new long[0];

        private SharpDX.Direct2D1.Brush dxVol, dxVaBar, dxAsk, dxBid, dxPos, dxNeg, dxPoc, dxVa;

        private static readonly int[] NiceSteps = { 1, 2, 4, 5, 8, 10, 16, 20, 25, 40, 50, 80, 100, 160, 200, 250, 400, 500, 800, 1000 };

        #endregion

        #region State

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                     = "NT8Profile";
                Description              = "Volume / Ask-Bid / Delta profile from a one-shot tick request (no Tick Replay). Use the 'NT8 Profile Range' drawing tool for custom ranges.";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;   // must keep receiving OnMarketData
                BarsRequiredToPlot       = 0;

                ProfileType      = NT8ProfileType.Volume;
                Scope            = NT8ProfileScope.Latest;
                LengthType       = NT8ProfileLength.Sessions;
                LengthValue      = 1;
                HistoricalProfiles = 0;
                ShowCustomRanges = true;

                TickDaysToLoad = 2;
                Grouping       = NT8ProfileGrouping.Automatic;
                ManualTicks    = 1;
                GroupFactor    = 1;
                AutoMinRowPx   = 3;

                ProfileWidthPx = 220;
                Placement      = NT8ProfilePlacement.SessionStart;
                OffsetPx       = 0;
                AskBidStyle    = NT8ProfileAskBidStyle.Stacked;
                DeltaSharePct  = 35;
                Layer          = NT8ProfileLayer.Background;

                ShowPoc        = true;
                HighlightPoc   = true;
                ShowPocLine    = true;
                ExtendPocLine  = false;
                PocLineWidth   = 2;
                ShowValueArea  = true;
                ValueAreaPct   = 70;
                ColorValueAreaBars = true;

                VolumeBrush   = Brushes.SlateGray;
                AskBrush      = Brushes.MediumSeaGreen;
                BidBrush      = Brushes.IndianRed;
                DeltaPosBrush = Brushes.LimeGreen;
                DeltaNegBrush = Brushes.Crimson;
                PocBrush      = Brushes.Gold;
                VaBrush       = Brushes.DodgerBlue;
                VaBarBrush    = Brushes.SteelBlue;

                VolumeOpacity   = 60;
                VaBarOpacity    = 70;
                AskOpacity      = 70;
                BidOpacity      = 70;
                DeltaPosOpacity = 70;
                DeltaNegOpacity = 70;
                PocOpacity      = 100;
                VaOpacity       = 90;
            }
            else if (State == State.DataLoaded)
            {
                BarsPeriodType t = BarsPeriod.BarsPeriodType;
                isTimeBased = t == BarsPeriodType.Minute || t == BarsPeriodType.Second || t == BarsPeriodType.Day
                           || t == BarsPeriodType.Week   || t == BarsPeriodType.Month  || t == BarsPeriodType.Year;
                tickSize = TickSize;
            }
            else if (State == State.Historical)
            {
                // Background = render behind price bars; Foreground = on top of bars and drawings.
                SetZOrder(Layer == NT8ProfileLayer.Background ? int.MinValue : int.MaxValue);

                if (ChartControl != null)
                    StartTickRequest();
                else
                    histReady = true;
            }
            else if (State == State.Terminated)
            {
                terminated = true;
                lock (sync)
                {
                    if (tickRequest != null)
                    {
                        try { tickRequest.Dispose(); } catch { }
                        tickRequest = null;
                    }
                    store = new List<int[]>();
                    pending.Clear();
                }
                cache.Clear();
                DisposeBrushes();
            }
        }

        #endregion

        #region Historical load (single async tick request)

        private void StartTickRequest()
        {
            int n = Bars.Count;
            if (n == 0) { histReady = true; return; }

            // Snapshot of primary bar end-times for tick -> bar mapping on the worker thread.
            barTimes = new long[n];
            for (int i = 0; i < n; i++)
                barTimes[i] = Bars.GetTime(i).Ticks;

            // Sessions of ticks to load: at least TickDaysToLoad, extended to cover historical profiles.
            int need = TickDaysToLoad;
            if (Scope == NT8ProfileScope.Latest && HistoricalProfiles > 0)
            {
                int profiles = HistoricalProfiles + 1;
                int cover = LengthType == NT8ProfileLength.Sessions
                          ? profiles * Math.Max(1, LengthValue)
                          : (int)Math.Ceiling(profiles * Math.Max(1, LengthValue) / 1440.0) + 1;
                need = Math.Max(need, cover);
            }

            // Walk back `need` sessions.
            int startIdx = 0, found = 0;
            for (int i = n - 1; i >= 0; i--)
            {
                if (Bars.IsFirstBarOfSessionByIndex(i))
                {
                    found++;
                    if (found >= need) { startIdx = i; break; }
                }
            }

            DateTime from = startIdx > 0 ? Bars.GetTime(startIdx - 1) : Bars.GetTime(0);
            loadBoundaryTicks = startIdx > 0 ? from.Ticks : long.MinValue;

            DateTime lastBar = Bars.GetTime(n - 1);
            DateTime now     = Core.Globals.Now;
            DateTime to      = now > lastBar ? now : lastBar;

            status = "NT8 Profile: loading tick data…";

            tickRequest = new BarsRequest(Instrument, from, to)
            {
                BarsPeriod   = new BarsPeriod { BarsPeriodType = BarsPeriodType.Tick, Value = 1 },
                TradingHours = Bars.TradingHours
            };
            tickRequest.Request(new Action<BarsRequest, ErrorCode, string>(OnTicksLoaded));
        }

        private void OnTicksLoaded(BarsRequest req, ErrorCode err, string msg)
        {
            try
            {
                if (terminated) return;

                if (err != ErrorCode.NoError)
                {
                    status = "NT8 Profile: tick request failed (" + err + ") " + msg;
                    lock (sync)
                    {
                        foreach (PendingTick pt in pending)
                            AddToStore(store, pt.Bar, pt.Tick, pt.Vol, pt.IsAsk);
                        pending.Clear();
                        histReady = true;
                        histVersion++;
                    }
                    return;
                }

                Bars tb     = req.Bars;
                long[] bt   = barTimes;
                int nb      = bt.Length;
                int count   = tb.Count;
                var local   = new List<int[]>(nb);
                int cursor  = 0;
                double prev = 0;
                bool lastAsk = true;
                long lastT  = long.MinValue;

                for (int i = 0; i < count; i++)
                {
                    long t = tb.GetTime(i).Ticks;
                    if (t <= loadBoundaryTicks) continue;

                    // primary bar = first bar whose end-time >= tick time (ticks arrive in time order)
                    while (cursor < nb - 1 && bt[cursor] < t) cursor++;

                    double p = tb.GetClose(i);
                    bool isAsk = Classify(p, tb.GetAsk(i), tb.GetBid(i), ref prev, ref lastAsk);
                    AddToStore(local, cursor, ToTick(p), ClampVol(tb.GetVolume(i)), isAsk);
                    lastT = t;

                    if ((i & 0xFFFF) == 0 && terminated) return;
                }

                for (int i = 0; i < local.Count; i++)
                    local[i] = Trim(local[i]);

                lock (sync)
                {
                    foreach (PendingTick pt in pending)
                        if (pt.Time > lastT)
                            AddToStore(local, pt.Bar, pt.Tick, pt.Vol, pt.IsAsk);
                    pending.Clear();
                    pending.TrimExcess();

                    store     = local;
                    histReady = true;
                    histVersion++;
                }
                status = null;
            }
            catch (Exception ex)
            {
                status = "NT8 Profile: " + ex.Message;
                lock (sync) { histReady = true; histVersion++; }
            }
            finally
            {
                lock (sync) { if (tickRequest == req) tickRequest = null; }
                try { req.Dispose(); } catch { }
                barTimes = null;
                RequestRepaint();
            }
        }

        private void RequestRepaint()
        {
            ChartControl cc = ChartControl;
            if (cc == null || terminated) return;
            cc.Dispatcher.InvokeAsync(new Action(() => { if (!terminated) ForceRefresh(); }));
        }

        #endregion

        #region Real-time

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            if (IsFirstTickOfBar && Bars.IsFirstBarOfSession)
            {
                lock (sync)
                {
                    if (sessionStarts.Count == 0 || sessionStarts[sessionStarts.Count - 1] != CurrentBar)
                        sessionStarts.Add(CurrentBar);
                }
            }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last || ChartControl == null || State != State.Realtime)
                return;

            double p   = e.Price;
            bool isAsk = Classify(p, e.Ask, e.Bid, ref rtPrev, ref rtLastAsk);
            int bar    = MapRealtimeBar(e.Time);
            int tick   = ToTick(p);
            int vol    = ClampVol(e.Volume);

            lock (sync)
            {
                if (!histReady)
                {
                    pending.Add(new PendingTick { Time = e.Time.Ticks, Bar = bar, Tick = tick, Vol = vol, IsAsk = isAsk });
                }
                else
                {
                    AddToStore(store, bar, tick, vol, isAsk);
                    if (bar < firstRtBar) firstRtBar = bar;
                    rtVersion++;
                }
            }
        }

        private int MapRealtimeBar(DateTime time)
        {
            int last = Bars.Count - 1;
            if (last < 0) return 0;
            // Time-based bars: a tick later than the last bar's end-time belongs to the next (not yet created) bar.
            if (isTimeBased && time > Bars.GetTime(last)) return last + 1;
            return last;
        }

        #endregion

        #region Storage helpers

        private int ToTick(double price) { return (int)Math.Round(price / tickSize); }

        private static int ClampVol(long v) { return v > int.MaxValue ? int.MaxValue : (v < 0 ? 0 : (int)v); }

        private static bool Classify(double p, double ask, double bid, ref double prev, ref bool lastAsk)
        {
            bool r;
            if (ask > 0 && bid > 0 && ask > bid)
            {
                if (p >= ask)      r = true;
                else if (p <= bid) r = false;
                else               r = p > prev ? true : (p < prev ? false : lastAsk);   // inside spread -> tick rule
            }
            else
            {
                r = prev == 0 ? true : (p > prev ? true : (p < prev ? false : lastAsk)); // no quote -> tick rule
            }
            prev = p;
            lastAsk = r;
            return r;
        }

        // Layout: a[0] = base tick, a[1 + 2k] = ask vol at base+k, a[2 + 2k] = bid vol at base+k
        private static void AddToStore(List<int[]> s, int bar, int tick, int vol, bool isAsk)
        {
            if (bar < 0 || vol <= 0) return;
            while (s.Count <= bar) s.Add(null);

            int[] a = s[bar];
            if (a == null)
            {
                a = new int[1 + 2 * 8];
                a[0] = tick - 3;
                s[bar] = a;
            }

            int levels = (a.Length - 1) >> 1;
            int off    = tick - a[0];

            if (off < 0)
            {
                int grow = -off + 4;
                int[] na = new int[a.Length + 2 * grow];
                na[0] = a[0] - grow;
                Array.Copy(a, 1, na, 1 + 2 * grow, a.Length - 1);
                a = na; s[bar] = a;
                off = tick - a[0];
            }
            else if (off >= levels)
            {
                int grow = off - levels + 1 + 4;
                int[] na = new int[a.Length + 2 * grow];
                na[0] = a[0];
                Array.Copy(a, 1, na, 1, a.Length - 1);
                a = na; s[bar] = a;
            }

            int idx = 1 + 2 * off + (isAsk ? 0 : 1);
            long nv = (long)a[idx] + vol;
            a[idx]  = nv > int.MaxValue ? int.MaxValue : (int)nv;
        }

        private static int[] Trim(int[] a)
        {
            if (a == null) return null;
            int levels = (a.Length - 1) >> 1;
            int first = -1, last = -1;
            for (int k = 0; k < levels; k++)
            {
                if (a[1 + 2 * k] != 0 || a[2 + 2 * k] != 0)
                {
                    if (first < 0) first = k;
                    last = k;
                }
            }
            if (first < 0) return null;
            if (first == 0 && last == levels - 1) return a;

            int n = last - first + 1;
            int[] na = new int[1 + 2 * n];
            na[0] = a[0] + first;
            Array.Copy(a, 1 + 2 * first, na, 1, 2 * n);
            return na;
        }

        private ProfileData GetProfile(int from, int to)
        {
            long key = ((long)from << 32) | (uint)to;
            ProfileData p;
            cache.TryGetValue(key, out p);

            lock (sync)
            {
                bool stale = p == null
                          || p.HistVersion != histVersion
                          || (to >= firstRtBar && p.RtVersion != rtVersion);

                if (stale)
                {
                    p = Build(from, to);
                    if (p == null) { cache.Remove(key); return null; }
                    cache[key] = p;
                }
            }
            p.LastFrame = frame;
            return p;
        }

        // caller holds sync
        private ProfileData Build(int from, int to)
        {
            int end = Math.Min(to, store.Count - 1);
            from    = Math.Max(0, from);
            if (end < from) return null;

            int lo = int.MaxValue, hi = int.MinValue;
            for (int b = from; b <= end; b++)
            {
                int[] a = store[b];
                if (a == null) continue;
                int levels = (a.Length - 1) >> 1;
                if (a[0] < lo) lo = a[0];
                if (a[0] + levels - 1 > hi) hi = a[0] + levels - 1;
            }
            if (lo > hi) return null;

            int span  = hi - lo + 1;
            long[] ask = new long[span];
            long[] bid = new long[span];

            for (int b = from; b <= end; b++)
            {
                int[] a = store[b];
                if (a == null) continue;
                int levels = (a.Length - 1) >> 1;
                int o = a[0] - lo;
                for (int k = 0; k < levels; k++)
                {
                    ask[o + k] += a[1 + 2 * k];
                    bid[o + k] += a[2 + 2 * k];
                }
            }

            return new ProfileData { MinTick = lo, Ask = ask, Bid = bid, HistVersion = histVersion, RtVersion = rtVersion };
        }

        #endregion

        #region Rendering

        public override void OnRenderTargetChanged()
        {
            DisposeBrushes();
            if (RenderTarget == null) return;

            dxVol   = MakeBrush(VolumeBrush,   VolumeOpacity);
            dxVaBar = MakeBrush(VaBarBrush,    VaBarOpacity);
            dxAsk = MakeBrush(AskBrush,      AskOpacity);
            dxBid = MakeBrush(BidBrush,      BidOpacity);
            dxPos = MakeBrush(DeltaPosBrush, DeltaPosOpacity);
            dxNeg = MakeBrush(DeltaNegBrush, DeltaNegOpacity);
            dxPoc = MakeBrush(PocBrush,      PocOpacity);
            dxVa  = MakeBrush(VaBrush,       VaOpacity);
        }

        private SharpDX.Direct2D1.Brush MakeBrush(Brush b, int opacityPct)
        {
            SharpDX.Direct2D1.Brush d = b.ToDxBrush(RenderTarget);
            d.Opacity = Math.Max(0f, Math.Min(1f, opacityPct / 100f));
            return d;
        }

        private void DisposeBrushes()
        {
            if (dxVol != null) { dxVol.Dispose(); dxVol = null; }
            if (dxVaBar != null) { dxVaBar.Dispose(); dxVaBar = null; }
            if (dxAsk != null) { dxAsk.Dispose(); dxAsk = null; }
            if (dxBid != null) { dxBid.Dispose(); dxBid = null; }
            if (dxPos != null) { dxPos.Dispose(); dxPos = null; }
            if (dxNeg != null) { dxNeg.Dispose(); dxNeg = null; }
            if (dxPoc != null) { dxPoc.Dispose(); dxPoc = null; }
            if (dxVa  != null) { dxVa.Dispose();  dxVa  = null; }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (ChartBars == null || RenderTarget == null || dxVol == null) return;

            string s = status;
            if (s != null) DrawStatus(chartControl, s);
            if (!histReady && store.Count == 0) return;

            frame++;
            SharpDX.Direct2D1.AntialiasMode oldAa = RenderTarget.AntialiasMode;
            RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;

            try
            {
                Bars bars   = ChartBars.Bars;
                int lastIdx = bars.Count - 1;
                float panelL = ChartPanel.X;
                float panelR = ChartPanel.X + ChartPanel.W;

                int from = -1, to = -1;
                if (Scope == NT8ProfileScope.Latest && lastIdx >= 0)
                {
                    List<int> starts = PeriodStarts(bars, lastIdx, Math.Max(0, HistoricalProfiles) + 1);

                    // Historical profiles: always anchored at their own period start,
                    // width capped so they never overlap the next period.
                    for (int k = 0; k < starts.Count - 1; k++)
                    {
                        float xs = BarLeftX(chartControl, starts[k]) + OffsetPx;
                        float xn = BarLeftX(chartControl, starts[k + 1]);
                        if (xn < panelL || xs > panelR) continue;          // off-screen: don't build
                        float w = Math.Min(ProfileWidthPx, xn - xs - 2);
                        RenderProfile(chartControl, chartScale, starts[k], starts[k + 1] - 1, xs, xs + w,
                                      ProfileType, false, false, 0, 0, xn);
                    }

                    from = starts[starts.Count - 1];
                    to   = int.MaxValue;
                }
                else if (Scope == NT8ProfileScope.Visible)
                {
                    from = ChartBars.FromIndex;
                    to   = ChartBars.ToIndex >= lastIdx ? int.MaxValue : ChartBars.ToIndex;
                }

                if (from >= 0)
                {
                    float xL, xR;
                    bool growLeft = false;
                    switch (Placement)
                    {
                        case NT8ProfilePlacement.RightMargin:
                            xR = panelR - OffsetPx;
                            xL = xR - ProfileWidthPx;
                            growLeft = true;
                            break;
                        case NT8ProfilePlacement.LeftMargin:
                            xL = panelL + OffsetPx;
                            xR = xL + ProfileWidthPx;
                            break;
                        default: // SessionStart: first bar of the profile period
                            xL = Math.Max(BarLeftX(chartControl, from), panelL) + OffsetPx;
                            xR = xL + ProfileWidthPx;
                            break;
                    }
                    RenderProfile(chartControl, chartScale, from, to, xL, xR, ProfileType, growLeft, false, 0, 0,
                                  growLeft ? panelL : panelR);
                }

                if (ShowCustomRanges)
                    RenderCustomRanges(chartControl, chartScale);
            }
            finally
            {
                RenderTarget.AntialiasMode = oldAa;
            }

            PruneCache();
        }

        private void RenderCustomRanges(ChartControl cc, ChartScale cs)
        {
            foreach (var obj in DrawObjects.ToList())
            {
                NT8ProfileRange r = obj as NT8ProfileRange;
                if (r == null || r.StartAnchor == null || r.EndAnchor == null) continue;

                ChartAnchor a1 = r.StartAnchor, a2 = r.EndAnchor;
                DateTime tA = a1.Time <= a2.Time ? a1.Time : a2.Time;
                DateTime tB = a1.Time <= a2.Time ? a2.Time : a1.Time;

                System.Windows.Point p1 = a1.GetPoint(cc, ChartPanel, cs);
                System.Windows.Point p2 = a2.GetPoint(cc, ChartPanel, cs);
                float xl = (float)Math.Min(p1.X, p2.X);
                float xr = (float)Math.Max(p1.X, p2.X);

                int i1 = ChartBars.GetBarIdxByTime(cc, tA);
                int i2 = ChartBars.GetBarIdxByTime(cc, tB);
                if (i2 < i1) continue;

                double lo = Math.Min(a1.Price, a2.Price);
                double hi = Math.Max(a1.Price, a2.Price);

                NT8ProfileType type = ProfileType;
                switch (r.ProfileType)
                {
                    case NT8ProfileRangeType.Volume:        type = NT8ProfileType.Volume; break;
                    case NT8ProfileRangeType.AskBid:        type = NT8ProfileType.AskBid; break;
                    case NT8ProfileRangeType.Delta:         type = NT8ProfileType.Delta; break;
                    case NT8ProfileRangeType.DeltaAndTotal: type = NT8ProfileType.DeltaAndTotal; break;
                }

                RenderProfile(cc, cs, i1, i2, xl, xr, type, false, r.ClipToPriceRange, lo, hi,
                              ChartPanel.X + ChartPanel.W);
            }
        }

        private float BarLeftX(ChartControl cc, int idx)
        {
            return cc.GetXByBarIndex(ChartBars, idx) - cc.GetBarPaintWidth(ChartBars) / 2f;
        }

        // Start bar indices of the last `count` periods, oldest first. The last entry is the current period.
        private List<int> PeriodStarts(Bars bars, int lastIdx, int count)
        {
            var res = new List<int>(count);

            if (LengthType == NT8ProfileLength.Sessions)
            {
                int len = Math.Max(1, LengthValue);
                lock (sync)
                {
                    int S = sessionStarts.Count;
                    if (S == 0) { res.Add(0); return res; }
                    int idx = S - len;
                    while (res.Count < count)
                    {
                        res.Add(sessionStarts[Math.Max(0, idx)]);
                        if (idx <= 0) break;
                        idx -= len;
                    }
                }
            }
            else if (LengthType == NT8ProfileLength.Minutes)
            {
                // Rolling: the current profile = the last N minutes up to the latest bar.
                // Historical profiles = the contiguous N-minute windows before it.
                // Bar time = bar END time, so a bar belongs to the window if it ends after the cutoff.
                TimeSpan win = TimeSpan.FromMinutes(Math.Max(1, LengthValue));
                DateTime end = bars.GetTime(lastIdx);
                int i = lastIdx;
                while (res.Count < count && i >= 0)
                {
                    DateTime cutoff = end - win;
                    int s = i;
                    while (s > 0 && bars.GetTime(s - 1) > cutoff) s--;
                    res.Add(s);
                    i   = s - 1;
                    end = cutoff;
                }
                if (res.Count == 0) res.Add(0);
            }
            else // ClockMinutes: blocks aligned to the clock (e.g. :00, :15, :30, :45)
            {
                int i = lastIdx;
                while (res.Count < count && i >= 0)
                {
                    int s = MinuteBlockStart(bars, i);
                    res.Add(s);
                    i = s - 1;
                }
                if (res.Count == 0) res.Add(0);
            }

            res.Reverse();
            return res;
        }

        // First bar of the N-minute block (aligned to midnight) containing bar i.
        private int MinuteBlockStart(Bars bars, int i)
        {
            int n        = Math.Max(1, Math.Min(1440, LengthValue));
            DateTime lt  = bars.GetTime(i);
            double mins  = (lt - lt.Date).TotalMinutes;
            double endM  = Math.Ceiling(mins / n) * n;
            DateTime blockStart = lt.Date.AddMinutes(endM - n);
            while (i > 0 && bars.GetTime(i - 1) > blockStart) i--;
            return i;
        }

        private int ComputeRowTicks(ChartScale cs)
        {
            int factor = Math.Max(1, GroupFactor);
            if (Grouping == NT8ProfileGrouping.Manual)
                return Math.Max(1, ManualTicks) * factor;

            // NOTE: ChartScale.GetPixelsForDistance() returns an INTEGER pixel count. Once a tick is
            // smaller than ~0.5 px it rounds to 0, the old Math.Max(0.0001, 0) guard turned that into
            // need = 30,000 ticks per row, and the whole profile collapsed into ONE row starting at
            // price 0 -> a solid POC-coloured block topped at a round price (e.g. 7500).
            // Compute pixels-per-tick in floating point from the full visible scale instead.
            double range = cs.MaxValue - cs.MinValue;
            double pxPerTick = (range > 0 && cs.Height > 0)
                             ? cs.Height / (range / tickSize)
                             : 1.0;
            if (double.IsNaN(pxPerTick) || double.IsInfinity(pxPerTick) || pxPerTick <= 0) pxPerTick = 1.0;

            int need = (int)Math.Ceiling(AutoMinRowPx / pxPerTick);
            need = Math.Max(1, Math.Min(need, NiceSteps[NiceSteps.Length - 1]));
            int step = need;
            for (int i = 0; i < NiceSteps.Length; i++)
                if (NiceSteps[i] >= need) { step = NiceSteps[i]; break; }
            return Math.Max(1, step) * factor;
        }

        private static int FloorDiv(int a, int b) { return a >= 0 ? a / b : -((-a + b - 1) / b); }

        private void RenderProfile(ChartControl cc, ChartScale cs, int from, int to, float xL, float xR,
                                   NT8ProfileType type, bool growLeft, bool clip, double clipLo, double clipHi,
                                   float extendEdge)
        {
            float W = xR - xL;
            if (W < 4) return;

            ProfileData p = GetProfile(from, to);
            if (p == null) return;

            int loTick = p.MinTick;
            int hiTick = p.MinTick + p.Ask.Length - 1;
            if (clip)
            {
                loTick = Math.Max(loTick, ToTick(clipLo));
                hiTick = Math.Min(hiTick, ToTick(clipHi));
                if (loTick > hiTick) return;
            }

            // Safety cap: never let one row swallow the profile — keep at least ~8 rows
            // (unless Manual grouping explicitly asks for coarser rows).
            int rowTicks = ComputeRowTicks(cs);
            if (Grouping == NT8ProfileGrouping.Automatic)
                rowTicks = Math.Max(1, Math.Min(rowTicks, (hiTick - loTick + 1) / 8));

            int r0 = FloorDiv(loTick, rowTicks);
            int r1 = FloorDiv(hiTick, rowTicks);
            int n  = r1 - r0 + 1;
            if (rowAsk.Length < n) { rowAsk = new long[n]; rowBid = new long[n]; }
            Array.Clear(rowAsk, 0, n);
            Array.Clear(rowBid, 0, n);

            for (int t = loTick; t <= hiTick; t++)
            {
                int k = t - p.MinTick;
                int r = FloorDiv(t, rowTicks) - r0;
                rowAsk[r] += p.Ask[k];
                rowBid[r] += p.Bid[k];
            }

            // ---- stats ----
            long maxTot = 0, maxSide = 0, maxAbsD = 0, sum = 0;
            int poc = -1;
            for (int r = 0; r < n; r++)
            {
                long a = rowAsk[r], b = rowBid[r], tot = a + b;
                sum += tot;
                if (tot > maxTot) { maxTot = tot; poc = r; }
                if (a > maxSide) maxSide = a;
                if (b > maxSide) maxSide = b;
                long ad = Math.Abs(a - b);
                if (ad > maxAbsD) maxAbsD = ad;
            }
            if (maxTot == 0 || poc < 0) return;

            int vaLo = poc, vaHi = poc;
            if (ShowValueArea)
            {
                long acc    = rowAsk[poc] + rowBid[poc];
                long target = (long)Math.Ceiling(sum * ValueAreaPct / 100.0);
                while (acc < target && (vaLo > 0 || vaHi < n - 1))
                {
                    long up = vaHi < n - 1 ? rowAsk[vaHi + 1] + rowBid[vaHi + 1] : -1;
                    long dn = vaLo > 0     ? rowAsk[vaLo - 1] + rowBid[vaLo - 1] : -1;
                    if (up >= dn) { vaHi++; acc += up; }
                    else          { vaLo--; acc += dn; }
                }
            }

            // ---- rows ----
            double half    = tickSize * 0.5;
            float panelTop = ChartPanel.Y;
            float panelBot = ChartPanel.Y + ChartPanel.H;
            float halfW    = W / 2f;
            float xMid     = xL + halfW;
            float wD       = W * Math.Max(5, Math.Min(95, DeltaSharePct)) / 100f;
            float xSplit   = xL + wD;
            float pocY = 0, pocH = 0;

            for (int r = 0; r < n; r++)
            {
                double bottomPrice = (double)(r0 + r) * rowTicks * tickSize;
                double topPrice    = bottomPrice + (rowTicks - 1) * tickSize;
                float yTop = cs.GetYByValue(topPrice + half);
                float yBot = cs.GetYByValue(bottomPrice - half);
                float h    = Math.Max(1f, yBot - yTop);

                if (r == poc) { pocY = yTop; pocH = h; }
                if (yBot < panelTop || yTop > panelBot) continue;

                long a = rowAsk[r], b = rowBid[r], tot = a + b;
                if (tot == 0) continue;

                float hd    = h > 3 ? h - 1 : h;   // 1px row gap when there is room
                bool isPoc  = ShowPoc && HighlightPoc && r == poc;
                bool inVa   = ShowValueArea && ColorValueAreaBars && r >= vaLo && r <= vaHi;
                long d      = a - b;
                SharpDX.Direct2D1.Brush dBrush = d >= 0 ? dxPos : dxNeg;
                SharpDX.Direct2D1.Brush vBrush = isPoc ? dxPoc : (inVa ? dxVaBar : dxVol);

                switch (type)
                {
                    case NT8ProfileType.Volume:
                    {
                        float len = (float)tot / maxTot * W;
                        Bar(growLeft ? xR : xL, len, growLeft, yTop, hd, vBrush);
                        break;
                    }
                    case NT8ProfileType.AskBid:
                    {
                        if (AskBidStyle == NT8ProfileAskBidStyle.Stacked)
                        {
                            float lb = (float)b / maxTot * W;
                            float la = (float)a / maxTot * W;
                            if (growLeft)
                            {
                                Bar(xR, lb, true, yTop, hd, dxBid);
                                Bar(xR - lb, la, true, yTop, hd, dxAsk);
                            }
                            else
                            {
                                Bar(xL, lb, false, yTop, hd, dxBid);
                                Bar(xL + lb, la, false, yTop, hd, dxAsk);
                            }
                        }
                        else if (maxSide > 0)
                        {
                            Bar(xMid, (float)b / maxSide * halfW, true,  yTop, hd, dxBid);
                            Bar(xMid, (float)a / maxSide * halfW, false, yTop, hd, dxAsk);
                        }
                        if (isPoc) Outline(xL, xR, yTop, hd);
                        break;
                    }
                    case NT8ProfileType.Delta:
                    {
                        // single-sided: |delta| grows from the anchor edge, colour = sign
                        if (maxAbsD > 0)
                            Bar(growLeft ? xR : xL, (float)Math.Abs(d) / maxAbsD * W, growLeft, yTop, hd, dBrush);
                        if (isPoc) Outline(xL, xR, yTop, hd);
                        break;
                    }
                    case NT8ProfileType.DeltaAndTotal:
                    {
                        if (maxAbsD > 0)
                            Bar(xSplit, (float)Math.Abs(d) / maxAbsD * wD, true, yTop, hd, dBrush);
                        Bar(xSplit, (float)tot / maxTot * (W - wD), false, yTop, hd, vBrush);
                        break;
                    }
                }
            }

            // centre axis for diverging layouts
            if (type == NT8ProfileType.AskBid && AskBidStyle == NT8ProfileAskBidStyle.Split)
                VLine(xMid, cs, loTick, hiTick);
            else if (type == NT8ProfileType.DeltaAndTotal)
                VLine(xSplit, cs, loTick, hiTick);

            // ---- POC line ----
            if (ShowPoc && ShowPocLine)
            {
                float y  = pocY + pocH / 2f;
                float x1 = xL, x2 = xR;
                if (ExtendPocLine)
                {
                    if (growLeft) x1 = Math.Min(x1, extendEdge);
                    else          x2 = Math.Max(x2, extendEdge);
                }
                RenderTarget.DrawLine(new SharpDX.Vector2(x1, y), new SharpDX.Vector2(x2, y), dxPoc, Math.Max(1, PocLineWidth));
            }

            // ---- Value area ----
            if (ShowValueArea)
            {
                float yVah = cs.GetYByValue(((double)(r0 + vaHi) * rowTicks + rowTicks - 1) * tickSize + half);
                float yVal = cs.GetYByValue((double)(r0 + vaLo) * rowTicks * tickSize - half);
                RenderTarget.DrawLine(new SharpDX.Vector2(xL, yVah), new SharpDX.Vector2(xR, yVah), dxVa, 1f);
                RenderTarget.DrawLine(new SharpDX.Vector2(xL, yVal), new SharpDX.Vector2(xR, yVal), dxVa, 1f);
            }
        }

        private void Bar(float xFrom, float len, bool leftward, float y, float h, SharpDX.Direct2D1.Brush brush)
        {
            if (len < 0.5f) return;
            float x = leftward ? xFrom - len : xFrom;
            RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, len, h), brush);
        }

        private void Outline(float xL, float xR, float y, float h)
        {
            RenderTarget.DrawRectangle(new SharpDX.RectangleF(xL, y, xR - xL, h), dxPoc, 1f);
        }

        private void VLine(float x, ChartScale cs, int loTick, int hiTick)
        {
            float yTop = cs.GetYByValue(hiTick * tickSize + tickSize * 0.5);
            float yBot = cs.GetYByValue(loTick * tickSize - tickSize * 0.5);
            RenderTarget.DrawLine(new SharpDX.Vector2(x, yTop), new SharpDX.Vector2(x, yBot), dxVol, 1f);
        }

        private void DrawStatus(ChartControl cc, string text)
        {
            using (SharpDX.DirectWrite.TextFormat tf = cc.Properties.LabelFont.ToDirectWriteTextFormat())
            using (SharpDX.Direct2D1.Brush br = cc.Properties.ChartText.ToDxBrush(RenderTarget))
            {
                RenderTarget.DrawText(text, tf, new SharpDX.RectangleF(ChartPanel.X + 6, ChartPanel.Y + 6, 600, 22), br);
            }
        }

        private void PruneCache()
        {
            if (cache.Count < 8) return;
            List<long> dead = null;
            foreach (KeyValuePair<long, ProfileData> kv in cache)
                if (kv.Value.LastFrame < frame - 1)
                    (dead ?? (dead = new List<long>())).Add(kv.Key);
            if (dead != null)
                foreach (long k in dead) cache.Remove(k);
        }

        #endregion

        #region Properties

        // ---------- 1. General ----------
        [Display(Name = "Profile type", Order = 1, GroupName = "1. General")]
        public NT8ProfileType ProfileType { get; set; }

        [Display(Name = "Scope", Description = "Latest period, visible range, or only custom range drawings.", Order = 2, GroupName = "1. General")]
        public NT8ProfileScope Scope { get; set; }

        [Display(Name = "Length type (Latest)", Order = 3, GroupName = "1. General")]
        public NT8ProfileLength LengthType { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "Length value (Latest)", Description = "Sessions: last N sessions. Minutes: rolling last N minutes up to the latest bar. ClockMinutes: current clock-aligned N-minute block (e.g. 14:45-15:00).", Order = 4, GroupName = "1. General")]
        public int LengthValue { get; set; }

        [Range(0, 250)]
        [Display(Name = "Historical profiles to show", Description = "Latest scope: number of past sessions (or periods, per Length type/value) drawn before the current one. 0 = current only. Tick load is extended automatically to cover them.", Order = 5, GroupName = "1. General")]
        public int HistoricalProfiles { get; set; }

        [Display(Name = "Show custom range profiles", Order = 6, GroupName = "1. General")]
        public bool ShowCustomRanges { get; set; }

        // ---------- 2. Data ----------
        [Range(1, 60)]
        [Display(Name = "Tick sessions to load", Description = "Sessions of tick data requested once at load. Lower = faster load, less peak memory.", Order = 1, GroupName = "2. Data")]
        public int TickDaysToLoad { get; set; }

        [Display(Name = "Tick grouping", Order = 2, GroupName = "2. Data")]
        public NT8ProfileGrouping Grouping { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "Manual ticks per row", Order = 3, GroupName = "2. Data")]
        public int ManualTicks { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "Group factor", Description = "Multiplier on the (auto or manual) row size.", Order = 4, GroupName = "2. Data")]
        public int GroupFactor { get; set; }

        [Range(1, 50)]
        [Display(Name = "Auto min row height (px)", Order = 5, GroupName = "2. Data")]
        public int AutoMinRowPx { get; set; }

        // ---------- 3. Layout ----------
        [Range(20, 2000)]
        [Display(Name = "Profile width (px)", Description = "Latest/Visible scopes. Custom ranges use the rectangle width.", Order = 1, GroupName = "3. Layout")]
        public int ProfileWidthPx { get; set; }

        [Display(Name = "Placement", Description = "Latest/Visible scopes: anchor at the session (period) start, the left margin or the right margin.", Order = 2, GroupName = "3. Layout")]
        public NT8ProfilePlacement Placement { get; set; }

        [Range(-2000, 2000)]
        [Display(Name = "Offset (px)", Description = "Shift from the anchor. Positive = inward (right of session start / left margin, left of right margin).", Order = 3, GroupName = "3. Layout")]
        public int OffsetPx { get; set; }

        [Display(Name = "Ask/Bid style", Order = 4, GroupName = "3. Layout")]
        public NT8ProfileAskBidStyle AskBidStyle { get; set; }

        [Range(5, 95)]
        [Display(Name = "Delta share % (Delta+Total)", Order = 5, GroupName = "3. Layout")]
        public int DeltaSharePct { get; set; }

        [Display(Name = "Layer", Description = "Background = behind price bars. Foreground = on top of price bars and drawings.", Order = 6, GroupName = "3. Layout")]
        public NT8ProfileLayer Layer { get; set; }

        // ---------- 4. POC / Value area ----------
        [Display(Name = "Show POC", Order = 1, GroupName = "4. POC / Value area")]
        public bool ShowPoc { get; set; }

        [Display(Name = "Highlight POC row", Order = 2, GroupName = "4. POC / Value area")]
        public bool HighlightPoc { get; set; }

        [Display(Name = "Show POC line", Order = 3, GroupName = "4. POC / Value area")]
        public bool ShowPocLine { get; set; }

        [Display(Name = "Extend POC line to window edge", Order = 4, GroupName = "4. POC / Value area")]
        public bool ExtendPocLine { get; set; }

        [Range(1, 10)]
        [Display(Name = "POC line width", Order = 5, GroupName = "4. POC / Value area")]
        public int PocLineWidth { get; set; }

        [Display(Name = "Show value area", Order = 6, GroupName = "4. POC / Value area")]
        public bool ShowValueArea { get; set; }

        [Range(10, 99)]
        [Display(Name = "Value area %", Order = 7, GroupName = "4. POC / Value area")]
        public int ValueAreaPct { get; set; }

        [Display(Name = "Color value area bars", Description = "Volume bars inside the value area use the 'Value area bars' color.", Order = 8, GroupName = "4. POC / Value area")]
        public bool ColorValueAreaBars { get; set; }

        // ---------- 5. Colors ----------
        [XmlIgnore]
        [Display(Name = "Volume", Order = 1, GroupName = "5. Colors")]
        public Brush VolumeBrush { get; set; }
        [Browsable(false)]
        public string VolumeBrushSerializable { get { return Serialize.BrushToString(VolumeBrush); } set { VolumeBrush = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Volume opacity %", Order = 2, GroupName = "5. Colors")]
        public int VolumeOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Value area bars", Order = 3, GroupName = "5. Colors")]
        public Brush VaBarBrush { get; set; }
        [Browsable(false)]
        public string VaBarBrushSerializable { get { return Serialize.BrushToString(VaBarBrush); } set { VaBarBrush = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Value area bars opacity %", Order = 4, GroupName = "5. Colors")]
        public int VaBarOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Ask (buy aggressor)", Order = 5, GroupName = "5. Colors")]
        public Brush AskBrush { get; set; }
        [Browsable(false)]
        public string AskBrushSerializable { get { return Serialize.BrushToString(AskBrush); } set { AskBrush = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Ask (buy aggressor) opacity %", Order = 6, GroupName = "5. Colors")]
        public int AskOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Bid (sell aggressor)", Order = 7, GroupName = "5. Colors")]
        public Brush BidBrush { get; set; }
        [Browsable(false)]
        public string BidBrushSerializable { get { return Serialize.BrushToString(BidBrush); } set { BidBrush = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Bid (sell aggressor) opacity %", Order = 8, GroupName = "5. Colors")]
        public int BidOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Delta positive", Order = 9, GroupName = "5. Colors")]
        public Brush DeltaPosBrush { get; set; }
        [Browsable(false)]
        public string DeltaPosBrushSerializable { get { return Serialize.BrushToString(DeltaPosBrush); } set { DeltaPosBrush = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Delta positive opacity %", Order = 10, GroupName = "5. Colors")]
        public int DeltaPosOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Delta negative", Order = 11, GroupName = "5. Colors")]
        public Brush DeltaNegBrush { get; set; }
        [Browsable(false)]
        public string DeltaNegBrushSerializable { get { return Serialize.BrushToString(DeltaNegBrush); } set { DeltaNegBrush = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Delta negative opacity %", Order = 12, GroupName = "5. Colors")]
        public int DeltaNegOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "POC", Order = 13, GroupName = "5. Colors")]
        public Brush PocBrush { get; set; }
        [Browsable(false)]
        public string PocBrushSerializable { get { return Serialize.BrushToString(PocBrush); } set { PocBrush = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "POC opacity %", Order = 14, GroupName = "5. Colors")]
        public int PocOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Value area lines", Order = 15, GroupName = "5. Colors")]
        public Brush VaBrush { get; set; }
        [Browsable(false)]
        public string VaBrushSerializable { get { return Serialize.BrushToString(VaBrush); } set { VaBrush = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Value area lines opacity %", Order = 16, GroupName = "5. Colors")]
        public int VaOpacity { get; set; }

        #endregion
    }
}
