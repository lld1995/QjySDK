using Common;
using Model;
using Skender.Stock.Indicators;
using stgInterface;
using System;
using System.Linq;
using static Model.EnumDef;
using System.Collections.Generic;

namespace QjySDK.Stg
{
    /// <summary>
    /// 多周期共振交易系统 (Multi-Period Resonance Trading System)
    /// 
    /// 核心设计理念：
    /// 1. 多周期趋势评估 - 同时分析多个时间周期的趋势方向
    /// 2. 共振强度计算 - 当多个周期趋势一致时，共振强度增加
    /// 3. 动态仓位管理 - 根据共振强度和市场波动率调整仓位
    /// 4. 多指标融合 - EMA趋势 + RSI动量 + ATR波动率
    /// 
    /// 交易逻辑：
    /// - 计算每个周期的趋势得分 (-100 到 +100)
    /// - 综合所有周期得分，加权计算总共振得分
    /// - 共振得分超过阈值时产生交易信号
    /// - 使用ATR进行动态止损止盈
    /// </summary>
    public class MultiPeriodResonance : StgBase
    {
        public MultiPeriodResonance()
        {
        }

        public MultiPeriodResonance(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // ===== 多周期参数 =====
            sd.ArgDic["usePeriod1"] = 1;           // 启用周期1 (主周期)
            sd.ArgDic["usePeriod2"] = 1;           // 启用周期2 (次周期)
            sd.ArgDic["usePeriod3"] = 1;           // 启用周期3 (大周期)
            sd.ArgDic["period1Weight"] = 30;       // 周期1权重
            sd.ArgDic["period2Weight"] = 35;       // 周期2权重
            sd.ArgDic["period3Weight"] = 35;       // 周期3权重

            // ===== EMA趋势参数 =====
            sd.ArgDic["emaFast"] = 12;             // 快速EMA周期
            sd.ArgDic["emaMid"] = 21;              // 中速EMA周期
            sd.ArgDic["emaSlow"] = 55;             // 慢速EMA周期
            sd.ArgDic["emaTrendWeight"] = 40;      // EMA趋势权重

            // ===== RSI动量参数 =====
            sd.ArgDic["rsiPeriod"] = 14;           // RSI周期
            sd.ArgDic["rsiOverbought"] = 70;       // RSI超买线
            sd.ArgDic["rsiOversold"] = 30;         // RSI超卖线
            sd.ArgDic["rsiWeight"] = 30;           // RSI权重

            // ===== MACD参数 =====
            sd.ArgDic["macdFast"] = 12;            // MACD快线
            sd.ArgDic["macdSlow"] = 26;            // MACD慢线
            sd.ArgDic["macdSignal"] = 9;           // MACD信号线
            sd.ArgDic["macdWeight"] = 30;          // MACD权重

            // ===== 共振信号参数 =====
            sd.ArgDic["resonanceThreshold"] = 50;  // 共振阈值 (0-100)
            sd.ArgDic["strongResonance"] = 75;     // 强共振阈值
            sd.ArgDic["confirmBars"] = 2;          // 信号确认K线数

            // ===== ATR波动率参数 =====
            sd.ArgDic["atrPeriod"] = 14;           // ATR周期
            sd.ArgDic["atrStopMultiplier"] = 2.0m; // ATR止损倍数
            sd.ArgDic["atrProfitMultiplier"] = 3.0m; // ATR止盈倍数
            sd.ArgDic["useAtrFilter"] = 1;         // 使用ATR过滤低波动

            // ===== 交易参数 =====
            sd.ArgDic["tradePeriod"] = (int)Period.TIME_15M;  // 交易周期
            sd.ArgDic["mode"] = 0;                 // 0:双向 1:仅多 2:仅空
            sd.ArgDic["sendMode"] = 0;             // 发单模式
            sd.ArgDic["positionMode"] = 0;         // 0:固定仓位 1:动态仓位(按共振强度)
            sd.ArgDic["maxPositionScale"] = 3.0m;  // 最大仓位倍数

            // ===== 手数控制 =====
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;

            // ===== 风控参数 =====
            sd.ArgDic["useStopLoss"] = 1;          // 启用止损
            sd.ArgDic["useTakeProfit"] = 1;        // 启用止盈
            sd.ArgDic["useTrailingStop"] = 0;      // 启用移动止损
            sd.ArgDic["trailingAtrMult"] = 1.5m;   // 移动止损ATR倍数

            // ===== 参数说明 =====
            sd.ArgDescDic["usePeriod1"] = new ArgDesc() { Text = "启用周期1", Explain = "启用主交易周期", Options = "0:禁用|1:启用 主交易周期", Type = "bool" };
            sd.ArgDescDic["usePeriod2"] = new ArgDesc() { Text = "启用周期2", Explain = "启用次级周期", Options = "0:禁用|1:启用 次级周期(通常为主周期的3-5倍)", Type = "bool" };
            sd.ArgDescDic["usePeriod3"] = new ArgDesc() { Text = "启用周期3", Explain = "启用大周期", Options = "0:禁用|1:启用 大周期(通常为主周期的10-20倍)", Type = "bool" };
            sd.ArgDescDic["period1Weight"] = new ArgDesc() { Text = "周期1权重", Explain = "主周期在共振计算中的权重", Type = "number" };
            sd.ArgDescDic["period2Weight"] = new ArgDesc() { Text = "周期2权重", Explain = "次周期在共振计算中的权重", Type = "number" };
            sd.ArgDescDic["period3Weight"] = new ArgDesc() { Text = "周期3权重", Explain = "大周期在共振计算中的权重", Type = "number" };

            sd.ArgDescDic["emaFast"] = new ArgDesc() { Text = "快速EMA", Explain = "快速均线周期，用于捕捉短期趋势", Type = "number" };
            sd.ArgDescDic["emaMid"] = new ArgDesc() { Text = "中速EMA", Explain = "中速均线周期，用于确认趋势方向", Type = "number" };
            sd.ArgDescDic["emaSlow"] = new ArgDesc() { Text = "慢速EMA", Explain = "慢速均线周期，用于判断大趋势", Type = "number" };
            sd.ArgDescDic["emaTrendWeight"] = new ArgDesc() { Text = "EMA权重", Explain = "EMA趋势在单周期评分中的权重", Type = "number" };

            sd.ArgDescDic["rsiPeriod"] = new ArgDesc() { Text = "RSI周期", Explain = "RSI计算周期", Type = "number" };
            sd.ArgDescDic["rsiOverbought"] = new ArgDesc() { Text = "RSI超买", Explain = "RSI超买阈值", Type = "number" };
            sd.ArgDescDic["rsiOversold"] = new ArgDesc() { Text = "RSI超卖", Explain = "RSI超卖阈值", Type = "number" };
            sd.ArgDescDic["rsiWeight"] = new ArgDesc() { Text = "RSI权重", Explain = "RSI在单周期评分中的权重", Type = "number" };

            sd.ArgDescDic["macdFast"] = new ArgDesc() { Text = "MACD快线", Explain = "MACD快线周期", Type = "number" };
            sd.ArgDescDic["macdSlow"] = new ArgDesc() { Text = "MACD慢线", Explain = "MACD慢线周期", Type = "number" };
            sd.ArgDescDic["macdSignal"] = new ArgDesc() { Text = "MACD信号", Explain = "MACD信号线周期", Type = "number" };
            sd.ArgDescDic["macdWeight"] = new ArgDesc() { Text = "MACD权重", Explain = "MACD在单周期评分中的权重", Type = "number" };

            sd.ArgDescDic["resonanceThreshold"] = new ArgDesc() { Text = "共振阈值", Explain = "触发交易信号的最低共振得分", Type = "number" };
            sd.ArgDescDic["strongResonance"] = new ArgDesc() { Text = "强共振阈值", Explain = "强共振信号阈值，用于加仓", Type = "number" };
            sd.ArgDescDic["confirmBars"] = new ArgDesc() { Text = "确认K线", Explain = "信号需要连续确认的K线数", Type = "number" };

            sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "ATR计算周期", Type = "number" };
            sd.ArgDescDic["atrStopMultiplier"] = new ArgDesc() { Text = "止损ATR倍数", Explain = "止损距离=ATR*此倍数", Type = "number" };
            sd.ArgDescDic["atrProfitMultiplier"] = new ArgDesc() { Text = "止盈ATR倍数", Explain = "止盈距离=ATR*此倍数", Type = "number" };
            sd.ArgDescDic["useAtrFilter"] = new ArgDesc() { Text = "ATR过滤", Explain = "过滤低波动行情", Options = "0:不过滤|1:过滤低波动行情", Type = "bool" };

