# Orderflow SnR — NT8 Indicator Design & Pseudocode

## 0. Current rule (latest — overrides anything below)

- Each closed bar draws a band from its VAL to its VAH. An up close makes it support (green); a down close makes it resistance (red).
- A **touch does not remove** a band. It only raises the touch signal or alert once.
- A band is removed only when a bar **closes beyond its opposite side**: support when Close < VAL, resistance when Close > VAH.
- Active bands extend **Minimum Length (4) bars into the future** past the current bar, capped at the chart's right-hand margin.
- The smoothed EMA 20 is drawn for visual confluence only. It does **not** filter bands.

## 1. What the sources actually say

**KISS Order Flow /setups**: the page says nothing about how the bands are built. It only states:
- From v2.1.10 the support (green) and resistance (red) bands are drawn automatically.
- The setup: price touches a band, then the 1st or 2nd bar after the touch closes in the opposite direction → enter.
- Bands are meant as context for the KISS reversal signals.

**DeepCharts "Deep-M Effort (NQ)"** (help center + 3rd-party review): a closed-source bias model, tuned for a 40-Range NQ chart.
- Green zones = path of least resistance up; purple = down.
- It has a built-in MA (two colours, above/below) and alerts on a change of bias.
- It is based on effort vs result (Wyckoff/VSA). The core logic and parameters are not published.
- So M-Effort here is **our own reconstruction** of the concept, not a clone.

## 2. Reverse-engineering the bands from the screenshot

| Observation | Inferred rule |
|---|---|
| Each bar is drawn as a single-bar volume profile with a POC tick (cyan) and a coloured VA strip (blue = +delta, magenta = −delta) | Per-bar profile → POC, VAH, VAL (70% VA) |
| Green band 26934–26945 starts at the 3:31 bar and spans that bar's VA, not its full range (26922–26947) | Band height = **the origin bar's value area (VAL→VAH)** |
| The band extends right while later bars stay above it, and stops at the 3:50 bar that closes back into or under it | Band **extends until broken**; broken = a close through the far edge |
| Red band 27012–27033 starts at the 3:37 swing-high bar ("Traders") and extends right while all later highs stay under it | Resistance = VA of a bar that price then leaves **downward** |
| The 3:48 and 3:50 red bands still extend to the last bar; they don't run to the chart edge | Active band → right edge = current bar (unless "Draw to edge of chart") |
| No band from most bars | A candidate shows only after it survives **Minimum Length = 4 bars** untouched |
| Settings show "Opacity % (Broken Band)" | Broken bands stay visible, frozen at the break bar, with their own opacity |
| "Draw S/R Band for = EveryValueArea" | Other modes probably filter by origin: reversal signal bars, POC-only, etc. |

**Core idea (confirmed rule)**: every completed bar draws a band from its **VAL to its VAH**.
- **Colour comes from the bar's close.** An up close (Close > Open) gives a green support band. A down close gives a red resistance band.
- **If the next bar doesn't touch the VAL–VAH area**, the band stays and extends right, bar by bar.
- **The first later bar that touches the area ends the band, and it disappears.** "Touch" means the bar's range overlaps [VAL, VAH].
- **Minimum Length** is how far right an active band is drawn: at least N bars from its origin bar, into the right margin if needed. It is not a survival filter.
- **The "Broken Band" opacity** lets a touched band optionally stay visible, ending at the touch bar. The default is to remove it.

> The table above was the first inference. Where it differs (for example "side from the exit direction" or "break on close"), the rule in this box and §5 wins.

## 3. Parameters

```
// Band
bool   ShowBands            = true
enum   BandMode             = EveryValueArea | SignalBarsOnly | HighVolumeBars | PocOnly
int    MinLengthBars        = 4
bool   ExtendToChartEdge    = false
double ValueAreaPct         = 0.70
enum   ColorRule            = BarDirection | CloseVsPoc   // default BarDirection (Close>Open → support)
enum   TouchRule            = RangeOverlap | WickIntoNearEdge   // default RangeOverlap: Low<=VAH && High>=VAL
int    MaxActiveBands       = 50
bool   MergeOverlapping     = true
double MinVolumePctile      = 0     // HighVolumeBars mode: origin bar volume >= Nth pctile of last 50
Brush  SupportColor, ResistanceColor
int    Opacity, BrokenOpacity
bool   ShowBrokenBands      = false   // KISS default: touched band disappears

// Effort (M-Effort style, lower pane)
int    EffortMaLength       = 14
enum   EffortSource         = Volume | AbsDelta
int    BiasLookback         = 20
bool   UseBiasFilter        = false   // only draw support in bull bias / resistance in bear bias
bool   AlertOnTouch, AlertOnBiasFlip
```

## 4. Data layer — single-bar profile (works on any chart with Tick Replay, like KISS)

