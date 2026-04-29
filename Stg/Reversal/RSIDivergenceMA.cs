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
    /// RSI背离交易系统（配合均线过滤）
    /// 
    /// 核心理念：
    /// 统一的RSI背离检测逻辑，通过均线系统自动判断市场环境，
    /// 在趋势市中捕捉回调入场机会，在震荡市中捕捉反转机会。
    /// 
    /// 统一背离检测：
    /// - 底背离：价格创新低，但RSI未创新低（动能减弱，看涨信号）
    /// - 顶背离：价格创新高，但RSI未创新高（动能减弱，看跌信号）
    /// 
    /// 均线过滤系统：
    /// - 价格在均线上方 + 快均线在慢均线上方 = 多头环境，只做多
    /// - 价格在均线下方 + 快均线在慢均线下方 = 空头环境，只做空
    /// - 均线斜率确认趋势强度
    /// 
    /// 出场策略：
    /// - RSI超买/超卖出场
    /// - ATR动态止损止盈
    /// - 移动止损保护利润
    /// </summary>
    public class RSIDivergenceMA : StgBase
    {
        public RSIDivergenceMA()
        {
        }

        public RSIDivergenceMA(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // RSI参数
            sd.ArgDic["rsiPeriod"] = 14;                // RSI周期
            sd.ArgDic["rsiOverbought"] = 70;            // RSI超买阈值
            sd.ArgDic["rsiOversold"] = 30;              // RSI超卖阈值

            // 均线参数
            sd.ArgDic["fastMaPeriod"] = 20;             // 快速均线周期
            sd.ArgDic["slowMaPeriod"] = 60;             // 慢速均线周期
            sd.ArgDic["maType"] = 0;                    // 均线类型：0=EMA 1=SMA

            // 背离检测参数
            sd.ArgDic["lookbackPeriod"] = 14;           // 极值点回溯周期
            sd.ArgDic["minDivergenceBars"] = 5;         // 两个极值点之间最小K线数
            sd.ArgDic["maxDivergenceBars"] = 50;        // 两个极值点之间最大K线数
            sd.ArgDic["divergenceValidBars"] = 10;      // 背离信号有效K线数

            // 均线过滤
            sd.ArgDic["requireMaFilter"] = 1;           // 是否需要均线过滤：0=否 1=是

            // ATR止损参数
            sd.ArgDic["atrPeriod"] = 14;                // ATR周期
            sd.ArgDic["atrStopMultiplier"] = 2.0;       // ATR止损倍数
            sd.ArgDic["atrProfitMultiplier"] = 3.0;     // ATR止盈倍数
            sd.ArgDic["useAtrStop"] = 1;                // 是否使用ATR止损止盈

            // 固定止损止盈
            sd.ArgDic["stopLossPercent"] = 5.0;         // 止损百分比
            sd.ArgDic["takeProfitPercent"] = 10.0;      // 止盈百分比

            // 移动止损
            sd.ArgDic["useTrailingStop"] = 1;           // 是否使用移动止损
            sd.ArgDic["trailingActivation"] = 1.5;      // 移动止损激活倍数
            sd.ArgDic["trailingDistance"] = 1.0;        // 移动止损距离（ATR倍数）

            // RSI出场
            sd.ArgDic["useRsiExit"] = 1;                // 是否使用RSI出场
            sd.ArgDic["rsiExitOverbought"] = 75;        // RSI出场超买阈值
            sd.ArgDic["rsiExitOversold"] = 25;          // RSI出场超卖阈值

            // 交易方向
            sd.ArgDic["tradeDirection"] = 0;            // 0:双向 1:仅做多 2:仅做空

            // 发单模式
            sd.ArgDic["sendMode"] = 0;                  // 0:立即 1:下个开盘

            // 手数控制
            sd.ArgDic["lotsMode"] = 1;                  // 0:固定手数 1:固定金额
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;

            // 参数说明
            sd.ArgDescDic["rsiPeriod"] = new ArgDesc() { Text = "RSI周期", Explain = "RSI计算周期，默认14", Type = "number" };
            sd.ArgDescDic["rsiOverbought"] = new ArgDesc() { Text = "RSI超买", Explain = "RSI超买阈值，默认70", Type = "number" };
            sd.ArgDescDic["rsiOversold"] = new ArgDesc() { Text = "RSI超卖", Explain = "RSI超卖阈值，默认30", Type = "number" };
            sd.ArgDescDic["fastMaPeriod"] = new ArgDesc() { Text = "快速均线周期", Explain = "快速均线周期，默认20", Type = "number" };
            sd.ArgDescDic["slowMaPeriod"] = new ArgDesc() { Text = "慢速均线周期", Explain = "慢速均线周期，默认60", Type = "number" };
            sd.ArgDescDic["maType"] = new ArgDesc() { Text = "均线类型", Explain = "均线算法选择", Options = "0:EMA|1:SMA", Type = "select" };
            sd.ArgDescDic["lookbackPeriod"] = new ArgDesc() { Text = "极值回溯周期", Explain = "寻找价格和RSI极值点的回溯周期", Type = "number" };
            sd.ArgDescDic["minDivergenceBars"] = new ArgDesc() { Text = "最小背离间隔", Explain = "两个极值点之间最少K线数", Type = "number" };
            sd.ArgDescDic["maxDivergenceBars"] = new ArgDesc() { Text = "最大背离间隔", Explain = "两个极值点之间最多K线数", Type = "number" };
            sd.ArgDescDic["divergenceValidBars"] = new ArgDesc() { Text = "背离有效期", Explain = "背离信号有效的K线数", Type = "number" };
            sd.ArgDescDic["requireMaFilter"] = new ArgDesc() { Text = "均线过滤", Explain = "需要均线方向过滤", Options = "0:不需要|1:需要均线方向过滤", Type = "bool" };
            sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "ATR计算周期", Type = "number" };
            sd.ArgDescDic["atrStopMultiplier"] = new ArgDesc() { Text = "ATR止损倍数", Explain = "止损距离=ATR*此倍数", Type = "number" };
            sd.ArgDescDic["atrProfitMultiplier"] = new ArgDesc() { Text = "ATR止盈倍数", Explain = "止盈距离=ATR*此倍数", Type = "number" };
            sd.ArgDescDic["useAtrStop"] = new ArgDesc() { Text = "ATR止损", Explain = "使用ATR动态止损", Options = "0:使用固定百分比|1:使用ATR动态止损", Type = "select" };
            sd.ArgDescDic["stopLossPercent"] = new ArgDesc() { Text = "止损百分比", Explain = "固定止损百分比", Type = "number" };
            sd.ArgDescDic["takeProfitPercent"] = new ArgDesc() { Text = "止盈百分比", Explain = "固定止盈百分比", Type = "number" };
            sd.ArgDescDic["useTrailingStop"] = new ArgDesc() { Text = "移动止损", Explain = "跟踪最高/低点调整止损", Options = "0:不使用|1:使用移动止损", Type = "bool" };
            sd.ArgDescDic["trailingActivation"] = new ArgDesc() { Text = "移动止损激活", Explain = "盈利达到ATR*此值时激活移动止损", Type = "number" };
            sd.ArgDescDic["trailingDistance"] = new ArgDesc() { Text = "移动止损距离", Explain = "移动止损与最高/低点的距离（ATR倍数）", Type = "number" };
            sd.ArgDescDic["useRsiExit"] = new ArgDesc() { Text = "RSI出场", Explain = "RSI超买/超卖时止盈", Options = "0:不使用|1:使用RSI超买超卖出场", Type = "bool" };
            sd.ArgDescDic["rsiExitOverbought"] = new ArgDesc() { Text = "RSI出场超买", Explain = "多头持仓RSI达到此值出场", Type = "number" };
            sd.ArgDescDic["rsiExitOversold"] = new ArgDesc() { Text = "RSI出场超卖", Explain = "空头持仓RSI达到此值出场", Type = "number" };
            sd.ArgDescDic["tradeDirection"] = new ArgDesc() { Text = "交易方向", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };

            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };

            sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 1;

            // 颜色配置
            sd.ColorDic["rsi-rsi"] = "#9C27B0";
            sd.ColorDic["rsi-overbought"] = "#F6465D";
            sd.ColorDic["rsi-oversold"] = "#0ECB81";
            sd.ColorDic["main-fastMa"] = "#2196F3";
            sd.ColorDic["main-slowMa"] = "#FF9800";
            sd.ColorDic["main-stopLoss"] = "#E74C3C";
            sd.ColorDic["main-takeProfit"] = "#27AE60";

            sd.MidValDic["rsi"] = 50;

            return sd;
        }

        private class State
        {
            public int Position { get; set; }
            public decimal Num { get; set; }
            public decimal EntryPrice { get; set; }
            public decimal StopLoss { get; set; }
            public decimal TakeProfit { get; set; }
            public decimal EntryAtr { get; set; }
            public decimal HighestSinceEntry { get; set; }
            public decimal LowestSinceEntry { get; set; }
            public bool TrailingActivated { get; set; }

            public bool BullDivergenceDetected { get; set; }
            public bool BearDivergenceDetected { get; set; }
            public int DivergenceBar { get; set; }
            // 用 latestPriceLow.Index / latestPriceHigh.Index 作为稳定的背离信号ID
            public int LastBullPivotIndex { get; set; } = -1;       // 当前已记录的底背离 latest priceLow 索引
            public int LastBearPivotIndex { get; set; } = -1;       // 当前已记录的顶背离 latest priceHigh 索引
            public int EntryBullPivotIndex { get; set; } = -1;      // 多头入场时使用的背离 pivot 索引
            public int EntryBearPivotIndex { get; set; } = -1;      // 空头入场时使用的背离 pivot 索引
            public int BlockedBullPivotIndex { get; set; } = -1;    // 多头止损后封锁的 pivot 索引（含此索引及更早）
            public int BlockedBearPivotIndex { get; set; } = -1;    // 空头止损后封锁的 pivot 索引（含此索引及更早）

            public decimal? PrevFastMa { get; set; }
            public decimal? PrevSlowMa { get; set; }
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        private class ExtremePoint
        {
            public int Index { get; set; }
            public decimal PriceValue { get; set; }
            public decimal RsiValue { get; set; }
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 获取参数
            int rsiPeriod = Convert.ToInt32(ArgDic["rsiPeriod"]);
            int rsiOverbought = Convert.ToInt32(ArgDic["rsiOverbought"]);
            int rsiOversold = Convert.ToInt32(ArgDic["rsiOversold"]);
            int fastMaPeriod = Convert.ToInt32(ArgDic["fastMaPeriod"]);
            int slowMaPeriod = Convert.ToInt32(ArgDic["slowMaPeriod"]);
            int maType = Convert.ToInt32(ArgDic["maType"]);
            int lookbackPeriod = Convert.ToInt32(ArgDic["lookbackPeriod"]);
            int minDivergenceBars = Convert.ToInt32(ArgDic["minDivergenceBars"]);
            int maxDivergenceBars = Convert.ToInt32(ArgDic["maxDivergenceBars"]);
            int divergenceValidBars = Convert.ToInt32(ArgDic["divergenceValidBars"]);
            int requireMaFilter = Convert.ToInt32(ArgDic["requireMaFilter"]);
            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
            double atrStopMultiplier = Convert.ToDouble(ArgDic["atrStopMultiplier"]);
            double atrProfitMultiplier = Convert.ToDouble(ArgDic["atrProfitMultiplier"]);
            int useAtrStop = Convert.ToInt32(ArgDic["useAtrStop"]);
            double stopLossPercent = Convert.ToDouble(ArgDic["stopLossPercent"]);
            double takeProfitPercent = Convert.ToDouble(ArgDic["takeProfitPercent"]);
            int useTrailingStop = Convert.ToInt32(ArgDic["useTrailingStop"]);
            double trailingActivation = Convert.ToDouble(ArgDic["trailingActivation"]);
            double trailingDistance = Convert.ToDouble(ArgDic["trailingDistance"]);
            int useRsiExit = Convert.ToInt32(ArgDic["useRsiExit"]);
            int rsiExitOverbought = Convert.ToInt32(ArgDic["rsiExitOverbought"]);
            int rsiExitOversold = Convert.ToInt32(ArgDic["rsiExitOversold"]);
            int tradeDirection = Convert.ToInt32(ArgDic["tradeDirection"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            // 最小数据要求
            int minDataCount = Math.Max(slowMaPeriod, Math.Max(rsiPeriod, atrPeriod)) + maxDivergenceBars + 10;
            if (tu.QuoteList.Count < minDataCount) return;

            var q = tu.QuoteList.Last();

            // 计算RSI
            var rsiList = tu.QuoteList.GetRsi(rsiPeriod).ToList();
            var currRsi = rsiList[rsiList.Count - 1];

            // 计算均线
            decimal currentFastMa = 0, currentSlowMa = 0;
            decimal prevFastMa = 0, prevSlowMa = 0;

            if (maType == 0)
            {
                var fastEmaList = tu.QuoteList.GetEma(fastMaPeriod).ToList();
                var slowEmaList = tu.QuoteList.GetEma(slowMaPeriod).ToList();
                if (fastEmaList[fastEmaList.Count - 1].Ema.HasValue)
                    currentFastMa = (decimal)fastEmaList[fastEmaList.Count - 1].Ema.Value;
                if (slowEmaList[slowEmaList.Count - 1].Ema.HasValue)
                    currentSlowMa = (decimal)slowEmaList[slowEmaList.Count - 1].Ema.Value;
                if (fastEmaList[fastEmaList.Count - 2].Ema.HasValue)
                    prevFastMa = (decimal)fastEmaList[fastEmaList.Count - 2].Ema.Value;
                if (slowEmaList[slowEmaList.Count - 2].Ema.HasValue)
                    prevSlowMa = (decimal)slowEmaList[slowEmaList.Count - 2].Ema.Value;
            }
            else
            {
                var fastSmaList = tu.QuoteList.GetSma(fastMaPeriod).ToList();
                var slowSmaList = tu.QuoteList.GetSma(slowMaPeriod).ToList();
                if (fastSmaList[fastSmaList.Count - 1].Sma.HasValue)
                    currentFastMa = (decimal)fastSmaList[fastSmaList.Count - 1].Sma.Value;
                if (slowSmaList[slowSmaList.Count - 1].Sma.HasValue)
                    currentSlowMa = (decimal)slowSmaList[slowSmaList.Count - 1].Sma.Value;
                if (fastSmaList[fastSmaList.Count - 2].Sma.HasValue)
                    prevFastMa = (decimal)fastSmaList[fastSmaList.Count - 2].Sma.Value;
                if (slowSmaList[slowSmaList.Count - 2].Sma.HasValue)
                    prevSlowMa = (decimal)slowSmaList[slowSmaList.Count - 2].Sma.Value;
            }

            // 计算ATR
            var atrList = tu.QuoteList.GetAtr(atrPeriod).ToList();
            var currAtr = atrList[atrList.Count - 1];

            // 绘制指标
            if (currRsi.Rsi.HasValue)
                Plot("rsi", "rsi", PlotType.LINE, currRsi.Rsi);
            Plot("rsi", "overbought", PlotType.LINE, rsiOverbought);
            Plot("rsi", "oversold", PlotType.LINE, rsiOversold);
            if (currentFastMa > 0)
                Plot("main", "fastMa", PlotType.LINE, (double)currentFastMa);
            if (currentSlowMa > 0)
                Plot("main", "slowMa", PlotType.LINE, (double)currentSlowMa);

            // 数据有效性检查
            if (!currRsi.Rsi.HasValue || !currAtr.Atr.HasValue) return;
            if (currentFastMa == 0 || currentSlowMa == 0) return;

            decimal currentRsi = (decimal)currRsi.Rsi.Value;
            decimal atrValue = (decimal)currAtr.Atr.Value;

            // 获取或创建状态
            var sk = tu.GetStateKey();
            if (!_stateDic.TryGetValue(sk, out State? s) || s == null)
            {
                s = new State();
                _stateDic[sk] = s;
            }

            var num = CalculateLots(tu, q);

            // ==================== 均线过滤条件 ====================
            bool maFilterLong = true;
            bool maFilterShort = true;

            if (requireMaFilter == 1)
            {
                // 多头环境：价格在均线上方 + 快均线在慢均线上方
                maFilterLong = q.Close > currentFastMa && q.Close > currentSlowMa && currentFastMa > currentSlowMa;
                // 空头环境：价格在均线下方 + 快均线在慢均线下方
                maFilterShort = q.Close < currentFastMa && q.Close < currentSlowMa && currentFastMa < currentSlowMa;
            }

            // ==================== 统一背离检测 ====================
            var (bullDiv, bearDiv, bullPivotIdx, bearPivotIdx) = DetectDivergence(tu.QuoteList, rsiList, lookbackPeriod, minDivergenceBars, maxDivergenceBars);

            // 止损封锁：背离 latest pivot 必须严格新于已封锁的 pivot 索引
            if (bullDiv && bullPivotIdx <= s.BlockedBullPivotIndex) bullDiv = false;
            if (bearDiv && bearPivotIdx <= s.BlockedBearPivotIndex) bearDiv = false;

            // 记录背离状态：仅在 pivot 索引发生变化（即出现新极值组合）时刷新
            if (bullDiv && bullPivotIdx != s.LastBullPivotIndex)
            {
                s.BullDivergenceDetected = true;
                s.LastBullPivotIndex = bullPivotIdx;
                s.DivergenceBar = tu.QuoteList.Count - 1;
            }
            if (bearDiv && bearPivotIdx != s.LastBearPivotIndex)
            {
                s.BearDivergenceDetected = true;
                s.LastBearPivotIndex = bearPivotIdx;
                s.DivergenceBar = tu.QuoteList.Count - 1;
            }

            // 背离信号有效期
            if (s.BullDivergenceDetected && tu.QuoteList.Count - s.DivergenceBar > divergenceValidBars)
            {
                s.BullDivergenceDetected = false;
            }
            if (s.BearDivergenceDetected && tu.QuoteList.Count - s.DivergenceBar > divergenceValidBars)
            {
                s.BearDivergenceDetected = false;
            }

            // 更新前值
            s.PrevFastMa = currentFastMa;
            s.PrevSlowMa = currentSlowMa;

            // ==================== 入场信号 ====================
            bool longSignal = s.BullDivergenceDetected && maFilterLong;
            bool shortSignal = s.BearDivergenceDetected && maFilterShort;

            // 交易方向过滤
            if (tradeDirection == 1) shortSignal = false;
            if (tradeDirection == 2) longSignal = false;

            // ==================== 持仓管理 ====================
            if (s.Position != 0)
            {
                // 更新最高/最低价
                if (s.Position == 1)
                    s.HighestSinceEntry = Math.Max(s.HighestSinceEntry, q.High);
                else
                    s.LowestSinceEntry = Math.Min(s.LowestSinceEntry, q.Low);

                // 移动止损
                if (useTrailingStop == 1 && !s.TrailingActivated)
                {
                    decimal activationDistance = s.EntryAtr * (decimal)trailingActivation;
                    if (s.Position == 1 && q.Close - s.EntryPrice >= activationDistance)
                        s.TrailingActivated = true;
                    else if (s.Position == -1 && s.EntryPrice - q.Close >= activationDistance)
                        s.TrailingActivated = true;
                }

                if (s.TrailingActivated)
                {
                    decimal trailDistance = s.EntryAtr * (decimal)trailingDistance;
                    if (s.Position == 1)
                        s.StopLoss = Math.Max(s.StopLoss, s.HighestSinceEntry - trailDistance);
                    else
                        s.StopLoss = Math.Min(s.StopLoss, s.LowestSinceEntry + trailDistance);
                }

                // 绘制止损止盈线
                Plot("main", "stopLoss", PlotType.LINE, (double)s.StopLoss);
                Plot("main", "takeProfit", PlotType.LINE, (double)s.TakeProfit);

                // 检查出场条件
                bool stopLossHit = false;
                bool takeProfitHit = false;
                bool rsiExitHit = false;

                if (s.Position == 1)
                {
                    stopLossHit = q.Close <= s.StopLoss;
                    takeProfitHit = q.Close >= s.TakeProfit;
                    if (useRsiExit == 1)
                        rsiExitHit = currentRsi >= rsiExitOverbought;
                }
                else
                {
                    stopLossHit = q.Close >= s.StopLoss;
                    takeProfitHit = q.Close <= s.TakeProfit;
                    if (useRsiExit == 1)
                        rsiExitHit = currentRsi <= rsiExitOversold;
                }

                bool reverseSignal = (s.Position == 1 && shortSignal) || (s.Position == -1 && longSignal);

                if (stopLossHit || takeProfitHit || rsiExitHit || reverseSignal)
                {
                    // 平仓
                    if (s.Position == 1)
                        Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
                    else
                        Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);

                    // 出场后封锁本次入场所基于的 pivot 索引（含止盈/止损/RSI出场/反向信号），
                    // 必须等到更新的 pivot 形成才可再入场，避免同一背离信号在止盈后立即再次触发开仓。
                    if (s.Position == 1)
                    {
                        s.BullDivergenceDetected = false;
                        if (s.EntryBullPivotIndex >= 0)
                            s.BlockedBullPivotIndex = Math.Max(s.BlockedBullPivotIndex, s.EntryBullPivotIndex);
                    }
                    if (s.Position == -1)
                    {
                        s.BearDivergenceDetected = false;
                        if (s.EntryBearPivotIndex >= 0)
                            s.BlockedBearPivotIndex = Math.Max(s.BlockedBearPivotIndex, s.EntryBearPivotIndex);
                    }

                    // 反向开仓
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
                // 空仓入场
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
        /// 统一的RSI背离检测；返回 latestPriceLow.Index / latestPriceHigh.Index 作为稳定信号ID
        /// </summary>
        private (bool bullDiv, bool bearDiv, int bullPivotIdx, int bearPivotIdx) DetectDivergence(
            List<SkQuote> quotes, List<RsiResult> rsiList,
            int lookbackPeriod, int minBars, int maxBars)
        {
            bool bullDiv = false;
            bool bearDiv = false;
            int bullPivotIdx = -1;
            int bearPivotIdx = -1;

            int count = quotes.Count;
            if (count < maxBars + 5) return (false, false, -1, -1);

            // 寻找价格和RSI的极值点
            var priceLows = FindPriceExtremes(quotes, false, lookbackPeriod, maxBars);
            var priceHighs = FindPriceExtremes(quotes, true, lookbackPeriod, maxBars);
            var rsiLows = FindRsiExtremes(rsiList, false, lookbackPeriod, maxBars);
            var rsiHighs = FindRsiExtremes(rsiList, true, lookbackPeriod, maxBars);

            // 底背离：价格创新低，RSI未创新低
            if (priceLows.Count >= 2 && rsiLows.Count >= 2)
            {
                var latestPriceLow = priceLows[priceLows.Count - 1];
                var prevPriceLow = priceLows[priceLows.Count - 2];

                int barsBetween = latestPriceLow.Index - prevPriceLow.Index;
                if (barsBetween >= minBars && barsBetween <= maxBars)
                {
                    var latestRsiLow = rsiLows.LastOrDefault(r => Math.Abs(r.Index - latestPriceLow.Index) <= 3);
                    var prevRsiLow = rsiLows.LastOrDefault(r => Math.Abs(r.Index - prevPriceLow.Index) <= 3);

                    if (latestRsiLow != null && prevRsiLow != null)
                    {
                        if (latestPriceLow.PriceValue < prevPriceLow.PriceValue &&
                            latestRsiLow.RsiValue > prevRsiLow.RsiValue)
                        {
                            bullDiv = true;
                            bullPivotIdx = latestPriceLow.Index;
                        }
                    }
                }
            }

            // 顶背离：价格创新高，RSI未创新高
            if (priceHighs.Count >= 2 && rsiHighs.Count >= 2)
            {
                var latestPriceHigh = priceHighs[priceHighs.Count - 1];
                var prevPriceHigh = priceHighs[priceHighs.Count - 2];

                int barsBetween = latestPriceHigh.Index - prevPriceHigh.Index;
                if (barsBetween >= minBars && barsBetween <= maxBars)
                {
                    var latestRsiHigh = rsiHighs.LastOrDefault(r => Math.Abs(r.Index - latestPriceHigh.Index) <= 3);
                    var prevRsiHigh = rsiHighs.LastOrDefault(r => Math.Abs(r.Index - prevPriceHigh.Index) <= 3);

                    if (latestRsiHigh != null && prevRsiHigh != null)
                    {
                        if (latestPriceHigh.PriceValue > prevPriceHigh.PriceValue &&
                            latestRsiHigh.RsiValue < prevRsiHigh.RsiValue)
                        {
                            bearDiv = true;
                            bearPivotIdx = latestPriceHigh.Index;
                        }
                    }
                }
            }

            return (bullDiv, bearDiv, bullPivotIdx, bearPivotIdx);
        }

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
                    if (quotes[i].High > quotes[i - 1].High && quotes[i].High > quotes[i + 1].High)
                    {
                        isExtreme = true;
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
                    if (quotes[i].Low < quotes[i - 1].Low && quotes[i].Low < quotes[i + 1].Low)
                    {
                        isExtreme = true;
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

        private List<ExtremePoint> FindRsiExtremes(List<RsiResult> rsiList, bool findHighs, int lookbackPeriod, int maxBars)
        {
            var extremes = new List<ExtremePoint>();
            int count = rsiList.Count;
            int startIndex = Math.Max(2, count - maxBars - 5);

            for (int i = startIndex; i < count - 1; i++)
            {
                if (!rsiList[i].Rsi.HasValue || !rsiList[i - 1].Rsi.HasValue || !rsiList[i + 1].Rsi.HasValue)
                    continue;

                double curr = rsiList[i].Rsi.Value;
                double prev = rsiList[i - 1].Rsi.Value;
                double next = rsiList[i + 1].Rsi.Value;

                bool isExtreme = false;

                if (findHighs)
                {
                    if (curr > prev && curr > next)
                    {
                        isExtreme = true;
                        for (int j = Math.Max(0, i - lookbackPeriod); j < i; j++)
                        {
                            if (rsiList[j].Rsi.HasValue && rsiList[j].Rsi.Value > curr)
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
                            if (rsiList[j].Rsi.HasValue && rsiList[j].Rsi.Value < curr)
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
                        RsiValue = (decimal)curr
                    });
                }
            }

            return extremes;
        }

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

            if (direction == 1)
                Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
            else
                Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
        }

        private decimal CalculateLots(TableUnit tu, SkQuote q)
        {
            var num = Convert.ToDecimal(ArgDic["lots"]);
            var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);

            if (lotsMode == 1)
            {
                var symbol = GetSymbol(tu.MktSymbol);
                num = Convert.ToDecimal(ArgDic["money"]) / (q.Close * symbol.multiplier * symbol.margin_ratio);

                if (symbol.symbol_type == (int)SymbolType.COIN)
                    num = Math.Floor(num * 1000) / 1000m;
                else
                    num = Math.Floor(num);
            }

            return Math.Max(num, 0.001m);
        }

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
