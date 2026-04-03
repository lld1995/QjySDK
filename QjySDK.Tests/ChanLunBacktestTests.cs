using Common;
using Model;
using QjySDK.Stg;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using static Model.EnumDef;

namespace QjySDK.Tests
{
    public class ChanLunBacktestTests
    {
        private readonly ITestOutputHelper _output;

        public ChanLunBacktestTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// rawSymbol格式: COIN_FUTURES_BTCUSDT → mktSymbol: FUTURES_BTCUSDT
        /// </summary>
        private static string RawToMkt(string rawSymbol)
        {
            var strs = rawSymbol.Split(new char[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (strs.Length >= 3)
                return strs[1] + "_" + string.Join("_", strs, 2, strs.Length - 2);
            return rawSymbol;
        }

        private void SkipIfNoData(string rawSymbol, Period period, int minBars)
        {
            if (!TDEngineDataLoader.IsAvailable())
                Assert.Fail("[SKIP] TDEngine 不可用");

            if (!TDEngineDataLoader.HasData(rawSymbol, period, minBars))
                Assert.Fail($"[SKIP] 标的 {rawSymbol} 周期 {period} 数据不足 {minBars} 条");
        }

        private void OutputResult(BacktestResult r)
        {
            _output.WriteLine(r.ToReport());
        }

        /// <summary>
        /// 运行单个缠论策略回测
        /// </summary>
        private BacktestResult RunChanLunTest(StgBase stg, string rawSymbol, Period period, int limit)
        {
            var mktSymbol = RawToMkt(rawSymbol);
            var quotes = TDEngineDataLoader.LoadKlines(rawSymbol, period, limit);
            _output.WriteLine($"[{stg.GetType().Name}] {rawSymbol} {period} 加载了 {quotes.Count} 根K线");
            var result = BacktestEngine.RunSingleSymbol(stg, mktSymbol, quotes, period);
            OutputResult(result);
            return result;
        }

        // ==================== 测试标的和参数 ====================
        private static readonly string[] RawSymbols = new[]
        {
            "COIN_FUTURES_BTCUSDT",
            "COIN_FUTURES_ETHUSDT",
            "COIN_FUTURES_XAUUSDT",
            "STOCK_SPOT_SZSE.000001",
            "STOCK_SPOT_SHSE.510300",
            "FUTURES_FUTURES_SHFE.au2605",
            "FUTURES_FUTURES_DCE.c2605"
        };

        #region ==================== ChanLun (线段级别) 1D ====================

        [Theory]
        [InlineData("COIN_FUTURES_BTCUSDT", 365)]
        [InlineData("COIN_FUTURES_ETHUSDT", 365)]
        [InlineData("COIN_FUTURES_XAUUSDT", 365)]
        [InlineData("STOCK_SPOT_SZSE.000001", 365)]
        [InlineData("STOCK_SPOT_SHSE.510300", 365)]
        [InlineData("FUTURES_FUTURES_SHFE.au2605", 365)]
        [InlineData("FUTURES_FUTURES_DCE.c2605", 365)]
        public void Test_ChanLun_1D(string rawSymbol, int limit)
        {
            SkipIfNoData(rawSymbol, Period.TIME_1D, 60);
            var stg = new ChanLun();
            var result = RunChanLunTest(stg, rawSymbol, Period.TIME_1D, limit);
            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        #endregion

        #region ==================== ChanLun (线段级别) 15M ====================

        [Theory]
        [InlineData("COIN_FUTURES_BTCUSDT", 8640)]
        [InlineData("COIN_FUTURES_ETHUSDT", 8640)]
        [InlineData("COIN_FUTURES_XAUUSDT", 8640)]
        [InlineData("STOCK_SPOT_SZSE.000001", 8640)]
        [InlineData("STOCK_SPOT_SHSE.510300", 8640)]
        [InlineData("FUTURES_FUTURES_SHFE.au2605", 8640)]
        [InlineData("FUTURES_FUTURES_DCE.c2605", 8640)]
        public void Test_ChanLun_15M(string rawSymbol, int limit)
        {
            SkipIfNoData(rawSymbol, Period.TIME_15M, 60);
            var stg = new ChanLun();
            var result = RunChanLunTest(stg, rawSymbol, Period.TIME_15M, limit);
            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        #endregion

        #region ==================== ChanLunBi (笔级别) 1D ====================

        [Theory]
        [InlineData("COIN_FUTURES_BTCUSDT", 365)]
        [InlineData("COIN_FUTURES_ETHUSDT", 365)]
        [InlineData("COIN_FUTURES_XAUUSDT", 365)]
        [InlineData("STOCK_SPOT_SZSE.000001", 365)]
        [InlineData("STOCK_SPOT_SHSE.510300", 365)]
        [InlineData("FUTURES_FUTURES_SHFE.au2605", 365)]
        [InlineData("FUTURES_FUTURES_DCE.c2605", 365)]
        public void Test_ChanLunBi_1D(string rawSymbol, int limit)
        {
            SkipIfNoData(rawSymbol, Period.TIME_1D, 60);
            var stg = new ChanLunBi();
            var result = RunChanLunTest(stg, rawSymbol, Period.TIME_1D, limit);
            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        #endregion

        #region ==================== ChanLunBi (笔级别) 15M ====================

        [Theory]
        [InlineData("COIN_FUTURES_BTCUSDT", 8640)]
        [InlineData("COIN_FUTURES_ETHUSDT", 8640)]
        [InlineData("COIN_FUTURES_XAUUSDT", 8640)]
        [InlineData("STOCK_SPOT_SZSE.000001", 8640)]
        [InlineData("STOCK_SPOT_SHSE.510300", 8640)]
        [InlineData("FUTURES_FUTURES_SHFE.au2605", 8640)]
        [InlineData("FUTURES_FUTURES_DCE.c2605", 8640)]
        public void Test_ChanLunBi_15M(string rawSymbol, int limit)
        {
            SkipIfNoData(rawSymbol, Period.TIME_15M, 60);
            var stg = new ChanLunBi();
            var result = RunChanLunTest(stg, rawSymbol, Period.TIME_15M, limit);
            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        #endregion

        #region ==================== 综合基准测试 ====================

        [Fact]
        public void Test_ChanLun_Baseline_All()
        {
            if (!TDEngineDataLoader.IsAvailable())
            {
                Assert.Fail("[SKIP] TDEngine 不可用");
                return;
            }

            _output.WriteLine("========== 缠论策略基准测试 ==========");
            _output.WriteLine("");

            var periods = new[] { Period.TIME_1D, Period.TIME_15M };
            var limits = new Dictionary<Period, int>
            {
                { Period.TIME_1D, 365 },
                { Period.TIME_15M, 8640 }
            };

            foreach (var rawSymbol in RawSymbols)
            {
                foreach (var period in periods)
                {
                    if (!TDEngineDataLoader.HasData(rawSymbol, period, 60))
                    {
                        _output.WriteLine($"[SKIP] {rawSymbol} {period} 数据不足");
                        continue;
                    }

                    // ChanLun (线段级别)
                    try
                    {
                        var stg1 = new ChanLun();
                        RunChanLunTest(stg1, rawSymbol, period, limits[period]);
                    }
                    catch (Exception ex)
                    {
                        _output.WriteLine($"[ERROR] ChanLun {rawSymbol} {period}: {ex.Message}");
                    }

                    // ChanLunBi (笔级别)
                    try
                    {
                        var stg2 = new ChanLunBi();
                        RunChanLunTest(stg2, rawSymbol, period, limits[period]);
                    }
                    catch (Exception ex)
                    {
                        _output.WriteLine($"[ERROR] ChanLunBi {rawSymbol} {period}: {ex.Message}");
                    }
                }
            }
        }

        #endregion

        #region ==================== V0 vs V2 对比测试 ====================

        /// <summary>
        /// 运行回测，可选覆盖参数（在InitForTest之后覆盖ArgDic）
        /// </summary>
        private BacktestResult RunWithOverrides(StgBase stg, string mktSymbol, List<SkQuote> quotes, Period period,
            Dictionary<string, object> overrides = null)
        {
            var cts = StgTestHelper.InitForTest(stg, mktSymbol);
            try
            {
                // InitForTest已经调用GetStgDesc设置了ArgDic，现在覆盖参数
                if (overrides != null)
                {
                    var argDicProp = typeof(StgBase).GetProperty("ArgDic",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var argDic = (Dictionary<string, object>)argDicProp.GetValue(stg);
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
                    catch (Exception ex) { Console.WriteLine($"[OnBar] Bar {i} exception: {ex.Message}"); }
                    trades.AddRange(StgTestHelper.DrainTrades(stg));
                }

                var calcMethod = typeof(BacktestEngine).GetMethod("CalcResult",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var result = (BacktestResult)calcMethod.Invoke(null,
                    new object[] { stg.GetType().Name, new[] { mktSymbol }, quotes.Count, trades, period });

                Dictionary<string, int> bsCounts = null;
                if (stg is ChanLun cl) bsCounts = cl.GetBSPointCounts();
                else if (stg is ChanLunBi clb) bsCounts = clb.GetBSPointCounts();
                if (bsCounts != null)
                {
                    result.Buy1Count = bsCounts.GetValueOrDefault("Buy1");
                    result.Sell1Count = bsCounts.GetValueOrDefault("Sell1");
                    result.Buy2Count = bsCounts.GetValueOrDefault("Buy2");
                    result.Sell2Count = bsCounts.GetValueOrDefault("Sell2");
                    result.Buy3Count = bsCounts.GetValueOrDefault("Buy3");
                    result.Sell3Count = bsCounts.GetValueOrDefault("Sell3");
                }
                return result;
            }
            finally { cts.Cancel(); }
        }

        /// <summary>
        /// 在同一数据上对比 V0（无优化）和 V2（当前）的回测结果
        /// V0: 关闭止损、移动止损、信号过期、中枢回归平仓、放开中枢偏离限制
        /// V2: 当前代码默认参数
        /// </summary>
        [Fact]
        public void Test_ChanLun_V0_vs_V2()
        {
            if (!TDEngineDataLoader.IsAvailable())
            {
                Assert.Fail("[SKIP] TDEngine 不可用");
                return;
            }

            // V0参数覆盖：关闭所有V2优化
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

            var periods = new[] { Period.TIME_1D, Period.TIME_15M };
            var limits = new Dictionary<Period, int>
            {
                { Period.TIME_1D, 365 },
                { Period.TIME_15M, 8640 }
            };

            _output.WriteLine("========== V0(无优化) vs V2(当前) 对比 ==========");

            foreach (var rawSymbol in RawSymbols)
            {
                foreach (var period in periods)
                {
                    if (!TDEngineDataLoader.HasData(rawSymbol, period, 60))
                    {
                        _output.WriteLine($"[SKIP] {rawSymbol} {period} 数据不足");
                        continue;
                    }

                    var mktSymbol = RawToMkt(rawSymbol);
                    var quotes = TDEngineDataLoader.LoadKlines(rawSymbol, period, limits[period]);
                    _output.WriteLine($"\n[DATA] {rawSymbol} {period} K线数:{quotes.Count}");

                    foreach (var stgType in new[] { "ChanLun", "ChanLunBi" })
                    {
                        // V0
                        try
                        {
                            StgBase stgV0 = stgType == "ChanLun" ? (StgBase)new ChanLun() : new ChanLunBi();
                            var r0 = RunWithOverrides(stgV0, mktSymbol, quotes, period, v0Overrides);
                            _output.WriteLine($"  [V0-{stgType}] 交易:{r0.TradeCount} 胜率:{r0.WinRate:F1}% 收益:{r0.TotalProfit:F2} 回撤:{r0.MaxDrawdown:F2} 夏普:{r0.SharpeRatio:F4} 盈亏比:{r0.ProfitFactor:F2}");
                        }
                        catch (Exception ex)
                        {
                            _output.WriteLine($"  [ERROR] V0-{stgType}: {ex.Message}");
                        }

                        // V2
                        try
                        {
                            StgBase stgV2 = stgType == "ChanLun" ? (StgBase)new ChanLun() : new ChanLunBi();
                            var r2 = RunWithOverrides(stgV2, mktSymbol, quotes, period);
                            _output.WriteLine($"  [V2-{stgType}] 交易:{r2.TradeCount} 胜率:{r2.WinRate:F1}% 收益:{r2.TotalProfit:F2} 回撤:{r2.MaxDrawdown:F2} 夏普:{r2.SharpeRatio:F4} 盈亏比:{r2.ProfitFactor:F2}");
                        }
                        catch (Exception ex)
                        {
                            _output.WriteLine($"  [ERROR] V2-{stgType}: {ex.Message}");
                        }
                    }
                }
            }
        }

        #endregion

        #region ==================== 参数网格搜索 ====================

        /// <summary>
        /// 网格搜索：测试多组参数，找到每组都优于V0的最佳参数
        /// </summary>
        [Fact]
        public void Test_ChanLun_ParamGrid()
        {
            if (!TDEngineDataLoader.IsAvailable())
            {
                Assert.Fail("[SKIP] TDEngine 不可用");
                return;
            }

            var periods = new[] { Period.TIME_15M };
            var limits = new Dictionary<Period, int> { { Period.TIME_15M, 8640 } };

            // 只测有交易的品种（从V0 vs V2对比中筛选）
            var testSymbols = new[] {
                "COIN_FUTURES_BTCUSDT", "COIN_FUTURES_ETHUSDT", "COIN_FUTURES_XAUUSDT",
                "STOCK_SPOT_SZSE.000001", "STOCK_SPOT_SHSE.510300", "FUTURES_FUTURES_DCE.c2605"
            };

            // V0: 无优化基准（带5%硬止损，统一基准）
            var v0Params = new Dictionary<string, object>
            {
                ["useStopLoss"] = 1, ["stopLossPercent"] = 5.0m,
                ["useTrailingStop"] = 0, ["signalExpiryBars"] = 0,
                ["useZhongShuExit"] = 0, ["maxZhongShuDeviation"] = 9999.0m,
                ["tradeCooldownBars"] = 0, ["noReversalOnBuy3Sell3"] = 0, ["zhongShuExitScope"] = 0
            };

            // 参数候选组合（ChanLun用null即当前默认，ChanLunBi测试多组参数）
            var paramSetsChanLun = new List<(string name, Dictionary<string, object> p)>
            {
                ("V2-cur", null),
            };
            var paramSetsChanLunBi = new List<(string name, Dictionary<string, object> p)>
            {
                ("V2-cur", null),  // 当前默认（5%止损，无其他优化）
                ("Bi-Dev20", new Dictionary<string, object> { ["maxZhongShuDeviation"] = 20.0m }),
                ("Bi-Cool5", new Dictionary<string, object> { ["tradeCooldownBars"] = 5 }),
                ("Bi-Cool10", new Dictionary<string, object> { ["tradeCooldownBars"] = 10 }),
                ("Bi-Exp20", new Dictionary<string, object> { ["signalExpiryBars"] = 20 }),
                ("Bi-Exp30", new Dictionary<string, object> { ["signalExpiryBars"] = 30 }),
                ("Bi-D20C5", new Dictionary<string, object> { ["maxZhongShuDeviation"] = 20.0m, ["tradeCooldownBars"] = 5 }),
            };

            // 收集结果: key = "symbol|period|stgType", value = list of (paramName, result)
            var allResults = new Dictionary<string, List<(string name, BacktestResult r)>>();

            foreach (var rawSymbol in testSymbols)
            {
                foreach (var period in periods)
                {
                    if (!TDEngineDataLoader.HasData(rawSymbol, period, 60)) continue;
                    var mktSymbol = RawToMkt(rawSymbol);
                    var quotes = TDEngineDataLoader.LoadKlines(rawSymbol, period, limits[period]);
                    if (quotes.Count < 200) continue;

                    foreach (var stgType in new[] { "ChanLun", "ChanLunBi" })
                    {
                        var key = $"{rawSymbol}|{period}|{stgType}";

                        // V0
                        StgBase stg0 = stgType == "ChanLun" ? (StgBase)new ChanLun() : new ChanLunBi();
                        var r0 = RunWithOverrides(stg0, mktSymbol, quotes, period, v0Params);
                        var list = new List<(string, BacktestResult)> { ("V0", r0) };

                        // 各参数组（ChanLun和ChanLunBi用不同参数集）
                        var paramSets = stgType == "ChanLun" ? paramSetsChanLun : paramSetsChanLunBi;
                        foreach (var (pName, pDic) in paramSets)
                        {
                            StgBase stg = stgType == "ChanLun" ? (StgBase)new ChanLun() : new ChanLunBi();
                            var r = RunWithOverrides(stg, mktSymbol, quotes, period, pDic);
                            list.Add((pName, r));
                        }
                        allResults[key] = list;
                    }
                }
            }

            // 输出结果表
            _output.WriteLine("========== 参数网格搜索结果 ==========\n");
            foreach (var kv in allResults)
            {
                var parts = kv.Key.Split('|');
                if (kv.Value[0].r.TradeCount == 0 && kv.Value.All(x => x.r.TradeCount == 0)) continue;
                _output.WriteLine($"--- {parts[0]} {parts[1]} {parts[2]} ---");
                foreach (var (name, r) in kv.Value)
                {
                    _output.WriteLine($"  {name,-20} 交易:{r.TradeCount,3} 胜率:{r.WinRate,5:F1}% 收益:{r.TotalProfit,10:F2} 回撤:{r.MaxDrawdown,10:F2} 夏普:{r.SharpeRatio,8:F4} 盈亏比:{r.ProfitFactor,5:F2}");
                }
                _output.WriteLine("");
            }

            // 统计每个参数组 vs V0 的胜负
            _output.WriteLine("\n========== 参数组 vs V0 胜负统计 ==========");
            var allParamNames = allResults.Values.SelectMany(v => v.Select(x => x.name)).Where(n => n != "V0").Distinct().ToList();
            foreach (var pName in allParamNames)
            {
                int wins = 0, losses = 0, ties = 0;
                foreach (var kv in allResults)
                {
                    var v0r = kv.Value[0].r;
                    var match = kv.Value.FirstOrDefault(x => x.name == pName);
                    if (match.name == null) continue;
                    if (v0r.TradeCount == 0 && match.r.TradeCount == 0) continue;
                    if (match.r.TotalProfit > v0r.TotalProfit) wins++;
                    else if (match.r.TotalProfit < v0r.TotalProfit) losses++;
                    else ties++;
                }
                _output.WriteLine($"  {pName,-20} 胜V0:{wins} 负V0:{losses} 平:{ties}");
            }
        }

        #endregion

        #region ==================== 诊断测试 ====================

        [Fact]
        public void Test_Diagnose_ZhongShu_Structure()
        {
            if (!TDEngineDataLoader.IsAvailable())
            {
                Assert.Fail("[SKIP] TDEngine 不可用");
                return;
            }

            var testCases = new[]
            {
                ("COIN_FUTURES_BTCUSDT", Period.TIME_1D, 365),
                ("COIN_FUTURES_BTCUSDT", Period.TIME_15M, 8640),
                ("STOCK_SPOT_SZSE.000001", Period.TIME_1D, 365),
            };

            foreach (var (rawSymbol, period, limit) in testCases)
            {
                if (!TDEngineDataLoader.HasData(rawSymbol, period, 60))
                {
                    _output.WriteLine($"[SKIP] {rawSymbol} {period}");
                    continue;
                }

                _output.WriteLine($"\n===== ChanLunBi {rawSymbol} {period} =====");
                var stg = new ChanLunBi();
                var mktSymbol = RawToMkt(rawSymbol);
                var quotes = TDEngineDataLoader.LoadKlines(rawSymbol, period, limit);
                _output.WriteLine($"  Loaded {quotes.Count} bars");
                var result = BacktestEngine.RunSingleSymbol(stg, mktSymbol, quotes, period);

                var stateDicField = typeof(ChanLunBi).GetField("_stateDic",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var stateDict = stateDicField?.GetValue(stg);
                if (stateDict is System.Collections.IDictionary dict)
                {
                    foreach (System.Collections.DictionaryEntry entry in dict)
                    {
                        var state = entry.Value;
                        var zsList = state.GetType().GetProperty("ZhongShus")?.GetValue(state) as System.Collections.IList;

                        _output.WriteLine($"  Key: {entry.Key}, ZhongShus count: {zsList?.Count ?? 0}");
                        if (zsList == null || zsList.Count == 0) continue;

                        for (int i = 0; i < zsList.Count; i++)
                        {
                            var zs = zsList[i];
                            var t = zs.GetType();
                            var zg = t.GetProperty("ZG")?.GetValue(zs);
                            var zd = t.GetProperty("ZD")?.GetValue(zs);
                            var gg = t.GetProperty("GG")?.GetValue(zs);
                            var dd = t.GetProperty("DD")?.GetValue(zs);
                            var leaveDir = t.GetProperty("LeaveDirection")?.GetValue(zs);
                            var leaveStroke = t.GetProperty("LeaveStroke")?.GetValue(zs);
                            decimal zgV = (decimal)(zg ?? 0m);
                            decimal zdV = (decimal)(zd ?? 0m);
                            decimal center = (zgV + zdV) / 2;
                            _output.WriteLine($"    ZS[{i}]: ZG={zg} ZD={zd} GG={gg} DD={dd} Center={center:F2} LeaveDir={leaveDir} HasLeave={leaveStroke != null}");
                        }

                        for (int i = zsList.Count - 1; i >= 1; i--)
                        {
                            var curr = zsList[i];
                            var prev = zsList[i - 1];
                            var ct = curr.GetType();
                            decimal cZG = (decimal)(ct.GetProperty("ZG")?.GetValue(curr) ?? 0m);
                            decimal cZD = (decimal)(ct.GetProperty("ZD")?.GetValue(curr) ?? 0m);
                            decimal cGG = (decimal)(ct.GetProperty("GG")?.GetValue(curr) ?? 0m);
                            decimal cDD = (decimal)(ct.GetProperty("DD")?.GetValue(curr) ?? 0m);
                            decimal pZG = (decimal)(ct.GetProperty("ZG")?.GetValue(prev) ?? 0m);
                            decimal pZD = (decimal)(ct.GetProperty("ZD")?.GetValue(prev) ?? 0m);
                            decimal pGG = (decimal)(ct.GetProperty("GG")?.GetValue(prev) ?? 0m);
                            decimal pDD = (decimal)(ct.GetProperty("DD")?.GetValue(prev) ?? 0m);
                            decimal cCenter = (cZG + cZD) / 2;
                            decimal pCenter = (pZG + pZD) / 2;
                            bool strictDown = cZG < pZD;
                            bool strictUp = cZD > pZG;
                            bool relaxDown = cCenter < pCenter && cGG < pGG && cDD < pDD;
                            bool relaxUp = cCenter > pCenter && cGG > pGG && cDD > pDD;
                            _output.WriteLine($"    Pair[{i - 1},{i}]: strictDown={strictDown} strictUp={strictUp} relaxDown={relaxDown} relaxUp={relaxUp}");
                        }
                    }
                }
            }
        }

        #endregion
    }
}
