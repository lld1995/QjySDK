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
    /// 统计套利策略 (Statistical Arbitrage Strategy)
    /// 
    /// 策略核心思想：
    /// 利用价格与其统计均值之间的偏离进行交易，当价格显著偏离均值时入场，
    /// 等待价格回归均值时获利。该策略被D.E. Shaw、Two Sigma等顶级量化机构广泛使用。
    /// 
    /// 核心逻辑：
    /// 1. Z-Score计算：标准化价格偏离度 = (价格 - 均值) / 标准差
    /// 2. 协整检验：使用半衰期判断均值回归速度
    /// 3. 动态阈值：根据市场波动率调整入场阈值
    /// 4. 风险控制：最大偏离度止损、时间止损
    /// 
    /// 入场条件（做多）：
    /// - Z-Score < -入场阈值（价格显著低于均值）
    /// - 半衰期在合理范围内（确保会回归）
    /// - RSI确认超卖
    /// 
    /// 入场条件（做空）：
    /// - Z-Score > 入场阈值（价格显著高于均值）
    /// - 半衰期在合理范围内
    /// - RSI确认超买
    /// 
    /// 出场条件：
    /// - Z-Score回归到出场阈值
    /// - 最大持仓时间
    /// - Z-Score继续偏离超过止损阈值
    /// 
    /// 特色功能：
    /// - 自适应均值周期
    /// - 动态波动率调整
    /// - 半衰期过滤
    /// - 多重确认机制
    /// </summary>
    public class StatisticalArbitrage : StgBase
    {
        public StatisticalArbitrage()
        {
        }

        public StatisticalArbitrage(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 2;
            sd.UseGlobalCalc = 0;

            // ==================== 均值回归参数 ====================
            sd.ArgDescDic["lookbackPeriod"] = new ArgDesc { Text = "回溯周期", Explain = "计算均值和标准差的周期" };
            sd.ArgDic["lookbackPeriod"] = 60;

            sd.ArgDescDic["useAdaptivePeriod"] = new ArgDesc { Text = "自适应周期", Explain = "1=根据半衰期自动调整周期 0=固定周期" };
            sd.ArgDic["useAdaptivePeriod"] = 1;

            sd.ArgDescDic["minLookback"] = new ArgDesc { Text = "最小回溯周期", Explain = "自适应模式下的最小周期" };
            sd.ArgDic["minLookback"] = 20;

            sd.ArgDescDic["maxLookback"] = new ArgDesc { Text = "最大回溯周期", Explain = "自适应模式下的最大周期" };
            sd.ArgDic["maxLookback"] = 120;

            // ==================== Z-Score阈值参数 ====================
            sd.ArgDescDic["entryZScore"] = new ArgDesc { Text = "入场Z-Score", Explain = "Z-Score绝对值超过此值入场" };
            sd.ArgDic["entryZScore"] = 2.0;

            sd.ArgDescDic["exitZScore"] = new ArgDesc { Text = "出场Z-Score", Explain = "Z-Score绝对值低于此值出场" };
            sd.ArgDic["exitZScore"] = 0.5;

            sd.ArgDescDic["stopLossZScore"] = new ArgDesc { Text = "止损Z-Score", Explain = "Z-Score绝对值超过此值止损" };
            sd.ArgDic["stopLossZScore"] = 3.5;

            sd.ArgDescDic["useDynamicThreshold"] = new ArgDesc { Text = "动态阈值", Explain = "1=根据波动率调整阈值 0=固定阈值" };
            sd.ArgDic["useDynamicThreshold"] = 1;

            // ==================== 半衰期参数 ====================
            sd.ArgDescDic["useHalfLifeFilter"] = new ArgDesc { Text = "半衰期过滤", Explain = "1=启用 0=禁用" };
            sd.ArgDic["useHalfLifeFilter"] = 1;

            sd.ArgDescDic["minHalfLife"] = new ArgDesc { Text = "最小半衰期", Explain = "半衰期低于此值不入场(回归太快)" };
            sd.ArgDic["minHalfLife"] = 5;

            sd.ArgDescDic["maxHalfLife"] = new ArgDesc { Text = "最大半衰期", Explain = "半衰期高于此值不入场(回归太慢)" };
            sd.ArgDic["maxHalfLife"] = 60;

            // ==================== 确认指标参数 ====================
            sd.ArgDescDic["rsiPeriod"] = new ArgDesc { Text = "RSI周期", Explain = "RSI计算周期" };
            sd.ArgDic["rsiPeriod"] = 14;

            sd.ArgDescDic["rsiOverbought"] = new ArgDesc { Text = "RSI超买线", Explain = "RSI超买阈值" };
            sd.ArgDic["rsiOverbought"] = 70.0;

            sd.ArgDescDic["rsiOversold"] = new ArgDesc { Text = "RSI超卖线", Explain = "RSI超卖阈值" };
            sd.ArgDic["rsiOversold"] = 30.0;

            sd.ArgDescDic["useRsiConfirm"] = new ArgDesc { Text = "RSI确认", Explain = "1=需要RSI确认 0=不需要" };
            sd.ArgDic["useRsiConfirm"] = 1;

            sd.ArgDescDic["bollPeriod"] = new ArgDesc { Text = "布林带周期", Explain = "布林带计算周期" };
            sd.ArgDic["bollPeriod"] = 20;

            sd.ArgDescDic["bollStdDev"] = new ArgDesc { Text = "布林带标准差", Explain = "布林带标准差倍数" };
            sd.ArgDic["bollStdDev"] = 2.0;

            // ==================== 风控参数 ====================
            sd.ArgDescDic["atrPeriod"] = new ArgDesc { Text = "ATR周期", Explain = "ATR计算周期" };
            sd.ArgDic["atrPeriod"] = 14;

            sd.ArgDescDic["maxHoldBars"] = new ArgDesc { Text = "最大持仓K线数", Explain = "超过此数量强制平仓" };
            sd.ArgDic["maxHoldBars"] = 30;

            sd.ArgDescDic["useTimeDecay"] = new ArgDesc { Text = "时间衰减", Explain = "1=持仓越久出场阈值越宽松 0=固定" };
            sd.ArgDic["useTimeDecay"] = 1;

            // ==================== 交易参数 ====================
            sd.ArgDescDic["mode"] = new ArgDesc { Text = "交易模式", Explain = "0=双向 1=仅做多 2=仅做空" };
            sd.ArgDic["mode"] = 0;

            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "0=立即 1=下个开盘" };
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "0=固定手数 1=固定金额 2=Z-Score加权" };
            sd.ArgDic["lotsMode"] = 1;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "交易手数", Explain = "固定手数模式下的交易数量" };
            sd.ArgDic["lots"] = 1.0m;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "交易金额", Explain = "固定金额模式下的交易金额" };
            sd.ArgDic["money"] = 10000m;

            // ==================== 颜色配置 ====================
            // 主图
            sd.ColorDic["main-Mean"] = "#2196F3";
            sd.ColorDic["main-UpperBand"] = "#4CAF50";
            sd.ColorDic["main-LowerBand"] = "#F44336";
            sd.ColorDic["main-Boll_Upper"] = "#9C27B0";
            sd.ColorDic["main-Boll_Lower"] = "#9C27B0";

            // 副图0：Z-Score
            sd.ColorDic["sub0-ZScore"] = "#2196F3";
            sd.ColorDic["sub0-EntryUpper"] = "#F44336";
            sd.ColorDic["sub0-EntryLower"] = "#4CAF50";
            sd.ColorDic["sub0-ExitUpper"] = "#FF9800";
            sd.ColorDic["sub0-ExitLower"] = "#FF9800";

            // 副图1：RSI
            sd.ColorDic["sub1-RSI"] = "#E91E63";
            sd.ColorDic["sub1-Overbought"] = "#F44336";
            sd.ColorDic["sub1-Oversold"] = "#4CAF50";

            // 中值线
            sd.MidValDic["sub0"] = 0;
            sd.MidValDic["sub1"] = 50;

            return sd;
        }

        private class TradeState
        {
            public int Status { get; set; }           // 0=空仓 1=多头 2=空头
            public decimal Num { get; set; }          // 持仓数量
            public decimal EntryPrice { get; set; }   // 入场价格
            public double EntryZScore { get; set; }   // 入场时Z-Score
            public int HoldBars { get; set; }         // 持仓K线数
            public double MaxZScore { get; set; }     // 持仓期间最大Z-Score绝对值

            public void Reset()
            {
                Status = 0;
                Num = 0;
                EntryPrice = 0;
                EntryZScore = 0;
                HoldBars = 0;
                MaxZScore = 0;
            }
        }

        private Dictionary<string, TradeState> _stateDic = new Dictionary<string, TradeState>();
        private Dictionary<string, List<double>> _priceHistory = new Dictionary<string, List<double>>();

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (!isFinal) return;

            if (ArgDic == null) return;

            // 获取参数
            int lookbackPeriod = Convert.ToInt32(ArgDic["lookbackPeriod"]);
            int useAdaptivePeriod = Convert.ToInt32(ArgDic["useAdaptivePeriod"]);
            int minLookback = Convert.ToInt32(ArgDic["minLookback"]);
            int maxLookback = Convert.ToInt32(ArgDic["maxLookback"]);

            double entryZScore = Convert.ToDouble(ArgDic["entryZScore"]);
            double exitZScore = Convert.ToDouble(ArgDic["exitZScore"]);
            double stopLossZScore = Convert.ToDouble(ArgDic["stopLossZScore"]);
            int useDynamicThreshold = Convert.ToInt32(ArgDic["useDynamicThreshold"]);

            int useHalfLifeFilter = Convert.ToInt32(ArgDic["useHalfLifeFilter"]);
            int minHalfLife = Convert.ToInt32(ArgDic["minHalfLife"]);
            int maxHalfLife = Convert.ToInt32(ArgDic["maxHalfLife"]);

            int rsiPeriod = Convert.ToInt32(ArgDic["rsiPeriod"]);
            double rsiOverbought = Convert.ToDouble(ArgDic["rsiOverbought"]);
            double rsiOversold = Convert.ToDouble(ArgDic["rsiOversold"]);
            int useRsiConfirm = Convert.ToInt32(ArgDic["useRsiConfirm"]);

            int bollPeriod = Convert.ToInt32(ArgDic["bollPeriod"]);
            double bollStdDev = Convert.ToDouble(ArgDic["bollStdDev"]);

            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
            int maxHoldBars = Convert.ToInt32(ArgDic["maxHoldBars"]);
            int useTimeDecay = Convert.ToInt32(ArgDic["useTimeDecay"]);

            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            // 计算最小K线数
            int minBars = Math.Max(Math.Max(maxLookback, bollPeriod), Math.Max(rsiPeriod, atrPeriod)) + 10;
            if (tu.QuoteList.Count < minBars) return;

            var quotes = tu.QuoteList;
            var q = quotes.Last();
            decimal currentPrice = q.Close;
            var sk = tu.GetStateKey();

            // 维护价格历史
            if (!_priceHistory.ContainsKey(sk))
            {
                _priceHistory[sk] = new List<double>();
            }
            var priceHist = _priceHistory[sk];
            priceHist.Add((double)currentPrice);
            if (priceHist.Count > maxLookback + 50)
            {
                priceHist.RemoveAt(0);
            }

            // ==================== 计算半衰期 ====================
            double halfLife = CalculateHalfLife(priceHist, Math.Min(priceHist.Count - 1, lookbackPeriod));

            // 自适应周期调整
            int actualLookback = lookbackPeriod;
            if (useAdaptivePeriod == 1 && halfLife > 0)
            {
                // 半衰期越长，回溯周期越长
                actualLookback = (int)Math.Max(minLookback, Math.Min(maxLookback, halfLife * 2));
            }

            // 确保actualLookback不超过可用数据量，避免指标断线
            actualLookback = Math.Min(actualLookback, priceHist.Count);
            if (actualLookback < minLookback) return; // 数据量不足最小周期时才跳过

            // ==================== 计算Z-Score ====================
            double mean = 0;
            for (int i = 0; i < actualLookback; i++)
            {
                mean += priceHist[priceHist.Count - 1 - i];
            }
            mean /= actualLookback;

            double variance = 0;
            for (int i = 0; i < actualLookback; i++)
            {
                double diff = priceHist[priceHist.Count - 1 - i] - mean;
                variance += diff * diff;
            }
            variance /= actualLookback;
            double stdDev = Math.Sqrt(variance);

            double zScore = stdDev > 0 ? ((double)currentPrice - mean) / stdDev : 0;

            // ==================== 动态阈值调整 ====================
            double dynamicEntryZ = entryZScore;
            double dynamicExitZ = exitZScore;
            double dynamicStopZ = stopLossZScore;

            if (useDynamicThreshold == 1)
            {
                // 根据近期波动率调整阈值
                var atrList = quotes.GetAtr(atrPeriod).ToList();
                double atrVal = atrList.Last().Atr.GetValueOrDefault(0);
                double atrPercent = atrVal / (double)currentPrice * 100;

                // 波动率高时提高入场阈值，波动率低时降低入场阈值
                double volAdjust = Math.Max(0.5, Math.Min(1.5, atrPercent / 2.0));
                dynamicEntryZ = entryZScore * volAdjust;
                // 止损阈值只允许收紧，不允许放宽（波动率高时不放大止损阈值）
                dynamicStopZ = stopLossZScore * Math.Min(1.0, volAdjust);
            }

            // ==================== 计算确认指标 ====================
            // RSI
            var rsiList = quotes.GetRsi(rsiPeriod).ToList();
            double rsiVal = rsiList.Last().Rsi.GetValueOrDefault(50);

            // 布林带
            var bollList = quotes.GetBollingerBands(bollPeriod, bollStdDev).ToList();
            var bollLast = bollList.Last();
            double bollUpper = bollLast.UpperBand.GetValueOrDefault(0);
            double bollLower = bollLast.LowerBand.GetValueOrDefault(0);

            double upperBand = mean + stdDev * dynamicEntryZ;
            double lowerBand = mean - stdDev * dynamicEntryZ;

            // ==================== 获取或创建状态 ====================
            if (!_stateDic.ContainsKey(sk))
            {
                _stateDic[sk] = new TradeState();
            }
            var state = _stateDic[sk];

            // ==================== 绘制指标 ====================
            Plot("main", "Mean", PlotType.LINE, mean);
            Plot("main", "UpperBand", PlotType.LINE, upperBand);
            Plot("main", "LowerBand", PlotType.LINE, lowerBand);
            Plot("main", "Boll_Upper", PlotType.LINE, bollUpper);
            Plot("main", "Boll_Lower", PlotType.LINE, bollLower);

            Plot("sub0", "ZScore", PlotType.LINE, zScore);
            Plot("sub0", "EntryUpper", PlotType.LINE, dynamicEntryZ);
            Plot("sub0", "EntryLower", PlotType.LINE, -dynamicEntryZ);
            Plot("sub0", "ExitUpper", PlotType.LINE, dynamicExitZ);
            Plot("sub0", "ExitLower", PlotType.LINE, -dynamicExitZ);

            Plot("sub1", "RSI", PlotType.LINE, rsiVal);
            Plot("sub1", "Overbought", PlotType.LINE, rsiOverbought);
            Plot("sub1", "Oversold", PlotType.LINE, rsiOversold);

            // 更新持仓状态
            if (state.Status != 0)
            {
                state.HoldBars++;
                if (Math.Abs(zScore) > state.MaxZScore)
                {
                    state.MaxZScore = Math.Abs(zScore);
                }
            }

            // ==================== 计算手数 ====================
            var num = (decimal)ArgDic["lots"];
            var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            if (lotsMode == 1)
            {
                var s2 = GetSymbol(tu.MktSymbol);
                num = ((decimal)ArgDic["money"] / (q.Close * s2.multiplier * s2.margin_ratio));
                if (s2.symbol_type == (int)SymbolType.COIN)
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
                // Z-Score加权：偏离越大仓位越大
                double zWeight = Math.Min(2.0, Math.Abs(zScore) / dynamicEntryZ);
                var s2 = GetSymbol(tu.MktSymbol);
                num = (decimal)(zWeight * (double)((decimal)ArgDic["money"] / (q.Close * s2.multiplier * s2.margin_ratio)));
                if (s2.symbol_type == (int)SymbolType.COIN)
                {
                    num = (int)(num * 1000) / 1000.0m;
                }
                else
                {
                    num = (int)num;
                }
            }

            // ==================== 信号判断 ====================
            // 半衰期过滤
            bool halfLifeOk = useHalfLifeFilter == 0 || (halfLife >= minHalfLife && halfLife <= maxHalfLife);

            // 做多信号：Z-Score显著为负（价格低于均值）
            bool longSignal = zScore < -dynamicEntryZ && halfLifeOk && mode != 2;
            if (useRsiConfirm == 1)
            {
                longSignal = longSignal && rsiVal < rsiOversold;
            }

            // 做空信号：Z-Score显著为正（价格高于均值）
            bool shortSignal = zScore > dynamicEntryZ && halfLifeOk && mode != 1;
            if (useRsiConfirm == 1)
            {
                shortSignal = shortSignal && rsiVal > rsiOverbought;
            }

            // ==================== 交易逻辑 ====================
            if (state.Status == 0)
            {
                // 空仓：寻找入场信号
                if (longSignal)
                {
                    state.Status = 1;
                    state.Num = num;
                    state.EntryPrice = currentPrice;
                    state.EntryZScore = zScore;
                    state.HoldBars = 0;
                    state.MaxZScore = Math.Abs(zScore);
                    Trade(tu.MktSymbol, OrderType.BUY, currentPrice, num, period, sendMode);
                }
                else if (shortSignal)
                {
                    state.Status = 2;
                    state.Num = num;
                    state.EntryPrice = currentPrice;
                    state.EntryZScore = zScore;
                    state.HoldBars = 0;
                    state.MaxZScore = Math.Abs(zScore);
                    Trade(tu.MktSymbol, OrderType.SELL, currentPrice, num, period, sendMode);
                }
            }
            else if (state.Status == 1)
            {
                // 多头持仓：等待Z-Score回归
                double currentExitZ = dynamicExitZ;
                if (useTimeDecay == 1)
                {
                    // 时间衰减：持仓越久，出场阈值越宽松
                    double timeDecayFactor = 1.0 + (double)state.HoldBars / maxHoldBars;
                    currentExitZ = dynamicExitZ * timeDecayFactor;
                }

                bool exitSignal = false;

                // Z-Score回归到出场阈值
                if (zScore >= -currentExitZ)
                {
                    exitSignal = true;
                }
                // Z-Score继续偏离超过止损阈值
                else if (zScore < -dynamicStopZ)
                {
                    exitSignal = true;
                }
                // 最大持仓时间
                else if (state.HoldBars >= maxHoldBars)
                {
                    exitSignal = true;
                }

                if (exitSignal)
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.Num, period, sendMode);
                    state.Reset();
                }
            }
            else if (state.Status == 2)
            {
                // 空头持仓：等待Z-Score回归
                double currentExitZ = dynamicExitZ;
                if (useTimeDecay == 1)
                {
                    double timeDecayFactor = 1.0 + (double)state.HoldBars / maxHoldBars;
                    currentExitZ = dynamicExitZ * timeDecayFactor;
                }

                bool exitSignal = false;

                // Z-Score回归到出场阈值
                if (zScore <= currentExitZ)
                {
                    exitSignal = true;
                }
                // Z-Score继续偏离超过止损阈值
                else if (zScore > dynamicStopZ)
                {
                    exitSignal = true;
                }
                // 最大持仓时间
                else if (state.HoldBars >= maxHoldBars)
                {
                    exitSignal = true;
                }

                if (exitSignal)
                {
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.Num, period, sendMode);
                    state.Reset();
                }
            }
        }

        /// <summary>
        /// 计算半衰期 (Half-Life)
        /// 使用Ornstein-Uhlenbeck过程的简化估计
        /// 半衰期 = -ln(2) / ln(1 + beta)，其中beta是价格变化对偏离度的回归系数
        /// </summary>
        private double CalculateHalfLife(List<double> prices, int lookback)
        {
            if (prices.Count < lookback + 1 || lookback < 10) return -1;

            // 计算价格变化和滞后价格
            var deltaY = new List<double>();
            var lagY = new List<double>();

            for (int i = prices.Count - lookback; i < prices.Count; i++)
            {
                deltaY.Add(prices[i] - prices[i - 1]);
                lagY.Add(prices[i - 1]);
            }

            // 计算均值
            double meanDelta = deltaY.Average();
            double meanLag = lagY.Average();

            // 计算回归系数 beta = Cov(deltaY, lagY) / Var(lagY)
            double covariance = 0;
            double variance = 0;

            for (int i = 0; i < deltaY.Count; i++)
            {
                covariance += (deltaY[i] - meanDelta) * (lagY[i] - meanLag);
                variance += (lagY[i] - meanLag) * (lagY[i] - meanLag);
            }

            if (variance == 0) return -1;

            double beta = covariance / variance;

            // 半衰期计算
            if (beta >= 0) return -1; // 非均值回归

            double halfLife = -Math.Log(2) / Math.Log(1 + beta);

            return halfLife > 0 ? halfLife : -1;
        }
    }
}
