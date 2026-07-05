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
    /// 自适应均值回归策略 (Adaptive Mean Reversion Strategy)
    /// 
    /// 策略核心思想：
    /// 结合布林带、RSI和Keltner通道，在价格极端偏离时入场，
    /// 利用市场的均值回归特性获利。该策略是Renaissance Technologies等顶级量化机构的核心策略之一。
    /// 
    /// 核心创新：
    /// 1. 自适应参数：根据市场波动率动态调整布林带宽度和RSI阈值
    /// 2. 多重确认：布林带突破 + RSI超买超卖 + Keltner通道确认
    /// 3. 挤压检测：布林带收窄进入Keltner通道时识别低波动期
    /// 4. 波动率过滤：高波动期降低仓位，低波动期增加仓位
    /// 
    /// 入场条件（做多）：
    /// - 价格触及或突破布林带下轨
    /// - RSI进入超卖区域
    /// - 非挤压状态（避免趋势启动期）
    /// - 可选：价格在Keltner通道内（确认非趋势）
    /// 
    /// 入场条件（做空）：
    /// - 价格触及或突破布林带上轨
    /// - RSI进入超买区域
    /// - 非挤压状态
    /// - 可选：价格在Keltner通道内
    /// 
    /// 出场条件：
    /// - 价格回归到布林带中轨
    /// - RSI回归到中性区域
    /// - ATR动态止损
    /// - 最大持仓时间
    /// 
    /// 风控机制：
    /// - 波动率自适应仓位
    /// - 挤压期不入场
    /// - 动态止损止盈
    /// </summary>
    public class AdaptiveMeanReversion : StgBase
    {
        public AdaptiveMeanReversion()
        {
        }

        public AdaptiveMeanReversion(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 2;
            sd.UseGlobalCalc = 0;

            // ==================== 布林带参数 ====================
            sd.ArgDescDic["bollPeriod"] = new ArgDesc { Text = "布林带周期", Explain = "布林带计算周期", Type = "number" };
            sd.ArgDic["bollPeriod"] = 20;

            sd.ArgDescDic["bollStdDev"] = new ArgDesc { Text = "布林带标准差", Explain = "布林带标准差倍数", Type = "number" };
            sd.ArgDic["bollStdDev"] = 2.0;

            sd.ArgDescDic["useAdaptiveBoll"] = new ArgDesc { Text = "自适应布林带", Explain = "根据波动率调整标准差", Options = "1:根据波动率调整标准差|0:固定", Type = "select" };
            sd.ArgDic["useAdaptiveBoll"] = 1;

            sd.ArgDescDic["minStdDev"] = new ArgDesc { Text = "最小标准差", Explain = "自适应模式下的最小标准差倍数", Type = "number" };
            sd.ArgDic["minStdDev"] = 1.5;

            sd.ArgDescDic["maxStdDev"] = new ArgDesc { Text = "最大标准差", Explain = "自适应模式下的最大标准差倍数", Type = "number" };
            sd.ArgDic["maxStdDev"] = 3.0;

            // ==================== RSI参数 ====================
            sd.ArgDescDic["rsiPeriod"] = new ArgDesc { Text = "RSI周期", Explain = "RSI计算周期", Type = "number" };
            sd.ArgDic["rsiPeriod"] = 14;

            sd.ArgDescDic["rsiOverbought"] = new ArgDesc { Text = "RSI超买线", Explain = "RSI超买阈值", Type = "number" };
            sd.ArgDic["rsiOverbought"] = 70.0;

            sd.ArgDescDic["rsiOversold"] = new ArgDesc { Text = "RSI超卖线", Explain = "RSI超卖阈值", Type = "number" };
            sd.ArgDic["rsiOversold"] = 30.0;

            sd.ArgDescDic["useAdaptiveRsi"] = new ArgDesc { Text = "自适应RSI阈值", Explain = "根据波动率调整RSI阈值", Options = "1:根据波动率调整RSI阈值|0:固定", Type = "select" };
            sd.ArgDic["useAdaptiveRsi"] = 1;

            sd.ArgDescDic["rsiExitLevel"] = new ArgDesc { Text = "RSI出场水平", Explain = "RSI回归到此水平出场", Type = "number" };
            sd.ArgDic["rsiExitLevel"] = 50.0;

            // ==================== Keltner通道参数 ====================
            sd.ArgDescDic["keltnerPeriod"] = new ArgDesc { Text = "Keltner周期", Explain = "Keltner通道EMA周期", Type = "number" };
            sd.ArgDic["keltnerPeriod"] = 20;

            sd.ArgDescDic["keltnerMultiplier"] = new ArgDesc { Text = "Keltner倍数", Explain = "Keltner通道ATR倍数", Type = "number" };
            sd.ArgDic["keltnerMultiplier"] = 1.5;

            sd.ArgDescDic["useKeltnerFilter"] = new ArgDesc { Text = "Keltner过滤", Explain = "使用Keltner通道确认", Options = "1:使用Keltner通道确认|0:不使用", Type = "select" };
            sd.ArgDic["useKeltnerFilter"] = 1;

            // ==================== 挤压检测参数 ====================
            sd.ArgDescDic["useSqueezeFilter"] = new ArgDesc { Text = "挤压过滤", Explain = "挤压期不入场", Options = "1:挤压期不入场|0:不过滤", Type = "select" };
            sd.ArgDic["useSqueezeFilter"] = 1;

            sd.ArgDescDic["squeezeReleaseBars"] = new ArgDesc { Text = "挤压释放K线数", Explain = "挤压释放后等待的K线数", Type = "number" };
            sd.ArgDic["squeezeReleaseBars"] = 3;

            // ==================== 风控参数 ====================
            sd.ArgDescDic["atrPeriod"] = new ArgDesc { Text = "ATR周期", Explain = "ATR计算周期", Type = "number" };
            sd.ArgDic["atrPeriod"] = 14;

            sd.ArgDescDic["stopLossAtr"] = new ArgDesc { Text = "止损ATR倍数", Explain = "止损距离 = ATR × 此倍数", Type = "number" };
            sd.ArgDic["stopLossAtr"] = 2.0;

            sd.ArgDescDic["takeProfitMode"] = new ArgDesc { Text = "止盈模式", Explain = "止盈触发方式", Options = "0:回归中轨|1:ATR倍数|2:RSI回归", Type = "select" };
            sd.ArgDic["takeProfitMode"] = 0;

            sd.ArgDescDic["takeProfitAtr"] = new ArgDesc { Text = "止盈ATR倍数", Explain = "止盈距离 = ATR × 此倍数(模式1)", Type = "number" };
            sd.ArgDic["takeProfitAtr"] = 2.0;

            sd.ArgDescDic["useTrailingStop"] = new ArgDesc { Text = "启用移动止损", Explain = "跟踪最高/低点调整止损", Options = "1:启用|0:禁用", Type = "bool" };
            sd.ArgDic["useTrailingStop"] = 1;

            sd.ArgDescDic["trailingStopAtr"] = new ArgDesc { Text = "移动止损ATR倍数", Explain = "移动止损距离 = ATR × 此倍数", Type = "number" };
            sd.ArgDic["trailingStopAtr"] = 1.5;

            sd.ArgDescDic["maxHoldBars"] = new ArgDesc { Text = "最大持仓K线数", Explain = "超过此数量强制平仓", Type = "number" };
            sd.ArgDic["maxHoldBars"] = 20;

            // ==================== 交易参数 ====================
            sd.ArgDescDic["mode"] = new ArgDesc { Text = "交易模式", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDic["mode"] = 0;

            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额|2:波动率反比", Type = "select" };
            sd.ArgDic["lotsMode"] = 1;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "交易手数", Explain = "固定手数模式下的交易数量", Type = "number" };
            sd.ArgDic["lots"] = 1.0m;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "交易金额", Explain = "固定金额模式下的交易金额", Type = "number" };
            sd.ArgDic["money"] = 10000m;

            sd.ArgDescDic["requireBollTouch"] = new ArgDesc { Text = "要求触及布林带", Explain = "价格需触及布林带", Options = "1:价格必须触及布林带|0:突破即可", Type = "select" };
            sd.ArgDic["requireBollTouch"] = 0;

            // ==================== 颜色配置 ====================
            // 主图
            sd.ColorDic["main-Boll_Upper"] = "#F44336";
            sd.ColorDic["main-Boll_Lower"] = "#4CAF50";
            sd.ColorDic["main-Boll_Mid"] = "#2196F3";
            sd.ColorDic["main-Keltner_Upper"] = "#9C27B0";
            sd.ColorDic["main-Keltner_Lower"] = "#9C27B0";
            sd.ColorDic["main-StopLoss"] = "#FF5722";
            sd.ColorDic["main-TakeProfit"] = "#8BC34A";

            // 副图0：RSI
            sd.ColorDic["sub0-RSI"] = "#E91E63";
            sd.ColorDic["sub0-Overbought"] = "#F44336";
            sd.ColorDic["sub0-Oversold"] = "#4CAF50";
            sd.ColorDic["sub0-ExitLevel"] = "#FF9800";

            // 副图1：挤压指标
            sd.ColorDic["sub1-Squeeze"] = "#2196F3";
            sd.ColorDic["sub1-BollWidth"] = "#4CAF50";

            // 中值线
            sd.MidValDic["sub0"] = 50;
            sd.MidValDic["sub1"] = 0;

            sd.ArgDescDic["stopCooldownBars"] = new ArgDesc() { Text = "止损冷却K线数", Explain = "止损后N根K线内禁止开仓,设0关闭", Type = "number" };
            sd.ArgDic["stopCooldownBars"] = 5;

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
            public double EntryRsi { get; set; }      // 入场时RSI
            public int BlockedDir { get; set; }       // 止损后被封锁的方向:0无 1多 2空
            public int CooldownRemaining { get; set; } // 止损后剩余冷却K线数

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
                EntryRsi = 50;
            }
        }

        private Dictionary<string, TradeState> _stateDic = new Dictionary<string, TradeState>();
        private Dictionary<string, int> _squeezeBarsCount = new Dictionary<string, int>();
        private Dictionary<string, bool> _wasInSqueeze = new Dictionary<string, bool>();

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (!isFinal) return;

            if (ArgDic == null) return;

            // 获取参数
            int bollPeriod = Convert.ToInt32(ArgDic["bollPeriod"]);
            double bollStdDev = Convert.ToDouble(ArgDic["bollStdDev"]);
            int useAdaptiveBoll = Convert.ToInt32(ArgDic["useAdaptiveBoll"]);
            double minStdDev = Convert.ToDouble(ArgDic["minStdDev"]);
            double maxStdDev = Convert.ToDouble(ArgDic["maxStdDev"]);

            int rsiPeriod = Convert.ToInt32(ArgDic["rsiPeriod"]);
            double rsiOverbought = Convert.ToDouble(ArgDic["rsiOverbought"]);
            double rsiOversold = Convert.ToDouble(ArgDic["rsiOversold"]);
            int useAdaptiveRsi = Convert.ToInt32(ArgDic["useAdaptiveRsi"]);
            double rsiExitLevel = Convert.ToDouble(ArgDic["rsiExitLevel"]);

            int keltnerPeriod = Convert.ToInt32(ArgDic["keltnerPeriod"]);
            double keltnerMultiplier = Convert.ToDouble(ArgDic["keltnerMultiplier"]);
            int useKeltnerFilter = Convert.ToInt32(ArgDic["useKeltnerFilter"]);

            int useSqueezeFilter = Convert.ToInt32(ArgDic["useSqueezeFilter"]);
            int squeezeReleaseBars = Convert.ToInt32(ArgDic["squeezeReleaseBars"]);

            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
            double stopLossAtr = Convert.ToDouble(ArgDic["stopLossAtr"]);
            int takeProfitMode = Convert.ToInt32(ArgDic["takeProfitMode"]);
            double takeProfitAtr = Convert.ToDouble(ArgDic["takeProfitAtr"]);
            int useTrailingStop = Convert.ToInt32(ArgDic["useTrailingStop"]);
            double trailingStopAtr = Convert.ToDouble(ArgDic["trailingStopAtr"]);
            int maxHoldBars = Convert.ToInt32(ArgDic["maxHoldBars"]);

            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
            int requireBollTouch = Convert.ToInt32(ArgDic["requireBollTouch"]);

            // 计算最小K线数
            int minBars = Math.Max(Math.Max(bollPeriod, keltnerPeriod), Math.Max(rsiPeriod, atrPeriod)) + 20;
            if (tu.QuoteList.Count < minBars) return;

            var quotes = tu.QuoteList;
            var q = quotes.Last();
            decimal currentPrice = q.Close;
            var sk = tu.GetStateKey();

            // ==================== 计算ATR ====================
            var atrList = quotes.GetAtr(atrPeriod).ToList();
            decimal atrVal = (decimal)atrList.Last().Atr.GetValueOrDefault(0);
            double atrPercent = atrVal > 0 ? (double)atrVal / (double)currentPrice * 100 : 2;

            // ==================== 自适应布林带 ====================
            double adaptiveStdDev = bollStdDev;
            if (useAdaptiveBoll == 1)
            {
                // 波动率高时增加标准差，波动率低时减少
                // 假设正常波动率为2%
                double volRatio = atrPercent / 2.0;
                adaptiveStdDev = bollStdDev * Math.Max(0.75, Math.Min(1.5, volRatio));
                adaptiveStdDev = Math.Max(minStdDev, Math.Min(maxStdDev, adaptiveStdDev));
            }

            var bollList = quotes.GetBollingerBands(bollPeriod, adaptiveStdDev).ToList();
            var bollLast = bollList.Last();
            double bollUpper = bollLast.UpperBand.GetValueOrDefault(0);
            double bollLower = bollLast.LowerBand.GetValueOrDefault(0);
            double bollMid = bollLast.Sma.GetValueOrDefault(0);
            double bollWidth = bollMid > 0 ? (bollUpper - bollLower) / bollMid * 100 : 0;

            // ==================== 计算Keltner通道 ====================
            var keltnerList = quotes.GetKeltner(keltnerPeriod, keltnerMultiplier).ToList();
            var keltnerLast = keltnerList.Last();
            double keltnerUpper = keltnerLast.UpperBand.GetValueOrDefault(0);
            double keltnerLower = keltnerLast.LowerBand.GetValueOrDefault(0);

            // ==================== 挤压检测 ====================
            bool isInSqueeze = bollLower > keltnerLower && bollUpper < keltnerUpper;

            if (!_wasInSqueeze.ContainsKey(sk)) _wasInSqueeze[sk] = false;
            if (!_squeezeBarsCount.ContainsKey(sk)) _squeezeBarsCount[sk] = 0;

            // 挤压释放计数
            if (_wasInSqueeze[sk] && !isInSqueeze)
            {
                _squeezeBarsCount[sk] = squeezeReleaseBars;
            }
            if (_squeezeBarsCount[sk] > 0)
            {
                _squeezeBarsCount[sk]--;
            }
            _wasInSqueeze[sk] = isInSqueeze;

            bool squeezeOk = useSqueezeFilter == 0 || (!isInSqueeze && _squeezeBarsCount[sk] == 0);

            // ==================== 自适应RSI阈值 ====================
            double adaptiveOverbought = rsiOverbought;
            double adaptiveOversold = rsiOversold;
            if (useAdaptiveRsi == 1)
            {
                // 波动率高时放宽RSI阈值
                double volAdjust = Math.Max(0.8, Math.Min(1.2, atrPercent / 2.0));
                adaptiveOverbought = 50 + (rsiOverbought - 50) * volAdjust;
                adaptiveOversold = 50 - (50 - rsiOversold) * volAdjust;
            }

            // ==================== 计算RSI ====================
            var rsiList = quotes.GetRsi(rsiPeriod).ToList();
            double rsiVal = rsiList.Last().Rsi.GetValueOrDefault(50);

            // ==================== 获取或创建状态 ====================
            if (!_stateDic.ContainsKey(sk))
            {
                _stateDic[sk] = new TradeState();
            }
            var state = _stateDic[sk];

            // ==================== 绘制指标 ====================
            Plot("main", "Boll_Upper", PlotType.LINE, bollUpper);
            Plot("main", "Boll_Lower", PlotType.LINE, bollLower);
            Plot("main", "Boll_Mid", PlotType.LINE, bollMid);

            if (useKeltnerFilter == 1)
            {
                Plot("main", "Keltner_Upper", PlotType.LINE, keltnerUpper);
                Plot("main", "Keltner_Lower", PlotType.LINE, keltnerLower);
            }

            Plot("sub0", "RSI", PlotType.LINE, rsiVal);
            Plot("sub0", "Overbought", PlotType.LINE, adaptiveOverbought);
            Plot("sub0", "Oversold", PlotType.LINE, adaptiveOversold);
            Plot("sub0", "ExitLevel", PlotType.LINE, rsiExitLevel);

            // 挤压指标：1=挤压中 0=正常
            Plot("sub1", "Squeeze", PlotType.LINE, isInSqueeze ? 1.0 : 0.0);
            Plot("sub1", "BollWidth", PlotType.LINE, bollWidth);

            // 绘制止损止盈线
            if (state.Status != 0)
            {
                Plot("main", "StopLoss", PlotType.LINE, (double)state.StopLoss);
                if (takeProfitMode == 1)
                {
                    Plot("main", "TakeProfit", PlotType.LINE, (double)state.TakeProfit);
                }
            }

            // 更新持仓状态
            if (state.Status != 0)
            {
                state.HoldBars++;
                if (q.High > state.HighestPrice) state.HighestPrice = q.High;
                if (q.Low < state.LowestPrice) state.LowestPrice = q.Low;
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
            else if (lotsMode == 2 && atrPercent > 0)
            {
                // 波动率反比：波动率高时减少仓位
                double volInverse = 2.0 / atrPercent; // 假设2%为基准波动率
                volInverse = Math.Max(0.5, Math.Min(2.0, volInverse));
                var sym = GetSymbol(tu.MktSymbol);
                num = (decimal)(volInverse * (double)(Convert.ToDecimal(ArgDic["money"]) / (q.Close * sym.multiplier * sym.margin_ratio)));
                if (sym.symbol_type == (int)SymbolType.COIN)
                {
                    num = (int)(num * sym.scale) / (decimal)sym.scale;
                }
                else
                {
                    num = (int)num;
                }
            }

            // ==================== 信号判断 ====================
            // 做多信号：价格触及布林带下轨 + RSI超卖
            bool longBollSignal = requireBollTouch == 1 
                ? (double)q.Low <= bollLower 
                : (double)currentPrice < bollLower;
            bool longRsiSignal = rsiVal < adaptiveOversold;
            bool longKeltnerOk = useKeltnerFilter == 0 || (double)currentPrice > keltnerLower;
            bool longSignal = longBollSignal && longRsiSignal && longKeltnerOk && squeezeOk && mode != 2;

            // 做空信号：价格触及布林带上轨 + RSI超买
            bool shortBollSignal = requireBollTouch == 1 
                ? (double)q.High >= bollUpper 
                : (double)currentPrice > bollUpper;
            bool shortRsiSignal = rsiVal > adaptiveOverbought;
            bool shortKeltnerOk = useKeltnerFilter == 0 || (double)currentPrice < keltnerUpper;
            bool shortSignal = shortBollSignal && shortRsiSignal && shortKeltnerOk && squeezeOk && mode != 1;

            // ==================== 交易逻辑 ====================
            if (state.Status == 0)
            {
                // 信号重置再武装：出现反向信号时解除封锁
                if (state.BlockedDir == 1 && shortSignal)
                    state.BlockedDir = 0;
                else if (state.BlockedDir == 2 && longSignal)
                    state.BlockedDir = 0;

                // 止损后冷却期
                if (state.CooldownRemaining > 0)
                {
                    state.CooldownRemaining--;
                    return;
                }

                // 空仓：寻找入场信号
                if (longSignal && atrVal > 0 && state.BlockedDir != 1)
                {
                    state.Status = 1;
                    state.Num = num;
                    state.EntryPrice = currentPrice;
                    state.EntryAtr = atrVal;
                    state.EntryRsi = rsiVal;
                    state.StopLoss = currentPrice - atrVal * (decimal)stopLossAtr;
                    state.TakeProfit = takeProfitMode == 1 
                        ? currentPrice + atrVal * (decimal)takeProfitAtr 
                        : (decimal)bollMid;
                    state.HighestPrice = q.High;
                    state.LowestPrice = q.Low;
                    state.HoldBars = 0;
                    Trade(tu.MktSymbol, OrderType.BUY, currentPrice, num, period, sendMode);
                }
                else if (shortSignal && atrVal > 0 && state.BlockedDir != 2)
                {
                    state.Status = 2;
                    state.Num = num;
                    state.EntryPrice = currentPrice;
                    state.EntryAtr = atrVal;
                    state.EntryRsi = rsiVal;
                    state.StopLoss = currentPrice + atrVal * (decimal)stopLossAtr;
                    state.TakeProfit = takeProfitMode == 1 
                        ? currentPrice - atrVal * (decimal)takeProfitAtr 
                        : (decimal)bollMid;
                    state.HighestPrice = q.High;
                    state.LowestPrice = q.Low;
                    state.HoldBars = 0;
                    Trade(tu.MktSymbol, OrderType.SELL, currentPrice, num, period, sendMode);
                }
            }
            else if (state.Status == 1)
            {
                // 多头持仓：检查出场条件
                bool exitSignal = false;
                bool stopLossHit = false;

                // 止损
                if (currentPrice <= state.StopLoss)
                {
                    exitSignal = true;
                    stopLossHit = true;
                }
                // 止盈模式判断
                else if (takeProfitMode == 0)
                {
                    // 回归中轨
                    if ((double)currentPrice >= bollMid)
                    {
                        exitSignal = true;
                    }
                }
                else if (takeProfitMode == 1)
                {
                    // ATR倍数止盈
                    if (currentPrice >= state.TakeProfit)
                    {
                        exitSignal = true;
                    }
                }
                else if (takeProfitMode == 2)
                {
                    // RSI回归
                    if (rsiVal >= rsiExitLevel)
                    {
                        exitSignal = true;
                    }
                }
                // 最大持仓时间
                if (state.HoldBars >= maxHoldBars)
                {
                    exitSignal = true;
                }

                if (exitSignal)
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.Num, period, sendMode);
                    state.Reset();
                    if (stopLossHit)
                    {
                        state.BlockedDir = 1;
                        state.CooldownRemaining = Convert.ToInt32(ArgDic["stopCooldownBars"]);
                    }
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
                bool stopLossHit = false;

                // 止损
                if (currentPrice >= state.StopLoss)
                {
                    exitSignal = true;
                    stopLossHit = true;
                }
                // 止盈模式判断
                else if (takeProfitMode == 0)
                {
                    // 回归中轨
                    if ((double)currentPrice <= bollMid)
                    {
                        exitSignal = true;
                    }
                }
                else if (takeProfitMode == 1)
                {
                    // ATR倍数止盈
                    if (currentPrice <= state.TakeProfit)
                    {
                        exitSignal = true;
                    }
                }
                else if (takeProfitMode == 2)
                {
                    // RSI回归
                    if (rsiVal <= rsiExitLevel)
                    {
                        exitSignal = true;
                    }
                }
                // 最大持仓时间
                if (state.HoldBars >= maxHoldBars)
                {
                    exitSignal = true;
                }

                if (exitSignal)
                {
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.Num, period, sendMode);
                    state.Reset();
                    if (stopLossHit)
                    {
                        state.BlockedDir = 2;
                        state.CooldownRemaining = Convert.ToInt32(ArgDic["stopCooldownBars"]);
                    }
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
