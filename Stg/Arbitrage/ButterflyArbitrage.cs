using Common;
using Model;
using Skender.Stock.Indicators;
using stgInterface;
using System;
using System.Collections.Generic;
using System.Linq;
using static Model.EnumDef;

namespace QjySDK.Stg
{
    /// <summary>
    /// 蝶式套利策略 (Butterfly Spread Arbitrage)
    /// 
    /// 策略核心思想：
    /// 在三个相关品种(A、B、C)之间构建蝶式价差：
    ///   ButterflySpread = Price_A + Price_C - 2 * Price_B
    /// 其中B是"中间腿"(通常是A和C的中间品种或均值参考)。
    /// 
    /// 当蝶式价差偏离历史均值时做均值回归：
    /// - 价差偏高 → 卖A卖C + 买2份B
    /// - 价差偏低 → 买A买C + 卖2份B
    /// 价差回归时平仓获利。
    /// 
    /// 适用场景：
    /// - 同板块三个品种(如三个钢铁股、三个原油品种)
    /// - 期货不同到期月(近月-中月-远月)
    /// - 任意三个高相关品种
    /// 
    /// OnGlobalIndicator中的全局计算：
    /// 1. 收集所有品种价格数据
    /// 2. 自动寻找最优三品种组合（相关性最高、价差均值回归性最好）
    /// 3. 计算蝶式价差序列、Z-Score、半衰期
    /// 
    /// OnBar中的交易逻辑：
    /// 1. 根据所属三元组的蝶式价差Z-Score决定方向
    /// 2. 两端品种同方向、中间品种反方向、中间品种数量为两端的2倍
    /// 3. Z-Score回归或止损时同时平仓三腿
    /// </summary>
    public class ButterflyArbitrage : StgBase
    {
        public ButterflyArbitrage()
        {
        }

