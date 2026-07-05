using Common;
using Model;
using QjySDK.Stg;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using static Model.EnumDef;

namespace QjySDK.Tests
{
    public class CachedBacktestTests
    {
        private readonly ITestOutputHelper _output;

        public CachedBacktestTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ==================== 40个品种定义 ====================
        private static readonly (string name, string rawSymbol)[] AllSymbols = new[]
        {
            ("宝丰能源", "STOCK_SPOT_SHSE.600989"),
            ("ETHUSDT永续", "COIN_FUTURES_ETHUSDT"),
            ("XAUUSDT永续", "COIN_FUTURES_XAUUSDT"),
            ("LTCUSDT永续", "COIN_FUTURES_LTCUSDT"),
            ("生猪2605", "FUTURES_FUTURES_DCE.lh2605"),
            ("燃油2605", "FUTURES_FUTURES_SHFE.fu2605"),
            ("豆粕2605", "FUTURES_FUTURES_DCE.m2605"),
            ("厦门钨业", "STOCK_SPOT_SHSE.600549"),
            ("玉米2605", "FUTURES_FUTURES_DCE.c2605"),
            ("中钨高新", "STOCK_SPOT_SZSE.000657"),
            ("华泰柏瑞沪深300ETF", "STOCK_SPOT_SHSE.510300"),
            ("特变电工", "STOCK_SPOT_SHSE.600089"),
            ("信维通信", "STOCK_SPOT_SZSE.300136"),
            ("云图控股", "STOCK_SPOT_SZSE.002539"),
            ("招商证券", "STOCK_SPOT_SHSE.600999"),
            ("贵研铂业", "STOCK_SPOT_SHSE.600459"),
            ("奥普光电", "STOCK_SPOT_SZSE.002338"),
            ("吉大正元", "STOCK_SPOT_SZSE.003029"),
            ("三江购物", "STOCK_SPOT_SHSE.601116"),
            ("华鑫股份", "STOCK_SPOT_SHSE.600621"),
            ("易方达创业板ETF", "STOCK_SPOT_SZSE.159915"),
            ("华安黄金易ETF", "STOCK_SPOT_SHSE.518880"),
            ("三六零", "STOCK_SPOT_SHSE.601360"),
            ("华夏中证动漫游戏ETF", "STOCK_SPOT_SZSE.159869"),
            ("陕国投A", "STOCK_SPOT_SZSE.000563"),
            ("国泰上证综合交易ETF", "STOCK_SPOT_SHSE.510760"),
            ("苏垦农发", "STOCK_SPOT_SHSE.601952"),
            ("江特电机", "STOCK_SPOT_SZSE.002176"),
            ("金桥信息", "STOCK_SPOT_SHSE.603918"),
            ("鸿博股份", "STOCK_SPOT_SZSE.002229"),
            ("红宝丽", "STOCK_SPOT_SZSE.002165"),
            ("酒鬼酒", "STOCK_SPOT_SZSE.000799"),
            ("北方稀土", "STOCK_SPOT_SHSE.600111"),
            ("中国软件", "STOCK_SPOT_SHSE.600536"),
            ("深信服", "STOCK_SPOT_SZSE.300454"),
            ("东江环保", "STOCK_SPOT_SZSE.002672"),
            ("中航成飞", "STOCK_SPOT_SZSE.302132"),
            ("三变科技", "STOCK_SPOT_SZSE.002112"),
            ("拓维信息", "STOCK_SPOT_SZSE.002261"),
            ("通宇通讯", "STOCK_SPOT_SZSE.002792"),
        };

        private static readonly Period[] TestPeriods = { Period.TIME_1D, Period.TIME_15M };

        private static readonly Dictionary<Period, int> PeriodLimits = new()
        {
            { Period.TIME_1D, 365 },
            { Period.TIME_15M, 8640 }
        };

