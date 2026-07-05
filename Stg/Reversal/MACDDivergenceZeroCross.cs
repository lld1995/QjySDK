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
    /// MACD背离+零轴交叉组合交易系统
    /// 
    /// 核心理念：结合MACD背离信号和零轴交叉，形成高胜率的交易系统
    /// 
    /// 策略逻辑：
    /// 1. 主信号 - MACD背离：
    ///    - 底背离做多：价格创新低，但MACD未创新低（动能减弱，反转信号）
    ///    - 顶背离做空：价格创新高，但MACD未创新高（动能减弱，反转信号）
    /// 
    /// 2. 确认信号 - 零轴交叉：
    ///    - MACD上穿零轴：多头趋势确认
    ///    - MACD下穿零轴：空头趋势确认
    /// 
    /// 3. 组合模式：
    ///    - 模式0：背离+零轴交叉双重确认（最严格）
    ///    - 模式1：仅背离信号
    ///    - 模式2：仅零轴交叉信号
    ///    - 模式3：背离或零轴交叉任一触发
    /// 
    /// 4. 出场策略：
    ///    - 反向信号出场
    ///    - ATR动态止损
    ///    - 固定止盈止损
    /// </summary>
    public class MACDDivergenceZeroCross : StgBase
    {
        public MACDDivergenceZeroCross()
        {
        }

        public MACDDivergenceZeroCross(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // MACD参数
            sd.ArgDic["fastPeriod"] = 12;           // 快速EMA周期
            sd.ArgDic["slowPeriod"] = 26;           // 慢速EMA周期
            sd.ArgDic["signalPeriod"] = 9;          // 信号线周期

            // 背离检测参数
            sd.ArgDic["lookbackPeriod"] = 14;       // 极值点回溯周期
            sd.ArgDic["minDivergenceBars"] = 5;     // 两个极值点之间最小K线数
            sd.ArgDic["maxDivergenceBars"] = 60;    // 两个极值点之间最大K线数

            // 信号组合模式
            sd.ArgDic["signalMode"] = 0;            // 0:背离+零轴双确认 1:仅背离 2:仅零轴交叉 3:任一触发

            // 零轴交叉参数
            sd.ArgDic["zeroCrossConfirmBars"] = 2;  // 零轴交叉确认K线数

            // ATR止损参数
            sd.ArgDic["atrPeriod"] = 14;            // ATR周期
            sd.ArgDic["atrStopMultiplier"] = 2.0;   // ATR止损倍数
            sd.ArgDic["atrProfitMultiplier"] = 3.0; // ATR止盈倍数
            sd.ArgDic["useAtrStop"] = 1;            // 是否使用ATR止损止盈

            // 固定止损止盈（当不使用ATR时）
            sd.ArgDic["stopLossPercent"] = 5.0;     // 止损百分比
            sd.ArgDic["takeProfitPercent"] = 10.0;  // 止盈百分比

            // 移动止损
            sd.ArgDic["useTrailingStop"] = 1;       // 是否使用移动止损
            sd.ArgDic["trailingActivation"] = 1.5;  // 移动止损激活倍数（盈利达到ATR*此值时激活）
            sd.ArgDic["trailingDistance"] = 1.0;    // 移动止损距离（ATR倍数）

            // 交易方向
            sd.ArgDic["mode"] = 0;        // 0:双向 1:仅做多 2:仅做空

            // 发单模式
            sd.ArgDic["sendMode"] = 0;              // 0:立即 1:下个开盘

            // 手数控制
            sd.ArgDic["lotsMode"] = 1;              // 0:固定手数 1:固定金额
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;

            // 参数说明
            sd.ArgDescDic["fastPeriod"] = new ArgDesc() { Text = "快速EMA周期", Explain = "MACD快速线周期，默认12", Type = "number" };
            sd.ArgDescDic["slowPeriod"] = new ArgDesc() { Text = "慢速EMA周期", Explain = "MACD慢速线周期，默认26", Type = "number" };
            sd.ArgDescDic["signalPeriod"] = new ArgDesc() { Text = "信号线周期", Explain = "MACD信号线周期，默认9", Type = "number" };
            sd.ArgDescDic["lookbackPeriod"] = new ArgDesc() { Text = "极值回溯周期", Explain = "寻找价格和MACD极值点的回溯周期", Type = "number" };
            sd.ArgDescDic["minDivergenceBars"] = new ArgDesc() { Text = "最小背离间隔", Explain = "两个极值点之间最少K线数", Type = "number" };
            sd.ArgDescDic["maxDivergenceBars"] = new ArgDesc() { Text = "最大背离间隔", Explain = "两个极值点之间最多K线数", Type = "number" };
            sd.ArgDescDic["signalMode"] = new ArgDesc() { Text = "信号模式", Explain = "信号触发方式", Options = "0:背离+零轴双确认|1:仅背离|2:仅零轴交叉|3:任一触发", Type = "select" };
            sd.ArgDescDic["zeroCrossConfirmBars"] = new ArgDesc() { Text = "零轴确认K线", Explain = "MACD穿越零轴后需要确认的K线数", Type = "number" };
            sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "ATR计算周期", Type = "number" };
            sd.ArgDescDic["atrStopMultiplier"] = new ArgDesc() { Text = "ATR止损倍数", Explain = "止损距离=ATR*此倍数", Type = "number" };
            sd.ArgDescDic["atrProfitMultiplier"] = new ArgDesc() { Text = "ATR止盈倍数", Explain = "止盈距离=ATR*此倍数", Type = "number" };
            sd.ArgDescDic["useAtrStop"] = new ArgDesc() { Text = "ATR止损", Explain = "使用ATR动态止损", Options = "0:使用固定百分比|1:使用ATR动态止损", Type = "select" };
            sd.ArgDescDic["stopLossPercent"] = new ArgDesc() { Text = "止损百分比", Explain = "固定止损百分比", Type = "number" };
            sd.ArgDescDic["takeProfitPercent"] = new ArgDesc() { Text = "止盈百分比", Explain = "固定止盈百分比", Type = "number" };
            sd.ArgDescDic["useTrailingStop"] = new ArgDesc() { Text = "移动止损", Explain = "跟踪最高/低点调整止损", Options = "0:不使用|1:使用移动止损", Type = "bool" };
            sd.ArgDescDic["trailingActivation"] = new ArgDesc() { Text = "移动止损激活", Explain = "盈利达到ATR*此值时激活移动止损", Type = "number" };
            sd.ArgDescDic["trailingDistance"] = new ArgDesc() { Text = "移动止损距离", Explain = "移动止损与最高/低点的距离（ATR倍数）", Type = "number" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易方向", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };

            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };

            sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 1;

            // 颜色配置
            sd.ColorDic["macd-macd"] = "#2196F3";               // MACD线蓝色
            sd.ColorDic["macd-signal"] = "#FF9800";             // 信号线橙色
            sd.ColorDic["macd-histogram"] = "#F6465D;#0ECB81"; // 柱状图红/绿
            sd.ColorDic["main-stopLoss"] = "#E74C3C";           // 止损线红色
            sd.ColorDic["main-takeProfit"] = "#27AE60";         // 止盈线绿色

            sd.MidValDic["macd"] = 0;

            return sd;
        }

        /// <summary>
        /// 持仓状态
        /// </summary>
        private class State
        {
            public int Position { get; set; }           // 0:空仓 1:多头 -1:空头
            public decimal Num { get; set; }            // 持仓数量
            public decimal EntryPrice { get; set; }     // 入场价格
            public decimal StopLoss { get; set; }       // 止损价格
            public decimal TakeProfit { get; set; }     // 止盈价格
            public decimal EntryAtr { get; set; }       // 入场时ATR值
            public decimal HighestSinceEntry { get; set; }  // 入场后最高价（用于移动止损）
            public decimal LowestSinceEntry { get; set; }   // 入场后最低价（用于移动止损）
            public bool TrailingActivated { get; set; } // 移动止损是否已激活

            // 背离检测状态
            public bool BullDivergenceDetected { get; set; }    // 是否检测到底背离
            public bool BearDivergenceDetected { get; set; }    // 是否检测到顶背离
            public int DivergenceBar { get; set; }              // 背离发生的K线索引

            // 背离信号ID及止损封锁（以 latestPriceLow.Index / latestPriceHigh.Index 作为稳定信号ID）
            public int LastBullPivotIndex { get; set; } = -1;       // 当前已记录的底背离 latest priceLow 索引
            public int LastBearPivotIndex { get; set; } = -1;       // 当前已记录的顶背离 latest priceHigh 索引
            public int EntryBullPivotIndex { get; set; } = -1;      // 多头入场时使用的背离 pivot 索引
            public int EntryBearPivotIndex { get; set; } = -1;      // 空头入场时使用的背离 pivot 索引
            public int BlockedBullPivotIndex { get; set; } = -1;    // 多头止损后封锁的 pivot 索引
            public int BlockedBearPivotIndex { get; set; } = -1;    // 空头止损后封锁的 pivot 索引

            // 零轴交叉状态
            public int ZeroCrossDirection { get; set; }         // 零轴交叉方向：1上穿 -1下穿 0无
            public int ZeroCrossConfirmCount { get; set; }      // 零轴交叉确认计数

            // 前值记录
            public decimal? PrevMacd { get; set; }
            public decimal? PrevSignal { get; set; }
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        /// <summary>
        /// 极值点信息
        /// </summary>
        private class ExtremePoint
        {
            public int Index { get; set; }      // K线索引
            public decimal PriceValue { get; set; }  // 价格值（高点或低点）
            public decimal MacdValue { get; set; }   // 对应的MACD值
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 获取参数
            int fastPeriod = Convert.ToInt32(ArgDic["fastPeriod"]);
            int slowPeriod = Convert.ToInt32(ArgDic["slowPeriod"]);
            int signalPeriod = Convert.ToInt32(ArgDic["signalPeriod"]);
            int lookbackPeriod = Convert.ToInt32(ArgDic["lookbackPeriod"]);
            int minDivergenceBars = Convert.ToInt32(ArgDic["minDivergenceBars"]);
            int maxDivergenceBars = Convert.ToInt32(ArgDic["maxDivergenceBars"]);
            int signalMode = Convert.ToInt32(ArgDic["signalMode"]);
            int zeroCrossConfirmBars = Convert.ToInt32(ArgDic["zeroCrossConfirmBars"]);
            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
            double atrStopMultiplier = Convert.ToDouble(ArgDic["atrStopMultiplier"]);
            double atrProfitMultiplier = Convert.ToDouble(ArgDic["atrProfitMultiplier"]);
            int useAtrStop = Convert.ToInt32(ArgDic["useAtrStop"]);
            double stopLossPercent = Convert.ToDouble(ArgDic["stopLossPercent"]);
            double takeProfitPercent = Convert.ToDouble(ArgDic["takeProfitPercent"]);
            int useTrailingStop = Convert.ToInt32(ArgDic["useTrailingStop"]);
            double trailingActivation = Convert.ToDouble(ArgDic["trailingActivation"]);
            double trailingDistance = Convert.ToDouble(ArgDic["trailingDistance"]);
            int mode = ArgDic.ContainsKey("mode") ? Convert.ToInt32(ArgDic["mode"]) : (ArgDic.ContainsKey("tradeDirection") ? Convert.ToInt32(ArgDic["tradeDirection"]) : 0);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            // 最小数据要求
            int minDataCount = Math.Max(slowPeriod + signalPeriod, atrPeriod) + maxDivergenceBars + 10;
            if (tu.QuoteList.Count < minDataCount) return;

            var q = tu.QuoteList.Last();

            // 计算MACD
            var macdList = tu.QuoteList.GetMacd(fastPeriod, slowPeriod, signalPeriod).ToList();
            var currMacd = macdList[macdList.Count - 1];
            var prevMacdResult = macdList[macdList.Count - 2];

            // 计算ATR
            var atrList = tu.QuoteList.GetAtr(atrPeriod).ToList();
            var currAtr = atrList[atrList.Count - 1];

            // 绘制MACD
            if (currMacd.Macd.HasValue)
                Plot("macd", "macd", PlotType.LINE, currMacd.Macd);
            if (currMacd.Signal.HasValue)
                Plot("macd", "signal", PlotType.LINE, currMacd.Signal);
            if (currMacd.Histogram.HasValue)
                Plot("macd", "histogram", PlotType.RECTANGLE, currMacd.Histogram);

            // 数据有效性检查
            if (!currMacd.Macd.HasValue || !currMacd.Signal.HasValue || !currAtr.Atr.HasValue) return;
            if (!prevMacdResult.Macd.HasValue) return;

            decimal currentMacd = (decimal)currMacd.Macd.Value;
            decimal currentSignal = (decimal)currMacd.Signal.Value;
            decimal prevMacd = (decimal)prevMacdResult.Macd.Value;
            decimal atrValue = (decimal)currAtr.Atr.Value;

            // 获取或创建状态
            var sk = tu.GetStateKey();
            if (!_stateDic.TryGetValue(sk, out State? s) || s == null)
            {
                s = new State();
                _stateDic[sk] = s;
            }

            // 计算手数
            var num = CalculateLots(tu, q);

            // ==================== 信号检测 ====================

            // 1. 检测背离信号
            bool bullDivergence = false;
            bool bearDivergence = false;

            if (signalMode != 2) // 非仅零轴模式时检测背离
            {
                var (bullDiv, bearDiv, bullPivotIdx, bearPivotIdx) = DetectDivergence(tu.QuoteList, macdList, lookbackPeriod, minDivergenceBars, maxDivergenceBars);

                // 止损封锁：背离 latest pivot 必须严格新于已封锁的 pivot 索引
                if (bullDiv && bullPivotIdx <= s.BlockedBullPivotIndex) bullDiv = false;
                if (bearDiv && bearPivotIdx <= s.BlockedBearPivotIndex) bearDiv = false;

                bullDivergence = bullDiv;
                bearDivergence = bearDiv;

                // 记录背离状态：仅在 pivot 索引发生变化（出现新极值组合）时刷新
                if (bullDivergence && bullPivotIdx != s.LastBullPivotIndex)
                {
                    s.BullDivergenceDetected = true;
                    s.LastBullPivotIndex = bullPivotIdx;
                    s.DivergenceBar = tu.QuoteList.Count - 1;
                }
                if (bearDivergence && bearPivotIdx != s.LastBearPivotIndex)
                {
                    s.BearDivergenceDetected = true;
                    s.LastBearPivotIndex = bearPivotIdx;
                    s.DivergenceBar = tu.QuoteList.Count - 1;
                }

                // 背离信号有效期（超过一定K线数后失效）
                if (s.BullDivergenceDetected && tu.QuoteList.Count - s.DivergenceBar > lookbackPeriod)
                    s.BullDivergenceDetected = false;
                if (s.BearDivergenceDetected && tu.QuoteList.Count - s.DivergenceBar > lookbackPeriod)
                    s.BearDivergenceDetected = false;
            }

            // 2. 检测零轴交叉信号
            bool zeroCrossUp = false;
            bool zeroCrossDown = false;

            if (signalMode != 1) // 非仅背离模式时检测零轴交叉
            {
                // 检测零轴交叉
                if (s.PrevMacd.HasValue)
                {
                    if (s.PrevMacd.Value <= 0 && currentMacd > 0)
                    {
                        // MACD上穿零轴
                        s.ZeroCrossDirection = 1;
                        s.ZeroCrossConfirmCount = 1;
                    }
                    else if (s.PrevMacd.Value >= 0 && currentMacd < 0)
                    {
                        // MACD下穿零轴
                        s.ZeroCrossDirection = -1;
                        s.ZeroCrossConfirmCount = 1;
                    }
                    else if (s.ZeroCrossDirection != 0)
                    {
                        // 确认计数
                        if ((s.ZeroCrossDirection == 1 && currentMacd > 0) ||
                            (s.ZeroCrossDirection == -1 && currentMacd < 0))
                        {
                            s.ZeroCrossConfirmCount++;
                        }
                        else
                        {
                            // 交叉失效
                            s.ZeroCrossDirection = 0;
                            s.ZeroCrossConfirmCount = 0;
                        }
                    }
                }

                // 确认零轴交叉
                if (s.ZeroCrossDirection == 1 && s.ZeroCrossConfirmCount >= zeroCrossConfirmBars)
                {
                    zeroCrossUp = true;
                    s.ZeroCrossDirection = 0;
                    s.ZeroCrossConfirmCount = 0;
                }
                else if (s.ZeroCrossDirection == -1 && s.ZeroCrossConfirmCount >= zeroCrossConfirmBars)
                {
                    zeroCrossDown = true;
                    s.ZeroCrossDirection = 0;
                    s.ZeroCrossConfirmCount = 0;
                }
            }

            // 更新前值
            s.PrevMacd = currentMacd;
            s.PrevSignal = currentSignal;

            // 3. 组合信号判断
            bool longSignal = false;
            bool shortSignal = false;

            switch (signalMode)
            {
                case 0: // 背离+零轴双确认
                    longSignal = s.BullDivergenceDetected && zeroCrossUp;
                    shortSignal = s.BearDivergenceDetected && zeroCrossDown;
                    break;
                case 1: // 仅背离
                    longSignal = bullDivergence;
                    shortSignal = bearDivergence;
                    break;
                case 2: // 仅零轴交叉
                    longSignal = zeroCrossUp;
                    shortSignal = zeroCrossDown;
                    break;
                case 3: // 任一触发
                    longSignal = bullDivergence || s.BullDivergenceDetected || zeroCrossUp;
                    shortSignal = bearDivergence || s.BearDivergenceDetected || zeroCrossDown;
                    break;
            }

            // 应用交易方向过滤
            if (mode == 1) shortSignal = false;  // 仅做多
            if (mode == 2) longSignal = false;   // 仅做空

            // ==================== 持仓管理 ====================

            if (s.Position != 0)
            {
                // 更新最高/最低价
                if (s.Position == 1)
                {
                    s.HighestSinceEntry = Math.Max(s.HighestSinceEntry, q.High);
                }
                else
                {
                    s.LowestSinceEntry = Math.Min(s.LowestSinceEntry, q.Low);
                }

                // 移动止损逻辑
                if (useTrailingStop == 1 && !s.TrailingActivated)
                {
                    decimal activationDistance = s.EntryAtr * (decimal)trailingActivation;
                    if (s.Position == 1 && q.Close - s.EntryPrice >= activationDistance)
                    {
                        s.TrailingActivated = true;
                    }
                    else if (s.Position == -1 && s.EntryPrice - q.Close >= activationDistance)
                    {
                        s.TrailingActivated = true;
                    }
                }

                // 更新移动止损价格
                if (s.TrailingActivated)
                {
                    decimal trailDistance = s.EntryAtr * (decimal)trailingDistance;
                    if (s.Position == 1)
                    {
                        decimal newStop = s.HighestSinceEntry - trailDistance;
                        s.StopLoss = Math.Max(s.StopLoss, newStop);
                    }
                    else
                    {
                        decimal newStop = s.LowestSinceEntry + trailDistance;
                        s.StopLoss = Math.Min(s.StopLoss, newStop);
                    }
                }

                // 绘制止损止盈线
                Plot("main", "stopLoss", PlotType.LINE, (double)s.StopLoss);
                Plot("main", "takeProfit", PlotType.LINE, (double)s.TakeProfit);

                // 检查止损止盈
                bool stopLossHit = false;
                bool takeProfitHit = false;

                if (s.Position == 1)
                {
                    stopLossHit = q.Close <= s.StopLoss;
                    takeProfitHit = q.Close >= s.TakeProfit;
                }
                else
                {
                    stopLossHit = q.Close >= s.StopLoss;
                    takeProfitHit = q.Close <= s.TakeProfit;
                }

                // 反向信号出场
                bool reverseSignal = (s.Position == 1 && shortSignal) || (s.Position == -1 && longSignal);

                if (stopLossHit || takeProfitHit || reverseSignal)
                {
                    // 平仓
                    if (s.Position == 1)
                    {
                        Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
                    }
                    else
                    {
                        Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
                    }

                    // 出场后封锁“截止当前 K 线已存在的全部 pivot”，强制等待出场后形成的全新 pivot 才可再入场。
                    // 避免同一背离/相邻次新 pivot 在止损后立即再次触发开仓（signalMode=1/3 下 longSignal 直接使用本地 bullDivergence）。
                    int blockUpTo = tu.QuoteList.Count - 1;
                    if (s.Position == 1)
                    {
                        s.BullDivergenceDetected = false;
                        s.BlockedBullPivotIndex = Math.Max(s.BlockedBullPivotIndex, blockUpTo);
                    }
                    if (s.Position == -1)
                    {
                        s.BearDivergenceDetected = false;
                        s.BlockedBearPivotIndex = Math.Max(s.BlockedBearPivotIndex, blockUpTo);
                    }

                    // 如果是反向信号，开反向仓位
                    if (reverseSignal && !stopLossHit)
                    {
                        int newDir = shortSignal ? -1 : 1;
                        OpenPosition(s, tu, q, newDir, num, atrValue,
                            useAtrStop, atrStopMultiplier, atrProfitMultiplier,
                            stopLossPercent, takeProfitPercent, period, sendMode);
                        if (newDir == 1)
                        {
                            s.EntryBullPivotIndex = s.LastBullPivotIndex;
                            s.BullDivergenceDetected = false;
                        }
                        else
                        {
                            s.EntryBearPivotIndex = s.LastBearPivotIndex;
                            s.BearDivergenceDetected = false;
                        }
                    }
                    else
                    {
                        ResetState(s);
                    }
                    return;
                }
            }
            else
            {
                // 空仓状态：检查入场信号
                if (longSignal)
                {
                    OpenPosition(s, tu, q, 1, num, atrValue,
                        useAtrStop, atrStopMultiplier, atrProfitMultiplier,
                        stopLossPercent, takeProfitPercent, period, sendMode);
                    s.EntryBullPivotIndex = s.LastBullPivotIndex; // 记录入场所基于的 pivot 索引
                    s.BullDivergenceDetected = false;
                }
                else if (shortSignal)
                {
                    OpenPosition(s, tu, q, -1, num, atrValue,
                        useAtrStop, atrStopMultiplier, atrProfitMultiplier,
                        stopLossPercent, takeProfitPercent, period, sendMode);
                    s.EntryBearPivotIndex = s.LastBearPivotIndex;
                    s.BearDivergenceDetected = false;
                }
            }
        }

        /// <summary>
        /// 检测MACD背离；返回 latestPriceLow.Index / latestPriceHigh.Index 作为稳定信号ID
        /// </summary>
        private (bool bullDivergence, bool bearDivergence, int bullPivotIdx, int bearPivotIdx) DetectDivergence(
            List<SkQuote> quotes, List<MacdResult> macdList,
            int lookbackPeriod, int minBars, int maxBars)
        {
            bool bullDiv = false;
            bool bearDiv = false;
            int bullPivotIdx = -1;
            int bearPivotIdx = -1;

            int count = quotes.Count;
            if (count < maxBars + 5) return (false, false, -1, -1);

            // 寻找价格低点和MACD低点（用于底背离检测）
            var priceLows = FindPriceExtremes(quotes, false, lookbackPeriod, maxBars);
            var macdLows = FindMacdExtremes(macdList, false, lookbackPeriod, maxBars);

            // 寻找价格高点和MACD高点（用于顶背离检测）
            var priceHighs = FindPriceExtremes(quotes, true, lookbackPeriod, maxBars);
            var macdHighs = FindMacdExtremes(macdList, true, lookbackPeriod, maxBars);

            // 底背离检测：价格创新低，MACD未创新低
            if (priceLows.Count >= 2 && macdLows.Count >= 2)
            {
                var latestPriceLow = priceLows[priceLows.Count - 1];
                var prevPriceLow = priceLows[priceLows.Count - 2];

                // 确保两个低点之间的距离在有效范围内
                int barsBetween = latestPriceLow.Index - prevPriceLow.Index;
                if (barsBetween >= minBars && barsBetween <= maxBars)
                {
                    // 找到对应时间范围内的MACD低点
                    var latestMacdLow = macdLows.LastOrDefault(m => Math.Abs(m.Index - latestPriceLow.Index) <= 3);
                    var prevMacdLow = macdLows.LastOrDefault(m => Math.Abs(m.Index - prevPriceLow.Index) <= 3);

                    if (latestMacdLow != null && prevMacdLow != null)
                    {
                        // 价格创新低，但MACD未创新低（底背离）
                        if (latestPriceLow.PriceValue < prevPriceLow.PriceValue &&
                            latestMacdLow.MacdValue > prevMacdLow.MacdValue)
                        {
                            bullDiv = true;
                            bullPivotIdx = latestPriceLow.Index;
                        }
                    }
                }
            }

            // 顶背离检测：价格创新高，MACD未创新高
            if (priceHighs.Count >= 2 && macdHighs.Count >= 2)
            {
                var latestPriceHigh = priceHighs[priceHighs.Count - 1];
                var prevPriceHigh = priceHighs[priceHighs.Count - 2];

                int barsBetween = latestPriceHigh.Index - prevPriceHigh.Index;
                if (barsBetween >= minBars && barsBetween <= maxBars)
                {
                    var latestMacdHigh = macdHighs.LastOrDefault(m => Math.Abs(m.Index - latestPriceHigh.Index) <= 3);
                    var prevMacdHigh = macdHighs.LastOrDefault(m => Math.Abs(m.Index - prevPriceHigh.Index) <= 3);

                    if (latestMacdHigh != null && prevMacdHigh != null)
                    {
                        // 价格创新高，但MACD未创新高（顶背离）
                        if (latestPriceHigh.PriceValue > prevPriceHigh.PriceValue &&
                            latestMacdHigh.MacdValue < prevMacdHigh.MacdValue)
                        {
                            bearDiv = true;
                            bearPivotIdx = latestPriceHigh.Index;
                        }
                    }
                }
            }

            return (bullDiv, bearDiv, bullPivotIdx, bearPivotIdx);
        }

        /// <summary>
        /// 寻找价格极值点
        /// </summary>
        private List<ExtremePoint> FindPriceExtremes(List<SkQuote> quotes, bool findHighs, int lookbackPeriod, int maxBars)
        {
            var extremes = new List<ExtremePoint>();
            int count = quotes.Count;
            int startIndex = Math.Max(2, count - maxBars - 5);

            for (int i = startIndex; i < count - 1; i++)
            {
                bool isExtreme = false;

                if (findHighs)
                {
                    // 检测局部高点
                    if (quotes[i].High > quotes[i - 1].High && quotes[i].High > quotes[i + 1].High)
                    {
                        isExtreme = true;
                        // 验证是否为lookbackPeriod内的最高点
                        for (int j = Math.Max(0, i - lookbackPeriod); j < i; j++)
                        {
                            if (quotes[j].High > quotes[i].High)
                            {
                                isExtreme = false;
                                break;
                            }
                        }
                    }
                }
                else
                {
                    // 检测局部低点
                    if (quotes[i].Low < quotes[i - 1].Low && quotes[i].Low < quotes[i + 1].Low)
                    {
                        isExtreme = true;
                        // 验证是否为lookbackPeriod内的最低点
                        for (int j = Math.Max(0, i - lookbackPeriod); j < i; j++)
                        {
                            if (quotes[j].Low < quotes[i].Low)
                            {
                                isExtreme = false;
                                break;
                            }
                        }
                    }
                }

                if (isExtreme)
                {
                    extremes.Add(new ExtremePoint
                    {
                        Index = i,
                        PriceValue = findHighs ? quotes[i].High : quotes[i].Low
                    });
                }
            }

            return extremes;
        }

        /// <summary>
        /// 寻找MACD极值点
        /// </summary>
        private List<ExtremePoint> FindMacdExtremes(List<MacdResult> macdList, bool findHighs, int lookbackPeriod, int maxBars)
        {
            var extremes = new List<ExtremePoint>();
            int count = macdList.Count;
            int startIndex = Math.Max(2, count - maxBars - 5);

            for (int i = startIndex; i < count - 1; i++)
            {
                if (!macdList[i].Macd.HasValue || !macdList[i - 1].Macd.HasValue || !macdList[i + 1].Macd.HasValue)
                    continue;

                double curr = macdList[i].Macd.Value;
                double prev = macdList[i - 1].Macd.Value;
                double next = macdList[i + 1].Macd.Value;

                bool isExtreme = false;

                if (findHighs)
                {
                    if (curr > prev && curr > next)
                    {
                        isExtreme = true;
                        for (int j = Math.Max(0, i - lookbackPeriod); j < i; j++)
                        {
                            if (macdList[j].Macd.HasValue && macdList[j].Macd.Value > curr)
                            {
                                isExtreme = false;
                                break;
                            }
                        }
                    }
                }
                else
                {
                    if (curr < prev && curr < next)
                    {
                        isExtreme = true;
                        for (int j = Math.Max(0, i - lookbackPeriod); j < i; j++)
                        {
                            if (macdList[j].Macd.HasValue && macdList[j].Macd.Value < curr)
                            {
                                isExtreme = false;
                                break;
                            }
                        }
                    }
                }

                if (isExtreme)
                {
                    extremes.Add(new ExtremePoint
                    {
                        Index = i,
                        MacdValue = (decimal)curr
                    });
                }
            }

            return extremes;
        }

        /// <summary>
        /// 开仓
        /// </summary>
        private void OpenPosition(State s, TableUnit tu, SkQuote q, int direction, decimal num,
            decimal atrValue, int useAtrStop, double atrStopMultiplier, double atrProfitMultiplier,
            double stopLossPercent, double takeProfitPercent, Period period, int sendMode)
        {
            s.Position = direction;
            s.Num = num;
            s.EntryPrice = q.Close;
            s.EntryAtr = atrValue;
            s.HighestSinceEntry = q.High;
            s.LowestSinceEntry = q.Low;
            s.TrailingActivated = false;

            // 计算止损止盈
            if (useAtrStop == 1)
            {
                if (direction == 1)
                {
                    s.StopLoss = q.Close - atrValue * (decimal)atrStopMultiplier;
                    s.TakeProfit = q.Close + atrValue * (decimal)atrProfitMultiplier;
                }
                else
                {
                    s.StopLoss = q.Close + atrValue * (decimal)atrStopMultiplier;
                    s.TakeProfit = q.Close - atrValue * (decimal)atrProfitMultiplier;
                }
            }
            else
            {
                if (direction == 1)
                {
                    s.StopLoss = q.Close * (1 - (decimal)stopLossPercent / 100);
                    s.TakeProfit = q.Close * (1 + (decimal)takeProfitPercent / 100);
                }
                else
                {
                    s.StopLoss = q.Close * (1 + (decimal)stopLossPercent / 100);
                    s.TakeProfit = q.Close * (1 - (decimal)takeProfitPercent / 100);
                }
            }

            // 下单
            if (direction == 1)
            {
                Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
            }
            else
            {
                Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
            }
        }

        /// <summary>
        /// 计算交易手数
        /// </summary>
        private decimal CalculateLots(TableUnit tu, SkQuote q)
        {
            var num = Convert.ToDecimal(ArgDic["lots"]);
            var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);

            if (lotsMode == 1)
            {
                var sym = GetSymbol(tu.MktSymbol);
                num = Convert.ToDecimal(ArgDic["money"]) / (q.Close * sym.multiplier * sym.margin_ratio);

                if (sym.symbol_type == (int)SymbolType.COIN)
                {
                    num = (int)(num * sym.scale) / (decimal)sym.scale;
                }
                else
                {
                    num = Math.Floor(num);
                }
            }

            return Math.Max(num, 0.001m);
        }

        /// <summary>
        /// 重置状态
        /// </summary>
        private void ResetState(State s)
        {
            s.Position = 0;
            s.Num = 0;
            s.EntryPrice = 0;
            s.StopLoss = 0;
            s.TakeProfit = 0;
            s.EntryAtr = 0;
            s.HighestSinceEntry = 0;
            s.LowestSinceEntry = 0;
            s.TrailingActivated = false;
        }
    }
}