        public ButterflyArbitrage(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 2;
            sd.UseGlobalCalc = 1;

            // ==================== 蝶式价差参数 ====================
            sd.ArgDescDic["lookbackPeriod"] = new ArgDesc { Text = "回溯周期", Explain = "计算蝶式价差均值和标准差的滚动窗口" };
            sd.ArgDic["lookbackPeriod"] = 60;

            sd.ArgDescDic["spreadMode"] = new ArgDesc { Text = "价差模式", Explain = "0=原始价格差 1=标准化(除以B价格)" };
            sd.ArgDic["spreadMode"] = 1;

            sd.ArgDescDic["selectionMode"] = new ArgDesc { Text = "组合选择模式", Explain = "0=自动选最优三元组 1=按品种顺序固定分组(每3个一组)" };
            sd.ArgDic["selectionMode"] = 0;

            // ==================== Z-Score阈值 ====================
            sd.ArgDescDic["entryZScore"] = new ArgDesc { Text = "入场Z-Score", Explain = "蝶式价差Z-Score绝对值超过此值入场" };
            sd.ArgDic["entryZScore"] = 2.0;

            sd.ArgDescDic["exitZScore"] = new ArgDesc { Text = "出场Z-Score", Explain = "蝶式价差Z-Score绝对值低于此值出场" };
            sd.ArgDic["exitZScore"] = 0.5;

            sd.ArgDescDic["stopLossZScore"] = new ArgDesc { Text = "止损Z-Score", Explain = "蝶式价差Z-Score绝对值超过此值止损" };
            sd.ArgDic["stopLossZScore"] = 2.5;

            // ==================== 过滤参数 ====================
            sd.ArgDescDic["minCorrelation"] = new ArgDesc { Text = "最小相关系数", Explain = "三品种两两相关系数最小值" };
            sd.ArgDic["minCorrelation"] = 0.6;

            sd.ArgDescDic["useHalfLifeFilter"] = new ArgDesc { Text = "半衰期过滤", Explain = "1=启用 0=禁用" };
            sd.ArgDic["useHalfLifeFilter"] = 1;

            sd.ArgDescDic["minHalfLife"] = new ArgDesc { Text = "最小半衰期", Explain = "半衰期低于此值不入场" };
            sd.ArgDic["minHalfLife"] = 5;

            sd.ArgDescDic["maxHalfLife"] = new ArgDesc { Text = "最大半衰期", Explain = "半衰期高于此值不入场" };
            sd.ArgDic["maxHalfLife"] = 50;

            sd.ArgDescDic["confirmBars"] = new ArgDesc { Text = "确认K线数", Explain = "连续N根K线偏离才入场" };
            sd.ArgDic["confirmBars"] = 1;

            // ==================== 风控参数 ====================
            sd.ArgDescDic["maxHoldBars"] = new ArgDesc { Text = "最大持仓K线数", Explain = "超过此数量强制平仓" };
            sd.ArgDic["maxHoldBars"] = 20;

            sd.ArgDescDic["useTimeDecay"] = new ArgDesc { Text = "时间衰减", Explain = "1=持仓越久出场阈值越宽松 0=固定" };
            sd.ArgDic["useTimeDecay"] = 1;

            // ==================== 交易参数 ====================
            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "0=立即 1=下个开盘" };
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "0=固定手数 1=固定金额" };
            sd.ArgDic["lotsMode"] = 1;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "交易手数", Explain = "固定手数模式下两端品种的交易数量(中间腿为2倍)" };
            sd.ArgDic["lots"] = 1.0m;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "交易金额", Explain = "固定金额模式下每条腿的交易金额" };
            sd.ArgDic["money"] = 10000m;

            // ==================== 颜色配置 ====================
            sd.ColorDic["sub0-ZScore"] = "#2196F3";
            sd.ColorDic["sub0-EntryUpper"] = "#F44336";
            sd.ColorDic["sub0-EntryLower"] = "#4CAF50";
            sd.ColorDic["sub0-ExitUpper"] = "#FF9800";
            sd.ColorDic["sub0-ExitLower"] = "#FF9800";
            sd.ColorDic["sub0-Zero"] = "#666666";

            sd.ColorDic["sub1-ButterflySpread"] = "#9C27B0";
            sd.ColorDic["sub1-Mean"] = "#2196F3";
            sd.ColorDic["sub1-UpperBand"] = "#F44336";
            sd.ColorDic["sub1-LowerBand"] = "#4CAF50";

            sd.MidValDic["sub0"] = 0;

            return sd;
        }

        #region 全局状态

        /// <summary>
        /// 蝶式三元组数据
        /// </summary>
        private class ButterflyData
        {
            public string SymbolA { get; set; }     // 左翼
            public string SymbolB { get; set; }     // 中间腿
            public string SymbolC { get; set; }     // 右翼
            public List<double> SpreadHistory { get; set; } = new List<double>();
            public double CurrentSpread { get; set; }
            public double ZScore { get; set; }
            public double Mean { get; set; }
            public double StdDev { get; set; }
            public double HalfLife { get; set; }
            public double MinCorrelation { get; set; } // 三品种中最低的两两相关系数
            public bool IsValid { get; set; }
        }

        /// <summary>
        /// 交易状态
        /// </summary>
        private class TradeState
        {
            public int Status { get; set; }          // 0=空仓 1=买蝶式(买两端卖中间) 2=卖蝶式(卖两端买中间)
            public decimal Num { get; set; }
            public double EntryZScore { get; set; }
            public int HoldBars { get; set; }
            public int ConfirmCount { get; set; }
            public int LastSignalDir { get; set; }
            public string TripleKey { get; set; }    // 所属三元组key
            public string Role { get; set; }         // "A"=左翼 "B"=中间腿 "C"=右翼
        }

        // 全局蝶式三元组数据: key = "SymA|SymB|SymC"
        private Dictionary<string, ButterflyData> _butterflyDic = new Dictionary<string, ButterflyData>();
        // 每个品种的最新价格
        private Dictionary<string, decimal> _latestPriceDic = new Dictionary<string, decimal>();
        // 交易状态
        private Dictionary<string, TradeState> _stateDic = new Dictionary<string, TradeState>();

        #endregion

        #region OnGlobalIndicator — 全局蝶式价差计算

        public override void OnGlobalIndicator(List<TableUnit> tableUnitList)
        {
            base.OnGlobalIndicator(tableUnitList);
            if (tableUnitList == null || tableUnitList.Count == 0) return;

            int lookbackPeriod = Convert.ToInt32(ArgDic["lookbackPeriod"]);
            int spreadMode = Convert.ToInt32(ArgDic["spreadMode"]);
            int selectionMode = Convert.ToInt32(ArgDic["selectionMode"]);
            double minCorrelation = Convert.ToDouble(ArgDic["minCorrelation"]);

            // 收集每个品种数据
            var symbolDataDic = new Dictionary<string, List<SkQuote>>();
            foreach (var tu in tableUnitList)
            {
                if (tu.QuoteList == null || tu.QuoteList.Count < lookbackPeriod + 10) continue;
                if (!symbolDataDic.ContainsKey(tu.MktSymbol))
                {
                    symbolDataDic[tu.MktSymbol] = tu.QuoteList;
                }
            }

            // 更新最新价格
            foreach (var kvp in symbolDataDic)
            {
                if (kvp.Value.Count > 0)
                {
                    _latestPriceDic[kvp.Key] = kvp.Value.Last().Close;
                }
            }

            var symbols = symbolDataDic.Keys.OrderBy(s => s).ToList();
            if (symbols.Count < 3) return;

            _butterflyDic.Clear();

            if (selectionMode == 1)
            {
                // 固定分组模式：按顺序每3个一组
                for (int i = 0; i + 2 < symbols.Count; i += 3)
                {
                    CalcButterflyTriple(symbols[i], symbols[i + 1], symbols[i + 2],
                        symbolDataDic, lookbackPeriod, spreadMode, minCorrelation);
                }
            }
            else
            {
                // 自动选择模式：遍历所有三元组合，选出合格的
                for (int i = 0; i < symbols.Count; i++)
                {
                    for (int j = i + 1; j < symbols.Count; j++)
                    {
                        for (int k = j + 1; k < symbols.Count; k++)
                        {
                            // 三个品种轮流做中间腿，选最优的
                            TryAllMiddleLegs(symbols[i], symbols[j], symbols[k],
                                symbolDataDic, lookbackPeriod, spreadMode, minCorrelation);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 尝试三个品种的所有中间腿组合，选Z-Score均值回归性最好的
        /// </summary>
        private void TryAllMiddleLegs(string s1, string s2, string s3,
            Dictionary<string, List<SkQuote>> symbolDataDic,
            int lookbackPeriod, int spreadMode, double minCorrelation)
        {
            // 尝试s2做中间腿: s1 + s3 - 2*s2
            var bd1 = CalcButterflyTriple(s1, s2, s3, symbolDataDic, lookbackPeriod, spreadMode, minCorrelation);
            // 尝试s1做中间腿: s2 + s3 - 2*s1
            var bd2 = CalcButterflyTriple(s2, s1, s3, symbolDataDic, lookbackPeriod, spreadMode, minCorrelation);
            // 尝试s3做中间腿: s1 + s2 - 2*s3
            var bd3 = CalcButterflyTriple(s1, s3, s2, symbolDataDic, lookbackPeriod, spreadMode, minCorrelation);

            // 只保留半衰期最短（均值回归最快）的那个
            var candidates = new List<ButterflyData>();
            if (bd1 != null && bd1.IsValid) candidates.Add(bd1);
            if (bd2 != null && bd2.IsValid) candidates.Add(bd2);
            if (bd3 != null && bd3.IsValid) candidates.Add(bd3);

            if (candidates.Count == 0) return;

            var best = candidates.Where(c => c.HalfLife > 0).OrderBy(c => c.HalfLife).FirstOrDefault();
            if (best == null) best = candidates.First();

            // 移除非最优的
            foreach (var c in candidates)
            {
                if (c != best)
                {
                    string key = GetTripleKey(c.SymbolA, c.SymbolB, c.SymbolC);
                    _butterflyDic.Remove(key);
                }
            }
        }

        /// <summary>
        /// 计算一个蝶式三元组: ButterflySpread = Price_A + Price_C - 2 * Price_B
        /// </summary>
        private ButterflyData CalcButterflyTriple(string symA, string symB, string symC,
            Dictionary<string, List<SkQuote>> symbolDataDic,
            int lookbackPeriod, int spreadMode, double minCorrelation)
        {
            if (!symbolDataDic.ContainsKey(symA) || !symbolDataDic.ContainsKey(symB) || !symbolDataDic.ContainsKey(symC))
                return null;

            var quotesA = symbolDataDic[symA];
            var quotesB = symbolDataDic[symB];
            var quotesC = symbolDataDic[symC];

            int alignLen = Math.Min(quotesA.Count, Math.Min(quotesB.Count, quotesC.Count));
            if (alignLen < lookbackPeriod + 10) return null;

            var pricesA = quotesA.Skip(quotesA.Count - alignLen).Select(q => (double)q.Close).ToList();
            var pricesB = quotesB.Skip(quotesB.Count - alignLen).Select(q => (double)q.Close).ToList();
            var pricesC = quotesC.Skip(quotesC.Count - alignLen).Select(q => (double)q.Close).ToList();

            // 检查两两相关性
            int corrLen = Math.Min(lookbackPeriod, alignLen);
            var tailA = pricesA.Skip(pricesA.Count - corrLen).ToList();
            var tailB = pricesB.Skip(pricesB.Count - corrLen).ToList();
            var tailC = pricesC.Skip(pricesC.Count - corrLen).ToList();

            double corrAB = CalcCorrelation(tailA, tailB);
            double corrAC = CalcCorrelation(tailA, tailC);
            double corrBC = CalcCorrelation(tailB, tailC);
            double minCorr = Math.Min(Math.Abs(corrAB), Math.Min(Math.Abs(corrAC), Math.Abs(corrBC)));

            string tripleKey = GetTripleKey(symA, symB, symC);
            var bd = new ButterflyData
            {
                SymbolA = symA,
                SymbolB = symB,
                SymbolC = symC,
                MinCorrelation = minCorr
            };

            if (minCorr < minCorrelation)
            {
                bd.IsValid = false;
                _butterflyDic[tripleKey] = bd;
                return bd;
            }

            // 计算蝶式价差序列: A + C - 2B
            var spreadSeries = new List<double>();
            for (int i = 0; i < alignLen; i++)
            {
                double spread;
                if (spreadMode == 1 && pricesB[i] > 0)
                {
                    // 标准化模式：除以B的价格，消除量纲
                    spread = (pricesA[i] + pricesC[i] - 2 * pricesB[i]) / pricesB[i];
                }
                else
                {
                    spread = pricesA[i] + pricesC[i] - 2 * pricesB[i];
                }
                spreadSeries.Add(spread);
            }

            bd.SpreadHistory = spreadSeries;
            bd.CurrentSpread = spreadSeries.Last();

            // 半衰期
            double halfLife = CalcHalfLife(spreadSeries, Math.Min(spreadSeries.Count - 1, lookbackPeriod));
            bd.HalfLife = halfLife;

            // 计算均值和标准差
            int actualLookback = Math.Min(lookbackPeriod, spreadSeries.Count);
            var recentSpreads = spreadSeries.Skip(spreadSeries.Count - actualLookback).ToList();
            double mean = recentSpreads.Average();
            double variance = recentSpreads.Sum(s => (s - mean) * (s - mean)) / actualLookback;
            double stdDev = Math.Sqrt(variance);

            bd.Mean = mean;
            bd.StdDev = stdDev;
            bd.ZScore = stdDev > 1e-10 ? (bd.CurrentSpread - mean) / stdDev : 0;
            bd.IsValid = true;

            _butterflyDic[tripleKey] = bd;
            return bd;
        }

        #endregion

        #region OnBar — 每个品种的交易逻辑

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (!isFinal) return;
            if (ArgDic == null) return;

            double entryZScore = Convert.ToDouble(ArgDic["entryZScore"]);
            double exitZScore = Convert.ToDouble(ArgDic["exitZScore"]);
            double stopLossZScore = Convert.ToDouble(ArgDic["stopLossZScore"]);
            int useHalfLifeFilter = Convert.ToInt32(ArgDic["useHalfLifeFilter"]);
            int minHalfLife = Convert.ToInt32(ArgDic["minHalfLife"]);
            int maxHalfLife = Convert.ToInt32(ArgDic["maxHalfLife"]);
            int confirmBars = Convert.ToInt32(ArgDic["confirmBars"]);
            int maxHoldBars = Convert.ToInt32(ArgDic["maxHoldBars"]);
            int useTimeDecay = Convert.ToInt32(ArgDic["useTimeDecay"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            string mktSymbol = tu.MktSymbol;
            var sk = tu.GetStateKey();

            if (!_stateDic.ContainsKey(sk))
            {
                _stateDic[sk] = new TradeState();
            }
            var state = _stateDic[sk];

            decimal currentPrice = tu.QuoteList.Last().Close;

            // 找到该品种所属的最佳蝶式三元组
            ButterflyData bestButterfly = null;
            string bestRole = null;

            foreach (var kvp in _butterflyDic)
            {
                var bd = kvp.Value;
                if (!bd.IsValid) continue;
                if (bd.SymbolA != mktSymbol && bd.SymbolB != mktSymbol && bd.SymbolC != mktSymbol) continue;

                // 如果已有持仓，只关注已配对的三元组
                if (state.Status != 0 && state.TripleKey != null)
                {
                    if (kvp.Key != state.TripleKey) continue;
                }

                // 选Z-Score绝对值最大的三元组
                if (bestButterfly == null || Math.Abs(bd.ZScore) > Math.Abs(bestButterfly.ZScore))
                {
                    bestButterfly = bd;
                    if (bd.SymbolA == mktSymbol) bestRole = "A";
                    else if (bd.SymbolB == mktSymbol) bestRole = "B";
                    else bestRole = "C";
                }
            }

            // 绘制指标
            if (bestButterfly != null)
            {
                Plot("sub0", "ZScore", PlotType.LINE, bestButterfly.ZScore);
                Plot("sub0", "EntryUpper", PlotType.XLINE, entryZScore);
                Plot("sub0", "EntryLower", PlotType.XLINE, -entryZScore);
                Plot("sub0", "ExitUpper", PlotType.XLINE, exitZScore);
                Plot("sub0", "ExitLower", PlotType.XLINE, -exitZScore);
                Plot("sub0", "Zero", PlotType.XLINE, 0);

                Plot("sub1", "ButterflySpread", PlotType.LINE, bestButterfly.CurrentSpread);
                Plot("sub1", "Mean", PlotType.LINE, bestButterfly.Mean);
                if (bestButterfly.StdDev > 0)
                {
                    Plot("sub1", "UpperBand", PlotType.LINE, bestButterfly.Mean + bestButterfly.StdDev * entryZScore);
                    Plot("sub1", "LowerBand", PlotType.LINE, bestButterfly.Mean - bestButterfly.StdDev * entryZScore);
                }
            }

            if (bestButterfly == null || bestRole == null) return;

            // 半衰期过滤
            bool halfLifeOk = useHalfLifeFilter == 0 ||
                              (bestButterfly.HalfLife >= minHalfLife && bestButterfly.HalfLife <= maxHalfLife);

            double bfZ = bestButterfly.ZScore;

            // 计算手数（中间腿B是两端的2倍）
            decimal num = CalcLots(mktSymbol, currentPrice);
            decimal tradeNum = (bestRole == "B") ? num * 2 : num;

            // ==================== 持仓管理 ====================
            if (state.Status != 0)
            {
                state.HoldBars++;

                double currentExitZ = exitZScore;
                if (useTimeDecay == 1)
                {
                    double decayFactor = 1.0 + (double)state.HoldBars / maxHoldBars;
                    currentExitZ = exitZScore * decayFactor;
                }

                bool exitSignal = false;

                // Z-Score回归 → 平仓
                if (Math.Abs(bfZ) <= currentExitZ)
                {
                    exitSignal = true;
                }

                // Z-Score继续偏离 → 止损
                if (!exitSignal && Math.Abs(bfZ) > stopLossZScore)
                {
                    exitSignal = true;
                }

                // 超时平仓
                if (!exitSignal && state.HoldBars >= maxHoldBars)
                {
                    exitSignal = true;
                }

                if (exitSignal)
                {
                    // 根据角色和方向平仓
                    if (state.Status == 1)
                    {
                        // 买蝶式: A买C买B卖 → 平仓: A卖C卖B买
                        if (bestRole == "A" || bestRole == "C")
                        {
                            Trade(mktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.Num, period, sendMode);
                        }
                        else
                        {
                            Trade(mktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.Num, period, sendMode);
                        }
                    }
                    else if (state.Status == 2)
                    {
                        // 卖蝶式: A卖C卖B买 → 平仓: A买C买B卖
                        if (bestRole == "A" || bestRole == "C")
                        {
                            Trade(mktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.Num, period, sendMode);
                        }
                        else
                        {
                            Trade(mktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.Num, period, sendMode);
                        }
                    }
                    state.Status = 0;
                    state.Num = 0;
                    state.HoldBars = 0;
                    state.TripleKey = null;
                    state.Role = null;
                    state.ConfirmCount = 0;
                    state.LastSignalDir = 0;
                }
            }

            // ==================== 开仓逻辑 ====================
            if (state.Status == 0 && halfLifeOk)
            {
                // bfZ > entryZScore → 价差偏高 → 卖蝶式(卖两端A/C + 买中间B*2)
                // bfZ < -entryZScore → 价差偏低 → 买蝶式(买两端A/C + 卖中间B*2)
                int signalDir = 0;
                if (bfZ > entryZScore) signalDir = 2;        // 卖蝶式
                else if (bfZ < -entryZScore) signalDir = 1;  // 买蝶式

                // 连续确认
                if (signalDir > 0 && signalDir == state.LastSignalDir)
                {
                    state.ConfirmCount++;
                }
                else
                {
                    state.ConfirmCount = signalDir > 0 ? 1 : 0;
                }
                state.LastSignalDir = signalDir;

                if (state.ConfirmCount >= confirmBars)
                {
                    string tripleKey = GetTripleKey(bestButterfly.SymbolA, bestButterfly.SymbolB, bestButterfly.SymbolC);

                    if (signalDir == 1)
                    {
                        // 买蝶式: 买两端A/C + 卖中间B(数量*2)
                        state.Status = 1;
                        state.Num = tradeNum;
                        state.EntryZScore = bfZ;
                        state.HoldBars = 0;
                        state.TripleKey = tripleKey;
                        state.Role = bestRole;

                        if (bestRole == "A" || bestRole == "C")
                        {
                            Trade(mktSymbol, OrderType.BUY, currentPrice, tradeNum, period, sendMode);
                        }
                        else
                        {
                            Trade(mktSymbol, OrderType.SELL, currentPrice, tradeNum, period, sendMode);
                        }
                    }
                    else if (signalDir == 2)
                    {
                        // 卖蝶式: 卖两端A/C + 买中间B(数量*2)
                        state.Status = 2;
                        state.Num = tradeNum;
                        state.EntryZScore = bfZ;
                        state.HoldBars = 0;
                        state.TripleKey = tripleKey;
                        state.Role = bestRole;

                        if (bestRole == "A" || bestRole == "C")
                        {
                            Trade(mktSymbol, OrderType.SELL, currentPrice, tradeNum, period, sendMode);
                        }
                        else
                        {
                            Trade(mktSymbol, OrderType.BUY, currentPrice, tradeNum, period, sendMode);
                        }
                    }

                    state.ConfirmCount = 0;
                }
            }
        }

        #endregion

        #region 工具方法

        private string GetTripleKey(string symA, string symB, string symC)
        {
            return symA + "|" + symB + "|" + symC;
        }

        private decimal CalcLots(string mktSymbol, decimal price)
        {
            var num = (decimal)ArgDic["lots"];
            var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            if (lotsMode == 1)
            {
                var s = GetSymbol(mktSymbol);
                num = ((decimal)ArgDic["money"] / (price * s.multiplier * s.margin_ratio));
                if (s.symbol_type == (int)SymbolType.COIN)
                {
                    num = (int)(num * 1000) / 1000.0m;
                }
                else
                {
                    num = (int)num;
                }
            }
            return Math.Max(0.001m, num);
        }

        /// <summary>
        /// 计算两个序列的皮尔逊相关系数
        /// </summary>
        private double CalcCorrelation(List<double> x, List<double> y)
        {
            int n = Math.Min(x.Count, y.Count);
            if (n < 10) return 0;

            double meanX = 0, meanY = 0;
            for (int i = 0; i < n; i++)
            {
                meanX += x[i];
                meanY += y[i];
            }
            meanX /= n;
            meanY /= n;

            double covXY = 0, varX = 0, varY = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = x[i] - meanX;
                double dy = y[i] - meanY;
                covXY += dx * dy;
                varX += dx * dx;
                varY += dy * dy;
            }

            double denominator = Math.Sqrt(varX * varY);
            if (denominator < 1e-10) return 0;
            return covXY / denominator;
        }

        /// <summary>
        /// 半衰期计算: ΔY = β * Y(-1) + ε，半衰期 = -ln(2) / ln(1+β)
        /// </summary>
        private double CalcHalfLife(List<double> series, int lookback)
        {
            if (series.Count < lookback + 1 || lookback < 10) return -1;

            var deltaY = new List<double>();
            var lagY = new List<double>();

            for (int i = series.Count - lookback; i < series.Count; i++)
            {
                deltaY.Add(series[i] - series[i - 1]);
                lagY.Add(series[i - 1]);
            }

            double meanDelta = deltaY.Average();
            double meanLag = lagY.Average();

            double covariance = 0;
            double variance = 0;

            for (int i = 0; i < deltaY.Count; i++)
            {
                covariance += (deltaY[i] - meanDelta) * (lagY[i] - meanLag);
                variance += (lagY[i] - meanLag) * (lagY[i] - meanLag);
            }

            if (variance == 0) return -1;

            double beta = covariance / variance;
            if (beta >= 0) return -1;

            double halfLife = -Math.Log(2) / Math.Log(1 + beta);
            return halfLife > 0 ? halfLife : -1;
        }

        #endregion
    }
}
