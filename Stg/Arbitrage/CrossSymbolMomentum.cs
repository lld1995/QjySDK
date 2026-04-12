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
    /// 跨品种动量套利策略 (Cross-Symbol Momentum Arbitrage)
    /// 
    /// 策略核心思想：
    /// 在OnGlobalIndicator中同时计算所有品种的动量强度，对品种进行排名，
    /// 做多动量最强的品种、做空动量最弱的品种，利用相对强弱的均值回归获利。
    /// 这是一种市场中性策略，多空对冲后降低系统性风险。
    /// 
    /// 核心逻辑：
    /// 1. OnGlobalIndicator: 计算每个品种的综合动量得分(ROC+RSI+EMA趋势)
    /// 2. 对所有品种按动量得分排名
    /// 3. 排名前N的品种做多，排名后N的品种做空
    /// 4. 定期轮换(rebalanceBars周期)：重新排名，调整持仓
    /// 
    /// 风控：
    /// - ATR过滤低波动品种
    /// - 最大持仓品种数限制
    /// - 动量极值过滤(防止追涨杀跌)
    /// - 定期再平衡，而非每根K线都调仓
    /// </summary>
    public class CrossSymbolMomentum : StgBase
    {
        public CrossSymbolMomentum()
        {
        }

        public CrossSymbolMomentum(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 2;
            sd.UseGlobalCalc = 1;

            // ==================== 动量计算参数 ====================
            sd.ArgDescDic["rocPeriod"] = new ArgDesc { Text = "ROC周期", Explain = "价格变化率的计算周期" };
            sd.ArgDic["rocPeriod"] = 20;

            sd.ArgDescDic["rsiPeriod"] = new ArgDesc { Text = "RSI周期", Explain = "RSI计算周期" };
            sd.ArgDic["rsiPeriod"] = 14;

            sd.ArgDescDic["emaFast"] = new ArgDesc { Text = "快速EMA", Explain = "快速均线周期" };
            sd.ArgDic["emaFast"] = 12;

            sd.ArgDescDic["emaSlow"] = new ArgDesc { Text = "慢速EMA", Explain = "慢速均线周期" };
            sd.ArgDic["emaSlow"] = 26;

            sd.ArgDescDic["rocWeight"] = new ArgDesc { Text = "ROC权重", Explain = "ROC在综合得分中的权重(0-100)" };
            sd.ArgDic["rocWeight"] = 40;

            sd.ArgDescDic["rsiWeight"] = new ArgDesc { Text = "RSI权重", Explain = "RSI在综合得分中的权重(0-100)" };
            sd.ArgDic["rsiWeight"] = 30;

            sd.ArgDescDic["emaWeight"] = new ArgDesc { Text = "EMA权重", Explain = "EMA趋势在综合得分中的权重(0-100)" };
            sd.ArgDic["emaWeight"] = 30;

            // ==================== 排名与选股参数 ====================
            sd.ArgDescDic["topN"] = new ArgDesc { Text = "多头品种数", Explain = "排名前N做多(0=自动，取总品种数的20%)" };
            sd.ArgDic["topN"] = 0;

            sd.ArgDescDic["bottomN"] = new ArgDesc { Text = "空头品种数", Explain = "排名后N做空(0=自动，取总品种数的20%)" };
            sd.ArgDic["bottomN"] = 0;

            sd.ArgDescDic["minScoreGap"] = new ArgDesc { Text = "最小分差", Explain = "多空分差低于此值不开仓(防止品种区分度不足)" };
            sd.ArgDic["minScoreGap"] = 20.0;

            // ==================== 再平衡参数 ====================
            sd.ArgDescDic["rebalanceBars"] = new ArgDesc { Text = "再平衡周期", Explain = "每隔多少根K线重新排名调仓" };
            sd.ArgDic["rebalanceBars"] = 5;

            sd.ArgDescDic["forceRebalance"] = new ArgDesc { Text = "强制再平衡", Explain = "到期强制调仓", Options = "1:到期必须调仓|0:只在排名变化时调仓", Type = "select" };
            sd.ArgDic["forceRebalance"] = 0;

            // ==================== 风控参数 ====================
            sd.ArgDescDic["atrPeriod"] = new ArgDesc { Text = "ATR周期", Explain = "ATR计算周期" };
            sd.ArgDic["atrPeriod"] = 14;

            sd.ArgDescDic["atrStopMultiplier"] = new ArgDesc { Text = "止损ATR倍数", Explain = "个股止损=ATR*此倍数" };
            sd.ArgDic["atrStopMultiplier"] = 3.0;

            sd.ArgDescDic["maxMomentumScore"] = new ArgDesc { Text = "动量上限", Explain = "动量得分超过此值视为过热不追多" };
            sd.ArgDic["maxMomentumScore"] = 90.0;

            sd.ArgDescDic["minMomentumScore"] = new ArgDesc { Text = "动量下限", Explain = "动量得分低于此值视为超跌不追空" };
            sd.ArgDic["minMomentumScore"] = -90.0;

            // ==================== 交易参数 ====================
            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
            sd.ArgDic["lotsMode"] = 1;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "交易手数", Explain = "固定手数模式下的交易数量" };
            sd.ArgDic["lots"] = 1.0m;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "交易金额", Explain = "固定金额模式下的每个品种金额" };
            sd.ArgDic["money"] = 10000m;

            // ==================== 颜色配置 ====================
            sd.ColorDic["sub0-MomentumScore"] = "#2196F3";
            sd.ColorDic["sub0-Zero"] = "#666666";
            sd.ColorDic["sub0-UpperThreshold"] = "#F44336";
            sd.ColorDic["sub0-LowerThreshold"] = "#4CAF50";

            sd.ColorDic["sub1-Rank"] = "#FF9800";
            sd.ColorDic["sub1-TopLine"] = "#4CAF50";
            sd.ColorDic["sub1-BottomLine"] = "#F44336";

            sd.MidValDic["sub0"] = 0;

            return sd;
        }

        #region 全局状态

        /// <summary>
        /// 品种动量数据
        /// </summary>
        private class SymbolMomentum
        {
            public string MktSymbol { get; set; }
            public double RocScore { get; set; }
            public double RsiScore { get; set; }
            public double EmaScore { get; set; }
            public double TotalScore { get; set; }
            public int Rank { get; set; }          // 1=最强
            public double Atr { get; set; }
            public decimal LatestPrice { get; set; }
            public bool IsValid { get; set; }
        }

        /// <summary>
        /// 交易状态
        /// </summary>
        private class TradeState
        {
            public int Status { get; set; }         // 0=空仓 1=做多(强品种) 2=做空(弱品种)
            public decimal Num { get; set; }
            public decimal EntryPrice { get; set; }
            public decimal StopLoss { get; set; }
            public int HoldBars { get; set; }
        }

        // 全局动量排名（OnGlobalIndicator更新）
        private Dictionary<string, SymbolMomentum> _momentumDic = new Dictionary<string, SymbolMomentum>();
        private List<SymbolMomentum> _rankedList = new List<SymbolMomentum>();
        private int _totalSymbols = 0;

        // 交易状态
        private Dictionary<string, TradeState> _stateDic = new Dictionary<string, TradeState>();

        // 再平衡计数器
        private int _barsSinceRebalance = 0;

        // 上一次的多空列表（用于判断排名是否变化）
        private HashSet<string> _prevLongSet = new HashSet<string>();
        private HashSet<string> _prevShortSet = new HashSet<string>();

        #endregion

        #region OnGlobalIndicator — 全局动量计算与排名

        public override void OnGlobalIndicator(List<TableUnit> tableUnitList)
        {
            base.OnGlobalIndicator(tableUnitList);
            if (tableUnitList == null || tableUnitList.Count == 0) return;

            int rocPeriod = Convert.ToInt32(ArgDic["rocPeriod"]);
            int rsiPeriod = Convert.ToInt32(ArgDic["rsiPeriod"]);
            int emaFast = Convert.ToInt32(ArgDic["emaFast"]);
            int emaSlow = Convert.ToInt32(ArgDic["emaSlow"]);
            int rocWeight = Convert.ToInt32(ArgDic["rocWeight"]);
            int rsiWeight = Convert.ToInt32(ArgDic["rsiWeight"]);
            int emaWeight = Convert.ToInt32(ArgDic["emaWeight"]);
            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);

            int minBars = Math.Max(emaSlow, Math.Max(rocPeriod, rsiPeriod + atrPeriod)) + 10;

            // 收集每个品种最小周期的数据
            var symbolDataDic = new Dictionary<string, List<SkQuote>>();
            foreach (var tu in tableUnitList)
            {
                if (tu.QuoteList == null || tu.QuoteList.Count < minBars) continue;
                if (!symbolDataDic.ContainsKey(tu.MktSymbol))
                {
                    symbolDataDic[tu.MktSymbol] = tu.QuoteList;
                }
                else
                {
                    // 取最小周期
                    var existing = tableUnitList.First(t => t.MktSymbol == tu.MktSymbol && t.QuoteList == symbolDataDic[tu.MktSymbol]);
                    if ((int)tu.Period < (int)existing.Period)
                    {
                        symbolDataDic[tu.MktSymbol] = tu.QuoteList;
                    }
                }
            }

            _totalSymbols = symbolDataDic.Count;
            double totalWeight = rocWeight + rsiWeight + emaWeight;
            if (totalWeight <= 0) totalWeight = 100;

            // 计算每个品种的动量得分
            foreach (var kvp in symbolDataDic)
            {
                string sym = kvp.Key;
                var quotes = kvp.Value;

                if (!_momentumDic.ContainsKey(sym))
                {
                    _momentumDic[sym] = new SymbolMomentum { MktSymbol = sym };
                }
                var md = _momentumDic[sym];
                md.LatestPrice = quotes.Last().Close;

                // 1. ROC得分 (-100 to 100)
                var rocList = quotes.GetRoc(rocPeriod).ToList();
                double rocVal = rocList.Last().Roc ?? 0;
                md.RocScore = Math.Max(-100, Math.Min(100, rocVal * 5));

                // 2. RSI得分 (-100 to 100)
                var rsiList = quotes.GetRsi(rsiPeriod).ToList();
                double rsiVal = rsiList.Last().Rsi ?? 50;
                md.RsiScore = (rsiVal - 50) * 2;

                // 3. EMA趋势得分 (-100 to 100)
                var emaFastList = quotes.GetEma(emaFast).ToList();
                var emaSlowList = quotes.GetEma(emaSlow).ToList();
                var lastFast = emaFastList.Last().Ema;
                var lastSlow = emaSlowList.Last().Ema;
                if (lastFast != null && lastSlow != null && lastSlow.Value != 0)
                {
                    double emaDiff = (lastFast.Value - lastSlow.Value) / lastSlow.Value * 100;
                    md.EmaScore = Math.Max(-100, Math.Min(100, emaDiff * 20));
                }
                else
                {
                    md.EmaScore = 0;
                }

                // ATR
                var atrList = quotes.GetAtr(atrPeriod).ToList();
                md.Atr = atrList.Last().Atr ?? 0;

                // 综合得分
                md.TotalScore = (md.RocScore * rocWeight + md.RsiScore * rsiWeight + md.EmaScore * emaWeight) / totalWeight;
                md.IsValid = true;
            }

            // 排名
            _rankedList = _momentumDic.Values.Where(m => m.IsValid).OrderByDescending(m => m.TotalScore).ToList();
            for (int i = 0; i < _rankedList.Count; i++)
            {
                _rankedList[i].Rank = i + 1;
            }

            // 更新再平衡计数
            _barsSinceRebalance++;
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

            if (!_momentumDic.ContainsKey(mktSymbol)) return;
            var momentum = _momentumDic[mktSymbol];
            if (!momentum.IsValid) return;

            int topN = Convert.ToInt32(ArgDic["topN"]);
            int bottomN = Convert.ToInt32(ArgDic["bottomN"]);
            double minScoreGap = Convert.ToDouble(ArgDic["minScoreGap"]);
            int rebalanceBars = Convert.ToInt32(ArgDic["rebalanceBars"]);
            int forceRebalance = Convert.ToInt32(ArgDic["forceRebalance"]);
            double atrStopMultiplier = Convert.ToDouble(ArgDic["atrStopMultiplier"]);
            double maxMomentumScore = Convert.ToDouble(ArgDic["maxMomentumScore"]);
            double minMomentumScore = Convert.ToDouble(ArgDic["minMomentumScore"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            if (!_stateDic.ContainsKey(sk))
            {
                _stateDic[sk] = new TradeState();
            }
            var state = _stateDic[sk];

            decimal currentPrice = tu.QuoteList.Last().Close;

            // 自动计算topN/bottomN (取总品种数的20%，至少1)
            int validCount = _rankedList.Count;
            int actualTopN = topN > 0 ? topN : Math.Max(1, validCount / 5);
            int actualBottomN = bottomN > 0 ? bottomN : Math.Max(1, validCount / 5);

            // 判断当前品种属于多头组还是空头组
            bool isLong = momentum.Rank <= actualTopN;
            bool isShort = momentum.Rank > validCount - actualBottomN;

            // 检查多空分差是否足够
            double topScore = _rankedList.Count > 0 ? _rankedList.First().TotalScore : 0;
            double bottomScore = _rankedList.Count > 0 ? _rankedList.Last().TotalScore : 0;
            bool gapSufficient = (topScore - bottomScore) >= minScoreGap;

            // 极值过滤
            if (isLong && momentum.TotalScore > maxMomentumScore) isLong = false;
            if (isShort && momentum.TotalScore < minMomentumScore) isShort = false;

            // 绘制指标
            Plot("sub0", "MomentumScore", PlotType.LINE, momentum.TotalScore);
            Plot("sub0", "Zero", PlotType.XLINE, 0);
            Plot("sub0", "UpperThreshold", PlotType.XLINE, maxMomentumScore);
            Plot("sub0", "LowerThreshold", PlotType.XLINE, minMomentumScore);

            Plot("sub1", "Rank", PlotType.LINE, momentum.Rank);
            Plot("sub1", "TopLine", PlotType.XLINE, actualTopN);
            Plot("sub1", "BottomLine", PlotType.XLINE, validCount - actualBottomN);

            // 是否到了再平衡时间
            bool isRebalanceTime = _barsSinceRebalance >= rebalanceBars;

            // ==================== 持仓管理 ====================
            if (state.Status != 0)
            {
                state.HoldBars++;

                // 止损检查
                bool shouldExit = false;
                if (state.Status == 1 && state.StopLoss > 0 && currentPrice <= state.StopLoss)
                {
                    shouldExit = true;
                }
                else if (state.Status == 2 && state.StopLoss > 0 && currentPrice >= state.StopLoss)
                {
                    shouldExit = true;
                }

                // 再平衡检查：品种不再属于原来的组别
                if (!shouldExit && isRebalanceTime)
                {
                    if (state.Status == 1 && !isLong)
                    {
                        shouldExit = true; // 不再是强品种，平多
                    }
                    else if (state.Status == 2 && !isShort)
                    {
                        shouldExit = true; // 不再是弱品种，平空
                    }
                    else if (forceRebalance == 1)
                    {
                        shouldExit = true; // 强制调仓
                    }
                }

                if (shouldExit)
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
                }
            }

            // ==================== 开仓逻辑 ====================
            if (state.Status == 0 && isRebalanceTime && gapSufficient)
            {
                decimal num = CalcLots(mktSymbol, currentPrice);
                decimal atrDecimal = (decimal)momentum.Atr;

                if (isLong)
                {
                    state.Status = 1;
                    state.Num = num;
                    state.EntryPrice = currentPrice;
                    state.HoldBars = 0;
                    if (atrDecimal > 0)
                    {
                        state.StopLoss = currentPrice - atrDecimal * (decimal)atrStopMultiplier;
                    }
                    Trade(mktSymbol, OrderType.BUY, currentPrice, num, period, sendMode);
                }
                else if (isShort)
                {
                    state.Status = 2;
                    state.Num = num;
                    state.EntryPrice = currentPrice;
                    state.HoldBars = 0;
                    if (atrDecimal > 0)
                    {
                        state.StopLoss = currentPrice + atrDecimal * (decimal)atrStopMultiplier;
                    }
                    Trade(mktSymbol, OrderType.SELL, currentPrice, num, period, sendMode);
                }
            }

            // 再平衡完成后重置计数器（只在最后一个品种时重置）
            if (isRebalanceTime && mktSymbol == _rankedList.LastOrDefault()?.MktSymbol)
            {
                _barsSinceRebalance = 0;
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

        #endregion
    }
}
