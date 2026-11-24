// ImportantLevels.cs  —  NinjaTrader 8 indicator  (v2)
// Daily / Weekly / Monthly key levels: Open, High, Low, Close, Mid (average), POC, VAH/VAL, VWAP
// + London session High / Low as of the New York open.
//
// v2 changes
//  - Calculation is fully independent of the chart's trading hours. The internal series always loads the
//    instrument's full (ETH) session; RTH / Custom are applied as time windows in a selectable time zone.
//    An RTH chart and an ETH chart therefore draw identical levels for the same input settings.
//  - London High / Low: range from London open (London time) to NY open (New York time), DST-correct for
//    both cities, drawn from the NY open onward.
//
// Install: Documents\NinjaTrader 8\bin\Custom\Indicators\  then compile (F5 in the NinjaScript Editor).

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
using NinjaTrader.NinjaScript;
using ImportantLevelsTypes;
#endregion

namespace ImportantLevelsTypes
{
    public enum ILFilterTime { ETH, RTH, Custom }
    public enum ILTimeZone { NewYork, Chicago, London, Exchange, Local }
    public enum ILPlotType { Label, Line, LabelAndLine }
    public enum ILTextAlign { Left, Right, CurrentToRight, LastVisibleBar, LineStart }
    public enum ILLineExtent { FullWidth, FromPeriod, CurrentToRight }
    public enum ILPeriod { Daily = 0, Weekly = 1, Monthly = 2, London = 3 }
    public enum ILLevel { Mid = 0, Low = 1, High = 2, Open = 3, Close = 4, POC = 5, VAH = 6, VAL = 7, VWAP = 8 }
}

namespace NinjaTrader.NinjaScript.Indicators
{
    public class ImportantLevels : Indicator
    {
        #region Internal types
        private const int LevelCount = 9;
        private const int ColorLondonHigh = 9, ColorLondonLow = 10, ColorCount = 11;
        private static readonly string[] LevelNames = { "Mid", "Low", "High", "Open", "Close", "POC", "VAH", "VAL", "VWAP" };
        private static readonly string[] PeriodPrefix = { "D", "W", "M", "LDN" };

        private sealed class Period
        {
            public ILPeriod Kind;
            public DateTime Key;
            public DateTime FirstTime;          // local time, start of first included calc bar
            public DateTime LastTime;           // local time, end of last included calc bar
            public DateTime NyOpenLocal;        // London periods only
            public double Open, Close;
            public double High = double.MinValue, Low = double.MaxValue;
            public double SumPV, SumV;
            public bool HasData, Partial, Finalized;
            public int MinRow = int.MaxValue, MaxRow = int.MinValue;
            public readonly Dictionary<int, double> Profile = new Dictionary<int, double>();
            public double[] Levels;              // cached once finalized
        }

        private sealed class BarData
        {
            public DateTime Start, End;
            public double O, H, L, C, V, TP;
            public int RowLo, RowHi;
            public Period Day, Week, Month, London;   // null when the bar is outside that period's session window
        }

        private sealed class LevelItem
        {
            public double Price;
            public int ColorIdx;
            public ILPeriod Period;
            public bool Developing;
            public string Name;
            public DateTime Origin;
        }

        private sealed class VaZone
        {
            public double Vah, Val;
            public DateTime Origin;
        }

        private sealed class Snapshot
        {
            public readonly List<LevelItem> Items = new List<LevelItem>();
            public readonly List<VaZone> Zones = new List<VaZone>();
        }

        private sealed class LabelReq
        {
            public float Y, OriginX;
            public double Price;
            public int ColorIdx;
            public readonly List<string> Names = new List<string>();
        }
        #endregion

        #region Fields
        private List<Period> days, weeks, months, londons;
        private BarData pending;
        private int lastCommitted = -1;
        private SessionIterator sessionIterator;
        private DateTime curTradingDay = DateTime.MinValue;
        private DateTime lastBarEnd = DateTime.MinValue;
        private bool haveBar0, bar0SessionStart;
        private DateTime td0, bar0Start;
        private TimeZoneInfo localTz, exchangeTz, sessionTz, nyTz, londonTz;
        private DateTime winDay = DateTime.MinValue, ldnStartLocal, nyOpenLocal;
        private bool[][] enabled;
        private volatile Snapshot snapshot;
        private DateTime lastBuildUtc = DateTime.MinValue;
        private bool snapshotDirty;
        private double lastAlertPrice = double.NaN;

        // SharpDX
        private bool dxReady;
        private SharpDX.Direct2D1.Brush[] dxLineBrushes, dxTextBrushes;
        private SharpDX.Direct2D1.Brush dxVaFill, dxLabelBg;
        private SharpDX.Direct2D1.StrokeStyle[] dxStrokes;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Daily / weekly / monthly important levels (OHLC, mid, POC, value area, VWAP) and London High/Low at the NY open. Chart-session independent.";
                Name = "ImportantLevels";
                Calculate = Calculate.OnPriceChange;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;
                ScaleJustification = ScaleJustification.Right;
                BarsRequiredToPlot = 0;

                // Length
                Days = 1; Weeks = 1; Months = 1; SkipLast = false;

                // Plot
                LabelFont = new SimpleFont("Arial", 11);
                PlotType = ILPlotType.LabelAndLine;
                TextAlign = ILTextAlign.Right;
                LineExtent = ILLineExtent.FromPeriod;
                ShowPriceInLabel = true;
                LineOpacity = 85;
                LabelBackgroundBrush = Brushes.Black;
                LabelBackgroundOpacity = 60;
                ValueAreaFillOpacity = 0;
                DailyWidth = 1; DailyDash = DashStyleHelper.Solid;
                WeeklyWidth = 2; WeeklyDash = DashStyleHelper.Dash;
                MonthlyWidth = 2; MonthlyDash = DashStyleHelper.DashDot;

                // Daily / Weekly / Monthly levels
                DMid = false; DLow = true; DHigh = true; DOpen = false; DClose = true; DPoc = true; DValueArea = true; DVwap = false;
                WMid = false; WLow = true; WHigh = true; WOpen = false; WClose = false; WPoc = true; WValueArea = false; WVwap = false;
                MMid = false; MLow = true; MHigh = true; MOpen = false; MClose = false; MPoc = true; MValueArea = false; MVwap = false;

