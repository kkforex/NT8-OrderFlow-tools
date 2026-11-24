# NT8-OrderFlow-tools
These are a collection of orderflow tools coded for NT8, it needs a tick replay for the charts and preferably level 2 data for the heatmap. 

AVWAP

The AVWAP (Anchored Volume Weighted Average Price) indicator plots a VWAP anchored to a user-defined date and time, with optional ±1 SD and ±2 SD standard deviation bands and semi-transparent fills between them. Standard deviation is calculated using the volume-weighted formula sqrt(sum(v×p²)/sum(v) − VWAP²) where p = (H+L+C)/3, giving a statistically accurate measure of price dispersion around the mean. The anchor point, band multipliers, colours, and line styles are all configurable from the indicator properties panel.

This indicator is designed as an overlay on the price panel and works on any timeframe. It is best used to identify dynamic support and resistance zones relative to a significant market event such as an earnings report, economic release, or key swing high/low. When price trades within the first SD band it is considered fair value; moves beyond ±2 SD often signal overextension and potential mean reversion. No Tick Replay is required.

AVWAPBands

AVWAPBands is a drawing tool version of the AVWAP — instead of configuring a fixed anchor date in the properties panel, you click directly on any bar in the chart to set the anchor point and the bands update live from that bar to the latest bar. The tool uses identical mathematics to the AVWAP indicator (p = (H+L+C)/3, volume-weighted standard deviation) and renders ±1 SD and ±2 SD bands with colour-filled regions between them. The anchor dot and any band line can be dragged to re-anchor without re-applying the tool.

This makes AVWAPBands ideal for quickly anchoring to any structural point on the chart — a swing high or low, a gap, a news candle — without leaving the chart. The coloured fills (purple inner band, red upper, green lower) provide an instant visual read on where price is trading relative to fair value. It is best used alongside regular candlestick or footprint charts and pairs naturally with the AVWAP indicator when multiple anchor points are needed simultaneously.

BigTrades

BigTrades visualises large, aggressive market orders on the chart in the style of DeepCharts' "Big Trades" panel. Each print above the configured size threshold is marked with a circle or square at the trade price, sized and coloured by volume and direction (green for buy aggressor, red for sell aggressor). The indicator supports three aggregation modes — raw prints, aggregation by timestamp, and aggregation by time window — to reduce noise from split fills. The size threshold can be set manually or determined automatically from the live trade-size distribution once enough samples have been collected.

BigTrades is a tape reading tool designed to highlight the moments when large participants are aggressively taking liquidity. Clusters of large buy prints at a price level often confirm absorption or breakout intent; clusters of large sells can signal distribution. Tick Replay must be enabled on the data series to capture historical trades; without it, only real-time prints are shown. The indicator also supports configurable zone overlays to highlight areas with an imbalance of large buys versus large sells.

ChartDomTrader

ChartDomTrader is a DeepCharts-style Chart DOM order entry panel built directly into the NinjaTrader 8 price panel. It pins two columns to the right edge of the chart — a green BID/BUY column and a red ASK/SELL column — showing live Level 2 depth. Left-clicking in the BUY column below the last price places a Buy Limit; above the last price places a Buy Stop (and vice versa for the SELL column). Right-clicking in either column fires a market order. Working orders can be dragged to move them or right-clicked to cancel. An optional ATM strategy template can be attached to every entry.

A floating control panel complements the DOM, displaying the day's realized P&L, open P&L, and current position/quantity. It includes one-click buttons for passive BUY BID and SELL ASK limit orders, a REV (reverse) button, CANCEL ALL, and FLAT ALL. The panel can be dragged anywhere on the chart and double-clicked to snap back to its docked corner; position is saved with the workspace. Account and quantity selectors follow the Chart Trader controls when present, falling back to the indicator's own properties. Level 2 data is required for the depth columns to populate.

ImportantLevels

ImportantLevels draws the key reference levels from the previous Daily, Weekly, and Monthly periods directly on the chart: Open, High, Low, Close, Midpoint, Point of Control (POC), Value Area High (VAH), Value Area Low (VAL), and VWAP. It also plots the London session High and Low as of the New York open — DST-corrected for both cities — making it straightforward to track the most-watched institutional reference prices without switching charts. Each level and each period can be enabled or disabled individually, with configurable colours, line styles, label text, and label alignment.