```
class BarProfile {
    SortedDictionary<double, (long bidVol, long askVol)> levels
    long   totalVol, delta
    double poc, vah, val
}

OnStateChange:
  SetDefaults → defaults above, IsOverlay = true, Calculate = OnEachTick
  Configure   → AddPlot(effort histogram in a separate pane, or put the effort part in a 2nd companion indicator)
  DataLoaded  → profiles = new Dictionary<int, BarProfile>()
  // Require Tick Replay; show a warning if !Bars.IsTickReplay

OnMarketData(e):
  if e.MarketDataType != Last return
  p = profiles[CurrentBar] (create if missing)
  price = Instrument.MasterInstrument.RoundToTickSize(e.Price)
  if e.Price >= e.Ask   → p.levels[price].askVol += e.Volume ; p.delta += e.Volume
  elif e.Price <= e.Bid → p.levels[price].bidVol += e.Volume ; p.delta -= e.Volume
  else                  → split or assign to nearer side
  p.totalVol += e.Volume

ComputeValueArea(p):
  p.poc = level with max(bid+ask)      // tie → closest to bar mid
  target = p.totalVol * ValueAreaPct
  up = down = index(poc); acc = vol(poc)
  while acc < target:
      volUp   = vol(up+1) + vol(up+2)
      volDown = vol(down-1) + vol(down-2)
      if volUp >= volDown: up += 2 ; acc += volUp   else: down -= 2 ; acc += volDown
      (clamp to the bar's high/low)
  p.vah = price(up) ; p.val = price(down)
```
*Alternative:* if the chart uses Volumetric bars, read
`((VolumetricBarsType)Bars.BarsSeries.BarsType).Volumes[idx]` → `GetBidVolumeForPrice` /
`GetAskVolumeForPrice` / `GetMaximumVolume` instead of building it yourself.

## 5. Band engine (confirmed KISS rule)

```
class Band {
    int    originBar
    int    endBar = -1                    // -1 = still active
    double vah, val, poc
    enum   Side  { Support, Resistance }  // from the origin bar's close
    enum   State { Pending, Active, Touched }
    int    barsAlive                      // bars since origin, untouched
}

OnBarUpdate:
  if !IsFirstTickOfBar return            // evaluate completed bars only
  c   = CurrentBar - 1                   // bar that just closed
  ago = CurrentBar - c                   // = 1
  p   = profiles[c] ; ComputeValueArea(p)

  // 1) update every live band against bar c (BEFORE adding bar c's own band)
  foreach b in bands where b.State != Touched:
      hi = High[ago] ; lo = Low[ago]
      touched = (TouchRule == RangeOverlap)
                  ? (lo <= b.vah && hi >= b.val)                 // any overlap with VAL..VAH
                  : (b.Side==Support ? lo <= b.vah : hi >= b.val) // wick reaches near edge
      if touched:
          b.State  = Touched
          b.endBar = c
          if b.barsAlive >= MinLengthBars - 1: FireTouch(b, c)   // only real (visible) bands trigger setups
          if !ShowBrokenBands or b.barsAlive < MinLengthBars - 1: remove b   // disappears
      else:
          b.barsAlive++
          if b.State == Pending and b.barsAlive >= MinLengthBars - 1: b.State = Active  // origin bar counts as 1

  // 2) every closed bar spawns a band from VAL to VAH, coloured by its close
  if PassesModeFilter(c, p):
      side = (ColorRule == BarDirection)
               ? (Close[ago] >= Open[ago] ? Support : Resistance)
               : (Close[ago] >= p.poc     ? Support : Resistance)
      bands.Add(new Band{ originBar=c, vah=p.vah, val=p.val, poc=p.poc,
                          Side=side, State=Pending, barsAlive=0 })
      // the very next bar decides first survival: if it overlaps VAL..VAH → removed in step 1

  // 3) housekeeping
  cap Active bands to MaxActiveBands (drop oldest)
  (optional) MergeOverlapping for same-side Active bands
  if UseBiasFilter: hide Support while bias==Bear, Resistance while bias==Bull

PassesModeFilter(c, p):
  EveryValueArea  → true
  SignalBarsOnly  → reversalSignal[c] (see §7)
  HighVolumeBars  → p.totalVol >= percentile(vol, last 50 bars, MinVolumePctile)
  PocOnly         → true, but vah = val = poc (± half tick for drawing)
```

Only `State == Active` bands are drawn (plus `Touched` if `ShowBrokenBands`). A band that is `Pending` is still tracked, but it shows only once it reaches MinLengthBars.

**Walkthrough, using the screenshot:**
- 3:31 bar closes up → green band at its VAL–VAH (≈26934–26945).
- The 3:33…3:48 bars all have lows above 26945, so nothing touches it. It turns Active after 4 bars and keeps extending.
- The 3:50 bar trades down into 26945 → touched → the band ends at 3:50 (shown with Broken opacity, or removed by default).
- The 3:37 "Traders" bar closes down → red band at its VA (≈27012–27033). No later bar reaches 27012, so it is still extending.
- Most other bars get a band, but the next bar overlaps it straight away, so it's removed before it's ever shown.

