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
    /// 领先滞后套利策略 (Lead-Lag Arbitrage)
    /// 
    /// 策略核心思想：
    /// 某些品种对信息的反应速度不同，领先品种先动、滞后品种后动。
    /// 在OnGlobalIndicator中计算所有品种两两之间的领先-滞后关系（交叉相关），
    /// 当领先品种出现大幅变动时，预判滞后品种即将跟随，提前布局。
    /// 
    /// 核心逻辑：
    /// 1. OnGlobalIndicator: 计算所有品种两两的滞后交叉相关系数
    ///    对于品种对(A,B)，计算 Corr(Return_A[t], Return_B[t+lag])
    ///    如果lag>0处相关最高，说明A领先B
    /// 2. 筛选出具有显著领先-滞后关系的品种对
    /// 3. 当领先品种出现大幅变动(领先信号)时，对滞后品种下单
    /// 4. 超时或信号消失时平仓
    /// 
    /// 适用场景：
    /// - 大盘股领先小盘股
    /// - 期货领先现货
    /// - 高流动性品种领先低流动性品种
    /// - 信息传递存在时滞的关联品种
    /// </summary>
    public class LeadLagArbitrage : StgBase
    {
        public LeadLagArbitrage()
        {
        }

        public LeadLagArbitrage(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 2;
            sd.UseGlobalCalc = 1;

            // ==================== 领先滞后计算参数 ====================
            sd.ArgDescDic["maxLag"] = new ArgDesc { Text = "最大滞后期数", Explain = "搜索领先滞后关系的最大K线数", Type = "number" };
            sd.ArgDic["maxLag"] = 5;

            sd.ArgDescDic["correlationPeriod"] = new ArgDesc { Text = "相关性周期", Explain = "计算交叉相关的滚动窗口", Type = "number" };
            sd.ArgDic["correlationPeriod"] = 60;

            sd.ArgDescDic["minLeadCorrelation"] = new ArgDesc { Text = "最小领先相关系数", Explain = "滞后相关系数低于此值不视为有效领先关系", Type = "number" };
            sd.ArgDic["minLeadCorrelation"] = 0.2;

            sd.ArgDescDic["minLeadAdvantage"] = new ArgDesc { Text = "最小领先优势", Explain = "滞后相关系数需比同期相关系数高出此值", Type = "number" };
            sd.ArgDic["minLeadAdvantage"] = 0.05;

            // ==================== 信号参数 ====================
            sd.ArgDescDic["signalThreshold"] = new ArgDesc { Text = "信号阈值", Explain = "领先品种收益率Z-Score超过此值触发信号", Type = "number" };
            sd.ArgDic["signalThreshold"] = 1.0;

            sd.ArgDescDic["returnPeriod"] = new ArgDesc { Text = "收益率周期", Explain = "计算领先品种变化率的K线间隔", Type = "number" };
            sd.ArgDic["returnPeriod"] = 1;

            sd.ArgDescDic["returnLookback"] = new ArgDesc { Text = "收益率回溯", Explain = "计算收益率Z-Score的历史窗口", Type = "number" };
            sd.ArgDic["returnLookback"] = 30;

            sd.ArgDescDic["confirmBars"] = new ArgDesc { Text = "确认K线数", Explain = "连续N根K线信号一致才入场", Type = "number" };
            sd.ArgDic["confirmBars"] = 1;

            // ==================== 风控参数 ====================
            sd.ArgDescDic["maxHoldBars"] = new ArgDesc { Text = "最大持仓K线数", Explain = "跟随信号最多持有多少根K线", Type = "number" };
            sd.ArgDic["maxHoldBars"] = 10;

            sd.ArgDescDic["atrPeriod"] = new ArgDesc { Text = "ATR周期", Explain = "ATR计算周期", Type = "number" };
            sd.ArgDic["atrPeriod"] = 14;

            sd.ArgDescDic["atrStopMultiplier"] = new ArgDesc { Text = "ATR止损倍数", Explain = "止损=ATR*此倍数", Type = "number" };
            sd.ArgDic["atrStopMultiplier"] = 2.0;

            sd.ArgDescDic["atrProfitMultiplier"] = new ArgDesc { Text = "ATR止盈倍数", Explain = "止盈=ATR*此倍数(0=不止盈)", Type = "number" };
            sd.ArgDic["atrProfitMultiplier"] = 3.0;

            // ==================== 交易参数 ====================
            sd.ArgDescDic["mode"] = new ArgDesc { Text = "交易方向", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDic["mode"] = 0;

            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
            sd.ArgDic["lotsMode"] = 1;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "交易手数", Explain = "固定手数模式下的交易数量", Type = "number" };
            sd.ArgDic["lots"] = 1.0m;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "交易金额", Explain = "固定金额模式下的交易金额", Type = "number" };
            sd.ArgDic["money"] = 10000m;

            // ==================== 颜色配置 ====================
            sd.ColorDic["sub0-LeadSignal"] = "#F44336";
            sd.ColorDic["sub0-SignalThreshold"] = "#FF9800";
            sd.ColorDic["sub0-NegThreshold"] = "#FF9800";
            sd.ColorDic["sub0-Zero"] = "#666666";

            sd.ColorDic["sub1-LeadCorrelation"] = "#2196F3";
            sd.ColorDic["sub1-MinCorr"] = "#FF9800";

            sd.MidValDic["sub0"] = 0;

            return sd;
        }

        #region 全局状态

        /// <summary>
        /// 领先滞后关系数据
        /// </summary>
        private class LeadLagPair
        {
            public string LeaderSymbol { get; set; }    // 领先品种
            public string LaggerSymbol { get; set; }    // 滞后品种
            public int OptimalLag { get; set; }          // 最优滞后期数
            public double LeadCorrelation { get; set; }  // 滞后处的相关系数
            public double SyncCorrelation { get; set; }  // 同期相关系数
            public double LeadAdvantage { get; set; }    // 领先优势 = LeadCorrelation - SyncCorrelation
            public bool IsValid { get; set; }
        }

        /// <summary>
        /// 品种的领先信号强度
        /// </summary>
        private class SymbolLeadSignal
        {
            public string MktSymbol { get; set; }
            public double ReturnZScore { get; set; }   // 领先品种的收益率Z-Score
            public double Atr { get; set; }
            public decimal LatestPrice { get; set; }
            public List<string> LaggerSymbols { get; set; } = new List<string>();
            public List<string> LeaderSymbols { get; set; } = new List<string>();
        }

        /// <summary>
        /// 交易状态
        /// </summary>
        private class TradeState
        {
            public int Status { get; set; }         // 0=空仓 1=做多 2=做空
            public decimal Num { get; set; }
            public decimal EntryPrice { get; set; }
            public decimal StopLoss { get; set; }
            public decimal TakeProfit { get; set; }
            public int HoldBars { get; set; }
            public int ConfirmCount { get; set; }
            public int LastSignalDir { get; set; }
            public string LeaderSymbol { get; set; } // 触发此交易的领先品种
        }

        // 品种对的领先滞后关系
        private Dictionary<string, LeadLagPair> _leadLagDic = new Dictionary<string, LeadLagPair>();
        // 每个品种的信号数据
        private Dictionary<string, SymbolLeadSignal> _signalDic = new Dictionary<string, SymbolLeadSignal>();
        // 交易状态
        private Dictionary<string, TradeState> _stateDic = new Dictionary<string, TradeState>();

        #endregion

        #region OnGlobalIndicator — 全局领先滞后关系计算

        public override void OnGlobalIndicator(List<TableUnit> tableUnitList)
        {
            base.OnGlobalIndicator(tableUnitList);
            if (tableUnitList == null || tableUnitList.Count == 0) return;

            int maxLag = Convert.ToInt32(ArgDic["maxLag"]);
            int correlationPeriod = Convert.ToInt32(ArgDic["correlationPeriod"]);
            double minLeadCorrelation = Convert.ToDouble(ArgDic["minLeadCorrelation"]);
            double minLeadAdvantage = Convert.ToDouble(ArgDic["minLeadAdvantage"]);
            int returnPeriod = Convert.ToInt32(ArgDic["returnPeriod"]);
            int returnLookback = Convert.ToInt32(ArgDic["returnLookback"]);
            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);

            int minBars = correlationPeriod + maxLag + returnPeriod + atrPeriod + 20;

            // 收集每个品种数据
            var symbolDataDic = new Dictionary<string, List<SkQuote>>();
            foreach (var tu in tableUnitList)
            {
                if (tu.QuoteList == null || tu.QuoteList.Count < minBars) continue;
                if (!symbolDataDic.ContainsKey(tu.MktSymbol))
                {
                    symbolDataDic[tu.MktSymbol] = tu.QuoteList;
                }
            }

            var symbols = symbolDataDic.Keys.ToList();
            if (symbols.Count < 2) return;

            // 计算每个品种的收益率序列
            var returnsDic = new Dictionary<string, List<double>>();
            foreach (var sym in symbols)
            {
                var quotes = symbolDataDic[sym];
                var returns = new List<double>();
                for (int i = returnPeriod; i < quotes.Count; i++)
                {
                    decimal prev = quotes[i - returnPeriod].Close;
                    if (prev > 0)
                    {
                        returns.Add((double)(quotes[i].Close - prev) / (double)prev);
                    }
                    else
                    {
                        returns.Add(0);
                    }
                }
                returnsDic[sym] = returns;
            }

            // 对齐收益率序列长度
            int retLen = returnsDic.Values.Min(r => r.Count);
            if (retLen < correlationPeriod + maxLag) return;

            // 清空并重建领先滞后关系和信号
            _leadLagDic.Clear();
            foreach (var sym in symbols)
            {
                if (!_signalDic.ContainsKey(sym))
                {
                    _signalDic[sym] = new SymbolLeadSignal { MktSymbol = sym };
                }
                _signalDic[sym].LaggerSymbols.Clear();
                _signalDic[sym].LeaderSymbols.Clear();
            }

            // 两两计算交叉相关
            for (int i = 0; i < symbols.Count; i++)
            {
                for (int j = i + 1; j < symbols.Count; j++)
                {
                    string symA = symbols[i];
                    string symB = symbols[j];

                    var retA = returnsDic[symA].Skip(returnsDic[symA].Count - retLen).ToList();
                    var retB = returnsDic[symB].Skip(returnsDic[symB].Count - retLen).ToList();

                    // 计算同期相关
                    int corrStart = retLen - correlationPeriod;
                    var tailA = retA.Skip(corrStart).Take(correlationPeriod).ToList();
                    var tailB = retB.Skip(corrStart).Take(correlationPeriod).ToList();
                    double syncCorr = CalcCorrelation(tailA, tailB);

                    // 搜索最优滞后: A领先B (A[t] vs B[t+lag])
                    double bestCorrAB = 0;
                    int bestLagAB = 0;

                    for (int lag = 1; lag <= maxLag; lag++)
                    {
                        if (corrStart - lag < 0) break;
                        var laggedA = retA.Skip(corrStart - lag).Take(correlationPeriod).ToList();
                        double corr = CalcCorrelation(laggedA, tailB);
                        if (Math.Abs(corr) > Math.Abs(bestCorrAB))
                        {
                            bestCorrAB = corr;
                            bestLagAB = lag;
                        }
                    }

                    // 搜索最优滞后: B领先A (B[t] vs A[t+lag])
                    double bestCorrBA = 0;
                    int bestLagBA = 0;

                    for (int lag = 1; lag <= maxLag; lag++)
                    {
                        if (corrStart - lag < 0) break;
                        var laggedB = retB.Skip(corrStart - lag).Take(correlationPeriod).ToList();
                        double corr = CalcCorrelation(laggedB, tailA);
                        if (Math.Abs(corr) > Math.Abs(bestCorrBA))
                        {
                            bestCorrBA = corr;
                            bestLagBA = lag;
                        }
                    }

                    // 确定谁领先谁
                    if (Math.Abs(bestCorrAB) > Math.Abs(bestCorrBA) &&
                        Math.Abs(bestCorrAB) >= minLeadCorrelation &&
                        Math.Abs(bestCorrAB) - Math.Abs(syncCorr) >= minLeadAdvantage)
                    {
                        // A领先B
                        string key = symA + ">" + symB;
                        _leadLagDic[key] = new LeadLagPair
                        {
                            LeaderSymbol = symA,
                            LaggerSymbol = symB,
                            OptimalLag = bestLagAB,
                            LeadCorrelation = bestCorrAB,
                            SyncCorrelation = syncCorr,
                            LeadAdvantage = Math.Abs(bestCorrAB) - Math.Abs(syncCorr),
                            IsValid = true
                        };
                        _signalDic[symA].LaggerSymbols.Add(symB);
                        _signalDic[symB].LeaderSymbols.Add(symA);
                    }
                    else if (Math.Abs(bestCorrBA) >= minLeadCorrelation &&
                             Math.Abs(bestCorrBA) - Math.Abs(syncCorr) >= minLeadAdvantage)
                    {
                        // B领先A
                        string key = symB + ">" + symA;
                        _leadLagDic[key] = new LeadLagPair
                        {
                            LeaderSymbol = symB,
                            LaggerSymbol = symA,
                            OptimalLag = bestLagBA,
                            LeadCorrelation = bestCorrBA,
                            SyncCorrelation = syncCorr,
                            LeadAdvantage = Math.Abs(bestCorrBA) - Math.Abs(syncCorr),
                            IsValid = true
                        };
                        _signalDic[symB].LaggerSymbols.Add(symA);
                        _signalDic[symA].LeaderSymbols.Add(symB);
                    }
                }
            }

            // 计算每个品种的收益率Z-Score和ATR
            foreach (var sym in symbols)
            {
                var quotes = symbolDataDic[sym];
                var returns = returnsDic[sym];

                _signalDic[sym].LatestPrice = quotes.Last().Close;

                // 收益率Z-Score
                int zLen = Math.Min(returnLookback, returns.Count);
                var recentReturns = returns.Skip(returns.Count - zLen).ToList();
                double latestReturn = returns.Last();
                double mean = recentReturns.Average();
                double variance = recentReturns.Sum(r => (r - mean) * (r - mean)) / zLen;
                double stdDev = Math.Sqrt(variance);
                _signalDic[sym].ReturnZScore = stdDev > 1e-10 ? (latestReturn - mean) / stdDev : 0;

                // ATR
                if (quotes.Count > atrPeriod + 5)
                {
                    var atrList = quotes.GetAtr(atrPeriod).ToList();
                    _signalDic[sym].Atr = atrList.Last().Atr ?? 0;
                }
            }
        }

        #endregion

        #region OnBar — 每个品种的交易逻辑

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (!isFinal) return;
            if (ArgDic == null) return;

            string mktSymbol = tu.MktSymbol;
            var sk = tu.GetStateKey();

            if (!_signalDic.ContainsKey(mktSymbol)) return;
            var signal = _signalDic[mktSymbol];

            double signalThreshold = Convert.ToDouble(ArgDic["signalThreshold"]);
            int confirmBars = Convert.ToInt32(ArgDic["confirmBars"]);
            int maxHoldBars = Convert.ToInt32(ArgDic["maxHoldBars"]);
            double atrStopMultiplier = Convert.ToDouble(ArgDic["atrStopMultiplier"]);
            double atrProfitMultiplier = Convert.ToDouble(ArgDic["atrProfitMultiplier"]);
            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            if (!_stateDic.ContainsKey(sk))
            {
                _stateDic[sk] = new TradeState();
            }
            var state = _stateDic[sk];
            decimal currentPrice = tu.QuoteList.Last().Close;

            // 找到该品种的领先品种的信号
            // 本品种是滞后品种时才交易（跟随领先品种的信号）
            double bestLeaderSignal = 0;
            double bestLeadCorr = 0;
            string bestLeader = null;

            foreach (var leaderSym in signal.LeaderSymbols)
            {
                if (!_signalDic.ContainsKey(leaderSym)) continue;
                var leaderSignal = _signalDic[leaderSym];

                string key = leaderSym + ">" + mktSymbol;
                if (!_leadLagDic.ContainsKey(key) || !_leadLagDic[key].IsValid) continue;

                var pair = _leadLagDic[key];

                // 如果已持仓，只关注触发此交易的领先品种
                if (state.Status != 0 && state.LeaderSymbol != null && state.LeaderSymbol != leaderSym)
                    continue;

                // 取相关系数最高的领先品种信号
                if (bestLeader == null || Math.Abs(pair.LeadCorrelation) > Math.Abs(bestLeadCorr))
                {
                    bestLeaderSignal = leaderSignal.ReturnZScore;
                    bestLeadCorr = pair.LeadCorrelation;
                    bestLeader = leaderSym;
                }
            }

            // 绘制指标
            Plot("sub0", "LeadSignal", PlotType.LINE, bestLeaderSignal);
            Plot("sub0", "SignalThreshold", PlotType.XLINE, signalThreshold);
            Plot("sub0", "NegThreshold", PlotType.XLINE, -signalThreshold);
            Plot("sub0", "Zero", PlotType.XLINE, 0);

            Plot("sub1", "LeadCorrelation", PlotType.LINE, bestLeadCorr);
            Plot("sub1", "MinCorr", PlotType.XLINE, Convert.ToDouble(ArgDic["minLeadCorrelation"]));

            // ==================== 持仓管理 ====================
            if (state.Status != 0)
            {
                state.HoldBars++;

                bool exitSignal = false;

                // 止损
                if (state.Status == 1 && state.StopLoss > 0 && currentPrice <= state.StopLoss)
                {
                    exitSignal = true;
                }
                else if (state.Status == 2 && state.StopLoss > 0 && currentPrice >= state.StopLoss)
                {
                    exitSignal = true;
                }

                // 止盈
                if (!exitSignal && state.TakeProfit > 0)
                {
                    if (state.Status == 1 && currentPrice >= state.TakeProfit)
                    {
                        exitSignal = true;
                    }
                    else if (state.Status == 2 && currentPrice <= state.TakeProfit)
                    {
                        exitSignal = true;
                    }
                }

                // 超时
                if (!exitSignal && state.HoldBars >= maxHoldBars)
                {
                    exitSignal = true;
                }

                if (exitSignal)
                {
                    if (state.Status == 1)
                    {
                        Trade(mktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.Num, period, sendMode);
                    }
                    else if (state.Status == 2)
                    {
                        Trade(mktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.Num, period, sendMode);
                    }
                    state.Status = 0;
                    state.Num = 0;
                    state.EntryPrice = 0;
                    state.StopLoss = 0;
                    state.TakeProfit = 0;
                    state.HoldBars = 0;
                    state.ConfirmCount = 0;
                    state.LastSignalDir = 0;
                    state.LeaderSymbol = null;
                }
            }

            // ==================== 开仓逻辑 ====================
            if (state.Status == 0 && bestLeader != null)
            {
                // 领先品种大幅上涨(Z-Score > threshold) + 正相关 → 滞后品种做多
                // 领先品种大幅下跌(Z-Score < -threshold) + 正相关 → 滞后品种做空
                // 负相关则反向
                int signalDir = 0;
                if (bestLeadCorr > 0)
                {
                    if (bestLeaderSignal > signalThreshold) signalDir = 1;
                    else if (bestLeaderSignal < -signalThreshold) signalDir = 2;
                }
                else
                {
                    // 负相关：领先品种上涨 → 滞后品种做空
                    if (bestLeaderSignal > signalThreshold) signalDir = 2;
                    else if (bestLeaderSignal < -signalThreshold) signalDir = 1;
                }

                // 方向过滤
                if (mode == 1 && signalDir == 2) signalDir = 0;
                if (mode == 2 && signalDir == 1) signalDir = 0;

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
                    decimal num = CalcLots(mktSymbol, currentPrice);
                    decimal atrDecimal = (decimal)signal.Atr;

                    if (signalDir == 1)
                    {
                        state.Status = 1;
                        state.Num = num;
                        state.EntryPrice = currentPrice;
                        state.HoldBars = 0;
                        state.LeaderSymbol = bestLeader;
                        if (atrDecimal > 0)
                        {
                            state.StopLoss = currentPrice - atrDecimal * (decimal)atrStopMultiplier;
                            if (atrProfitMultiplier > 0)
                            {
                                state.TakeProfit = currentPrice + atrDecimal * (decimal)atrProfitMultiplier;
                            }
                        }
                        Trade(mktSymbol, OrderType.BUY, currentPrice, num, period, sendMode);
                    }
                    else if (signalDir == 2)
                    {
                        state.Status = 2;
                        state.Num = num;
                        state.EntryPrice = currentPrice;
                        state.HoldBars = 0;
                        state.LeaderSymbol = bestLeader;
                        if (atrDecimal > 0)
                        {
                            state.StopLoss = currentPrice + atrDecimal * (decimal)atrStopMultiplier;
                            if (atrProfitMultiplier > 0)
                            {
                                state.TakeProfit = currentPrice - atrDecimal * (decimal)atrProfitMultiplier;
                            }
                        }
                        Trade(mktSymbol, OrderType.SELL, currentPrice, num, period, sendMode);
                    }

                    state.ConfirmCount = 0;
                }
            }
        }

        #endregion

        #region 工具方法

        private decimal CalcLots(string mktSymbol, decimal price)
        {
            var num = Convert.ToDecimal(ArgDic["lots"]);
            var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            if (lotsMode == 1)
            {
                var s = GetSymbol(mktSymbol);
                num = (Convert.ToDecimal(ArgDic["money"]) / (price * s.multiplier * s.margin_ratio));
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

        #endregion
    }
}
