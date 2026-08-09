using Common;
using Model;
using QjySDK.Stg;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;
using static Model.EnumDef;

namespace QjySDK.Tests
{
    public class ReversalTurtleOptimizationProbeTests
    {
        [Fact]
        [Trait("Category", "RealData")]
        public void WriteBaselineMetrics()
        {
            var symbols = new (string raw, string market)[]
            {
                ("COIN_FUTURES_ETHUSDT", "FUTURES_ETHUSDT"),
                ("COIN_FUTURES_LTCUSDT", "FUTURES_LTCUSDT"),
                ("STOCK_SPOT_SHSE.600989", "SPOT_SHSE.600989"),
                ("STOCK_SPOT_SHSE.510300", "SPOT_SHSE.510300"),
                ("STOCK_SPOT_SHSE.518880", "SPOT_SHSE.518880"),
                ("STOCK_SPOT_SZSE.159915", "SPOT_SZSE.159915"),
            };
            var periods = new (Period period, int limit)[]
            {
                (Period.TIME_1D, 1500),
                (Period.TIME_15M, 3000),
            };
            string output = Path.Combine(Directory.GetCurrentDirectory(), ".burstcode", "reversal-probe.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var writer = new StreamWriter(output, false);
            writer.WriteLine("symbol,period,bars,trades,winRate,profit,annualized,drawdownRate,pf,sharpe");
            foreach (var symbol in symbols)
            {
                foreach (var p in periods)
                {
                    var quotes = TDEngineDataLoader.LoadKlines(symbol.raw, p.period, p.limit);
                    if (quotes.Count < 100) continue;
                    var result = BacktestEngine.RunSingleSymbol(new TrendAwareReversalTurtleTrading(), symbol.market, quotes, p.period);
                    writer.WriteLine(string.Join(",",
                        symbol.raw, p.period, quotes.Count, result.TradeCount,
                        result.WinRate.ToString("F2", CultureInfo.InvariantCulture),
                        result.TotalProfit.ToString("F2", CultureInfo.InvariantCulture),
                        result.AnnualizedReturn.ToString("F2", CultureInfo.InvariantCulture),
                        result.MaxDrawdownRate.ToString("F2", CultureInfo.InvariantCulture),
                        result.ProfitFactor.ToString("F3", CultureInfo.InvariantCulture),
                        result.SharpeRatio.ToString("F4", CultureInfo.InvariantCulture)));
                }
            }
        }

        [Fact]
        [Trait("Category", "RealData")]
        public void WriteLocalStrategyPortfolioMetrics()
        {
            string[] symbols =
            {
                "AMD", "BABA", "BZ", "CL", "COIN", "DIS", "DRAM", "ETH",
                "GOOGL", "HOOD", "INTC", "LTC", "META", "MSFT", "MU", "NATGAS",
                "NVDA", "QQQ", "SPY", "TSLA", "USAR", "V", "WMT", "XAU",
            };
            string output = Path.Combine(Directory.GetCurrentDirectory(), ".burstcode", "reversal-local-portfolio.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var writer = new StreamWriter(output, false);
            writer.WriteLine("symbol,bars,trades,winRate,profit,annualized,drawdownRate,pf,sharpe");
            double annualized = 0;
            double drawdown = 0;
            decimal profit = 0;
            int trades = 0;
            int dataSets = 0;
            foreach (string symbol in symbols)
            {
                string raw = "COIN_FUTURES_" + symbol + "USDT";
                string market = "FUTURES_" + symbol + "USDT";
                List<SkQuote> quotes;
                try { quotes = TDEngineDataLoader.LoadKlines(raw, Period.TIME_1D, 1500); }
                catch { continue; }
                if (quotes.Count < 100) continue;
                var result = BacktestEngine.RunSingleSymbol(new TrendAwareReversalTurtleTrading(), market, quotes, Period.TIME_1D);
                writer.WriteLine(string.Join(",", raw, quotes.Count, result.TradeCount,
                    result.WinRate.ToString("F2", CultureInfo.InvariantCulture),
                    result.TotalProfit.ToString("F2", CultureInfo.InvariantCulture),
                    result.AnnualizedReturn.ToString("F2", CultureInfo.InvariantCulture),
                    result.MaxDrawdownRate.ToString("F2", CultureInfo.InvariantCulture),
                    result.ProfitFactor.ToString("F3", CultureInfo.InvariantCulture),
                    result.SharpeRatio.ToString("F4", CultureInfo.InvariantCulture)));
                annualized += result.AnnualizedReturn;
                drawdown += result.MaxDrawdownRate;
                profit += result.TotalProfit;
                trades += result.TradeCount;
                dataSets++;
            }
            writer.WriteLine(string.Join(",", "PORTFOLIO_AVG", dataSets, trades, "-",
                profit.ToString("F2", CultureInfo.InvariantCulture),
                (annualized / Math.Max(1, dataSets)).ToString("F2", CultureInfo.InvariantCulture),
                (drawdown / Math.Max(1, dataSets)).ToString("F2", CultureInfo.InvariantCulture), "-", "-"));

            var portfolioQuotes = new Dictionary<string, List<SkQuote>>();
            foreach (string symbol in symbols)
            {
                try
                {
                    var quotes = TDEngineDataLoader.LoadKlines("COIN_FUTURES_" + symbol + "USDT", Period.TIME_1D, 1500);
                    if (quotes.Count >= 100) portfolioQuotes["FUTURES_" + symbol + "USDT"] = quotes;
                }
                catch { }
            }
            var portfolioArgs = new Dictionary<string, object>
            {
                ["trendRecentPeriod"] = 14,
                ["trendGlobalPeriod"] = 120,
                ["trendSlopeThreshold"] = 0.12m,
                ["lookbackPeriod"] = 10,
                ["exitLookbackPeriod"] = 20,
                ["stopATR"] = 3m,
            };
            var portfolioStrategy = new TrendAwareReversalTurtleTrading();
            var portfolio = BacktestEngine.RunMultiSymbol(portfolioStrategy, portfolioQuotes, portfolioArgs);
            var firstHalfQuotes = portfolioQuotes.ToDictionary(kv => kv.Key,
                kv => kv.Value.Take(Math.Max(100, kv.Value.Count / 2)).ToList());
            var secondHalfQuotes = portfolioQuotes.ToDictionary(kv => kv.Key,
                kv => kv.Value.Skip(Math.Max(0, kv.Value.Count / 2 - 120)).ToList());
            var firstHalf = BacktestEngine.RunMultiSymbol(new TrendAwareReversalTurtleTrading(), firstHalfQuotes, portfolioArgs);
            var secondHalf = BacktestEngine.RunMultiSymbol(new TrendAwareReversalTurtleTrading(), secondHalfQuotes, portfolioArgs);
            string FormatResult(string name, BacktestResult result) => string.Join(",", name,
                result.TradeCount,
                result.TotalProfit.ToString("F2", CultureInfo.InvariantCulture),
                result.AnnualizedReturn.ToString("F2", CultureInfo.InvariantCulture),
                result.MaxDrawdownRate.ToString("F2", CultureInfo.InvariantCulture),
                result.ProfitFactor.ToString("F3", CultureInfo.InvariantCulture),
                result.SharpeRatio.ToString("F4", CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), ".burstcode", "reversal-local-portfolio-summary.csv"),
                "slice,trades,profit,annualized,drawdown,pf,sharpe\n" +
                FormatResult("full", portfolio) + "\n" +
                FormatResult("first", firstHalf) + "\n" +
                FormatResult("second", secondHalf));

            string benchmark = Path.Combine(Directory.GetCurrentDirectory(), ".burstcode", "reversal-local-benchmarks.csv");
            using var benchmarkWriter = new StreamWriter(benchmark, false);
            benchmarkWriter.WriteLine("strategy,dataSets,trades,totalProfit,avgAnnualized,avgDrawdown");
            foreach (string strategyName in new[] { "Turtle", "Donchian" })
            {
                double strategyAnnualized = 0;
                double strategyDrawdown = 0;
                decimal strategyProfit = 0;
                int strategyTrades = 0;
                int strategySets = 0;
                foreach (string symbol in symbols)
                {
                    string raw = "COIN_FUTURES_" + symbol + "USDT";
                    string market = "FUTURES_" + symbol + "USDT";
                    List<SkQuote> quotes;
                    try { quotes = TDEngineDataLoader.LoadKlines(raw, Period.TIME_1D, 1500); }
                    catch { continue; }
                    if (quotes.Count < 100) continue;
                    StgBase stg = strategyName == "Turtle" ? new TurtleTrading() : new DonchianChannel();
                    Dictionary<string, object>? args = strategyName == "Turtle"
                        ? new Dictionary<string, object> { ["systemType"] = 1, ["useLastTradeFilter"] = 0 }
                        : null;
                    var result = BacktestEngine.RunSingleSymbol(stg, market, quotes, Period.TIME_1D, args);
                    strategyAnnualized += result.AnnualizedReturn;
                    strategyDrawdown += result.MaxDrawdownRate;
                    strategyProfit += result.TotalProfit;
                    strategyTrades += result.TradeCount;
                    strategySets++;
                }
                benchmarkWriter.WriteLine(string.Join(",", strategyName, strategySets, strategyTrades,
                    strategyProfit.ToString("F2", CultureInfo.InvariantCulture),
                    (strategyAnnualized / Math.Max(1, strategySets)).ToString("F2", CultureInfo.InvariantCulture),
                    (strategyDrawdown / Math.Max(1, strategySets)).ToString("F2", CultureInfo.InvariantCulture)));
            }
        }

        [Fact]
        [Trait("Category", "RealData")]
        public void ScanStopAndExitPortfolioParameters()
        {
            string[] symbols =
            {
                "AMD", "BABA", "BZ", "CL", "COIN", "DIS", "DRAM", "ETH",
                "GOOGL", "HOOD", "INTC", "LTC", "META", "MSFT", "MU", "NATGAS",
                "NVDA", "QQQ", "SPY", "TSLA", "USAR", "V", "WMT", "XAU",
            };
            var quotes = new Dictionary<string, List<SkQuote>>();
            foreach (string symbol in symbols)
            {
                try
                {
                    var bars = TDEngineDataLoader.LoadKlines("COIN_FUTURES_" + symbol + "USDT", Period.TIME_1D, 1500);
                    if (bars.Count >= 100) quotes["FUTURES_" + symbol + "USDT"] = bars;
                }
                catch { }
            }

            string output = Path.Combine(Directory.GetCurrentDirectory(), ".burstcode", "reversal-stop-exit-scan.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var writer = new StreamWriter(output, false);
            writer.WriteLine("stop,exit,trades,profit,annualized,drawdown,pf,sharpe");
            foreach (decimal stop in new[] { 2m, 2.5m, 3m, 3.5m, 4m, 5m })
            foreach (int exit in new[] { 10, 15, 20, 30, 40, 60 })
            {
                var args = new Dictionary<string, object>
                {
                    ["trendRecentPeriod"] = 14,
                    ["trendGlobalPeriod"] = 120,
                    ["trendSlopeThreshold"] = 0.12m,
                    ["lookbackPeriod"] = 10,
                    ["exitLookbackPeriod"] = exit,
                    ["stopATR"] = stop,
                };
                var result = BacktestEngine.RunMultiSymbol(new TrendAwareReversalTurtleTrading(), quotes, args);
                writer.WriteLine(string.Join(",", stop, exit, result.TradeCount,
                    result.TotalProfit.ToString("F2", CultureInfo.InvariantCulture),
                    result.AnnualizedReturn.ToString("F2", CultureInfo.InvariantCulture),
                    result.MaxDrawdownRate.ToString("F2", CultureInfo.InvariantCulture),
                    result.ProfitFactor.ToString("F3", CultureInfo.InvariantCulture),
                    result.SharpeRatio.ToString("F4", CultureInfo.InvariantCulture)));
            }
        }

        [Fact]
        [Trait("Category", "RealData")]
        public void ScanTrendParameters()
        {
            string[] symbols = { "AMD", "BABA", "BZ", "CL", "COIN", "DIS", "DRAM", "ETH", "GOOGL", "HOOD", "INTC", "LTC", "META", "MSFT", "MU", "NATGAS", "NVDA", "QQQ", "SPY", "TSLA", "USAR", "V", "WMT", "XAU" };
            var loaded = new List<(string market, Period period, List<SkQuote> quotes)>();
            foreach (string symbol in symbols)
            {
                try
                {
                    var quotes = TDEngineDataLoader.LoadKlines("COIN_FUTURES_" + symbol + "USDT", Period.TIME_1D, 1500);
                    if (quotes.Count >= 100) loaded.Add(("FUTURES_" + symbol + "USDT", Period.TIME_1D, quotes));
                }
                catch { }
            }

            string output = Path.Combine(Directory.GetCurrentDirectory(), ".burstcode", "reversal-scan.csv");
            using var writer = new StreamWriter(output, false);
            writer.WriteLine("recent,global,threshold,lookback,exit,stop,avgAnnualized,worstAnnualized,totalProfit,totalTrades,avgDrawdown");
            int[] recentValues = { 10, 14, 21 };
            int[] globalValues = { 60, 120 };
            decimal[] thresholds = { 0.05m, 0.08m, 0.12m };
            int[] lookbacks = { 10, 20 };
            int[] exits = { 5, 10, 20 };
            decimal[] stops = { 2m, 3m };
            foreach (int recent in recentValues)
            foreach (int global in globalValues)
            foreach (decimal threshold in thresholds)
            foreach (int lookback in lookbacks)
            foreach (int exit in exits)
            foreach (decimal stop in stops)
            {
                double annualized = 0;
                double worst = double.MaxValue;
                decimal profit = 0;
                int trades = 0;
                double drawdown = 0;
                foreach (var item in loaded)
                {
                    var args = new Dictionary<string, object>
                    {
                        ["trendRecentPeriod"] = recent,
                        ["trendGlobalPeriod"] = global,
                        ["trendSlopeThreshold"] = threshold,
                        ["lookbackPeriod"] = lookback,
                        ["exitLookbackPeriod"] = exit,
                        ["stopATR"] = stop,
                    };
                    var result = BacktestEngine.RunSingleSymbol(new TrendAwareReversalTurtleTrading(), item.market,
                        item.quotes, item.period, args);
                    annualized += result.AnnualizedReturn;
                    worst = Math.Min(worst, result.AnnualizedReturn);
                    profit += result.TotalProfit;
                    trades += result.TradeCount;
                    drawdown += result.MaxDrawdownRate;
                }
                writer.WriteLine(string.Join(",", recent, global,
                    threshold.ToString(CultureInfo.InvariantCulture), lookback, exit,
                    stop.ToString(CultureInfo.InvariantCulture),
                    (annualized / loaded.Count).ToString("F3", CultureInfo.InvariantCulture),
                    worst.ToString("F3", CultureInfo.InvariantCulture),
                    profit.ToString("F2", CultureInfo.InvariantCulture), trades,
                    (drawdown / loaded.Count).ToString("F3", CultureInfo.InvariantCulture)));
            }
        }
    }
}