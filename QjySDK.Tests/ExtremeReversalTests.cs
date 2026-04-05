using Common;
using Model;
using QjySDK.Stg;
using Skender.Stock.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using static Model.EnumDef;

namespace QjySDK.Tests
{
    public class ExtremeReversalTests
    {
        private readonly ITestOutputHelper _output;

        public ExtremeReversalTests(ITestOutputHelper output)
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

        private BacktestResult RunTest(StgBase stg, string rawSymbol, Period period, int limit,
            Dictionary<string, object>? overrides = null)
        {
            var mktSymbol = RawToMkt(rawSymbol);
            var quotes = KlineCache.LoadKlines(rawSymbol, period, limit);
            _output.WriteLine($"[{stg.GetType().Name}] {rawSymbol} {period} 加载了 {quotes.Count} 根K线");

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

                var lastPrices = new Dictionary<string, decimal> { { mktSymbol, quotes.Last().Close } };
                var calcMethod = typeof(BacktestEngine).GetMethod("CalcResult",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var result = (BacktestResult)calcMethod!.Invoke(null,
                    new object[] { stg.GetType().Name, new[] { mktSymbol }, quotes.Count, trades, period, lastPrices })!;

                _output.WriteLine(result.ToReport());
                return result;
            }
            finally { cts.Cancel(); }
        }

        /// <summary>
        /// 基线测试：ETH 5M
        /// </summary>
        [Fact]
        public void Test_Baseline_ETH_5M()
        {
            var stg = new ExtremeReversal();
            var r = RunTest(stg, "COIN_FUTURES_ETHUSDT", Period.TIME_5M, 20000);
            _output.WriteLine($"\n>>> 交易:{r.TradeCount} 胜率:{r.WinRate:F1}% 收益:{r.TotalProfit:F2} 回撤:{r.MaxDrawdown:F2} 盈亏比:{r.ProfitFactor:F2}");
            Assert.True(r.TradeCount > 0, "应有交易");
        }

