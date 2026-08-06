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
    /// 反转海龟策略。
    /// 在当前K线之前的回溯窗口中，找到最低Low对应K线的High作为做多触发价，
    /// 找到最高High对应K线的Low作为做空触发价。最近极值为底且Close高于做多触发价时持多，
    /// 最近极值为顶且Close低于做空触发价时持空；尚未确认或顶底同根时保持空仓。
    /// </summary>
    public class ReversalTurtleTrading : StgBase
    {
        private sealed class PositionState
        {
            public int Direction { get; set; }
            public decimal Num { get; set; }
            public decimal EntryPrice { get; set; }
            public decimal EntryAtr { get; set; }
            public decimal StopPrice { get; set; }
            public decimal UnitNum { get; set; }
            public decimal LastEntryPrice { get; set; }
            public int UnitCount { get; set; }
            public int ConsumedDirection { get; set; }
            public DateTimeOffset? ConsumedExtremeDate { get; set; }

            public void Reset()
            {
                Direction = 0;
                Num = 0;
                EntryPrice = 0;
                EntryAtr = 0;
                StopPrice = 0;
                UnitNum = 0;
                LastEntryPrice = 0;
                UnitCount = 0;
            }
        }

        private readonly Dictionary<string, PositionState> _stateDic = new Dictionary<string, PositionState>();

        public ReversalTurtleTrading()
        {
        }

        public ReversalTurtleTrading(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            sd.ArgDic["lookbackPeriod"] = 20;
            sd.ArgDic["atrPeriod"] = 20;
            sd.ArgDic["stopATR"] = 2.0m;
            // 趋势利润不设上限：只由硬止损、结构通道或反向信号退出。
            sd.ArgDic["exitLookbackPeriod"] = 20;
            sd.ArgDic["enablePyramiding"] = 0;
            sd.ArgDic["pyramidingATR"] = 0.5m;
            sd.ArgDic["maxUnits"] = 4;

            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;
            sd.ArgDic["accountEquity"] = 100000m;
            sd.ArgDic["riskPerTrade"] = 0.01m;

            sd.ArgDic["mode"] = 0;
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["lookbackPeriod"] = new ArgDesc() { Text = "极值回溯周期", Explain = "从当前K线之前的已完成K线中寻找最低/最高参考K线", Type = "number" };
            sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "硬止损和风险仓位使用的ATR周期", Type = "number" };
            sd.ArgDescDic["stopATR"] = new ArgDesc() { Text = "硬止损ATR", Explain = "初始止损距离；必须大于0", Type = "number" };
            sd.ArgDescDic["exitLookbackPeriod"] = new ArgDesc() { Text = "结构出场周期", Explain = "持仓后已完成K线的结构出场回溯周期；0表示关闭", Type = "number" };
            sd.ArgDescDic["enablePyramiding"] = new ArgDesc() { Text = "启用加仓", Explain = "按海龟规则在价格顺势移动后逐单位加仓", Options = "0:关闭|1:开启", Type = "select" };
            sd.ArgDescDic["pyramidingATR"] = new ArgDesc() { Text = "加仓ATR间距", Explain = "价格相对上次入场价每顺势移动该ATR倍数加一个单位", Type = "number" };
            sd.ArgDescDic["maxUnits"] = new ArgDesc() { Text = "最大单位数", Explain = "包含首次入场在内的最大持仓单位数", Type = "number" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "仓位计算方式；加仓沿用首次入场的单位手数", Options = "0:固定手数|1:固定金额|2:按风险计算", Type = "select" };
            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "固定手数", Explain = "固定手数模式的下单数量", Type = "number" };
            sd.ArgDescDic["money"] = new ArgDesc() { Text = "固定金额", Explain = "固定金额模式的名义资金", Type = "number" };
            sd.ArgDescDic["accountEquity"] = new ArgDesc() { Text = "账户权益", Explain = "风险仓位模式使用的账户权益", Type = "number" };
            sd.ArgDescDic["riskPerTrade"] = new ArgDesc() { Text = "单笔风险比例", Explain = "风险仓位模式下每笔最大初始风险占权益比例", Type = "number" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易方向", Explain = "切换为单向模式时会先平掉不允许方向的已有仓位", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即发单|1:下个开盘发单", Type = "select" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 1;
            sd.ColorDic["main-longTrigger"] = "#4CAF50";
            sd.ColorDic["main-shortTrigger"] = "#F44336";
            sd.ColorDic["main-stopLoss"] = "#FF9800";
            sd.ColorDic["sub0-ATR"] = "#2196F3";
            return sd;
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (!isFinal || tu.QuoteList == null) return;

            int lookbackPeriod = Convert.ToInt32(ArgDic["lookbackPeriod"]);
            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
            decimal stopAtr = Convert.ToDecimal(ArgDic["stopATR"]);
            int exitLookbackPeriod = Convert.ToInt32(ArgDic["exitLookbackPeriod"]);
            int enablePyramiding = Convert.ToInt32(ArgDic["enablePyramiding"]);
            decimal pyramidingAtr = Convert.ToDecimal(ArgDic["pyramidingATR"]);
            int maxUnits = Convert.ToInt32(ArgDic["maxUnits"]);
            int lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            if (lookbackPeriod <= 0 || atrPeriod <= 0 || stopAtr <= 0 ||
                exitLookbackPeriod <= 0 || enablePyramiding < 0 || enablePyramiding > 1 || pyramidingAtr <= 0 || maxUnits <= 0 ||
                lotsMode < 0 || lotsMode > 2 || mode < 0 || mode > 2 || sendMode < 0 || sendMode > 1)
            {
                return;
            }

            int requiredBars = Math.Max(Math.Max(lookbackPeriod + 1, atrPeriod + 1), exitLookbackPeriod + 1);
            if (tu.QuoteList.Count < requiredBars) return;

            SkQuote q = tu.QuoteList[tu.QuoteList.Count - 1];
            decimal atr = CalculateAtr(tu.QuoteList, atrPeriod);
            if (atr <= 0) return;

            var levels = CalculateReferenceLevels(tu.QuoteList, lookbackPeriod);
            decimal longTrigger = levels.LongTrigger;
            decimal shortTrigger = levels.ShortTrigger;
            var exitLevels = CalculateExitLevels(tu.QuoteList, exitLookbackPeriod);

            Plot("main", "longTrigger", PlotType.LINE, (double)longTrigger);
            Plot("main", "shortTrigger", PlotType.LINE, (double)shortTrigger);
            Plot("sub0", "ATR", PlotType.LINE, (double)atr);

            string stateKey = tu.GetStateKey();
            if (!_stateDic.TryGetValue(stateKey, out PositionState state))
            {
                state = new PositionState();
                _stateDic[stateKey] = state;
            }

            int confirmedDirection = EvaluateTargetDirection(q.Close, levels);
            DateTimeOffset? confirmedExtremeDate = GetConfirmedExtremeDate(tu.QuoteList, confirmedDirection, levels);
            if (IsStructureStale(state, confirmedExtremeDate))
            {
                confirmedDirection = 0;
            }
            if ((confirmedDirection > 0 && mode == 2) || (confirmedDirection < 0 && mode == 1))
            {
                confirmedDirection = 0;
            }

            // 0 表示最近极值尚未得到价格确认，不是“目标空仓”。
            // 未确认时维持已有仓位；只有确认的新方向才开仓或反手。
            if ((state.Direction > 0 && mode == 2) || (state.Direction < 0 && mode == 1))
            {
                ClosePosition(tu.MktSymbol, period, q.Close, state, sendMode);
                return;
            }

            int nextDirection = ResolvePositionDirection(state.Direction, confirmedDirection);
            if (state.Direction != nextDirection)
            {
                if (state.Direction != 0)
                {
                    ClosePosition(tu.MktSymbol, period, q.Close, state, sendMode);
                }
                if (nextDirection != 0)
                {
                    OpenPosition(tu, period, q.Close, atr, nextDirection, state, stopAtr, lotsMode, sendMode);
                    if (state.Direction == nextDirection)
                    {
                        state.ConsumedDirection = nextDirection;
                        state.ConsumedExtremeDate = confirmedExtremeDate;
                    }
                }
                return;
            }

            if (state.Direction == 0) return;

            PlotProtection(state);
            bool shouldExit = state.Direction > 0
                ? q.Close <= state.StopPrice || q.Close < exitLevels.Low
                : q.Close >= state.StopPrice || q.Close > exitLevels.High;
            if (shouldExit)
            {
                ClosePosition(tu.MktSymbol, period, q.Close, state, sendMode);
                return;
            }

            if (enablePyramiding == 1 && ShouldPyramid(state.Direction, q.Close, state.LastEntryPrice,
                state.EntryAtr, pyramidingAtr, state.UnitCount, maxUnits))
            {
                AddPosition(tu.MktSymbol, period, q.Close, state, stopAtr, sendMode);
            }
        }

        internal readonly struct ReferenceLevels
        {
            public ReferenceLevels(decimal longTrigger, decimal shortTrigger, int lowestBarIndex, int highestBarIndex)
            {
                LongTrigger = longTrigger;
                ShortTrigger = shortTrigger;
                LowestBarIndex = lowestBarIndex;
                HighestBarIndex = highestBarIndex;
            }

            public decimal LongTrigger { get; }
            public decimal ShortTrigger { get; }
            public int LowestBarIndex { get; }
            public int HighestBarIndex { get; }

            public void Deconstruct(out decimal longTrigger, out decimal shortTrigger,
                out int lowestBarIndex, out int highestBarIndex)
            {
                longTrigger = LongTrigger;
                shortTrigger = ShortTrigger;
                lowestBarIndex = LowestBarIndex;
                highestBarIndex = HighestBarIndex;
            }
        }

        internal static ReferenceLevels CalculateReferenceLevels(List<SkQuote> quotes, int lookbackPeriod)
        {
            if (quotes == null) throw new ArgumentNullException(nameof(quotes));
            if (lookbackPeriod <= 0 || quotes.Count < lookbackPeriod + 1)
                throw new ArgumentOutOfRangeException(nameof(lookbackPeriod));

            int endExclusive = quotes.Count - 1;
            int start = endExclusive - lookbackPeriod;
            int lowestBarIndex = start;
            int highestBarIndex = start;

            for (int i = start + 1; i < endExclusive; i++)
            {
                // 极值相同取最近一根，符合“最近的最低/最高K线”。
                if (quotes[i].Low <= quotes[lowestBarIndex].Low) lowestBarIndex = i;
                if (quotes[i].High >= quotes[highestBarIndex].High) highestBarIndex = i;
            }

            return new ReferenceLevels(
                quotes[lowestBarIndex].High,
                quotes[highestBarIndex].Low,
                lowestBarIndex,
                highestBarIndex);
        }

        internal readonly struct ExitLevels
        {
            public ExitLevels(decimal low, decimal high)
            {
                Low = low;
                High = high;
            }

            public decimal Low { get; }
            public decimal High { get; }
        }

        internal static ExitLevels CalculateExitLevels(List<SkQuote> quotes, int exitLookbackPeriod)
        {
            if (quotes == null) throw new ArgumentNullException(nameof(quotes));
            if (exitLookbackPeriod <= 0 || quotes.Count < exitLookbackPeriod + 1)
                throw new ArgumentOutOfRangeException(nameof(exitLookbackPeriod));

            int endExclusive = quotes.Count - 1;
            int start = endExclusive - exitLookbackPeriod;
            decimal low = quotes[start].Low;
            decimal high = quotes[start].High;
            for (int i = start + 1; i < endExclusive; i++)
            {
                low = Math.Min(low, quotes[i].Low);
                high = Math.Max(high, quotes[i].High);
            }
            return new ExitLevels(low, high);
        }

        internal static int ResolvePositionDirection(int currentDirection, int confirmedDirection)
        {
            return confirmedDirection == 0 ? currentDirection : confirmedDirection;
        }

        internal static int EvaluateTargetDirection(decimal close, ReferenceLevels levels)
        {
            if (levels.LowestBarIndex > levels.HighestBarIndex)
            {
                return close > levels.LongTrigger ? 1 : 0;
            }
            if (levels.HighestBarIndex > levels.LowestBarIndex)
            {
                return close < levels.ShortTrigger ? -1 : 0;
            }

            // 同一根K线同时成为最高与最低时没有明确的反转先后关系。
            return 0;
        }

        internal static DateTimeOffset? GetConfirmedExtremeDate(List<SkQuote> quotes, int direction, ReferenceLevels levels)
        {
            if (direction == 0) return null;
            int index = direction > 0 ? levels.LowestBarIndex : levels.HighestBarIndex;
            return quotes[index].Date;
        }

        internal static bool IsNewStructure(DateTimeOffset? consumedExtremeDate, DateTimeOffset? candidateExtremeDate)
        {
            return candidateExtremeDate.HasValue &&
                (!consumedExtremeDate.HasValue || candidateExtremeDate.Value > consumedExtremeDate.Value);
        }

        private static bool IsStructureStale(PositionState state, DateTimeOffset? extremeDate)
        {
            // 只有消费点之后新形成的极值结构才是新信号；
            // 窗口滚动淘汰旧极值后换出的更早K线不能重新触发交易。
            return !IsNewStructure(state.ConsumedExtremeDate, extremeDate);
        }

        private static decimal CalculateAtr(List<SkQuote> quotes, int atrPeriod)
        {
            var atrResult = quotes.GetAtr(atrPeriod).LastOrDefault();
            return atrResult?.Atr == null ? 0 : (decimal)atrResult.Atr.Value;
        }

        private void OpenPosition(TableUnit tu, Period period, decimal price, decimal atr, int direction,
            PositionState state, decimal stopAtr, int lotsMode, int sendMode)
        {
            decimal num = CalculateLots(tu, price, atr, stopAtr, lotsMode);
            if (num <= 0) return;

            state.Direction = direction;
            state.Num = num;
            state.EntryPrice = price;
            state.EntryAtr = atr;
            state.UnitNum = num;
            state.LastEntryPrice = price;
            state.UnitCount = 1;
            state.StopPrice = direction > 0 ? price - stopAtr * atr : price + stopAtr * atr;

            OrderType orderType = direction > 0 ? OrderType.BUY : OrderType.SELL;
            Trade(tu.MktSymbol, orderType, price, num, period, sendMode);
            PlotProtection(state);
        }

        internal static bool ShouldPyramid(int direction, decimal close, decimal lastEntryPrice,
            decimal entryAtr, decimal pyramidingAtr, int unitCount, int maxUnits)
        {
            if ((direction != 1 && direction != -1) || entryAtr <= 0 || pyramidingAtr <= 0 ||
                unitCount <= 0 || unitCount >= maxUnits)
            {
                return false;
            }

            decimal distance = pyramidingAtr * entryAtr;
            return direction > 0
                ? close >= lastEntryPrice + distance
                : close <= lastEntryPrice - distance;
        }

        private void AddPosition(string mktSymbol, Period period, decimal price,
            PositionState state, decimal stopAtr, int sendMode)
        {
            if (state.Direction == 0 || state.UnitNum <= 0) return;

            OrderType orderType = state.Direction > 0 ? OrderType.BUY : OrderType.SELL;
            Trade(mktSymbol, orderType, price, state.UnitNum, period, sendMode);

            state.Num += state.UnitNum;
            state.LastEntryPrice = price;
            state.UnitCount++;

            // 与经典海龟一致：每次加仓后，以最后一单位为基准把整组仓位的硬止损向盈利方向收紧。
            decimal pyramidStop = state.Direction > 0
                ? price - stopAtr * state.EntryAtr
                : price + stopAtr * state.EntryAtr;
            state.StopPrice = state.Direction > 0
                ? Math.Max(state.StopPrice, pyramidStop)
                : Math.Min(state.StopPrice, pyramidStop);
            PlotProtection(state);
        }

        private decimal CalculateLots(TableUnit tu, decimal price, decimal atr, decimal stopAtr, int lotsMode)
        {
            if (lotsMode == 0)
                return Math.Max(Convert.ToDecimal(ArgDic["lots"]), 0);

            var symbol = GetSymbol(tu.MktSymbol);
            decimal num;
            if (lotsMode == 1)
            {
                decimal denominator = price * symbol.multiplier * symbol.margin_ratio;
                decimal money = Convert.ToDecimal(ArgDic["money"]);
                if (denominator <= 0 || money <= 0) return 0;
                num = money / denominator;
            }
            else
            {
                decimal denominator = atr * stopAtr * symbol.multiplier;
                decimal equity = Convert.ToDecimal(ArgDic["accountEquity"]);
                decimal risk = Convert.ToDecimal(ArgDic["riskPerTrade"]);
                if (denominator <= 0 || equity <= 0 || risk <= 0) return 0;
                num = equity * risk / denominator;
            }

            if (symbol.symbol_type == (int)SymbolType.COIN)
            {
                if (symbol.scale <= 0) return 0;
                num = Math.Floor(num * symbol.scale) / symbol.scale;
            }
            else
            {
                num = Math.Floor(num);
            }
            return Math.Max(num, 0);
        }

        private void ClosePosition(string mktSymbol, Period period, decimal price, PositionState state, int sendMode)
        {
            if (state.Direction == 0 || state.Num <= 0)
            {
                state.Reset();
                return;
            }

            OrderType orderType = state.Direction > 0 ? OrderType.SELL_TO_COVER : OrderType.BUY_TO_COVER;
            Trade(mktSymbol, orderType, price, state.Num, period, sendMode);
            state.Reset();
        }

        private void PlotProtection(PositionState state)
        {
            if (state.Direction == 0) return;
            Plot("main", "stopLoss", PlotType.LINE, (double)state.StopPrice);
        }
    }
}
