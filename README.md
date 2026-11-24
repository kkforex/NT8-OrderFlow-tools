# NT8-OrderFlow-tools
These are a collection of orderflow tools coded for NT8, it needs a tick replay for the charts and preferably level 2 data for the heatmap. 

NT8CVD

NT8CVD is a Cumulative Volume Delta indicator that tracks the running total of buy-aggressor volume minus sell-aggressor volume tick by tick, displayed as either standard Candlesticks or Heikin Ashi candles rendered with DirectX for smooth, high-quality charting. The cumulative total can be reset at the start of each trading session, at each calendar day, at a custom user-defined time, or never (cumulative from the first loaded bar). An optional Filtered CVD line applies a per-print minimum and maximum size filter to isolate the contribution of institutional-sized prints only, coloured by its sign. Cumulative Ask and Bid volume lines can also be displayed simultaneously for a fuller picture of one-sided pressure.

NT8CVD is designed to reveal divergences between price and aggressive order flow — when price makes a higher high but CVD fails to confirm, it signals that the move is being driven by passive sellers absorbing market buys rather than genuine bullish conviction. The Heikin Ashi mode smooths the CVD candles, making trend changes in delta easier to read at a glance. Requires Tick Replay on the data series for accurate historical classification; without it the indicator plots from real-time data onward only.

NT8Delta

NT8Delta displays per-bar delta (ask-traded volume minus bid-traded volume) as a histogram in a separate panel, modelled on the NT8Charts Delta indicator. It supports two modes: Classic (a single histogram bar per price bar) and Multi-Range (four independently configurable volume ranges, each with its own bid and ask colours), making it possible to visually separate the contribution of retail-sized and institutional-sized prints within the same bar. Configurable threshold lines mark positive and negative delta extremes, and optional absorption markers flag bars where delta strongly opposes the bar's price direction. Shadow bars showing the intra-bar maximum and minimum delta can also be enabled.

This indicator is a core order flow tool for identifying buying and selling conviction on each bar. Strong positive delta on an up bar confirms buyers are aggressively lifting the offer; strong negative delta on a down bar confirms sellers are hitting the bid. Divergences — where price extends but delta weakens or reverses — are a primary signal for potential trend exhaustion or reversal. Requires Tick Replay for accurate historical bid/ask classification; without it only real-time bars are populated.

NT8DeltaSwing

NT8DeltaSwing is an order-flow swing delta indicator that builds a zig-zag structure on the chart using a configurable reversal size in ticks, then accumulates the aggressive volume delta (ask-traded minus bid-traded) for the full duration of each swing. Three signal algorithms fire when a swing completes: Rising/Falling Delta (delta deteriorating over three consecutive same-side swings while price keeps extending, signalling exhaustion), Pullback (a strong-delta impulse followed by a weak-delta shallow retracement), and Imbalance (a swing whose delta strongly opposes its price direction, indicating absorption). BUY and SELL markers, swing lines, delta labels, and a live developing-swing label are all drawn on the chart. A Signal plot (+1 / -1 / 0) is exposed for use in automated strategies or the Market Analyzer.

This indicator is best used on Range or Renko charts where each bar represents a fixed price move, allowing the swing structure to be purely price-driven. The combination of swing price direction and delta direction gives a nuanced read of whether a move has genuine aggressor conviction behind it or is running on empty. With Tick Replay enabled every trade is classified at the bid or ask; without it delta is estimated per bar from the Close Location Value (CLV) as a fallback. Alert functionality is included for all three signal types.

NT8Delta_Claude

NT8Delta_Claude is an alternative implementation of the NT8Delta indicator with an extended input data model supporting Volume, Trades count, and Aggregate Trades (consecutive same-side prints sharing a timestamp are merged into a single trade) modes. Like NT8Delta it supports Classic and Multi-Range display with four independently configurable volume ranges, threshold lines, absorption markers, and shadow bars. The aggregate-trades mode is particularly useful for reducing noise from split fills and better representing the number of distinct order decisions rather than the raw number of exchange prints.

This version was developed as a refined variant with cleaner enum naming (using NT8DeltaMode and NT8DeltaInputData) and improved tick-replay detection logic to prevent silent miscalculation when Tick Replay is not enabled. The functional output is equivalent to NT8Delta and both can be run side by side for comparison. As with all delta indicators in this collection, Tick Replay must be enabled on the data series for accurate historical bid/ask classification.

NT8Print

NT8Print is a full-featured bid/ask footprint indicator that renders a live per-bar volume profile at tick-level resolution directly on the chart. Each cell in the footprint shows the volume traded at the bid and ask at every price level within a bar. The indicator supports multiple background colouration modes — including Fixed, Fading, Volume-proportional, Delta, Imbalance, Dominant Side, and Dominant Delta — to highlight structurally significant cells at a glance. Tick grouping (Auto or Manual), Point of Control (POC), Delta POC, Value Area, Single Prints, Ratio, Volume Cluster, and Pull Back detection are all configurable from an organised properties panel.

NT8Print is the most feature-complete indicator in this collection and is designed for traders who read the tape at the footprint level, using imbalances (large bid volume at one price against small ask volume at the adjacent price, or vice versa) and stacked imbalances to identify where trapped participants and exhaustion are most concentrated. Memory usage is carefully managed: only one compact bid[] + ask[] array per bar is stored at 1-tick resolution, with optional caps on stored bar count. All rendering is done with cached DirectX brushes and DirectWrite text formats for performance. Requires Tick Replay for historical data.