                // Colors
                LowBrush = Brushes.IndianRed;
                HighBrush = Brushes.MediumSeaGreen;
                OpenBrush = Brushes.Gold;
                CloseBrush = Brushes.SkyBlue;
                MidBrush = Brushes.Silver;
                PocBrush = Brushes.Orange;
                ValueAreaBrush = Brushes.MediumPurple;
                VwapBrush = Brushes.DeepPink;

                // Session
                FilterTime = ILFilterTime.ETH;
                EthTemplateName = string.Empty;
                SessionTimeZone = ILTimeZone.NewYork;
                RthStart = DateTime.Parse("09:30", System.Globalization.CultureInfo.InvariantCulture);
                RthEnd = DateTime.Parse("16:00", System.Globalization.CultureInfo.InvariantCulture);
                CustomStart = DateTime.Parse("09:30", System.Globalization.CultureInfo.InvariantCulture);
                CustomEnd = DateTime.Parse("16:00", System.Globalization.CultureInfo.InvariantCulture);
                ValueAreaPercent = 70;
                ProfileRowTicks = 1;
                CalcBarMinutes = 1;

                // London range
                ShowLondon = true;
                LondonOpen = DateTime.Parse("08:00", System.Globalization.CultureInfo.InvariantCulture);
                NyOpen = DateTime.Parse("09:30", System.Globalization.CultureInfo.InvariantCulture);
                LondonDays = 0;
                ShowDevelopingLondon = false;
                LondonHighBrush = Brushes.DodgerBlue;
                LondonLowBrush = Brushes.DodgerBlue;
                LondonWidth = 2;
                LondonDash = DashStyleHelper.Solid;