            sd.ArgDescDic["tradePeriod"] = new ArgDesc() { Text = "交易周期", Explain = "执行交易的K线周期(秒): 60=1分钟 300=5分钟 900=15分钟 3600=1小时", Type = "number" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即发单|1:下根K线开盘发单", Type = "select" };
            sd.ArgDescDic["positionMode"] = new ArgDesc() { Text = "仓位模式", Explain = "仓位管理方式", Options = "0:固定仓位|1:动态仓位(按共振强度调整)", Type = "select" };
            sd.ArgDescDic["maxPositionScale"] = new ArgDesc() { Text = "最大仓位倍数", Explain = "动态仓位模式下的最大仓位倍数", Type = "number" };

            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
            sd.ArgDescDic["useStopLoss"] = new ArgDesc() { Text = "启用止损", Explain = "触及止损价自动平仓", Options = "0:禁用|1:启用ATR动态止损", Type = "bool" };
            sd.ArgDescDic["useTakeProfit"] = new ArgDesc() { Text = "启用止盈", Explain = "触及止盈价自动平仓", Options = "0:禁用|1:启用ATR动态止盈", Type = "bool" };
            sd.ArgDescDic["useTrailingStop"] = new ArgDesc() { Text = "移动止损", Explain = "跟踪最高/低点调整止损", Options = "0:禁用|1:启用移动止损", Type = "bool" };
            sd.ArgDescDic["trailingAtrMult"] = new ArgDesc() { Text = "移动止损倍数", Explain = "移动止损的ATR倍数", Type = "number" };

            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };

            sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 1;
            sd.SubChartNum = 3;

            // 副图0: 共振强度
            sd.ColorDic["sub0-ResonanceScore"] = "#2196F3";
            sd.ColorDic["sub0-Threshold"] = "#FF9800";
            sd.ColorDic["sub0-Zero"] = "#666666";

            // 副图1: RSI
            sd.ColorDic["sub1-RSI"] = "#9C27B0";
            sd.ColorDic["sub1-Overbought"] = "#F6465D";
            sd.ColorDic["sub1-Oversold"] = "#0ECB81";