NT8Profile

NT8Profile draws a composite volume profile for a configurable scope — the latest N sessions, the latest N minutes, the currently visible chart range, or a custom range defined by an NT8ProfileRange drawing tool — rendered directly on the price panel. It supports four profile types: Volume (total traded volume per price level), Ask/Bid (ask and bid volume stacked or split), Delta (ask minus bid per level), and Delta + Total (both displayed together). The indicator is designed for fast chart load: rather than requiring Tick Replay on the data series, it fires a single asynchronous BarsRequest for the tick data it needs; the chart loads instantly and the profile fills in the background when the request returns.

NT8Profile gives traders a structural view of where the market has spent the most time and volume, with the Point of Control (highest volume price), Value Area High and Low all visible at a glance. The Ask/Bid and Delta modes add order flow context — a profile where the ask side dominates below the POC and the bid side dominates above it is a bullish sign of absorption. The CustomOnly scope paired with an NT8ProfileRange drawing tool lets you draw freehand profile boxes on any range of bars directly on the chart without changing the indicator's global settings.

NT8Profile V3.0

NT8Profile V3.0 is an updated version of NT8Profile with an expanded scope system including an additional ClockMinutes length mode (a fixed rolling window by wall-clock minutes rather than bar count) alongside the existing Sessions and Minutes modes. The core architecture is identical — asynchronous BarsRequest for history, OnMarketData for real-time, compact interleaved ask/bid storage per bar — with the same four profile types (Volume, AskBid, Delta, DeltaAndTotal) and placement options (SessionStart, LeftMargin, RightMargin).

This version also includes refinements to the profile caching and rebuild logic, reducing unnecessary recalculations when only the right edge of the visible range changes during a live session. It is otherwise drop-in compatible with NT8Profile and both can coexist on the same chart if needed. The companion NT8ProfileRange drawing tool works with both versions and allows independent profile type overrides per drawn range box.

NT8ProfileRange

NT8ProfileRange is a drawing tool companion to the NT8Profile indicator. It is a plain resizable rectangle that you draw on the chart to define a custom price and time range; the NT8Profile indicator automatically detects every NT8ProfileRange on the chart and renders a volume profile inside it. The drawing tool itself computes nothing — all profile calculation is handled by the NT8Profile indicator using its existing tick data. Because it inherits NinjaTrader's built-in Rectangle drawing tool, all standard behaviours (move, resize, select, copy, paste, templates) work natively with no extra code.

Each NT8ProfileRange instance has a Profile Type override property that allows it to display a different profile type (Volume, AskBid, Delta, or DeltaAndTotal) from the indicator's global setting, so you can have a Delta profile box and a Volume profile box on the same chart simultaneously. A Clip to price range option restricts the profile bars to the price levels visible within the rectangle's vertical bounds. This tool is the recommended way to quickly add contextual profiles to key structural areas such as prior day ranges, consolidation zones, or specific session windows.

NT8Stats

NT8Stats renders a multi-row per-bar statistics table in a separate panel below the price chart, displaying up to 21 order flow metrics for every bar: Total Volume, Bid Volume, Ask Volume, Delta Volume, Max Delta, Min Delta, Total Trades, Delta Trades, Delta at High/Low, Delta %, Session CVD (Delta Daily), Volume per second, COT High/Low/Bar, Time in seconds, Bar Ratio, High Ratio, Low Ratio, Total Effort, and Delta Effort. Each cell is colour-coded — green for ask-dominant values, red for bid-dominant, neutral for symmetric — giving an immediate visual read of a bar's order flow character without requiring a separate footprint chart.

NT8Stats is a compact, data-dense tool for traders who want quantitative confirmation of what they are seeing in the price action. Delta Effort (delta divided by price range) highlights bars where a large delta produced very little price movement, indicating absorption. Bar Ratio (volume divided by range in ticks) is a normalised measure of activity per tick of price movement. The COT (Change of Trend) rows track when the intra-bar delta changed direction at the high or low, flagging potential exhaustion wicks. Tick Replay must be enabled on the data series for historical bars to be populated accurately.

NT8Trades

NT8Trades classifies and visualises the interaction between aggressive market orders and large resting passive orders using four distinct marker types drawn on the price chart: a Hollow Circle (a cluster built from many small aggressive orders), a Filled Circle (a cluster containing at least one large individual aggressive order), a Square (a cluster that hit a large resting passive order/wall), and a Diamond (a large aggressive order that directly hit a large resting order — a "battle"). This four-way classification goes beyond simple large-trade detection to capture the structural significance of each event: a Square or Diamond implies the presence of a committed passive participant defending a level, which has stronger mean-reversion implications than a Filled Circle alone.

Because NinjaTrader 8 does not provide a public Market-By-Order feed, both sides are reconstructed from available data: aggressive orders are inferred from consecutive same-side prints sharing a timestamp that sweep monotonically in price; passive size is estimated from the peak resting depth observed via OnMarketDepth during a configurable memory window before each hit, and from the volume executed at a single price level within the cluster as a lower bound. Requires Tick Replay for historical markers and Level 2 (DOM) data for real-time Square and Diamond detection from resting depth; on historical bars the executed-at-level estimate is used as a fallback.

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
