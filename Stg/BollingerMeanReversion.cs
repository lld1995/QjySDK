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
    /// 布林线均值回归交易系统
    /// 
    /// 核心理念：价格具有向均值回归的特性，当价格偏离均值过远时，会有回归的倾向
    /// 
    /// 策略逻辑：
    /// 1. 入场信号：
    ///    - 做多：价格触及或跌破下轨后反弹，收盘价重新站上下轨
    ///    - 做空：价格触及或突破上轨后回落，收盘价重新跌破上轨
    /// 
    /// 2. 出场信号：
    ///    - 多头：价格回归到中轨附近或触及上轨止盈
    ///    - 空头：价格回归到中轨附近或触及下轨止盈
    /// 
    /// 3. 风险控制：
    ///    - ATR动态止损
    ///    - 布林带宽度过滤（避免震荡过小时交易）
    ///    - RSI超买超卖确认
    /// </summary>
    public class BollingerMeanReversion : StgBase
    {
        public BollingerMeanReversion(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // 布林带参数
            sd.ArgDic["bollPeriod"] = 20;           // 布林带周期
            sd.ArgDic["stdDev"] = 2.0;              // 标准差倍数
            
            // RSI参数
            sd.ArgDic["rsiPeriod"] = 14;            // RSI周期
            sd.ArgDic["rsiOversold"] = 30;          // RSI超卖阈值
            sd.ArgDic["rsiOverbought"] = 70;        // RSI超买阈值
            sd.ArgDic["useRsiFilter"] = 1;          // 是否使用RSI过滤
            
            // ATR止损参数
            sd.ArgDic["atrPeriod"] = 14;            // ATR周期
            sd.ArgDic["atrMultiplier"] = 2.0;       // ATR止损倍数
            sd.ArgDic["useAtrStop"] = 1;            // 是否使用ATR止损
            
            // 布林带宽度过滤
            sd.ArgDic["minBandWidth"] = 0.01;       // 最小带宽百分比（避免窄幅震荡）
            sd.ArgDic["useBandWidthFilter"] = 1;   // 是否使用带宽过滤
            
            // 出场模式
            sd.ArgDic["exitMode"] = 0;              // 0:中轨止盈 1:对侧轨道止盈 2:中轨+部分止盈
            sd.ArgDic["partialExitRatio"] = 0.5;    // 部分止盈比例
            
            // 交易模式
            sd.ArgDic["mode"] = 0;                  // 0:双向 1:仅做多 2:仅做空
            sd.ArgDic["sendMode"] = 0;              // 发单模式：0立即 1下个开盘

            // 手数控制
            sd.ArgDic["lotsMode"] = 0;              // 0:固定手数 1:固定金额
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 100000m;

            // 参数说明
            sd.ArgDescDic["bollPeriod"] = new ArgDesc() { Text = "布林带周期", Explain = "计算布林带的周期数，默认20" };
            sd.ArgDescDic["stdDev"] = new ArgDesc() { Text = "标准差倍数", Explain = "布林带上下轨的标准差倍数，默认2.0" };
            sd.ArgDescDic["rsiPeriod"] = new ArgDesc() { Text = "RSI周期", Explain = "RSI指标计算周期，默认14" };
            sd.ArgDescDic["rsiOversold"] = new ArgDesc() { Text = "RSI超卖阈值", Explain = "RSI低于此值视为超卖，默认30" };
            sd.ArgDescDic["rsiOverbought"] = new ArgDesc() { Text = "RSI超买阈值", Explain = "RSI高于此值视为超买，默认70" };
            sd.ArgDescDic["useRsiFilter"] = new ArgDesc() { Text = "RSI过滤", Explain = "0:不使用 1:使用RSI确认信号" };
            sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "ATR指标计算周期，默认14" };
            sd.ArgDescDic["atrMultiplier"] = new ArgDesc() { Text = "ATR止损倍数", Explain = "止损距离=ATR*倍数，默认2.0" };
            sd.ArgDescDic["useAtrStop"] = new ArgDesc() { Text = "ATR止损", Explain = "0:不使用 1:使用ATR动态止损" };
            sd.ArgDescDic["minBandWidth"] = new ArgDesc() { Text = "最小带宽", Explain = "布林带宽度低于此值不交易，默认0.01(1%)" };
            sd.ArgDescDic["useBandWidthFilter"] = new ArgDesc() { Text = "带宽过滤", Explain = "0:不使用 1:使用带宽过滤" };
            sd.ArgDescDic["exitMode"] = new ArgDesc() { Text = "出场模式", Explain = "0:中轨止盈 1:对侧轨道止盈 2:中轨+部分止盈" };
            sd.ArgDescDic["partialExitRatio"] = new ArgDesc() { Text = "部分止盈比例", Explain = "模式2下首次止盈的比例，默认0.5" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易方向", Explain = "0:双向 1:仅做多 2:仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0:立即 1:下个开盘" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0:固定手数 1:固定金额" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 1;

            // 颜色配置
            sd.ColorDic["main-upper"] = "#F6465D";      // 上轨红色
            sd.ColorDic["main-middle"] = "#F0B90B";     // 中轨黄色
            sd.ColorDic["main-lower"] = "#0ECB81";      // 下轨绿色
            sd.ColorDic["sub1-rsi"] = "#9B59B6";             // RSI紫色
            sd.ColorDic["main-stopLoss"] = "#E74C3C";        // 止损线红色

            // 中值线配置
            sd.MidValDic["rsi"] = 50;

            return sd;
        }

        /// <summary>
        /// 持仓状态
        /// </summary>
        private class State
        {
            public int Status { get; set; }             // 0:空仓 1:多头 2:空头
            public decimal Num { get; set; }            // 当前持仓数量
            public decimal OriginalNum { get; set; }    // 原始持仓数量（用于部分止盈）
            public decimal EntryPrice { get; set; }     // 入场价格
            public decimal StopLoss { get; set; }       // 止损价格
            public bool PartialExited { get; set; }     // 是否已部分止盈
            public decimal EntryAtr { get; set; }       // 入场时的ATR值
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 获取参数
            int bollPeriod = (int)ArgDic["bollPeriod"];
            int rsiPeriod = (int)ArgDic["rsiPeriod"];
            int atrPeriod = (int)ArgDic["atrPeriod"];
            int minDataCount = Math.Max(Math.Max(bollPeriod, rsiPeriod), atrPeriod) + 5;

            if (tu.QuoteList.Count < minDataCount) return;

            double stdDev = Convert.ToDouble(ArgDic["stdDev"]);
            int rsiOversold = (int)ArgDic["rsiOversold"];
            int rsiOverbought = (int)ArgDic["rsiOverbought"];
            int useRsiFilter = (int)ArgDic["useRsiFilter"];
            double atrMultiplier = Convert.ToDouble(ArgDic["atrMultiplier"]);
            int useAtrStop = (int)ArgDic["useAtrStop"];
            double minBandWidth = Convert.ToDouble(ArgDic["minBandWidth"]);
            int useBandWidthFilter = (int)ArgDic["useBandWidthFilter"];
            int exitMode = (int)ArgDic["exitMode"];
            double partialExitRatio = Convert.ToDouble(ArgDic["partialExitRatio"]);
            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];

            var q = tu.QuoteList.Last();
            var prevQ = tu.QuoteList[tu.QuoteList.Count - 2];

            // 计算布林带
            var bollList = tu.QuoteList.GetBollingerBands(bollPeriod, stdDev).ToList();
            var boll = bollList[bollList.Count - 1];
            var prevBoll = bollList[bollList.Count - 2];

            // 计算RSI
            var rsiList = tu.QuoteList.GetRsi(rsiPeriod).ToList();
            var rsi = rsiList[rsiList.Count - 1];

            // 计算ATR
            var atrList = tu.QuoteList.GetAtr(atrPeriod).ToList();
            var atr = atrList[atrList.Count - 1];

            // 绘制布林带
            Plot("main", "upper", PlotType.LINE, boll.UpperBand);
            Plot("main", "middle", PlotType.LINE, boll.Sma);
            Plot("main", "lower", PlotType.LINE, boll.LowerBand);

            // 绘制RSI
            Plot("sub1", "rsi", PlotType.LINE, rsi.Rsi);

            // 数据有效性检查
            if (boll.UpperBand == null || boll.LowerBand == null || boll.Sma == null) return;
            if (prevBoll.UpperBand == null || prevBoll.LowerBand == null || prevBoll.Sma == null) return;
            if (rsi.Rsi == null || atr.Atr == null) return;

            decimal upper = (decimal)boll.UpperBand.Value;
            decimal middle = (decimal)boll.Sma.Value;
            decimal lower = (decimal)boll.LowerBand.Value;
            decimal prevUpper = (decimal)prevBoll.UpperBand.Value;
            decimal prevMiddle = (decimal)prevBoll.Sma.Value;
            decimal prevLower = (decimal)prevBoll.LowerBand.Value;
            double rsiValue = rsi.Rsi.Value;
            decimal atrValue = (decimal)atr.Atr.Value;

            // 计算带宽百分比
            double bandWidth = (double)((upper - lower) / middle);

            // 带宽过滤：避免在窄幅震荡中交易
            bool bandWidthOk = useBandWidthFilter == 0 || bandWidth >= minBandWidth;

            // 计算手数
            var num = CalculateLots(tu, q);

            // 获取或创建状态
            var sk = tu.GetStateKey();
            if (!_stateDic.TryGetValue(sk, out State? s) || s == null)
            {
                s = new State();
                _stateDic[sk] = s;
            }

            // 绘制止损线
            if (s.Status != 0 && useAtrStop == 1 && s.StopLoss > 0)
            {
                Plot("main", "stopLoss", PlotType.LINE, (double)s.StopLoss);
            }

            // ==================== 交易逻辑 ====================

            if (s.Status == 0)
            {
                // 空仓状态：寻找入场信号
                ProcessEntrySignal(s, tu, q, prevQ, upper, lower, middle, prevUpper, prevLower,
                    rsiValue, rsiOversold, rsiOverbought, useRsiFilter,
                    atrValue, atrMultiplier, useAtrStop,
                    bandWidthOk, mode, sendMode, num, period);
            }
            else if (s.Status == 1)
            {
                // 多头持仓：处理出场逻辑
                ProcessLongExit(s, tu, q, upper, middle, lower, atrValue, useAtrStop,
                    exitMode, partialExitRatio, sendMode, period);
            }
            else if (s.Status == 2)
            {
                // 空头持仓：处理出场逻辑
                ProcessShortExit(s, tu, q, upper, middle, lower, atrValue, useAtrStop,
                    exitMode, partialExitRatio, sendMode, period);
            }
        }

        /// <summary>
        /// 处理入场信号
        /// </summary>
        private void ProcessEntrySignal(State s, TableUnit tu, SkQuote q, SkQuote prevQ,
            decimal upper, decimal lower, decimal middle, decimal prevUpper, decimal prevLower,
            double rsiValue, int rsiOversold, int rsiOverbought, int useRsiFilter,
            decimal atrValue, double atrMultiplier, int useAtrStop,
            bool bandWidthOk, int mode, int sendMode, decimal num, Period period)
        {
            if (!bandWidthOk) return;

            // 均值回归做多信号：
            // 1. 前一根K线收盘价低于或等于下轨（价格触及下轨区域）
            // 2. 当前K线收盘价重新站上下轨（反弹信号）
            // 3. RSI处于超卖区域（可选）
            bool longSignal = prevQ.Close <= prevLower && q.Close > lower;
            bool rsiLongOk = useRsiFilter == 0 || rsiValue < rsiOversold + 20; // RSI在超卖区域附近

            if (longSignal && rsiLongOk && mode != 2)
            {
                s.Status = 1;
                s.Num = num;
                s.OriginalNum = num;
                s.EntryPrice = q.Close;
                s.EntryAtr = atrValue;
                s.PartialExited = false;

                // 设置止损
                if (useAtrStop == 1)
                {
                    s.StopLoss = q.Close - atrValue * (decimal)atrMultiplier;
                }
                else
                {
                    s.StopLoss = lower - atrValue; // 默认止损在下轨下方
                }

                Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
            }

            // 均值回归做空信号：
            // 1. 前一根K线收盘价高于或等于上轨（价格触及上轨区域）
            // 2. 当前K线收盘价重新跌破上轨（回落信号）
            // 3. RSI处于超买区域（可选）
            bool shortSignal = prevQ.Close >= prevUpper && q.Close < upper;
            bool rsiShortOk = useRsiFilter == 0 || rsiValue > rsiOverbought - 20; // RSI在超买区域附近

            if (shortSignal && rsiShortOk && mode != 1)
            {
                s.Status = 2;
                s.Num = num;
                s.OriginalNum = num;
                s.EntryPrice = q.Close;
                s.EntryAtr = atrValue;
                s.PartialExited = false;

                // 设置止损
                if (useAtrStop == 1)
                {
                    s.StopLoss = q.Close + atrValue * (decimal)atrMultiplier;
                }
                else
                {
                    s.StopLoss = upper + atrValue; // 默认止损在上轨上方
                }

                Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
            }
        }

        /// <summary>
        /// 处理多头出场
        /// </summary>
        private void ProcessLongExit(State s, TableUnit tu, SkQuote q,
            decimal upper, decimal middle, decimal lower, decimal atrValue, int useAtrStop,
            int exitMode, double partialExitRatio, int sendMode, Period period)
        {
            bool shouldFullExit = false;
            bool shouldPartialExit = false;

            // ATR止损检查
            if (useAtrStop == 1 && q.Close <= s.StopLoss)
            {
                shouldFullExit = true;
            }

            // 根据出场模式判断
            if (!shouldFullExit)
            {
                switch (exitMode)
                {
                    case 0: // 中轨止盈
                        if (q.Close >= middle)
                        {
                            shouldFullExit = true;
                        }
                        break;

                    case 1: // 对侧轨道止盈（上轨）
                        if (q.Close >= upper)
                        {
                            shouldFullExit = true;
                        }
                        break;

                    case 2: // 中轨部分止盈 + 上轨全部止盈
                        if (!s.PartialExited && q.Close >= middle)
                        {
                            shouldPartialExit = true;
                        }
                        else if (q.Close >= upper)
                        {
                            shouldFullExit = true;
                        }
                        break;
                }
            }

            // 执行部分止盈
            if (shouldPartialExit && s.Num > 0)
            {
                decimal exitNum = Math.Round(s.OriginalNum * (decimal)partialExitRatio, 3);
                if (exitNum > 0 && exitNum < s.Num)
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, exitNum, period, sendMode);
                    s.Num -= exitNum;
                    s.PartialExited = true;

                    // 移动止损到入场价（保本）
                    s.StopLoss = s.EntryPrice;
                }
            }

            // 执行全部止盈/止损
            if (shouldFullExit && s.Num > 0)
            {
                Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
                ResetState(s);
            }
        }

        /// <summary>
        /// 处理空头出场
        /// </summary>
        private void ProcessShortExit(State s, TableUnit tu, SkQuote q,
            decimal upper, decimal middle, decimal lower, decimal atrValue, int useAtrStop,
            int exitMode, double partialExitRatio, int sendMode, Period period)
        {
            bool shouldFullExit = false;
            bool shouldPartialExit = false;

            // ATR止损检查
            if (useAtrStop == 1 && q.Close >= s.StopLoss)
            {
                shouldFullExit = true;
            }

            // 根据出场模式判断
            if (!shouldFullExit)
            {
                switch (exitMode)
                {
                    case 0: // 中轨止盈
                        if (q.Close <= middle)
                        {
                            shouldFullExit = true;
                        }
                        break;

                    case 1: // 对侧轨道止盈（下轨）
                        if (q.Close <= lower)
                        {
                            shouldFullExit = true;
                        }
                        break;

                    case 2: // 中轨部分止盈 + 下轨全部止盈
                        if (!s.PartialExited && q.Close <= middle)
                        {
                            shouldPartialExit = true;
                        }
                        else if (q.Close <= lower)
                        {
                            shouldFullExit = true;
                        }
                        break;
                }
            }

            // 执行部分止盈
            if (shouldPartialExit && s.Num > 0)
            {
                decimal exitNum = Math.Round(s.OriginalNum * (decimal)partialExitRatio, 3);
                if (exitNum > 0 && exitNum < s.Num)
                {
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, exitNum, period, sendMode);
                    s.Num -= exitNum;
                    s.PartialExited = true;

                    // 移动止损到入场价（保本）
                    s.StopLoss = s.EntryPrice;
                }
            }

            // 执行全部止盈/止损
            if (shouldFullExit && s.Num > 0)
            {
                Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
                ResetState(s);
            }
        }

        /// <summary>
        /// 计算交易手数
        /// </summary>
        private decimal CalculateLots(TableUnit tu, SkQuote q)
        {
            var num = (decimal)ArgDic["lots"];
            var lotsMode = (int)ArgDic["lotsMode"];

            if (lotsMode == 1)
            {
                var symbol = GetSymbol(tu.MktSymbol);
                num = (decimal)ArgDic["money"] / (q.Close * symbol.multiplier * symbol.margin_ratio);

                if (symbol.symbol_type == (int)SymbolType.COIN)
                {
                    num = Math.Floor(num * 1000) / 1000m;
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
            s.Status = 0;
            s.Num = 0;
            s.OriginalNum = 0;
            s.EntryPrice = 0;
            s.StopLoss = 0;
            s.PartialExited = false;
            s.EntryAtr = 0;
        }
    }
}