            // 副图2: MACD
            sd.ColorDic["sub2-Histogram"] = "#F6465D;#0ECB81";
            sd.ColorDic["sub2-DIF"] = "#2196F3";
            sd.ColorDic["sub2-DEA"] = "#FF9800";

            sd.MidValDic["sub0"] = 0;
            sd.MidValDic["sub1"] = 50;
            sd.MidValDic["sub2"] = 0;

            return sd;
        }

        #region 状态管理

        /// <summary>
        /// 单周期趋势数据
        /// </summary>
        private class PeriodTrendData
        {
            public double EmaScore { get; set; }      // EMA趋势得分 (-100 to 100)
            public double RsiScore { get; set; }      // RSI动量得分 (-100 to 100)
            public double MacdScore { get; set; }     // MACD得分 (-100 to 100)
            public double TotalScore { get; set; }    // 综合得分
            public double Atr { get; set; }           // ATR值
            public bool IsValid { get; set; }         // 数据是否有效
        }

        /// <summary>
        /// 交易状态
        /// </summary>
        private class TradeState
        {
            public int Status { get; set; }           // 0:空仓 1:多头 2:空头
            public decimal Num { get; set; }          // 持仓数量
            public decimal EntryPrice { get; set; }   // 入场价格
            public decimal StopLoss { get; set; }     // 止损价格
            public decimal TakeProfit { get; set; }   // 止盈价格
            public decimal TrailingStop { get; set; } // 移动止损价格
            public decimal HighestPrice { get; set; } // 持仓期间最高价
            public decimal LowestPrice { get; set; }  // 持仓期间最低价
            public int ConfirmCount { get; set; }     // 信号确认计数
            public double LastResonanceScore { get; set; } // 上一次共振得分
            public int BarsInPosition { get; set; }   // 持仓K线数
        }

        /// <summary>
        /// 全局多周期数据 (按品种存储)
        /// </summary>
        private class GlobalPeriodData
        {
            public Dictionary<Period, List<SkQuote>> PeriodQuotes { get; set; }
            public Dictionary<Period, PeriodTrendData> PeriodTrends { get; set; }
            public double ResonanceScore { get; set; }
            public double CurrentAtr { get; set; }

            public GlobalPeriodData()
            {
                PeriodQuotes = new Dictionary<Period, List<SkQuote>>();
                PeriodTrends = new Dictionary<Period, PeriodTrendData>();
            }
        }

        private Dictionary<string, TradeState> _stateDic = new Dictionary<string, TradeState>();
        private Dictionary<string, GlobalPeriodData> _globalDataDic = new Dictionary<string, GlobalPeriodData>();

        #endregion

        #region 指标计算

        /// <summary>
        /// 计算EMA趋势得分
        /// 得分范围: -100 到 +100
        /// 正分表示多头趋势，负分表示空头趋势
        /// </summary>
        private double CalcEmaTrendScore(List<SkQuote> quotes, int fast, int mid, int slow)
        {
            if (quotes.Count < slow + 5) return 0;

            var emaFast = quotes.GetEma(fast).ToList();
            var emaMid = quotes.GetEma(mid).ToList();
            var emaSlow = quotes.GetEma(slow).ToList();

            var lastFast = emaFast.Last().Ema;
            var lastMid = emaMid.Last().Ema;
            var lastSlow = emaSlow.Last().Ema;

            if (lastFast == null || lastMid == null || lastSlow == null) return 0;

            double score = 0;
            double price = (double)quotes.Last().Close;

            // 1. 价格与均线关系 (权重40%)
            if (price > lastFast.Value) score += 13.3;
            else score -= 13.3;

            if (price > lastMid.Value) score += 13.3;
            else score -= 13.3;

            if (price > lastSlow.Value) score += 13.4;
            else score -= 13.4;

            // 2. 均线排列关系 (权重40%)
            // 多头排列: Fast > Mid > Slow
            // 空头排列: Fast < Mid < Slow
            if (lastFast.Value > lastMid.Value && lastMid.Value > lastSlow.Value)
            {
                score += 40; // 完美多头排列
            }
            else if (lastFast.Value < lastMid.Value && lastMid.Value < lastSlow.Value)
            {
                score -= 40; // 完美空头排列
            }
            else if (lastFast.Value > lastMid.Value)
            {
                score += 20; // 短期多头
            }
            else if (lastFast.Value < lastMid.Value)
            {
                score -= 20; // 短期空头
            }

            // 3. 均线斜率 (权重20%)
            if (emaFast.Count >= 3)
            {
                var prevFast = emaFast[emaFast.Count - 3].Ema;
                if (prevFast != null)
                {
                    double slope = (lastFast.Value - prevFast.Value) / prevFast.Value * 100;
                    score += Math.Max(-20, Math.Min(20, slope * 10));
                }
            }

            return Math.Max(-100, Math.Min(100, score));
        }