                // Alerts
                AlertOnCross = false;
                AlertSoundFile = "Alert2.wav";
                AlertRearmSeconds = 60;
            }
            else if (State == State.Configure)
            {
                AddCalcSeries();
            }
            else if (State == State.DataLoaded)
            {
                days = new List<Period>();
                weeks = new List<Period>();
                months = new List<Period>();
                londons = new List<Period>();
                pending = null;
                lastCommitted = -1;
                haveBar0 = false;
                winDay = DateTime.MinValue;
                snapshot = null;
                sessionIterator = new SessionIterator(BarsArray[1]);

                localTz = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
                exchangeTz = BarsArray[1].TradingHours.TimeZoneInfo;
                nyTz = FindTz("Eastern Standard Time", exchangeTz);
                londonTz = FindTz("GMT Standard Time", exchangeTz);
                sessionTz = ResolveTz(SessionTimeZone);

                enabled = new bool[3][];
                enabled[0] = BuildEnabled(DMid, DLow, DHigh, DOpen, DClose, DPoc, DValueArea, DVwap);
                enabled[1] = BuildEnabled(WMid, WLow, WHigh, WOpen, WClose, WPoc, WValueArea, WVwap);
                enabled[2] = BuildEnabled(MMid, MLow, MHigh, MOpen, MClose, MPoc, MValueArea, MVwap);
            }
            else if (State == State.Realtime)
            {
                if (days != null) RebuildSnapshot();
            }
            else if (State == State.Terminated)
            {
                DisposeDx();
            }
        }

        // Always the instrument's full session (never the chart's template), so RTH and ETH charts compute identically.
        private void AddCalcSeries()
        {
            int mins = Math.Max(1, CalcBarMinutes);
            string th = null;
            if (!string.IsNullOrWhiteSpace(EthTemplateName)) th = EthTemplateName.Trim();
            else if (Instrument != null) th = Instrument.MasterInstrument.TradingHours.Name;

            if (!string.IsNullOrEmpty(th) && NinjaTrader.Data.TradingHours.Get(th) == null)
            {
                Log("ImportantLevels: trading hours template '" + th + "' not found, using the instrument default.", LogLevel.Warning);
                th = Instrument != null ? Instrument.MasterInstrument.TradingHours.Name : null;
            }

            if (string.IsNullOrEmpty(th) || Instrument == null)
                AddDataSeries(BarsPeriodType.Minute, mins);
            else
                AddDataSeries(Instrument.FullName, new BarsPeriod { BarsPeriodType = BarsPeriodType.Minute, Value = mins }, th);
        }

        private static TimeZoneInfo FindTz(string id, TimeZoneInfo fallback)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { return fallback; }
        }

        private TimeZoneInfo ResolveTz(ILTimeZone z)
        {
            switch (z)
            {
                case ILTimeZone.NewYork: return nyTz;
                case ILTimeZone.London: return londonTz;
                case ILTimeZone.Chicago: return FindTz("Central Standard Time", exchangeTz);
                case ILTimeZone.Local: return localTz;
                default: return exchangeTz;
            }
        }

        private DateTime ToLocal(DateTime t, TimeZoneInfo from)
        {
            try { return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), from, localTz); }
            catch { return t; }
        }

        private static bool[] BuildEnabled(bool mid, bool low, bool high, bool open, bool close, bool poc, bool va, bool vwap)
        {
            var e = new bool[LevelCount];
            e[(int)ILLevel.Mid] = mid;
            e[(int)ILLevel.Low] = low;
            e[(int)ILLevel.High] = high;
            e[(int)ILLevel.Open] = open;
            e[(int)ILLevel.Close] = close;
            e[(int)ILLevel.POC] = poc;
            e[(int)ILLevel.VAH] = va;
            e[(int)ILLevel.VAL] = va;
            e[(int)ILLevel.VWAP] = vwap;
            return e;
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 1)
                ProcessCalcSeries();
            else if (BarsInProgress == 0 && AlertOnCross && State == State.Realtime && CurrentBars[0] >= 0)
                CheckAlerts();
        }

        #region Calculation
        private void ProcessCalcSeries()
        {
            int cur = CurrentBars[1];
            if (cur < 0) return;
            Bars b = BarsArray[1];
            bool committed = false;

            // Commit every bar before the current one exactly once (idempotent for any Calculate mode).
            while (lastCommitted < cur - 1)
            {
                lastCommitted++;
                BarData bd = BuildBar(b, lastCommitted);
                AddToPeriod(bd.Day, bd);
                AddToPeriod(bd.Week, bd);
                AddToPeriod(bd.Month, bd);
                AddToPeriod(bd.London, bd);
                committed = true;
            }

            // Current bar held as "pending" and re-read on every update (never double counted).
            pending = BuildBar(b, cur);
            lastBarEnd = pending.End;

            if (State == State.Historical)
            {
                if (cur >= b.Count - 2) RebuildSnapshot();
            }
            else if (committed || snapshotDirty || (DateTime.UtcNow - lastBuildUtc).TotalMilliseconds >= 250)
            {
                RebuildSnapshot();
            }
        }

        private BarData BuildBar(Bars b, int idx)
        {
            DateTime end = b.GetTime(idx);
            DateTime start = end.AddMinutes(-Math.Max(1, CalcBarMinutes));

            if (idx == 0 || b.IsFirstBarOfSessionByIndex(idx))
            {
                sessionIterator.GetNextSession(end, true);
                curTradingDay = sessionIterator.ActualTradingDayExchange.Date;
                if (idx == 0 && !haveBar0)
                {
                    haveBar0 = true;
                    td0 = curTradingDay;
                    bar0Start = start;
                    bar0SessionStart = start <= sessionIterator.ActualSessionBegin;
                }
            }

            var bd = new BarData
            {
                Start = start,
                End = end,
                O = b.GetOpen(idx),
                H = b.GetHigh(idx),
                L = b.GetLow(idx),
                C = b.GetClose(idx),
                V = (double)b.GetVolume(idx)
            };
            bd.TP = (bd.H + bd.L + bd.C) / 3.0;
            bd.RowLo = RowOf(bd.L);
            bd.RowHi = RowOf(bd.H);

            // Daily / weekly / monthly — subject to the selected session filter
            if (InFilterSession(start))
            {
                bd.Day = GetOrCreate(days, ILPeriod.Daily, curTradingDay, Days + 1);
                bd.Week = GetOrCreate(weeks, ILPeriod.Weekly, WeekKey(curTradingDay), Weeks + 1);
                bd.Month = GetOrCreate(months, ILPeriod.Monthly, MonthKey(curTradingDay), Months + 1);
            }

            // London range — always from full-session data, independent of the filter
            if (ShowLondon)
            {
                EnsureLondonWindow(curTradingDay);
                if (start >= ldnStartLocal && start < nyOpenLocal)
                    bd.London = GetOrCreate(londons, ILPeriod.London, curTradingDay, LondonDays + 1);
            }
            return bd;
        }

        private bool InFilterSession(DateTime localBarStart)
        {
            TimeSpan s, e;
            switch (FilterTime)
            {
                case ILFilterTime.RTH: s = RthStart.TimeOfDay; e = RthEnd.TimeOfDay; break;
                case ILFilterTime.Custom: s = CustomStart.TimeOfDay; e = CustomEnd.TimeOfDay; break;
                default: return true;   // ETH: whole instrument session
            }
            DateTime t;
            try { t = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(localBarStart, DateTimeKind.Unspecified), localTz, sessionTz); }
            catch { t = localBarStart; }
            TimeSpan tod = t.TimeOfDay;
            return s <= e ? (tod >= s && tod < e) : (tod >= s || tod < e);   // windows may cross midnight
        }

        // London open (London clock) -> NY open (New York clock) on the trading day's date, converted to local time.
        private void EnsureLondonWindow(DateTime td)
        {
            if (td == winDay) return;
            winDay = td;
            ldnStartLocal = ToLocal(td.Date + LondonOpen.TimeOfDay, londonTz);
            nyOpenLocal = ToLocal(td.Date + NyOpen.TimeOfDay, nyTz);
        }

        private Period GetOrCreate(List<Period> list, ILPeriod kind, DateTime key, int keep)
        {
            Period last = list.Count > 0 ? list[list.Count - 1] : null;
            if (last != null && (last.Key == key || key < last.Key)) return last;

            if (last != null && !last.Finalized)
            {
                last.Finalized = true;
                last.Levels = ComputeLevels(last, null);
                snapshotDirty = true;
            }

            var p = new Period { Kind = kind, Key = key, Partial = IsPartial(kind, key) };
            if (kind == ILPeriod.London) p.NyOpenLocal = nyOpenLocal;
            list.Add(p);
            while (list.Count > Math.Max(1, keep)) list.RemoveAt(0);
            return p;
        }

        private bool IsPartial(ILPeriod kind, DateTime key)
        {
            if (!haveBar0) return false;
            switch (kind)
            {
                case ILPeriod.Daily:
                    return key == td0 && !bar0SessionStart;
                case ILPeriod.Weekly:
                    return key == WeekKey(td0) && !(bar0SessionStart && td0.DayOfWeek == DayOfWeek.Monday);
                case ILPeriod.London:
                    return key == td0 && bar0Start > ldnStartLocal;
                default:
                    return key == MonthKey(td0) && !(bar0SessionStart && IsFirstWeekdayOfMonth(td0));
            }
        }

        private static bool IsFirstWeekdayOfMonth(DateTime d)
        {
            for (DateTime x = new DateTime(d.Year, d.Month, 1); x < d.Date; x = x.AddDays(1))
                if (x.DayOfWeek != DayOfWeek.Saturday && x.DayOfWeek != DayOfWeek.Sunday) return false;
            return true;
        }

        private static DateTime WeekKey(DateTime td) { return td.Date.AddDays(-(((int)td.DayOfWeek + 6) % 7)); }
        private static DateTime MonthKey(DateTime td) { return new DateTime(td.Year, td.Month, 1); }

        private int RowOf(double price)
        {
            long ticks = (long)Math.Round(price / TickSize);
            return (int)Math.Floor(ticks / (double)Math.Max(1, ProfileRowTicks));
        }

        private double RowBottom(int row) { return Instrument.MasterInstrument.RoundToTickSize((double)row * Math.Max(1, ProfileRowTicks) * TickSize); }
        private double RowTop(int row) { int rt = Math.Max(1, ProfileRowTicks); return Instrument.MasterInstrument.RoundToTickSize(((double)row * rt + rt - 1) * TickSize); }
        private double RowMid(int row) { int rt = Math.Max(1, ProfileRowTicks); return Instrument.MasterInstrument.RoundToTickSize(((double)row * rt + (rt - 1) / 2.0) * TickSize); }

        private static void AddToPeriod(Period p, BarData b)
        {
            if (p == null) return;
            if (!p.HasData)
            {
                p.HasData = true;
                p.Open = b.O;
                p.FirstTime = b.Start;
            }
            if (b.H > p.High) p.High = b.H;
            if (b.L < p.Low) p.Low = b.L;
            p.Close = b.C;
            p.LastTime = b.End;
            if (p.Kind == ILPeriod.London) return;   // London only needs the range

            p.SumPV += b.TP * b.V;
            p.SumV += b.V;
            if (b.RowLo < p.MinRow) p.MinRow = b.RowLo;
            if (b.RowHi > p.MaxRow) p.MaxRow = b.RowHi;

            if (b.V > 0)
            {
                double per = b.V / (b.RowHi - b.RowLo + 1);
                for (int r = b.RowLo; r <= b.RowHi; r++)
                {
                    double v;
                    p.Profile.TryGetValue(r, out v);
                    p.Profile[r] = v + per;
                }
            }
        }

        // All levels for a period, optionally including the in-progress (pending) bar without mutating state.
        private double[] ComputeLevels(Period p, BarData pb)
        {
            var r = new double[LevelCount];
            for (int i = 0; i < LevelCount; i++) r[i] = double.NaN;

            bool hasP = p.HasData, hasB = pb != null;
            if (!hasP && !hasB) return r;

            double high = hasP ? p.High : double.MinValue;
            double low = hasP ? p.Low : double.MaxValue;
            if (hasB) { high = Math.Max(high, pb.H); low = Math.Min(low, pb.L); }

            r[(int)ILLevel.Open] = hasP ? p.Open : pb.O;
            r[(int)ILLevel.High] = high;
            r[(int)ILLevel.Low] = low;
            r[(int)ILLevel.Close] = hasB ? pb.C : p.Close;
            r[(int)ILLevel.Mid] = Instrument.MasterInstrument.RoundToTickSize((high + low) / 2.0);
            if (p.Kind == ILPeriod.London) return r;

            double sumPV = (hasP ? p.SumPV : 0) + (hasB ? pb.TP * pb.V : 0);
            double sumV = (hasP ? p.SumV : 0) + (hasB ? pb.V : 0);
            if (sumV > 0) r[(int)ILLevel.VWAP] = Instrument.MasterInstrument.RoundToTickSize(sumPV / sumV);

            int minR = hasP ? p.MinRow : int.MaxValue;
            int maxR = hasP ? p.MaxRow : int.MinValue;
            if (hasB) { minR = Math.Min(minR, pb.RowLo); maxR = Math.Max(maxR, pb.RowHi); }
            if (sumV <= 0 || minR > maxR) return r;

            int n = maxR - minR + 1;
            var vols = new double[n];
            if (hasP)
                foreach (KeyValuePair<int, double> kv in p.Profile)
                    vols[kv.Key - minR] += kv.Value;
            if (hasB && pb.V > 0)
            {
                double per = pb.V / (pb.RowHi - pb.RowLo + 1);
                for (int row = pb.RowLo; row <= pb.RowHi; row++) vols[row - minR] += per;
            }

            double center = (n - 1) / 2.0, best = -1, total = 0;
            int poc = 0;
            for (int i = 0; i < n; i++)
            {
                total += vols[i];
                if (vols[i] > best || (vols[i] == best && Math.Abs(i - center) < Math.Abs(poc - center)))
                {
                    best = vols[i];
                    poc = i;
                }
            }
            if (total <= 0) return r;

            double target = total * Math.Max(1, Math.Min(100, ValueAreaPercent)) / 100.0;
            int lo = poc, hi = poc;
            double acc = vols[poc];
            while (acc < target && (lo > 0 || hi < n - 1))
            {
                double up = hi < n - 1 ? vols[hi + 1] : -1;
                double dn = lo > 0 ? vols[lo - 1] : -1;
                if (up >= dn) { hi++; acc += up; }
                else { lo--; acc += dn; }
            }

            r[(int)ILLevel.POC] = RowMid(minR + poc);
            r[(int)ILLevel.VAH] = RowTop(minR + hi);
            r[(int)ILLevel.VAL] = RowBottom(minR + lo);
            return r;
        }

        private BarData PendingFor(Period p)
        {
            BarData pb = pending;
            if (pb == null || p == null) return null;
            return (pb.Day == p || pb.Week == p || pb.Month == p || pb.London == p) ? pb : null;
        }

        private List<Period> ListFor(ILPeriod kind)
        {
            switch (kind)
            {
                case ILPeriod.Daily: return days;
                case ILPeriod.Weekly: return weeks;
                case ILPeriod.Monthly: return months;
                default: return londons;
            }
        }

        private double[] LevelsOf(Period p)
        {
            if (p.Finalized) return p.Levels ?? (p.Levels = ComputeLevels(p, null));
            return ComputeLevels(p, PendingFor(p));
        }

        private void RebuildSnapshot()
        {
            var s = new Snapshot();
            AddItems(s, ILPeriod.Daily, Days);
            AddItems(s, ILPeriod.Weekly, Weeks);
            AddItems(s, ILPeriod.Monthly, Months);
            if (ShowLondon) AddLondonItems(s);
            snapshot = s;
            snapshotDirty = false;
            lastBuildUtc = DateTime.UtcNow;
        }

        private void AddItems(Snapshot s, ILPeriod kind, int count)
        {
            List<Period> list = ListFor(kind);
            if (list == null) return;
            bool[] en = enabled[(int)kind];

            for (int back = 0; back <= count; back++)
            {
                if (back == 0 && SkipLast) continue;
                int i = list.Count - 1 - back;
                if (i < 0) break;

                Period p = list[i];
                BarData pb = p.Finalized ? null : PendingFor(p);
                if (!p.HasData && pb == null) continue;

                double[] lv = LevelsOf(p);
                DateTime origin;
                if (back == 0) origin = p.HasData ? p.FirstTime : pb.Start;
                else origin = pb != null ? pb.End : p.LastTime;

                string prefix = PeriodPrefix[(int)kind] + (back == 0 ? "0" : "-" + back) + " ";
                string suffix = p.Partial ? "*" : string.Empty;

                for (int k = 0; k < LevelCount; k++)
                {
                    if (!en[k] || double.IsNaN(lv[k])) continue;
                    if (back == 0 && k == (int)ILLevel.Close) continue;   // current "close" is just last price
                    s.Items.Add(new LevelItem
                    {
                        Price = lv[k],
                        ColorIdx = k,
                        Period = kind,
                        Developing = back == 0,
                        Name = prefix + LevelNames[k] + suffix,
                        Origin = origin
                    });
                }

                if (en[(int)ILLevel.VAH] && !double.IsNaN(lv[(int)ILLevel.VAH]) && !double.IsNaN(lv[(int)ILLevel.VAL]))
                    s.Zones.Add(new VaZone { Vah = lv[(int)ILLevel.VAH], Val = lv[(int)ILLevel.VAL], Origin = origin });
            }
        }

        private void AddLondonItems(Snapshot s)
        {
            for (int back = 0; back <= LondonDays; back++)
            {
                int i = londons.Count - 1 - back;
                if (i < 0) break;
                Period p = londons[i];
                if (!p.HasData && PendingFor(p) == null) continue;

                bool complete = p.Finalized || lastBarEnd >= p.NyOpenLocal;   // range is fixed once NY opens
                if (!complete && !ShowDevelopingLondon) continue;

                double[] lv = LevelsOf(p);
                DateTime origin = complete ? p.NyOpenLocal : (p.HasData ? p.FirstTime : pending.Start);
                string tag = "LDN" + (back == 0 ? string.Empty : "-" + back) + (complete ? " " : "~ ");
                string suffix = p.Partial ? "*" : string.Empty;

                if (!double.IsNaN(lv[(int)ILLevel.High]))
                    s.Items.Add(new LevelItem { Price = lv[(int)ILLevel.High], ColorIdx = ColorLondonHigh, Period = ILPeriod.London, Developing = !complete, Name = tag + "High" + suffix, Origin = origin });
                if (!double.IsNaN(lv[(int)ILLevel.Low]))
                    s.Items.Add(new LevelItem { Price = lv[(int)ILLevel.Low], ColorIdx = ColorLondonLow, Period = ILPeriod.London, Developing = !complete, Name = tag + "Low" + suffix, Origin = origin });
            }
        }

        private void CheckAlerts()
        {
            Snapshot snap = snapshot;
            double price = Close[0];
            if (snap != null && !double.IsNaN(lastAlertPrice))
            {
                foreach (LevelItem it in snap.Items)
                {
                    if (it.Developing) continue;   // only fixed levels
                    bool crossUp = lastAlertPrice < it.Price && price >= it.Price;
                    bool crossDn = lastAlertPrice > it.Price && price <= it.Price;
                    if (!crossUp && !crossDn) continue;

                    Alert("IL_" + it.Name, Priority.Medium,
                        Instrument.FullName + " " + (crossUp ? "crossed above " : "crossed below ") + it.Name + " @ " + Instrument.MasterInstrument.FormatPrice(it.Price),
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\" + AlertSoundFile,
                        Math.Max(1, AlertRearmSeconds), Brushes.Black, BrushForColor(it.ColorIdx));
                }
            }
            lastAlertPrice = price;
        }

        /// <summary>
        /// Strategy access. periodsBack: 0 = current period, 1 = previous, ...
        /// ILPeriod.London supports ILLevel.High / ILLevel.Low. Returns NaN when not available.
        /// </summary>
        public double GetLevel(ILPeriod period, int periodsBack, ILLevel level)
        {
            Update();
            List<Period> list = ListFor(period);
            if (list == null || periodsBack < 0) return double.NaN;
            int i = list.Count - 1 - periodsBack;
            if (i < 0) return double.NaN;
            return LevelsOf(list[i])[(int)level];
        }
        #endregion

        #region Rendering
        private Brush BrushForColor(int idx)
        {
            switch (idx)
            {
                case (int)ILLevel.Low: return LowBrush;
                case (int)ILLevel.High: return HighBrush;
                case (int)ILLevel.Open: return OpenBrush;
                case (int)ILLevel.Close: return CloseBrush;
                case (int)ILLevel.POC: return PocBrush;
                case (int)ILLevel.VAH:
                case (int)ILLevel.VAL: return ValueAreaBrush;
                case (int)ILLevel.VWAP: return VwapBrush;
                case ColorLondonHigh: return LondonHighBrush;
                case ColorLondonLow: return LondonLowBrush;
                default: return MidBrush;
            }
        }

        private static SharpDX.Direct2D1.DashStyle ToDxDash(DashStyleHelper d)
        {
            switch (d)
            {
                case DashStyleHelper.Dash: return SharpDX.Direct2D1.DashStyle.Dash;
                case DashStyleHelper.DashDot: return SharpDX.Direct2D1.DashStyle.DashDot;
                case DashStyleHelper.DashDotDot: return SharpDX.Direct2D1.DashStyle.DashDotDot;
                case DashStyleHelper.Dot: return SharpDX.Direct2D1.DashStyle.Dot;
                default: return SharpDX.Direct2D1.DashStyle.Solid;
            }
        }

        private void CreateDx()
        {
            dxLineBrushes = new SharpDX.Direct2D1.Brush[ColorCount];
            dxTextBrushes = new SharpDX.Direct2D1.Brush[ColorCount];
            for (int k = 0; k < ColorCount; k++)
            {
                Brush wpf = BrushForColor(k);
                dxLineBrushes[k] = wpf.ToDxBrush(RenderTarget);
                dxLineBrushes[k].Opacity = LineOpacity / 100f;
                dxTextBrushes[k] = wpf.ToDxBrush(RenderTarget);
            }
            dxVaFill = ValueAreaBrush.ToDxBrush(RenderTarget);
            dxVaFill.Opacity = ValueAreaFillOpacity / 100f;
            dxLabelBg = LabelBackgroundBrush.ToDxBrush(RenderTarget);
            dxLabelBg.Opacity = LabelBackgroundOpacity / 100f;

            DashStyleHelper[] dashes = { DailyDash, WeeklyDash, MonthlyDash, LondonDash };
            dxStrokes = new SharpDX.Direct2D1.StrokeStyle[4];
            for (int i = 0; i < 4; i++)
                dxStrokes[i] = new SharpDX.Direct2D1.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory,
                    new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = ToDxDash(dashes[i]), DashCap = SharpDX.Direct2D1.CapStyle.Flat });

            dxReady = true;
        }

        private void DisposeDx()
        {
            if (dxLineBrushes != null) foreach (var b in dxLineBrushes) if (b != null) b.Dispose();
            if (dxTextBrushes != null) foreach (var b in dxTextBrushes) if (b != null) b.Dispose();
            if (dxStrokes != null) foreach (var s in dxStrokes) if (s != null) s.Dispose();
            if (dxVaFill != null) dxVaFill.Dispose();
            if (dxLabelBg != null) dxLabelBg.Dispose();
            dxLineBrushes = dxTextBrushes = null;
            dxStrokes = null;
            dxVaFill = dxLabelBg = null;
            dxReady = false;
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDx();   // recreated lazily against the new render target
        }

        // Maps a time to an x position. On an RTH chart, times in the overnight gap land on the next RTH bar.
        private float XForTime(ChartControl cc, DateTime t, float left, float right)
        {
            int idx = ChartBars.GetBarIdxByTime(cc, t);
            float x = cc.GetXByBarIndex(ChartBars, idx);
            return Math.Max(left, Math.Min(right, x));
        }

        private int WidthFor(ILPeriod p)
        {
            switch (p)
            {
                case ILPeriod.Daily: return DailyWidth;
                case ILPeriod.Weekly: return WeeklyWidth;
                case ILPeriod.Monthly: return MonthlyWidth;
                default: return LondonWidth;
            }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            Snapshot snap = snapshot;
            if (snap == null || IsInHitTest || RenderTarget == null || ChartBars == null || ChartBars.Count == 0) return;
            if (!dxReady) CreateDx();

            SharpDX.Direct2D1.AntialiasMode oldAA = RenderTarget.AntialiasMode;
            RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;
            try
            {
                float left = ChartPanel.X, right = ChartPanel.X + ChartPanel.W;
                float top = ChartPanel.Y, bottom = ChartPanel.Y + ChartPanel.H;
                int lastIdx = ChartBars.Count - 1;
                float halfBar = (float)chartControl.GetBarPaintWidth(ChartBars) / 2f;
                float liveX = chartControl.GetXByBarIndex(ChartBars, lastIdx) + halfBar + 4f;
                float lastVisX = chartControl.GetXByBarIndex(ChartBars, Math.Min(ChartBars.ToIndex, lastIdx)) + halfBar + 4f;
                bool drawLines = PlotType != ILPlotType.Label;
                bool drawLabels = PlotType != ILPlotType.Line;

                if (ValueAreaFillOpacity > 0)
                {
                    foreach (VaZone z in snap.Zones)
                    {
                        float y1 = chartScale.GetYByValue(z.Vah), y2 = chartScale.GetYByValue(z.Val);
                        float yt = Math.Max(top, Math.Min(y1, y2)), yb = Math.Min(bottom, Math.Max(y1, y2));
                        if (yb <= yt) continue;
                        float x1 = LineStartX(chartControl, z.Origin, left, right, liveX);
                        if (x1 >= right) continue;
                        RenderTarget.FillRectangle(new SharpDX.RectangleF(x1, yt, right - x1, yb - yt), dxVaFill);
                    }
                }

                var labels = new Dictionary<string, LabelReq>();

                foreach (LevelItem it in snap.Items)
                {
                    float y = chartScale.GetYByValue(it.Price);
                    if (y < top || y > bottom) continue;
                    float originX = XForTime(chartControl, it.Origin, left, right);

                    if (drawLines)
                    {
                        float x1 = LineStartX(chartControl, it.Origin, left, right, liveX);
                        if (x1 < right)
                        {
                            int w = Math.Max(1, WidthFor(it.Period));
                            float ys = (float)Math.Round(y) + (w % 2 == 1 ? 0.5f : 0f);
                            RenderTarget.DrawLine(new SharpDX.Vector2(x1, ys), new SharpDX.Vector2(right, ys),
                                dxLineBrushes[it.ColorIdx], w, dxStrokes[(int)it.Period]);
                        }
                    }

                    if (drawLabels)
                    {
                        long tick = (long)Math.Round(it.Price / TickSize);
                        string key = tick.ToString() + (TextAlign == ILTextAlign.LineStart ? "_" + (int)originX : string.Empty);
                        LabelReq lr;
                        if (!labels.TryGetValue(key, out lr))
                        {
                            lr = new LabelReq { Y = y, OriginX = originX, Price = it.Price, ColorIdx = it.ColorIdx };
                            labels.Add(key, lr);
                        }
                        lr.Names.Add(it.Name);
                    }
                }

                if (drawLabels && labels.Count > 0)
                    DrawLabels(labels.Values.OrderBy(l => l.Y).ToList(), left, right, liveX, lastVisX);
            }
            finally
            {
                RenderTarget.AntialiasMode = oldAA;
            }
        }

        private float LineStartX(ChartControl cc, DateTime origin, float left, float right, float liveX)
        {
            switch (LineExtent)
            {
                case ILLineExtent.FullWidth: return left;
                case ILLineExtent.CurrentToRight: return Math.Max(left, liveX);
                default: return XForTime(cc, origin, left, right);
            }
        }

        private void DrawLabels(List<LabelReq> reqs, float left, float right, float liveX, float lastVisX)
        {
            using (SharpDX.DirectWrite.TextFormat tf = LabelFont.ToDirectWriteTextFormat())
            {
                tf.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;
                var placed = new List<SharpDX.RectangleF>();

                foreach (LabelReq lr in reqs)
                {
                    string text = string.Join(" / ", lr.Names);
                    if (ShowPriceInLabel) text += "  " + Instrument.MasterInstrument.FormatPrice(lr.Price);

                    using (var tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, text, tf, 4000f, 400f))
                    {
                        float w = tl.Metrics.Width + 8f, h = tl.Metrics.Height + 2f;
                        float maxX = right - w - 4f;
                        float x;
                        switch (TextAlign)
                        {
                            case ILTextAlign.Left: x = left + 4f; break;
                            case ILTextAlign.CurrentToRight: x = Math.Min(liveX, maxX); break;
                            case ILTextAlign.LastVisibleBar: x = Math.Min(lastVisX, maxX); break;
                            case ILTextAlign.LineStart: x = Math.Min(lr.OriginX + 4f, maxX); break;
                            default: x = maxX; break;
                        }
                        x = Math.Max(left + 2f, x);
                        float y = lr.Y - h / 2f;

                        bool moved = true;
                        for (int guard = 0; moved && guard <= placed.Count; guard++)
                        {
                            moved = false;
                            foreach (SharpDX.RectangleF r in placed)
                            {
                                if (x < r.Right && x + w > r.Left && y < r.Bottom && y + h > r.Top)
                                {
                                    y = r.Bottom + 1f;
                                    moved = true;
                                }
                            }
                        }

                        var rect = new SharpDX.RectangleF(x, y, w, h);
                        placed.Add(rect);
                        if (LabelBackgroundOpacity > 0) RenderTarget.FillRectangle(rect, dxLabelBg);
                        RenderTarget.DrawTextLayout(new SharpDX.Vector2(x + 4f, y + 1f), tl, dxTextBrushes[lr.ColorIdx],
                            SharpDX.Direct2D1.DrawTextOptions.NoSnap);
                    }
                }
            }
        }
        #endregion

        #region Properties — Length
        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Days", Description = "Number of previous (completed) days to display. The current day is shown in addition unless Skip Last is on.", Order = 1, GroupName = "1. Length")]
        public int Days { get; set; }

        [NinjaScriptProperty]
        [Range(0, 52)]
        [Display(Name = "Weeks", Description = "Number of previous (completed) weeks to display.", Order = 2, GroupName = "1. Length")]
        public int Weeks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 36)]
        [Display(Name = "Months", Description = "Number of previous (completed) months to display.", Order = 3, GroupName = "1. Length")]
        public int Months { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Skip Last", Description = "Hide the current (developing) day, week and month.", Order = 4, GroupName = "1. Length")]
        public bool SkipLast { get; set; }
        #endregion

        #region Properties — Plot
        [Display(Name = "Font", Order = 1, GroupName = "2. Plot")]
        public SimpleFont LabelFont { get; set; }

        [Display(Name = "Plot Type", Order = 2, GroupName = "2. Plot")]
        public ILPlotType PlotType { get; set; }

        [Display(Name = "Text Align", Description = "Left / Right panel edge, right of the live bar, right of the last visible bar, or at the start of each line.", Order = 3, GroupName = "2. Plot")]
        public ILTextAlign TextAlign { get; set; }

        [Display(Name = "Line Extent", Description = "FullWidth: whole panel. FromPeriod: from the period's start (current) or end (completed) to the right edge; London lines start at the NY open. CurrentToRight: from the live bar.", Order = 4, GroupName = "2. Plot")]
        public ILLineExtent LineExtent { get; set; }

        [Display(Name = "Show Price In Label", Order = 5, GroupName = "2. Plot")]
        public bool ShowPriceInLabel { get; set; }

        [Range(5, 100)]
        [Display(Name = "Line Opacity %", Order = 6, GroupName = "2. Plot")]
        public int LineOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Label Background", Order = 7, GroupName = "2. Plot")]
        public Brush LabelBackgroundBrush { get; set; }
        [Browsable(false)]
        public string LabelBackgroundBrushSerializable { get { return Serialize.BrushToString(LabelBackgroundBrush); } set { LabelBackgroundBrush = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Label Background Opacity %", Order = 8, GroupName = "2. Plot")]
        public int LabelBackgroundOpacity { get; set; }

        [Range(0, 100)]
        [Display(Name = "Value Area Fill Opacity %", Description = "0 = no fill, lines only.", Order = 9, GroupName = "2. Plot")]
        public int ValueAreaFillOpacity { get; set; }

        [Range(1, 10)]
        [Display(Name = "Daily Line Width", Order = 10, GroupName = "2. Plot")]
        public int DailyWidth { get; set; }

        [Display(Name = "Daily Line Style", Order = 11, GroupName = "2. Plot")]
        public DashStyleHelper DailyDash { get; set; }

        [Range(1, 10)]
        [Display(Name = "Weekly Line Width", Order = 12, GroupName = "2. Plot")]
        public int WeeklyWidth { get; set; }

        [Display(Name = "Weekly Line Style", Order = 13, GroupName = "2. Plot")]
        public DashStyleHelper WeeklyDash { get; set; }

        [Range(1, 10)]
        [Display(Name = "Monthly Line Width", Order = 14, GroupName = "2. Plot")]
        public int MonthlyWidth { get; set; }

        [Display(Name = "Monthly Line Style", Order = 15, GroupName = "2. Plot")]
        public DashStyleHelper MonthlyDash { get; set; }
        #endregion

        #region Properties — Daily levels
        [Display(Name = "Average Value (Mid)", Order = 1, GroupName = "3. Daily Levels")] public bool DMid { get; set; }
        [Display(Name = "Low", Order = 2, GroupName = "3. Daily Levels")] public bool DLow { get; set; }
        [Display(Name = "High", Order = 3, GroupName = "3. Daily Levels")] public bool DHigh { get; set; }
        [Display(Name = "Open", Order = 4, GroupName = "3. Daily Levels")] public bool DOpen { get; set; }
        [Display(Name = "Close", Order = 5, GroupName = "3. Daily Levels")] public bool DClose { get; set; }
        [Display(Name = "POC", Order = 6, GroupName = "3. Daily Levels")] public bool DPoc { get; set; }
        [Display(Name = "Value Area", Order = 7, GroupName = "3. Daily Levels")] public bool DValueArea { get; set; }
        [Display(Name = "VWAP", Order = 8, GroupName = "3. Daily Levels")] public bool DVwap { get; set; }
        #endregion

        #region Properties — Weekly levels
        [Display(Name = "Average Value (Mid)", Order = 1, GroupName = "4. Weekly Levels")] public bool WMid { get; set; }
        [Display(Name = "Low", Order = 2, GroupName = "4. Weekly Levels")] public bool WLow { get; set; }
        [Display(Name = "High", Order = 3, GroupName = "4. Weekly Levels")] public bool WHigh { get; set; }
        [Display(Name = "Open", Order = 4, GroupName = "4. Weekly Levels")] public bool WOpen { get; set; }
        [Display(Name = "Close", Order = 5, GroupName = "4. Weekly Levels")] public bool WClose { get; set; }
        [Display(Name = "POC", Order = 6, GroupName = "4. Weekly Levels")] public bool WPoc { get; set; }
        [Display(Name = "Value Area", Order = 7, GroupName = "4. Weekly Levels")] public bool WValueArea { get; set; }
        [Display(Name = "VWAP", Order = 8, GroupName = "4. Weekly Levels")] public bool WVwap { get; set; }
        #endregion

        #region Properties — Monthly levels
        [Display(Name = "Average Value (Mid)", Order = 1, GroupName = "5. Monthly Levels")] public bool MMid { get; set; }
        [Display(Name = "Low", Order = 2, GroupName = "5. Monthly Levels")] public bool MLow { get; set; }
        [Display(Name = "High", Order = 3, GroupName = "5. Monthly Levels")] public bool MHigh { get; set; }
        [Display(Name = "Open", Order = 4, GroupName = "5. Monthly Levels")] public bool MOpen { get; set; }
        [Display(Name = "Close", Order = 5, GroupName = "5. Monthly Levels")] public bool MClose { get; set; }
        [Display(Name = "POC", Order = 6, GroupName = "5. Monthly Levels")] public bool MPoc { get; set; }
        [Display(Name = "Value Area", Order = 7, GroupName = "5. Monthly Levels")] public bool MValueArea { get; set; }
        [Display(Name = "VWAP", Order = 8, GroupName = "5. Monthly Levels")] public bool MVwap { get; set; }
        #endregion

        #region Properties — Colors
        [XmlIgnore][Display(Name = "Low", Order = 1, GroupName = "6. Colors")] public Brush LowBrush { get; set; }
        [Browsable(false)] public string LowBrushSerializable { get { return Serialize.BrushToString(LowBrush); } set { LowBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "High", Order = 2, GroupName = "6. Colors")] public Brush HighBrush { get; set; }
        [Browsable(false)] public string HighBrushSerializable { get { return Serialize.BrushToString(HighBrush); } set { HighBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Open", Order = 3, GroupName = "6. Colors")] public Brush OpenBrush { get; set; }
        [Browsable(false)] public string OpenBrushSerializable { get { return Serialize.BrushToString(OpenBrush); } set { OpenBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Close", Order = 4, GroupName = "6. Colors")] public Brush CloseBrush { get; set; }
        [Browsable(false)] public string CloseBrushSerializable { get { return Serialize.BrushToString(CloseBrush); } set { CloseBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Mid Point", Order = 5, GroupName = "6. Colors")] public Brush MidBrush { get; set; }
        [Browsable(false)] public string MidBrushSerializable { get { return Serialize.BrushToString(MidBrush); } set { MidBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "POC", Order = 6, GroupName = "6. Colors")] public Brush PocBrush { get; set; }
        [Browsable(false)] public string PocBrushSerializable { get { return Serialize.BrushToString(PocBrush); } set { PocBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Value Area", Order = 7, GroupName = "6. Colors")] public Brush ValueAreaBrush { get; set; }
        [Browsable(false)] public string ValueAreaBrushSerializable { get { return Serialize.BrushToString(ValueAreaBrush); } set { ValueAreaBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "VWAP", Order = 8, GroupName = "6. Colors")] public Brush VwapBrush { get; set; }
        [Browsable(false)] public string VwapBrushSerializable { get { return Serialize.BrushToString(VwapBrush); } set { VwapBrush = Serialize.StringToBrush(value); } }
        #endregion

        #region Properties — Session / calculation
        [NinjaScriptProperty]
        [Display(Name = "Filter Time", Description = "Session used for daily/weekly/monthly levels, regardless of the chart's trading hours. ETH = full instrument session. RTH / Custom = time windows below.", Order = 1, GroupName = "7. Session")]
        public ILFilterTime FilterTime { get; set; }

        [Display(Name = "Session Time Zone", Description = "Time zone of the RTH and Custom windows.", Order = 2, GroupName = "7. Session")]
        public ILTimeZone SessionTimeZone { get; set; }

        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "RTH Start", Order = 3, GroupName = "7. Session")]
        public DateTime RthStart { get; set; }

        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "RTH End", Order = 4, GroupName = "7. Session")]
        public DateTime RthEnd { get; set; }

        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Custom Ini Session", Order = 5, GroupName = "7. Session")]
        public DateTime CustomStart { get; set; }

        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Custom End Session", Order = 6, GroupName = "7. Session")]
        public DateTime CustomEnd { get; set; }

        [Display(Name = "Full Session Template", Description = "Blank = the instrument's default trading hours (ETH for CME products). Never taken from the chart.", Order = 7, GroupName = "7. Session")]
        public string EthTemplateName { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Value Area %", Order = 8, GroupName = "7. Session")]
        public int ValueAreaPercent { get; set; }

        [Range(1, 1000)]
        [Display(Name = "Profile Row Size (ticks)", Order = 9, GroupName = "7. Session")]
        public int ProfileRowTicks { get; set; }

        [Range(1, 60)]
        [Display(Name = "Calculation Bar (minutes)", Description = "Resolution of the internal series. 1 = most accurate.", Order = 10, GroupName = "7. Session")]
        public int CalcBarMinutes { get; set; }
        #endregion

        #region Properties — London range
        [Display(Name = "Show London High/Low", Order = 1, GroupName = "8. London Range")]
        public bool ShowLondon { get; set; }

        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "London Open (London time)", Order = 2, GroupName = "8. London Range")]
        public DateTime LondonOpen { get; set; }

        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "NY Open (New York time)", Description = "London range is measured from London open up to this time and fixed from here on.", Order = 3, GroupName = "8. London Range")]
        public DateTime NyOpen { get; set; }

        [Range(0, 20)]
        [Display(Name = "Previous Days", Description = "Number of prior days' London ranges to keep on the chart (0 = today only).", Order = 4, GroupName = "8. London Range")]
        public int LondonDays { get; set; }

        [Display(Name = "Show Developing Range", Description = "Also show the London High/Low before NY open (labelled LDN~).", Order = 5, GroupName = "8. London Range")]
        public bool ShowDevelopingLondon { get; set; }

        [XmlIgnore][Display(Name = "London High Color", Order = 6, GroupName = "8. London Range")] public Brush LondonHighBrush { get; set; }
        [Browsable(false)] public string LondonHighBrushSerializable { get { return Serialize.BrushToString(LondonHighBrush); } set { LondonHighBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "London Low Color", Order = 7, GroupName = "8. London Range")] public Brush LondonLowBrush { get; set; }
        [Browsable(false)] public string LondonLowBrushSerializable { get { return Serialize.BrushToString(LondonLowBrush); } set { LondonLowBrush = Serialize.StringToBrush(value); } }

        [Range(1, 10)]
        [Display(Name = "London Line Width", Order = 8, GroupName = "8. London Range")]
        public int LondonWidth { get; set; }

        [Display(Name = "London Line Style", Order = 9, GroupName = "8. London Range")]
        public DashStyleHelper LondonDash { get; set; }
        #endregion

        #region Properties — Alerts
        [Display(Name = "Alert On Cross", Description = "Alert when price crosses a fixed level: completed periods and London High/Low after NY open (realtime only).", Order = 1, GroupName = "9. Alerts")]
        public bool AlertOnCross { get; set; }

        [Display(Name = "Sound File", Order = 2, GroupName = "9. Alerts")]
        public string AlertSoundFile { get; set; }

        [Range(1, 86400)]
        [Display(Name = "Rearm Seconds", Order = 3, GroupName = "9. Alerts")]
        public int AlertRearmSeconds { get; set; }
        #endregion
    }
}
