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
    /// <summary>
    /// PhoenixReversal（凤凰反转）回测验证：
    /// 1. 全品种日线上与 BollingerMeanReversion 基线对比
    /// 2. 参数网格搜索（共振分门槛/出场模式/加仓开关）
    /// 3. 15分钟周期子集验证
    /// </summary>
    public class PhoenixReversalTests
    {
        private readonly ITestOutputHelper _output;

        public PhoenixReversalTests(ITestOutputHelper output)
        {
            _output = output;
        }

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

        // 15分钟周期只跑代表性子集，控制运行时长
        private static readonly (string name, string rawSymbol)[] SubsetSymbols = new[]
        {
            ("ETHUSDT永续", "COIN_FUTURES_ETHUSDT"),
            ("XAUUSDT永续", "COIN_FUTURES_XAUUSDT"),
            ("豆粕2605", "FUTURES_FUTURES_DCE.m2605"),
            ("燃油2605", "FUTURES_FUTURES_SHFE.fu2605"),
            ("华泰柏瑞沪深300ETF", "STOCK_SPOT_SHSE.510300"),
            ("北方稀土", "STOCK_SPOT_SHSE.600111"),
        };

        private static string RawToMkt(string rawSymbol)
        {
            var strs = rawSymbol.Split(new char[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (strs.Length >= 3)
                return strs[1] + "_" + string.Join("_", strs, 2, strs.Length - 2);
            return rawSymbol;
        }

        // ==================== 测试1: 日线全品种 Phoenix vs Bollinger基线 ====================

        [Fact]
        public void Phoenix_vs_Bollinger_Daily()
        {
            _output.WriteLine("========== PhoenixReversal vs BollingerMeanReversion 日线全品种对比 ==========\n");
            _output.WriteLine($"{"品种",-16} {"策略",-10} {"交易",5} {"胜率",7} {"收益",12} {"回撤",12} {"夏普",8} {"盈亏比",7}");
            _output.WriteLine(new string('-', 90));

            var phxAgg = new Aggregate();
            var bmrAgg = new Aggregate();

            foreach (var (name, rawSymbol) in AllSymbols)
            {
                var quotes = KlineCache.LoadFromCacheOnly(rawSymbol, Period.TIME_1D, 365);
                if (quotes == null || quotes.Count < 100) continue;

                var mktSymbol = RawToMkt(rawSymbol);
                try
                {
                    var rp = RunWithOverrides<PhoenixReversal>(mktSymbol, quotes, Period.TIME_1D, null);
                    var rb = RunWithOverrides<BollingerMeanReversion>(mktSymbol, quotes, Period.TIME_1D, null);
                    PrintLine(name, "Phoenix", rp);
                    PrintLine(name, "BollMR", rb);
                    phxAgg.Add(rp);
                    bmrAgg.Add(rb);
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"{name,-16} ERROR: {ex.Message}");
                }
            }

            _output.WriteLine(new string('-', 90));
            _output.WriteLine($"\n========== 汇总 ==========");
            _output.WriteLine($"Phoenix : {phxAgg}");
            _output.WriteLine($"BollMR  : {bmrAgg}");
        }

        // ==================== 测试2: 日线参数网格 ====================

        [Fact]
        public void Phoenix_ParamGrid_Daily()
        {
            _output.WriteLine("========== PhoenixReversal 日线参数网格 ==========\n");

            var paramSets = new List<(string name, Dictionary<string, object> overrides)>
            {
                ("双向默认(SAR开)", null),
                ("双向关SAR", new Dictionary<string, object> { ["sarMode"] = 0 }),
                ("双向SAR+加仓", new Dictionary<string, object> { ["allowAdds"] = 1 }),
                ("双向SAR+RSI10", new Dictionary<string, object> { ["pulseRsiBuy"] = 10.0 }),
            };

            var aggs = paramSets.ToDictionary(p => p.name, p => new Aggregate());

            foreach (var (name, rawSymbol) in AllSymbols)
            {
                var quotes = KlineCache.LoadFromCacheOnly(rawSymbol, Period.TIME_1D, 365);
                if (quotes == null || quotes.Count < 100) continue;

                var mktSymbol = RawToMkt(rawSymbol);
                foreach (var (pName, overrides) in paramSets)
                {
                    try
                    {
                        var r = RunWithOverrides<PhoenixReversal>(mktSymbol, quotes, Period.TIME_1D, overrides);
                        aggs[pName].Add(r);
                    }
                    catch { }
                }
            }

            _output.WriteLine($"{"参数组",-14} {"交易",6} {"胜率",7} {"总收益",12} {"盈利组数",8}");
            _output.WriteLine(new string('-', 60));
            foreach (var (pName, _) in paramSets)
            {
                _output.WriteLine($"{pName,-14} {aggs[pName]}");
            }
        }

        // ==================== 测试3: 15分钟子集验证 ====================

        [Fact]
        public void Phoenix_vs_Bollinger_15M_Subset()
        {
            _output.WriteLine("========== PhoenixReversal 15M 顺势模式交叉验证 ==========\n");
            _output.WriteLine($"{"品种",-16} {"策略",-14} {"交易",5} {"胜率",7} {"收益",12} {"回撤",12} {"盈亏比",7}");
            _output.WriteLine(new string('-', 90));

            var phxAgg = new Aggregate();
            var phx15Agg = new Aggregate();
            var phxAdpAgg = new Aggregate();
            var bmrAgg = new Aggregate();

            foreach (var (name, rawSymbol) in SubsetSymbols)
            {
                var quotes = KlineCache.LoadFromCacheOnly(rawSymbol, Period.TIME_15M, 4000);
                if (quotes == null || quotes.Count < 200) continue;

                int mode = quotes.Last().Close >= quotes.First().Close ? 1 : 2;
                var aligned = new Dictionary<string, object> { ["mode"] = mode };
                var alignedRsi10 = new Dictionary<string, object> { ["mode"] = mode, ["pulseRsiBuy"] = 10.0 };
                var mktSymbol = RawToMkt(rawSymbol);
                try
                {
                    var rp = RunWithOverrides<PhoenixReversal>(mktSymbol, quotes, Period.TIME_15M, aligned);
                    var rp10 = RunWithOverrides<PhoenixReversal>(mktSymbol, quotes, Period.TIME_15M, alignedRsi10);
                    var rb = RunWithOverrides<DonchianReverse>(mktSymbol, quotes, Period.TIME_15M, aligned);
                    PrintLine(name, "Phx默认RSI15", rp);
                    PrintLine(name, "Phx波段RSI10", rp10);
                    PrintLine(name, "Donchian", rb);
                    phxAgg.Add(rp);
                    phxAdpAgg.Add(rp10);
                    bmrAgg.Add(rb);
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"{name,-16} ERROR: {ex.Message}");
                }
            }

            _output.WriteLine(new string('-', 90));
            _output.WriteLine($"\n========== 汇总 ==========");
            _output.WriteLine($"Phx默认RSI15 : {phxAgg}");
            _output.WriteLine($"Phx波段RSI10 : {phxAdpAgg}");
            _output.WriteLine($"Donchian     : {bmrAgg}");
        }

        // ==================== 测试4: 顺势单向模式（模拟用户正确判断大趋势） ====================

        /// <summary>
        /// 策略的设计场景是用户确定大趋势后单向运行。
        /// 以数据集首尾涨跌方向作为"用户判断的大趋势"（涨→mode=1仅做多，跌→mode=2仅做空），
        /// 对比：Phoenix顺势单向 vs Phoenix双向 vs BollMR顺势单向。
        /// </summary>
        [Fact]
        public void Phoenix_TrendAligned_Daily()
        {
            _output.WriteLine("========== PhoenixReversal 顺势单向模式 日线全品种 ==========\n");
            _output.WriteLine($"{"品种",-16} {"方向",-4} {"策略",-12} {"交易",5} {"胜率",7} {"收益",12} {"回撤",12} {"盈亏比",7}");
            _output.WriteLine(new string('-', 90));

            var phxAligned = new Aggregate();
            var phxBoth = new Aggregate();
            var donAligned = new Aggregate();
            var donBoth = new Aggregate();
            int beatAligned = 0, beatBoth = 0, tieAligned = 0, compared = 0;

            foreach (var (name, rawSymbol) in AllSymbols)
            {
                var quotes = KlineCache.LoadFromCacheOnly(rawSymbol, Period.TIME_1D, 365);
                if (quotes == null || quotes.Count < 100) continue;

                // 以首尾方向模拟用户判断的大趋势
                int mode = quotes.Last().Close >= quotes.First().Close ? 1 : 2;
                var dir = mode == 1 ? "多" : "空";
                var alignedOverride = new Dictionary<string, object> { ["mode"] = mode };

                var mktSymbol = RawToMkt(rawSymbol);
                try
                {
                    var rpa = RunWithOverrides<PhoenixReversal>(mktSymbol, quotes, Period.TIME_1D, alignedOverride);
                    var rpb = RunWithOverrides<PhoenixReversal>(mktSymbol, quotes, Period.TIME_1D, null);
                    var rda = RunWithOverrides<DonchianReverse>(mktSymbol, quotes, Period.TIME_1D, alignedOverride);
                    var rdb = RunWithOverrides<DonchianReverse>(mktSymbol, quotes, Period.TIME_1D, null);
                    _output.WriteLine($"{name,-16} {dir,-4} {"Phx顺势",-12} {rpa.TradeCount,5} {rpa.WinRate,6:F1}% {rpa.TotalProfit,12:F2} {rpa.MaxDrawdown,12:F2} {rpa.ProfitFactor,7:F2}");
                    _output.WriteLine($"{name,-16} {dir,-4} {"Phx双向",-12} {rpb.TradeCount,5} {rpb.WinRate,6:F1}% {rpb.TotalProfit,12:F2} {rpb.MaxDrawdown,12:F2} {rpb.ProfitFactor,7:F2}");
                    _output.WriteLine($"{name,-16} {dir,-4} {"Don顺势",-12} {rda.TradeCount,5} {rda.WinRate,6:F1}% {rda.TotalProfit,12:F2} {rda.MaxDrawdown,12:F2} {rda.ProfitFactor,7:F2}");
                    _output.WriteLine($"{name,-16} {dir,-4} {"Don双向",-12} {rdb.TradeCount,5} {rdb.WinRate,6:F1}% {rdb.TotalProfit,12:F2} {rdb.MaxDrawdown,12:F2} {rdb.ProfitFactor,7:F2}");
                    phxAligned.Add(rpa);
                    phxBoth.Add(rpb);
                    donAligned.Add(rda);
                    donBoth.Add(rdb);
                    compared++;
                    if (rpa.TotalProfit > rda.TotalProfit) beatAligned++;
                    else if (rpa.TotalProfit == rda.TotalProfit) tieAligned++;
                    if (rpb.TotalProfit > rdb.TotalProfit) beatBoth++;
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"{name,-16} ERROR: {ex.Message}");
                }
            }

            _output.WriteLine(new string('-', 90));
            _output.WriteLine($"\n========== 汇总 (交易数 胜率 总收益 盈利组数) ==========");
            _output.WriteLine($"Phoenix顺势  : {phxAligned}");
            _output.WriteLine($"Phoenix双向  : {phxBoth}");
            _output.WriteLine($"Donchian顺势 : {donAligned}");
            _output.WriteLine($"Donchian双向 : {donBoth}");
            _output.WriteLine($"\n逐品种对打唐奇安: 顺势 {beatAligned}胜/{compared - beatAligned - tieAligned}负/{tieAligned}平  双向 {beatBoth}胜/{compared - beatBoth}负");
        }

        // ==================== 测试5: 顺势模式参数调优 ====================

        [Fact]
        public void Phoenix_TrendAligned_Tuning()
        {
            _output.WriteLine("========== PhoenixReversal 顺势单向模式参数调优 ==========\n");

            var paramSets = new List<(string name, Dictionary<string, object> extra)>
            {
                ("默认v8", new Dictionary<string, object>()),
                ("RSI10", new Dictionary<string, object> { ["pulseRsiBuy"] = 10.0 }),
                ("允许加仓", new Dictionary<string, object> { ["allowAdds"] = 1 }),
                ("关制度检测", new Dictionary<string, object> { ["useRegime"] = 0 }),
                ("分4狙击", new Dictionary<string, object> { ["minScore"] = 4 }),
                ("胜率型出场", new Dictionary<string, object> { ["exitStyle"] = 0, ["atrMult"] = 2.2, ["maxStopPct"] = 4.0 }),
                ("关脉冲", new Dictionary<string, object> { ["pulseMode"] = 0 }),
            };

            var aggs = paramSets.ToDictionary(p => p.name, p => new Aggregate());
            var diags = paramSets.ToDictionary(p => p.name, p => new Dictionary<string, int>());

            foreach (var (name, rawSymbol) in AllSymbols)
            {
                var quotes = KlineCache.LoadFromCacheOnly(rawSymbol, Period.TIME_1D, 365);
                if (quotes == null || quotes.Count < 100) continue;

                int mode = quotes.Last().Close >= quotes.First().Close ? 1 : 2;
                var mktSymbol = RawToMkt(rawSymbol);

                foreach (var (pName, extra) in paramSets)
                {
                    var overrides = new Dictionary<string, object>(extra) { ["mode"] = mode };
                    try
                    {
                        var r = RunWithOverrides<PhoenixReversal>(mktSymbol, quotes, Period.TIME_1D, overrides, diags[pName]);
                        aggs[pName].Add(r);
                    }
                    catch { }
                }
            }

            _output.WriteLine($"{"参数组",-18} {"交易",6} {"胜率",7} {"总收益",12} {"盈利组数",8}");
            _output.WriteLine(new string('-', 64));
            foreach (var (pName, _) in paramSets)
            {
                _output.WriteLine($"{pName,-18} {aggs[pName]}");
            }

            _output.WriteLine("\n========== 诊断统计 ==========");
            foreach (var (pName, _) in paramSets)
            {
                var d = diags[pName];
                int V(string k) => d.TryGetValue(k, out var v) ? v : 0;
                double wr(int w, int l) => w + l > 0 ? 100.0 * w / (w + l) : 0;
                double avgHold = V("hold_n") > 0 ? (double)V("hold_sum") / V("hold_n") : 0;
                double exposure = V("bars_total") > 0 ? 100.0 * V("bars_inpos") / V("bars_total") : 0;
                _output.WriteLine($"{pName,-18} 狙击:{V("sniper_win")}胜/{V("sniper_loss")}负({wr(V("sniper_win"), V("sniper_loss")):F0}%) " +
                    $"脉冲:{V("pulse_win")}胜/{V("pulse_loss")}负({wr(V("pulse_win"), V("pulse_loss")):F0}%) 破位:{V("breach_entry")} 均持仓:{avgHold:F1}根 持仓占比:{exposure:F0}% | " +
                    $"止损:{V("exit_stop")} 时间:{V("exit_time")} 部分:{V("exit_partial")} 目标:{V("exit_target")}");
            }
        }

        // ==================== 测试6: 凤凰涅槃 vs 凤凰反转 ====================

        [Fact]
        public void Phoenix_Nirvana_Daily()
        {
            _output.WriteLine("========== 凤凰涅槃(全进全出)：价位评估器与延续次数对比 ==========\n");

            var variants = new (string name, Dictionary<string, object> extra)[]
            {
                ("默认5/5", new Dictionary<string, object>()),
                ("盈利要求3", new Dictionary<string, object> { ["profitTargetPct"] = 3.0 }),
                ("盈利要求8", new Dictionary<string, object> { ["profitTargetPct"] = 8.0 }),
                ("过热偏离8", new Dictionary<string, object> { ["devFullPct"] = 8.0 }),
            };
            var vAligned = variants.ToDictionary(v => v.name, _ => new Aggregate());
            var vBoth = variants.ToDictionary(v => v.name, _ => new Aggregate());

            var nirAligned = new Aggregate();
            var nirBoth = new Aggregate();
            var phxAligned = new Aggregate();
            var nirDiag = new Dictionary<string, int>();

            foreach (var (name, rawSymbol) in AllSymbols)
            {
                var quotes = KlineCache.LoadFromCacheOnly(rawSymbol, Period.TIME_1D, 365);
                if (quotes == null || quotes.Count < 100) continue;

                int mode = quotes.Last().Close >= quotes.First().Close ? 1 : 2;
                var mktSymbol = RawToMkt(rawSymbol);

                try
                {
                    foreach (var (vName, extra) in variants)
                    {
                        var aligned = new Dictionary<string, object>(extra) { ["mode"] = mode };
                        var sink = vName == "默认5/5" ? nirDiag : null;
                        var ra = RunWithOverrides<PhoenixNirvana>(mktSymbol, quotes, Period.TIME_1D, aligned, sink);
                        vAligned[vName].Add(ra);
                        vBoth[vName].Add(RunWithOverrides<PhoenixNirvana>(mktSymbol, quotes, Period.TIME_1D, extra));
                        if (vName == "默认5/5")
                        {
                            nirAligned.Add(ra);
                            if (ra.TotalProfit < -500)
                                _output.WriteLine($"[重亏品种] {name,-14} 收益:{ra.TotalProfit,10:F2} 回撤:{ra.MaxDrawdown,10:F2} 交易:{ra.TradeCount}");
                        }
                    }
                    phxAligned.Add(RunWithOverrides<PhoenixReversal>(mktSymbol, quotes, Period.TIME_1D,
                        new Dictionary<string, object> { ["mode"] = mode }));
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"{name,-16} ERROR: {ex.Message}");
                }
            }

            _output.WriteLine($"\n{"变体",-14} {"场景",-6} {"交易",6} {"胜率",7} {"总收益",12} {"盈利组数",8}");
            _output.WriteLine(new string('-', 62));
            foreach (var (vName, _) in variants)
            {
                _output.WriteLine($"{vName,-14} {"顺势",-6} {vAligned[vName]}");
                _output.WriteLine($"{vName,-14} {"双向",-6} {vBoth[vName]}");
            }

            // 15分钟周期复现（短周期敞口累积风险检查）
            _output.WriteLine("\n========== 15M 顺势逐品种 ==========");
            foreach (var (name, rawSymbol) in SubsetSymbols)
            {
                var quotes = KlineCache.LoadFromCacheOnly(rawSymbol, Period.TIME_15M, 4000);
                if (quotes == null || quotes.Count < 300) continue;
                int mode = quotes.Last().Close >= quotes.First().Close ? 1 : 2;
                var mktSymbol = RawToMkt(rawSymbol);
                try
                {
                    var r15 = RunWithOverrides<PhoenixNirvana>(mktSymbol, quotes, Period.TIME_15M,
                        new Dictionary<string, object> { ["mode"] = mode });
                    _output.WriteLine($"{name,-16} 收益:{r15.TotalProfit,12:F2} 回撤:{r15.MaxDrawdown,12:F2} 交易:{r15.TradeCount,5} 胜率:{r15.WinRate,5:F1}%");
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"{name,-16} ERROR: {ex.Message}");
                }
            }

            int V(string k) => nirDiag.TryGetValue(k, out var v) ? v : 0;
            double exposure = V("bars_total") > 0 ? 100.0 * V("bars_inpos") / V("bars_total") : 0;
            _output.WriteLine($"{"方案",-12} {"交易",6} {"胜率",7} {"总收益",12} {"盈利组数",8}");
            _output.WriteLine(new string('-', 60));
            _output.WriteLine($"{"涅槃顺势",-12} {nirAligned}");
            _output.WriteLine($"{"涅槃双向",-12} {nirBoth}");
            _output.WriteLine($"{"凤凰顺势",-12} {phxAligned}");
            _output.WriteLine($"\n涅槃诊断(顺势): 挂起:{V("hang")} 加仓延续:{V("renew")} 盈利加仓:{V("signal_add")} 强处落袋:{V("exit_dev_take")} 净额抵消:{V("net_reduce")} 持仓占比:{exposure:F0}%");
        }

        // ==================== 工具 ====================

        private class Aggregate
        {
            public int Trades;
            public int Wins;
            public decimal Profit;
            public int ProfitableSets;
            public int TotalSets;

            public void Add(BacktestResult r)
            {
                Trades += r.TradeCount;
                Wins += r.WinCount;
                Profit += r.TotalProfit;
                TotalSets++;
                if (r.TotalProfit > 0) ProfitableSets++;
            }

            public override string ToString()
            {
                var wr = Trades > 0 ? 100.0 * Wins / Trades : 0;
                return $"{Trades,6} {wr,6:F1}% {Profit,12:F2} {ProfitableSets + "/" + TotalSets,8}";
            }
        }

        private void PrintLine(string name, string stgName, BacktestResult r)
        {
            var marker = r.TotalProfit > 0 ? "+" : "";
            _output.WriteLine($"{name,-16} {stgName,-10} {r.TradeCount,5} {r.WinRate,6:F1}% {marker}{r.TotalProfit,11:F2} {r.MaxDrawdown,12:F2} {r.SharpeRatio,8:F4} {r.ProfitFactor,7:F2}");
        }

        private BacktestResult RunWithOverrides<T>(string mktSymbol, List<SkQuote> quotes, Period period,
            Dictionary<string, object> overrides, Dictionary<string, int> diagSink = null) where T : StgBase, new()
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

                if (diagSink != null)
                {
                    var diagMethod = stg.GetType().GetMethod("GetDiagCounts");
                    if (diagMethod?.Invoke(stg, null) is Dictionary<string, int> diagCounts)
                    {
                        foreach (var kv in diagCounts)
                            diagSink[kv.Key] = diagSink.TryGetValue(kv.Key, out var v) ? v + kv.Value : kv.Value;
                    }
                }

                return CalcResultExact(stg.GetType().Name, mktSymbol, quotes.Count, trades, period, quotes.Last().Close);
            }
            finally { cts.Cancel(); }
        }

        /// <summary>
        /// 精确盈亏计算：正确处理分批平仓（部分止盈后剩余仓位的盈亏也计入），
        /// 修正 BacktestEngine.CalcResult 首次平仓即清空持仓记录导致的统计偏差。
        /// 每次平仓事件计为一笔交易；期末未平仓头寸按最后收盘价计浮动盈亏（不计交易笔数）。
        /// </summary>
        private static BacktestResult CalcResultExact(string strategyName, string mktSymbol, int totalBars,
            List<stgInterface.RemoteTradeRecord> trades, Period period, decimal lastPrice)
        {
            var result = new BacktestResult
            {
                StrategyName = strategyName,
                Symbols = mktSymbol,
                PeriodName = period.ToString(),
                TotalBars = totalBars
            };

            // 双边分账（与服务端一致）：多空各自独立记账，支持同时持有
            decimal buyNum = 0, buyAvg = 0, sellNum = 0, sellAvg = 0;
            decimal equity = 0, peak = 0, maxDd = 0;
            var wins = new List<decimal>();
            var losses = new List<decimal>();

            foreach (var t in trades)
            {
                if (t.OT == OrderType.BUY)
                {
                    buyAvg = buyNum + t.Num > 0 ? (buyAvg * buyNum + t.Price * t.Num) / (buyNum + t.Num) : 0;
                    buyNum += t.Num;
                }
                else if (t.OT == OrderType.SELL)
                {
                    sellAvg = sellNum + t.Num > 0 ? (sellAvg * sellNum + t.Price * t.Num) / (sellNum + t.Num) : 0;
                    sellNum += t.Num;
                }
                else if (t.OT == OrderType.SELL_TO_COVER && buyNum > 0)
                {
                    var closeNum = Math.Min(t.Num, buyNum);
                    var pnl = (t.Price - buyAvg) * closeNum;
                    equity += pnl;
                    if (pnl > 0) wins.Add(pnl); else losses.Add(pnl);
                    buyNum -= closeNum;
                    if (buyNum == 0) buyAvg = 0;
                }
                else if (t.OT == OrderType.BUY_TO_COVER && sellNum > 0)
                {
                    var closeNum = Math.Min(t.Num, sellNum);
                    var pnl = (sellAvg - t.Price) * closeNum;
                    equity += pnl;
                    if (pnl > 0) wins.Add(pnl); else losses.Add(pnl);
                    sellNum -= closeNum;
                    if (sellNum == 0) sellAvg = 0;
                }

                if (equity > peak) peak = equity;
                var dd = peak - equity;
                if (dd > maxDd) maxDd = dd;
            }

            // 期末浮动盈亏（双边）
            if (buyNum > 0) equity += (lastPrice - buyAvg) * buyNum;
            if (sellNum > 0) equity += (sellAvg - lastPrice) * sellNum;
            if (equity > peak) peak = equity;
            if (peak - equity > maxDd) maxDd = peak - equity;

            result.TradeCount = wins.Count + losses.Count;
            result.WinCount = wins.Count;
            result.LossCount = losses.Count;
            result.TotalProfit = equity;
            result.MaxDrawdown = maxDd;
            result.AvgWin = wins.Count > 0 ? wins.Average() : 0;
            result.AvgLoss = losses.Count > 0 ? losses.Average() : 0;

            var allPnl = wins.Concat(losses).ToList();
            if (allPnl.Count > 1)
            {
                double mean = (double)allPnl.Average();
                double variance = allPnl.Sum(p => Math.Pow((double)p - mean, 2)) / (allPnl.Count - 1);
                double stdDev = Math.Sqrt(variance);
                result.SharpeRatio = stdDev > 0 ? (mean / stdDev) * Math.Sqrt(252) : 0;
            }

            return result;
        }
    }
}
