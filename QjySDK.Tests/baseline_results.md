# ChanLun Baseline & Optimization Results

## Applied Optimizations (Round 3 + V3 Fixes)

### Round 3 (original):
1. **Relaxed trend detection**: HasDownTrend/HasUpTrend 2-stage check
2. **Consolidation divergence (盘整背驰)**: IdentifyConsolidationBuy1/Sell1 fallback
3. **BSPoint priority weighting**: Buy1/Sell1 > Buy2/Sell2 > Buy3/Sell3
4. **Relaxed maxZhongShuDeviation**: 5% → 10% in ChanLun.cs
5. **Trailing stop loss**: Activate after 3% profit, trail at 2% from peak
6. **Signal expiry**: Clear stale Buy1/Sell1 after 50 bars
7. **ZhongShu reentry exit**: Close position when price returns inside entry ZhongShu

### V3 Fixes (regression repair):
1. **ZhongShu exit restricted to Buy3/Sell3 only** — Buy1/Sell1 traverse through ZhongShu as profit direction
2. **Trailing stop widened**: 3%/2% → 5%/3% to tolerate normal retracements
3. **Signal expiry: no longer clears LastBuy1/LastSell1** — only skips stale Buy2/Sell2 in selection loop
4. **EntryBSPointType tracking** — differentiates exit logic by entry signal type

---

## PRE-OPTIMIZATION BASELINE — ChanLun (Segment Level)

| Symbol | Period | Bars | Trades | WinRate | Profit | MaxDD | Sharpe |
|--------|--------|------|--------|---------|--------|-------|--------|
| BTCUSDT | 15M | 8640 | 33 | 45.5% | +1840.13 | 650.59 | 2.02 |
| ETHUSDT | 15M | 8640 | 14 | 28.6% | -94.27 | 2975.58 | -0.10 |
| XAUUSDT | 15M | 8640 | 25 | 48.0% | +875.50 | 1862.90 | 1.30 |
| SZSE.000001 | 15M | 6080 | 9 | 55.6% | +1.08 | 4.72 | 0.81 |
| SHSE.510300 | 15M | 6096 | 16 | 31.2% | -3.34 | 12.89 | -0.67 |

## PRE-OPTIMIZATION BASELINE — ChanLunBi (Stroke Level)

| Symbol | Period | Bars | Trades | WinRate | Profit | MaxDD | Sharpe |
|--------|--------|------|--------|---------|--------|-------|--------|
| BTCUSDT | 1D | 365 | 3 | 0% | -1978.46 | 1978.46 | -59.53 |
| BTCUSDT | 15M | 8640 | 35 | 48.6% | +3690.39 | 1915.05 | 3.54 |
| ETHUSDT | 1D | 365 | 1 | 0% | -1000.93 | 1000.93 | 0 |
| ETHUSDT | 15M | 8640 | 45 | 37.8% | -269.49 | 3647.01 | -0.18 |
| XAUUSDT | 15M | 8640 | 42 | 38.1% | +1941.38 | 987.66 | 2.30 |
| SZSE.000001 | 1D | 365 | 2 | 0% | -10.89 | 10.89 | -194.03 |
| SZSE.000001 | 15M | 6080 | 17 | 29.4% | -30.17 | 47.58 | -4.47 |
| SHSE.510300 | 15M | 6096 | 30 | 33.3% | -27.64 | 32.87 | -3.10 |

---

## V3 OPTIMIZATION — ChanLun (Segment Level)

| Symbol | Period | Bars | Trades | WinRate | Profit | MaxDD | Sharpe |
|--------|--------|------|--------|---------|--------|-------|--------|
| BTCUSDT | 15M | 8640 | 26 | 50.0% | +3379.85 | 1111.77 | 4.52 |
| ETHUSDT | 15M | 8640 | 18 | 33.3% | -1527.88 | 2535.00 | -3.11 |
| XAUUSDT | 15M | 8640 | 25 | 56.0% | +2218.79 | 1413.38 | 3.76 |
| SZSE.000001 | 15M | 6080 | 5 | 60.0% | +9.84 | 4.72 | 7.46 |
| SHSE.510300 | 15M | 6096 | 8 | 62.5% | +10.50 | 10.09 | 4.02 |

## V3 OPTIMIZATION — ChanLunBi (Stroke Level)

| Symbol | Period | Bars | Trades | WinRate | Profit | MaxDD | Sharpe |
|--------|--------|------|--------|---------|--------|-------|--------|
| BTCUSDT | 1D | 365 | 4 | 50.0% | -1047.94 | 1353.20 | -8.26 |
| BTCUSDT | 15M | 8640 | 54 | 38.9% | +464.44 | 2052.00 | 0.48 |
| ETHUSDT | 1D | 365 | 5 | 80.0% | +1904.50 | 827.45 | 7.71 |
| ETHUSDT | 15M | 8640 | 53 | 49.1% | +5287.11 | 2104.46 | 3.77 |
| XAUUSDT | 15M | 8640 | 58 | 37.9% | +1403.56 | 1768.77 | 1.84 |
| SZSE.000001 | 1D | 365 | 2 | 0% | -7.43 | 7.43 | -45.08 |
| SZSE.000001 | 15M | 6080 | 26 | 38.5% | -22.52 | 30.67 | -2.94 |
| SHSE.510300 | 1D | 365 | 1 | 100% | +2.35 | 0 | 0 |
| SHSE.510300 | 15M | 6096 | 37 | 35.1% | -22.88 | 30.76 | -2.37 |

