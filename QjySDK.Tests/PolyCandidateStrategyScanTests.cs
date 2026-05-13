using Common;
using Model;
using QjySDK.Stg;
using Skender.Stock.Indicators;
using stgInterface;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using static Model.EnumDef;

namespace QjySDK.Tests
{
    public class PolyCandidateStrategyScanTests
    {
        private readonly ITestOutputHelper _output;
        private const string RawSymbol = "COIN_FUTURES_ETHUSDT";
        private const int BarLimit = 60000;
        private const double StakeUsd = 5.0;
        private const double WinFeeUsd = 0.06;
        private const double BarsPerYear5M = 365.0 * 24.0 * 12.0;

        public PolyCandidateStrategyScanTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Run_2026_Candidate_Strategy_Scan()
        {
            Assert.True(TDEngineDataLoader.IsAvailable(), "TDEngine 不可用，无法拉取 2026 全量 5M 数据");
            var quotes = TDEngineDataLoader.LoadKlines(RawSymbol, Period.TIME_5M, BarLimit);
            Assert.NotNull(quotes);
            Assert.True(quotes.Count > 0);

            var outDir = Path.Combine(KlineCache.CacheDirectory, "..");
            Directory.CreateDirectory(outDir);
            var outPath = Path.GetFullPath(Path.Combine(outDir, $"poly_candidate_scan_2026_5m_{DateTime.Now:yyyyMMdd_HHmmss}.csv"));
            using var sw = new StreamWriter(outPath, false);
            sw.WriteLine("strategy,config,month,bars,trades,coverage,win_rate,win_cov_score,fee_ev_per_trade,fee_ev_per_bar,buy_trades,buy_win_rate,sell_trades,sell_win_rate");

            _output.WriteLine($"loaded bars={quotes.Count}, range={quotes[0].Date:yyyy-MM-dd HH:mm}->{quotes[^1].Date:yyyy-MM-dd HH:mm}");
            _output.WriteLine($"csv={outPath}");

            var months = quotes
                .Where(q => q.Date.Year == 2026)
                .GroupBy(q => new DateTime(q.Date.Year, q.Date.Month, 1))
                .OrderBy(g => g.Key)
                .ToList();

            var configs = BuildConfigs();
            foreach (var month in months)
            {
                var monthQuotes = month.OrderBy(q => q.Date).ToList();
                if (monthQuotes.Count < 500)
                    continue;

                foreach (var cfg in configs)
                {
                    var result = cfg.Scan(monthQuotes);
                    WriteScanLine(sw, cfg.Strategy, cfg.Config, month.Key, monthQuotes.Count, result);
                }
                sw.Flush();
            }
        }