        /// <summary>
        /// 计算RSI动量得分
        /// </summary>
        private double CalcRsiScore(List<SkQuote> quotes, int period, int overbought, int oversold)
        {
            if (quotes.Count < period + 5) return 0;

            var rsiList = quotes.GetRsi(period).ToList();
            var lastRsi = rsiList.Last().Rsi;
            var prevRsi = rsiList[rsiList.Count - 2].Rsi;

            if (lastRsi == null) return 0;

            double rsi = lastRsi.Value;
            double score = 0;

            // 1. RSI位置得分 (权重50%)
            // RSI > 50 为多头区域，< 50 为空头区域
            score += (rsi - 50);

            // 2. RSI趋势得分 (权重30%)
            if (prevRsi != null)
            {
                double rsiChange = rsi - prevRsi.Value;
                score += rsiChange * 1.5;
            }

            // 3. 超买超卖区域加分 (权重20%)
            if (rsi < oversold)
            {
                // 超卖区域，可能反弹，给予多头加分
                score += (oversold - rsi) * 0.5;
            }
            else if (rsi > overbought)
            {
                // 超买区域，可能回调，给予空头加分
                score -= (rsi - overbought) * 0.5;
            }

            return Math.Max(-100, Math.Min(100, score));
        }

        /// <summary>
        /// 计算MACD得分
        /// </summary>
        private double CalcMacdScore(List<SkQuote> quotes, int fast, int slow, int signal)
        {
            if (quotes.Count < slow + signal + 5) return 0;

            var macdList = quotes.GetMacd(fast, slow, signal).ToList();
            var last = macdList.Last();
            var prev = macdList[macdList.Count - 2];

            if (last.Macd == null || last.Signal == null) return 0;
            if (prev.Macd == null || prev.Signal == null) return 0;

            double dif = last.Macd.Value;
            double dea = last.Signal.Value;
            double histogram = last.Histogram ?? 0;

            double prevDif = prev.Macd.Value;
            double prevDea = prev.Signal.Value;

            double score = 0;

            // 1. DIF与DEA关系 (权重40%)
            if (dif > dea)
            {
                score += 40 * Math.Min(1, Math.Abs(dif - dea) / Math.Max(0.0001, Math.Abs(dea)) * 5);
            }
            else
            {
                score -= 40 * Math.Min(1, Math.Abs(dif - dea) / Math.Max(0.0001, Math.Abs(dea)) * 5);
            }

            // 2. 金叉死叉信号 (权重30%)
            bool goldenCross = prevDif <= prevDea && dif > dea;
            bool deathCross = prevDif >= prevDea && dif < dea;

            if (goldenCross) score += 30;
            if (deathCross) score -= 30;

            // 3. 柱状图趋势 (权重30%)
            double prevHistogram = prev.Histogram ?? 0;
            if (histogram > prevHistogram)
            {
                score += 15;
            }
            else
            {
                score -= 15;
            }

            if (histogram > 0) score += 15;
            else score -= 15;

            return Math.Max(-100, Math.Min(100, score));
        }

        /// <summary>
        /// 计算单周期综合趋势得分
        /// </summary>
        private PeriodTrendData CalcPeriodTrend(List<SkQuote> quotes)
        {
            var data = new PeriodTrendData();

            int emaFast = Convert.ToInt32(ArgDic["emaFast"]);
            int emaMid = Convert.ToInt32(ArgDic["emaMid"]);
            int emaSlow = Convert.ToInt32(ArgDic["emaSlow"]);
            int emaTrendWeight = Convert.ToInt32(ArgDic["emaTrendWeight"]);

            int rsiPeriod = Convert.ToInt32(ArgDic["rsiPeriod"]);
            int rsiOverbought = Convert.ToInt32(ArgDic["rsiOverbought"]);
            int rsiOversold = Convert.ToInt32(ArgDic["rsiOversold"]);
            int rsiWeight = Convert.ToInt32(ArgDic["rsiWeight"]);

            int macdFast = Convert.ToInt32(ArgDic["macdFast"]);
            int macdSlow = Convert.ToInt32(ArgDic["macdSlow"]);
            int macdSignal = Convert.ToInt32(ArgDic["macdSignal"]);
            int macdWeight = Convert.ToInt32(ArgDic["macdWeight"]);

            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);

            int minBars = Math.Max(emaSlow, Math.Max(macdSlow + macdSignal, rsiPeriod + atrPeriod)) + 10;
            if (quotes.Count < minBars)
            {
                data.IsValid = false;
                return data;
            }

            // 计算各指标得分
            data.EmaScore = CalcEmaTrendScore(quotes, emaFast, emaMid, emaSlow);
            data.RsiScore = CalcRsiScore(quotes, rsiPeriod, rsiOverbought, rsiOversold);
            data.MacdScore = CalcMacdScore(quotes, macdFast, macdSlow, macdSignal);

            // 计算ATR
            var atrList = quotes.GetAtr(atrPeriod).ToList();
            var lastAtr = atrList.Last().Atr;
            data.Atr = lastAtr ?? 0;

            // 加权计算综合得分
            double totalWeight = emaTrendWeight + rsiWeight + macdWeight;
            data.TotalScore = (data.EmaScore * emaTrendWeight +
                              data.RsiScore * rsiWeight +
                              data.MacdScore * macdWeight) / totalWeight;