A key design feature is that the calculation is fully independent of the chart's trading hours setting. The indicator loads the instrument's full ETH session internally and applies RTH or Custom time windows as a filter, so an RTH chart and an ETH chart draw identical levels for the same input settings. This avoids the common issue where changing the chart session shifts or removes levels unexpectedly. The indicator is intended as a structural roadmap — price reacting to or stalling at these levels, particularly POC and VAH/VAL, is one of the core setups in order flow and auction market theory.

OrderFlowGauges

OrderFlowGauges displays up to five real-time gauges on the chart showing the current pace and direction of order flow: Tick Speed (prints per second in a rolling window), Bid Volume (volume traded at or below the bid), Ask Volume (volume traded at or above the ask), Delta (Ask Volume minus Bid Volume), and an optional combined Bid|Ask dual gauge. Each gauge is independently toggleable and the panel can be anchored to any corner of the chart in horizontal or vertical layout. Scale mode can be set to Auto (rolling percentile) or Fixed, so the needles remain readable whether the market is quiet or in a fast move.

The gauges are powered by an OnMarketData(Last) engine that classifies every print as buy or sell aggressor by comparing the trade price to the live bid and ask at print time. A ring buffer with O(1) running sums keeps the rolling window calculation efficient even at high tick rates. A DispatcherTimer expires the window when the tape is quiet and caps the repaint rate so the chart remains responsive. These gauges are designed for live reading only — they intentionally ignore historical and Tick Replay data — making them a live tape reading tool rather than a backtesting indicator.

OrderFlow_SnR

OrderFlow_SnR builds support and resistance bands from single-bar volume profiles using Tick Replay data. For each completed bar, it constructs a full volume profile from the tick prints, computes the Point of Control (POC), Value Area High (VAH), and Value Area Low (VAL) at the configured value area percentage (default 70%). A band is drawn from VAL to VAH, coloured green if the bar closed bullish (support) or red if it closed bearish (resistance). A band remains on the chart until a subsequent bar closes beyond its opposite boundary — a support band is invalidated only when a bar closes below its VAL, and a resistance band only when a bar closes above its VAH.

This tool captures the idea that price levels where significant volume traded are likely to act as support or resistance in the future, based on the unfilled orders and anchored participants left behind. Four draw modes are available: every value area, high-volume bars only, delta-confirmed bars only, and swing-pivot bars only (the most selective, similar to the Deep-M style). An EMA 20 is plotted as a visual confluence reference but does not filter band creation. Tick Replay must be enabled on the data series for historical bars to have accurate profiles; without it, an OHLC fallback is used.

TradeHistory

TradeHistory renders every completed and open trade from a connected NinjaTrader 8 account directly on the price chart, giving an at-a-glance visual audit of execution quality and trade placement. For each matched entry/exit pair it draws a directional arrow at the entry price (up for long, down for short), a dashed line from entry to exit, a rounded label at the mid-line showing direction and P&L in points, and a diamond marker at the exit price. Open trades track live to the current price. A toolbar button on the chart allows the overlay to be toggled on and off without removing the indicator.

The indicator reads from Account.Executions rather than from the chart's data series, so no Tick Replay is required and it works on any timeframe or instrument. Connected accounts auto-populate in a dropdown property, defaulting to the simulation account (Sim101) when no live account is connected. The indicator is primarily useful for reviewing intraday execution relative to price structure — seeing whether entries were placed at key levels and whether exits were premature or well-timed. It is an overlay indicator and does not affect the chart's scale or other indicators.

SpreadCandlesticks

SpreadCandlesticks plots a synthetic inter-market spread as a standard OHLC candlestick chart in a separate panel below the price chart. The spread is computed as Qty1 × Instrument1 + Qty2 × Instrument2 bar by bar, where Instrument1 is the chart's primary data series and Instrument2 is any second symbol entered in the properties (for example NQ 12-26, SPY, or EURUSD). Using a negative Qty2 (the default is -1) creates a standard long/short spread. The OHLC values of the spread are exposed as named plots so they can be accessed by other scripts or the data box.

This indicator is useful for traders who monitor lead-lag relationships between correlated instruments — such as ES/NQ, ES/SPY, or CL/XLE — to spot divergences that may precede a reversion in the primary instrument. It was originally a NinjaTrader 7 indicator and has been fully ported to NinjaScript 8 with SharpDX rendering for high-quality candle drawing. No Tick Replay or Level 2 data is required. The panel scales automatically to the spread values and supports configurable up/down candle colours.
