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
    public class BollShadowTests
    {
        private readonly ITestOutputHelper _output;

        public BollShadowTests(ITestOutputHelper output)
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

        // 用缓存数据的40品种
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

        /// <summary>
        /// 基线测试：当前Boll_Shadow在40品种 × 2周期上的表现
        /// </summary>
        [Fact]
        public void Step1_Baseline()
        {
            _output.WriteLine($"{"品种",-18} {"周期",-12} {"K线",5} {"交易",4} {"胜率",6} {"收益",12} {"回撤",10} {"夏普",10} {"盈亏比",7}");
            _output.WriteLine(new string('-', 90));

            decimal totalProfit = 0;
            int totalTrades = 0;
            int totalWins = 0;
            int profitSymbols1D = 0, totalSymbols1D = 0;
            int profitSymbols15M = 0, totalSymbols15M = 0;

            foreach (var period in TestPeriods)
            {
                decimal periodProfit = 0;
                int periodTrades = 0;
                int periodWins = 0;
                int periodProfitSymbols = 0;
                int periodTotalSymbols = 0;

                foreach (var (name, rawSymbol) in AllSymbols)
                {
                    var quotes = KlineCache.LoadFromCacheOnly(rawSymbol, period, PeriodLimits[period]);
                    if (quotes == null || quotes.Count < 60) continue;

                    var stg = new Boll_Shadow();
                    var mktSymbol = RawToMkt(rawSymbol);
                    var r = BacktestEngine.RunSingleSymbol(stg, mktSymbol, quotes, period);

                    periodTotalSymbols++;
                    periodTrades += r.TradeCount;
                    periodWins += r.WinCount;
                    periodProfit += r.TotalProfit;
                    if (r.TotalProfit > 0) periodProfitSymbols++;

                    var marker = r.TotalProfit > 0 ? "+" : "";
                    _output.WriteLine($"{name,-18} {period,-12} {quotes.Count,5} {r.TradeCount,4} {r.WinRate,5:F1}% {marker + r.TotalProfit.ToString("F2"),12} {r.MaxDrawdown,10:F2} {r.SharpeRatio,10:F4} {r.ProfitFactor,7:F2}");
                }

                var wr = periodTrades > 0 ? 100.0 * periodWins / periodTrades : 0;
                _output.WriteLine($"[小计] {period,-12}            {periodTrades,4} {wr,5:F1}% {periodProfit,12:F2}               盈利品种:{periodProfitSymbols}/{periodTotalSymbols}");
                _output.WriteLine("");

                totalProfit += periodProfit;
                totalTrades += periodTrades;
                totalWins += periodWins;
                if (period == Period.TIME_1D) { profitSymbols1D = periodProfitSymbols; totalSymbols1D = periodTotalSymbols; }
                else { profitSymbols15M = periodProfitSymbols; totalSymbols15M = periodTotalSymbols; }
            }

            var totalWr = totalTrades > 0 ? 100.0 * totalWins / totalTrades : 0;
            _output.WriteLine(new string('-', 90));
            _output.WriteLine($"[总计]                       {totalTrades,4} {totalWr,5:F1}% {totalProfit,12:F2}");
            _output.WriteLine($"  1D盈利品种: {profitSymbols1D}/{totalSymbols1D}  15M盈利品种: {profitSymbols15M}/{totalSymbols15M}");
        }

        /// <summary>
        /// 参数网格搜索：聚焦ATR止损/追踪止损/最大持仓 + shadowRate
        /// </summary>
        [Fact]
        public void Step2_ParamGrid()
        {
            var paramSets = new List<(string name, Dictionary<string, object> overrides)>();

            // 当前默认参数
            paramSets.Add(("default", null));

            // ATR止损倍数
            paramSets.Add(("atr=1.5", new Dictionary<string, object> { ["atrStopMult"] = 1.5m }));
            paramSets.Add(("atr=2.5", new Dictionary<string, object> { ["atrStopMult"] = 2.5m }));
            paramSets.Add(("atr=3.0", new Dictionary<string, object> { ["atrStopMult"] = 3.0m }));

            // 追踪止损倍数
            paramSets.Add(("trail=1.5", new Dictionary<string, object> { ["trailingAtrMult"] = 1.5m }));
            paramSets.Add(("trail=2.5", new Dictionary<string, object> { ["trailingAtrMult"] = 2.5m }));
            paramSets.Add(("trail=3.0", new Dictionary<string, object> { ["trailingAtrMult"] = 3.0m }));

            // 最大持仓bars
            paramSets.Add(("hold=15", new Dictionary<string, object> { ["maxHoldBars"] = 15 }));
            paramSets.Add(("hold=50", new Dictionary<string, object> { ["maxHoldBars"] = 50 }));
            paramSets.Add(("hold=80", new Dictionary<string, object> { ["maxHoldBars"] = 80 }));

            // shadowRate
            paramSets.Add(("sr=1.5", new Dictionary<string, object> { ["shadowRate"] = 1.5m }));
            paramSets.Add(("sr=2.5", new Dictionary<string, object> { ["shadowRate"] = 2.5m }));
            paramSets.Add(("sr=3.0", new Dictionary<string, object> { ["shadowRate"] = 3.0m }));

            // 组合候选
            paramSets.Add(("atr3+trail3+h50", new Dictionary<string, object> { ["atrStopMult"] = 3.0m, ["trailingAtrMult"] = 3.0m, ["maxHoldBars"] = 50 }));
            paramSets.Add(("atr2.5+trail2.5+h50", new Dictionary<string, object> { ["atrStopMult"] = 2.5m, ["trailingAtrMult"] = 2.5m, ["maxHoldBars"] = 50 }));
            paramSets.Add(("atr3+trail2.5+sr2.5", new Dictionary<string, object> { ["atrStopMult"] = 3.0m, ["trailingAtrMult"] = 2.5m, ["shadowRate"] = 2.5m }));

            foreach (var period in TestPeriods)
            {
                _output.WriteLine($"\n===== {period} =====");
                _output.WriteLine($"{"参数",-24} {"交易",5} {"胜率",7} {"总收益",12} {"最大回撤",10} {"盈利品种",8}");
                _output.WriteLine(new string('-', 75));

                foreach (var (pname, overrides) in paramSets)
                {
                    int totalTrades = 0, totalWins = 0;
                    decimal totalProfit = 0, maxDD = 0;
                    int profitSymbols = 0, totalSymbols = 0;

                    foreach (var (name, rawSymbol) in AllSymbols)
                    {
                        var quotes = KlineCache.LoadFromCacheOnly(rawSymbol, period, PeriodLimits[period]);
                        if (quotes == null || quotes.Count < 60) continue;

                        var mktSymbol = RawToMkt(rawSymbol);
                        var r = RunWithOverrides(mktSymbol, quotes, period, overrides);
                        totalTrades += r.TradeCount;
                        totalWins += r.WinCount;
                        totalProfit += r.TotalProfit;
                        if (r.MaxDrawdown > maxDD) maxDD = r.MaxDrawdown;
                        totalSymbols++;
                        if (r.TotalProfit > 0) profitSymbols++;
                    }

                    var wr = totalTrades > 0 ? 100.0 * totalWins / totalTrades : 0;
                    var marker = totalProfit > 0 ? " ★" : "";
                    _output.WriteLine($"{pname,-24} {totalTrades,5} {wr,6:F1}% {totalProfit,12:F2} {maxDD,10:F2} {profitSymbols,3}/{totalSymbols}{marker}");
                }
            }
        }
        /// <summary>
        /// 优化A/B对比：逐项测试5个优化开关的效果
        /// </summary>
        [Fact]
        public void Step3_OptimizationAB()
        {
            var paramSets = new List<(string name, Dictionary<string, object> overrides)>
            {
                ("baseline", null),
                ("OPT1-trend", new Dictionary<string, object> { ["useTrendFilter"] = 1 }),
                ("OPT2-vol", new Dictionary<string, object> { ["useVolumeFilter"] = 1 }),
                ("OPT3-edge", new Dictionary<string, object> { ["useBandEdge"] = 1 }),
                ("OPT4-break", new Dictionary<string, object> { ["useBreakConfirm"] = 1 }),
                ("OPT5-entry", new Dictionary<string, object> { ["useEntryHighFix"] = 1 }),
                ("OPT1+2", new Dictionary<string, object> { ["useTrendFilter"] = 1, ["useVolumeFilter"] = 1 }),
                ("OPT1+3", new Dictionary<string, object> { ["useTrendFilter"] = 1, ["useBandEdge"] = 1 }),
                ("OPT1+2+3", new Dictionary<string, object> { ["useTrendFilter"] = 1, ["useVolumeFilter"] = 1, ["useBandEdge"] = 1 }),
                ("ALL5", new Dictionary<string, object> { ["useTrendFilter"] = 1, ["useVolumeFilter"] = 1, ["useBandEdge"] = 1, ["useBreakConfirm"] = 1, ["useEntryHighFix"] = 1 }),
            };

            foreach (var period in TestPeriods)
            {
                _output.WriteLine($"\n===== {period} =====");
                _output.WriteLine($"{"配置",-16} {"交易",5} {"胜率",7} {"总收益",12} {"最大回撤",10} {"盈利品种",8}");
                _output.WriteLine(new string('-', 70));

                foreach (var (pname, overrides) in paramSets)
                {
                    int totalTrades = 0, totalWins = 0;
                    decimal totalProfit = 0, maxDD = 0;
                    int profitSymbols = 0, totalSymbols = 0;

                    foreach (var (name, rawSymbol) in AllSymbols)
                    {
                        var quotes = KlineCache.LoadFromCacheOnly(rawSymbol, period, PeriodLimits[period]);
                        if (quotes == null || quotes.Count < 60) continue;

                        var mktSymbol = RawToMkt(rawSymbol);
                        var r = RunWithOverrides(mktSymbol, quotes, period, overrides);
                        totalTrades += r.TradeCount;
                        totalWins += r.WinCount;
                        totalProfit += r.TotalProfit;
                        if (r.MaxDrawdown > maxDD) maxDD = r.MaxDrawdown;
                        totalSymbols++;
                        if (r.TotalProfit > 0) profitSymbols++;
                    }

                    var wr = totalTrades > 0 ? 100.0 * totalWins / totalTrades : 0;
                    var marker = totalProfit > 0 ? " ★" : "";
                    _output.WriteLine($"{pname,-16} {totalTrades,5} {wr,6:F1}% {totalProfit,12:F2} {maxDD,10:F2} {profitSymbols,3}/{totalSymbols}{marker}");
                }
            }
        }

        // ==================== 工具方法 ====================

        private BacktestResult RunWithOverrides(string mktSymbol, List<SkQuote> quotes, Period period,
            Dictionary<string, object> overrides)
        {
            var stg = new Boll_Shadow();
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

                var lastPrices = new Dictionary<string, decimal> { { mktSymbol, quotes.Last().Close } };
                var calcMethod = typeof(BacktestEngine).GetMethod("CalcResult",
                    BindingFlags.NonPublic | BindingFlags.Static);
                return (BacktestResult)calcMethod!.Invoke(null,
                    new object[] { stg.GetType().Name, new[] { mktSymbol }, quotes.Count, trades, period, lastPrices })!;
            }
            finally { cts.Cancel(); }
        }
    }
}
