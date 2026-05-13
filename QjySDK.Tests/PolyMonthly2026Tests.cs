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
    public class PolyMonthly2026Tests
    {
        private readonly ITestOutputHelper _output;
        private const string RawSymbol = "COIN_FUTURES_ETHUSDT";
        private const string MktSymbol = "FUTURES_ETHUSDT";
        private const int BarLimit = 60000;
        private const double StakeUsd = 5.0;
        private const double WinFeeUsd = 0.06;
        private const double BarsPerYear5M = 365.0 * 24.0 * 12.0;

        public PolyMonthly2026Tests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Run_2026_Monthly_5M_NonTimesFm()
        {
            Assert.True(TDEngineDataLoader.IsAvailable(), "TDEngine 不可用，无法拉取 2026 全量 5M 数据");
            var quotes = TDEngineDataLoader.LoadKlines(RawSymbol, Period.TIME_5M, BarLimit);
            Assert.NotNull(quotes);
            Assert.True(quotes.Count > 0);

            var outDir = Path.Combine(KlineCache.CacheDirectory, "..");
            Directory.CreateDirectory(outDir);
            var outPath = Path.GetFullPath(Path.Combine(outDir, $"poly_monthly_2026_5m_{DateTime.Now:yyyyMMdd_HHmmss}.csv"));
            using var sw = new StreamWriter(outPath, false);
            sw.WriteLine("strategy,month,bars,start,end,trades,coverage,win_rate,binary_pnl,min_capital,annualized_return,binary_sharpe,max_loss_streak,pnl,max_drawdown,sharpe,profit_factor");

            var strategies = new List<(string Name, Func<StgBase> Factory)>
            {
                ("ExtremeReversal", () => new ExtremeReversal()),
                ("MoEPredict", () => new MoEPredict()),
                ("RsiFastReversion", () => new RsiFastReversion()),
                ("BollRsiShortReversion", () => new BollRsiShortReversion()),
            };

            _output.WriteLine($"loaded bars={quotes.Count}, range={quotes[0].Date:yyyy-MM-dd HH:mm}->{quotes[^1].Date:yyyy-MM-dd HH:mm}");
            _output.WriteLine($"csv={outPath}");

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
                    var stg = strategy.Factory();
                    var result = RunWithOverrides(stg, MktSymbol, monthQuotes, Period.TIME_5M, new Dictionary<string, object>
                    {
                        ["lotsMode"] = 0,
                        ["lots"] = 1.0m,
                        ["polyNum"] = 0m,
                        ["sendMode"] = 0,
                    });

                    var coverage = monthQuotes.Count > 0 ? (double)result.Backtest.TradeCount / monthQuotes.Count : 0.0;
                    var line = string.Join(',',
                        Csv(strategy.Name),
                        month.Key.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                        monthQuotes.Count.ToString(CultureInfo.InvariantCulture),
                        Csv(monthQuotes[0].Date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                        Csv(monthQuotes[^1].Date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
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
                        result.Backtest.ProfitFactor.ToString("F4", CultureInfo.InvariantCulture));
                    sw.WriteLine(line);
                    sw.Flush();

                    _output.WriteLine($"{strategy.Name,-16} {month.Key:yyyy-MM} bars={monthQuotes.Count,5} trades={result.Backtest.TradeCount,4} cov={coverage,7:P2} win={result.Backtest.WinRate,6:F2}% binPnl={result.BinaryPnl,8:F2} minCap={result.MinCapital,7:F2} annual={result.AnnualizedReturn,8:F2}% sharpe={result.BinarySharpe,7:F2} maxL={result.MaxLossStreak,3}");
                }
            }
        }

        [Fact]
        public void Run_2026_WinRate_OptimizationScan()
        {
            Assert.True(TDEngineDataLoader.IsAvailable(), "TDEngine 不可用，无法拉取 2026 全量 5M 数据");
            var quotes = TDEngineDataLoader.LoadKlines(RawSymbol, Period.TIME_5M, BarLimit);
            Assert.NotNull(quotes);
            Assert.True(quotes.Count > 0);

            var outDir = Path.Combine(KlineCache.CacheDirectory, "..");
            Directory.CreateDirectory(outDir);
            var outPath = Path.GetFullPath(Path.Combine(outDir, $"poly_winrate_scan_2026_5m_{DateTime.Now:yyyyMMdd_HHmmss}.csv"));
            using var sw = new StreamWriter(outPath, false);
            sw.WriteLine("strategy,config,month,bars,trades,coverage,win_rate,win_cov_score,fee_ev_per_trade,fee_ev_per_bar,buy_trades,buy_win_rate,sell_trades,sell_win_rate");

            _output.WriteLine($"loaded bars={quotes.Count}, range={quotes[0].Date:yyyy-MM-dd HH:mm}->{quotes[^1].Date:yyyy-MM-dd HH:mm}");
            _output.WriteLine($"csv={outPath}");

            var months = quotes
                .Where(q => q.Date.Year == 2026)
                .GroupBy(q => new DateTime(q.Date.Year, q.Date.Month, 1))
                .OrderBy(g => g.Key)
                .ToList();

            var erConfigs = new List<(int MinConfirm, int RsiLow, int RsiHigh, double VolRatio, int Mode)>();
            foreach (var minConfirm in new[] { 3, 4, 5 })
            foreach (var rsiPair in new[] { (30, 70), (35, 65), (40, 60) })
            foreach (var volRatio in new[] { 1.2, 1.5, 2.0 })
            foreach (var mode in new[] { 0, 1, 2 })
                erConfigs.Add((minConfirm, rsiPair.Item1, rsiPair.Item2, volRatio, mode));

            var moeConfigs = new List<(int Threshold, int Mode, int TrendMode, int TrendPeriod, int AdaptiveMinObs, double AdaptiveMinWinRate)>();
            foreach (var threshold in new[] { 4, 5, 6, 7 })
            foreach (var mode in new[] { 0, 1, 2 })
                moeConfigs.Add((threshold, mode, 0, 20, 0, 0.0));
            foreach (var threshold in new[] { 4, 5 })
            foreach (var mode in new[] { 0, 1, 2 })
            foreach (var trendMode in new[] { 1, 2 })
            foreach (var trendPeriod in new[] { 20, 50, 100 })
                moeConfigs.Add((threshold, mode, trendMode, trendPeriod, 0, 0.0));
            foreach (var mode in new[] { 0, 1, 2 })
            foreach (var minObs in new[] { 20, 40, 80 })
            foreach (var minWr in new[] { 0.54, 0.56, 0.58 })
                moeConfigs.Add((4, mode, 0, 20, minObs, minWr));

            foreach (var month in months)
            {
                var monthQuotes = month.OrderBy(q => q.Date).ToList();
                if (monthQuotes.Count < 500)
                    continue;

                foreach (var cfg in erConfigs)
                {
                    var result = ScanExtremeReversal(monthQuotes, cfg.MinConfirm, cfg.RsiLow, cfg.RsiHigh, cfg.VolRatio, cfg.Mode);
                    var name = $"MC{cfg.MinConfirm}_RSI{cfg.RsiLow}_{cfg.RsiHigh}_VOL{cfg.VolRatio:F1}_{ModeName(cfg.Mode)}";
                    WriteScanLine(sw, "ExtremeReversal", name, month.Key, monthQuotes.Count, result);
                }

                foreach (var cfg in moeConfigs)
                {
                    var result = ScanMoEPredictExtreme(monthQuotes, cfg.Threshold, cfg.Mode, cfg.TrendMode, cfg.TrendPeriod, cfg.AdaptiveMinObs, cfg.AdaptiveMinWinRate);
                    var trendName = cfg.TrendMode == 1 ? $"_TREND{cfg.TrendPeriod}" : cfg.TrendMode == 2 ? $"_COUNTER{cfg.TrendPeriod}" : "";
                    var adaptiveName = cfg.AdaptiveMinObs > 0 ? $"_ADP{cfg.AdaptiveMinObs}_{cfg.AdaptiveMinWinRate:F2}" : "";
                    var name = $"EXT{cfg.Threshold}_{ModeName(cfg.Mode)}{trendName}{adaptiveName}";
                    WriteScanLine(sw, "MoEPredict", name, month.Key, monthQuotes.Count, result);
                }

                sw.Flush();
            }
        }

        private static MonthlyResult RunWithOverrides(StgBase stg, string mktSymbol, List<SkQuote> quotes, Period period, Dictionary<string, object> overrides)
        {
            var cts = StgTestHelper.InitForTest(stg, mktSymbol);
            try
            {
                var argDicProp = typeof(StgBase).GetProperty("ArgDic", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var argDic = (Dictionary<string, object>)argDicProp.GetValue(stg)!;
                foreach (var kv in overrides)
                {
                    if (argDic.ContainsKey(kv.Key))
                        argDic[kv.Key] = kv.Value;
                }

                var tu = new TableUnit
                {
                    QuoteList = new List<SkQuote>(),
                    MktSymbol = mktSymbol,
                    Period = period
                };

                var trades = new List<RemoteTradeRecord>();
                var binary = new BinaryStats(quotes.Count);
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
                    var newTrades = StgTestHelper.DrainTrades(stg);
                    trades.AddRange(newTrades);
                    binary.Process(newTrades, q);
                }

                var calcMethod = typeof(BacktestEngine).GetMethod("CalcResult", BindingFlags.NonPublic | BindingFlags.Static)!;
                var backtest = (BacktestResult)calcMethod.Invoke(null,
                    new object[] { stg.GetType().Name, new[] { mktSymbol }, quotes.Count, trades, period, new Dictionary<string, decimal> { [mktSymbol] = quotes[^1].Close } })!;
                return new MonthlyResult(backtest, binary.Build());
            }
            finally
            {
                cts.Cancel();
            }
        }

        private static string Csv(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
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

        private static string ModeName(int mode)
        {
            return mode == 1 ? "LONG" : mode == 2 ? "SHORT" : "BOTH";
        }

        private static ScanResult ScanExtremeReversal(List<SkQuote> quotes, int minConfirm, int rsiLow, int rsiHigh, double volRatio, int mode)
        {
            var result = new ScanResult();
            var rsiList = quotes.GetRsi(14).ToList();
            var stochList = quotes.GetStoch(14, 3, 3).ToList();
            var bollList = quotes.GetBollingerBands(10, 2).ToList();

            for (var i = 30; i < quotes.Count - 1; i++)
            {
                var q = quotes[i];
                var next = quotes[i + 1];
                var buyScore = 0;
                var sellScore = 0;
                var curBarUp = q.Close > q.Open;

                var consecUp = 0;
                var consecDown = 0;
                for (var ci = i; ci >= 1 && ci >= i - 10; ci--)
                {
                    if (quotes[ci].Close > quotes[ci - 1].Close)
                    {
                        if (consecDown > 0)
                            break;
                        consecUp++;
                    }
                    else if (quotes[ci].Close < quotes[ci - 1].Close)
                    {
                        if (consecUp > 0)
                            break;
                        consecDown++;
                    }
                    else
                    {
                        break;
                    }
                }
                if (consecDown >= 3)
                    buyScore++;
                if (consecUp >= 3)
                    sellScore++;

                var rsi = rsiList[i].Rsi;
                if (rsi.HasValue)
                {
                    if (rsi.Value < rsiLow)
                        buyScore++;
                    if (rsi.Value > rsiHigh)
                        sellScore++;
                }

                var stochK = stochList[i].K;
                if (stochK.HasValue)
                {
                    if (stochK.Value < 20)
                        buyScore++;
                    if (stochK.Value > 80)
                        sellScore++;
                }

                if (i >= 19)
                {
                    var avgVol = 0m;
                    for (var vi = i - 19; vi <= i; vi++)
                        avgVol += quotes[vi].Volume;
                    avgVol /= 20;
                    if (avgVol > 0 && q.Volume > avgVol * (decimal)volRatio)
                    {
                        if (!curBarUp)
                            buyScore++;
                        if (curBarUp)
                            sellScore++;
                    }
                }

                var boll = bollList[i];
                if (boll.LowerBand.HasValue && boll.UpperBand.HasValue)
                {
                    if ((double)q.Close < boll.LowerBand.Value)
                        buyScore++;
                    if ((double)q.Close > boll.UpperBand.Value)
                        sellScore++;
                }

                var signal = 0;
                if (buyScore >= minConfirm && sellScore < minConfirm)
                    signal = 1;
                else if (sellScore >= minConfirm && buyScore < minConfirm)
                    signal = 2;

                if (mode == 1 && signal == 2)
                    signal = 0;
                if (mode == 2 && signal == 1)
                    signal = 0;

                result.Add(signal, next.Close > q.Close, next.Close < q.Close);
            }

            return result;
        }

        private static ScanResult ScanMoEPredictExtreme(List<SkQuote> quotes, int threshold, int mode, int trendMode, int trendPeriod, int adaptiveMinObs, double adaptiveMinWinRate)
        {
            var result = new ScanResult();
            var rsiList = quotes.GetRsi(14).ToList();
            var stochList = quotes.GetStoch(14, 3, 3).ToList();
            var macdList = quotes.GetMacd(12, 26, 9).ToList();
            var bollList = quotes.GetBollingerBands(10, 2).ToList();
            var emaList = quotes.GetEma(trendPeriod).ToList();
            var minBars = Math.Max(135, trendPeriod + 1);
            var adaptiveStats = new Dictionary<int, (int wins, int total)>();

            for (var i = minBars - 1; i < quotes.Count - 1; i++)
            {
                var q = quotes[i];
                var next = quotes[i + 1];
                var volumeRatio = 1.0;
                if (i >= 19)
                {
                    var avgVol = 0m;
                    for (var vi = i - 19; vi <= i; vi++)
                        avgVol += quotes[vi].Volume;
                    avgVol /= 20;
                    if (avgVol > 0)
                        volumeRatio = (double)(q.Volume / avgVol);
                }

                var consecUp = 0;
                var consecDown = 0;
                for (var ci = i; ci >= 1 && ci >= i - 10; ci--)
                {
                    if (quotes[ci].Close > quotes[ci - 1].Close)
                    {
                        if (consecDown > 0)
                            break;
                        consecUp++;
                    }
                    else if (quotes[ci].Close < quotes[ci - 1].Close)
                    {
                        if (consecUp > 0)
                            break;
                        consecDown++;
                    }
                    else
                    {
                        break;
                    }
                }

                var curBarUp = i >= 1 && q.Close > quotes[i - 1].Close;
                var rsi = rsiList[i].Rsi.GetValueOrDefault(50);
                var stochK = stochList[i].K.GetValueOrDefault(50);
                var macdHist = macdList[i].Histogram.GetValueOrDefault(0);
                var macdHistPrev = i >= 1 ? macdList[i - 1].Histogram.GetValueOrDefault(0) : 0;
                var range = q.High - q.Low;
                var lowerWick = Math.Min(q.Open, q.Close) - q.Low;
                var upperWick = q.High - Math.Max(q.Open, q.Close);
                var boll = bollList[i];
                var bsLower = boll.LowerBand.GetValueOrDefault(0);
                var bsUpper = boll.UpperBand.GetValueOrDefault(0);

                var buyExt = 0;
                var sellExt = 0;
                if (rsi < 30)
                    buyExt++;
                if (rsi > 70)
                    sellExt++;
                if (stochK < 20)
                    buyExt++;
                if (stochK > 80)
                    sellExt++;
                if (macdHist > macdHistPrev && macdHist < 0)
                    buyExt++;
                if (macdHist < macdHistPrev && macdHist > 0)
                    sellExt++;
                if (consecDown >= 3)
                    buyExt++;
                if (consecUp >= 3)
                    sellExt++;
                if (range > 0 && lowerWick > range * 0.6m)
                    buyExt++;
                if (range > 0 && upperWick > range * 0.6m)
                    sellExt++;
                if (volumeRatio > 1.5 && !curBarUp)
                    buyExt++;
                if (volumeRatio > 1.5 && curBarUp)
                    sellExt++;
                if (bsUpper - bsLower > 0 && (double)q.Close < bsLower)
                    buyExt++;
                if (bsUpper - bsLower > 0 && (double)q.Close > bsUpper)
                    sellExt++;

                var rawSignal = 0;
                if (buyExt >= threshold)
                    rawSignal = 1;
                else if (sellExt >= threshold)
                    rawSignal = 2;

                var signal = rawSignal;

                if (mode == 1 && signal == 2)
                    signal = 0;
                if (mode == 2 && signal == 1)
                    signal = 0;
                if (signal != 0 && trendMode != 0)
                {
                    var ema = emaList[i].Ema;
                    if (ema.HasValue)
                    {
                        if (trendMode == 1 && signal == 1 && (double)q.Close < ema.Value)
                            signal = 0;
                        if (trendMode == 1 && signal == 2 && (double)q.Close > ema.Value)
                            signal = 0;
                        if (trendMode == 2 && signal == 1 && (double)q.Close > ema.Value)
                            signal = 0;
                        if (trendMode == 2 && signal == 2 && (double)q.Close < ema.Value)
                            signal = 0;
                    }
                }

                if (signal != 0 && adaptiveMinObs > 0)
                {
                    if (adaptiveStats.TryGetValue(signal, out var stat) && stat.total >= adaptiveMinObs)
                    {
                        var winRate = stat.total > 0 ? (double)stat.wins / stat.total : 0.0;
                        if (winRate < adaptiveMinWinRate)
                            signal = 0;
                    }
                }

                var nextUp = next.Close > q.Close;
                var nextDown = next.Close < q.Close;
                result.Add(signal, nextUp, nextDown);

                if (rawSignal != 0 && adaptiveMinObs > 0)
                {
                    adaptiveStats.TryGetValue(rawSignal, out var stat);
                    var win = rawSignal == 1 ? nextUp : nextDown;
                    adaptiveStats[rawSignal] = (stat.wins + (win ? 1 : 0), stat.total + 1);
                }
            }

            return result;
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

            public void Process(List<RemoteTradeRecord> trades, SkQuote quote)
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
    }
}
