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
    /// 趋势感知反转海龟策略。
    /// 在当前K线之前的回溯窗口中，找到最低Low对应K线的High作为做多触发价，
    /// 找到最高High对应K线的Low作为做空触发价。最近极值为底且Close高于做多触发价时持多，
    /// 最近极值为顶且Close低于做空触发价时持空；尚未确认或顶底同根时保持空仓。
    /// </summary>
    public class TrendAwareReversalTurtleTrading : StgBase
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
            public int TrendDirection { get; set; }
            public bool WaitingPullbackReentry { get; set; }
            public decimal PullbackReentryLevel { get; set; }
            public int StopReentryDirection { get; set; }
            public decimal StopReentryLevel { get; set; }
            public decimal StopReentryExitPrice { get; set; }
            public bool StopReentryPullbackSeen { get; set; }

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

        public TrendAwareReversalTurtleTrading()
        {
        }

        public TrendAwareReversalTurtleTrading(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            sd.ArgDic["lookbackPeriod"] = 10;
            sd.ArgDic["atrPeriod"] = 20;
            sd.ArgDic["stopATR"] = 3.0m;
            // 趋势利润不设上限：只由硬止损、结构通道或反向信号退出。
            sd.ArgDic["exitLookbackPeriod"] = 20;
            sd.ArgDic["trendRecentPeriod"] = 14;
            sd.ArgDic["trendGlobalPeriod"] = 120;
            sd.ArgDic["trendSlopeThreshold"] = 0.12m;
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
            sd.ArgDescDic["trendRecentPeriod"] = new ArgDesc() { Text = "近期走势周期", Explain = "用于识别当前走势的最新K线数量，默认14", Type = "number" };
            sd.ArgDescDic["trendGlobalPeriod"] = new ArgDesc() { Text = "全局趋势周期", Explain = "近期窗口之前用于识别全局趋势的最大K线数量，不足时使用全部历史，默认120", Type = "number" };
            sd.ArgDescDic["trendSlopeThreshold"] = new ArgDesc() { Text = "趋势斜率阈值", Explain = "回归区间涨跌占全局振幅的最小比例，低于该值视为震荡", Type = "number" };
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
            int exitLookbackPeriod = Convert.ToInt32(ArgDic["exitLookbackPeriod"]);
            decimal stopAtr = Convert.ToDecimal(ArgDic["stopATR"]);
            int trendRecentPeriod = Convert.ToInt32(ArgDic["trendRecentPeriod"]);
            int trendGlobalPeriod = Convert.ToInt32(ArgDic["trendGlobalPeriod"]);
            decimal trendSlopeThreshold = Convert.ToDecimal(ArgDic["trendSlopeThreshold"]);
            int enablePyramiding = Convert.ToInt32(ArgDic["enablePyramiding"]);
            decimal pyramidingAtr = Convert.ToDecimal(ArgDic["pyramidingATR"]);
            int maxUnits = Convert.ToInt32(ArgDic["maxUnits"]);
            int lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            if (lookbackPeriod <= 0 || atrPeriod <= 0 || exitLookbackPeriod < 0 || stopAtr <= 0 || trendRecentPeriod < 2 ||
                trendGlobalPeriod < 2 || trendSlopeThreshold <= 0 || enablePyramiding < 0 ||
                enablePyramiding > 1 || pyramidingAtr <= 0 || maxUnits <= 0 || lotsMode < 0 ||
                lotsMode > 2 || mode < 0 || mode > 2 || sendMode < 0 || sendMode > 1)
            {
                return;
            }

            int requiredBars = Math.Max(Math.Max(lookbackPeriod + 1, atrPeriod + 1), trendRecentPeriod + 2);
            if (tu.QuoteList.Count < requiredBars) return;

            SkQuote q = tu.QuoteList[tu.QuoteList.Count - 1];
            decimal atr = CalculateAtr(tu.QuoteList, atrPeriod);
            if (atr <= 0) return;

            ReferenceLevels levels = CalculateReferenceLevels(tu.QuoteList, lookbackPeriod);
            ExitLevels? exitLevels = exitLookbackPeriod > 0 && tu.QuoteList.Count >= exitLookbackPeriod + 1
                ? CalculateExitLevels(tu.QuoteList, exitLookbackPeriod)
                : null;
            TrendAssessment trend = ClassifyTrend(tu.QuoteList, trendRecentPeriod,
                trendGlobalPeriod, trendSlopeThreshold);
            Plot("main", "longTrigger", PlotType.LINE, (double)levels.LongTrigger);
            Plot("main", "shortTrigger", PlotType.LINE, (double)levels.ShortTrigger);
            Plot("sub0", "ATR", PlotType.LINE, (double)atr);
            Plot("sub0", "trendRegime", PlotType.LINE, (double)(int)trend.Regime);

            string stateKey = tu.GetStateKey();
            if (!_stateDic.TryGetValue(stateKey, out PositionState state))
            {
                state = new PositionState();
                _stateDic[stateKey] = state;
            }

            int turtleDirection = EvaluateTargetDirection(q.Close, levels);
            DateTimeOffset? turtleExtremeDate = GetConfirmedExtremeDate(tu.QuoteList, turtleDirection, levels);
            if (IsStructureStale(state, turtleExtremeDate)) turtleDirection = 0;

            // 反转海龟信号负责抓底/顶；唐奇安突破负责补上没有形成标准反转结构的趋势段。
            int trendEntryDirection = turtleDirection;
            decimal priorHigh = tu.QuoteList[levels.HighestBarIndex].High;
            decimal priorLow = tu.QuoteList[levels.LowestBarIndex].Low;
            if (q.Close > priorHigh) trendEntryDirection = 1;
            else if (q.Close < priorLow) trendEntryDirection = -1;

            int swingStart = Math.Max(0, tu.QuoteList.Count - 1 - trendGlobalPeriod);
            decimal longSupport = FindLatestConfirmedSwingLevel(tu.QuoteList, swingStart,
                tu.QuoteList.Count - 1, 1);
            decimal shortResistance = FindLatestConfirmedSwingLevel(tu.QuoteList, swingStart,
                tu.QuoteList.Count - 1, -1);
            MarketAction action = ResolveMarketAction(state.Direction, state.TrendDirection,
                state.WaitingPullbackReentry, state.PullbackReentryLevel, trend, turtleDirection,
                trendEntryDirection, longSupport, shortResistance, q.High, q.Low);

            if (state.StopReentryDirection != 0 && action.TrendDirection != 0 &&
                action.TrendDirection != state.StopReentryDirection)
            {
                state.StopReentryDirection = 0;
                state.StopReentryLevel = 0;
                state.StopReentryExitPrice = 0;
                state.StopReentryPullbackSeen = false;
            }

            // 再入场是严格的跨K线两阶段状态机：本根K线只能确认回撤，至少从下一根K线起
            // 才允许用顺势方向的实际高/低价突破离场K线极值。不能只看收盘价，否则盘中
            // 已经完成的回撤与突破会被漏掉，导致原趋势恢复后一直接不回来。
            bool pullbackSeenBeforeCurrentBar = state.StopReentryPullbackSeen;
            if (state.StopReentryDirection != 0)
            {
                state.StopReentryPullbackSeen = UpdateStopReentryPullbackSeen(
                    state.StopReentryDirection, state.StopReentryExitPrice,
                    state.StopReentryPullbackSeen, q.High, q.Low);
            }

            state.TrendDirection = action.TrendDirection;
            state.WaitingPullbackReentry = false;
            state.PullbackReentryLevel = 0;

            int nextDirection = action.TargetDirection;
            int stopReentryDirection = ResolveStopReentryDirection(state.StopReentryDirection,
                state.StopReentryLevel, pullbackSeenBeforeCurrentBar, action.TrendDirection, q.High, q.Low);
            if (state.Direction == 0 && state.StopReentryDirection != 0 &&
                nextDirection == state.StopReentryDirection)
            {
                // 待恢复期间屏蔽普通同向信号，避免平仓后一两根K线直接重新开仓。
                nextDirection = 0;
            }
            if (state.Direction == 0 && stopReentryDirection != 0)
            {
                nextDirection = stopReentryDirection;
                state.StopReentryDirection = 0;
                state.StopReentryLevel = 0;
                state.StopReentryExitPrice = 0;
                state.StopReentryPullbackSeen = false;
            }
            if ((nextDirection > 0 && mode == 2) || (nextDirection < 0 && mode == 1)) nextDirection = 0;
            if ((state.Direction > 0 && mode == 2) || (state.Direction < 0 && mode == 1)) nextDirection = 0;

            bool hardStop = state.Direction > 0
                ? q.Close <= state.StopPrice
                : state.Direction < 0 && q.Close >= state.StopPrice;
            bool structureExit = exitLevels.HasValue && (state.Direction > 0
                ? q.Close < exitLevels.Value.Low
                : state.Direction < 0 && q.Close > exitLevels.Value.High);
            if (hardStop || structureExit) nextDirection = 0;

            if (state.Direction != nextDirection)
            {
                if (state.Direction != 0)
                {
                    if (state.Direction == action.TrendDirection)
                    {
                        state.StopReentryDirection = state.Direction;
                        // 所有顺势离场统一进入同一个两阶段恢复状态机；旧的支撑/阻力
                        // 直接接回路径不得绕过“先反向回撤、再突破离场K线极值”的确认。
                        state.StopReentryLevel = state.Direction > 0 ? q.High : q.Low;
                        state.StopReentryExitPrice = q.Close;
                        state.StopReentryPullbackSeen = false;
                    }
                    else
                    {
                        state.StopReentryDirection = 0;
                        state.StopReentryLevel = 0;
                        state.StopReentryExitPrice = 0;
                        state.StopReentryPullbackSeen = false;
                    }
                    ClosePosition(tu.MktSymbol, period, q.Close, state, sendMode);
                }
                if (nextDirection != 0)
                    OpenPosition(tu, period, q.Close, atr, nextDirection, state, stopAtr, lotsMode, sendMode);
                if (action.ConsumesTurtleSignal && turtleDirection != 0)
                {
                    state.ConsumedDirection = turtleDirection;
                    state.ConsumedExtremeDate = turtleExtremeDate;
                }
                return;
            }

            if (state.Direction == 0) return;
            PlotProtection(state);
            if (enablePyramiding == 1 && ShouldPyramid(state.Direction, q.Close, state.LastEntryPrice,
                state.EntryAtr, pyramidingAtr, state.UnitCount, maxUnits))
            {
                AddPosition(tu.MktSymbol, period, q.Close, state, stopAtr, sendMode);
            }
        }

        internal readonly struct ReferenceLevels
        {
            public ReferenceLevels(decimal longTrigger, decimal shortTrigger, int lowestBarIndex,
                int highestBarIndex, bool longStructureAllowed = true, bool shortStructureAllowed = true)
            {
                LongTrigger = longTrigger;
                ShortTrigger = shortTrigger;
                LowestBarIndex = lowestBarIndex;
                HighestBarIndex = highestBarIndex;
                LongStructureAllowed = longStructureAllowed;
                ShortStructureAllowed = shortStructureAllowed;
            }

            public decimal LongTrigger { get; }
            public decimal ShortTrigger { get; }
            public int LowestBarIndex { get; }
            public int HighestBarIndex { get; }
            public bool LongStructureAllowed { get; }
            public bool ShortStructureAllowed { get; }

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
                highestBarIndex,
                HasNonDescendingRecentSwingLows(quotes, start, endExclusive),
                HasNonAscendingRecentSwingHighs(quotes, start, endExclusive));
        }

        internal static bool HasNonDescendingRecentSwingLows(List<SkQuote> quotes, int start, int endExclusive)
        {
            int previousSwingLowIndex = -1;
            int latestSwingLowIndex = -1;
            for (int i = start + 2; i < endExclusive - 2; i++)
            {
                decimal low = quotes[i].Low;
                bool isSwingLow = low < quotes[i - 1].Low && low < quotes[i - 2].Low &&
                    low < quotes[i + 1].Low && low < quotes[i + 2].Low;
                if (!isSwingLow) continue;
                previousSwingLowIndex = latestSwingLowIndex;
                latestSwingLowIndex = i;
            }

            if (previousSwingLowIndex < 0 || latestSwingLowIndex < 0) return false;
            if (quotes[latestSwingLowIndex].Low <= quotes[previousSwingLowIndex].Low) return false;
            for (int i = latestSwingLowIndex + 1; i < endExclusive; i++)
                if (quotes[i].Low < quotes[latestSwingLowIndex].Low) return false;
            return true;
        }

        internal static bool HasNonAscendingRecentSwingHighs(List<SkQuote> quotes, int start, int endExclusive)
        {
            int previousSwingHighIndex = -1;
            int latestSwingHighIndex = -1;
            for (int i = start + 2; i < endExclusive - 2; i++)
            {
                decimal high = quotes[i].High;
                bool isSwingHigh = high > quotes[i - 1].High && high > quotes[i - 2].High &&
                    high > quotes[i + 1].High && high > quotes[i + 2].High;
                if (!isSwingHigh) continue;
                previousSwingHighIndex = latestSwingHighIndex;
                latestSwingHighIndex = i;
            }

            if (previousSwingHighIndex < 0 || latestSwingHighIndex < 0) return false;
            if (quotes[latestSwingHighIndex].High >= quotes[previousSwingHighIndex].High) return false;
            for (int i = latestSwingHighIndex + 1; i < endExclusive; i++)
                if (quotes[i].High > quotes[latestSwingHighIndex].High) return false;
            return true;
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

        internal enum TrendRegime
        {
            Sideways = 0,
            Following = 1,
            Pullback = 2,
            Reversing = 3,
        }

        internal readonly struct TrendAssessment
        {
            public TrendAssessment(TrendRegime regime, int globalDirection, int recentDirection,
                decimal globalLow, decimal globalHigh, decimal latestClose)
            {
                Regime = regime;
                GlobalDirection = globalDirection;
                RecentDirection = recentDirection;
                GlobalLow = globalLow;
                GlobalHigh = globalHigh;
                LatestClose = latestClose;
            }

            public TrendRegime Regime { get; }
            public int GlobalDirection { get; }
            public int RecentDirection { get; }
            public decimal GlobalLow { get; }
            public decimal GlobalHigh { get; }
            public decimal LatestClose { get; }
        }

        internal static TrendAssessment ClassifyTrend(List<SkQuote> quotes, int recentPeriod,
            int globalPeriod, decimal slopeThreshold)
        {
            if (quotes == null) throw new ArgumentNullException(nameof(quotes));
            if (recentPeriod < 2 || globalPeriod < 2 || slopeThreshold <= 0 || quotes.Count < recentPeriod + 2)
                throw new ArgumentOutOfRangeException(nameof(recentPeriod));

            int recentStart = quotes.Count - recentPeriod;
            int globalCount = Math.Min(globalPeriod, recentStart);
            int globalStart = recentStart - globalCount;

            decimal globalLow = quotes[globalStart].Low;
            decimal globalHigh = quotes[globalStart].High;
            for (int i = globalStart + 1; i < recentStart; i++)
            {
                globalLow = Math.Min(globalLow, quotes[i].Low);
                globalHigh = Math.Max(globalHigh, quotes[i].High);
            }

            decimal globalRange = globalHigh - globalLow;
            decimal latestClose = quotes[quotes.Count - 1].Close;
            if (globalRange <= 0)
                return new TrendAssessment(TrendRegime.Sideways, 0, 0,
                    globalLow, globalHigh, latestClose);

            decimal globalSlope = CalculateRegressionSlope(quotes, globalStart, globalCount);
            decimal recentSlope = CalculateRegressionSlope(quotes, recentStart, recentPeriod);
            decimal globalMove = globalSlope * (globalCount - 1) / globalRange;
            decimal recentMove = recentSlope * (recentPeriod - 1) / globalRange;
            int globalDirection = Math.Abs(globalMove) >= slopeThreshold ? Math.Sign(globalMove) : 0;
            int recentDirection = Math.Abs(recentMove) >= slopeThreshold ? Math.Sign(recentMove) : 0;

            // 只有当前收盘仍站在旧区间外、且近期斜率同向，突破才改变主趋势。
            // 不能因为14根窗口内曾经创新高/新低，就在价格已经折返后继续沿用过期方向。
            if (latestClose > globalHigh && recentDirection > 0)
                return new TrendAssessment(TrendRegime.Following, 1, 1,
                    globalLow, globalHigh, latestClose);
            if (latestClose < globalLow && recentDirection < 0)
                return new TrendAssessment(TrendRegime.Following, -1, -1,
                    globalLow, globalHigh, latestClose);

            if (globalDirection == 0 || recentDirection == 0)
                return new TrendAssessment(TrendRegime.Sideways, globalDirection, recentDirection,
                    globalLow, globalHigh, latestClose);
            if (recentDirection == globalDirection)
                return new TrendAssessment(TrendRegime.Following, globalDirection, recentDirection,
                    globalLow, globalHigh, latestClose);

            decimal globalEnd = CalculateRegressionValueAtEnd(quotes, globalStart, globalCount);
            bool crossedGlobalBaseline = globalDirection > 0
                ? latestClose < globalEnd
                : latestClose > globalEnd;
            TrendRegime regime = crossedGlobalBaseline ? TrendRegime.Reversing : TrendRegime.Pullback;
            return new TrendAssessment(regime, globalDirection, recentDirection,
                globalLow, globalHigh, latestClose);
        }

        internal readonly struct MarketAction
        {
            public MarketAction(int targetDirection, int trendDirection, bool waitingPullbackReentry,
                decimal pullbackReentryLevel, bool consumesTurtleSignal)
            {
                TargetDirection = targetDirection;
                TrendDirection = trendDirection;
                WaitingPullbackReentry = waitingPullbackReentry;
                PullbackReentryLevel = pullbackReentryLevel;
                ConsumesTurtleSignal = consumesTurtleSignal;
            }

            public int TargetDirection { get; }
            public int TrendDirection { get; }
            public bool WaitingPullbackReentry { get; }
            public decimal PullbackReentryLevel { get; }
            public bool ConsumesTurtleSignal { get; }
        }

        internal static MarketAction ResolveMarketAction(int currentDirection, int rememberedTrendDirection,
            bool waitingPullbackReentry, decimal pullbackReentryLevel, TrendAssessment trend,
            int turtleDirection, int trendEntryDirection, decimal longSupport, decimal shortResistance,
            decimal currentHigh, decimal currentLow)
        {
            int trendDirection = rememberedTrendDirection;

            if (trend.Regime == TrendRegime.Reversing && trend.RecentDirection != 0 &&
                trend.RecentDirection == -trend.GlobalDirection)
            {
                int reversalDirection = trend.RecentDirection;
                return new MarketAction(reversalDirection, reversalDirection, false, 0,
                    turtleDirection == reversalDirection);
            }

            if (trend.Regime == TrendRegime.Following)
            {
                if (trend.GlobalDirection != 0) trendDirection = trend.GlobalDirection;
                // 已有逆势仓先退出；顺势开仓可由底/顶反转或唐奇安突破触发，避免追着每次斜率抖动反手。
                if (currentDirection != 0 && currentDirection != trendDirection)
                    return new MarketAction(0, trendDirection, false, 0, false);
                int target = trendEntryDirection == trendDirection ? trendDirection : currentDirection;
                bool consumesSignal = turtleDirection != 0 && turtleDirection == target;
                return new MarketAction(target, trendDirection, false, 0, consumesSignal);
            }

            if (trend.Regime == TrendRegime.Pullback)
            {
                if (trendDirection == 0) trendDirection = trend.GlobalDirection;
                if (currentDirection == trendDirection && turtleDirection == -trendDirection)
                {
                    // 反向海龟结构只负责平掉当前顺势仓；是否接回由 OnBar 中统一的
                    // 两阶段再入场状态机决定，不能在支撑/阻力处直接重开。
                    return new MarketAction(0, trendDirection, false, 0, true);
                }
                return new MarketAction(currentDirection, trendDirection, false, 0, false);
            }

            // 震荡仅在全局区间边界区域并出现对应海龟结构时高抛低吸。
            decimal range = trend.GlobalHigh - trend.GlobalLow;
            decimal lowerBoundary = trend.GlobalLow + range * 0.25m;
            decimal upperBoundary = trend.GlobalHigh - range * 0.25m;
            int sidewaysTarget = currentDirection;
            if (range > 0 && turtleDirection > 0 && currentLow <= lowerBoundary) sidewaysTarget = 1;
            if (range > 0 && turtleDirection < 0 && currentHigh >= upperBoundary) sidewaysTarget = -1;
            return new MarketAction(sidewaysTarget, trendDirection, false, 0,
                sidewaysTarget != currentDirection);
        }

        internal static bool UpdateStopReentryPullbackSeen(int pendingDirection, decimal exitPrice,
            bool pullbackSeen, decimal currentHigh, decimal currentLow)
        {
            if (pullbackSeen) return true;
            if (pendingDirection > 0) return currentLow < exitPrice;
            if (pendingDirection < 0) return currentHigh > exitPrice;
            return false;
        }

        internal static int ResolveStopReentryDirection(int pendingDirection, decimal stopLevel,
            bool pullbackSeenBeforeCurrentBar, int trendDirection, decimal currentHigh, decimal currentLow)
        {
            if (!pullbackSeenBeforeCurrentBar || pendingDirection == 0 ||
                pendingDirection != trendDirection || stopLevel <= 0)
                return 0;
            if (pendingDirection > 0) return currentHigh > stopLevel ? 1 : 0;
            return currentLow < stopLevel ? -1 : 0;
        }

        internal static decimal FindLatestConfirmedSwingLevel(List<SkQuote> quotes, int start,
            int endExclusive, int direction)
        {
            if (quotes == null) throw new ArgumentNullException(nameof(quotes));
            if (direction != 1 && direction != -1) throw new ArgumentOutOfRangeException(nameof(direction));
            start = Math.Max(start, 0);
            endExclusive = Math.Min(endExclusive, quotes.Count);
            for (int i = endExclusive - 3; i >= start + 2; i--)
            {
                if (direction > 0)
                {
                    decimal low = quotes[i].Low;
                    if (low < quotes[i - 1].Low && low < quotes[i - 2].Low &&
                        low < quotes[i + 1].Low && low < quotes[i + 2].Low) return low;
                }
                else
                {
                    decimal high = quotes[i].High;
                    if (high > quotes[i - 1].High && high > quotes[i - 2].High &&
                        high > quotes[i + 1].High && high > quotes[i + 2].High) return high;
                }
            }
            return 0;
        }

        private static decimal CalculateRegressionSlope(List<SkQuote> quotes, int start, int count)
        {
            decimal sumX = (decimal)count * (count - 1) / 2m;
            decimal sumXX = (decimal)(count - 1) * count * (2 * count - 1) / 6m;
            decimal sumY = 0;
            decimal sumXY = 0;
            for (int i = 0; i < count; i++)
            {
                decimal close = quotes[start + i].Close;
                sumY += close;
                sumXY += i * close;
            }
            decimal denominator = count * sumXX - sumX * sumX;
            return denominator == 0 ? 0 : (count * sumXY - sumX * sumY) / denominator;
        }

        private static decimal CalculateRegressionValueAtEnd(List<SkQuote> quotes, int start, int count)
        {
            decimal slope = CalculateRegressionSlope(quotes, start, count);
            decimal averageX = (count - 1) / 2m;
            decimal averageY = 0;
            for (int i = 0; i < count; i++) averageY += quotes[start + i].Close;
            averageY /= count;
            return averageY + slope * ((count - 1) - averageX);
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
