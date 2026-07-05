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
    /// 协整套利策略 (Cointegration Arbitrage)
    /// 
    /// 策略核心思想：
    /// 基于Engle-Granger两步法协整检验，找到具有长期均衡关系的品种对，
    /// 构建最优套利组合(hedge ratio)，对残差序列做均值回归交易。
    /// 
    /// 与PairTrading的区别：
    /// - PairTrading: 简单比值/价差，假设对冲比1:1
    /// - CointegrationArbitrage: 使用OLS回归计算最优对冲比例(hedge ratio)，
    ///   残差序列更加平稳，均值回归效果更好
    /// 
    /// 核心逻辑：
    /// 1. OnGlobalIndicator: 对所有品种两两做OLS回归
    ///    Y = α + β*X + ε，其中β即为hedge ratio
    /// 2. 对残差ε做ADF平稳性检验(近似)，筛选协整品种对
    /// 3. 计算残差的Z-Score
    /// 4. Z-Score偏离 → 做空高估腿 + β倍做多低估腿
    /// 5. Z-Score回归 → 平仓
    /// 
    /// 特色：
    /// - 动态更新hedge ratio(滚动OLS)
    /// - 残差平稳性检验(半衰期+方差比检验)
    /// - 对冲比例自动计算，非等量对冲
    /// </summary>
    public class CointegrationArbitrage : StgBase
    {
        public CointegrationArbitrage()
        {
        }

        public CointegrationArbitrage(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 2;
            sd.UseGlobalCalc = 1;

            // ==================== 协整计算参数 ====================
            sd.ArgDescDic["regressionPeriod"] = new ArgDesc { Text = "回归周期", Explain = "OLS回归计算hedge ratio的窗口", Type = "number" };
            sd.ArgDic["regressionPeriod"] = 60;

            sd.ArgDescDic["residualZPeriod"] = new ArgDesc { Text = "残差Z周期", Explain = "计算残差Z-Score的滚动窗口", Type = "number" };
            sd.ArgDic["residualZPeriod"] = 40;

            sd.ArgDescDic["updateInterval"] = new ArgDesc { Text = "更新间隔", Explain = "每隔多少根K线重新计算hedge ratio(0=每根都算)", Type = "number" };
            sd.ArgDic["updateInterval"] = 3;

            // ==================== Z-Score阈值 ====================
            sd.ArgDescDic["entryZScore"] = new ArgDesc { Text = "入场Z-Score", Explain = "残差Z-Score绝对值超过此值入场", Type = "number" };
            sd.ArgDic["entryZScore"] = 2.0;

            sd.ArgDescDic["exitZScore"] = new ArgDesc { Text = "出场Z-Score", Explain = "残差Z-Score绝对值低于此值出场", Type = "number" };
            sd.ArgDic["exitZScore"] = 0.3;

            sd.ArgDescDic["stopLossZScore"] = new ArgDesc { Text = "止损Z-Score", Explain = "残差Z-Score绝对值超过此值止损", Type = "number" };
            sd.ArgDic["stopLossZScore"] = 2.5;

            // ==================== 协整过滤参数 ====================
            sd.ArgDescDic["maxHalfLife"] = new ArgDesc { Text = "最大半衰期", Explain = "残差半衰期超过此值不交易(回归太慢)", Type = "number" };
            sd.ArgDic["maxHalfLife"] = 50;

            sd.ArgDescDic["minHalfLife"] = new ArgDesc { Text = "最小半衰期", Explain = "残差半衰期低于此值不交易(太不稳定)", Type = "number" };
            sd.ArgDic["minHalfLife"] = 3;

            sd.ArgDescDic["useVarianceRatioTest"] = new ArgDesc { Text = "方差比检验", Explain = "辅助判断价差平稳性", Options = "1:启用方差比检验辅助判断平稳性|0:仅用半衰期", Type = "bool" };
            sd.ArgDic["useVarianceRatioTest"] = 1;

            sd.ArgDescDic["varianceRatioThreshold"] = new ArgDesc { Text = "方差比阈值", Explain = "方差比低于此值认为平稳(理想值=1表示随机游走)", Type = "number" };
            sd.ArgDic["varianceRatioThreshold"] = 0.8;

            sd.ArgDescDic["minRSquared"] = new ArgDesc { Text = "最小R²", Explain = "回归R²低于此值不认为有协整关系", Type = "number" };
            sd.ArgDic["minRSquared"] = 0.5;

            // ==================== 风控参数 ====================
            sd.ArgDescDic["confirmBars"] = new ArgDesc { Text = "确认K线数", Explain = "连续N根K线信号一致才入场", Type = "number" };
            sd.ArgDic["confirmBars"] = 1;

            sd.ArgDescDic["maxHoldBars"] = new ArgDesc { Text = "最大持仓K线数", Explain = "超过此数量强制平仓", Type = "number" };
            sd.ArgDic["maxHoldBars"] = 15;

            sd.ArgDescDic["useTimeDecay"] = new ArgDesc { Text = "时间衰减", Explain = "持仓时间衰减", Options = "1:持仓越久出场阈值越宽松|0:固定", Type = "select" };
            sd.ArgDic["useTimeDecay"] = 1;

            // ==================== 交易参数 ====================
            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
            sd.ArgDic["lotsMode"] = 1;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "交易手数", Explain = "固定手数模式下的交易数量", Type = "number" };
            sd.ArgDic["lots"] = 1.0m;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "交易金额", Explain = "固定金额模式下的交易金额", Type = "number" };
            sd.ArgDic["money"] = 10000m;

            // ==================== 颜色配置 ====================
            sd.ColorDic["sub0-ResidualZ"] = "#2196F3";
            sd.ColorDic["sub0-EntryUpper"] = "#F44336";
            sd.ColorDic["sub0-EntryLower"] = "#4CAF50";
            sd.ColorDic["sub0-ExitUpper"] = "#FF9800";
            sd.ColorDic["sub0-ExitLower"] = "#FF9800";
            sd.ColorDic["sub0-Zero"] = "#666666";

            sd.ColorDic["sub1-Residual"] = "#9C27B0";
            sd.ColorDic["sub1-Mean"] = "#2196F3";
            sd.ColorDic["sub1-UpperBand"] = "#F44336";
            sd.ColorDic["sub1-LowerBand"] = "#4CAF50";

            sd.MidValDic["sub0"] = 0;

            return sd;
        }

        #region 全局状态

        /// <summary>
        /// 协整对数据
        /// </summary>
        private class CointPairData
        {
            public string SymbolY { get; set; }      // 因变量品种
            public string SymbolX { get; set; }      // 自变量品种
            public double Alpha { get; set; }        // 截距
            public double Beta { get; set; }         // hedge ratio (对冲比例)
            public double RSquared { get; set; }     // R²
            public List<double> ResidualHistory { get; set; } = new List<double>();
            public double CurrentResidual { get; set; }
            public double ResidualZScore { get; set; }
            public double ResidualMean { get; set; }
            public double ResidualStdDev { get; set; }
            public double HalfLife { get; set; }
            public double VarianceRatio { get; set; }
            public bool IsCointegrated { get; set; }
        }

        /// <summary>
        /// 交易状态
        /// </summary>
        private class TradeState
        {
            public int Status { get; set; }           // 0=空仓 1=做多Y空X 2=做空Y多X
            public decimal Num { get; set; }
            public double EntryZScore { get; set; }
            public int HoldBars { get; set; }
            public int ConfirmCount { get; set; }
            public int LastSignalDir { get; set; }
            public string PairedSymbol { get; set; }
            public double HedgeRatio { get; set; }   // 记录入场时的hedge ratio
            public bool IsCoolingDown { get; set; }  // 是否处于止损冷却期(防止止损后同向立即重开)
            public int CoolDownDir { get; set; }     // 冷却方向: 1=本品种做多腿方向止损 2=本品种做空腿方向止损
        }

        // 协整对数据: key = "SymY|SymX"
        private Dictionary<string, CointPairData> _cointDic = new Dictionary<string, CointPairData>();
        // 交易状态
        private Dictionary<string, TradeState> _stateDic = new Dictionary<string, TradeState>();
        // 更新计数器
        private int _barsSinceUpdate = 0;

        #endregion

        #region OnGlobalIndicator — 全局协整关系计算

        public override void OnGlobalIndicator(List<TableUnit> tableUnitList)
        {
            base.OnGlobalIndicator(tableUnitList);
            if (tableUnitList == null || tableUnitList.Count == 0) return;

            int regressionPeriod = Convert.ToInt32(ArgDic["regressionPeriod"]);
            int residualZPeriod = Convert.ToInt32(ArgDic["residualZPeriod"]);
            int updateInterval = Convert.ToInt32(ArgDic["updateInterval"]);
            double maxHalfLife = Convert.ToDouble(ArgDic["maxHalfLife"]);
            double minHalfLife = Convert.ToDouble(ArgDic["minHalfLife"]);
            int useVarianceRatioTest = Convert.ToInt32(ArgDic["useVarianceRatioTest"]);
            double varianceRatioThreshold = Convert.ToDouble(ArgDic["varianceRatioThreshold"]);
            double minRSquared = Convert.ToDouble(ArgDic["minRSquared"]);

            _barsSinceUpdate++;
            bool shouldUpdate = updateInterval == 0 || _barsSinceUpdate >= updateInterval;

            int minBars = regressionPeriod + residualZPeriod + 20;

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

            if (!shouldUpdate)
            {
                // 仅更新残差Z-Score，不重新计算hedge ratio
                UpdateResidualZScores(symbolDataDic, residualZPeriod);
                return;
            }

            _barsSinceUpdate = 0;

            // 两两做OLS回归
            for (int i = 0; i < symbols.Count; i++)
            {
                for (int j = i + 1; j < symbols.Count; j++)
                {
                    string symA = symbols[i];
                    string symB = symbols[j];

                    // 尝试A=Y, B=X
                    CalcCointegration(symA, symB, symbolDataDic, regressionPeriod, residualZPeriod,
                        minHalfLife, maxHalfLife, useVarianceRatioTest, varianceRatioThreshold, minRSquared);

                    // 尝试B=Y, A=X (选R²更高的)
                    CalcCointegration(symB, symA, symbolDataDic, regressionPeriod, residualZPeriod,
                        minHalfLife, maxHalfLife, useVarianceRatioTest, varianceRatioThreshold, minRSquared);
                }
            }
        }

        /// <summary>
        /// 计算两个品种的协整关系: Y = α + β*X + ε
        /// </summary>
        private void CalcCointegration(string symY, string symX,
            Dictionary<string, List<SkQuote>> symbolDataDic,
            int regressionPeriod, int residualZPeriod,
            double minHalfLife, double maxHalfLife,
            int useVarianceRatioTest, double varianceRatioThreshold, double minRSquared)
        {
            var quotesY = symbolDataDic[symY];
            var quotesX = symbolDataDic[symX];

            int alignLen = Math.Min(quotesY.Count, quotesX.Count);
            if (alignLen < regressionPeriod + residualZPeriod) return;

            var pricesY = quotesY.Skip(quotesY.Count - alignLen).Select(q => (double)q.Close).ToList();
            var pricesX = quotesX.Skip(quotesX.Count - alignLen).Select(q => (double)q.Close).ToList();

            // OLS回归: Y = α + β*X
            int regStart = alignLen - regressionPeriod;
            var regY = pricesY.Skip(regStart).Take(regressionPeriod).ToList();
            var regX = pricesX.Skip(regStart).Take(regressionPeriod).ToList();

            double meanY = regY.Average();
            double meanX = regX.Average();

            double covXY = 0, varX = 0;
            for (int k = 0; k < regressionPeriod; k++)
            {
                covXY += (regX[k] - meanX) * (regY[k] - meanY);
                varX += (regX[k] - meanX) * (regX[k] - meanX);
            }

            if (varX < 1e-10) return;

            double beta = covXY / varX;
            double alpha = meanY - beta * meanX;

            // 计算R²
            double ssRes = 0, ssTot = 0;
            for (int k = 0; k < regressionPeriod; k++)
            {
                double predicted = alpha + beta * regX[k];
                ssRes += (regY[k] - predicted) * (regY[k] - predicted);
                ssTot += (regY[k] - meanY) * (regY[k] - meanY);
            }
            double rSquared = ssTot > 0 ? 1.0 - ssRes / ssTot : 0;

            // 计算残差序列
            var residuals = new List<double>();
            for (int k = 0; k < alignLen; k++)
            {
                residuals.Add(pricesY[k] - alpha - beta * pricesX[k]);
            }

            // 半衰期
            int hlLookback = Math.Min(residuals.Count - 1, regressionPeriod);
            double halfLife = CalcHalfLife(residuals, hlLookback);

            // 方差比检验
            double varianceRatio = 1.0;
            if (useVarianceRatioTest == 1)
            {
                varianceRatio = CalcVarianceRatio(residuals, Math.Min(10, residuals.Count / 4));
            }

            // 判断是否协整
            bool isCointegrated = rSquared >= minRSquared &&
                                  halfLife >= minHalfLife &&
                                  halfLife <= maxHalfLife;

            if (useVarianceRatioTest == 1)
            {
                isCointegrated = isCointegrated && varianceRatio < varianceRatioThreshold;
            }

            // 残差Z-Score
            int zLen = Math.Min(residualZPeriod, residuals.Count);
            var recentResiduals = residuals.Skip(residuals.Count - zLen).ToList();
            double resMean = recentResiduals.Average();
            double resVariance = recentResiduals.Sum(r => (r - resMean) * (r - resMean)) / zLen;
            double resStdDev = Math.Sqrt(resVariance);
            double currentResidual = residuals.Last();
            double residualZ = resStdDev > 1e-10 ? (currentResidual - resMean) / resStdDev : 0;

            string pairKey = symY + "|" + symX;

            // 如果已有反向的协整对且R²更高，保留更好的
            string reverseKey = symX + "|" + symY;
            if (_cointDic.ContainsKey(reverseKey) && _cointDic[reverseKey].RSquared > rSquared)
            {
                return; // 反向回归更好，不覆盖
            }

            _cointDic[pairKey] = new CointPairData
            {
                SymbolY = symY,
                SymbolX = symX,
                Alpha = alpha,
                Beta = beta,
                RSquared = rSquared,
                ResidualHistory = residuals,
                CurrentResidual = currentResidual,
                ResidualZScore = residualZ,
                ResidualMean = resMean,
                ResidualStdDev = resStdDev,
                HalfLife = halfLife,
                VarianceRatio = varianceRatio,
                IsCointegrated = isCointegrated
            };
        }

        /// <summary>
        /// 仅更新残差Z-Score（不重新计算hedge ratio）
        /// </summary>
        private void UpdateResidualZScores(Dictionary<string, List<SkQuote>> symbolDataDic, int residualZPeriod)
        {
            foreach (var kvp in _cointDic)
            {
                var cp = kvp.Value;
                if (!symbolDataDic.ContainsKey(cp.SymbolY) || !symbolDataDic.ContainsKey(cp.SymbolX)) continue;

                var quotesY = symbolDataDic[cp.SymbolY];
                var quotesX = symbolDataDic[cp.SymbolX];

                double priceY = (double)quotesY.Last().Close;
                double priceX = (double)quotesX.Last().Close;

                cp.CurrentResidual = priceY - cp.Alpha - cp.Beta * priceX;
                cp.ResidualHistory.Add(cp.CurrentResidual);
                if (cp.ResidualHistory.Count > residualZPeriod * 3)
                {
                    cp.ResidualHistory.RemoveRange(0, cp.ResidualHistory.Count - residualZPeriod * 2);
                }

                int zLen = Math.Min(residualZPeriod, cp.ResidualHistory.Count);
                var recent = cp.ResidualHistory.Skip(cp.ResidualHistory.Count - zLen).ToList();
                double mean = recent.Average();
                double variance = recent.Sum(r => (r - mean) * (r - mean)) / zLen;
                double stdDev = Math.Sqrt(variance);
                cp.ResidualMean = mean;
                cp.ResidualStdDev = stdDev;
                cp.ResidualZScore = stdDev > 1e-10 ? (cp.CurrentResidual - mean) / stdDev : 0;
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

            // 找到该品种的最佳协整对
            CointPairData bestPair = null;
            bool isSymbolY = false;

            foreach (var kvp in _cointDic)
            {
                var cp = kvp.Value;
                if (!cp.IsCointegrated) continue;
                if (cp.SymbolY != mktSymbol && cp.SymbolX != mktSymbol) continue;

                // 已持仓时只关注原配对
                if (state.Status != 0 && state.PairedSymbol != null)
                {
                    string otherSym = cp.SymbolY == mktSymbol ? cp.SymbolX : cp.SymbolY;
                    if (otherSym != state.PairedSymbol) continue;
                }

                if (bestPair == null || Math.Abs(cp.ResidualZScore) > Math.Abs(bestPair.ResidualZScore))
                {
                    bestPair = cp;
                    isSymbolY = (cp.SymbolY == mktSymbol);
                }
            }

            // 绘制指标
            if (bestPair != null)
            {
                double displayZ = bestPair.ResidualZScore;
                Plot("sub0", "ResidualZ", PlotType.LINE, displayZ);
                Plot("sub0", "EntryUpper", PlotType.XLINE, entryZScore);
                Plot("sub0", "EntryLower", PlotType.XLINE, -entryZScore);
                Plot("sub0", "ExitUpper", PlotType.XLINE, exitZScore);
                Plot("sub0", "ExitLower", PlotType.XLINE, -exitZScore);
                Plot("sub0", "Zero", PlotType.XLINE, 0);

                Plot("sub1", "Residual", PlotType.LINE, bestPair.CurrentResidual);
                Plot("sub1", "Mean", PlotType.LINE, bestPair.ResidualMean);
                if (bestPair.ResidualStdDev > 0)
                {
                    Plot("sub1", "UpperBand", PlotType.LINE, bestPair.ResidualMean + bestPair.ResidualStdDev * entryZScore);
                    Plot("sub1", "LowerBand", PlotType.LINE, bestPair.ResidualMean - bestPair.ResidualStdDev * entryZScore);
                }
            }

            if (bestPair == null) return;

            double resZ = bestPair.ResidualZScore;
            string otherSymbol = isSymbolY ? bestPair.SymbolX : bestPair.SymbolY;

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
                bool stopLossHit = false;

                // 残差Z-Score回归
                if (Math.Abs(resZ) <= currentExitZ) exitSignal = true;

                // 残差Z-Score止损
                if (!exitSignal && Math.Abs(resZ) > stopLossZScore)
                {
                    exitSignal = true;
                    stopLossHit = true;
                }

                // 超时
                if (!exitSignal && state.HoldBars >= maxHoldBars) exitSignal = true;

                if (exitSignal)
                {
                    if (state.Status == 1)
                    {
                        // 做多Y空X → 对Y平多
                        if (isSymbolY)
                            Trade(mktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.Num, period, sendMode);
                        else
                            Trade(mktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.Num, period, sendMode);
                    }
                    else if (state.Status == 2)
                    {
                        // 做空Y多X → 对Y平空
                        if (isSymbolY)
                            Trade(mktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.Num, period, sendMode);
                        else
                            Trade(mktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.Num, period, sendMode);
                    }
                    int prevStatus = state.Status;
                    state.Status = 0;
                    state.Num = 0;
                    state.HoldBars = 0;
                    state.PairedSymbol = null;
                    state.ConfirmCount = 0;
                    state.LastSignalDir = 0;

                    if (stopLossHit)
                    {
                        // 止损后开启同向冷却并立即返回,确保同一根K线不会再进入开仓块同向重开
                        state.IsCoolingDown = true;
                        state.CoolDownDir = prevStatus;
                        return;
                    }
                }
            }

            // ==================== 冷却解除 ====================
            // 止损冷却:残差Z回到入场阈值以内(中性区)或反向穿越0才解除,杜绝止损后原地同向重开
            if (state.Status == 0 && state.IsCoolingDown)
            {
                // 冷却方向信号所在的残差Z侧: Y腿做空(2)/X腿做多(1)对应resZ>0侧,反之对应resZ<0侧
                int coolSide = ((state.CoolDownDir == 2) == isSymbolY) ? 1 : -1;
                if (Math.Abs(resZ) <= entryZScore ||
                    (coolSide == 1 && resZ <= 0) ||
                    (coolSide == -1 && resZ >= 0))
                {
                    state.IsCoolingDown = false;
                    state.CoolDownDir = 0;
                }
            }

            // ==================== 开仓逻辑 ====================
            if (state.Status == 0)
            {
                // resZ > entry → 残差偏高 → Y相对X高估 → 空Y多X
                // resZ < -entry → 残差偏低 → Y相对X低估 → 多Y空X
                // 对于SymbolY: 直接按Z-Score方向
                // 对于SymbolX: 反向(X是对冲腿)

                int signalDir = 0;
                if (isSymbolY)
                {
                    if (resZ > entryZScore) signalDir = 2;       // Y高估→空Y
                    else if (resZ < -entryZScore) signalDir = 1; // Y低估→多Y
                }
                else
                {
                    if (resZ > entryZScore) signalDir = 1;       // Y高估→多X(对冲)
                    else if (resZ < -entryZScore) signalDir = 2; // Y低估→空X(对冲)
                }

                // 冷却期内屏蔽同向信号(反方向信号不受影响)
                if (state.IsCoolingDown && signalDir == state.CoolDownDir) signalDir = 0;

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
                    // 计算手数：X腿需要按hedge ratio调整
                    decimal baseNum = CalcLots(mktSymbol, currentPrice);
                    decimal tradeNum;
                    if (isSymbolY)
                    {
                        tradeNum = baseNum;
                    }
                    else
                    {
                        // X腿: num * |beta| (对冲比例)
                        tradeNum = baseNum * Math.Max(0.001m, (decimal)Math.Abs(bestPair.Beta));
                        var sym = GetSymbol(mktSymbol);
                        if (sym.symbol_type == (int)SymbolType.COIN)
                        {
                            tradeNum = (int)(tradeNum * sym.scale) / (decimal)sym.scale;
                        }
                        else
                        {
                            tradeNum = Math.Max(1, (int)tradeNum);
                        }
                    }
                    tradeNum = Math.Max(0.001m, tradeNum);

                    if (signalDir == 1)
                    {
                        state.Status = 1;
                        state.Num = tradeNum;
                        state.EntryZScore = resZ;
                        state.HoldBars = 0;
                        state.PairedSymbol = otherSymbol;
                        state.HedgeRatio = bestPair.Beta;
                        Trade(mktSymbol, OrderType.BUY, currentPrice, tradeNum, period, sendMode);
                    }
                    else if (signalDir == 2)
                    {
                        state.Status = 2;
                        state.Num = tradeNum;
                        state.EntryZScore = resZ;
                        state.HoldBars = 0;
                        state.PairedSymbol = otherSymbol;
                        state.HedgeRatio = bestPair.Beta;
                        Trade(mktSymbol, OrderType.SELL, currentPrice, tradeNum, period, sendMode);
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
                var sym = GetSymbol(mktSymbol);
                num = (Convert.ToDecimal(ArgDic["money"]) / (price * sym.multiplier * sym.margin_ratio));
                if (sym.symbol_type == (int)SymbolType.COIN)
                {
                    num = (int)(num * sym.scale) / (decimal)sym.scale;
                }
                else
                {
                    num = (int)num;
                }
            }
            return Math.Max(0.001m, num);
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

        /// <summary>
        /// 方差比检验 (Lo-MacKinlay Variance Ratio Test)
        /// VR(q) = Var(q期收益) / (q * Var(1期收益))
        /// VR ≈ 1 → 随机游走(不平稳)；VR < 1 → 均值回归(平稳)
        /// </summary>
        private double CalcVarianceRatio(List<double> series, int q)
        {
            if (series.Count < q * 2 || q < 2) return 1.0;

            // 1期差分
            var diff1 = new List<double>();
            for (int i = 1; i < series.Count; i++)
            {
                diff1.Add(series[i] - series[i - 1]);
            }

            // q期差分
            var diffQ = new List<double>();
            for (int i = q; i < series.Count; i++)
            {
                diffQ.Add(series[i] - series[i - q]);
            }

            if (diff1.Count == 0 || diffQ.Count == 0) return 1.0;

            double var1 = CalcVariance(diff1);
            double varQ = CalcVariance(diffQ);

            if (var1 < 1e-10) return 1.0;
            return varQ / (q * var1);
        }

        private double CalcVariance(List<double> data)
        {
            double mean = data.Average();
            return data.Sum(d => (d - mean) * (d - mean)) / data.Count;
        }

        #endregion
    }
}
