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
    /// 篮子均值回归套利策略 (Mean Reversion Basket Arbitrage)
    /// 
    /// 策略核心思想：
    /// 将所有品种分成两组篮子(强势篮子/弱势篮子)，基于滚动收益率排名。
    /// 做空近期涨幅最大的篮子、做多近期跌幅最大的篮子，
    /// 利用短期动量反转(均值回归)获利。
    /// 
    /// 与CrossSymbolMomentum的区别：
    /// - CrossSymbolMomentum: 趋势跟随，做多最强做空最弱
    /// - MeanReversionBasket: 反转策略，做空最强做多最弱(赌反转)
    /// 
    /// 核心逻辑：
    /// 1. OnGlobalIndicator: 计算所有品种的N日收益率
    /// 2. 按收益率排名，分为赢家篮子(涨幅前K)和输家篮子(跌幅前K)
    /// 3. 计算篮子收益率的Z-Score，判断是否过度分化
    /// 4. 分化过度 → 做多输家篮子、做空赢家篮子
    /// 5. 分化收敛 → 平仓
    /// 
    /// 学术依据：
    /// - Jegadeesh & Titman (1993): 短期反转效应(1-4周)
    /// - Lo & MacKinlay (1990): 对比收益率策略
    /// </summary>
    public class MeanReversionBasket : StgBase
    {
        public MeanReversionBasket()
        {
        }

        public MeanReversionBasket(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 2;
            sd.UseGlobalCalc = 1;

            // ==================== 排名参数 ====================
            sd.ArgDescDic["rankingPeriod"] = new ArgDesc { Text = "排名周期", Explain = "计算收益率排名的K线数", Type = "number" };
            sd.ArgDic["rankingPeriod"] = 10;

            sd.ArgDescDic["basketSize"] = new ArgDesc { Text = "篮子大小", Explain = "每个篮子的品种数(0=自动，取总数的25%)", Type = "number" };
            sd.ArgDic["basketSize"] = 0;

            sd.ArgDescDic["useWeightedReturn"] = new ArgDesc { Text = "加权收益率", Explain = "收益率加权方式", Options = "1:成交量加权收益率|0:简单收益率", Type = "select" };
            sd.ArgDic["useWeightedReturn"] = 0;

            // ==================== 分化度阈值 ====================
            sd.ArgDescDic["divergenceZLookback"] = new ArgDesc { Text = "分化度回溯", Explain = "计算分化度Z-Score的历史窗口", Type = "number" };
            sd.ArgDic["divergenceZLookback"] = 40;

            sd.ArgDescDic["entryZScore"] = new ArgDesc { Text = "入场Z-Score", Explain = "分化度Z-Score超过此值入场", Type = "number" };
            sd.ArgDic["entryZScore"] = 1.5;

            sd.ArgDescDic["exitZScore"] = new ArgDesc { Text = "出场Z-Score", Explain = "分化度Z-Score低于此值出场", Type = "number" };
            sd.ArgDic["exitZScore"] = 0.3;

            sd.ArgDescDic["stopLossZScore"] = new ArgDesc { Text = "止损Z-Score", Explain = "分化度Z-Score超过此值止损", Type = "number" };
            sd.ArgDic["stopLossZScore"] = 2.5;

            // ==================== 确认与过滤 ====================
            sd.ArgDescDic["confirmBars"] = new ArgDesc { Text = "确认K线数", Explain = "连续N根K线分化度超阈值才入场", Type = "number" };
            sd.ArgDic["confirmBars"] = 1;

            sd.ArgDescDic["minRankStability"] = new ArgDesc { Text = "最小排名稳定度", Explain = "近N根K线排名不变化的比例(0-1)，太不稳定不入场", Type = "number" };
            sd.ArgDic["minRankStability"] = 0.5;

            // ==================== 风控参数 ====================
            sd.ArgDescDic["maxHoldBars"] = new ArgDesc { Text = "最大持仓K线数", Explain = "超过此数量强制平仓", Type = "number" };
            sd.ArgDic["maxHoldBars"] = 15;

            sd.ArgDescDic["useTimeDecay"] = new ArgDesc { Text = "时间衰减", Explain = "持仓时间衰减", Options = "1:持仓越久出场阈值越宽松|0:固定", Type = "select" };
            sd.ArgDic["useTimeDecay"] = 1;

            sd.ArgDescDic["atrPeriod"] = new ArgDesc { Text = "ATR周期", Explain = "ATR计算周期", Type = "number" };
            sd.ArgDic["atrPeriod"] = 14;

            sd.ArgDescDic["atrStopMultiplier"] = new ArgDesc { Text = "ATR止损倍数", Explain = "个股额外止损(0=仅用Z-Score止损)", Type = "number" };
            sd.ArgDic["atrStopMultiplier"] = 0.0;

            // ==================== 交易参数 ====================
            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
            sd.ArgDic["lotsMode"] = 1;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "交易手数", Explain = "固定手数模式下的交易数量", Type = "number" };
            sd.ArgDic["lots"] = 1.0m;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "交易金额", Explain = "固定金额模式下每个品种的金额", Type = "number" };
            sd.ArgDic["money"] = 10000m;

            // ==================== 颜色配置 ====================
            sd.ColorDic["sub0-DivergenceZ"] = "#2196F3";
            sd.ColorDic["sub0-EntryLine"] = "#F44336";
            sd.ColorDic["sub0-ExitLine"] = "#FF9800";
            sd.ColorDic["sub0-Zero"] = "#666666";

            sd.ColorDic["sub1-Return"] = "#9C27B0";
            sd.ColorDic["sub1-WinnerAvg"] = "#F44336";
            sd.ColorDic["sub1-LoserAvg"] = "#4CAF50";

            sd.MidValDic["sub0"] = 0;
            sd.MidValDic["sub1"] = 0;

            return sd;
        }

        #region 全局状态

        /// <summary>
        /// 品种排名数据
        /// </summary>
        private class SymbolRankData
        {
            public string MktSymbol { get; set; }
            public double Return { get; set; }          // N日收益率
            public int Rank { get; set; }               // 排名(1=最高收益)
            public bool IsWinner { get; set; }          // 是否在赢家篮子
            public bool IsLoser { get; set; }           // 是否在输家篮子
            public double Atr { get; set; }
            public decimal LatestPrice { get; set; }
            public List<int> RankHistory { get; set; } = new List<int>();
        }

        /// <summary>
        /// 交易状态
        /// </summary>
        private class TradeState
        {
            public int Status { get; set; }          // 0=空仓 1=做多(输家篮子) 2=做空(赢家篮子)
            public decimal Num { get; set; }
            public decimal EntryPrice { get; set; }
            public decimal StopLoss { get; set; }
            public int HoldBars { get; set; }
            public int ConfirmCount { get; set; }
            public int LastSignalDir { get; set; }
            public bool IsCoolingDown { get; set; }  // 是否处于止损冷却期(防止止损后同向立即重开)
            public int CoolDownDir { get; set; }     // 冷却方向: 1=做多输家篮子方向止损 2=做空赢家篮子方向止损
        }

        // 品种排名数据
        private Dictionary<string, SymbolRankData> _rankDataDic = new Dictionary<string, SymbolRankData>();
        // 分化度历史(赢家平均收益 - 输家平均收益)
        private List<double> _divergenceHistory = new List<double>();
        // 当前分化度Z-Score
        private double _divergenceZScore = 0;
        // 当前分化度(原始值)
        private double _currentDivergence = 0;
        // 赢家/输家篮子平均收益
        private double _winnerAvgReturn = 0;
        private double _loserAvgReturn = 0;
        // 交易状态
        private Dictionary<string, TradeState> _stateDic = new Dictionary<string, TradeState>();

        #endregion

        #region OnGlobalIndicator — 全局排名与分化度计算

        public override void OnGlobalIndicator(List<TableUnit> tableUnitList)
        {
            base.OnGlobalIndicator(tableUnitList);
            if (tableUnitList == null || tableUnitList.Count == 0) return;

            int rankingPeriod = Convert.ToInt32(ArgDic["rankingPeriod"]);
            int basketSize = Convert.ToInt32(ArgDic["basketSize"]);
            int useWeightedReturn = Convert.ToInt32(ArgDic["useWeightedReturn"]);
            int divergenceZLookback = Convert.ToInt32(ArgDic["divergenceZLookback"]);
            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);

            int minBars = rankingPeriod + atrPeriod + 20;

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

            if (symbolDataDic.Count < 4) return; // 至少需要4个品种才能分篮子

            // 计算每个品种的N日收益率
            foreach (var kvp in symbolDataDic)
            {
                string sym = kvp.Key;
                var quotes = kvp.Value;

                if (!_rankDataDic.ContainsKey(sym))
                {
                    _rankDataDic[sym] = new SymbolRankData { MktSymbol = sym };
                }
                var rd = _rankDataDic[sym];
                rd.LatestPrice = quotes.Last().Close;

                decimal prevPrice = quotes[quotes.Count - 1 - rankingPeriod].Close;
                if (prevPrice > 0)
                {
                    if (useWeightedReturn == 1)
                    {
                        // 成交量加权收益率
                        double weightedSum = 0;
                        double volumeSum = 0;
                        for (int i = quotes.Count - rankingPeriod; i < quotes.Count; i++)
                        {
                            double vol = (double)quotes[i].Volume;
                            double ret = quotes[i - 1].Close > 0
                                ? (double)(quotes[i].Close - quotes[i - 1].Close) / (double)quotes[i - 1].Close
                                : 0;
                            weightedSum += ret * vol;
                            volumeSum += vol;
                        }
                        rd.Return = volumeSum > 0 ? weightedSum / volumeSum * rankingPeriod : 0;
                    }
                    else
                    {
                        rd.Return = (double)(quotes.Last().Close - prevPrice) / (double)prevPrice;
                    }
                }
                else
                {
                    rd.Return = 0;
                }

                // ATR
                if (quotes.Count > atrPeriod + 5)
                {
                    var atrList = quotes.GetAtr(atrPeriod).ToList();
                    rd.Atr = atrList.Last().Atr ?? 0;
                }
            }

            // 按收益率排名
            var rankedList = _rankDataDic.Values
                .Where(r => symbolDataDic.ContainsKey(r.MktSymbol))
                .OrderByDescending(r => r.Return)
                .ToList();

            int totalSymbols = rankedList.Count;
            int actualBasketSize = basketSize > 0 ? basketSize : Math.Max(1, totalSymbols / 4);
            actualBasketSize = Math.Min(actualBasketSize, totalSymbols / 2);

            for (int i = 0; i < rankedList.Count; i++)
            {
                var rd = rankedList[i];
                rd.Rank = i + 1;
                rd.IsWinner = (i < actualBasketSize);
                rd.IsLoser = (i >= totalSymbols - actualBasketSize);

                // 记录排名历史(用于稳定度计算)
                rd.RankHistory.Add(rd.Rank);
                if (rd.RankHistory.Count > 20) rd.RankHistory.RemoveAt(0);
            }

            // 计算赢家和输家篮子的平均收益
            var winners = rankedList.Where(r => r.IsWinner).ToList();
            var losers = rankedList.Where(r => r.IsLoser).ToList();

            _winnerAvgReturn = winners.Count > 0 ? winners.Average(r => r.Return) : 0;
            _loserAvgReturn = losers.Count > 0 ? losers.Average(r => r.Return) : 0;

            // 分化度 = 赢家平均收益 - 输家平均收益
            _currentDivergence = _winnerAvgReturn - _loserAvgReturn;
            _divergenceHistory.Add(_currentDivergence);
            if (_divergenceHistory.Count > divergenceZLookback * 2)
            {
                _divergenceHistory.RemoveAt(0);
            }

            // 计算分化度Z-Score
            int zLen = Math.Min(divergenceZLookback, _divergenceHistory.Count);
            if (zLen >= 10)
            {
                var recentDiv = _divergenceHistory.Skip(_divergenceHistory.Count - zLen).ToList();
                double mean = recentDiv.Average();
                double variance = recentDiv.Sum(d => (d - mean) * (d - mean)) / zLen;
                double stdDev = Math.Sqrt(variance);
                _divergenceZScore = stdDev > 1e-10 ? (_currentDivergence - mean) / stdDev : 0;
            }
            else
            {
                _divergenceZScore = 0;
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

            if (!_rankDataDic.ContainsKey(mktSymbol)) return;
            var rankData = _rankDataDic[mktSymbol];

            double entryZScore = Convert.ToDouble(ArgDic["entryZScore"]);
            double exitZScore = Convert.ToDouble(ArgDic["exitZScore"]);
            double stopLossZScore = Convert.ToDouble(ArgDic["stopLossZScore"]);
            int confirmBars = Convert.ToInt32(ArgDic["confirmBars"]);
            double minRankStability = Convert.ToDouble(ArgDic["minRankStability"]);
            int maxHoldBars = Convert.ToInt32(ArgDic["maxHoldBars"]);
            int useTimeDecay = Convert.ToInt32(ArgDic["useTimeDecay"]);
            double atrStopMultiplier = Convert.ToDouble(ArgDic["atrStopMultiplier"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            if (!_stateDic.ContainsKey(sk))
            {
                _stateDic[sk] = new TradeState();
            }
            var state = _stateDic[sk];
            decimal currentPrice = tu.QuoteList.Last().Close;

            // 绘制指标
            Plot("sub0", "DivergenceZ", PlotType.LINE, _divergenceZScore);
            Plot("sub0", "EntryLine", PlotType.XLINE, entryZScore);
            Plot("sub0", "ExitLine", PlotType.XLINE, exitZScore);
            Plot("sub0", "Zero", PlotType.XLINE, 0);

            Plot("sub1", "Return", PlotType.LINE, rankData.Return * 100);
            Plot("sub1", "WinnerAvg", PlotType.LINE, _winnerAvgReturn * 100);
            Plot("sub1", "LoserAvg", PlotType.LINE, _loserAvgReturn * 100);

            // 排名稳定度检查
            bool rankStable = true;
            if (rankData.RankHistory.Count >= 5)
            {
                var recent = rankData.RankHistory.Skip(rankData.RankHistory.Count - 5).ToList();
                bool isCurrentWinner = rankData.IsWinner;
                bool isCurrentLoser = rankData.IsLoser;
                int stableCount = 0;
                int totalRanks = _rankDataDic.Count(r => r.Value.RankHistory.Count > 0);
                int bSize = Convert.ToInt32(ArgDic["basketSize"]);
                int actualBasket = bSize > 0 ? bSize : Math.Max(1, totalRanks / 4);

                foreach (var r in recent)
                {
                    if (isCurrentWinner && r <= actualBasket) stableCount++;
                    else if (isCurrentLoser && r > totalRanks - actualBasket) stableCount++;
                }
                rankStable = (double)stableCount / recent.Count >= minRankStability;
            }

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

                // 分化度Z-Score回归 → 平仓
                if (_divergenceZScore <= currentExitZ && _divergenceZScore >= -currentExitZ)
                {
                    exitSignal = true;
                }

                // 分化度Z-Score继续极端 → 止损
                if (!exitSignal && Math.Abs(_divergenceZScore) > stopLossZScore)
                {
                    exitSignal = true;
                    stopLossHit = true;
                }

                // ATR止损
                if (!exitSignal && state.StopLoss > 0)
                {
                    if (state.Status == 1 && currentPrice <= state.StopLoss) exitSignal = true;
                    else if (state.Status == 2 && currentPrice >= state.StopLoss) exitSignal = true;
                }

                // 超时
                if (!exitSignal && state.HoldBars >= maxHoldBars) exitSignal = true;

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
                    int prevStatus = state.Status;
                    state.Status = 0;
                    state.Num = 0;
                    state.StopLoss = 0;
                    state.HoldBars = 0;
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
            // 止损冷却:分化度Z回到入场阈值以内(中性区,含反向穿0)才解除,杜绝止损后原地同向重开
            if (state.Status == 0 && state.IsCoolingDown)
            {
                if (_divergenceZScore <= entryZScore)
                {
                    state.IsCoolingDown = false;
                    state.CoolDownDir = 0;
                }
            }

            // ==================== 开仓逻辑 ====================
            if (state.Status == 0 && rankStable)
            {
                // 分化度Z-Score > entry → 赢家输家分化过度 → 赌反转
                //   输家篮子品种 → 做多(反弹)
                //   赢家篮子品种 → 做空(回调)

                int signalDir = 0;
                if (_divergenceZScore > entryZScore)
                {
                    if (rankData.IsLoser) signalDir = 1;       // 输家做多
                    else if (rankData.IsWinner) signalDir = 2;  // 赢家做空
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
                    decimal num = CalcLots(mktSymbol, currentPrice);
                    decimal atrDecimal = (decimal)rankData.Atr;

                    if (signalDir == 1)
                    {
                        state.Status = 1;
                        state.Num = num;
                        state.EntryPrice = currentPrice;
                        state.HoldBars = 0;
                        if (atrStopMultiplier > 0 && atrDecimal > 0)
                        {
                            state.StopLoss = currentPrice - atrDecimal * (decimal)atrStopMultiplier;
                        }
                        Trade(mktSymbol, OrderType.BUY, currentPrice, num, period, sendMode);
                    }
                    else if (signalDir == 2)
                    {
                        state.Status = 2;
                        state.Num = num;
                        state.EntryPrice = currentPrice;
                        state.HoldBars = 0;
                        if (atrStopMultiplier > 0 && atrDecimal > 0)
                        {
                            state.StopLoss = currentPrice + atrDecimal * (decimal)atrStopMultiplier;
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

        #endregion
    }
}