        private static string RawToMkt(string rawSymbol)
        {
            var strs = rawSymbol.Split(new char[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (strs.Length >= 3)
                return strs[1] + "_" + string.Join("_", strs, 2, strs.Length - 2);
            return rawSymbol;
        }

        // ==================== Step 1: 预热缓存 ====================

        /// <summary>
        /// 从TDEngine下载所有品种的1D和15M K线数据并保存到本地JSON文件。
        /// 首次运行需要TDEngine连接，后续运行直接使用缓存文件。
        /// </summary>
        [Fact]
        public void Step1_WarmCache()
        {
            if (!TDEngineDataLoader.IsAvailable())
            {
                Assert.Fail("[SKIP] TDEngine 不可用，无法预热缓存");
                return;
            }

            _output.WriteLine($"缓存目录: {KlineCache.CacheDirectory}");
            _output.WriteLine($"品种数: {AllSymbols.Length}, 周期: 1D + 15M");
            _output.WriteLine($"总计: {AllSymbols.Length * TestPeriods.Length} 个数据集\n");

            int cached = 0, failed = 0, skipped = 0;

            foreach (var (name, rawSymbol) in AllSymbols)
            {
                foreach (var period in TestPeriods)
                {
                    try
                    {
                        if (KlineCache.HasCache(rawSymbol, period))
                        {
                            skipped++;
                            _output.WriteLine($"[SKIP] {name}({rawSymbol}) {period} 已有缓存");
                            continue;
                        }

                        var quotes = KlineCache.LoadKlines(rawSymbol, period, PeriodLimits[period]);
                        cached++;
                        _output.WriteLine($"[OK] {name}({rawSymbol}) {period}: {quotes.Count} bars");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _output.WriteLine($"[FAIL] {name}({rawSymbol}) {period}: {ex.Message}");
                    }
                }
            }

            _output.WriteLine($"\n========== 缓存预热完成 ==========");
            _output.WriteLine($"新缓存: {cached}, 已存在: {skipped}, 失败: {failed}");
        }

        // ==================== Step 2: ChanLun 全品种基线回测 ====================

        /// <summary>
        /// ChanLun（线段级别）全品种回测，使用缓存数据
        /// </summary>
        [Fact]
        public void Step2_ChanLun_Baseline()
        {
            RunBaselineForStrategy<ChanLun>("ChanLun");
        }

        /// <summary>
        /// ChanLunBi（笔级别）全品种回测，使用缓存数据
        /// </summary>
        [Fact]
        public void Step2_ChanLunBi_Baseline()
        {
            RunBaselineForStrategy<ChanLunBi>("ChanLunBi");
        }

        private void RunBaselineForStrategy<T>(string stgName) where T : StgBase, new()
        {
            _output.WriteLine($"========== {stgName} 全品种基线回测 ==========\n");

            var results = new List<(string name, string rawSymbol, Period period, BacktestResult result)>();
            int skipped = 0;

            foreach (var (name, rawSymbol) in AllSymbols)
            {
                foreach (var period in TestPeriods)
                {
                    var quotes = KlineCache.LoadFromCacheOnly(rawSymbol, period, PeriodLimits[period]);
                    if (quotes == null || quotes.Count < 60)
                    {
                        skipped++;
                        _output.WriteLine($"[SKIP] {name} {period}: 无缓存或数据不足");
                        continue;
                    }

                    try
                    {
                        var stg = new T();
                        var mktSymbol = RawToMkt(rawSymbol);
                        var r = BacktestEngine.RunSingleSymbol(stg, mktSymbol, quotes, period);
                        results.Add((name, rawSymbol, period, r));
                        _output.WriteLine($"[OK] {name} {period}: {quotes.Count} bars, {r.TradeCount} trades");
                    }
                    catch (Exception ex)
                    {
                        _output.WriteLine($"[ERROR] {name} {period}: {ex.Message}");
                    }
                }
            }

            // 汇总表
            PrintSummaryTable(stgName, results, skipped);
        }

        // ==================== Step 3: V0 vs V2 对比 ====================

        /// <summary>
        /// ChanLun V0（无优化）vs V2（当前参数）对比
        /// </summary>
        [Fact]
        public void Step3_ChanLun_V0_vs_V2()
        {
            RunV0vsV2<ChanLun>("ChanLun");
        }

        /// <summary>
        /// ChanLunBi V0 vs V2 对比
        /// </summary>
        [Fact]
        public void Step3_ChanLunBi_V0_vs_V2()
        {
            RunV0vsV2<ChanLunBi>("ChanLunBi");
        }

        private void RunV0vsV2<T>(string stgName) where T : StgBase, new()
        {
            var v0Overrides = new Dictionary<string, object>
            {
                ["useStopLoss"] = 0,
                ["useTrailingStop"] = 0,
                ["signalExpiryBars"] = 0,
                ["useZhongShuExit"] = 0,
                ["maxZhongShuDeviation"] = 9999.0m,
                ["tradeCooldownBars"] = 0,
                ["noReversalOnBuy3Sell3"] = 0,
                ["zhongShuExitScope"] = 0
            };

            _output.WriteLine($"========== {stgName} V0 vs V2 对比 ==========\n");
            _output.WriteLine($"{"品种",-16} {"周期",-10} {"版本",-5} {"交易",5} {"胜率",7} {"收益",12} {"回撤",12} {"夏普",8} {"盈亏比",7}");
            _output.WriteLine(new string('-', 90));

            foreach (var (name, rawSymbol) in AllSymbols)
            {
                foreach (var period in TestPeriods)
                {
                    var quotes = KlineCache.LoadFromCacheOnly(rawSymbol, period, PeriodLimits[period]);
                    if (quotes == null || quotes.Count < 60) continue;

                    var mktSymbol = RawToMkt(rawSymbol);

                    try
                    {
                        // V0
                        var r0 = RunWithOverrides<T>(mktSymbol, quotes, period, v0Overrides);
                        PrintResultLine(name, period, "V0", r0);

                        // V2
                        var r2 = RunWithOverrides<T>(mktSymbol, quotes, period, null);
                        PrintResultLine(name, period, "V2", r2);
                    }
                    catch (Exception ex)
                    {
                        _output.WriteLine($"  {name,-16} {period,-10} ERROR: {ex.Message}");
                    }
                }
            }
        }

        // ==================== Step 4: 参数网格搜索 ====================

        /// <summary>
        /// ChanLunBi 参数网格搜索（可自定义参数组合）
        /// </summary>
        [Fact]
        public void Step4_ChanLunBi_ParamGrid()
        {
            _output.WriteLine("========== ChanLunBi 参数网格搜索 ==========\n");

            // 参数组定义：现有参数的默认值寻优（不改逻辑）
            var paramSets = new List<(string name, Dictionary<string, object> overrides)>
            {
                ("default", null),
                ("Trail5-3", new Dictionary<string, object> { ["useTrailingStop"] = 1, ["trailingActivatePercent"] = 5.0m, ["trailingStopPercent"] = 3.0m }),
                ("Trail10-5", new Dictionary<string, object> { ["useTrailingStop"] = 1, ["trailingActivatePercent"] = 10.0m, ["trailingStopPercent"] = 5.0m }),
                ("SL3", new Dictionary<string, object> { ["stopLossPercent"] = 3.0m }),
                ("SL8", new Dictionary<string, object> { ["stopLossPercent"] = 8.0m }),
                ("cd5", new Dictionary<string, object> { ["tradeCooldownBars"] = 5 }),
            };

            // 收集结果
            var allResults = new Dictionary<string, List<(string pName, BacktestResult r)>>();

            foreach (var (name, rawSymbol) in AllSymbols)
            {
                foreach (var period in TestPeriods)
                {
                    var quotes = KlineCache.LoadFromCacheOnly(rawSymbol, period, PeriodLimits[period]);
                    if (quotes == null || quotes.Count < 200) continue;

                    var mktSymbol = RawToMkt(rawSymbol);
                    var key = $"{name}|{period}";
                    var list = new List<(string, BacktestResult)>();

                    foreach (var (pName, overrides) in paramSets)
                    {
                        try
                        {
                            var r = RunWithOverrides<ChanLunBi>(mktSymbol, quotes, period, overrides);
                            list.Add((pName, r));
                        }
                        catch
                        {
                            // skip
                        }
                    }

                    if (list.Count > 0)
                        allResults[key] = list;
                }
            }

            // 输出
            foreach (var kv in allResults)
            {
                if (kv.Value.All(x => x.r.TradeCount == 0)) continue;
                _output.WriteLine($"--- {kv.Key} ---");
                foreach (var (pName, r) in kv.Value)
                {
                    _output.WriteLine($"  {pName,-16} 交易:{r.TradeCount,4} 胜率:{r.WinRate,5:F1}% 收益:{r.TotalProfit,12:F2} 回撤:{r.MaxDrawdown,12:F2} 夏普:{r.SharpeRatio,8:F4} 盈亏比:{r.ProfitFactor,5:F2}");
                }
                _output.WriteLine("");
            }

            // 统计每个参数组的综合表现
            _output.WriteLine("========== 参数组综合统计 ==========");
            var baselineName = paramSets[0].name;
            foreach (var (pName, _) in paramSets.Skip(1))
            {
                int wins = 0, losses = 0;
                decimal totalDelta = 0;
                foreach (var kv in allResults)
                {
                    var baseR = kv.Value.FirstOrDefault(x => x.pName == baselineName).r;
                    var testR = kv.Value.FirstOrDefault(x => x.pName == pName).r;
                    if (baseR == null || testR == null) continue;
                    if (testR.TotalProfit > baseR.TotalProfit) wins++;
                    else if (testR.TotalProfit < baseR.TotalProfit) losses++;
                    totalDelta += testR.TotalProfit - baseR.TotalProfit;
                }
                _output.WriteLine($"  {pName,-16} 胜:{wins} 负:{losses} 总收益差:{totalDelta:F2}");
            }
        }

        // ==================== 工具方法 ====================

        private BacktestResult RunWithOverrides<T>(string mktSymbol, List<SkQuote> quotes, Period period,
            Dictionary<string, object> overrides) where T : StgBase, new()
        {
            var stg = new T();
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
                    catch { }
                    trades.AddRange(StgTestHelper.DrainTrades(stg));
                }

                var calcMethod = typeof(BacktestEngine).GetMethod("CalcResult",
                    BindingFlags.NonPublic | BindingFlags.Static);
                return (BacktestResult)calcMethod!.Invoke(null,
                    new object[] { stg.GetType().Name, new[] { mktSymbol }, quotes.Count, trades, period, null })!;
            }
            finally { cts.Cancel(); }
        }

