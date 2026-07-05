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
    /// 动量突破策略 (Momentum Breakout Strategy)
    /// 
    /// 策略核心思想：
    /// 结合动量指标和价格突破，在趋势启动初期入场，捕捉大幅行情。
    /// 该策略是2025年最热门的量化策略之一，被AQR、Renaissance等顶级量化机构广泛使用。
    /// 
    /// 核心逻辑：
    /// 1. 动量确认：使用MACD、ROC、RSI等指标确认动量方向
    /// 2. 价格突破：突破N日高点/低点或布林带
    /// 3. 成交量验证：突破时需要成交量放大确认
    /// 4. 趋势过滤：ADX确认趋势强度
    /// 
    /// 入场条件（做多）：
    /// - 价格突破N日高点
    /// - MACD柱状图为正且递增
    /// - RSI在超买区下方（有上涨空间）
    /// - 成交量大于均量的X倍
    /// - ADX > 阈值（趋势明确）
    /// 
    /// 出场条件：
    /// - ATR动态止损
    /// - 移动止盈（跟踪最高价）
    /// - 动量衰减（MACD柱状图转负）
    /// - 最大持仓时间限制
    /// 
    /// 风控机制：
    /// - 波动率自适应仓位
    /// - 多重过滤减少假突破
    /// - 动态止损止盈
    /// </summary>
    public class MomentumBreakout : StgBase
    {
        public MomentumBreakout()
        {
        }

        public MomentumBreakout(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 2;
            sd.UseGlobalCalc = 0;

            // ==================== 突破参数 ====================
            sd.ArgDescDic["breakoutPeriod"] = new ArgDesc { Text = "突破周期", Explain = "N日高低点突破周期", Type = "number" };
            sd.ArgDic["breakoutPeriod"] = 20;

            sd.ArgDescDic["breakoutConfirmBars"] = new ArgDesc { Text = "突破确认K线数", Explain = "价格需在突破位上方维持的K线数", Type = "number" };
            sd.ArgDic["breakoutConfirmBars"] = 1;

            // ==================== 动量参数 ====================
            sd.ArgDescDic["macdFast"] = new ArgDesc { Text = "MACD快线周期", Explain = "MACD快速EMA周期", Type = "number" };
            sd.ArgDic["macdFast"] = 12;

            sd.ArgDescDic["macdSlow"] = new ArgDesc { Text = "MACD慢线周期", Explain = "MACD慢速EMA周期", Type = "number" };
            sd.ArgDic["macdSlow"] = 26;

            sd.ArgDescDic["macdSignal"] = new ArgDesc { Text = "MACD信号线周期", Explain = "MACD信号线EMA周期", Type = "number" };
            sd.ArgDic["macdSignal"] = 9;

            sd.ArgDescDic["rocPeriod"] = new ArgDesc { Text = "ROC周期", Explain = "价格变化率周期", Type = "number" };
            sd.ArgDic["rocPeriod"] = 10;

            sd.ArgDescDic["rsiPeriod"] = new ArgDesc { Text = "RSI周期", Explain = "相对强弱指标周期", Type = "number" };
            sd.ArgDic["rsiPeriod"] = 14;

            sd.ArgDescDic["rsiOverbought"] = new ArgDesc { Text = "RSI超买线", Explain = "RSI超买阈值", Type = "number" };
            sd.ArgDic["rsiOverbought"] = 70.0;

            sd.ArgDescDic["rsiOversold"] = new ArgDesc { Text = "RSI超卖线", Explain = "RSI超卖阈值", Type = "number" };
            sd.ArgDic["rsiOversold"] = 30.0;

            // ==================== 趋势过滤参数 ====================
            sd.ArgDescDic["adxPeriod"] = new ArgDesc { Text = "ADX周期", Explain = "趋势强度指标周期", Type = "number" };
            sd.ArgDic["adxPeriod"] = 14;

            sd.ArgDescDic["adxThreshold"] = new ArgDesc { Text = "ADX阈值", Explain = "ADX大于此值才入场", Type = "number" };
            sd.ArgDic["adxThreshold"] = 20.0;

            sd.ArgDescDic["useTrendFilter"] = new ArgDesc { Text = "启用趋势过滤", Explain = "仅在主趋势方向交易", Options = "1:启用|0:禁用", Type = "bool" };
            sd.ArgDic["useTrendFilter"] = 1;

            // ==================== 成交量参数 ====================
            sd.ArgDescDic["volumeMaPeriod"] = new ArgDesc { Text = "成交量均线周期", Explain = "成交量移动平均周期", Type = "number" };
            sd.ArgDic["volumeMaPeriod"] = 20;

            sd.ArgDescDic["volumeMultiplier"] = new ArgDesc { Text = "成交量倍数", Explain = "突破时成交量需大于均量的倍数", Type = "number" };
            sd.ArgDic["volumeMultiplier"] = 1.5;

            sd.ArgDescDic["useVolumeFilter"] = new ArgDesc { Text = "启用成交量过滤", Explain = "过滤低成交量信号", Options = "1:启用|0:禁用", Type = "bool" };
            sd.ArgDic["useVolumeFilter"] = 1;

            // ==================== 风控参数 ====================
            sd.ArgDescDic["atrPeriod"] = new ArgDesc { Text = "ATR周期", Explain = "平均真实波幅周期", Type = "number" };
            sd.ArgDic["atrPeriod"] = 14;

            sd.ArgDescDic["stopLossAtr"] = new ArgDesc { Text = "止损ATR倍数", Explain = "止损距离 = ATR × 此倍数", Type = "number" };
            sd.ArgDic["stopLossAtr"] = 2.0;

            sd.ArgDescDic["takeProfitAtr"] = new ArgDesc { Text = "止盈ATR倍数", Explain = "止盈距离 = ATR × 此倍数", Type = "number" };
            sd.ArgDic["takeProfitAtr"] = 4.0;

            sd.ArgDescDic["useTrailingStop"] = new ArgDesc { Text = "启用移动止损", Explain = "跟踪最高/低点调整止损", Options = "1:启用|0:禁用", Type = "bool" };
            sd.ArgDic["useTrailingStop"] = 1;

            sd.ArgDescDic["trailingStopAtr"] = new ArgDesc { Text = "移动止损ATR倍数", Explain = "移动止损距离 = ATR × 此倍数", Type = "number" };
            sd.ArgDic["trailingStopAtr"] = 2.0;

            sd.ArgDescDic["maxHoldBars"] = new ArgDesc { Text = "最大持仓K线数", Explain = "超过此数量强制平仓(0=不限制)", Type = "number" };
            sd.ArgDic["maxHoldBars"] = 100;

            sd.ArgDescDic["useMomentumExit"] = new ArgDesc { Text = "启用动量出场", Explain = "动量减弱时平仓", Options = "1:动量衰减时出场|0:禁用", Type = "bool" };
            sd.ArgDic["useMomentumExit"] = 1;

            // ==================== 交易参数 ====================
            sd.ArgDescDic["mode"] = new ArgDesc { Text = "交易模式", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDic["mode"] = 0;

            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额|2:波动率调整", Type = "select" };
            sd.ArgDic["lotsMode"] = 1;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "交易手数", Explain = "固定手数模式下的交易数量", Type = "number" };
            sd.ArgDic["lots"] = 1.0m;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "交易金额", Explain = "固定金额模式下的交易金额", Type = "number" };
            sd.ArgDic["money"] = 10000m;

            sd.ArgDescDic["targetVolatility"] = new ArgDesc { Text = "目标波动率", Explain = "波动率调整模式下的目标年化波动率(%)", Type = "number" };
            sd.ArgDic["targetVolatility"] = 15.0;

            // ==================== 颜色配置 ====================
            // 主图
            sd.ColorDic["main-HighestHigh"] = "#4CAF50";
            sd.ColorDic["main-LowestLow"] = "#F44336";
            sd.ColorDic["main-StopLoss"] = "#FF5722";
            sd.ColorDic["main-TakeProfit"] = "#8BC34A";

            // 副图0：MACD
            sd.ColorDic["sub0-MACD"] = "#2196F3";
            sd.ColorDic["sub0-Signal"] = "#FF9800";
            sd.ColorDic["sub0-Histogram"] = "#9C27B0";

            // 副图1：RSI
            sd.ColorDic["sub1-RSI"] = "#E91E63";
            sd.ColorDic["sub1-Overbought"] = "#F44336";
            sd.ColorDic["sub1-Oversold"] = "#4CAF50";

            // 中值线
            sd.MidValDic["sub0"] = 0;
            sd.MidValDic["sub1"] = 50;

            return sd;
        }

        private class TradeState
        {
            public int Status { get; set; }           // 0=空仓 1=多头 2=空头
            public decimal Num { get; set; }          // 持仓数量
            public decimal EntryPrice { get; set; }   // 入场价格
            public decimal StopLoss { get; set; }     // 止损价
            public decimal TakeProfit { get; set; }   // 止盈价
            public decimal HighestPrice { get; set; } // 持仓期间最高价
            public decimal LowestPrice { get; set; }  // 持仓期间最低价
            public int HoldBars { get; set; }         // 持仓K线数
            public decimal EntryAtr { get; set; }     // 入场时ATR
            public double LastMacdHist { get; set; }  // 上一个MACD柱状图值
            public int BreakoutConfirmCount { get; set; } // 突破确认计数（保留兼容）
            public int LongBreakoutBars { get; set; }  // 多头突破连续满足bar数
            public int ShortBreakoutBars { get; set; } // 空头突破连续满足bar数

            public void Reset()
            {
                Status = 0;
                Num = 0;
                EntryPrice = 0;
                StopLoss = 0;
                TakeProfit = 0;
                HighestPrice = 0;
                LowestPrice = decimal.MaxValue;
                HoldBars = 0;
                EntryAtr = 0;
                LastMacdHist = 0;
                BreakoutConfirmCount = 0;
                LongBreakoutBars = 0;
                ShortBreakoutBars = 0;
            }
        }

        private Dictionary<string, TradeState> _stateDic = new Dictionary<string, TradeState>();

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (!isFinal) return;

            if (ArgDic == null) return;

            // 获取参数
            int breakoutPeriod = Convert.ToInt32(ArgDic["breakoutPeriod"]);
            int breakoutConfirmBars = Convert.ToInt32(ArgDic["breakoutConfirmBars"]);

            int macdFast = Convert.ToInt32(ArgDic["macdFast"]);
            int macdSlow = Convert.ToInt32(ArgDic["macdSlow"]);
            int macdSignal = Convert.ToInt32(ArgDic["macdSignal"]);
            int rocPeriod = Convert.ToInt32(ArgDic["rocPeriod"]);
            int rsiPeriod = Convert.ToInt32(ArgDic["rsiPeriod"]);
            double rsiOverbought = Convert.ToDouble(ArgDic["rsiOverbought"]);
            double rsiOversold = Convert.ToDouble(ArgDic["rsiOversold"]);

            int adxPeriod = Convert.ToInt32(ArgDic["adxPeriod"]);
            double adxThreshold = Convert.ToDouble(ArgDic["adxThreshold"]);
            int useTrendFilter = Convert.ToInt32(ArgDic["useTrendFilter"]);

            int volumeMaPeriod = Convert.ToInt32(ArgDic["volumeMaPeriod"]);
            double volumeMultiplier = Convert.ToDouble(ArgDic["volumeMultiplier"]);
            int useVolumeFilter = Convert.ToInt32(ArgDic["useVolumeFilter"]);

            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
            double stopLossAtr = Convert.ToDouble(ArgDic["stopLossAtr"]);
            double takeProfitAtr = Convert.ToDouble(ArgDic["takeProfitAtr"]);
            int useTrailingStop = Convert.ToInt32(ArgDic["useTrailingStop"]);
            double trailingStopAtr = Convert.ToDouble(ArgDic["trailingStopAtr"]);
            int maxHoldBars = Convert.ToInt32(ArgDic["maxHoldBars"]);
            int useMomentumExit = Convert.ToInt32(ArgDic["useMomentumExit"]);

            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            // 计算最小K线数
            int minBars = Math.Max(Math.Max(Math.Max(breakoutPeriod, macdSlow + macdSignal), 
                                   Math.Max(adxPeriod, volumeMaPeriod)), 
                                   Math.Max(rocPeriod, rsiPeriod)) + 10;
            if (tu.QuoteList.Count < minBars) return;

            var quotes = tu.QuoteList;
            var q = quotes.Last();
            decimal currentPrice = q.Close;

            // ==================== 计算突破指标 ====================
            // N日最高价和最低价
            decimal highestHigh = decimal.MinValue;
            decimal lowestLow = decimal.MaxValue;
            for (int i = 1; i <= breakoutPeriod; i++)
            {
                var bar = quotes[quotes.Count - 1 - i];
                if (bar.High > highestHigh) highestHigh = bar.High;
                if (bar.Low < lowestLow) lowestLow = bar.Low;
            }

            // ==================== 计算动量指标 ====================
            // MACD
            var macdList = quotes.GetMacd(macdFast, macdSlow, macdSignal).ToList();
            var macdLast = macdList.Last();
            var macdPrev = macdList[macdList.Count - 2];
            double macdLine = macdLast.Macd.GetValueOrDefault(0);
            double signalLine = macdLast.Signal.GetValueOrDefault(0);
            double histogram = macdLast.Histogram.GetValueOrDefault(0);
            double histogramPrev = macdPrev.Histogram.GetValueOrDefault(0);

            // ROC
            var rocList = quotes.GetRoc(rocPeriod).ToList();
            double rocVal = rocList.Last().Roc.GetValueOrDefault(0);

            // RSI
            var rsiList = quotes.GetRsi(rsiPeriod).ToList();
            double rsiVal = rsiList.Last().Rsi.GetValueOrDefault(50);

            // ==================== 计算趋势指标 ====================
            // ADX
            var adxList = quotes.GetAdx(adxPeriod).ToList();
            var adxLast = adxList.Last();
            double adxVal = adxLast.Adx.GetValueOrDefault(0);
            double pdi = adxLast.Pdi.GetValueOrDefault(0);
            double mdi = adxLast.Mdi.GetValueOrDefault(0);

            // ==================== 计算成交量指标 ====================
            decimal avgVolume = 0;
            for (int i = 0; i < volumeMaPeriod; i++)
            {
                avgVolume += quotes[quotes.Count - 1 - i].Volume;
            }
            avgVolume /= volumeMaPeriod;
            double volumeRatio = avgVolume > 0 ? (double)(q.Volume / avgVolume) : 1.0;

            // ==================== 计算ATR ====================
            var atrList = quotes.GetAtr(atrPeriod).ToList();
            decimal atrVal = (decimal)atrList.Last().Atr.GetValueOrDefault(0);

            // ==================== 获取或创建状态 ====================
            var sk = tu.GetStateKey();
            if (!_stateDic.ContainsKey(sk))
            {
                _stateDic[sk] = new TradeState();
            }
            var state = _stateDic[sk];

            // ==================== 绘制指标 ====================
            Plot("main", "HighestHigh", PlotType.LINE, (double)highestHigh);
            Plot("main", "LowestLow", PlotType.LINE, (double)lowestLow);

            Plot("sub0", "MACD", PlotType.LINE, macdLine);
            Plot("sub0", "Signal", PlotType.LINE, signalLine);
            Plot("sub0", "Histogram", PlotType.RECTANGLE, histogram);

            Plot("sub1", "RSI", PlotType.LINE, rsiVal);
            Plot("sub1", "Overbought", PlotType.LINE, rsiOverbought);
            Plot("sub1", "Oversold", PlotType.LINE, rsiOversold);

            // 绘制止损止盈线
            if (state.Status != 0)
            {
                Plot("main", "StopLoss", PlotType.LINE, (double)state.StopLoss);
                Plot("main", "TakeProfit", PlotType.LINE, (double)state.TakeProfit);
            }

            // 更新持仓状态
            if (state.Status != 0)
            {
                state.HoldBars++;
                if (q.High > state.HighestPrice) state.HighestPrice = q.High;
                if (q.Low < state.LowestPrice) state.LowestPrice = q.Low;
                state.LastMacdHist = histogram;
            }

            // ==================== 计算手数 ====================
            var num = Convert.ToDecimal(ArgDic["lots"]);
            var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            if (lotsMode == 1)
            {
                var sym = GetSymbol(tu.MktSymbol);
                num = (Convert.ToDecimal(ArgDic["money"]) / (q.Close * sym.multiplier * sym.margin_ratio));
                if (sym.symbol_type == (int)SymbolType.COIN)
                {
                    num = (int)(num * sym.scale) / (decimal)sym.scale;
                }
                else
                {
                    num = (int)num;
                }
            }
            else if (lotsMode == 2 && atrVal > 0)
            {
                // 波动率调整模式
                double targetVol = Convert.ToDouble(ArgDic["targetVolatility"]) / 100.0;
                double annualizedAtr = (double)atrVal / (double)currentPrice * Math.Sqrt(252);
                if (annualizedAtr > 0)
                {
                    double volAdjustment = targetVol / annualizedAtr;
                    var sym = GetSymbol(tu.MktSymbol);
                    num = (decimal)(volAdjustment * (double)(Convert.ToDecimal(ArgDic["money"]) / (q.Close * sym.multiplier * sym.margin_ratio)));
                    if (sym.symbol_type == (int)SymbolType.COIN)
                    {
                        num = (int)(num * sym.scale) / (decimal)sym.scale;
                    }
                    else
                    {
                        num = (int)num;
                    }
                }
            }

            // ==================== 信号判断 ====================
            // 做多突破信号
            bool longBreakout = currentPrice > highestHigh;
            bool longMomentum = histogram > 0 && histogram > histogramPrev && rocVal > 0;
            bool longRsi = rsiVal < rsiOverbought && rsiVal > rsiOversold;
            bool longTrend = useTrendFilter == 0 || (adxVal > adxThreshold && pdi > mdi);
            bool longVolume = useVolumeFilter == 0 || volumeRatio >= volumeMultiplier;

            // 做空突破信号
            bool shortBreakout = currentPrice < lowestLow;
            bool shortMomentum = histogram < 0 && histogram < histogramPrev && rocVal < 0;
            bool shortRsi = rsiVal > rsiOversold && rsiVal < rsiOverbought;
            bool shortTrend = useTrendFilter == 0 || (adxVal > adxThreshold && mdi > pdi);
            bool shortVolume = useVolumeFilter == 0 || volumeRatio >= volumeMultiplier;

            // 突破确认计数（breakoutConfirmBars之前声明但未使用，现在接入）
            if (longBreakout) state.LongBreakoutBars++;
            else state.LongBreakoutBars = 0;
            if (shortBreakout) state.ShortBreakoutBars++;
            else state.ShortBreakoutBars = 0;

            // ==================== 交易逻辑 ====================
            if (state.Status == 0)
            {
                // 空仓：寻找入场信号
                int confirmBars = Math.Max(1, breakoutConfirmBars);
                bool longConfirmed = state.LongBreakoutBars >= confirmBars;
                bool shortConfirmed = state.ShortBreakoutBars >= confirmBars;
                bool longSignal = longConfirmed && longMomentum && longRsi && longTrend && longVolume && mode != 2;
                bool shortSignal = shortConfirmed && shortMomentum && shortRsi && shortTrend && shortVolume && mode != 1;

                if (longSignal && atrVal > 0)
                {
                    state.Status = 1;
                    state.Num = num;
                    state.EntryPrice = currentPrice;
                    state.EntryAtr = atrVal;
                    state.StopLoss = currentPrice - atrVal * (decimal)stopLossAtr;
                    state.TakeProfit = currentPrice + atrVal * (decimal)takeProfitAtr;
                    state.HighestPrice = q.High;
                    state.LowestPrice = q.Low;
                    state.HoldBars = 0;
                    state.LastMacdHist = histogram;
                    Trade(tu.MktSymbol, OrderType.BUY, currentPrice, num, period, sendMode);
                }
                else if (shortSignal && atrVal > 0)
                {
                    state.Status = 2;
                    state.Num = num;
                    state.EntryPrice = currentPrice;
                    state.EntryAtr = atrVal;
                    state.StopLoss = currentPrice + atrVal * (decimal)stopLossAtr;
                    state.TakeProfit = currentPrice - atrVal * (decimal)takeProfitAtr;
                    state.HighestPrice = q.High;
                    state.LowestPrice = q.Low;
                    state.HoldBars = 0;
                    state.LastMacdHist = histogram;
                    Trade(tu.MktSymbol, OrderType.SELL, currentPrice, num, period, sendMode);
                }
            }
            else if (state.Status == 1)
            {
                // 多头持仓：检查出场条件
                bool exitSignal = false;
                string exitReason = "";

                // 止损
                if (currentPrice <= state.StopLoss)
                {
                    exitSignal = true;
                    exitReason = "StopLoss";
                }
                // 止盈
                else if (currentPrice >= state.TakeProfit)
                {
                    exitSignal = true;
                    exitReason = "TakeProfit";
                }
                // 最大持仓时间
                else if (maxHoldBars > 0 && state.HoldBars >= maxHoldBars)
                {
                    exitSignal = true;
                    exitReason = "MaxHoldBars";
                }
                // 动量衰减出场
                else if (useMomentumExit == 1 && histogram < 0 && state.LastMacdHist > 0)
                {
                    exitSignal = true;
                    exitReason = "MomentumDecay";
                }

                if (exitSignal)
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.Num, period, sendMode);
                    state.Reset();
                }
                else if (useTrailingStop == 1 && atrVal > 0)
                {
                    // 移动止损
                    decimal newStopLoss = state.HighestPrice - atrVal * (decimal)trailingStopAtr;
                    if (newStopLoss > state.StopLoss)
                    {
                        state.StopLoss = newStopLoss;
                    }
                }
            }
            else if (state.Status == 2)
            {
                // 空头持仓：检查出场条件
                bool exitSignal = false;
                string exitReason = "";

                // 止损
                if (currentPrice >= state.StopLoss)
                {
                    exitSignal = true;
                    exitReason = "StopLoss";
                }
                // 止盈
                else if (currentPrice <= state.TakeProfit)
                {
                    exitSignal = true;
                    exitReason = "TakeProfit";
                }
                // 最大持仓时间
                else if (maxHoldBars > 0 && state.HoldBars >= maxHoldBars)
                {
                    exitSignal = true;
                    exitReason = "MaxHoldBars";
                }
                // 动量衰减出场
                else if (useMomentumExit == 1 && histogram > 0 && state.LastMacdHist < 0)
                {
                    exitSignal = true;
                    exitReason = "MomentumDecay";
                }

                if (exitSignal)
                {
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.Num, period, sendMode);
                    state.Reset();
                }
                else if (useTrailingStop == 1 && atrVal > 0)
                {
                    // 移动止损
                    decimal newStopLoss = state.LowestPrice + atrVal * (decimal)trailingStopAtr;
                    if (newStopLoss < state.StopLoss)
                    {
                        state.StopLoss = newStopLoss;
                    }
                }
            }
        }
    }
}