---

## V3 vs Baseline Comparison (15M)

### ChanLun (Segment Level) — Δ Change

| Symbol | Trades | WinRate | Profit | MaxDD | Sharpe |
|--------|--------|---------|--------|-------|--------|
| BTCUSDT | 33→26 | 45.5→50.0% (+4.5) | +1840→**+3380** (**+1540**) | 651→1112 (+461) | 2.02→**4.52** (**+2.50**) |
| ETHUSDT | 14→18 | 28.6→33.3% (+4.7) | -94→-1528 (**-1434 ❌**) | 2976→2535 (-441) | -0.10→-3.11 (-3.01) |
| XAUUSDT | 25→25 | 48.0→56.0% (**+8.0**) | +876→**+2219** (**+1343**) | 1863→1413 (-450) | 1.30→**3.76** (**+2.46**) |
| SZSE.000001 | 9→5 | 55.6→60.0% (+4.4) | +1→**+10** (**+9**) | 4.7→4.7 (0) | 0.81→**7.46** (**+6.65**) |
| SHSE.510300 | 16→8 | 31.2→**62.5%** (**+31.3**) | -3→**+11** (**+14**) | 13→10 (-3) | -0.67→**4.02** (**+4.69**) |

### ChanLunBi (Stroke Level) — Δ Change

| Symbol | Trades | WinRate | Profit | MaxDD | Sharpe |
|--------|--------|---------|--------|-------|--------|
| BTCUSDT 15M | 35→54 | 48.6→38.9% (-9.7) | +3690→+464 (**-3226 ❌**) | 1915→2052 (+137) | 3.54→0.48 (-3.06) |
| BTCUSDT 1D | 3→4 | 0→50% (+50) | -1978→-1048 (**+930**) | 1978→1353 (-625) | -59.5→-8.3 (+51.2) |
| ETHUSDT 15M | 45→53 | 37.8→**49.1%** (**+11.3**) | -269→**+5287** (**+5556**) | 3647→2104 (**-1543**) | -0.18→**3.77** (**+3.95**) |
| ETHUSDT 1D | 1→5 | 0→**80%** | -1001→**+1905** (**+2906**) | 1001→827 (-174) | 0→**7.71** |
| XAUUSDT 15M | 42→58 | 38.1→37.9% (-0.2) | +1941→+1404 (**-537 ❌**) | 988→1769 (+781) | 2.30→1.84 (-0.46) |
| SZSE.000001 15M | 17→26 | 29.4→38.5% (+9.1) | -30→-23 (**+7**) | 48→31 (**-17**) | -4.47→-2.94 (**+1.53**) |
| SHSE.510300 15M | 30→37 | 33.3→35.1% (+1.8) | -28→-23 (**+5**) | 33→31 (**-2**) | -3.10→-2.37 (**+0.73**) |

---

## Key Findings (V3)

### Major Improvements (vs Baseline)
- **ChanLun BTCUSDT 15M**: +1840→+3380 (+84%), Sharpe 2.02→4.52 ✅ (was regressed in V1)
- **ChanLun XAUUSDT 15M**: +876→+2219 (+153%), Sharpe 1.30→3.76 ✅
- **ChanLun SHSE.510300 15M**: Loss→Profit (-3→+11), WinRate 31%→63%, Sharpe -0.67→4.02 ✅
- **ChanLun SZSE.000001 15M**: +1→+10, Sharpe 0.81→7.46 ✅
- **ChanLunBi ETHUSDT 15M**: Loss→Profit (-269→+5287), MaxDD 3647→2104, Sharpe -0.18→3.77 ✅
- **ChanLunBi ETHUSDT 1D**: Loss→Profit (-1001→+1905), WinRate 0%→80% ✅
- **ChanLunBi BTCUSDT 1D**: -1978→-1048, MaxDD 1978→1353 ✅ (improved but still negative)

### Remaining Regressions
- **ChanLun ETHUSDT 15M**: -94→-1528 (more trades, but MaxDD improved 2976→2535)
- **ChanLunBi BTCUSDT 15M**: +3690→+464 (54 trades vs 35, too many low-quality signals)
- **ChanLunBi XAUUSDT 15M**: +1941→+1404 (58 trades vs 42, similar overtrading issue)

### Net Assessment (V3)
- **ChanLun total 15M profit**: +2620 (baseline) → +4091 (**+56%**, excl ETHUSDT: +2716→+5619)
- **ChanLunBi total 15M profit**: +5305 (baseline) → +7109 (**+34%**)
- V3 fixed the ChanLun BTCUSDT regression from V1 (+997→+3380)
- V3 ZhongShu exit fix (Buy3/Sell3 only) preserved profitable Buy1/Sell1 traversal trades
- Wider trailing stop (5%/3%) better accommodates different asset volatilities
- Remaining regressions are caused by consolidation divergence generating extra signals in certain regimes
