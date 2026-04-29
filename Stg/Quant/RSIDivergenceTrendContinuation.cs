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
    /// RSI背离+趋势延续背离双模式交易系统（配合均线过滤）
    /// 
    /// 核心理念：
    /// 结合RSI背离信号识别潜在反转点，同时利用趋势延续背离捕捉趋势中的回调入场机会，
    /// 通过均线系统过滤交易方向，形成完整的交易系统。
    /// 
    /// 双模式设计：
    /// 
    /// 【模式1：RSI反转背离】
    /// - 底背离做多：价格创新低，但RSI未创新低（超卖区域动能减弱，反转信号）
    /// - 顶背离做空：价格创新高，但RSI未创新高（超买区域动能减弱，反转信号）
    /// - 适用场景：震荡市或趋势末端的反转交易
    /// 
    /// 【模式2：趋势延续背离】
    /// - 多头延续：上升趋势中，价格回调创新低，但RSI未创新低（回调结束信号）
    /// - 空头延续：下降趋势中，价格反弹创新高，但RSI未创新高（反弹结束信号）
    /// - 适用场景：趋势中的回调入场
    /// 
    /// 均线过滤系统：
    /// - 快速均线 + 慢速均线构成趋势判断
    /// - 价格与均线位置关系过滤入场方向
    /// - 均线斜率确认趋势强度
    /// 
    /// 出场策略：
    /// - RSI超买/超卖反向出场
    /// - ATR动态止损止盈
    /// - 移动止损保护利润
    /// - 均线反向交叉出场
    /// </summary>
    public class RSIDivergenceTrendContinuation : StgBase
    {
        public RSIDivergenceTrendContinuation()
        {
        }

        public RSIDivergenceTrendContinuation(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // RSI参数
            sd.ArgDic["rsiPeriod"] = 14;                // RSI周期
            sd.ArgDic["rsiOverbought"] = 70;            // RSI超买阈值
            sd.ArgDic["rsiOversold"] = 30;              // RSI超卖阈值
            sd.ArgDic["rsiExtremeOverbought"] = 80;     // RSI极度超买
            sd.ArgDic["rsiExtremeOversold"] = 20;       // RSI极度超卖

            // 均线参数
            sd.ArgDic["fastMaPeriod"] = 20;             // 快速均线周期
            sd.ArgDic["slowMaPeriod"] = 60;             // 慢速均线周期
            sd.ArgDic["maType"] = 0;                    // 均线类型：0=EMA 1=SMA
            sd.ArgDic["maSlopeThreshold"] = 0.001;      // 均线斜率阈值（判断趋势强度）

            // 背离检测参数
            sd.ArgDic["lookbackPeriod"] = 14;           // 极值点回溯周期
            sd.ArgDic["minDivergenceBars"] = 5;         // 两个极值点之间最小K线数
            sd.ArgDic["maxDivergenceBars"] = 50;        // 两个极值点之间最大K线数
            sd.ArgDic["divergenceValidBars"] = 10;      // 背离信号有效K线数

            // 交易模式
            sd.ArgDic["tradingMode"] = 0;               // 0:双模式 1:仅反转背离 2:仅趋势延续
            sd.ArgDic["requireMaFilter"] = 1;           // 是否需要均线过滤：0=否 1=是
            sd.ArgDic["requireMaAlignment"] = 1;        // 是否需要均线排列确认：0=否 1=是

            // 趋势延续模式参数
            sd.ArgDic["trendPullbackDepth"] = 0.382;    // 回调深度阈值（相对于前一波段）
            sd.ArgDic["minTrendBars"] = 10;             // 最小趋势K线数

            // ATR止损参数
            sd.ArgDic["atrPeriod"] = 14;                // ATR周期
            sd.ArgDic["atrStopMultiplier"] = 2.0;       // ATR止损倍数
            sd.ArgDic["atrProfitMultiplier"] = 3.0;     // ATR止盈倍数
            sd.ArgDic["useAtrStop"] = 1;                // 是否使用ATR止损止盈

            // 固定止损止盈（当不使用ATR时）
            sd.ArgDic["stopLossPercent"] = 2.0;         // 止损百分比
            sd.ArgDic["takeProfitPercent"] = 4.0;       // 止盈百分比

            // 移动止损
            sd.ArgDic["useTrailingStop"] = 1;           // 是否使用移动止损
            sd.ArgDic["trailingActivation"] = 1.5;      // 移动止损激活倍数
            sd.ArgDic["trailingDistance"] = 1.0;        // 移动止损距离（ATR倍数）

            // RSI出场参数
            sd.ArgDic["useRsiExit"] = 1;                // 是否使用RSI出场
            sd.ArgDic["rsiExitOverbought"] = 75;        // RSI出场超买阈值
            sd.ArgDic["rsiExitOversold"] = 25;          // RSI出场超卖阈值

            // 均线出场
            sd.ArgDic["useMaCrossExit"] = 0;            // 是否使用均线交叉出场

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
            sd.ArgDescDic["rsiExtremeOverbought"] = new ArgDesc() { Text = "RSI极度超买", Explain = "RSI极度超买阈值，默认80", Type = "number" };
            sd.ArgDescDic["rsiExtremeOversold"] = new ArgDesc() { Text = "RSI极度超卖", Explain = "RSI极度超卖阈值，默认20", Type = "number" };
            sd.ArgDescDic["fastMaPeriod"] = new ArgDesc() { Text = "快速均线周期", Explain = "快速均线周期，默认20", Type = "number" };
            sd.ArgDescDic["slowMaPeriod"] = new ArgDesc() { Text = "慢速均线周期", Explain = "慢速均线周期，默认60", Type = "number" };
            sd.ArgDescDic["maType"] = new ArgDesc() { Text = "均线类型", Explain = "均线算法选择", Options = "0:EMA|1:SMA", Type = "select" };
            sd.ArgDescDic["maSlopeThreshold"] = new ArgDesc() { Text = "均线斜率阈值", Explain = "判断趋势强度的斜率阈值", Type = "number" };
            sd.ArgDescDic["lookbackPeriod"] = new ArgDesc() { Text = "极值回溯周期", Explain = "寻找价格和RSI极值点的回溯周期", Type = "number" };
            sd.ArgDescDic["minDivergenceBars"] = new ArgDesc() { Text = "最小背离间隔", Explain = "两个极值点之间最少K线数", Type = "number" };
            sd.ArgDescDic["maxDivergenceBars"] = new ArgDesc() { Text = "最大背离间隔", Explain = "两个极值点之间最多K线数", Type = "number" };
            sd.ArgDescDic["divergenceValidBars"] = new ArgDesc() { Text = "背离有效期", Explain = "背离信号有效的K线数", Type = "number" };
            sd.ArgDescDic["tradingMode"] = new ArgDesc() { Text = "交易模式", Explain = "策略交易模式", Options = "0:双模式|1:仅反转背离|2:仅趋势延续", Type = "select" };
            sd.ArgDescDic["requireMaFilter"] = new ArgDesc() { Text = "均线过滤", Explain = "需要均线方向过滤", Options = "0:不需要|1:需要均线方向过滤", Type = "bool" };
            sd.ArgDescDic["requireMaAlignment"] = new ArgDesc() { Text = "均线排列", Explain = "需要均线多空排列确认", Options = "0:不需要|1:需要均线多空排列确认", Type = "bool" };
            sd.ArgDescDic["trendPullbackDepth"] = new ArgDesc() { Text = "回调深度", Explain = "趋势延续模式的回调深度阈值", Type = "number" };
            sd.ArgDescDic["minTrendBars"] = new ArgDesc() { Text = "最小趋势K线", Explain = "判断趋势的最小K线数", Type = "number" };
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
            sd.ArgDescDic["useMaCrossExit"] = new ArgDesc() { Text = "均线交叉出场", Explain = "均线交叉时平仓", Options = "0:不使用|1:使用均线死叉/金叉出场", Type = "bool" };
            sd.ArgDescDic["tradeDirection"] = new ArgDesc() { Text = "交易方向", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };

            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };

            sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 1;

            // 颜色配置
            sd.ColorDic["rsi-rsi"] = "#9C27B0";                 // RSI线紫色
            sd.ColorDic["rsi-overbought"] = "#F6465D";          // 超买线红色
            sd.ColorDic["rsi-oversold"] = "#0ECB81";            // 超卖线绿色
            sd.ColorDic["main-fastMa"] = "#2196F3";                 // 快速均线蓝色
            sd.ColorDic["main-slowMa"] = "#FF9800";                 // 慢速均线橙色
            sd.ColorDic["main-stopLoss"] = "#E74C3C";                // 止损线红色
            sd.ColorDic["main-takeProfit"] = "#27AE60";              // 止盈线绿色

            sd.MidValDic["rsi"] = 50;

            return sd;
        }

        /// <summary>
        /// 持仓状态
        /// </summary>
        private class State
        {
            public int Position { get; set; }               // 0:空仓 1:多头 -1:空头
            public decimal Num { get; set; }                // 持仓数量
            public decimal EntryPrice { get; set; }         // 入场价格
            public decimal StopLoss { get; set; }           // 止损价格
            public decimal TakeProfit { get; set; }         // 止盈价格
            public decimal EntryAtr { get; set; }           // 入场时ATR值
            public decimal HighestSinceEntry { get; set; }  // 入场后最高价
            public decimal LowestSinceEntry { get; set; }   // 入场后最低价
            public bool TrailingActivated { get; set; }     // 移动止损是否已激活
            public int EntryMode { get; set; }              // 入场模式：1=反转背离 2=趋势延续

            // 背离检测状态
            public bool BullDivergenceDetected { get; set; }        // 底背离
            public bool BearDivergenceDetected { get; set; }        // 顶背离
            public int DivergenceBar { get; set; }                  // 背离发生的K线索引
            public int DivergenceType { get; set; }                 // 背离类型：1=反转 2=延续

            // 背离信号ID及止损封锁（以 latestPriceLow.Index / latestPriceHigh.Index 作为稳定信号ID）
            public int LastBullPivotIndex { get; set; } = -1;       // 当前已记录的底背离 latest priceLow 索引
            public int LastBearPivotIndex { get; set; } = -1;       // 当前已记录的顶背离 latest priceHigh 索引
            public int EntryBullPivotIndex { get; set; } = -1;      // 多头入场时使用的背离 pivot 索引
            public int EntryBearPivotIndex { get; set; } = -1;      // 空头入场时使用的背离 pivot 索引
            public int BlockedBullPivotIndex { get; set; } = -1;    // 多头止损后封锁的 pivot 索引
            public int BlockedBearPivotIndex { get; set; } = -1;    // 空头止损后封锁的 pivot 索引

            // 趋势状态
            public int TrendDirection { get; set; }                 // 趋势方向：1=上升 -1=下降 0=震荡
            public decimal TrendHighest { get; set; }               // 趋势中最高价
            public decimal TrendLowest { get; set; }                // 趋势中最低价
            public int TrendBars { get; set; }                      // 趋势持续K线数

            // 前值记录
            public decimal? PrevRsi { get; set; }
            public decimal? PrevFastMa { get; set; }
            public decimal? PrevSlowMa { get; set; }
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        /// <summary>
        /// 极值点信息
        /// </summary>
        private class ExtremePoint
        {
            public int Index { get; set; }              // K线索引
            public decimal PriceValue { get; set; }     // 价格值
            public decimal RsiValue { get; set; }       // 对应的RSI值
        }

        /// <summary>
        /// 背离信号
        /// </summary>
        private class DivergenceSignal
        {
            public bool Detected { get; set; }          // 是否检测到
            public int Type { get; set; }               // 类型：1=反转背离 2=趋势延续背离
            public int Direction { get; set; }          // 方向：1=看涨 -1=看跌
            public decimal Strength { get; set; }       // 背离强度（0-1）
            public int PivotIndex { get; set; } = -1;   // latestPriceLow.Index / latestPriceHigh.Index（稳定信号ID）
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 获取参数
            int rsiPeriod = Convert.ToInt32(ArgDic["rsiPeriod"]);
            int rsiOverbought = Convert.ToInt32(ArgDic["rsiOverbought"]);
            int rsiOversold = Convert.ToInt32(ArgDic["rsiOversold"]);
            int rsiExtremeOverbought = Convert.ToInt32(ArgDic["rsiExtremeOverbought"]);
            int rsiExtremeOversold = Convert.ToInt32(ArgDic["rsiExtremeOversold"]);
            int fastMaPeriod = Convert.ToInt32(ArgDic["fastMaPeriod"]);
            int slowMaPeriod = Convert.ToInt32(ArgDic["slowMaPeriod"]);
            int maType = Convert.ToInt32(ArgDic["maType"]);
            double maSlopeThreshold = Convert.ToDouble(ArgDic["maSlopeThreshold"]);
            int lookbackPeriod = Convert.ToInt32(ArgDic["lookbackPeriod"]);
            int minDivergenceBars = Convert.ToInt32(ArgDic["minDivergenceBars"]);
            int maxDivergenceBars = Convert.ToInt32(ArgDic["maxDivergenceBars"]);
            int divergenceValidBars = Convert.ToInt32(ArgDic["divergenceValidBars"]);
            int tradingMode = Convert.ToInt32(ArgDic["tradingMode"]);
            int requireMaFilter = Convert.ToInt32(ArgDic["requireMaFilter"]);
            int requireMaAlignment = Convert.ToInt32(ArgDic["requireMaAlignment"]);
            double trendPullbackDepth = Convert.ToDouble(ArgDic["trendPullbackDepth"]);
            int minTrendBars = Convert.ToInt32(ArgDic["minTrendBars"]);
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
            int useMaCrossExit = Convert.ToInt32(ArgDic["useMaCrossExit"]);
            int tradeDirection = Convert.ToInt32(ArgDic["tradeDirection"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            // 最小数据要求
            int minDataCount = Math.Max(slowMaPeriod, Math.Max(rsiPeriod, atrPeriod)) + maxDivergenceBars + 10;
            if (tu.QuoteList.Count < minDataCount) return;

            var q = tu.QuoteList.Last();

            // 计算RSI
            var rsiList = tu.QuoteList.GetRsi(rsiPeriod).ToList();
            var currRsi = rsiList[rsiList.Count - 1];
            var prevRsiResult = rsiList[rsiList.Count - 2];

            // 计算均线
            List<EmaResult> fastEmaList = null;
            List<EmaResult> slowEmaList = null;
            List<SmaResult> fastSmaList = null;
            List<SmaResult> slowSmaList = null;

            decimal currentFastMa = 0;
            decimal currentSlowMa = 0;
            decimal prevFastMa = 0;
            decimal prevSlowMa = 0;

            if (maType == 0)
            {
                fastEmaList = tu.QuoteList.GetEma(fastMaPeriod).ToList();
                slowEmaList = tu.QuoteList.GetEma(slowMaPeriod).ToList();
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
                fastSmaList = tu.QuoteList.GetSma(fastMaPeriod).ToList();
                slowSmaList = tu.QuoteList.GetSma(slowMaPeriod).ToList();
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

            // 绘制RSI
            if (currRsi.Rsi.HasValue)
                Plot("rsi", "rsi", PlotType.LINE, currRsi.Rsi);
            Plot("rsi", "overbought", PlotType.LINE, rsiOverbought);
            Plot("rsi", "oversold", PlotType.LINE, rsiOversold);

            // 绘制均线
            if (currentFastMa > 0)
                Plot("main", "fastMa", PlotType.LINE, (double)currentFastMa);
            if (currentSlowMa > 0)
                Plot("main", "slowMa", PlotType.LINE, (double)currentSlowMa);

            // 数据有效性检查
            if (!currRsi.Rsi.HasValue || !currAtr.Atr.HasValue) return;
            if (!prevRsiResult.Rsi.HasValue) return;
            if (currentFastMa == 0 || currentSlowMa == 0) return;

            decimal currentRsi = (decimal)currRsi.Rsi.Value;
            decimal prevRsi = (decimal)prevRsiResult.Rsi.Value;
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

            // ==================== 趋势分析 ====================
            AnalyzeTrend(s, tu.QuoteList, currentFastMa, currentSlowMa, prevFastMa, prevSlowMa, 
                maSlopeThreshold, minTrendBars);

            // ==================== 均线过滤条件 ====================
            bool maFilterLong = true;
            bool maFilterShort = true;

            if (requireMaFilter == 1)
            {
                // 价格在均线上方才能做多，价格在均线下方才能做空
                maFilterLong = q.Close > currentFastMa && q.Close > currentSlowMa;
                maFilterShort = q.Close < currentFastMa && q.Close < currentSlowMa;
            }

            if (requireMaAlignment == 1)
            {
                // 快速均线在慢速均线上方才能做多，反之做空
                maFilterLong = maFilterLong && currentFastMa > currentSlowMa;
                maFilterShort = maFilterShort && currentFastMa < currentSlowMa;
            }

            // ==================== 背离信号检测 ====================
            DivergenceSignal bullSignal = new DivergenceSignal();
            DivergenceSignal bearSignal = new DivergenceSignal();

            // 模式1：反转背离检测
            if (tradingMode == 0 || tradingMode == 1)
            {
                var reversalSignals = DetectReversalDivergence(tu.QuoteList, rsiList, 
                    lookbackPeriod, minDivergenceBars, maxDivergenceBars,
                    rsiOversold, rsiOverbought);

                if (reversalSignals.bullDiv)
                {
                    bullSignal.Detected = true;
                    bullSignal.Type = 1;
                    bullSignal.Direction = 1;
                    bullSignal.Strength = reversalSignals.strength;
                    bullSignal.PivotIndex = reversalSignals.bullPivotIdx;
                }
                if (reversalSignals.bearDiv)
                {
                    bearSignal.Detected = true;
                    bearSignal.Type = 1;
                    bearSignal.Direction = -1;
                    bearSignal.Strength = reversalSignals.strength;
                    bearSignal.PivotIndex = reversalSignals.bearPivotIdx;
                }
            }

            // 模式2：趋势延续背离检测
            if (tradingMode == 0 || tradingMode == 2)
            {
                var continuationSignals = DetectTrendContinuationDivergence(tu.QuoteList, rsiList,
                    s.TrendDirection, lookbackPeriod, minDivergenceBars, maxDivergenceBars,
                    trendPullbackDepth);

                if (continuationSignals.bullContinuation && !bullSignal.Detected)
                {
                    bullSignal.Detected = true;
                    bullSignal.Type = 2;
                    bullSignal.Direction = 1;
                    bullSignal.Strength = continuationSignals.strength;
                    bullSignal.PivotIndex = continuationSignals.bullPivotIdx;
                }
                if (continuationSignals.bearContinuation && !bearSignal.Detected)
                {
                    bearSignal.Detected = true;
                    bearSignal.Type = 2;
                    bearSignal.Direction = -1;
                    bearSignal.Strength = continuationSignals.strength;
                    bearSignal.PivotIndex = continuationSignals.bearPivotIdx;
                }
            }

            // 止损封锁：背离 latest pivot 必须严格新于已封锁的 pivot 索引
            if (bullSignal.Detected && bullSignal.PivotIndex <= s.BlockedBullPivotIndex)
            {
                bullSignal.Detected = false;
            }
            if (bearSignal.Detected && bearSignal.PivotIndex <= s.BlockedBearPivotIndex)
            {
                bearSignal.Detected = false;
            }

            // 记录背离状态：仅在 pivot 索引发生变化（出现新极值组合）时刷新
            if (bullSignal.Detected && bullSignal.PivotIndex != s.LastBullPivotIndex)
            {
                s.BullDivergenceDetected = true;
                s.LastBullPivotIndex = bullSignal.PivotIndex;
                s.DivergenceBar = tu.QuoteList.Count - 1;
                s.DivergenceType = bullSignal.Type;
            }
            if (bearSignal.Detected && bearSignal.PivotIndex != s.LastBearPivotIndex)
            {
                s.BearDivergenceDetected = true;
                s.LastBearPivotIndex = bearSignal.PivotIndex;
                s.DivergenceBar = tu.QuoteList.Count - 1;
                s.DivergenceType = bearSignal.Type;
            }

            // 背离信号有效期
            if (s.BullDivergenceDetected && tu.QuoteList.Count - s.DivergenceBar > divergenceValidBars)
                s.BullDivergenceDetected = false;
            if (s.BearDivergenceDetected && tu.QuoteList.Count - s.DivergenceBar > divergenceValidBars)
                s.BearDivergenceDetected = false;

            // 更新前值
            s.PrevRsi = currentRsi;
            s.PrevFastMa = currentFastMa;
            s.PrevSlowMa = currentSlowMa;

            // ==================== 入场信号判断 ====================
            bool longSignal = s.BullDivergenceDetected && maFilterLong;
            bool shortSignal = s.BearDivergenceDetected && maFilterShort;

            // 趋势延续模式需要趋势方向确认
            if (s.DivergenceType == 2)
            {
                longSignal = longSignal && s.TrendDirection == 1;
                shortSignal = shortSignal && s.TrendDirection == -1;
            }

            // 应用交易方向过滤
            if (tradeDirection == 1) shortSignal = false;
            if (tradeDirection == 2) longSignal = false;

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

                // 检查出场条件
                bool stopLossHit = false;
                bool takeProfitHit = false;
                bool rsiExitHit = false;
                bool maCrossExit = false;

                if (s.Position == 1)
                {
                    stopLossHit = q.Close <= s.StopLoss;
                    takeProfitHit = q.Close >= s.TakeProfit;
                    if (useRsiExit == 1)
                        rsiExitHit = currentRsi >= rsiExitOverbought;
                    if (useMaCrossExit == 1)
                        maCrossExit = prevFastMa > prevSlowMa && currentFastMa < currentSlowMa;
                }
                else
                {
                    stopLossHit = q.Close >= s.StopLoss;
                    takeProfitHit = q.Close <= s.TakeProfit;
                    if (useRsiExit == 1)
                        rsiExitHit = currentRsi <= rsiExitOversold;
                    if (useMaCrossExit == 1)
                        maCrossExit = prevFastMa < prevSlowMa && currentFastMa > currentSlowMa;
                }

                // 反向信号出场
                bool reverseSignal = (s.Position == 1 && shortSignal) || (s.Position == -1 && longSignal);

                if (stopLossHit || takeProfitHit || rsiExitHit || maCrossExit || reverseSignal)
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
                    // 避免同一背离/相邻次新 pivot 在止损/止盈后立即再次触发开仓（持仓中可能形成更新的 pivot）。
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

                    // 如果是反向信号且非止损，开反向仓位
                    if (reverseSignal && !stopLossHit)
                    {
                        int newDir = shortSignal ? -1 : 1;
                        OpenPosition(s, tu, q, newDir, num, atrValue,
                            useAtrStop, atrStopMultiplier, atrProfitMultiplier,
                            stopLossPercent, takeProfitPercent, period, sendMode,
                            s.DivergenceType);
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
                        stopLossPercent, takeProfitPercent, period, sendMode, s.DivergenceType);
                    s.EntryBullPivotIndex = s.LastBullPivotIndex; // 记录入场所基于的 pivot 索引
                    s.BullDivergenceDetected = false;
                }
                else if (shortSignal)
                {
                    OpenPosition(s, tu, q, -1, num, atrValue,
                        useAtrStop, atrStopMultiplier, atrProfitMultiplier,
                        stopLossPercent, takeProfitPercent, period, sendMode, s.DivergenceType);
                    s.EntryBearPivotIndex = s.LastBearPivotIndex;
                    s.BearDivergenceDetected = false;
                }
            }
        }

        /// <summary>
        /// 分析趋势
        /// </summary>
        private void AnalyzeTrend(State s, List<SkQuote> quotes, 
            decimal fastMa, decimal slowMa, decimal prevFastMa, decimal prevSlowMa,
            double slopeThreshold, int minTrendBars)
        {
            int count = quotes.Count;
            if (count < minTrendBars) return;

            // 计算均线斜率
            decimal fastSlope = (fastMa - prevFastMa) / prevFastMa;
            decimal slowSlope = (slowMa - prevSlowMa) / prevSlowMa;

            // 判断趋势方向
            int newTrendDirection = 0;
            if (fastMa > slowMa && fastSlope > (decimal)slopeThreshold && slowSlope > 0)
            {
                newTrendDirection = 1; // 上升趋势
            }
            else if (fastMa < slowMa && fastSlope < -(decimal)slopeThreshold && slowSlope < 0)
            {
                newTrendDirection = -1; // 下降趋势
            }

            // 更新趋势状态
            if (newTrendDirection == s.TrendDirection && newTrendDirection != 0)
            {
                s.TrendBars++;
                if (newTrendDirection == 1)
                {
                    s.TrendHighest = Math.Max(s.TrendHighest, quotes.Last().High);
                    s.TrendLowest = Math.Min(s.TrendLowest, quotes.Last().Low);
                }
                else
                {
                    s.TrendLowest = Math.Min(s.TrendLowest, quotes.Last().Low);
                    s.TrendHighest = Math.Max(s.TrendHighest, quotes.Last().High);
                }
            }
            else if (newTrendDirection != s.TrendDirection)
            {
                s.TrendDirection = newTrendDirection;
                s.TrendBars = 1;
                s.TrendHighest = quotes.Last().High;
                s.TrendLowest = quotes.Last().Low;
            }
        }

        /// <summary>
        /// 检测反转背离（经典RSI背离）；返回 pivot 索引作为稳定信号ID
        /// </summary>
        private (bool bullDiv, bool bearDiv, decimal strength, int bullPivotIdx, int bearPivotIdx) DetectReversalDivergence(
            List<SkQuote> quotes, List<RsiResult> rsiList,
            int lookbackPeriod, int minBars, int maxBars,
            int oversoldLevel, int overboughtLevel)
        {
            bool bullDiv = false;
            bool bearDiv = false;
            decimal strength = 0;
            int bullPivotIdx = -1;
            int bearPivotIdx = -1;

            int count = quotes.Count;
            if (count < maxBars + 5) return (false, false, 0, -1, -1);

            // 当前RSI值
            var currRsi = rsiList[count - 1];
            if (!currRsi.Rsi.HasValue) return (false, false, 0, -1, -1);
            decimal currentRsi = (decimal)currRsi.Rsi.Value;

            // 寻找价格低点和RSI低点（用于底背离检测）
            var priceLows = FindPriceExtremes(quotes, false, lookbackPeriod, maxBars);
            var rsiLows = FindRsiExtremes(rsiList, false, lookbackPeriod, maxBars);

            // 寻找价格高点和RSI高点（用于顶背离检测）
            var priceHighs = FindPriceExtremes(quotes, true, lookbackPeriod, maxBars);
            var rsiHighs = FindRsiExtremes(rsiList, true, lookbackPeriod, maxBars);

            // 底背离检测：价格创新低，RSI未创新低，且RSI在超卖区域
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
                        // 价格创新低，但RSI未创新低（底背离）
                        if (latestPriceLow.PriceValue < prevPriceLow.PriceValue &&
                            latestRsiLow.RsiValue > prevRsiLow.RsiValue)
                        {
                            // 检查RSI是否在超卖区域附近
                            if (latestRsiLow.RsiValue <= oversoldLevel + 10)
                            {
                                bullDiv = true;
                                bullPivotIdx = latestPriceLow.Index;
                                // 计算背离强度
                                decimal priceDiff = Math.Abs(latestPriceLow.PriceValue - prevPriceLow.PriceValue) / prevPriceLow.PriceValue;
                                decimal rsiDiff = Math.Abs(latestRsiLow.RsiValue - prevRsiLow.RsiValue);
                                strength = Math.Min(1, (priceDiff * 100 + rsiDiff / 100) / 2);
                            }
                        }
                    }
                }
            }

            // 顶背离检测：价格创新高，RSI未创新高，且RSI在超买区域
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
                        // 价格创新高，但RSI未创新高（顶背离）
                        if (latestPriceHigh.PriceValue > prevPriceHigh.PriceValue &&
                            latestRsiHigh.RsiValue < prevRsiHigh.RsiValue)
                        {
                            // 检查RSI是否在超买区域附近
                            if (latestRsiHigh.RsiValue >= overboughtLevel - 10)
                            {
                                bearDiv = true;
                                bearPivotIdx = latestPriceHigh.Index;
                                decimal priceDiff = Math.Abs(latestPriceHigh.PriceValue - prevPriceHigh.PriceValue) / prevPriceHigh.PriceValue;
                                decimal rsiDiff = Math.Abs(latestRsiHigh.RsiValue - prevRsiHigh.RsiValue);
                                strength = Math.Min(1, (priceDiff * 100 + rsiDiff / 100) / 2);
                            }
                        }
                    }
                }
            }

            return (bullDiv, bearDiv, strength, bullPivotIdx, bearPivotIdx);
        }

        /// <summary>
        /// 检测趋势延续背离；返回 pivot 索引作为稳定信号ID
        /// </summary>
        private (bool bullContinuation, bool bearContinuation, decimal strength, int bullPivotIdx, int bearPivotIdx) DetectTrendContinuationDivergence(
            List<SkQuote> quotes, List<RsiResult> rsiList,
            int trendDirection, int lookbackPeriod, int minBars, int maxBars,
            double pullbackDepth)
        {
            bool bullContinuation = false;
            bool bearContinuation = false;
            decimal strength = 0;
            int bullPivotIdx = -1;
            int bearPivotIdx = -1;

            int count = quotes.Count;
            if (count < maxBars + 10) return (false, false, 0, -1, -1);

            // 上升趋势中的回调背离（做多信号）
            if (trendDirection == 1)
            {
                // 寻找回调中的低点
                var priceLows = FindPriceExtremes(quotes, false, lookbackPeriod, maxBars);
                var rsiLows = FindRsiExtremes(rsiList, false, lookbackPeriod, maxBars);

                if (priceLows.Count >= 2 && rsiLows.Count >= 2)
                {
                    var latestLow = priceLows[priceLows.Count - 1];
                    var prevLow = priceLows[priceLows.Count - 2];

                    int barsBetween = latestLow.Index - prevLow.Index;
                    if (barsBetween >= minBars && barsBetween <= maxBars)
                    {
                        // 找到趋势中的最高点
                        decimal trendHigh = quotes.Skip(count - maxBars).Take(maxBars).Max(x => x.High);
                        decimal pullback = (trendHigh - latestLow.PriceValue) / trendHigh;

                        // 回调深度在合理范围内
                        if (pullback >= (decimal)pullbackDepth * 0.5m && pullback <= (decimal)pullbackDepth * 1.5m)
                        {
                            var latestRsiLow = rsiLows.LastOrDefault(r => Math.Abs(r.Index - latestLow.Index) <= 3);
                            var prevRsiLow = rsiLows.LastOrDefault(r => Math.Abs(r.Index - prevLow.Index) <= 3);

                            if (latestRsiLow != null && prevRsiLow != null)
                            {
                                // 价格创新低（回调更深），但RSI未创新低（动能未衰减）
                                if (latestLow.PriceValue < prevLow.PriceValue &&
                                    latestRsiLow.RsiValue > prevRsiLow.RsiValue)
                                {
                                    bullContinuation = true;
                                    bullPivotIdx = latestLow.Index;
                                    strength = Math.Min(1, (decimal)pullback * 2);
                                }
                            }
                        }
                    }
                }
            }
            // 下降趋势中的反弹背离（做空信号）
            else if (trendDirection == -1)
            {
                var priceHighs = FindPriceExtremes(quotes, true, lookbackPeriod, maxBars);
                var rsiHighs = FindRsiExtremes(rsiList, true, lookbackPeriod, maxBars);

                if (priceHighs.Count >= 2 && rsiHighs.Count >= 2)
                {
                    var latestHigh = priceHighs[priceHighs.Count - 1];
                    var prevHigh = priceHighs[priceHighs.Count - 2];

                    int barsBetween = latestHigh.Index - prevHigh.Index;
                    if (barsBetween >= minBars && barsBetween <= maxBars)
                    {
                        // 找到趋势中的最低点
                        decimal trendLow = quotes.Skip(count - maxBars).Take(maxBars).Min(x => x.Low);
                        decimal bounce = (latestHigh.PriceValue - trendLow) / trendLow;

                        // 反弹幅度在合理范围内
                        if (bounce >= (decimal)pullbackDepth * 0.5m && bounce <= (decimal)pullbackDepth * 1.5m)
                        {
                            var latestRsiHigh = rsiHighs.LastOrDefault(r => Math.Abs(r.Index - latestHigh.Index) <= 3);
                            var prevRsiHigh = rsiHighs.LastOrDefault(r => Math.Abs(r.Index - prevHigh.Index) <= 3);

                            if (latestRsiHigh != null && prevRsiHigh != null)
                            {
                                // 价格创新高（反弹更高），但RSI未创新高（动能未增强）
                                if (latestHigh.PriceValue > prevHigh.PriceValue &&
                                    latestRsiHigh.RsiValue < prevRsiHigh.RsiValue)
                                {
                                    bearContinuation = true;
                                    bearPivotIdx = latestHigh.Index;
                                    strength = Math.Min(1, (decimal)bounce * 2);
                                }
                            }
                        }
                    }
                }
            }

            return (bullContinuation, bearContinuation, strength, bullPivotIdx, bearPivotIdx);
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

        /// <summary>
        /// 寻找RSI极值点
        /// </summary>
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

        /// <summary>
        /// 开仓
        /// </summary>
        private void OpenPosition(State s, TableUnit tu, SkQuote q, int direction, decimal num,
            decimal atrValue, int useAtrStop, double atrStopMultiplier, double atrProfitMultiplier,
            double stopLossPercent, double takeProfitPercent, Period period, int sendMode, int entryMode)
        {
            s.Position = direction;
            s.Num = num;
            s.EntryPrice = q.Close;
            s.EntryAtr = atrValue;
            s.HighestSinceEntry = q.High;
            s.LowestSinceEntry = q.Low;
            s.TrailingActivated = false;
            s.EntryMode = entryMode;

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
                var symbol = GetSymbol(tu.MktSymbol);
                num = Convert.ToDecimal(ArgDic["money"]) / (q.Close * symbol.multiplier * symbol.margin_ratio);

                if (symbol.symbol_type == (int)SymbolType.COIN)
                {
                    num = Math.Floor(num * 1000) / 1000m;
                }
                else
                {
                    num = Math.Floor(num);
                }
            }

            return num;
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
            s.EntryMode = 0;
        }
    }
}
