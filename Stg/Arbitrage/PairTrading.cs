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
    /// 配对交易策略 (Pair Trading Strategy)
    /// 
    /// 策略核心思想：
    /// 选取两个高相关性品种，计算价格比值(Ratio)或价差(Spread)的Z-Score，
    /// 当Z-Score偏离均值时做均值回归套利。OnGlobalIndicator中同时获取所有品种数据，
    /// 计算配对关系和信号，OnBar中执行交易。
    /// 
    /// 核心逻辑：
    /// 1. OnGlobalIndicator: 计算所有品种两两之间的价格比值/价差
    /// 2. 使用滚动窗口计算比值的均值和标准差，得到Z-Score
    /// 3. 协整检验(ADF近似): 半衰期过滤，确保价差具有均值回归特性
    /// 4. Z-Score偏离阈值 → 做多低估品种 + 做空高估品种
    /// 5. Z-Score回归 → 同时平仓两腿
    /// 
    /// 入场条件：
    /// - Z-Score > entryZScore → 做空品种A + 做多品种B (比值偏高，A相对高估)
    /// - Z-Score < -entryZScore → 做多品种A + 做空品种B (比值偏低，A相对低估)
    /// 
    /// 出场条件：
    /// - Z-Score回归到exitZScore以内
    /// - 或Z-Score继续偏离超过stopLossZScore (止损)
    /// - 或持仓超过maxHoldBars (超时平仓)
    /// </summary>
    public class PairTrading : StgBase
    {
        public PairTrading()
        {
        }

        public PairTrading(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 2;
            sd.UseGlobalCalc = 1;

            // ==================== 配对参数 ====================
            sd.ArgDescDic["lookbackPeriod"] = new ArgDesc { Text = "回溯周期", Explain = "计算比值均值和标准差的滚动窗口" };
            sd.ArgDic["lookbackPeriod"] = 60;

            sd.ArgDescDic["useAdaptivePeriod"] = new ArgDesc { Text = "自适应周期", Explain = "1=根据半衰期自动调整周期 0=固定周期" };
            sd.ArgDic["useAdaptivePeriod"] = 1;

            sd.ArgDescDic["minLookback"] = new ArgDesc { Text = "最小回溯周期", Explain = "自适应模式下的最小周期" };
            sd.ArgDic["minLookback"] = 20;

            sd.ArgDescDic["maxLookback"] = new ArgDesc { Text = "最大回溯周期", Explain = "自适应模式下的最大周期" };
            sd.ArgDic["maxLookback"] = 120;

            sd.ArgDescDic["spreadMode"] = new ArgDesc { Text = "价差模式", Explain = "0=价格比值(Ratio) 1=价格差(Spread)" };
            sd.ArgDic["spreadMode"] = 0;

            // ==================== Z-Score阈值 ====================
            sd.ArgDescDic["entryZScore"] = new ArgDesc { Text = "入场Z-Score", Explain = "Z-Score绝对值超过此值入场" };
            sd.ArgDic["entryZScore"] = 2.0;

            sd.ArgDescDic["exitZScore"] = new ArgDesc { Text = "出场Z-Score", Explain = "Z-Score绝对值低于此值出场" };
            sd.ArgDic["exitZScore"] = 0.5;

            sd.ArgDescDic["stopLossZScore"] = new ArgDesc { Text = "止损Z-Score", Explain = "Z-Score绝对值超过此值止损" };
            sd.ArgDic["stopLossZScore"] = 3.5;

            // ==================== 半衰期过滤 ====================
            sd.ArgDescDic["useHalfLifeFilter"] = new ArgDesc { Text = "半衰期过滤", Explain = "1=启用 0=禁用" };
            sd.ArgDic["useHalfLifeFilter"] = 1;

            sd.ArgDescDic["minHalfLife"] = new ArgDesc { Text = "最小半衰期", Explain = "半衰期低于此值不入场(回归太快不稳定)" };
            sd.ArgDic["minHalfLife"] = 5;

            sd.ArgDescDic["maxHalfLife"] = new ArgDesc { Text = "最大半衰期", Explain = "半衰期高于此值不入场(回归太慢)" };
            sd.ArgDic["maxHalfLife"] = 60;

            // ==================== 相关性过滤 ====================
            sd.ArgDescDic["minCorrelation"] = new ArgDesc { Text = "最小相关系数", Explain = "品种间相关系数低于此值不配对" };
            sd.ArgDic["minCorrelation"] = 0.7;

            sd.ArgDescDic["correlationPeriod"] = new ArgDesc { Text = "相关系数周期", Explain = "计算滚动相关系数的窗口" };
            sd.ArgDic["correlationPeriod"] = 60;

            // ==================== 风控参数 ====================
            sd.ArgDescDic["maxHoldBars"] = new ArgDesc { Text = "最大持仓K线数", Explain = "超过此数量强制平仓" };
            sd.ArgDic["maxHoldBars"] = 50;

            sd.ArgDescDic["useTimeDecay"] = new ArgDesc { Text = "时间衰减", Explain = "1=持仓越久出场阈值越宽松 0=固定" };
            sd.ArgDic["useTimeDecay"] = 1;

            // ==================== 交易参数 ====================
            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "0=立即 1=下个开盘" };
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "0=固定手数 1=固定金额" };
            sd.ArgDic["lotsMode"] = 1;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "交易手数", Explain = "固定手数模式下的交易数量" };
            sd.ArgDic["lots"] = 1.0m;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "交易金额", Explain = "固定金额模式下的交易金额" };
            sd.ArgDic["money"] = 10000m;

            // ==================== 颜色配置 ====================
            sd.ColorDic["sub0-ZScore"] = "#2196F3";
            sd.ColorDic["sub0-EntryUpper"] = "#F44336";
            sd.ColorDic["sub0-EntryLower"] = "#4CAF50";
            sd.ColorDic["sub0-ExitUpper"] = "#FF9800";
            sd.ColorDic["sub0-ExitLower"] = "#FF9800";
            sd.ColorDic["sub0-Zero"] = "#666666";

            sd.ColorDic["sub1-Ratio"] = "#9C27B0";
            sd.ColorDic["sub1-Mean"] = "#2196F3";
            sd.ColorDic["sub1-UpperBand"] = "#F44336";
            sd.ColorDic["sub1-LowerBand"] = "#4CAF50";

            sd.MidValDic["sub0"] = 0;

            return sd;
        }

        #region 全局状态

        /// <summary>
        /// 配对数据（两个品种之间的关系）
        /// </summary>
        private class PairData
        {
            public string SymbolA { get; set; }
            public string SymbolB { get; set; }
            public List<double> RatioHistory { get; set; } = new List<double>();
            public double ZScore { get; set; }
            public double Mean { get; set; }
            public double StdDev { get; set; }
            public double HalfLife { get; set; }
            public double Correlation { get; set; }
            public double CurrentRatio { get; set; }
            public bool IsValid { get; set; }
        }

        /// <summary>
        /// 交易状态（每个品种的持仓）
        /// </summary>
        private class TradeState
        {
            public int Status { get; set; }          // 0=空仓 1=配对做多A空B 2=配对空A多B
            public decimal NumA { get; set; }
            public decimal NumB { get; set; }
            public double EntryZScore { get; set; }
            public int HoldBars { get; set; }
            public string PairedSymbol { get; set; } // 配对的另一个品种
        }

        // 全局配对数据：key = "SymbolA|SymbolB" (字母序排列保证唯一)
        private Dictionary<string, PairData> _pairDataDic = new Dictionary<string, PairData>();
        // 每个品种的最新收盘价（OnGlobalIndicator中更新）
        private Dictionary<string, decimal> _latestPriceDic = new Dictionary<string, decimal>();
        // 每个品种的交易状态
        private Dictionary<string, TradeState> _stateDic = new Dictionary<string, TradeState>();

        #endregion

        #region OnGlobalIndicator — 跨品种全局计算

        public override void OnGlobalIndicator(List<TableUnit> tableUnitList)
        {
            base.OnGlobalIndicator(tableUnitList);
            if (tableUnitList == null || tableUnitList.Count == 0) return;

            int lookbackPeriod = Convert.ToInt32(ArgDic["lookbackPeriod"]);
            int useAdaptivePeriod = Convert.ToInt32(ArgDic["useAdaptivePeriod"]);
            int minLookback = Convert.ToInt32(ArgDic["minLookback"]);
            int maxLookback = Convert.ToInt32(ArgDic["maxLookback"]);
            int spreadMode = Convert.ToInt32(ArgDic["spreadMode"]);
            double minCorrelation = Convert.ToDouble(ArgDic["minCorrelation"]);
            int correlationPeriod = Convert.ToInt32(ArgDic["correlationPeriod"]);

            // 收集所有品种的最新数据（取每个品种最小周期的TableUnit）
            var symbolDataDic = new Dictionary<string, List<SkQuote>>();
            foreach (var tu in tableUnitList)
            {
                if (tu.QuoteList == null || tu.QuoteList.Count < minLookback) continue;
                if (!symbolDataDic.ContainsKey(tu.MktSymbol) ||
                    (int)tu.Period < (int)tableUnitList.First(t => t.MktSymbol == tu.MktSymbol).Period)
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

            var symbols = symbolDataDic.Keys.ToList();
            if (symbols.Count < 2) return;

            // 两两计算配对关系
            for (int i = 0; i < symbols.Count; i++)
            {
                for (int j = i + 1; j < symbols.Count; j++)
                {
                    string symA = symbols[i];
                    string symB = symbols[j];
                    string pairKey = GetPairKey(symA, symB);

                    var quotesA = symbolDataDic[symA];
                    var quotesB = symbolDataDic[symB];

                    // 对齐K线长度
                    int alignLen = Math.Min(quotesA.Count, quotesB.Count);
                    if (alignLen < minLookback) continue;

                    var pricesA = quotesA.Skip(quotesA.Count - alignLen).Select(q => (double)q.Close).ToList();
                    var pricesB = quotesB.Skip(quotesB.Count - alignLen).Select(q => (double)q.Close).ToList();

                    // 计算滚动相关系数
                    int corrLen = Math.Min(correlationPeriod, alignLen);
                    double correlation = CalcCorrelation(
                        pricesA.Skip(pricesA.Count - corrLen).ToList(),
                        pricesB.Skip(pricesB.Count - corrLen).ToList());

                    if (!_pairDataDic.ContainsKey(pairKey))
                    {
                        _pairDataDic[pairKey] = new PairData { SymbolA = symA, SymbolB = symB };
                    }
                    var pd = _pairDataDic[pairKey];
                    pd.Correlation = correlation;

                    if (Math.Abs(correlation) < minCorrelation)
                    {
                        pd.IsValid = false;
                        continue;
                    }

                    // 计算比值或价差序列
                    var ratioSeries = new List<double>();
                    for (int k = 0; k < alignLen; k++)
                    {
                        if (spreadMode == 0)
                        {
                            // 比值模式
                            if (pricesB[k] > 0)
                                ratioSeries.Add(pricesA[k] / pricesB[k]);
                        }
                        else
                        {
                            // 价差模式
                            ratioSeries.Add(pricesA[k] - pricesB[k]);
                        }
                    }

                    if (ratioSeries.Count < minLookback)
                    {
                        pd.IsValid = false;
                        continue;
                    }

                    pd.RatioHistory = ratioSeries;
                    pd.CurrentRatio = ratioSeries.Last();

                    // 半衰期计算
                    double halfLife = CalcHalfLife(ratioSeries, Math.Min(ratioSeries.Count - 1, lookbackPeriod));
                    pd.HalfLife = halfLife;

                    // 自适应回溯周期
                    int actualLookback = lookbackPeriod;
                    if (useAdaptivePeriod == 1 && halfLife > 0)
                    {
                        actualLookback = (int)Math.Max(minLookback, Math.Min(maxLookback, halfLife * 2));
                    }
                    actualLookback = Math.Min(actualLookback, ratioSeries.Count);
                    if (actualLookback < minLookback)
                    {
                        pd.IsValid = false;
                        continue;
                    }

                    // 计算均值和标准差
                    var recentRatios = ratioSeries.Skip(ratioSeries.Count - actualLookback).ToList();
                    double mean = recentRatios.Average();
                    double variance = recentRatios.Sum(r => (r - mean) * (r - mean)) / actualLookback;
                    double stdDev = Math.Sqrt(variance);

                    pd.Mean = mean;
                    pd.StdDev = stdDev;
                    pd.ZScore = stdDev > 0 ? (pd.CurrentRatio - mean) / stdDev : 0;
                    pd.IsValid = true;
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

            double entryZScore = Convert.ToDouble(ArgDic["entryZScore"]);
            double exitZScore = Convert.ToDouble(ArgDic["exitZScore"]);
            double stopLossZScore = Convert.ToDouble(ArgDic["stopLossZScore"]);
            int useHalfLifeFilter = Convert.ToInt32(ArgDic["useHalfLifeFilter"]);
            int minHalfLife = Convert.ToInt32(ArgDic["minHalfLife"]);
            int maxHalfLife = Convert.ToInt32(ArgDic["maxHalfLife"]);
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

            // 找到该品种的最佳配对
            PairData bestPair = null;
            bool isSymbolA = false;

            foreach (var kvp in _pairDataDic)
            {
                var pd = kvp.Value;
                if (!pd.IsValid) continue;
                if (pd.SymbolA != mktSymbol && pd.SymbolB != mktSymbol) continue;

                // 如果已经有持仓，只关注已配对的品种
                if (state.Status != 0 && state.PairedSymbol != null)
                {
                    string expectedKey = GetPairKey(mktSymbol, state.PairedSymbol);
                    if (kvp.Key != expectedKey) continue;
                }

                // 选Z-Score绝对值最大的配对（套利机会最大）
                if (bestPair == null || Math.Abs(pd.ZScore) > Math.Abs(bestPair.ZScore))
                {
                    bestPair = pd;
                    isSymbolA = (pd.SymbolA == mktSymbol);
                }
            }

            // 绘制指标（用最佳配对的数据）
            if (bestPair != null)
            {
                double displayZ = isSymbolA ? bestPair.ZScore : -bestPair.ZScore;
                Plot("sub0", "ZScore", PlotType.LINE, displayZ);
                Plot("sub0", "EntryUpper", PlotType.XLINE, entryZScore);
                Plot("sub0", "EntryLower", PlotType.XLINE, -entryZScore);
                Plot("sub0", "ExitUpper", PlotType.XLINE, exitZScore);
                Plot("sub0", "ExitLower", PlotType.XLINE, -exitZScore);
                Plot("sub0", "Zero", PlotType.XLINE, 0);

                Plot("sub1", "Ratio", PlotType.LINE, bestPair.CurrentRatio);
                Plot("sub1", "Mean", PlotType.LINE, bestPair.Mean);
                if (bestPair.StdDev > 0)
                {
                    Plot("sub1", "UpperBand", PlotType.LINE, bestPair.Mean + bestPair.StdDev * entryZScore);
                    Plot("sub1", "LowerBand", PlotType.LINE, bestPair.Mean - bestPair.StdDev * entryZScore);
                }
            }

            if (bestPair == null) return;

            // 半衰期过滤
            bool halfLifeOk = useHalfLifeFilter == 0 ||
                              (bestPair.HalfLife >= minHalfLife && bestPair.HalfLife <= maxHalfLife);

            double pairZ = bestPair.ZScore;
            string otherSymbol = isSymbolA ? bestPair.SymbolB : bestPair.SymbolA;
            decimal currentPrice = tu.QuoteList.Last().Close;

            // 计算手数
            decimal num = CalcLots(mktSymbol, currentPrice);

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
                if (state.Status == 1 && isSymbolA)
                {
                    // 做多A空B的头寸，Z-Score从负向回归到0附近
                    if (pairZ >= -currentExitZ) exitSignal = true;
                }
                else if (state.Status == 2 && isSymbolA)
                {
                    // 做空A多B的头寸，Z-Score从正向回归到0附近
                    if (pairZ <= currentExitZ) exitSignal = true;
                }
                else if (state.Status == 1 && !isSymbolA)
                {
                    if (-pairZ >= -currentExitZ) exitSignal = true;
                }
                else if (state.Status == 2 && !isSymbolA)
                {
                    if (-pairZ <= currentExitZ) exitSignal = true;
                }

                // Z-Score继续偏离 → 止损
                if (!exitSignal)
                {
                    if (Math.Abs(pairZ) > stopLossZScore) exitSignal = true;
                }

                // 超时平仓
                if (!exitSignal && state.HoldBars >= maxHoldBars) exitSignal = true;

                if (exitSignal)
                {
                    if (state.Status == 1)
                    {
                        Trade(mktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.NumA, period, sendMode);
                    }
                    else if (state.Status == 2)
                    {
                        Trade(mktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.NumA, period, sendMode);
                    }
                    state.Status = 0;
                    state.NumA = 0;
                    state.NumB = 0;
                    state.PairedSymbol = null;
                    state.HoldBars = 0;
                }
            }

            // ==================== 开仓逻辑 ====================
            if (state.Status == 0 && halfLifeOk)
            {
                // 对于SymbolA:
                //   pairZ > entryZScore → 比值偏高 → A高估 → 空A多B
                //   pairZ < -entryZScore → 比值偏低 → A低估 → 多A空B
                // 对于SymbolB: 反向

                double localZ = isSymbolA ? pairZ : -pairZ;

                if (localZ > entryZScore)
                {
                    // 本品种高估 → 做空
                    state.Status = 2;
                    state.NumA = num;
                    state.EntryZScore = localZ;
                    state.HoldBars = 0;
                    state.PairedSymbol = otherSymbol;
                    Trade(mktSymbol, OrderType.SELL, currentPrice, num, period, sendMode);
                }
                else if (localZ < -entryZScore)
                {
                    // 本品种低估 → 做多
                    state.Status = 1;
                    state.NumA = num;
                    state.EntryZScore = localZ;
                    state.HoldBars = 0;
                    state.PairedSymbol = otherSymbol;
                    Trade(mktSymbol, OrderType.BUY, currentPrice, num, period, sendMode);
                }
            }
        }

        #endregion

        #region 工具方法

        private string GetPairKey(string symA, string symB)
        {
            return string.Compare(symA, symB, StringComparison.Ordinal) <= 0
                ? symA + "|" + symB
                : symB + "|" + symA;
        }

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

        /// <summary>
        /// 半衰期计算（ADF检验近似）
        /// 使用OLS回归: ΔY = β * Y(-1) + ε，半衰期 = -ln(2) / ln(1+β)
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