            data.IsValid = true;
            return data;
        }

        /// <summary>
        /// 计算多周期共振得分
        /// </summary>
        private double CalcResonanceScore(GlobalPeriodData globalData, Period currentPeriod)
        {
            int usePeriod1 = Convert.ToInt32(ArgDic["usePeriod1"]);
            int usePeriod2 = Convert.ToInt32(ArgDic["usePeriod2"]);
            int usePeriod3 = Convert.ToInt32(ArgDic["usePeriod3"]);
            int period1Weight = Convert.ToInt32(ArgDic["period1Weight"]);
            int period2Weight = Convert.ToInt32(ArgDic["period2Weight"]);
            int period3Weight = Convert.ToInt32(ArgDic["period3Weight"]);

            double totalScore = 0;
            double totalWeight = 0;

            // 获取当前周期及其倍数周期
            var periods = GetResonancePeriods(currentPeriod);

            // 周期1 (主周期)
            if (usePeriod1 == 1 && periods.Count >= 1)
            {
                var p1 = periods[0];
                if (globalData.PeriodTrends.ContainsKey(p1) && globalData.PeriodTrends[p1].IsValid)
                {
                    totalScore += globalData.PeriodTrends[p1].TotalScore * period1Weight;
                    totalWeight += period1Weight;
                }
            }

            // 周期2 (次周期)
            if (usePeriod2 == 1 && periods.Count >= 2)
            {
                var p2 = periods[1];
                if (globalData.PeriodTrends.ContainsKey(p2) && globalData.PeriodTrends[p2].IsValid)
                {
                    totalScore += globalData.PeriodTrends[p2].TotalScore * period2Weight;
                    totalWeight += period2Weight;
                }
            }

            // 周期3 (大周期)
            if (usePeriod3 == 1 && periods.Count >= 3)
            {
                var p3 = periods[2];
                if (globalData.PeriodTrends.ContainsKey(p3) && globalData.PeriodTrends[p3].IsValid)
                {
                    totalScore += globalData.PeriodTrends[p3].TotalScore * period3Weight;
                    totalWeight += period3Weight;
                }
            }

            if (totalWeight == 0) return 0;

            double resonanceScore = totalScore / totalWeight;

            // 共振加成：如果所有周期方向一致，额外加成
            bool allBullish = true;
            bool allBearish = true;

            foreach (var kvp in globalData.PeriodTrends)
            {
                if (kvp.Value.IsValid)
                {
                    if (kvp.Value.TotalScore <= 0) allBullish = false;
                    if (kvp.Value.TotalScore >= 0) allBearish = false;
                }
            }

            if (allBullish && resonanceScore > 0)
            {
                resonanceScore *= 1.2; // 全部看多，加成20%
            }
            else if (allBearish && resonanceScore < 0)
            {
                resonanceScore *= 1.2; // 全部看空，加成20%
            }

            return Math.Max(-100, Math.Min(100, resonanceScore));
        }

        /// <summary>
        /// 获取共振周期列表
        /// </summary>
        private List<Period> GetResonancePeriods(Period basePeriod)
        {
            var periods = new List<Period>();

            // 根据基础周期确定共振周期
            switch (basePeriod)
            {
                case Period.TIME_1M:
                    periods.Add(Period.TIME_1M);
                    periods.Add(Period.TIME_5M);
                    periods.Add(Period.TIME_15M);
                    break;
                case Period.TIME_5M:
                    periods.Add(Period.TIME_5M);
                    periods.Add(Period.TIME_15M);
                    periods.Add(Period.TIME_1H);
                    break;
                case Period.TIME_15M:
                    periods.Add(Period.TIME_15M);
                    periods.Add(Period.TIME_1H);
                    periods.Add(Period.TIME_4H);
                    break;
                case Period.TIME_30M:
                    periods.Add(Period.TIME_30M);
                    periods.Add(Period.TIME_2H);
                    periods.Add(Period.TIME_1D);
                    break;
                case Period.TIME_1H:
                    periods.Add(Period.TIME_1H);
                    periods.Add(Period.TIME_4H);
                    periods.Add(Period.TIME_1D);
                    break;
                case Period.TIME_4H:
                    periods.Add(Period.TIME_4H);
                    periods.Add(Period.TIME_1D);
                    periods.Add(Period.TIME_1D); // 使用日线作为最大周期
                    break;
                default:
                    periods.Add(basePeriod);
                    break;
            }

            return periods;
        }

        /// <summary>
        /// 合成更大周期的K线数据
        /// </summary>
        private List<SkQuote> SynthesizeHigherPeriod(List<SkQuote> quotes, Period sourcePeriod, Period targetPeriod)
        {
            if (quotes == null || quotes.Count == 0) return new List<SkQuote>();

            int multiplier = (int)targetPeriod / (int)sourcePeriod;
            if (multiplier <= 1) return quotes.ToList();

            var result = new List<SkQuote>();
            var tempQuotes = new List<SkQuote>();

            foreach (var q in quotes)
            {
                tempQuotes.Add(q);

                if (tempQuotes.Count >= multiplier)
                {
                    var merged = new SkQuote
                    {
                        Date = tempQuotes.First().Date,
                        Open = tempQuotes.First().Open,
                        High = tempQuotes.Max(x => x.High),
                        Low = tempQuotes.Min(x => x.Low),
                        Close = tempQuotes.Last().Close,
                        Volume = tempQuotes.Sum(x => x.Volume),
                        Amount = tempQuotes.Sum(x => x.Amount)
                    };
                    result.Add(merged);
                    tempQuotes.Clear();
                }
            }

            // 处理剩余的K线
            if (tempQuotes.Count > 0)
            {
                var merged = new SkQuote
                {
                    Date = tempQuotes.First().Date,
                    Open = tempQuotes.First().Open,
                    High = tempQuotes.Max(x => x.High),
                    Low = tempQuotes.Min(x => x.Low),
                    Close = tempQuotes.Last().Close,
                    Volume = tempQuotes.Sum(x => x.Volume),
                    Amount = tempQuotes.Sum(x => x.Amount)
                };
                result.Add(merged);
            }

            return result;
        }

        #endregion

        #region 交易逻辑

        /// <summary>
        /// 全局指标计算 - 在此处理多周期数据更新
        /// </summary>
        public override void OnGlobalIndicator(List<TableUnit> tableUnitList)
        {
            base.OnGlobalIndicator(tableUnitList);

            if (tableUnitList == null || tableUnitList.Count == 0) return;

            // 按品种分组
            var symbolGroups = tableUnitList.GroupBy(tu => tu.MktSymbol);

            foreach (var group in symbolGroups)
            {
                string mktSymbol = group.Key;

                // 获取或创建全局数据
                if (!_globalDataDic.ContainsKey(mktSymbol))
                {
                    _globalDataDic[mktSymbol] = new GlobalPeriodData();
                }
                var globalData = _globalDataDic[mktSymbol];

                // 清空旧数据
                globalData.PeriodQuotes.Clear();
                globalData.PeriodTrends.Clear();

                // 存储各周期数据
                foreach (var tu in group)
                {
                    if (tu.QuoteList != null && tu.QuoteList.Count > 0)
                    {
                        globalData.PeriodQuotes[tu.Period] = tu.QuoteList.ToList();
                    }
                }

                // 计算各周期趋势得分
                foreach (var kvp in globalData.PeriodQuotes)
                {
                    if (kvp.Value.Count > 0)
                    {
                        globalData.PeriodTrends[kvp.Key] = CalcPeriodTrend(kvp.Value);
                    }
                }

                // 找到最小周期作为基准计算共振得分
                Period basePeriod = Period.TIME_UNKNOWN;
                foreach (var p in globalData.PeriodQuotes.Keys)
                {
                    if (basePeriod == Period.TIME_UNKNOWN || (int)p < (int)basePeriod)
                    {
                        basePeriod = p;
                    }
                }

                if (basePeriod != Period.TIME_UNKNOWN)
                {
                    globalData.ResonanceScore = CalcResonanceScore(globalData, basePeriod);

                    // 获取基准周期的ATR
                    if (globalData.PeriodTrends.ContainsKey(basePeriod) && globalData.PeriodTrends[basePeriod].IsValid)
                    {
                        globalData.CurrentAtr = globalData.PeriodTrends[basePeriod].Atr;
                    }
                }
            }
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 只在指定周期上进行交易
            int tradePeriod = Convert.ToInt32(ArgDic["tradePeriod"]);
            if ((int)period != tradePeriod) return;

            var q = tu.QuoteList.Last();
            var sk = tu.GetStateKey();
            string mktSymbol = tu.MktSymbol;

            // 获取或创建交易状态
            if (!_stateDic.ContainsKey(sk))
            {
                _stateDic[sk] = new TradeState();
            }
            var state = _stateDic[sk];

            // 获取全局多周期数据 (由OnGlobalIndicator计算)
            if (!_globalDataDic.ContainsKey(mktSymbol))
            {
                return; // 全局数据尚未计算，跳过
            }
            var globalData = _globalDataDic[mktSymbol];

            // 获取共振得分和ATR
            double resonanceScore = globalData.ResonanceScore;
            double currentAtr = globalData.CurrentAtr;

            // 获取参数
            int resonanceThreshold = Convert.ToInt32(ArgDic["resonanceThreshold"]);
            int strongResonance = Convert.ToInt32(ArgDic["strongResonance"]);
            int confirmBars = Convert.ToInt32(ArgDic["confirmBars"]);
            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
            int positionMode = Convert.ToInt32(ArgDic["positionMode"]);
            decimal maxPositionScale = Convert.ToDecimal(ArgDic["maxPositionScale"]);
            int useStopLoss = Convert.ToInt32(ArgDic["useStopLoss"]);
            int useTakeProfit = Convert.ToInt32(ArgDic["useTakeProfit"]);
            int useTrailingStop = Convert.ToInt32(ArgDic["useTrailingStop"]);
            decimal atrStopMultiplier = Convert.ToDecimal(ArgDic["atrStopMultiplier"]);
            decimal atrProfitMultiplier = Convert.ToDecimal(ArgDic["atrProfitMultiplier"]);
            decimal trailingAtrMult = Convert.ToDecimal(ArgDic["trailingAtrMult"]);
            int useAtrFilter = Convert.ToInt32(ArgDic["useAtrFilter"]);

            // ATR过滤：波动率过低时不交易
            bool atrFilterPassed = true;
            if (useAtrFilter == 1 && currentAtr > 0)
            {
                double atrPercent = currentAtr / (double)q.Close * 100;
                if (atrPercent < 0.1)
                {
                    atrFilterPassed = false;
                }
            }

            // 绘制指标
            PlotIndicators(globalData, period, resonanceScore, resonanceThreshold);

            // 计算基础手数
            var baseNum = CalcLots(tu.MktSymbol, q.Close);

            // 动态仓位调整
            decimal positionScale = 1.0m;
            if (positionMode == 1)
            {
                double absScore = Math.Abs(resonanceScore);
                if (absScore >= strongResonance)
                {
                    positionScale = maxPositionScale;
                }
                else if (absScore >= resonanceThreshold)
                {
                    positionScale = 1.0m + (maxPositionScale - 1.0m) * (decimal)((absScore - resonanceThreshold) / (strongResonance - resonanceThreshold));
                }
            }

            var num = baseNum * positionScale;

            // 信号确认逻辑
            bool buySignal = resonanceScore >= resonanceThreshold && atrFilterPassed;
            bool sellSignal = resonanceScore <= -resonanceThreshold && atrFilterPassed;

            bool sameDirection = (buySignal && state.LastResonanceScore >= resonanceThreshold) ||
                                (sellSignal && state.LastResonanceScore <= -resonanceThreshold);

            if (sameDirection)
            {
                state.ConfirmCount++;
            }
            else
            {
                state.ConfirmCount = 1;
            }

            bool confirmedBuy = buySignal && state.ConfirmCount >= confirmBars;
            bool confirmedSell = sellSignal && state.ConfirmCount >= confirmBars;

            state.LastResonanceScore = resonanceScore;

            // 交易执行
            ExecuteTrade(state, tu, q, period, sendMode, mode,
                        confirmedBuy, confirmedSell, num,
                        currentAtr, atrStopMultiplier, atrProfitMultiplier,
                        useStopLoss, useTakeProfit, useTrailingStop, trailingAtrMult);
        }

        /// <summary>
        /// 绘制指标
        /// </summary>
        private void PlotIndicators(GlobalPeriodData globalData, Period period, double resonanceScore, int threshold)
        {
            // 副图0: 共振强度
            Plot("sub0", "ResonanceScore", PlotType.LINE, resonanceScore);
            Plot("sub0", "Threshold", PlotType.XLINE, threshold);
            Plot("sub0", "Zero", PlotType.XLINE, 0);

            // 副图1: RSI
            if (globalData.PeriodTrends.ContainsKey(period) && globalData.PeriodTrends[period].IsValid)
            {
                int rsiPeriod = Convert.ToInt32(ArgDic["rsiPeriod"]);
                int rsiOverbought = Convert.ToInt32(ArgDic["rsiOverbought"]);
                int rsiOversold = Convert.ToInt32(ArgDic["rsiOversold"]);

                if (globalData.PeriodQuotes.ContainsKey(period))
                {
                    var quotes = globalData.PeriodQuotes[period];
                    if (quotes.Count > rsiPeriod + 5)
                    {
                        var rsiList = quotes.GetRsi(rsiPeriod).ToList();
                        var lastRsi = rsiList.Last().Rsi;
                        if (lastRsi != null)
                        {
                            Plot("sub1", "RSI", PlotType.LINE, lastRsi.Value);
                            Plot("sub1", "Overbought", PlotType.XLINE, rsiOverbought);
                            Plot("sub1", "Oversold", PlotType.XLINE, rsiOversold);
                        }
                    }
                }
            }

            // 副图2: MACD
            if (globalData.PeriodQuotes.ContainsKey(period))
            {
                int macdFast = Convert.ToInt32(ArgDic["macdFast"]);
                int macdSlow = Convert.ToInt32(ArgDic["macdSlow"]);
                int macdSignal = Convert.ToInt32(ArgDic["macdSignal"]);

                var quotes = globalData.PeriodQuotes[period];
                if (quotes.Count > macdSlow + macdSignal + 5)
                {
                    var macdList = quotes.GetMacd(macdFast, macdSlow, macdSignal).ToList();
                    var last = macdList.Last();
                    if (last.Macd != null && last.Signal != null)
                    {
                        Plot("sub2", "Histogram", PlotType.RECTANGLE, last.Histogram ?? 0);
                        Plot("sub2", "DIF", PlotType.LINE, last.Macd.Value);
                        Plot("sub2", "DEA", PlotType.LINE, last.Signal.Value);
                    }
                }
            }
        }

        /// <summary>
        /// 计算手数
        /// </summary>
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
        /// 执行交易
        /// </summary>
        private void ExecuteTrade(TradeState state, TableUnit tu, SkQuote q, Period period, int sendMode, int mode,
                                  bool confirmedBuy, bool confirmedSell, decimal num,
                                  double atr, decimal atrStopMult, decimal atrProfitMult,
                                  int useStopLoss, int useTakeProfit, int useTrailingStop, decimal trailingAtrMult)
        {
            decimal atrDecimal = (decimal)atr;

            if (state.Status == 0)
            {
                // 空仓状态
                if (confirmedBuy && mode != 2)
                {
                    // 开多
                    state.Status = 1;
                    state.Num = num;
                    state.EntryPrice = q.Close;
                    state.HighestPrice = q.Close;
                    state.LowestPrice = q.Close;
                    state.BarsInPosition = 0;

                    if (useStopLoss == 1 && atrDecimal > 0)
                    {
                        state.StopLoss = q.Close - atrDecimal * atrStopMult;
                    }
                    if (useTakeProfit == 1 && atrDecimal > 0)
                    {
                        state.TakeProfit = q.Close + atrDecimal * atrProfitMult;
                    }
                    if (useTrailingStop == 1 && atrDecimal > 0)
                    {
                        state.TrailingStop = q.Close - atrDecimal * trailingAtrMult;
                    }

                    Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                }
                else if (confirmedSell && mode != 1)
                {
                    // 开空
                    state.Status = 2;
                    state.Num = num;
                    state.EntryPrice = q.Close;
                    state.HighestPrice = q.Close;
                    state.LowestPrice = q.Close;
                    state.BarsInPosition = 0;

                    if (useStopLoss == 1 && atrDecimal > 0)
                    {
                        state.StopLoss = q.Close + atrDecimal * atrStopMult;
                    }
                    if (useTakeProfit == 1 && atrDecimal > 0)
                    {
                        state.TakeProfit = q.Close - atrDecimal * atrProfitMult;
                    }
                    if (useTrailingStop == 1 && atrDecimal > 0)
                    {
                        state.TrailingStop = q.Close + atrDecimal * trailingAtrMult;
                    }

                    Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                }
            }
            else if (state.Status == 1)
            {
                // 多头持仓
                state.BarsInPosition++;
                state.HighestPrice = Math.Max(state.HighestPrice, q.High);

                // 更新移动止损
                if (useTrailingStop == 1 && atrDecimal > 0)
                {
                    decimal newTrailingStop = state.HighestPrice - atrDecimal * trailingAtrMult;
                    state.TrailingStop = Math.Max(state.TrailingStop, newTrailingStop);
                }

                bool shouldExit = false;
                string exitReason = "";

                // 检查止损
                if (useStopLoss == 1 && q.Close <= state.StopLoss)
                {
                    shouldExit = true;
                    exitReason = "StopLoss";
                }
                // 检查止盈
                else if (useTakeProfit == 1 && q.Close >= state.TakeProfit)
                {
                    shouldExit = true;
                    exitReason = "TakeProfit";
                }
                // 检查移动止损
                else if (useTrailingStop == 1 && q.Close <= state.TrailingStop)
                {
                    shouldExit = true;
                    exitReason = "TrailingStop";
                }
                // 检查反向信号
                else if (confirmedSell)
                {
                    shouldExit = true;
                    exitReason = "ReverseSignal";
                }

                if (shouldExit)
                {
                    var oriNum = state.Num;
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);

                    // 如果是反向信号且允许做空，则反手
                    if (exitReason == "ReverseSignal" && mode != 1)
                    {
                        state.Status = 2;
                        state.Num = num;
                        state.EntryPrice = q.Close;
                        state.HighestPrice = q.Close;
                        state.LowestPrice = q.Close;
                        state.BarsInPosition = 0;

                        if (useStopLoss == 1 && atrDecimal > 0)
                        {
                            state.StopLoss = q.Close + atrDecimal * atrStopMult;
                        }
                        if (useTakeProfit == 1 && atrDecimal > 0)
                        {
                            state.TakeProfit = q.Close - atrDecimal * atrProfitMult;
                        }
                        if (useTrailingStop == 1 && atrDecimal > 0)
                        {
                            state.TrailingStop = q.Close + atrDecimal * trailingAtrMult;
                        }

                        Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                    }
                    else
                    {
                        ResetState(state);
                    }
                }
            }
            else if (state.Status == 2)
            {
                // 空头持仓
                state.BarsInPosition++;
                state.LowestPrice = Math.Min(state.LowestPrice, q.Low);

                // 更新移动止损
                if (useTrailingStop == 1 && atrDecimal > 0)
                {
                    decimal newTrailingStop = state.LowestPrice + atrDecimal * trailingAtrMult;
                    state.TrailingStop = Math.Min(state.TrailingStop, newTrailingStop);
                }

                bool shouldExit = false;
                string exitReason = "";

                // 检查止损
                if (useStopLoss == 1 && q.Close >= state.StopLoss)
                {
                    shouldExit = true;
                    exitReason = "StopLoss";
                }
                // 检查止盈
                else if (useTakeProfit == 1 && q.Close <= state.TakeProfit)
                {
                    shouldExit = true;
                    exitReason = "TakeProfit";
                }
                // 检查移动止损
                else if (useTrailingStop == 1 && q.Close >= state.TrailingStop)
                {
                    shouldExit = true;
                    exitReason = "TrailingStop";
                }
                // 检查反向信号
                else if (confirmedBuy)
                {
                    shouldExit = true;
                    exitReason = "ReverseSignal";
                }

                if (shouldExit)
                {
                    var oriNum = state.Num;
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);

                    // 如果是反向信号且允许做多，则反手
                    if (exitReason == "ReverseSignal" && mode != 2)
                    {
                        state.Status = 1;
                        state.Num = num;
                        state.EntryPrice = q.Close;
                        state.HighestPrice = q.Close;
                        state.LowestPrice = q.Close;
                        state.BarsInPosition = 0;

                        if (useStopLoss == 1 && atrDecimal > 0)
                        {
                            state.StopLoss = q.Close - atrDecimal * atrStopMult;
                        }
                        if (useTakeProfit == 1 && atrDecimal > 0)
                        {
                            state.TakeProfit = q.Close + atrDecimal * atrProfitMult;
                        }
                        if (useTrailingStop == 1 && atrDecimal > 0)
                        {
                            state.TrailingStop = q.Close - atrDecimal * trailingAtrMult;
                        }

                        Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                    }
                    else
                    {
                        ResetState(state);
                    }
                }
            }
        }

        /// <summary>
        /// 重置状态
        /// </summary>
        private void ResetState(TradeState state)
        {
            state.Status = 0;
            state.Num = 0;
            state.EntryPrice = 0;
            state.StopLoss = 0;
            state.TakeProfit = 0;
            state.TrailingStop = 0;
            state.HighestPrice = 0;
            state.LowestPrice = 0;
            state.BarsInPosition = 0;
            state.ConfirmCount = 0;
        }

        #endregion
    }
}
