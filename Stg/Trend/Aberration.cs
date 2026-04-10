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
    /// Aberration 标准趋势跟踪策略
    /// 
    /// 策略原理：
    /// Aberration是Keith Fitschen开发的经典趋势跟踪系统，基于肯特纳通道(Keltner Channel)。
    /// 使用移动平均线作为中轨，ATR(真实波幅均值)的倍数作为通道宽度。
    /// 
    /// 入场规则：
    /// - 价格突破上轨 -> 做多
    /// - 价格突破下轨 -> 做空
    /// 
    /// 出场规则：
    /// - 多头持仓时，价格跌破中轨 -> 平多
    /// - 空头持仓时，价格涨破中轨 -> 平空
    /// 
    /// 可选止损：
    /// - 基于ATR的跟踪止损
    /// </summary>
    public class Aberration : StgBase
    {
        public Aberration()
        {
        }

        public Aberration(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // 通道参数
            sd.ArgDic["maPeriod"] = 20;           // 移动平均周期
            sd.ArgDic["atrPeriod"] = 20;          // ATR周期
            sd.ArgDic["atrMultiplier"] = 2.0;     // ATR倍数(通道宽度)
            
            // 交易模式
            sd.ArgDic["mode"] = 0;                // 0:双向 1:仅做多 2:仅做空
            sd.ArgDic["sendMode"] = 0;            // 发单模式
            sd.ArgDic["exitMode"] = 0;            // 0:中轨平仓 1:反向突破平仓
            
            // 止损参数
            sd.ArgDic["useTrailingStop"] = 0;     // 是否使用跟踪止损
            sd.ArgDic["trailingAtrMultiplier"] = 3.0; // 跟踪止损ATR倍数

            // 手数控制
            sd.ArgDic["lotsMode"] = 1;            // 0:固定手数 1:固定金额
            sd.ArgDic["lots"] = 1.0m;             // 固定手数
            sd.ArgDic["money"] = 10000m;         // 固定金额

            // 参数说明
            sd.ArgDescDic["maPeriod"] = new ArgDesc() { Text = "均线周期", Explain = "计算中轨的移动平均周期" };
            sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "计算ATR的周期数" };
            sd.ArgDescDic["atrMultiplier"] = new ArgDesc() { Text = "ATR倍数", Explain = "通道上下轨距离中轨的ATR倍数" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "0:双向交易 1:仅做多 2:仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0:立即发单 1:下个开盘发单" };
            sd.ArgDescDic["exitMode"] = new ArgDesc() { Text = "平仓模式", Explain = "0:中轨平仓 1:反向突破平仓" };
            sd.ArgDescDic["useTrailingStop"] = new ArgDesc() { Text = "跟踪止损", Explain = "0:不使用 1:使用ATR跟踪止损" };
            sd.ArgDescDic["trailingAtrMultiplier"] = new ArgDesc() { Text = "止损ATR倍数", Explain = "跟踪止损距离的ATR倍数" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0:固定手数 1:固定金额" };
            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "固定手数", Explain = "每次交易的固定手数" };
            sd.ArgDescDic["money"] = new ArgDesc() { Text = "固定金额", Explain = "每次交易的固定金额" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 0;

            // 通道颜色配置
            sd.ColorDic["main-upper"] = "#FF5722";   // 上轨橙红色
            sd.ColorDic["main-middle"] = "#FF9800";  // 中轨橙色
            sd.ColorDic["main-lower"] = "#2196F3";   // 下轨蓝色

            return sd;
        }

        /// <summary>
        /// 持仓状态
        /// </summary>
        private class State
        {
            public int Status { get; set; }           // 0:空仓 1:多头 2:空头
            public decimal Num { get; set; }          // 持仓数量
            public decimal EntryPrice { get; set; }   // 入场价格
            public decimal TrailingStop { get; set; } // 跟踪止损价格
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 获取参数
            int maPeriod = Convert.ToInt32(ArgDic["maPeriod"]);
            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
            double atrMultiplier = Convert.ToDouble(ArgDic["atrMultiplier"]);
            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
            int exitMode = Convert.ToInt32(ArgDic["exitMode"]);
            int useTrailingStop = Convert.ToInt32(ArgDic["useTrailingStop"]);
            double trailingAtrMultiplier = Convert.ToDouble(ArgDic["trailingAtrMultiplier"]);

            // 确保有足够的数据
            int requiredBars = Math.Max(maPeriod, atrPeriod) + 1;
            if (tu.QuoteList.Count < requiredBars) return;

            var q = tu.QuoteList.Last();
            var prevQ = tu.QuoteList[tu.QuoteList.Count - 2];

            // 计算SMA作为中轨
            var smaList = tu.QuoteList.GetSma(maPeriod).ToList();
            var sma1 = smaList[smaList.Count - 1];
            var sma2 = smaList[smaList.Count - 2];

            // 计算ATR
            var atrList = tu.QuoteList.GetAtr(atrPeriod).ToList();
            var atr1 = atrList[atrList.Count - 1];
            var atr2 = atrList[atrList.Count - 2];

            if (sma1.Sma == null || sma2.Sma == null) return;
            if (atr1.Atr == null || atr2.Atr == null) return;

            // 计算通道
            decimal middle = (decimal)sma1.Sma.Value;
            decimal atr = (decimal)atr1.Atr.Value;
            decimal upper = middle + atr * (decimal)atrMultiplier;
            decimal lower = middle - atr * (decimal)atrMultiplier;

            decimal prevMiddle = (decimal)sma2.Sma.Value;
            decimal prevAtr = (decimal)atr2.Atr.Value;
            decimal prevUpper = prevMiddle + prevAtr * (decimal)atrMultiplier;
            decimal prevLower = prevMiddle - prevAtr * (decimal)atrMultiplier;

            // 绘制通道
            Plot("main", "upper", PlotType.LINE, (double)upper);
            Plot("main", "middle", PlotType.LINE, (double)middle);
            Plot("main", "lower", PlotType.LINE, (double)lower);

            // 计算手数
            decimal num = CalculateLots(tu, q);
            if (num <= 0) return;

            // 获取或创建状态
            State s = GetOrCreateState(tu.GetStateKey());

            // 交易逻辑
            ExecuteTradeLogic(tu, period, q, prevQ, s, num,
                upper, lower, middle, prevUpper, prevLower, prevMiddle,
                atr, mode, sendMode, exitMode, useTrailingStop, trailingAtrMultiplier);
        }

        /// <summary>
        /// 计算交易手数
        /// </summary>
        private decimal CalculateLots(TableUnit tu, SkQuote q)
        {
            var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            var num = Convert.ToDecimal(ArgDic["lots"]);

            if (lotsMode == 1)
            {
                var symbol = GetSymbol(tu.MktSymbol);
                num = Convert.ToDecimal(ArgDic["money"]) / (q.Close * symbol.multiplier * symbol.margin_ratio);

                if (symbol.symbol_type == (int)SymbolType.COIN)
                {
                    num = Math.Floor(num * 1000) / 1000.0m;
                }
                else
                {
                    num = Math.Floor(num);
                }
            }

            return num;
        }

        /// <summary>
        /// 获取或创建持仓状态
        /// </summary>
        private State GetOrCreateState(string stateKey)
        {
            if (!_stateDic.ContainsKey(stateKey))
            {
                _stateDic[stateKey] = new State();
            }
            return _stateDic[stateKey];
        }

        /// <summary>
        /// 执行交易逻辑
        /// </summary>
        private void ExecuteTradeLogic(TableUnit tu, Period period, SkQuote q, SkQuote prevQ, State s, decimal num,
            decimal upper, decimal lower, decimal middle, decimal prevUpper, decimal prevLower, decimal prevMiddle,
            decimal atr, int mode, int sendMode, int exitMode, int useTrailingStop, double trailingAtrMultiplier)
        {
            decimal trailingAtr = atr * (decimal)trailingAtrMultiplier;

            if (s.Status == 0)
            {
                // 空仓状态：寻找入场信号
                HandleEntrySignal(tu, period, q, prevQ, s, num, upper, lower, prevUpper, prevLower, 
                    trailingAtr, mode, sendMode);
            }
            else if (s.Status == 1)
            {
                // 多头持仓
                HandleLongPosition(tu, period, q, prevQ, s, num, upper, lower, middle, 
                    prevUpper, prevLower, prevMiddle, trailingAtr, mode, sendMode, exitMode, useTrailingStop);
            }
            else if (s.Status == 2)
            {
                // 空头持仓
                HandleShortPosition(tu, period, q, prevQ, s, num, upper, lower, middle,
                    prevUpper, prevLower, prevMiddle, trailingAtr, mode, sendMode, exitMode, useTrailingStop);
            }
        }

        /// <summary>
        /// 处理入场信号
        /// </summary>
        private void HandleEntrySignal(TableUnit tu, Period period, SkQuote q, SkQuote prevQ, State s, decimal num,
            decimal upper, decimal lower, decimal prevUpper, decimal prevLower, decimal trailingAtr, int mode, int sendMode)
        {
            // 价格从下方突破上轨 -> 做多
            if (prevQ.Close <= prevUpper && q.Close > upper && mode != 2)
            {
                s.Status = 1;
                s.Num = num;
                s.EntryPrice = q.Close;
                s.TrailingStop = q.Close - trailingAtr;
                Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
            }
            // 价格从上方突破下轨 -> 做空
            else if (prevQ.Close >= prevLower && q.Close < lower && mode != 1)
            {
                s.Status = 2;
                s.Num = num;
                s.EntryPrice = q.Close;
                s.TrailingStop = q.Close + trailingAtr;
                Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
            }
        }

        /// <summary>
        /// 处理多头持仓
        /// </summary>
        private void HandleLongPosition(TableUnit tu, Period period, SkQuote q, SkQuote prevQ, State s, decimal num,
            decimal upper, decimal lower, decimal middle, decimal prevUpper, decimal prevLower, decimal prevMiddle,
            decimal trailingAtr, int mode, int sendMode, int exitMode, int useTrailingStop)
        {
            bool shouldExit = false;
            bool isStopLoss = false;

            // 更新跟踪止损
            if (useTrailingStop == 1)
            {
                decimal newStop = q.High - trailingAtr;
                if (newStop > s.TrailingStop)
                {
                    s.TrailingStop = newStop;
                }

                // 检查是否触发跟踪止损
                if (q.Close < s.TrailingStop)
                {
                    shouldExit = true;
                    isStopLoss = true;
                }
            }

            // 检查通道出场条件
            if (!shouldExit)
            {
                if (exitMode == 0)
                {
                    // 模式0：价格跌破中轨平仓
                    shouldExit = prevQ.Close >= prevMiddle && q.Close < middle;
                }
                else
                {
                    // 模式1：价格跌破下轨平仓
                    shouldExit = prevQ.Close >= prevLower && q.Close < lower;
                }
            }

            if (shouldExit)
            {
                var oriNum = s.Num;
                Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);

                // 判断是否反手做空（非止损出场且反向突破模式）
                if (!isStopLoss && exitMode == 1 && mode != 1 && prevQ.Close >= prevLower && q.Close < lower)
                {
                    s.Status = 2;
                    s.Num = num;
                    s.EntryPrice = q.Close;
                    s.TrailingStop = q.Close + trailingAtr;
                    Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                }
                else
                {
                    s.Status = 0;
                    s.Num = 0;
                    s.EntryPrice = 0;
                    s.TrailingStop = 0;
                }
            }
        }

        /// <summary>
        /// 处理空头持仓
        /// </summary>
        private void HandleShortPosition(TableUnit tu, Period period, SkQuote q, SkQuote prevQ, State s, decimal num,
            decimal upper, decimal lower, decimal middle, decimal prevUpper, decimal prevLower, decimal prevMiddle,
            decimal trailingAtr, int mode, int sendMode, int exitMode, int useTrailingStop)
        {
            bool shouldExit = false;
            bool isStopLoss = false;

            // 更新跟踪止损
            if (useTrailingStop == 1)
            {
                decimal newStop = q.Low + trailingAtr;
                if (newStop < s.TrailingStop)
                {
                    s.TrailingStop = newStop;
                }

                // 检查是否触发跟踪止损
                if (q.Close > s.TrailingStop)
                {
                    shouldExit = true;
                    isStopLoss = true;
                }
            }

            // 检查通道出场条件
            if (!shouldExit)
            {
                if (exitMode == 0)
                {
                    // 模式0：价格涨破中轨平仓
                    shouldExit = prevQ.Close <= prevMiddle && q.Close > middle;
                }
                else
                {
                    // 模式1：价格涨破上轨平仓
                    shouldExit = prevQ.Close <= prevUpper && q.Close > upper;
                }
            }

            if (shouldExit)
            {
                var oriNum = s.Num;
                Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);

                // 判断是否反手做多（非止损出场且反向突破模式）
                if (!isStopLoss && exitMode == 1 && mode != 2 && prevQ.Close <= prevUpper && q.Close > upper)
                {
                    s.Status = 1;
                    s.Num = num;
                    s.EntryPrice = q.Close;
                    s.TrailingStop = q.Close - trailingAtr;
                    Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                }
                else
                {
                    s.Status = 0;
                    s.Num = 0;
                    s.EntryPrice = 0;
                    s.TrailingStop = 0;
                }
            }
        }
    }
}