## 6. Rendering (SharpDX in OnRender — don't create one Draw.Rectangle per band)

```
OnRender(chartControl, chartScale):
  foreach b in bands where State == Active or (State == Touched and ShowBrokenBands):
      x1 = chartControl.GetXByBarIndex(ChartBars, b.originBar) - barHalfWidth
      x2 = b.State==Touched ? GetXByBarIndex(b.endBar) + barHalfWidth
         : ExtendToChartEdge ? ChartPanel.X + ChartPanel.W
         : GetXByBarIndex(CurrentBar) + barHalfWidth
      if x2 < ChartPanel.X or x1 > ChartPanel.X+ChartPanel.W continue   // off-screen
      y1 = chartScale.GetYByValue(b.vah) ; y2 = chartScale.GetYByValue(b.val)
      brush = Side==Support ? supportDx : resistanceDx
      brush.Opacity = State==Touched ? BrokenOpacity : Opacity
      RenderTarget.FillRectangle(rect, brush)
      RenderTarget.DrawRectangle(rect, borderBrush, 1)     // thin border like the screenshot
  // Create SharpDX brushes in OnRenderTargetChanged and dispose them there
```

## 7. M-Effort pane (effort vs result — our version of the Deep-M Effort idea)

The lower pane in the screenshot looks like this: green and red vertical bars, a yellow MA, dots on the zero line, labels 0–500. That fits an effort histogram with an average.

```
effort[i] = EffortSource==Volume ? Volume[i] : |delta[i]|
result[i] = (High[i]-Low[i]) / TickSize                        // or |Close-Open| in ticks
effMA     = SMA(effort, EffortMaLength)
resMA     = SMA(result, EffortMaLength)
effRatio  = effort / effMA ; resRatio = result / resMA

Plot histogram = effort * sign(delta)  (or abs for the screenshot style), MA line = effMA (yellow)
Colour per bar:
  delta>0 & effRatio>1 & resRatio>=1  → bright green  (effort produced result: genuine buying)
  delta>0 & effRatio>1 & resRatio<1   → dark green    (buying absorbed → potential resistance)
  delta<0 & effRatio>1 & resRatio>=1  → bright red
  delta<0 & effRatio>1 & resRatio<1   → dark red      (selling absorbed → potential support)
  effRatio<=1                          → dot only on zero line (low effort)

Bias (Deep-M style zones, optional background shading):
  cumEff = EMA(delta * resRatio, BiasLookback)      // effort weighted by how much it moved price
  bias   = cumEff > 0 and Close > EMA(Close, BiasLookback) ? Bull
         : cumEff < 0 and Close < EMA(Close, BiasLookback) ? Bear : Neutral
  on bias change → alert + start new shaded zone (green / purple)

Reversal signal (feeds SignalBarsOnly mode):
  bullish: bar makes a new 5-bar low AND absorbed selling (dark red) AND close in upper 50% of range
  bearish: bar makes a new 5-bar high AND absorbed buying (dark green) AND close in lower 50%
```

## 8. Trade setup / alerts (KISS rule on top of the bands)

```
FireTouch(b, c): mark touchBar = c; watch the next 2 bars
On each of those bars:
  Support:    Close > Open  (and optionally effort colour == dark red at touch) → Long signal, Draw.ArrowUp
  Resistance: Close < Open  (optionally dark green at touch)                    → Short signal
  Stop: beyond the far edge of the band + 1-2 ticks ; Target: next opposite band or 2R
Expose Series<int> Signal (+1/-1/0) so a strategy can consume it.
```

## 9. Implementation notes / pitfalls

- **Tick Replay required** for historical OnMarketData. Without it, fall back to volumetric bars or a 1-tick added series (`AddDataSeries(BarsPeriodType.Tick, 1)`) and classify trades with the bid/ask series.
- Make sure the profile is finished before you compute VA. Do it on `IsFirstTickOfBar` for the *previous* bar. Live, you can also preview the current bar's VA without making it a candidate.
- Memory: keep `profiles` only for the last ~MaxLookback bars. Bands store their own prices.
- A band ends on the **first touch** of VAL..VAH (range overlap), not on a close through. Test it on the bar that just closed, before you add that bar's own band.
- Merge overlapping bands, or EveryValueArea will stack dozens of near-identical rectangles during a trend.
- Range charts (40R NQ like Deep-M) give the most consistent VA sizes. Time bars work too.
- Optional extras: label the origin bar (the screenshot shows "Traders"/"Bulldozer" tags, which are probably KISS signal names), touch counters, and fading bands by age.
