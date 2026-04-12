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
    /// 价差套利策略 (Spread Arbitrage Strategy)
    /// 
    /// 策略核心思想：
    /// 在OnGlobalIndicator中构建多品种的"合成价差指数"，
    /// 每个品种相对全局均值的偏离度作为交易信号。
    /// 偏离大的品种做反向（回归），偏离小的品种不交易。
    /// 
    /// 与PairTrading的区别：
    /// - PairTrading：固定两两配对
    /// - SpreadArbitrage：以全局为参照，一对多，灵活性更高
    /// 
    /// 核心逻辑：
    /// 1. OnGlobalIndicator: 计算所有品种的标准化收益率
    /// 2. 构建全局均值基准线(等权/市值加权)
    /// 3. 计算每个品种相对基准的累积偏离度(Cumulative Deviation)
    /// 4. 对偏离度做Z-Score标准化
    /// 5. Z-Score > entryZ → 该品种相对高估 → 做空
    /// 6. Z-Score < -entryZ → 该品种相对低估 → 做多
    /// 7. Z-Score回归 → 平仓获利
    /// 
    /// 优势：
    /// - 天然市场中性：多空头寸自动平衡
    /// - 不依赖两个品种的固定配对关系
    /// - 适用于同板块/同类型品种池
    /// </summary>
    public class SpreadArbitrage : StgBase
    {
        public SpreadArbitrage()
        {
        }

        public SpreadArbitrage(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 2;
            sd.UseGlobalCalc = 1;

            // ==================== 偏离度计算参数 ====================
            sd.ArgDescDic["lookbackPeriod"] = new ArgDesc { Text = "回溯周期", Explain = "计算累积偏离度和Z-Score的滚动窗口" };
            sd.ArgDic["lookbackPeriod"] = 60;

            sd.ArgDescDic["returnPeriod"] = new ArgDesc { Text = "收益率周期", Explain = "计算标准化收益率的K线间隔" };
            sd.ArgDic["returnPeriod"] = 1;

            sd.ArgDescDic["benchmarkMode"] = new ArgDesc { Text = "基准模式", Explain = "基准收益计算方式", Options = "0:等权均值|1:中位数|2:收益率排名中位数", Type = "select" };
            sd.ArgDic["benchmarkMode"] = 0;

            // ==================== Z-Score阈值 ====================
            sd.ArgDescDic["entryZScore"] = new ArgDesc { Text = "入场Z-Score", Explain = "偏离度Z-Score绝对值超过此值入场" };
            sd.ArgDic["entryZScore"] = 1.5;

            sd.ArgDescDic["exitZScore"] = new ArgDesc { Text = "出场Z-Score", Explain = "偏离度Z-Score绝对值低于此值出场" };
            sd.ArgDic["exitZScore"] = 0.3;

            sd.ArgDescDic["stopLossZScore"] = new ArgDesc { Text = "止损Z-Score", Explain = "偏离度Z-Score绝对值超过此值止损" };
            sd.ArgDic["stopLossZScore"] = 2.5;

            // ==================== 偏离确认参数 ====================
            sd.ArgDescDic["confirmBars"] = new ArgDesc { Text = "确认K线数", Explain = "连续N根K线偏离才入场" };
            sd.ArgDic["confirmBars"] = 1;

            sd.ArgDescDic["minSymbols"] = new ArgDesc { Text = "最小品种数", Explain = "品种数低于此值不计算(样本不足)" };
            sd.ArgDic["minSymbols"] = 3;

            // ==================== 风控参数 ====================
            sd.ArgDescDic["maxHoldBars"] = new ArgDesc { Text = "最大持仓K线数", Explain = "超过此数量强制平仓" };
            sd.ArgDic["maxHoldBars"] = 25;

            sd.ArgDescDic["useTimeDecay"] = new ArgDesc { Text = "时间衰减", Explain = "持仓时间衰减", Options = "1:持仓越久出场阈值越宽松|0:固定", Type = "select" };
            sd.ArgDic["useTimeDecay"] = 1;

            sd.ArgDescDic["atrPeriod"] = new ArgDesc { Text = "ATR周期", Explain = "ATR计算周期" };
            sd.ArgDic["atrPeriod"] = 14;

            sd.ArgDescDic["atrStopMultiplier"] = new ArgDesc { Text = "ATR止损倍数", Explain = "额外ATR止损保护(0=禁用，仅用Z-Score止损)" };
            sd.ArgDic["atrStopMultiplier"] = 0.0;

            sd.ArgDescDic["maxPositions"] = new ArgDesc { Text = "最大持仓数", Explain = "同时持仓的最大品种数(0=不限制)" };
            sd.ArgDic["maxPositions"] = 0;

            // ==================== 交易参数 ====================
            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额|2:Z-Score加权", Type = "select" };
            sd.ArgDic["lotsMode"] = 1;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "交易手数", Explain = "固定手数模式下的交易数量" };
            sd.ArgDic["lots"] = 1.0m;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "交易金额", Explain = "固定金额模式下的交易金额" };
            sd.ArgDic["money"] = 10000m;

            // ==================== 颜色配置 ====================
            sd.ColorDic["sub0-Deviation"] = "#2196F3";
            sd.ColorDic["sub0-EntryUpper"] = "#F44336";
            sd.ColorDic["sub0-EntryLower"] = "#4CAF50";
            sd.ColorDic["sub0-ExitUpper"] = "#FF9800";
            sd.ColorDic["sub0-ExitLower"] = "#FF9800";
            sd.ColorDic["sub0-Zero"] = "#666666";

            sd.ColorDic["sub1-CumDeviation"] = "#9C27B0";
            sd.ColorDic["sub1-BenchmarkZero"] = "#666666";

            sd.MidValDic["sub0"] = 0;
            sd.MidValDic["sub1"] = 0;

            return sd;
        }

        #region 全局状态

        /// <summary>
        /// 品种偏离度数据
        /// </summary>
        private class SymbolDeviation
        {
            public string MktSymbol { get; set; }
            public List<double> ReturnHistory { get; set; } = new List<double>();
            public List<double> CumDeviationHistory { get; set; } = new List<double>();
            public double CurrentDeviation { get; set; }
            public double DeviationZScore { get; set; }
            public double Atr { get; set; }
            public decimal LatestPrice { get; set; }
            public bool IsValid { get; set; }
        }

        /// <summary>
        /// 交易状态
        /// </summary>
        private class TradeState
        {
            public int Status { get; set; }          // 0=空仓 1=做多(低估) 2=做空(高估)
            public decimal Num { get; set; }
            public decimal EntryPrice { get; set; }
            public double EntryZScore { get; set; }
            public decimal StopLoss { get; set; }
            public int HoldBars { get; set; }
            public int ConfirmCount { get; set; }    // 连续偏离确认计数
            public int LastSignalDir { get; set; }   // 上一次信号方向
        }

        // 全局偏离度数据
        private Dictionary<string, SymbolDeviation> _deviationDic = new Dictionary<string, SymbolDeviation>();
        // 全局基准收益率历史
        private List<double> _benchmarkReturnHistory = new List<double>();
        // 交易状态
        private Dictionary<string, TradeState> _stateDic = new Dictionary<string, TradeState>();
        // 当前持仓数量
        private int _currentPositions = 0;

        #endregion

        #region OnGlobalIndicator — 全局偏离度计算

        public override void OnGlobalIndicator(List<TableUnit> tableUnitList)
        {
            base.OnGlobalIndicator(tableUnitList);
            if (tableUnitList == null || tableUnitList.Count == 0) return;

            int lookbackPeriod = Convert.ToInt32(ArgDic["lookbackPeriod"]);
            int returnPeriod = Convert.ToInt32(ArgDic["returnPeriod"]);
            int benchmarkMode = Convert.ToInt32(ArgDic["benchmarkMode"]);
            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
            int minSymbols = Convert.ToInt32(ArgDic["minSymbols"]);

            int minBars = lookbackPeriod + returnPeriod + atrPeriod + 10;

            // 收集每个品种数据（取每个品种最小周期）
            var symbolDataDic = new Dictionary<string, List<SkQuote>>();
            foreach (var tu in tableUnitList)
            {
                if (tu.QuoteList == null || tu.QuoteList.Count < minBars) continue;
                if (!symbolDataDic.ContainsKey(tu.MktSymbol))
                {
                    symbolDataDic[tu.MktSymbol] = tu.QuoteList;
                }
            }

            if (symbolDataDic.Count < minSymbols) return;

            // 对齐所有品种的K线长度
            int alignLen = symbolDataDic.Values.Min(q => q.Count);
            alignLen = Math.Min(alignLen, lookbackPeriod + returnPeriod + 50);

            // 计算每个品种的收益率序列
            var symbolReturnsDic = new Dictionary<string, List<double>>();
            foreach (var kvp in symbolDataDic)
            {
                string sym = kvp.Key;
                var quotes = kvp.Value;
                var returns = new List<double>();

                int startIdx = quotes.Count - alignLen;
                for (int i = startIdx + returnPeriod; i < quotes.Count; i++)
                {
                    decimal prevPrice = quotes[i - returnPeriod].Close;
                    if (prevPrice > 0)
                    {
                        returns.Add((double)(quotes[i].Close - prevPrice) / (double)prevPrice);
                    }
                    else
                    {
                        returns.Add(0);
                    }
                }
                symbolReturnsDic[sym] = returns;
            }

            // 确保所有收益率序列等长
            int retLen = symbolReturnsDic.Values.Min(r => r.Count);
            if (retLen < lookbackPeriod) return;

            // 计算每个时点的基准收益率
            var symbols = symbolReturnsDic.Keys.ToList();
            var benchmarkReturns = new List<double>();

            for (int t = 0; t < retLen; t++)
            {
                var allReturnsAtT = symbols.Select(s => symbolReturnsDic[s][t]).ToList();

                double benchmark;
                if (benchmarkMode == 1)
                {
                    // 中位数
                    var sorted = allReturnsAtT.OrderBy(x => x).ToList();
                    int mid = sorted.Count / 2;
                    benchmark = sorted.Count % 2 == 0
                        ? (sorted[mid - 1] + sorted[mid]) / 2.0
                        : sorted[mid];
                }
                else
                {
                    // 等权均值
                    benchmark = allReturnsAtT.Average();
                }

                benchmarkReturns.Add(benchmark);
            }

            // 计算每个品种相对基准的累积偏离度
            foreach (var sym in symbols)
            {
                if (!_deviationDic.ContainsKey(sym))
                {
                    _deviationDic[sym] = new SymbolDeviation { MktSymbol = sym };
                }
                var sd = _deviationDic[sym];

                var returns = symbolReturnsDic[sym];
                var quotes = symbolDataDic[sym];

                sd.LatestPrice = quotes.Last().Close;

                // 计算累积偏离度序列
                var cumDeviation = new List<double>();
                double cumDev = 0;
                for (int t = 0; t < retLen; t++)
                {
                    double excessReturn = returns[t] - benchmarkReturns[t];
                    cumDev += excessReturn;
                    cumDeviation.Add(cumDev);
                }

                sd.CumDeviationHistory = cumDeviation;
                sd.CurrentDeviation = cumDev;

                // 对近期偏离度做Z-Score
                int zLen = Math.Min(lookbackPeriod, cumDeviation.Count);
                var recentDev = cumDeviation.Skip(cumDeviation.Count - zLen).ToList();
                double mean = recentDev.Average();
                double variance = recentDev.Sum(d => (d - mean) * (d - mean)) / zLen;
                double stdDev = Math.Sqrt(variance);

                sd.DeviationZScore = stdDev > 1e-10 ? (cumDev - mean) / stdDev : 0;

                // ATR
                if (quotes.Count > atrPeriod + 5)
                {
                    var atrList = quotes.GetAtr(atrPeriod).ToList();
                    sd.Atr = atrList.Last().Atr ?? 0;
                }

                sd.IsValid = true;
            }

            // 统计持仓数
            _currentPositions = 0;
            foreach (var kvp in _stateDic)
            {
                if (kvp.Value.Status != 0) _currentPositions++;
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

            if (!_deviationDic.ContainsKey(mktSymbol)) return;
            var deviation = _deviationDic[mktSymbol];
            if (!deviation.IsValid) return;

            double entryZScore = Convert.ToDouble(ArgDic["entryZScore"]);
            double exitZScore = Convert.ToDouble(ArgDic["exitZScore"]);
            double stopLossZScore = Convert.ToDouble(ArgDic["stopLossZScore"]);
            int confirmBars = Convert.ToInt32(ArgDic["confirmBars"]);
            int maxHoldBars = Convert.ToInt32(ArgDic["maxHoldBars"]);
            int useTimeDecay = Convert.ToInt32(ArgDic["useTimeDecay"]);
            double atrStopMultiplier = Convert.ToDouble(ArgDic["atrStopMultiplier"]);
            int maxPositions = Convert.ToInt32(ArgDic["maxPositions"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            if (!_stateDic.ContainsKey(sk))
            {
                _stateDic[sk] = new TradeState();
            }
            var state = _stateDic[sk];

            decimal currentPrice = tu.QuoteList.Last().Close;
            double zScore = deviation.DeviationZScore;

            // 绘制指标
            Plot("sub0", "Deviation", PlotType.LINE, zScore);
            Plot("sub0", "EntryUpper", PlotType.XLINE, entryZScore);
            Plot("sub0", "EntryLower", PlotType.XLINE, -entryZScore);
            Plot("sub0", "ExitUpper", PlotType.XLINE, exitZScore);
            Plot("sub0", "ExitLower", PlotType.XLINE, -exitZScore);
            Plot("sub0", "Zero", PlotType.XLINE, 0);

            Plot("sub1", "CumDeviation", PlotType.LINE, deviation.CurrentDeviation * 100);
            Plot("sub1", "BenchmarkZero", PlotType.XLINE, 0);

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

                // Z-Score回归平仓
                if (state.Status == 1 && zScore >= -currentExitZ)
                {
                    exitSignal = true;
                }
                else if (state.Status == 2 && zScore <= currentExitZ)
                {
                    exitSignal = true;
                }

                // Z-Score止损
                if (!exitSignal)
                {
                    if (state.Status == 1 && zScore < -stopLossZScore)
                    {
                        exitSignal = true;
                    }
                    else if (state.Status == 2 && zScore > stopLossZScore)
                    {
                        exitSignal = true;
                    }
                }

                // ATR止损
                if (!exitSignal && state.StopLoss > 0)
                {
                    if (state.Status == 1 && currentPrice <= state.StopLoss)
                    {
                        exitSignal = true;
                    }
                    else if (state.Status == 2 && currentPrice >= state.StopLoss)
                    {
                        exitSignal = true;
                    }
                }

                // 超时平仓
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
                    state.HoldBars = 0;
                    state.ConfirmCount = 0;
                    state.LastSignalDir = 0;
                    _currentPositions--;
                }
            }

            // ==================== 开仓逻辑 ====================
            if (state.Status == 0)
            {
                // 持仓数限制
                if (maxPositions > 0 && _currentPositions >= maxPositions) return;

                // 信号方向
                int signalDir = 0;
                if (zScore < -entryZScore) signalDir = 1;       // 低估 → 做多
                else if (zScore > entryZScore) signalDir = 2;   // 高估 → 做空

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
                    decimal num = CalcLots(mktSymbol, currentPrice, zScore, entryZScore);
                    decimal atrDecimal = (decimal)deviation.Atr;

                    if (signalDir == 1)
                    {
                        state.Status = 1;
                        state.Num = num;
                        state.EntryPrice = currentPrice;
                        state.EntryZScore = zScore;
                        state.HoldBars = 0;
                        if (atrStopMultiplier > 0 && atrDecimal > 0)
                        {
                            state.StopLoss = currentPrice - atrDecimal * (decimal)atrStopMultiplier;
                        }
                        Trade(mktSymbol, OrderType.BUY, currentPrice, num, period, sendMode);
                        _currentPositions++;
                    }
                    else if (signalDir == 2)
                    {
                        state.Status = 2;
                        state.Num = num;
                        state.EntryPrice = currentPrice;
                        state.EntryZScore = zScore;
                        state.HoldBars = 0;
                        if (atrStopMultiplier > 0 && atrDecimal > 0)
                        {
                            state.StopLoss = currentPrice + atrDecimal * (decimal)atrStopMultiplier;
                        }
                        Trade(mktSymbol, OrderType.SELL, currentPrice, num, period, sendMode);
                        _currentPositions++;
                    }

                    state.ConfirmCount = 0;
                }
            }
        }

        #endregion

        #region 工具方法

        private decimal CalcLots(string mktSymbol, decimal price, double zScore, double entryZ)
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
            else if (lotsMode == 2)
            {
                // Z-Score加权：偏离越大仓位越重
                double zWeight = Math.Min(2.0, Math.Abs(zScore) / Math.Max(0.1, entryZ));
                var s = GetSymbol(mktSymbol);
                num = (decimal)(zWeight * (double)(Convert.ToDecimal(ArgDic["money"]) / (price * s.multiplier * s.margin_ratio)));
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

        #endregion
    }
}