        [Fact]
        public void Run_2026_NewCandidates_Monthly_5M()
        {
            Assert.True(TDEngineDataLoader.IsAvailable(), "TDEngine 不可用，无法拉取 2026 全量 5M 数据");
            var quotes = TDEngineDataLoader.LoadKlines(RawSymbol, Period.TIME_5M, BarLimit);
            Assert.NotNull(quotes);
            Assert.True(quotes.Count > 0);

            var outDir = Path.Combine(KlineCache.CacheDirectory, "..");
            Directory.CreateDirectory(outDir);
            var outPath = Path.GetFullPath(Path.Combine(outDir, $"poly_new_candidates_monthly_2026_5m_{DateTime.Now:yyyyMMdd_HHmmss}.csv"));
            using var sw = new StreamWriter(outPath, false);
            sw.WriteLine("strategy,month,bars,trades,coverage,win_rate,binary_pnl,min_capital,annualized_return,binary_sharpe,max_loss_streak,pnl,max_drawdown,sharpe,profit_factor");

            var strategies = new List<(string Name, Func<StgBase> Factory)>
            {
                ("BollRsiShortReversion", () => new BollRsiShortReversion()),
            };

            var months = quotes
                .Where(q => q.Date.Year == 2026)
                .GroupBy(q => new DateTime(q.Date.Year, q.Date.Month, 1))
                .OrderBy(g => g.Key)
                .ToList();

            foreach (var month in months)
            {
                var monthQuotes = month.OrderBy(q => q.Date).ToList();
                if (monthQuotes.Count < 500)
                    continue;

                foreach (var strategy in strategies)
                {
                    var result = RunWithOverrides(strategy.Factory(), monthQuotes, Period.TIME_5M);
                    var coverage = monthQuotes.Count > 0 ? (double)result.Backtest.TradeCount / monthQuotes.Count : 0.0;
                    sw.WriteLine(string.Join(',',
                        Csv(strategy.Name),
                        month.Key.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                        monthQuotes.Count.ToString(CultureInfo.InvariantCulture),
                        result.Backtest.TradeCount.ToString(CultureInfo.InvariantCulture),
                        coverage.ToString("F6", CultureInfo.InvariantCulture),
                        result.Backtest.WinRate.ToString("F4", CultureInfo.InvariantCulture),
                        result.BinaryPnl.ToString("F2", CultureInfo.InvariantCulture),
                        result.MinCapital.ToString("F2", CultureInfo.InvariantCulture),
                        result.AnnualizedReturn.ToString("F4", CultureInfo.InvariantCulture),
                        result.BinarySharpe.ToString("F4", CultureInfo.InvariantCulture),
                        result.MaxLossStreak.ToString(CultureInfo.InvariantCulture),
                        result.Backtest.TotalProfit.ToString("F4", CultureInfo.InvariantCulture),
                        result.Backtest.MaxDrawdown.ToString("F4", CultureInfo.InvariantCulture),
                        result.Backtest.SharpeRatio.ToString("F4", CultureInfo.InvariantCulture),
                        result.Backtest.ProfitFactor.ToString("F4", CultureInfo.InvariantCulture)));
                    sw.Flush();
                    _output.WriteLine($"{strategy.Name,-24} {month.Key:yyyy-MM} trades={result.Backtest.TradeCount,4} cov={coverage,7:P2} win={result.Backtest.WinRate,6:F2}% annual={result.AnnualizedReturn,8:F2}%");
                }
            }
        }

