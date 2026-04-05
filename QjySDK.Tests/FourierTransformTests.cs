using Common;
using Model;
using QjySDK.Stg;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TDengine.Driver;
using TDengine.Driver.Client;
using Xunit;
using Xunit.Abstractions;
using static Model.EnumDef;

namespace QjySDK.Tests
{
    public class FourierTransformTests
    {
        private readonly ITestOutputHelper _output;

        public FourierTransformTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static string RawToMkt(string rawSymbol)
        {
            var strs = rawSymbol.Split(new char[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (strs.Length >= 3)
                return strs[1] + "_" + string.Join("_", strs, 2, strs.Length - 2);
            return rawSymbol;
        }

        private BacktestResult RunTest(string rawSymbol, Period period, int limit,
            Dictionary<string, object>? overrides = null)
        {
            if (!TDEngineDataLoader.IsAvailable())
                Assert.Fail("[SKIP] TDEngine 不可用");

            var mktSymbol = RawToMkt(rawSymbol);
            var quotes = TDEngineDataLoader.LoadKlines(rawSymbol, period, limit);
            _output.WriteLine($"[FourierTransform] {rawSymbol} {period} 加载了 {quotes.Count} 根K线");

            var stg = new FourierTransform();
            var cts = StgTestHelper.InitForTest(stg, mktSymbol);
            try
            {
                if (overrides != null)
                {
                    var argDicProp = typeof(StgBase).GetProperty("ArgDic",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    var argDic = (Dictionary<string, object>)argDicProp!.GetValue(stg)!;
                    foreach (var kv in overrides)
                        argDic[kv.Key] = kv.Value;
                }

                var tu = new TableUnit
                {
                    QuoteList = new List<SkQuote>(),
                    MktSymbol = mktSymbol,
                    Period = period
                };

                var trades = new List<stgInterface.RemoteTradeRecord>();
                for (int i = 0; i < quotes.Count; i++)
                {
                    tu.QuoteList.Add(quotes[i]);
                    try { stg.OnBar(period, tu, true, null); }
                    catch (Exception ex) { _output.WriteLine($"[OnBar] Bar {i}: {ex.Message}"); }
                    trades.AddRange(StgTestHelper.DrainTrades(stg));
                }

                var calcMethod = typeof(BacktestEngine).GetMethod("CalcResult",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var result = (BacktestResult)calcMethod!.Invoke(null,
                    new object[] { stg.GetType().Name, new[] { mktSymbol }, quotes.Count, trades, period })!;

                return result;
            }
            finally { cts.Cancel(); }
        }

        /// <summary>
        /// 基线测试：多品种多周期全面评估
        /// </summary>
        [Fact]
        public void Test_Baseline_MultiSymbolMultiPeriod()
        {
            if (!TDEngineDataLoader.IsAvailable())
                Assert.Fail("[SKIP] TDEngine 不可用");

            var configs = new (string symbol, Period period, int limit)[]
            {
                ("COIN_FUTURES_BTCUSDT", Period.TIME_1D, 2000),
                ("COIN_FUTURES_ETHUSDT", Period.TIME_1D, 2000),
                ("COIN_SPOT_BTCUSDT",   Period.TIME_1D, 2000),
                ("COIN_FUTURES_BTCUSDT", Period.TIME_4H, 5000),
                ("COIN_FUTURES_ETHUSDT", Period.TIME_4H, 5000),
                ("COIN_FUTURES_BTCUSDT", Period.TIME_1H, 5000),
                ("COIN_FUTURES_ETHUSDT", Period.TIME_1H, 5000),
                ("COIN_FUTURES_ETHUSDT", Period.TIME_5M, 20000),
            };

            var results = new List<(string desc, BacktestResult r)>();
            decimal totalProfit = 0;
            double totalWinRate = 0;
            double totalSharpe = 0;
            decimal totalDrawdown = 0;
            int count = 0;

            foreach (var (sym, period, limit) in configs)
            {
                try
                {
                    var r = RunTest(sym, period, limit);
                    var desc = $"{sym} {period}";
                    results.Add((desc, r));
                    _output.WriteLine(r.ToReport());

                    if (r.TradeCount > 0)
                    {
                        totalProfit += r.TotalProfit;
                        totalWinRate += r.WinRate;
                        totalSharpe += r.SharpeRatio;
                        totalDrawdown += r.MaxDrawdown;
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"[SKIP] {sym} {period}: {ex.Message}");
                }
            }

            _output.WriteLine("\n\n==================== 基线汇总 ====================");
            _output.WriteLine($"{"配置",-45} {"交易",5} {"胜率",7} {"收益",12} {"回撤",12} {"盈亏比",7} {"夏普",7}");
            _output.WriteLine(new string('-', 100));
            foreach (var (desc, r) in results)
            {
                _output.WriteLine($"{desc,-45} {r.TradeCount,5} {r.WinRate,6:F1}% {r.TotalProfit,12:F2} {r.MaxDrawdown,12:F2} {r.ProfitFactor,7:F2} {r.SharpeRatio,7:F2}");
            }

            if (count > 0)
            {
                _output.WriteLine(new string('=', 100));
                _output.WriteLine($"{"合计/平均",-45} {"",5} {totalWinRate / count,6:F1}% {totalProfit,12:F2} {totalDrawdown,12:F2} {"",7} {totalSharpe / count,7:F2}");
            }
        }

        /// <summary>
        /// 获取TDEngine中所有1D表名，转为rawSymbol格式
        /// </summary>
        private List<string> DiscoverAll1DSymbols()
        {
            var symbols = new List<string>();
            var builder = new ConnectionStringBuilder(TestConfig.TDEngine);
            using var client = DbDriver.Open(builder);
            client.Exec("use finance");
            using var rows = client.Query("show tables");
            while (rows.Read())
            {
                var name = rows.GetValue(0) is byte[] bytes
                    ? System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0')
                    : rows.GetValue(0)?.ToString() ?? "";
                if (!name.EndsWith("_time_1d")) continue;

                var baseName = name.Replace("_time_1d", "");
                if (baseName.StartsWith("futures_") && baseName.Contains("usdt"))
                {
                    // futures_btcusdt → COIN_FUTURES_BTCUSDT
                    var sym = baseName.Replace("futures_", "").ToUpperInvariant();
                    symbols.Add($"COIN_FUTURES_{sym}");
                }
                else if (baseName.StartsWith("spot_") && baseName.Contains("usdt"))
                {
                    // spot_btcusdt → COIN_SPOT_BTCUSDT
                    var sym = baseName.Replace("spot_", "").ToUpperInvariant();
                    symbols.Add($"COIN_SPOT_{sym}");
                }
                else if (baseName.StartsWith("spot_shse") || baseName.StartsWith("spot_szse"))
                {
                    // spot_shse510300 → SPOT_SHSE.510300
                    var raw = baseName.Replace("spot_", "").ToUpperInvariant();
                    // 还原点号: SHSE510300 → SHSE.510300, SZSE002539 → SZSE.002539
                    if (raw.StartsWith("SHSE")) raw = "SHSE." + raw.Substring(4);
                    else if (raw.StartsWith("SZSE")) raw = "SZSE." + raw.Substring(4);
                    symbols.Add($"SPOT_{raw}");
                }
                else if (baseName.StartsWith("futures_shfe") || baseName.StartsWith("futures_dce")
                    || baseName.StartsWith("futures_czce") || baseName.StartsWith("futures_ine"))
                {
                    // futures_shfefu2605 → FUTURES_SHFE.fu2605
                    var raw = baseName.Replace("futures_", "").ToUpperInvariant();
                    if (raw.StartsWith("SHFE")) raw = "SHFE." + raw.Substring(4).ToLower();
                    else if (raw.StartsWith("DCE")) raw = "DCE." + raw.Substring(3).ToLower();
                    else if (raw.StartsWith("CZCE")) raw = "CZCE." + raw.Substring(4).ToLower();
                    else if (raw.StartsWith("INE")) raw = "INE." + raw.Substring(3).ToLower();
                    symbols.Add($"FUTURES_{raw}");
                }
            }
            return symbols.OrderBy(s => s).ToList();
        }

        /// <summary>
        /// 获取TDEngine中所有可用的(symbol, period)组合
        /// </summary>
        private List<(string rawSymbol, Period period)> DiscoverAllCryptoData()
        {
            var result = new List<(string, Period)>();
            var periodMap = new Dictionary<string, Period>
            {
                ["time_5m"] = Period.TIME_5M,
                ["time_15m"] = Period.TIME_15M,
                ["time_30m"] = Period.TIME_30M,
                ["time_1h"] = Period.TIME_1H,
                ["time_2h"] = Period.TIME_2H,
                ["time_4h"] = Period.TIME_4H,
                ["time_1d"] = Period.TIME_1D,
            };

            var builder = new ConnectionStringBuilder(TestConfig.TDEngine);
            using var client = DbDriver.Open(builder);
            client.Exec("use finance");
            using var rows = client.Query("show tables");
            while (rows.Read())
            {
                var name = rows.GetValue(0) is byte[] bytes
                    ? System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0')
                    : rows.GetValue(0)?.ToString() ?? "";
                if (!name.Contains("usdt")) continue;

                foreach (var (suffix, period) in periodMap)
                {
                    if (!name.EndsWith("_" + suffix)) continue;
                    var baseName = name.Replace("_" + suffix, "");
                    string rawSymbol;
                    if (baseName.StartsWith("futures_"))
                        rawSymbol = "COIN_FUTURES_" + baseName.Replace("futures_", "").ToUpperInvariant();
                    else if (baseName.StartsWith("spot_"))
                        rawSymbol = "COIN_SPOT_" + baseName.Replace("spot_", "").ToUpperInvariant();
                    else continue;
                    result.Add((rawSymbol, period));
                    break;
                }
            }
            return result.OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToList();
        }

        /// <summary>
        /// 全量回测：60+品种 × 1D，模拟实盘环境
        /// </summary>
        [Fact]
        public void Test_FullScale_AllFutures_1D()
        {
            if (!TDEngineDataLoader.IsAvailable())
                Assert.Fail("[SKIP] TDEngine 不可用");

            var allSymbols = DiscoverAll1DSymbols();
            _output.WriteLine($"发现 {allSymbols.Count} 个加密品种1D数据");

            var results = new List<(string symbol, BacktestResult r)>();
            decimal totalProfit = 0;
            double totalWinRate = 0, totalSharpe = 0;
            decimal totalDrawdown = 0;
            int totalTrades = 0, count = 0;
            int profitableCount = 0;

            foreach (var rawSymbol in allSymbols)
            {
                try
                {
                    var r = RunTest(rawSymbol, Period.TIME_1D, 2000);
                    if (r.TradeCount > 0)
                    {
                        results.Add((rawSymbol, r));
                        totalProfit += r.TotalProfit;
                        totalWinRate += r.WinRate;
                        totalSharpe += r.SharpeRatio;
                        totalDrawdown += r.MaxDrawdown;
                        totalTrades += r.TradeCount;
                        if (r.TotalProfit > 0) profitableCount++;
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"[SKIP] {rawSymbol}: {ex.Message}");
                }
            }

            // 按收益排序输出
            _output.WriteLine($"\n\n==================== 全量回测 {count}品种 1D ====================");
            _output.WriteLine($"{"品种",-30} {"交易",5} {"胜率",7} {"收益",12} {"回撤",12} {"盈亏比",7} {"夏普",8}");
            _output.WriteLine(new string('-', 90));

            foreach (var (sym, r) in results.OrderByDescending(x => x.r.TotalProfit))
            {
                var shortSym = sym.Replace("COIN_FUTURES_", "");
                var marker = r.TotalProfit > 0 ? "+" : "";
                _output.WriteLine($"{shortSym,-30} {r.TradeCount,5} {r.WinRate,6:F1}% {marker}{r.TotalProfit,11:F2} {r.MaxDrawdown,12:F2} {r.ProfitFactor,7:F2} {r.SharpeRatio,8:F2}");
            }

            _output.WriteLine(new string('=', 90));
            _output.WriteLine($"品种数: {count}  盈利品种: {profitableCount} ({(count > 0 ? profitableCount * 100.0 / count : 0):F1}%)");
            _output.WriteLine($"总交易: {totalTrades}  平均胜率: {(count > 0 ? totalWinRate / count : 0):F1}%");
            _output.WriteLine($"总收益: {totalProfit:F2}  总回撤: {totalDrawdown:F2}");
            _output.WriteLine($"平均夏普: {(count > 0 ? totalSharpe / count : 0):F4}");
        }

        /// <summary>
        /// 复现用户回测：约400根1D（2025-03-04至今）
        /// </summary>
        [Fact]
        public void Test_FullScale_Recent400Bars()
        {
            if (!TDEngineDataLoader.IsAvailable())
                Assert.Fail("[SKIP] TDEngine 不可用");

            var allSymbols = DiscoverAll1DSymbols();
            _output.WriteLine($"发现 {allSymbols.Count} 个加密品种1D数据");

            var results = new List<(string symbol, BacktestResult r)>();
            decimal totalProfit = 0;
            double totalWinRate = 0, totalSharpe = 0;
            decimal totalDrawdown = 0;
            int totalTrades = 0, count = 0;
            int profitableCount = 0;

            foreach (var rawSymbol in allSymbols)
            {
                try
                {
                    var r = RunTest(rawSymbol, Period.TIME_1D, 400);
                    if (r.TradeCount > 0)
                    {
                        results.Add((rawSymbol, r));
                        totalProfit += r.TotalProfit;
                        totalWinRate += r.WinRate;
                        totalSharpe += r.SharpeRatio;
                        totalDrawdown += r.MaxDrawdown;
                        totalTrades += r.TradeCount;
                        if (r.TotalProfit > 0) profitableCount++;
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"[SKIP] {rawSymbol}: {ex.Message}");
                }
            }

            _output.WriteLine($"\n\n==================== 近期回测 {count}品种 1D (~400bars) ====================");
            _output.WriteLine($"{"品种",-30} {"交易",5} {"胜率",7} {"收益",12} {"回撤",12} {"盈亏比",7} {"夏普",8}");
            _output.WriteLine(new string('-', 90));

            foreach (var (sym, r) in results.OrderByDescending(x => x.r.TotalProfit))
            {
                var shortSym = sym.Replace("COIN_FUTURES_", "").Replace("COIN_SPOT_", "S_");
                var marker = r.TotalProfit > 0 ? "+" : "";
                _output.WriteLine($"{shortSym,-30} {r.TradeCount,5} {r.WinRate,6:F1}% {marker}{r.TotalProfit,11:F2} {r.MaxDrawdown,12:F2} {r.ProfitFactor,7:F2} {r.SharpeRatio,8:F2}");
            }

            _output.WriteLine(new string('=', 90));
            _output.WriteLine($"品种数: {count}  盈利品种: {profitableCount} ({(count > 0 ? profitableCount * 100.0 / count : 0):F1}%)");
            _output.WriteLine($"总交易: {totalTrades}  平均胜率: {(count > 0 ? totalWinRate / count : 0):F1}%");
            _output.WriteLine($"总收益: {totalProfit:F2}  总回撤: {totalDrawdown:F2}");
            _output.WriteLine($"平均夏普: {(count > 0 ? totalSharpe / count : 0):F4}");
        }
    }
}