        private void PrintSummaryTable(string stgName,
            List<(string name, string rawSymbol, Period period, BacktestResult result)> results, int skipped)
        {
            _output.WriteLine($"\n========== {stgName} 汇总 ({results.Count} 组, 跳过 {skipped}) ==========\n");
            _output.WriteLine($"{"品种",-16} {"周期",-10} {"K线",6} {"交易",5} {"胜率",7} {"收益",12} {"回撤",12} {"夏普",8} {"盈亏比",7}");
            _output.WriteLine(new string('-', 90));

            decimal totalProfit = 0;
            int totalTrades = 0;
            int totalWins = 0;

            foreach (var period in TestPeriods)
            {
                var periodResults = results.Where(r => r.period == period).ToList();
                foreach (var (name, rawSymbol, p, r) in periodResults)
                {
                    var marker = r.TotalProfit > 0 ? "+" : "";
                    _output.WriteLine($"{name,-16} {p,-10} {r.TotalBars,6} {r.TradeCount,5} {r.WinRate,6:F1}% {marker}{r.TotalProfit,11:F2} {r.MaxDrawdown,12:F2} {r.SharpeRatio,8:F4} {r.ProfitFactor,7:F2}");
                    totalProfit += r.TotalProfit;
                    totalTrades += r.TradeCount;
                    totalWins += r.WinCount;
                }

                if (periodResults.Count > 0)
                {
                    var pProfit = periodResults.Sum(r => r.result.TotalProfit);
                    var pTrades = periodResults.Sum(r => r.result.TradeCount);
                    var pWins = periodResults.Sum(r => r.result.WinCount);
                    var pWinRate = pTrades > 0 ? 100.0 * pWins / pTrades : 0;
                    var profitable = periodResults.Count(r => r.result.TotalProfit > 0);
                    _output.WriteLine($"{"[小计]",-16} {period,-10} {"",6} {pTrades,5} {pWinRate,6:F1}% {pProfit,12:F2} {"",12} {"",8} 盈利品种:{profitable}/{periodResults.Count}");
                    _output.WriteLine(new string('-', 90));
                }
            }

            var overallWinRate = totalTrades > 0 ? 100.0 * totalWins / totalTrades : 0;
            _output.WriteLine($"{"[总计]",-16} {"",10} {"",6} {totalTrades,5} {overallWinRate,6:F1}% {totalProfit,12:F2}");
        }

        private void PrintResultLine(string name, Period period, string version, BacktestResult r)
        {
            var marker = r.TotalProfit > 0 ? "+" : "";
            _output.WriteLine($"{name,-16} {period,-10} {version,-5} {r.TradeCount,5} {r.WinRate,6:F1}% {marker}{r.TotalProfit,11:F2} {r.MaxDrawdown,12:F2} {r.SharpeRatio,8:F4} {r.ProfitFactor,7:F2}");
        }
    }
}