        /// <summary>
        /// 运行单次回测（复用quotes避免重复加载）
        /// </summary>
        private BacktestResult RunWithQuotes(List<SkQuote> quotes, string mktSymbol, Period period,
            Dictionary<string, object> overrides)
        {
            var stg = new ExtremeReversal();
            var cts = StgTestHelper.InitForTest(stg, mktSymbol);
            try
            {
                var argDicProp = typeof(StgBase).GetProperty("ArgDic",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var argDic = (Dictionary<string, object>)argDicProp!.GetValue(stg)!;
                foreach (var kv in overrides)
                    argDic[kv.Key] = kv.Value;

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

        /// <summary>
        /// 参数网格搜索：寻找60%+胜率的参数组合
        /// </summary>
        [Fact]
        public void Test_ParamGrid_ETH_5M()
        {
            var rawSymbol = "COIN_FUTURES_ETHUSDT";
            var period = Period.TIME_5M;
            var mktSymbol = RawToMkt(rawSymbol);
            var quotes = KlineCache.LoadKlines(rawSymbol, period, 20000);
            _output.WriteLine($"加载了 {quotes.Count} 根K线\n");

            var configs = new (int mc, int sb, int rsiLo, int rsiHi)[]
            {
                (3, 5, 40, 60),   // current default
                (4, 5, 40, 60),   // stricter filter
                (5, 5, 40, 60),   // very strict
                (3, 5, 35, 65),   // tighter RSI
                (4, 5, 35, 65),   // stricter + tighter RSI
            };

            var results = new List<(string desc, BacktestResult r)>();

            foreach (var (mc, sb, rsiLo, rsiHi) in configs)
            {
                var overrides = new Dictionary<string, object>
                {
                    ["minConfirm"] = mc,
                    ["signalBars"] = sb,
                    ["rsiOversold"] = rsiLo,
                    ["rsiOverbought"] = rsiHi
                };

                var r = RunWithQuotes(quotes, mktSymbol, period, overrides);
                var desc = $"MC={mc} SB={sb} RSI={rsiLo}/{rsiHi}";
                results.Add((desc, r));
            }

            _output.WriteLine($"{"参数",-22} {"交易",5} {"胜率",7} {"收益",10} {"回撤",10} {"盈亏比",7}");
            _output.WriteLine(new string('-', 70));
            foreach (var (desc, r) in results.OrderByDescending(x => x.r.WinRate).ThenByDescending(x => x.r.TotalProfit))
            {
                var marker = r.WinRate >= 60 ? " ★" : "";
                _output.WriteLine($"{desc,-22} {r.TradeCount,5} {r.WinRate,6:F1}% {r.TotalProfit,10:F2} {r.MaxDrawdown,10:F2} {r.ProfitFactor,7:F2}{marker}");
            }
        }
        /// <summary>
        /// 对比测试：同数据(COIN_FUTURES_ETHUSDT 20000条5M)下 MoEPredict vs ExtremeReversal
        /// </summary>
        [Fact]
        public void Test_Compare_MoEPredict_vs_ExtremeReversal()
        {
            var rawSymbol = "COIN_FUTURES_ETHUSDT";
            var period = Period.TIME_5M;
            var mktSymbol = RawToMkt(rawSymbol);
            var quotes = KlineCache.LoadKlines(rawSymbol, period, 20000);
            _output.WriteLine($"加载了 {quotes.Count} 根K线 ({rawSymbol} {period})\n");

            // MoEPredict
            var moe = new MoEPredict();
            var moeResult = BacktestEngine.RunSingleSymbol(moe, mktSymbol, quotes, period);

            // ExtremeReversal (默认参数)
            var er = new ExtremeReversal();
            var erResult = BacktestEngine.RunSingleSymbol(er, mktSymbol, quotes, period);

            _output.WriteLine($"{"策略",-22} {"交易",5} {"胜率",7} {"收益",10} {"回撤",10} {"盈亏比",7} {"夏普",7}");
            _output.WriteLine(new string('-', 75));
            _output.WriteLine($"{"MoEPredict",-22} {moeResult.TradeCount,5} {moeResult.WinRate,6:F1}% {moeResult.TotalProfit,10:F2} {moeResult.MaxDrawdown,10:F2} {moeResult.ProfitFactor,7:F2} {moeResult.SharpeRatio,7:F2}");
            _output.WriteLine($"{"ExtremeReversal",-22} {erResult.TradeCount,5} {erResult.WinRate,6:F1}% {erResult.TotalProfit,10:F2} {erResult.MaxDrawdown,10:F2} {erResult.ProfitFactor,7:F2} {erResult.SharpeRatio,7:F2}");
        }

        /// <summary>
        /// 诊断测试：衡量每个条件单独的预测力
        /// </summary>
        [Fact]
        public void Test_Diagnostic_ConditionWinRates()
        {
            var quotes = KlineCache.LoadKlines("COIN_FUTURES_ETHUSDT", Period.TIME_5M, 20000);
            _output.WriteLine($"加载了 {quotes.Count} 根K线\n");

            // 预计算指标
            var rsiList = quotes.GetRsi(14).ToList();
            var stochList = quotes.GetStoch(14, 3, 3).ToList();
            var macdList = quotes.GetMacd(12, 26, 9).ToList();
            var bollList = quotes.GetBollingerBands(10, 2).ToList();

            // 每个条件: (name, buyCondition, sellCondition)
            // 统计: 条件触发时，下一根K线是否朝预测方向走
            var condNames = new[] { "D1_Shadow60", "D2_Consec3Rev", "D2b_Consec3Mom", "D3_RSI30/70", "D3b_RSI40/60",
                "D4_StochK20/80", "D5_MACD_Rev", "D6_VolDir_Rev", "D6b_VolDir_Mom", "D7_BB10_2" };
            var buyWin = new int[condNames.Length];
            var buyTotal = new int[condNames.Length];
            var sellWin = new int[condNames.Length];
            var sellTotal = new int[condNames.Length];

            for (int i = 30; i < quotes.Count - 1; i++)
            {
                var q = quotes[i];
                var next = quotes[i + 1];
                bool nextUp = next.Close > q.Close;
                bool nextDown = next.Close < q.Close;
                decimal range = q.High - q.Low;
                bool curBarUp = q.Close > q.Open;

                // D1: Shadow > 60% range
                if (range > 0)
                {
                    decimal lw = Math.Min(q.Open, q.Close) - q.Low;
                    decimal uw = q.High - Math.Max(q.Open, q.Close);
                    if (lw > range * 0.6m) { buyTotal[0]++; if (nextUp) buyWin[0]++; }
                    if (uw > range * 0.6m) { sellTotal[0]++; if (nextDown) sellWin[0]++; }
                }

                // D2: Consec 3 bars reversal (down→buy, up→sell)
                int cu = 0, cd = 0;
                for (int ci = i; ci >= 1 && ci >= i - 10; ci--)
                {
                    if (quotes[ci].Close > quotes[ci - 1].Close) { if (cd > 0) break; cu++; }
                    else if (quotes[ci].Close < quotes[ci - 1].Close) { if (cu > 0) break; cd++; }
                    else break;
                }
                if (cd >= 3) { buyTotal[1]++; if (nextUp) buyWin[1]++; }
                if (cu >= 3) { sellTotal[1]++; if (nextDown) sellWin[1]++; }

                // D2b: Consec 3 bars momentum (up→buy, down→sell)
                if (cu >= 3) { buyTotal[2]++; if (nextUp) buyWin[2]++; }
                if (cd >= 3) { sellTotal[2]++; if (nextDown) sellWin[2]++; }

                // D3: RSI < 30 buy, > 70 sell
                var rsi = rsiList[i].Rsi;
                if (rsi.HasValue)
                {
                    if (rsi.Value < 30) { buyTotal[3]++; if (nextUp) buyWin[3]++; }
                    if (rsi.Value > 70) { sellTotal[3]++; if (nextDown) sellWin[3]++; }
                }

                // D3b: RSI < 40 buy, > 60 sell (looser)
                if (rsi.HasValue)
                {
                    if (rsi.Value < 40) { buyTotal[4]++; if (nextUp) buyWin[4]++; }
                    if (rsi.Value > 60) { sellTotal[4]++; if (nextDown) sellWin[4]++; }
                }

                // D4: StochK < 20 buy, > 80 sell
                var sk = stochList[i].K;
                if (sk.HasValue)
                {
                    if (sk.Value < 20) { buyTotal[5]++; if (nextUp) buyWin[5]++; }
                    if (sk.Value > 80) { sellTotal[5]++; if (nextDown) sellWin[5]++; }
                }

                // D5: MACD histogram reversal
                if (i >= 1 && macdList[i].Histogram.HasValue && macdList[i - 1].Histogram.HasValue)
                {
                    if (macdList[i].Histogram.Value > macdList[i - 1].Histogram.Value)
                    { buyTotal[6]++; if (nextUp) buyWin[6]++; }
                    if (macdList[i].Histogram.Value < macdList[i - 1].Histogram.Value)
                    { sellTotal[6]++; if (nextDown) sellWin[6]++; }
                }

                // D6: Vol spike + reversal direction
                if (i >= 20)
                {
                    decimal avgVol = 0;
                    for (int vi = i - 19; vi <= i; vi++) avgVol += quotes[vi].Volume;
                    avgVol /= 20;
                    if (avgVol > 0 && q.Volume > avgVol * 1.5m)
                    {
                        if (!curBarUp) { buyTotal[7]++; if (nextUp) buyWin[7]++; }
                        if (curBarUp) { sellTotal[7]++; if (nextDown) sellWin[7]++; }
                    }
                }

                // D6b: Vol spike + momentum direction
                if (i >= 20)
                {
                    decimal avgVol = 0;
                    for (int vi = i - 19; vi <= i; vi++) avgVol += quotes[vi].Volume;
                    avgVol /= 20;
                    if (avgVol > 0 && q.Volume > avgVol * 1.5m)
                    {
                        if (curBarUp) { buyTotal[8]++; if (nextUp) buyWin[8]++; }
                        if (!curBarUp) { sellTotal[8]++; if (nextDown) sellWin[8]++; }
                    }
                }

                // D7: BB breakout
                if (bollList[i].LowerBand.HasValue && bollList[i].UpperBand.HasValue)
                {
                    if ((double)q.Close < bollList[i].LowerBand.Value)
                    { buyTotal[9]++; if (nextUp) buyWin[9]++; }
                    if ((double)q.Close > bollList[i].UpperBand.Value)
                    { sellTotal[9]++; if (nextDown) sellWin[9]++; }
                }
            }

            _output.WriteLine($"{"条件",-20} {"买入触发",7} {"买入胜率",8} {"卖出触发",7} {"卖出胜率",8} {"总触发",6} {"综合胜率",8}");
            _output.WriteLine(new string('-', 80));
            for (int c = 0; c < condNames.Length; c++)
            {
                double bWr = buyTotal[c] > 0 ? 100.0 * buyWin[c] / buyTotal[c] : 0;
                double sWr = sellTotal[c] > 0 ? 100.0 * sellWin[c] / sellTotal[c] : 0;
                int totalT = buyTotal[c] + sellTotal[c];
                int totalW = buyWin[c] + sellWin[c];
                double totalWr = totalT > 0 ? 100.0 * totalW / totalT : 0;
                var marker = totalWr > 52 ? " ★" : "";
                _output.WriteLine($"{condNames[c],-20} {buyTotal[c],7} {bWr,7:F1}% {sellTotal[c],7} {sWr,7:F1}% {totalT,6} {totalWr,7:F1}%{marker}");
            }
        }
    }
}
