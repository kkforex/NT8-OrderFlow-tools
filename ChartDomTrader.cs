#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using DX  = SharpDX;
using D2D = SharpDX.Direct2D1;
using DW  = SharpDX.DirectWrite;
#endregion

// =====================================================================================
//  ChartDomTrader  —  DeepCharts-style Chart DOM for NinjaTrader 8
//
//  Two columns pinned to the right edge of the price panel:
//      [ BID / BUY column (green) ][ ASK / SELL column (red) ]
//
//  Order entry (DeepCharts defaults)
//      BUY column   L-click below last -> Buy Limit     L-click above last -> Buy Stop
//      SELL column  L-click above last -> Sell Limit    L-click below last -> Sell Stop
//      Shift + L-click on the stop side -> Stop-Limit (limit = stop price)
//      R-click anywhere in a column     -> Market order for that side
//
//  Order management
//      L-click + drag a working order   -> move it
//      R-click a working order          -> cancel it
//
//  Control panel (separate from the DOM; drag the header/body anywhere on the chart,
//  double-click the header to snap back to the docked corner; position saved with workspace)
//      D: daily P/L for this instrument (realized FIFO today + open), O: open P/L, Pos | Qty
//      BUY BID | SELL ASK (passive limits at best bid / best ask), REV, CANCEL ALL | FLAT ALL
//
//  Account / Qty follow the Chart Trader selectors when present (falls back to properties).
//  Optional ATM template is attached to every column/market entry (not to REV/FLAT).
// =====================================================================================

namespace NinjaTrader.NinjaScript.Indicators
{
    public class ChartDomTrader : Indicator
    {
        #region Private types / constants
        private const int HitNone = 0, HitBid = 1, HitAsk = 2, HitBuyMkt = 3, HitSellMkt = 4,
                          HitRev = 5, HitCancel = 6, HitFlat = 7, HitLabel = 8;

        private class DepthRow { public double Price; public long Volume; }
        private class Lot      { public int Qty; public double Price; }
        #endregion

        #region Fields
        // account / chart trader
        private Account         account;
        private AccountSelector accountSelector;
        private QuantityUpDown  quantitySelector;
        private DateTime        lastSelectorSearch = DateTime.MinValue;

        // market data
        private readonly object  depthLock = new object();
        private List<DepthRow>   askRows   = new List<DepthRow>();
        private List<DepthRow>   bidRows   = new List<DepthRow>();
        private double           bestBid, bestAsk, lastPrice;
        private DateTime         lastDepthRefresh = DateTime.MinValue;

        // P/L
        private readonly object  pnlLock     = new object();
        private readonly object  sessionLock = new object();
        private SessionIterator  sessionIterator;
        private double           realizedToday;

        // layout (device pixels, rebuilt every render)
        private ChartScale       lastScale;
        private bool             layoutValid;
        private DX.RectangleF    bidColRect, askColRect, panelRect, headerRect, pnlRect, openRect, infoRect;
        private float            lastChartRight, lastChartTop;

        // movable panel
        private bool             panelDragging;
        private float            grabDX, grabDY;
        private bool             cursorSet;
        private DX.RectangleF    btnBuyMkt, btnSellMkt, btnRev, btnCancel, btnFlat;
        private float            labelsBottom, buttonsTop;

        // mouse
        private bool             uiAttached;
        private bool             mouseInside;
        private float            mouseX, mouseY;
        private Order            dragOrder;
        private float            dragY;
        private bool             dragMoved;
        private bool             rightConsumed;
        private int              originalBarMarginRight = -1;

        // safety
        private readonly Queue<DateTime> orderTimes = new Queue<DateTime>();

        // SharpDX resources
        private readonly List<IDisposable> dxResources = new List<IDisposable>();
        private D2D.SolidColorBrush dxBidCol, dxAskCol, dxDepthBid, dxDepthAsk, dxBuyOrder, dxSellOrder,
                                    dxButton, dxText, dxProfit, dxLoss, dxHover, dxLabelBg, dxBorder, dxDarkText;
        private DW.TextFormat       tfCenter, tfRight, tfButton;
        #endregion

        #region State
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                     = "ChartDomTrader";
                Description              = "DeepCharts-style Chart DOM: bid/ask columns tradable from the chart, P/L label and Chart Trader buttons.";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;
                ScaleJustification       = ScaleJustification.Right;

                ColumnWidth            = 50;
                RightOffset            = 0;
                ReserveRightMargin     = true;
                ColumnOpacity          = 85;

                UseChartTraderAccount  = true;
                AccountName            = "Sim101";
                DefaultQuantity        = 1;
                AtmTemplate            = string.Empty;
                MaxOrdersPerSecond     = 5;

                ShowDepth              = true;
                ShowBidAskLines        = true;
                ShowPnl                = true;
                ShowOpenPnl            = true;
                ShowButtons            = true;
                CurrencySymbol         = "$";

                PanelAtBottom          = false;
                PanelWidth             = 160;
                PanelMargin            = 6;
                PanelPosX              = -1;   // -1 = docked to the corner
                PanelPosY              = -1;
                ReservePanelMargin     = false;

                TextFont               = new SimpleFont("Arial", 11);