        private static MonthlyResult RunWithOverrides(StgBase stg, List<SkQuote> quotes, Period period)
        {
            var cts = StgTestHelper.InitForTest(stg, "FUTURES_ETHUSDT");
            try
            {
                var argDicProp = typeof(StgBase).GetProperty("ArgDic", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var argDic = (Dictionary<string, object>)argDicProp.GetValue(stg)!;
                argDic["lotsMode"] = 0;
                argDic["lots"] = 1.0m;
                argDic["polyNum"] = 0m;
                argDic["sendMode"] = 0;

                var tu = new TableUnit
                {
                    QuoteList = new List<SkQuote>(),
                    MktSymbol = "FUTURES_ETHUSDT",
                    Period = period
                };

                var trades = new List<RemoteTradeRecord>();
                var binaryStats = new BinaryStats(quotes.Count);
                foreach (var q in quotes)
                {
                    tu.QuoteList.Add(q);
                    try
                    {
                        stg.OnBar(period, tu, true, null);
                    }
                    catch
                    {
                    }
                    var drainedTrades = StgTestHelper.DrainTrades(stg);
                    trades.AddRange(drainedTrades);
                    binaryStats.Process(drainedTrades);
                }

                var calcMethod = typeof(BacktestEngine).GetMethod("CalcResult", BindingFlags.NonPublic | BindingFlags.Static)!;
                var backtest = (BacktestResult)calcMethod.Invoke(null,
                    new object[] { stg.GetType().Name, new[] { "FUTURES_ETHUSDT" }, quotes.Count, trades, period, new Dictionary<string, decimal> { ["FUTURES_ETHUSDT"] = quotes[^1].Close } })!;
                return new MonthlyResult(backtest, binaryStats.Build());
            }
            finally
            {
                cts.Cancel();
            }
        }

        private static List<CandidateConfig> BuildConfigs()
        {
            var configs = new List<CandidateConfig>();

            foreach (var mode in new[] { 0, 1, 2 })
            foreach (var rsiLow in new[] { 20, 25, 30, 35 })
            foreach (var bbStd in new[] { 1.8, 2.0, 2.2, 2.5 })
            foreach (var emaFilter in new[] { 0, 1, 2 })
            {
                var m = mode;
                var low = rsiLow;
                var high = 100 - rsiLow;
                var std = bbStd;
                var filter = emaFilter;
                configs.Add(new CandidateConfig("BollRsi", $"RSI{low}_{high}_BB20_{std:F1}_{FilterName(filter)}_{ModeName(m)}", q => ScanBollRsi(q, 14, low, high, 20, std, filter, m)));
            }

            foreach (var mode in new[] { 0, 1, 2 })
            foreach (var dev in new[] { 0.0015, 0.0020, 0.0030, 0.0040 })
            foreach (var rsiLow in new[] { 25, 30, 35 })
            foreach (var wick in new[] { 0.0, 0.4, 0.6 })
            {
                var m = mode;
                var d = dev;
                var low = rsiLow;
                var high = 100 - rsiLow;
                var w = wick;
                configs.Add(new CandidateConfig("VwapReversion", $"DEV{d:F4}_RSI{low}_{high}_W{w:F1}_{ModeName(m)}", q => ScanVwapReversion(q, d, low, high, w, m)));
            }

            foreach (var mode in new[] { 0, 1, 2 })
            foreach (var emaPeriod in new[] { 20, 50 })
            foreach (var atrK in new[] { 1.0, 1.5, 2.0, 2.5 })
            foreach (var rsiLow in new[] { 25, 30, 35 })
            {
                var m = mode;
                var ep = emaPeriod;
                var k = atrK;
                var low = rsiLow;
                var high = 100 - rsiLow;
                configs.Add(new CandidateConfig("AtrEmaReversion", $"EMA{ep}_ATR{k:F1}_RSI{low}_{high}_{ModeName(m)}", q => ScanAtrEmaReversion(q, ep, 14, k, low, high, m)));
            }

            foreach (var mode in new[] { 0, 1, 2 })
            foreach (var lookback in new[] { 12, 24, 48 })
            foreach (var wick in new[] { 0.4, 0.5, 0.6, 0.7 })
            foreach (var volRatio in new[] { 0.8, 1.0, 1.2, 1.5 })
            {
                var m = mode;
                var lb = lookback;
                var w = wick;
                var vr = volRatio;
                configs.Add(new CandidateConfig("FalseBreakout", $"LB{lb}_W{w:F1}_VOL{vr:F1}_{ModeName(m)}", q => ScanFalseBreakout(q, lb, w, vr, m)));
            }

            foreach (var mode in new[] { 0, 1, 2 })
            foreach (var fast in new[] { 9, 20 })
            foreach (var slow in new[] { 50, 100, 200 })
            foreach (var rsiMin in new[] { 40, 45 })
            foreach (var rsiMax in new[] { 55, 60, 65 })
            {
                var m = mode;
                var f = fast;
                var s = slow;
                var min = rsiMin;
                var max = rsiMax;
                configs.Add(new CandidateConfig("TrendPullback", $"EMA{f}_{s}_RSI{min}_{max}_{ModeName(m)}", q => ScanTrendPullback(q, f, s, min, max, m)));
            }

            foreach (var mode in new[] { 0, 1, 2 })
            foreach (var period in new[] { 2, 3, 5 })
            foreach (var low in new[] { 5, 10, 15, 20 })
            foreach (var emaFilter in new[] { 0, 1, 2 })
            {
                var m = mode;
                var p = period;
                var l = low;
                var high = 100 - low;
                var filter = emaFilter;
                configs.Add(new CandidateConfig("RsiFastReversion", $"RSI{p}_{l}_{high}_{FilterName(filter)}_{ModeName(m)}", q => ScanRsiFastReversion(q, p, l, high, filter, m)));
            }

            return configs;
        }

        private static ScanResult ScanBollRsi(List<SkQuote> quotes, int rsiPeriod, int rsiLow, int rsiHigh, int bbPeriod, double bbStd, int emaFilter, int mode)
        {
            var result = new ScanResult();
            var rsiList = quotes.GetRsi(rsiPeriod).ToList();
            var bollList = quotes.GetBollingerBands(bbPeriod, bbStd).ToList();
            var emaList = quotes.GetEma(200).ToList();
            var minBars = Math.Max(220, Math.Max(rsiPeriod, bbPeriod) + 5);
            for (var i = minBars; i < quotes.Count - 1; i++)
            {
                var q = quotes[i];
                var signal = 0;
                var rsi = rsiList[i].Rsi.GetValueOrDefault(50);
                var boll = bollList[i];
                if (boll.LowerBand.HasValue && boll.UpperBand.HasValue)
                {
                    if ((double)q.Close < boll.LowerBand.Value && rsi <= rsiLow)
                        signal = 1;
                    else if ((double)q.Close > boll.UpperBand.Value && rsi >= rsiHigh)
                        signal = 2;
                }
                signal = ApplyEmaFilter(signal, q, emaList[i].Ema, emaFilter);
                signal = ApplyMode(signal, mode);
                AddNext(result, signal, q, quotes[i + 1]);
            }
            return result;
        }

        private static ScanResult ScanVwapReversion(List<SkQuote> quotes, double dev, int rsiLow, int rsiHigh, double wickMin, int mode)
        {
            var result = new ScanResult();
            var rsiList = quotes.GetRsi(14).ToList();
            var day = DateTime.MinValue.Date;
            var pv = 0m;
            var vv = 0m;
            for (var i = 30; i < quotes.Count - 1; i++)
            {
                var q = quotes[i];
                if (q.Date.Date != day)
                {
                    day = q.Date.Date;
                    pv = 0m;
                    vv = 0m;
                }
                var typical = (q.High + q.Low + q.Close) / 3m;
                pv += typical * q.Volume;
                vv += q.Volume;
                if (vv <= 0)
                    continue;

                var vwap = pv / vv;
                var rsi = rsiList[i].Rsi.GetValueOrDefault(50);
                var range = q.High - q.Low;
                var lowerWick = Math.Min(q.Open, q.Close) - q.Low;
                var upperWick = q.High - Math.Max(q.Open, q.Close);
                var lowerOk = wickMin <= 0 || range > 0 && lowerWick >= range * (decimal)wickMin;
                var upperOk = wickMin <= 0 || range > 0 && upperWick >= range * (decimal)wickMin;
                var signal = 0;
                if ((double)((q.Close - vwap) / vwap) <= -dev && rsi <= rsiLow && lowerOk)
                    signal = 1;
                else if ((double)((q.Close - vwap) / vwap) >= dev && rsi >= rsiHigh && upperOk)
                    signal = 2;
                signal = ApplyMode(signal, mode);
                AddNext(result, signal, q, quotes[i + 1]);
            }
            return result;
        }

        private static ScanResult ScanAtrEmaReversion(List<SkQuote> quotes, int emaPeriod, int atrPeriod, double atrK, int rsiLow, int rsiHigh, int mode)
        {
            var result = new ScanResult();
            var emaList = quotes.GetEma(emaPeriod).ToList();
            var atrList = quotes.GetAtr(atrPeriod).ToList();
            var rsiList = quotes.GetRsi(14).ToList();
            var minBars = Math.Max(emaPeriod, atrPeriod) + 5;
            for (var i = minBars; i < quotes.Count - 1; i++)
            {
                var q = quotes[i];
                var ema = emaList[i].Ema;
                var atr = atrList[i].Atr;
                var signal = 0;
                if (ema.HasValue && atr.HasValue && atr.Value > 0)
                {
                    var rsi = rsiList[i].Rsi.GetValueOrDefault(50);
                    if ((double)q.Close < ema.Value - atr.Value * atrK && rsi <= rsiLow)
                        signal = 1;
                    else if ((double)q.Close > ema.Value + atr.Value * atrK && rsi >= rsiHigh)
                        signal = 2;
                }
                signal = ApplyMode(signal, mode);
                AddNext(result, signal, q, quotes[i + 1]);
            }
            return result;
        }

        private static ScanResult ScanFalseBreakout(List<SkQuote> quotes, int lookback, double wickMin, double volRatioMin, int mode)
        {
            var result = new ScanResult();
            for (var i = Math.Max(lookback + 1, 30); i < quotes.Count - 1; i++)
            {
                var q = quotes[i];
                var prev = quotes.Skip(i - lookback).Take(lookback).ToList();
                var priorHigh = prev.Max(x => x.High);
                var priorLow = prev.Min(x => x.Low);
                var avgVol = prev.Average(x => x.Volume);
                var range = q.High - q.Low;
                var lowerWick = Math.Min(q.Open, q.Close) - q.Low;
                var upperWick = q.High - Math.Max(q.Open, q.Close);
                var volumeOk = avgVol > 0 && q.Volume >= avgVol * (decimal)volRatioMin;
                var signal = 0;
                if (volumeOk && q.Low < priorLow && q.Close > priorLow && range > 0 && lowerWick >= range * (decimal)wickMin)
                    signal = 1;
                else if (volumeOk && q.High > priorHigh && q.Close < priorHigh && range > 0 && upperWick >= range * (decimal)wickMin)
                    signal = 2;
                signal = ApplyMode(signal, mode);
                AddNext(result, signal, q, quotes[i + 1]);
            }
            return result;
        }

        private static ScanResult ScanTrendPullback(List<SkQuote> quotes, int fastPeriod, int slowPeriod, int rsiMin, int rsiMax, int mode)
        {
            var result = new ScanResult();
            var fastList = quotes.GetEma(fastPeriod).ToList();
            var slowList = quotes.GetEma(slowPeriod).ToList();
            var rsiList = quotes.GetRsi(14).ToList();
            for (var i = slowPeriod + 5; i < quotes.Count - 1; i++)
            {
                var q = quotes[i];
                var prev = quotes[i - 1];
                var fast = fastList[i].Ema;
                var fastPrev = fastList[i - 1].Ema;
                var slow = slowList[i].Ema;
                var rsi = rsiList[i].Rsi.GetValueOrDefault(50);
                var signal = 0;
                if (fast.HasValue && fastPrev.HasValue && slow.HasValue)
                {
                    if (fast.Value > slow.Value && (double)prev.Close < fastPrev.Value && (double)q.Close > fast.Value && rsi >= rsiMin && rsi <= rsiMax)
                        signal = 1;
                    else if (fast.Value < slow.Value && (double)prev.Close > fastPrev.Value && (double)q.Close < fast.Value && rsi >= 100 - rsiMax && rsi <= 100 - rsiMin)
                        signal = 2;
                }
                signal = ApplyMode(signal, mode);
                AddNext(result, signal, q, quotes[i + 1]);
            }
            return result;
        }

        private static ScanResult ScanRsiFastReversion(List<SkQuote> quotes, int period, int rsiLow, int rsiHigh, int emaFilter, int mode)
        {
            var result = new ScanResult();
            var rsiList = quotes.GetRsi(period).ToList();
            var emaList = quotes.GetEma(200).ToList();
            for (var i = 220; i < quotes.Count - 1; i++)
            {
                var q = quotes[i];
                var rsi = rsiList[i].Rsi.GetValueOrDefault(50);
                var signal = 0;
                if (rsi <= rsiLow)
                    signal = 1;
                else if (rsi >= rsiHigh)
                    signal = 2;
                signal = ApplyEmaFilter(signal, q, emaList[i].Ema, emaFilter);
                signal = ApplyMode(signal, mode);
                AddNext(result, signal, q, quotes[i + 1]);
            }
            return result;
        }

        private static int ApplyEmaFilter(int signal, SkQuote q, double? ema, int emaFilter)
        {
            if (signal == 0 || emaFilter == 0 || !ema.HasValue)
                return signal;
            if (emaFilter == 1 && signal == 1 && (double)q.Close < ema.Value)
                return 0;
            if (emaFilter == 1 && signal == 2 && (double)q.Close > ema.Value)
                return 0;
            if (emaFilter == 2 && signal == 1 && (double)q.Close > ema.Value)
                return 0;
            if (emaFilter == 2 && signal == 2 && (double)q.Close < ema.Value)
                return 0;
            return signal;
        }

        private static int ApplyMode(int signal, int mode)
        {
            if (mode == 1 && signal == 2)
                signal = 0;
            if (mode == 2 && signal == 1)
                signal = 0;
            return signal;
        }

        private static void AddNext(ScanResult result, int signal, SkQuote q, SkQuote next)
        {
            result.Add(signal, next.Close > q.Close, next.Close < q.Close);
        }

        private static void WriteScanLine(StreamWriter sw, string strategy, string config, DateTime month, int bars, ScanResult result)
        {
            var coverage = bars > 0 ? (double)result.Trades / bars : 0.0;
            var winProbability = result.WinRate / 100.0;
            var winCovScore = result.WinRate * Math.Sqrt(coverage);
            var feeEvPerTrade = winProbability * (StakeUsd - WinFeeUsd) - (1.0 - winProbability) * StakeUsd;
            var feeEvPerBar = feeEvPerTrade * coverage;
            var line = string.Join(',',
                Csv(strategy),
                Csv(config),
                month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                bars.ToString(CultureInfo.InvariantCulture),
                result.Trades.ToString(CultureInfo.InvariantCulture),
                coverage.ToString("F6", CultureInfo.InvariantCulture),
                result.WinRate.ToString("F4", CultureInfo.InvariantCulture),
                winCovScore.ToString("F6", CultureInfo.InvariantCulture),
                feeEvPerTrade.ToString("F6", CultureInfo.InvariantCulture),
                feeEvPerBar.ToString("F6", CultureInfo.InvariantCulture),
                result.BuyTrades.ToString(CultureInfo.InvariantCulture),
                result.BuyWinRate.ToString("F4", CultureInfo.InvariantCulture),
                result.SellTrades.ToString(CultureInfo.InvariantCulture),
                result.SellWinRate.ToString("F4", CultureInfo.InvariantCulture));
            sw.WriteLine(line);
        }

        private static string Csv(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string ModeName(int mode)
        {
            return mode == 1 ? "LONG" : mode == 2 ? "SHORT" : "BOTH";
        }

        private static string FilterName(int filter)
        {
            return filter == 1 ? "TREND200" : filter == 2 ? "COUNTER200" : "NOEMA";
        }

        private sealed class MonthlyResult
        {
            public MonthlyResult(BacktestResult backtest, BinarySummary binary)
            {
                Backtest = backtest;
                BinaryPnl = binary.TotalPnl;
                MinCapital = binary.MinCapital;
                AnnualizedReturn = binary.AnnualizedReturn;
                BinarySharpe = binary.Sharpe;
                MaxLossStreak = binary.MaxLossStreak;
            }

            public BacktestResult Backtest { get; }
            public double BinaryPnl { get; }
            public double MinCapital { get; }
            public double AnnualizedReturn { get; }
            public double BinarySharpe { get; }
            public int MaxLossStreak { get; }
        }

        private sealed class BinarySummary
        {
            public double TotalPnl { get; init; }
            public double MinCapital { get; init; }
            public double AnnualizedReturn { get; init; }
            public double Sharpe { get; init; }
            public int MaxLossStreak { get; init; }
        }

        private sealed class BinaryStats
        {
            private readonly Dictionary<string, List<(OrderType ot, decimal price, decimal num)>> _positions = new Dictionary<string, List<(OrderType ot, decimal price, decimal num)>>();
            private readonly double[] _barPnl;
            private double _equity;
            private double _peakEquity;
            private double _maxDrawdown;
            private int _barIndex;
            private int _currentLossStreak;
            private int _maxLossStreak;

            public BinaryStats(int barCount)
            {
                _barPnl = new double[barCount];
            }

            public void Process(List<RemoteTradeRecord> trades)
            {
                var pnl = 0.0;
                foreach (var t in trades)
                    pnl += ProcessTrade(t);

                if (_barIndex < _barPnl.Length)
                    _barPnl[_barIndex] = pnl;
                _barIndex++;
            }

            public BinarySummary Build()
            {
                var drawdownCapital = _maxDrawdown + StakeUsd;
                var streakCapital = (_maxLossStreak + 1) * StakeUsd;
                var minCapital = Math.Max(drawdownCapital, streakCapital);
                var annualizedReturn = minCapital > 0 ? _equity / minCapital * (BarsPerYear5M / Math.Max(1, _barPnl.Length)) * 100.0 : 0.0;
                var returns = _barPnl.Select(v => v / minCapital).ToList();
                var sharpe = 0.0;
                if (returns.Count > 1)
                {
                    var mean = returns.Average();
                    var variance = returns.Sum(v => Math.Pow(v - mean, 2)) / (returns.Count - 1);
                    var stdDev = Math.Sqrt(variance);
                    sharpe = stdDev > 0 ? mean / stdDev * Math.Sqrt(BarsPerYear5M) : 0.0;
                }

                return new BinarySummary
                {
                    TotalPnl = _equity,
                    MinCapital = minCapital,
                    AnnualizedReturn = annualizedReturn,
                    Sharpe = sharpe,
                    MaxLossStreak = _maxLossStreak
                };
            }

            private double ProcessTrade(RemoteTradeRecord t)
            {
                if (!_positions.ContainsKey(t.MktSymbol))
                    _positions[t.MktSymbol] = new List<(OrderType ot, decimal price, decimal num)>();

                var pnl = 0.0;
                var pos = _positions[t.MktSymbol];
                if (t.OT == OrderType.BUY || t.OT == OrderType.SELL)
                {
                    pos.Add((t.OT, t.Price, t.Num));
                }
                else if (t.OT == OrderType.SELL_TO_COVER && pos.Count > 0)
                {
                    var openTrades = pos.Where(p => p.ot == OrderType.BUY).ToList();
                    var totalNum = openTrades.Sum(p => p.num);
                    if (openTrades.Count > 0 && totalNum > 0)
                    {
                        var avgOpen = openTrades.Sum(p => p.price * p.num) / totalNum;
                        pnl = ApplyClosedPnl((t.Price - avgOpen) * t.Num);
                        pos.RemoveAll(p => p.ot == OrderType.BUY);
                    }
                }
                else if (t.OT == OrderType.BUY_TO_COVER && pos.Count > 0)
                {
                    var openTrades = pos.Where(p => p.ot == OrderType.SELL).ToList();
                    var totalNum = openTrades.Sum(p => p.num);
                    if (openTrades.Count > 0 && totalNum > 0)
                    {
                        var avgOpen = openTrades.Sum(p => p.price * p.num) / totalNum;
                        pnl = ApplyClosedPnl((avgOpen - t.Price) * t.Num);
                        pos.RemoveAll(p => p.ot == OrderType.SELL);
                    }
                }
                return pnl;
            }

            private double ApplyClosedPnl(decimal rawPnl)
            {
                var pnl = rawPnl > 0 ? StakeUsd - WinFeeUsd : -StakeUsd;
                _equity += pnl;
                if (rawPnl > 0)
                {
                    _currentLossStreak = 0;
                }
                else
                {
                    _currentLossStreak++;
                    if (_currentLossStreak > _maxLossStreak)
                        _maxLossStreak = _currentLossStreak;
                }

                if (_equity > _peakEquity)
                    _peakEquity = _equity;
                var drawdown = _peakEquity - _equity;
                if (drawdown > _maxDrawdown)
                    _maxDrawdown = drawdown;
                return pnl;
            }
        }

        private sealed class CandidateConfig
        {
            public CandidateConfig(string strategy, string config, Func<List<SkQuote>, ScanResult> scan)
            {
                Strategy = strategy;
                Config = config;
                Scan = scan;
            }

            public string Strategy { get; }
            public string Config { get; }
            public Func<List<SkQuote>, ScanResult> Scan { get; }
        }

        private sealed class ScanResult
        {
            private int _wins;
            private int _buyWins;
            private int _sellWins;
            public int Trades { get; private set; }
            public int BuyTrades { get; private set; }
            public int SellTrades { get; private set; }
            public double WinRate => Trades > 0 ? 100.0 * _wins / Trades : 0.0;
            public double BuyWinRate => BuyTrades > 0 ? 100.0 * _buyWins / BuyTrades : 0.0;
            public double SellWinRate => SellTrades > 0 ? 100.0 * _sellWins / SellTrades : 0.0;

            public void Add(int signal, bool nextUp, bool nextDown)
            {
                if (signal == 0)
                    return;

                Trades++;
                if (signal == 1)
                {
                    BuyTrades++;
                    if (nextUp)
                    {
                        _wins++;
                        _buyWins++;
                    }
                }
                else if (signal == 2)
                {
                    SellTrades++;
                    if (nextDown)
                    {
                        _wins++;
                        _sellWins++;
                    }
                }
            }
        }
    }
}