                BidColumnBrush  = new SolidColorBrush(Color.FromRgb(0x1C, 0x3F, 0x2A));
                AskColumnBrush  = new SolidColorBrush(Color.FromRgb(0x3F, 0x16, 0x16));
                DepthBidBrush   = Brushes.LimeGreen;
                DepthAskBrush   = Brushes.Red;
                BuyOrderBrush   = Brushes.DodgerBlue;
                SellOrderBrush  = Brushes.DeepPink;
                ButtonBrush     = Brushes.DarkOrange;
                TextBrush       = Brushes.WhiteSmoke;
                ProfitBrush     = Brushes.LimeGreen;
                LossBrush       = Brushes.Red;
            }
            else if (State == State.DataLoaded)
            {
                sessionIterator = new SessionIterator(Bars);
            }
            else if (State == State.Historical)
            {
                if (ChartControl != null && !uiAttached)
                    ChartControl.Dispatcher.InvokeAsync(AttachUi);
            }
            else if (State == State.Terminated)
            {
                if (ChartControl != null && uiAttached)
                    ChartControl.Dispatcher.InvokeAsync(DetachUi);
                SwapAccount(null);
                DisposeDx();
            }
        }

        protected override void OnBarUpdate() { }
        #endregion

        #region UI attach / detach
        private void AttachUi()
        {
            if (ChartControl == null || uiAttached) return;
            ChartControl.PreviewMouseLeftButtonDown  += OnMouseLeftDown;
            ChartControl.PreviewMouseLeftButtonUp    += OnMouseLeftUp;
            ChartControl.PreviewMouseRightButtonDown += OnMouseRightDown;
            ChartControl.PreviewMouseRightButtonUp   += OnMouseRightUp;
            ChartControl.PreviewMouseMove            += OnMouseMove;
            ChartControl.MouseLeave                  += OnMouseLeave;
            uiAttached = true;

            if (ReserveRightMargin)
            {
                originalBarMarginRight = ChartControl.Properties.BarMarginRight;
                int needed = 2 * ColumnWidth + RightOffset + 40   // + room for position tag
                           + (ReservePanelMargin ? PanelWidth + PanelMargin : 0);
                if (ChartControl.Properties.BarMarginRight < needed)
                    ChartControl.Properties.BarMarginRight = needed;
            }
        }

        private void DetachUi()
        {
            if (ChartControl == null) return;
            ChartControl.PreviewMouseLeftButtonDown  -= OnMouseLeftDown;
            ChartControl.PreviewMouseLeftButtonUp    -= OnMouseLeftUp;
            ChartControl.PreviewMouseRightButtonDown -= OnMouseRightDown;
            ChartControl.PreviewMouseRightButtonUp   -= OnMouseRightUp;
            ChartControl.PreviewMouseMove            -= OnMouseMove;
            ChartControl.MouseLeave                  -= OnMouseLeave;
            if (ChartControl.IsMouseCaptured) ChartControl.ReleaseMouseCapture();
            if (cursorSet) { ChartControl.Cursor = null; cursorSet = false; }
            if (originalBarMarginRight >= 0) ChartControl.Properties.BarMarginRight = originalBarMarginRight;
            uiAttached = false;
        }
        #endregion

        #region Account resolution (UI thread)
        private void FindChartTraderSelectors()
        {
            if ((accountSelector != null && quantitySelector != null) || ChartControl == null) return;
            if ((DateTime.Now - lastSelectorSearch).TotalSeconds < 2) return;
            lastSelectorSearch = DateTime.Now;

            Window win = Window.GetWindow(ChartControl.Parent);
            if (win == null) return;
            if (accountSelector == null)
                accountSelector  = win.FindFirst("ChartTraderControlAccountSelector")  as AccountSelector;
            if (quantitySelector == null)
                quantitySelector = win.FindFirst("ChartTraderControlQuantitySelector") as QuantityUpDown;
        }

        private Account ResolveAccount()
        {
            Account acct = null;
            if (UseChartTraderAccount)
            {
                FindChartTraderSelectors();
                if (accountSelector != null) acct = accountSelector.SelectedAccount;
            }
            if (acct == null)
                lock (Account.All)
                    acct = Account.All.FirstOrDefault(a => a.Name == AccountName);

            if (acct != account) SwapAccount(acct);
            return acct;
        }

        private int ResolveQuantity()
        {
            int q = DefaultQuantity;
            if (UseChartTraderAccount)
            {
                FindChartTraderSelectors();
                if (quantitySelector != null && quantitySelector.Value > 0) q = quantitySelector.Value;
            }
            return Math.Max(1, q);
        }

        private void SwapAccount(Account newAccount)
        {
            if (account != null)
            {
                account.OrderUpdate     -= OnAccountOrderUpdate;
                account.ExecutionUpdate -= OnAccountExecutionUpdate;
                account.PositionUpdate  -= OnAccountPositionUpdate;
            }
            account = newAccount;
            if (account != null)
            {
                account.OrderUpdate     += OnAccountOrderUpdate;
                account.ExecutionUpdate += OnAccountExecutionUpdate;
                account.PositionUpdate  += OnAccountPositionUpdate;
            }
            RecalcRealized();
        }

        private bool IsMine(Instrument inst)
        {
            return inst != null && Instrument != null && inst.FullName == Instrument.FullName;
        }

        private void OnAccountOrderUpdate(object sender, OrderEventArgs e)
        {
            if (State != State.Terminated && e.Order != null && IsMine(e.Order.Instrument)) ForceRefresh();
        }

        private void OnAccountExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            if (State == State.Terminated || e.Execution == null || !IsMine(e.Execution.Instrument)) return;
            RecalcRealized();
            ForceRefresh();
        }

        private void OnAccountPositionUpdate(object sender, PositionEventArgs e)
        {
            if (State != State.Terminated && e.Position != null && IsMine(e.Position.Instrument)) ForceRefresh();
        }
        #endregion

        #region Orders / positions helpers
        private List<Order> GetWorkingOrders()
        {
            List<Order> list = new List<Order>();
            Account acct = account;
            if (acct == null) return list;
            lock (acct.Orders)
                foreach (Order o in acct.Orders)
                    if (IsMine(o.Instrument) && !Order.IsTerminalState(o.OrderState))
                        list.Add(o);
            return list;
        }

        private Position GetPosition(Account acct)
        {
            if (acct == null) return null;
            lock (acct.Positions)
                return acct.Positions.FirstOrDefault(p => IsMine(p.Instrument));
        }

        private static bool IsBuy(Order o)
        {
            return o.OrderAction == OrderAction.Buy || o.OrderAction == OrderAction.BuyToCover;
        }

        private static double OrderPrice(Order o)
        {
            return (o.OrderType == OrderType.Limit || o.OrderType == OrderType.MIT) ? o.LimitPrice : o.StopPrice;
        }

        private static string OrderTag(Order o)
        {
            switch (o.OrderType)
            {
                case OrderType.Limit:      return "LMT";
                case OrderType.StopMarket: return "STP";
                case OrderType.StopLimit:  return "SLM";
                case OrderType.MIT:        return "MIT";
                default:                   return "MKT";
            }
        }

        private double RefPrice()
        {
            if (lastPrice > 0) return lastPrice;
            if (Bars != null && Bars.Count > 0) return Bars.GetClose(Bars.Count - 1);
            return 0;
        }
        #endregion

        #region Daily realized P/L (FIFO over today's executions)
        private DateTime GetSessionStart()
        {
            try
            {
                lock (sessionLock)
                {
                    if (sessionIterator == null) return DateTime.Today;
                    sessionIterator.GetNextSession(NinjaTrader.Core.Globals.Now, true);
                    return sessionIterator.ActualSessionBegin;
                }
            }
            catch { return DateTime.Today; }
        }

        private void RecalcRealized()
        {
            Account acct = account;
            if (acct == null || Instrument == null) { lock (pnlLock) realizedToday = 0; return; }

            DateTime start = GetSessionStart();
            List<Execution> execs;
            lock (acct.Executions)
                execs = acct.Executions.Where(x => IsMine(x.Instrument) && x.Time >= start)
                                       .OrderBy(x => x.Time).ToList();

            int todayNet = 0;
            foreach (Execution x in execs)
                todayNet += (x.MarketPosition == MarketPosition.Long ? 1 : -1) * x.Quantity;

            Position pos  = GetPosition(acct);
            int curSigned = 0;
            if (pos != null)
                curSigned = pos.MarketPosition == MarketPosition.Long ? pos.Quantity
                          : pos.MarketPosition == MarketPosition.Short ? -pos.Quantity : 0;

            // Position carried in from a prior session is seeded at its average price.
            LinkedList<Lot> lots = new LinkedList<Lot>();
            int carried = curSigned - todayNet;
            if (carried != 0)
            {
                double seed = (pos != null && pos.AveragePrice > 0) ? pos.AveragePrice
                            : (execs.Count > 0 ? execs[0].Price : 0);
                lots.AddLast(new Lot { Qty = carried, Price = seed });
            }

            double pv = Instrument.MasterInstrument.PointValue;
            double realized = 0;
            foreach (Execution x in execs)
            {
                int sign = x.MarketPosition == MarketPosition.Long ? 1 : -1;
                int q    = x.Quantity;
                while (q > 0 && lots.Count > 0 && Math.Sign(lots.First.Value.Qty) == -sign)
                {
                    Lot l     = lots.First.Value;
                    int match = Math.Min(q, Math.Abs(l.Qty));
                    realized += match * (x.Price - l.Price) * Math.Sign(l.Qty) * pv;
                    l.Qty    -= match * Math.Sign(l.Qty);
                    q        -= match;
                    if (l.Qty == 0) lots.RemoveFirst();
                }
                if (q > 0) lots.AddLast(new Lot { Qty = q * sign, Price = x.Price });
            }
            lock (pnlLock) realizedToday = realized;
        }
        #endregion

        #region Market data
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if      (e.MarketDataType == MarketDataType.Ask)  bestAsk   = e.Price;
            else if (e.MarketDataType == MarketDataType.Bid)  bestBid   = e.Price;
            else if (e.MarketDataType == MarketDataType.Last) lastPrice = e.Price;
        }

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (!ShowDepth) return;
            lock (depthLock)
            {
                List<DepthRow> rows = e.MarketDataType == MarketDataType.Ask ? askRows
                                    : e.MarketDataType == MarketDataType.Bid ? bidRows : null;
                if (rows == null) return;
                int p = e.Position;

                if (e.Operation == Operation.Add)
                {
                    if (p <= rows.Count) rows.Insert(p, new DepthRow { Price = e.Price, Volume = e.Volume });
                    else                 rows.Add(new DepthRow { Price = e.Price, Volume = e.Volume });
                }
                else if (e.Operation == Operation.Remove)
                {
                    if (p < rows.Count) rows.RemoveAt(p);
                }
                else if (e.Operation == Operation.Update)
                {
                    if (p < rows.Count) { rows[p].Price = e.Price; rows[p].Volume = e.Volume; }
                    else if (p == rows.Count) rows.Add(new DepthRow { Price = e.Price, Volume = e.Volume });
                }
            }

            if ((DateTime.Now - lastDepthRefresh).TotalMilliseconds > 150)
            {
                lastDepthRefresh = DateTime.Now;
                ForceRefresh();
            }
        }
        #endregion

        #region Order actions
        private bool ThrottleOk()
        {
            DateTime now = DateTime.Now;
            while (orderTimes.Count > 0 && (now - orderTimes.Peek()).TotalSeconds >= 1) orderTimes.Dequeue();
            if (MaxOrdersPerSecond > 0 && orderTimes.Count >= MaxOrdersPerSecond)
            {
                Log("ChartDomTrader: order blocked — more than " + MaxOrdersPerSecond + " orders/second.", LogLevel.Warning);
                return false;
            }
            orderTimes.Enqueue(now);
            return true;
        }

        private void SubmitOrder(Account acct, OrderAction action, OrderType type, int qty,
                                 double limit, double stop, bool allowAtm)
        {
            if (acct == null) { Log("ChartDomTrader: no account selected.", LogLevel.Warning); return; }
            if (!ThrottleOk()) return;

            bool   useAtm = allowAtm && !string.IsNullOrWhiteSpace(AtmTemplate);
            string name   = useAtm ? "Entry" : "ChartDom";   // ATM requires the entry to be named "Entry"

            try
            {
                Order o = acct.CreateOrder(Instrument, action, type, OrderEntry.Manual, TimeInForce.Day, qty,
                                           limit, stop, string.Empty, name, NinjaTrader.Core.Globals.MaxDate, null);
                if (useAtm) NinjaTrader.NinjaScript.AtmStrategy.StartAtmStrategy(AtmTemplate, o);
                else        acct.Submit(new[] { o });
            }
            catch (Exception ex)
            {
                Log("ChartDomTrader: submit failed — " + ex.Message, LogLevel.Error);
            }
        }

        private void PlaceColumnOrder(bool buyColumn, double price, bool stopLimit)
        {
            Account acct = ResolveAccount();
            int     qty  = ResolveQuantity();
            double  last = RefPrice();
            price        = Instrument.MasterInstrument.RoundToTickSize(price);
            if (last <= 0) return;

            if (buyColumn)
            {
                if (price <= last) SubmitOrder(acct, OrderAction.Buy, OrderType.Limit, qty, price, 0, true);
                else if (stopLimit) SubmitOrder(acct, OrderAction.Buy, OrderType.StopLimit, qty, price, price, true);
                else SubmitOrder(acct, OrderAction.Buy, OrderType.StopMarket, qty, 0, price, true);
            }
            else
            {
                if (price >= last) SubmitOrder(acct, OrderAction.Sell, OrderType.Limit, qty, price, 0, true);
                else if (stopLimit) SubmitOrder(acct, OrderAction.Sell, OrderType.StopLimit, qty, price, price, true);
                else SubmitOrder(acct, OrderAction.Sell, OrderType.StopMarket, qty, 0, price, true);
            }
        }

        private void MoveOrder(Order o, double newPrice)
        {
            Account acct = account;
            if (acct == null || o == null || Order.IsTerminalState(o.OrderState)) return;
            newPrice = Instrument.MasterInstrument.RoundToTickSize(newPrice);
            if (newPrice.ApproxCompare(OrderPrice(o)) == 0) return;

            try
            {
                if (o.OrderType == OrderType.Limit || o.OrderType == OrderType.MIT)
                    o.LimitPriceChanged = newPrice;
                else if (o.OrderType == OrderType.StopMarket)
                    o.StopPriceChanged = newPrice;
                else if (o.OrderType == OrderType.StopLimit)
                {
                    double offset = o.LimitPrice - o.StopPrice;   // keep stop/limit spread
                    o.StopPriceChanged  = newPrice;
                    o.LimitPriceChanged = newPrice + offset;
                }
                else return;
                acct.Change(new[] { o });
            }
            catch (Exception ex) { Log("ChartDomTrader: change failed — " + ex.Message, LogLevel.Error); }
        }

        private void CancelOrder(Order o)
        {
            Account acct = account;
            if (acct == null || o == null) return;
            try { acct.Cancel(new[] { o }); }
            catch (Exception ex) { Log("ChartDomTrader: cancel failed — " + ex.Message, LogLevel.Error); }
        }

        private void JoinBest(Account acct, bool buy)
        {
            double price = buy ? (bestBid > 0 ? bestBid : GetCurrentBid())
                               : (bestAsk > 0 ? bestAsk : GetCurrentAsk());
            if (price <= 0) { Log("ChartDomTrader: no bid/ask available.", LogLevel.Warning); return; }
            price = Instrument.MasterInstrument.RoundToTickSize(price);
            SubmitOrder(acct, buy ? OrderAction.Buy : OrderAction.Sell, OrderType.Limit,
                        ResolveQuantity(), price, 0, true);
        }

        private void Reverse()
        {
            Account  acct = ResolveAccount();
            Position pos  = GetPosition(acct);
            if (acct == null || pos == null || pos.MarketPosition == MarketPosition.Flat) return;

            acct.CancelAllOrders(Instrument);   // drop old brackets before flipping
            OrderAction action = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
            SubmitOrder(acct, action, OrderType.Market, pos.Quantity * 2, 0, 0, false);
        }
        #endregion

        #region Mouse
        private void GetDevicePoint(MouseEventArgs e, out float x, out float y)
        {
            System.Windows.Point p = e.GetPosition(ChartControl as IInputElement);
            x = ChartingExtensions.ConvertToHorizontalPixels(p.X, ChartControl.PresentationSource);
            y = ChartingExtensions.ConvertToVerticalPixels(p.Y, ChartControl.PresentationSource);
        }

        private int HitTest(float x, float y)
        {
            if (!layoutValid) return HitNone;
            if (ShowButtons)
            {
                if (btnFlat.Contains(x, y))    return HitFlat;
                if (btnCancel.Contains(x, y))  return HitCancel;
                if (btnRev.Contains(x, y))     return HitRev;
                if (btnBuyMkt.Contains(x, y))  return HitBuyMkt;
                if (btnSellMkt.Contains(x, y)) return HitSellMkt;
            }
            if (panelRect.Contains(x, y)) return HitLabel;   // swallow clicks on the panel body
            if (bidColRect.Contains(x, y)) return HitBid;
            if (askColRect.Contains(x, y)) return HitAsk;
            return HitNone;
        }

        private Order FindOrderAt(bool buyColumn, float y)
        {
            if (lastScale == null) return null;
            float  tol  = Math.Max(RowHeight(lastScale) / 2f, 8f);
            Order  best = null;
            float  bestD = float.MaxValue;
            foreach (Order o in GetWorkingOrders())
            {
                if (IsBuy(o) != buyColumn) continue;
                float d = Math.Abs(lastScale.GetYByValue(OrderPrice(o)) - y);
                if (d <= tol && d < bestD) { bestD = d; best = o; }
            }
            return best;
        }

        private double PriceAtY(float y)
        {
            return Instrument.MasterInstrument.RoundToTickSize(lastScale.GetValueByY(y));
        }

        private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
        {
            if (lastScale == null) return;
            float x, y; GetDevicePoint(e, out x, out y);
            int hit = HitTest(x, y);
            if (hit == HitNone) return;
            e.Handled = true;
            // panel can be moved any time; order entry only in realtime
            if (hit != HitLabel && State != State.Realtime) return;

            // panel body / header -> move the panel (double-click header = snap back to corner)
            if (hit == HitLabel)
            {
                if (e.ClickCount == 2 && headerRect.Contains(x, y))
                {
                    PanelPosX = -1; PanelPosY = -1;
                    ForceRefresh();
                    return;
                }
                panelDragging = true;
                grabDX = x - panelRect.Left;
                grabDY = y - panelRect.Top;
                ChartControl.CaptureMouse();
                ForceRefresh();
                return;
            }

            Account acct = ResolveAccount();
            switch (hit)
            {
                case HitBuyMkt:  JoinBest(acct, true);  break;   // BUY BID  -> buy limit at best bid
                case HitSellMkt: JoinBest(acct, false); break;   // SELL ASK -> sell limit at best ask
                case HitRev:     Reverse(); break;
                case HitCancel:  if (acct != null) acct.CancelAllOrders(Instrument); break;
                case HitFlat:    if (acct != null) acct.Flatten(new[] { Instrument }); break;
                case HitBid:
                case HitAsk:
                    Order o = FindOrderAt(hit == HitBid, y);
                    if (o != null)
                    {
                        dragOrder = o; dragY = y; dragMoved = false;
                        ChartControl.CaptureMouse();
                    }
                    else
                        PlaceColumnOrder(hit == HitBid, PriceAtY(y), (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                    break;
            }
            ForceRefresh();
        }

        private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
        {
            if (panelDragging)
            {
                panelDragging = false;
                e.Handled = true;
                if (ChartControl.IsMouseCaptured) ChartControl.ReleaseMouseCapture();
                ForceRefresh();
                return;
            }
            if (dragOrder == null) return;
            e.Handled = true;
            float x, y; GetDevicePoint(e, out x, out y);
            if (ChartControl.IsMouseCaptured) ChartControl.ReleaseMouseCapture();
            if (dragMoved && lastScale != null) MoveOrder(dragOrder, PriceAtY(y));
            dragOrder = null;
            ForceRefresh();
        }

        private void OnMouseRightDown(object sender, MouseButtonEventArgs e)
        {
            if (State != State.Realtime || lastScale == null) return;
            float x, y; GetDevicePoint(e, out x, out y);
            int hit = HitTest(x, y);
            if (hit == HitNone) return;
            e.Handled = true;
            rightConsumed = true;

            if (hit == HitBid || hit == HitAsk)
            {
                Order o = FindOrderAt(hit == HitBid, y);
                if (o != null) CancelOrder(o);
                else SubmitOrder(ResolveAccount(), hit == HitBid ? OrderAction.Buy : OrderAction.Sell,
                                 OrderType.Market, ResolveQuantity(), 0, 0, true);
            }
            ForceRefresh();
        }

        private void OnMouseRightUp(object sender, MouseButtonEventArgs e)
        {
            if (!rightConsumed) return;
            rightConsumed = false;
            e.Handled = true;   // suppress the chart context menu
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            float x, y; GetDevicePoint(e, out x, out y);
            bool wasInside = mouseInside;
            mouseX = x; mouseY = y;
            int hit = HitTest(x, y);
            mouseInside = hit != HitNone;

            // move cursor over the draggable panel area
            bool wantMoveCursor = panelDragging || hit == HitLabel;
            if (wantMoveCursor && !cursorSet)      { ChartControl.Cursor = Cursors.SizeAll; cursorSet = true; }
            else if (!wantMoveCursor && cursorSet) { ChartControl.Cursor = null;           cursorSet = false; }

            if (panelDragging)
            {
                float pw = PanelWidth;
                float nx = x - grabDX;
                float ny = y - grabDY;
                PanelPosX = Math.Max(0, lastChartRight - nx - pw);
                PanelPosY = Math.Max(0, ny - lastChartTop);
                e.Handled = true;
                ForceRefresh();
            }
            else if (dragOrder != null)
            {
                if (Math.Abs(y - dragY) > 2) dragMoved = true;
                dragY = y;
                ForceRefresh();
            }
            else if (mouseInside || wasInside)
                ForceRefresh();
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            if (cursorSet && !panelDragging) { ChartControl.Cursor = null; cursorSet = false; }
            if (!mouseInside) return;
            mouseInside = false;
            ForceRefresh();
        }
        #endregion

        #region Rendering
        private float RowHeight(ChartScale scale)
        {
            double range = Math.Max(1e-9, scale.MaxValue - scale.MinValue);
            return Math.Max(1f, (float)(ChartPanel.H * TickSize / range));
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDx();
            if (RenderTarget != null) CreateDx();
        }

        private D2D.SolidColorBrush MakeBrush(Brush b, float opacity)
        {
            SolidColorBrush sc = b as SolidColorBrush;
            Color c = sc != null ? sc.Color : Colors.Gray;
            D2D.SolidColorBrush dx = new D2D.SolidColorBrush(RenderTarget,
                new DX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, (c.A / 255f) * opacity));
            dxResources.Add(dx);
            return dx;
        }

        private DW.TextFormat MakeFormat(SimpleFont f, DW.TextAlignment align)
        {
            DW.TextFormat tf = f.ToDirectWriteTextFormat();
            tf.TextAlignment      = align;
            tf.ParagraphAlignment = DW.ParagraphAlignment.Center;
            tf.WordWrapping       = DW.WordWrapping.NoWrap;
            dxResources.Add(tf);
            return tf;
        }

        private void CreateDx()
        {
            float colOp = Math.Max(0, Math.Min(100, ColumnOpacity)) / 100f;
            dxBidCol    = MakeBrush(BidColumnBrush, colOp);
            dxAskCol    = MakeBrush(AskColumnBrush, colOp);
            dxDepthBid  = MakeBrush(DepthBidBrush,  0.45f);
            dxDepthAsk  = MakeBrush(DepthAskBrush,  0.45f);
            dxBuyOrder  = MakeBrush(BuyOrderBrush,  0.90f);
            dxSellOrder = MakeBrush(SellOrderBrush, 0.90f);
            dxButton    = MakeBrush(ButtonBrush,    1f);
            dxText      = MakeBrush(TextBrush,      1f);
            dxProfit    = MakeBrush(ProfitBrush,    1f);
            dxLoss      = MakeBrush(LossBrush,      1f);
            dxHover     = MakeBrush(TextBrush,      0.18f);
            dxBorder    = MakeBrush(TextBrush,      0.35f);
            dxLabelBg   = MakeBrush(Brushes.Black,  0.55f);
            dxDarkText  = MakeBrush(Brushes.Black,  1f);

            SimpleFont bold = new SimpleFont(TextFont.Family.ToString(), (int)Math.Round(TextFont.Size)) { Bold = true };
            tfCenter = MakeFormat(TextFont, DW.TextAlignment.Center);
            tfRight  = MakeFormat(TextFont, DW.TextAlignment.Trailing);
            tfButton = MakeFormat(bold,     DW.TextAlignment.Center);
        }

        private void DisposeDx()
        {
            foreach (IDisposable d in dxResources)
                if (d != null) d.Dispose();
            dxResources.Clear();
            dxBidCol = null;
            tfCenter = null;
        }

        private void DrawCell(DX.RectangleF r, string text, DW.TextFormat tf, D2D.Brush brush)
        {
            if (r.Width > 4 && r.Height > 4) RenderTarget.DrawText(text, tf, r, brush);
        }

        private void DrawButton(DX.RectangleF r, string text, bool filled, D2D.SolidColorBrush color)
        {
            D2D.RoundedRectangle rr = new D2D.RoundedRectangle { Rect = r, RadiusX = 2, RadiusY = 2 };
            bool hover = mouseInside && dragOrder == null && r.Contains(mouseX, mouseY);
            if (filled)
            {
                RenderTarget.FillRoundedRectangle(rr, color);
                if (hover) RenderTarget.FillRoundedRectangle(rr, dxHover);
                DrawCell(r, text, tfButton, dxDarkText);
            }
            else
            {
                RenderTarget.FillRoundedRectangle(rr, hover ? dxHover : dxLabelBg);
                RenderTarget.DrawRoundedRectangle(rr, color, 1f);
                DrawCell(r, text, tfButton, color);
            }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (RenderTarget == null || ChartPanel == null || Bars == null || Instrument == null) return;
            if (dxBidCol == null || tfCenter == null) CreateDx();

            lastScale = chartScale;
            Account  acct = ResolveAccount();
            Position pos  = GetPosition(acct);

            D2D.AntialiasMode oldAA = RenderTarget.AntialiasMode;
            RenderTarget.AntialiasMode = D2D.AntialiasMode.Aliased;

            // ---------- layout ----------
            float top    = ChartPanel.Y;
            float bottom = ChartPanel.Y + ChartPanel.H;
            float w      = ColumnWidth;
            float right  = ChartPanel.X + ChartPanel.W - RightOffset;
            askColRect   = new DX.RectangleF(right - w,     top, w, ChartPanel.H);
            bidColRect   = new DX.RectangleF(right - 2 * w, top, w, ChartPanel.H);
            float left   = bidColRect.Left;
            const float pad = 5f, boxH = 18f, btnH = 22f, gap = 3f, infoH = 14f, headH = 12f;

            // DOM columns stay completely free: every row is tradable top to bottom.
            labelsBottom = top;
            buttonsTop   = bottom;

            // ---- control panel: P/L + buttons, docked in the upper/lower right corner, left of the DOM ----
            float contentH = headH + gap + infoH;
            if (ShowPnl)                contentH += boxH + gap;
            if (ShowPnl && ShowOpenPnl) contentH += boxH + gap;
            if (ShowButtons)            contentH += gap + 3 * btnH + 2 * gap;

            float pw = PanelWidth;
            float ph = contentH + 2 * pad;
            float chartRight = ChartPanel.X + ChartPanel.W;
            lastChartRight   = chartRight;
            lastChartTop     = top;

            float px, py;
            if (PanelPosX >= 0 && PanelPosY >= 0)
            {
                // user-placed: stored as distance from chart's right edge and top, so it survives resizes
                px = chartRight - (float)PanelPosX - pw;
                py = top + (float)PanelPosY;
            }
            else
            {
                // default dock: upper/lower corner just left of the DOM
                px = left - PanelMargin - pw;
                py = PanelAtBottom ? bottom - PanelMargin - ph : top + PanelMargin;
            }
            px = Math.Max(ChartPanel.X, Math.Min(px, chartRight - pw));
            py = Math.Max(top,          Math.Min(py, bottom - ph));
            panelRect  = new DX.RectangleF(px, py, pw, ph);
            headerRect = new DX.RectangleF(px, py, pw, headH + pad);

            float ix = px + pad, iw = pw - 2 * pad, yc = py + pad + headH + gap;
            pnlRect  = ShowPnl ? new DX.RectangleF(ix, yc, iw, boxH) : DX.RectangleF.Empty;
            if (ShowPnl) yc += boxH + gap;
            openRect = ShowPnl && ShowOpenPnl ? new DX.RectangleF(ix, yc, iw, boxH) : DX.RectangleF.Empty;
            if (ShowPnl && ShowOpenPnl) yc += boxH + gap;
            infoRect = new DX.RectangleF(ix, yc, iw, infoH);
            yc += infoH + gap;

            if (ShowButtons)
            {
                float half = (iw - gap) / 2f;
                btnBuyMkt  = new DX.RectangleF(ix,              yc, half, btnH);
                btnSellMkt = new DX.RectangleF(ix + half + gap, yc, half, btnH); yc += btnH + gap;
                btnRev     = new DX.RectangleF(ix,              yc, iw,   btnH); yc += btnH + gap;
                btnCancel  = new DX.RectangleF(ix,              yc, half, btnH);
                btnFlat    = new DX.RectangleF(ix + half + gap, yc, half, btnH);
            }
            layoutValid = true;

            float rowH   = RowHeight(chartScale);
            bool  rowTxt = rowH >= 9f;

            // ---------- columns ----------
            RenderTarget.FillRectangle(bidColRect, dxBidCol);
            RenderTarget.FillRectangle(askColRect, dxAskCol);
            RenderTarget.DrawLine(new DX.Vector2(askColRect.Left, top), new DX.Vector2(askColRect.Left, bottom), dxBorder, 1f);

            // ---------- depth ----------
            if (ShowDepth)
            {
                List<DepthRow> bids, asks;
                lock (depthLock)
                {
                    bids = bidRows.Select(r => new DepthRow { Price = r.Price, Volume = r.Volume }).ToList();
                    asks = askRows.Select(r => new DepthRow { Price = r.Price, Volume = r.Volume }).ToList();
                }
                double hi = chartScale.MaxValue, lo = chartScale.MinValue;
                long maxVol = 1;
                foreach (DepthRow r in bids) if (r.Price >= lo && r.Price <= hi) maxVol = Math.Max(maxVol, r.Volume);
                foreach (DepthRow r in asks) if (r.Price >= lo && r.Price <= hi) maxVol = Math.Max(maxVol, r.Volume);

                DrawDepth(bids, bidColRect, dxDepthBid, maxVol, rowH, rowTxt, chartScale);
                DrawDepth(asks, askColRect, dxDepthAsk, maxVol, rowH, rowTxt, chartScale);
            }

            // ---------- bid / ask / last ----------
            double bid = bestBid > 0 ? bestBid : GetCurrentBid();
            double ask = bestAsk > 0 ? bestAsk : GetCurrentAsk();
            double last = RefPrice();

            if (bid > 0)
            {
                float y = chartScale.GetYByValue(bid);
                RenderTarget.DrawRectangle(new DX.RectangleF(bidColRect.Left + 1, y - rowH / 2f, w - 2, rowH), dxDepthBid, 1f);
                if (ShowBidAskLines) RenderTarget.DrawLine(new DX.Vector2(ChartPanel.X, y), new DX.Vector2(left, y), dxDepthBid, 1f);
            }
            if (ask > 0)
            {
                float y = chartScale.GetYByValue(ask);
                RenderTarget.DrawRectangle(new DX.RectangleF(askColRect.Left + 1, y - rowH / 2f, w - 2, rowH), dxDepthAsk, 1f);
                if (ShowBidAskLines) RenderTarget.DrawLine(new DX.Vector2(ChartPanel.X, y), new DX.Vector2(left, y), dxDepthAsk, 1f);
            }
            if (last > 0)
            {
                float y = chartScale.GetYByValue(last);
                RenderTarget.DrawLine(new DX.Vector2(left, y), new DX.Vector2(right, y), dxText, 1f);
            }

            // ---------- hover preview ----------
            if (mouseInside && dragOrder == null && mouseY > labelsBottom && mouseY < buttonsTop && last > 0)
            {
                bool inBid = bidColRect.Contains(mouseX, mouseY);
                bool inAsk = askColRect.Contains(mouseX, mouseY);
                if (inBid || inAsk)
                {
                    double p   = PriceAtY(mouseY);
                    float  y   = chartScale.GetYByValue(p);
                    float  h   = Math.Max(rowH, 16f);
                    DX.RectangleF cell = new DX.RectangleF(inBid ? bidColRect.Left : askColRect.Left, y - h / 2f, w, h);
                    RenderTarget.FillRectangle(cell, dxHover);

                    bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                    string tag = inBid ? (p <= last ? "B LMT" : (shift ? "B SLM" : "B STP"))
                                       : (p >= last ? "S LMT" : (shift ? "S SLM" : "S STP"));
                    DrawCell(cell, tag + " " + ResolveQuantity(), tfCenter, dxText);
                }
            }

            // ---------- position ----------
            if (pos != null && pos.MarketPosition != MarketPosition.Flat)
            {
                bool  isLong = pos.MarketPosition == MarketPosition.Long;
                float y      = chartScale.GetYByValue(pos.AveragePrice);
                D2D.SolidColorBrush pb = isLong ? dxProfit : dxLoss;
                RenderTarget.DrawLine(new DX.Vector2(left, y), new DX.Vector2(right, y), pb, 2f);
                DX.RectangleF tagR = new DX.RectangleF(left - 36, y - 8, 34, 16);
                RenderTarget.FillRectangle(tagR, pb);
                DrawCell(tagR, (isLong ? "+" : "-") + pos.Quantity, tfCenter, dxDarkText);
            }

            // ---------- working orders ----------
            foreach (Order o in GetWorkingOrders())
            {
                bool  buy  = IsBuy(o);
                bool  drag = dragOrder != null && o == dragOrder && dragMoved;
                float y    = chartScale.GetYByValue(OrderPrice(o));
                float h    = Math.Max(rowH, 16f);
                DX.RectangleF col  = buy ? bidColRect : askColRect;
                DX.RectangleF cell = new DX.RectangleF(col.Left + 1, y - h / 2f, w - 2, h);
                D2D.SolidColorBrush ob = buy ? dxBuyOrder : dxSellOrder;
                string txt = OrderTag(o) + " " + (o.Quantity - o.Filled);

                if (drag)
                {
                    RenderTarget.DrawRectangle(cell, ob, 1f);
                    float gy = chartScale.GetYByValue(PriceAtY(dragY));
                    DX.RectangleF ghost = new DX.RectangleF(col.Left + 1, gy - h / 2f, w - 2, h);
                    RenderTarget.FillRectangle(ghost, ob);
                    DrawCell(ghost, txt, tfCenter, dxDarkText);
                    RenderTarget.DrawLine(new DX.Vector2(ChartPanel.X, gy), new DX.Vector2(col.Left, gy), ob, 1f);
                }
                else
                {
                    RenderTarget.FillRectangle(cell, ob);
                    DrawCell(cell, txt, tfCenter, dxDarkText);
                    RenderTarget.DrawLine(new DX.Vector2(ChartPanel.X, y), new DX.Vector2(col.Left, y), ob, 1f);
                }
            }

            // ---------- control panel ----------
            D2D.RoundedRectangle panelRR = new D2D.RoundedRectangle { Rect = panelRect, RadiusX = 3, RadiusY = 3 };
            RenderTarget.FillRoundedRectangle(panelRR, dxLabelBg);
            RenderTarget.FillRoundedRectangle(panelRR, dxLabelBg);   // double pass = ~80% opaque backdrop
            RenderTarget.DrawRoundedRectangle(panelRR, dxBorder, 1f);

            // drag handle / header
            bool overHeader = panelDragging || (mouseInside && panelRect.Contains(mouseX, mouseY) && HitTest(mouseX, mouseY) == HitLabel);
            DX.RectangleF grip = new DX.RectangleF(headerRect.Left + 1, headerRect.Top + 1, headerRect.Width - 2, headerRect.Height - 2);
            if (overHeader) RenderTarget.FillRectangle(grip, dxHover);
            DrawCell(grip, "• • •", tfCenter, overHeader ? dxText : dxBorder);
            RenderTarget.DrawLine(new DX.Vector2(headerRect.Left + 4, headerRect.Bottom),
                                  new DX.Vector2(headerRect.Right - 4, headerRect.Bottom), dxBorder, 1f);

            double unreal = 0;
            if (pos != null && pos.MarketPosition != MarketPosition.Flat && last > 0)
                unreal = pos.GetUnrealizedProfitLoss(PerformanceUnit.Currency, last);
            double realized;
            lock (pnlLock) realized = realizedToday;

            if (ShowPnl)
            {
                DrawPnlBox(pnlRect, "D: ", realized + unreal);
                if (ShowOpenPnl) DrawPnlBox(openRect, "O: ", unreal);
            }
            string posTxt = pos == null || pos.MarketPosition == MarketPosition.Flat ? "Flat"
                          : (pos.MarketPosition == MarketPosition.Long ? "+" : "-") + pos.Quantity;
            DrawCell(infoRect, posTxt + "  |  Q " + ResolveQuantity() + (acct == null ? "  |  no acct" : ""), tfCenter, dxText);

            // ---------- buttons ----------
            if (ShowButtons)
            {
                DrawButton(btnBuyMkt,  "BUY BID",    true,  dxProfit);
                DrawButton(btnSellMkt, "SELL ASK",   true,  dxLoss);
                DrawButton(btnRev,     "REV",        false, dxText);
                DrawButton(btnCancel,  "CANCEL ALL", false, dxButton);
                DrawButton(btnFlat,    "FLAT ALL",   true,  dxButton);
            }

            RenderTarget.AntialiasMode = oldAA;
        }

        private void DrawDepth(List<DepthRow> rows, DX.RectangleF col, D2D.SolidColorBrush brush, long maxVol,
                               float rowH, bool rowTxt, ChartScale scale)
        {
            foreach (DepthRow r in rows)
            {
                if (r.Price < scale.MinValue || r.Price > scale.MaxValue || r.Volume <= 0) continue;
                float y  = scale.GetYByValue(r.Price);
                float bw = (float)(col.Width * r.Volume / (double)maxVol);
                DX.RectangleF bar = new DX.RectangleF(col.Left, y - rowH / 2f, bw, Math.Max(1f, rowH - 1f));
                RenderTarget.FillRectangle(bar, brush);
                if (rowTxt && y > labelsBottom && y < buttonsTop)
                    DrawCell(new DX.RectangleF(col.Left, y - rowH / 2f, col.Width - 3, rowH),
                             r.Volume.ToString(CultureInfo.CurrentCulture), tfRight, dxText);
            }
        }

        private void DrawPnlBox(DX.RectangleF r, string prefix, double value)
        {
            D2D.SolidColorBrush c = value > 0.005 ? dxProfit : value < -0.005 ? dxLoss : dxText;
            RenderTarget.FillRectangle(r, dxLabelBg);
            RenderTarget.DrawRectangle(r, dxBorder, 1f);
            DrawCell(r, prefix + value.ToString("N2", CultureInfo.CurrentCulture) + " " + CurrencySymbol, tfButton, c);
        }
        #endregion

        #region Properties
        // ---- Layout ----
        [NinjaScriptProperty, Range(20, 300)]
        [Display(Name = "Column width (px)", Order = 1, GroupName = "1. Layout")]
        public int ColumnWidth { get; set; }

        [NinjaScriptProperty, Range(0, 500)]
        [Display(Name = "Right offset (px)", Order = 2, GroupName = "1. Layout")]
        public int RightOffset { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reserve right margin", Description = "Widens the chart's right side margin so bars don't print under the DOM.", Order = 3, GroupName = "1. Layout")]
        public bool ReserveRightMargin { get; set; }

        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Column opacity %", Order = 4, GroupName = "1. Layout")]
        public int ColumnOpacity { get; set; }

        // ---- Control panel ----
        [NinjaScriptProperty]
        [Display(Name = "Panel in lower corner", Description = "Unchecked = upper-right corner, checked = lower-right corner (always left of the DOM).", Order = 1, GroupName = "1b. Control panel")]
        public bool PanelAtBottom { get; set; }

        [NinjaScriptProperty, Range(110, 400)]
        [Display(Name = "Panel width (px)", Order = 2, GroupName = "1b. Control panel")]
        public int PanelWidth { get; set; }

        [NinjaScriptProperty, Range(0, 200)]
        [Display(Name = "Panel margin (px)", Description = "Gap between the panel, the DOM and the chart edge.", Order = 3, GroupName = "1b. Control panel")]
        public int PanelMargin { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reserve margin for panel", Description = "Also widen the right margin so bars never print under the panel.", Order = 4, GroupName = "1b. Control panel")]
        public bool ReservePanelMargin { get; set; }

        // Saved panel position (workspace-persisted, not shown in the grid).
        // Distance in px from the chart's right edge to the panel's right edge, and from the chart top.
        [Browsable(false)] public double PanelPosX { get; set; }
        [Browsable(false)] public double PanelPosY { get; set; }

        // ---- Trading ----
        [NinjaScriptProperty]
        [Display(Name = "Use Chart Trader account/qty", Description = "Follow the Chart Trader account and quantity selectors when available.", Order = 1, GroupName = "2. Trading")]
        public bool UseChartTraderAccount { get; set; }

        [NinjaScriptProperty]
        [TypeConverter(typeof(NinjaTrader.NinjaScript.AccountNameConverter))]
        [Display(Name = "Fallback account", Order = 2, GroupName = "2. Trading")]
        public string AccountName { get; set; }

        [NinjaScriptProperty, Range(1, int.MaxValue)]
        [Display(Name = "Fallback quantity", Order = 3, GroupName = "2. Trading")]
        public int DefaultQuantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ATM template (blank = none)", Order = 4, GroupName = "2. Trading")]
        public string AtmTemplate { get; set; }

        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Max orders / second (0 = off)", Order = 5, GroupName = "2. Trading")]
        public int MaxOrdersPerSecond { get; set; }

        // ---- Display ----
        [NinjaScriptProperty]
        [Display(Name = "Show market depth", Order = 1, GroupName = "3. Display")]
        public bool ShowDepth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show bid/ask lines", Order = 2, GroupName = "3. Display")]
        public bool ShowBidAskLines { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show daily P/L (D:)", Order = 3, GroupName = "3. Display")]
        public bool ShowPnl { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show open P/L (O:)", Order = 4, GroupName = "3. Display")]
        public bool ShowOpenPnl { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show buttons", Order = 5, GroupName = "3. Display")]
        public bool ShowButtons { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Currency symbol", Order = 6, GroupName = "3. Display")]
        public string CurrencySymbol { get; set; }

        [Display(Name = "Font", Order = 7, GroupName = "3. Display")]
        public SimpleFont TextFont { get; set; }

        // ---- Colors ----
        [XmlIgnore, Display(Name = "Bid column", Order = 1, GroupName = "4. Colors")]
        public Brush BidColumnBrush { get; set; }
        [Browsable(false)] public string BidColumnBrushSerializable { get { return Serialize.BrushToString(BidColumnBrush); } set { BidColumnBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore, Display(Name = "Ask column", Order = 2, GroupName = "4. Colors")]
        public Brush AskColumnBrush { get; set; }
        [Browsable(false)] public string AskColumnBrushSerializable { get { return Serialize.BrushToString(AskColumnBrush); } set { AskColumnBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore, Display(Name = "Depth bid", Order = 3, GroupName = "4. Colors")]
        public Brush DepthBidBrush { get; set; }
        [Browsable(false)] public string DepthBidBrushSerializable { get { return Serialize.BrushToString(DepthBidBrush); } set { DepthBidBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore, Display(Name = "Depth ask", Order = 4, GroupName = "4. Colors")]
        public Brush DepthAskBrush { get; set; }
        [Browsable(false)] public string DepthAskBrushSerializable { get { return Serialize.BrushToString(DepthAskBrush); } set { DepthAskBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore, Display(Name = "Buy order", Order = 5, GroupName = "4. Colors")]
        public Brush BuyOrderBrush { get; set; }
        [Browsable(false)] public string BuyOrderBrushSerializable { get { return Serialize.BrushToString(BuyOrderBrush); } set { BuyOrderBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore, Display(Name = "Sell order", Order = 6, GroupName = "4. Colors")]
        public Brush SellOrderBrush { get; set; }
        [Browsable(false)] public string SellOrderBrushSerializable { get { return Serialize.BrushToString(SellOrderBrush); } set { SellOrderBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore, Display(Name = "Buttons", Order = 7, GroupName = "4. Colors")]
        public Brush ButtonBrush { get; set; }
        [Browsable(false)] public string ButtonBrushSerializable { get { return Serialize.BrushToString(ButtonBrush); } set { ButtonBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore, Display(Name = "Text", Order = 8, GroupName = "4. Colors")]
        public Brush TextBrush { get; set; }
        [Browsable(false)] public string TextBrushSerializable { get { return Serialize.BrushToString(TextBrush); } set { TextBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore, Display(Name = "Profit", Order = 9, GroupName = "4. Colors")]
        public Brush ProfitBrush { get; set; }
        [Browsable(false)] public string ProfitBrushSerializable { get { return Serialize.BrushToString(ProfitBrush); } set { ProfitBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore, Display(Name = "Loss", Order = 10, GroupName = "4. Colors")]
        public Brush LossBrush { get; set; }
        [Browsable(false)] public string LossBrushSerializable { get { return Serialize.BrushToString(LossBrush); } set { LossBrush = Serialize.StringToBrush(value); } }
        #endregion
    }
}
