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
    /// MACD + ADX/DI 双指标共振交易系统
    /// 
    /// 核心理念：
    /// 结合MACD的动量信号与ADX/DI的趋势强度确认，实现高胜率的趋势跟踪交易。
    /// 只有当两个指标系统同时发出一致信号时才入场，大幅降低假信号。
    /// 
    /// 指标体系：
    /// 1. MACD (Moving Average Convergence Divergence)：
    ///    - MACD线 = 快速EMA - 慢速EMA
    ///    - Signal线 = MACD的EMA
    ///    - Histogram = MACD - Signal
    ///    - 金叉/死叉提供动量转换信号
    ///    - Histogram方向变化提供早期预警
    /// 
    /// 2. ADX/DI (Average Directional Index / Directional Indicators)：
    ///    - +DI：上升方向指标
    ///    - -DI：下降方向指标
    ///    - ADX：趋势强度指标
    ///    - DI交叉确认趋势方向
    ///    - ADX确认趋势强度
    /// 
    /// 共振入场条件：
    /// 【做多】
    ///    - MACD金叉（MACD上穿Signal）或 MACD > Signal 且 Histogram递增
    ///    - +DI > -DI（多头趋势）
    ///    - ADX > 入场阈值（趋势确立）
    ///    - 可选：MACD在零轴上方（强势区域）
    /// 
    /// 【做空】
    ///    - MACD死叉（MACD下穿Signal）或 MACD < Signal 且 Histogram递减
    ///    - -DI > +DI（空头趋势）
    ///    - ADX > 入场阈值（趋势确立）
    ///    - 可选：MACD在零轴下方（弱势区域）
    /// 
    /// 出场条件：
    /// 1. 反向共振信号
    /// 2. ADX跌破退出阈值（趋势消失）
    /// 3. DI反向交叉
    /// 4. MACD反向交叉
    /// 5. ATR动态止损/止盈
    /// 6. 移动止损保护利润
    /// </summary>
    public class MACD_ADX_Resonance : StgBase
    {
        public MACD_ADX_Resonance()
        {
        }

        public MACD_ADX_Resonance(string id) : base(id)
        {
        }

        #region 参数字段

        // MACD参数
        private int _macdFastPeriod;
        private int _macdSlowPeriod;
        private int _macdSignalPeriod;

        // ADX/DI参数
        private int _adxPeriod;
        private decimal _adxEntryThreshold;
        private decimal _adxExitThreshold;
        private decimal _adxStrongThreshold;
        private decimal _diDiffThreshold;

        // 共振模式参数
        private int _resonanceMode;
        private bool _useZeroAxisFilter;
        private bool _useHistogramConfirm;
        private int _histogramConfirmBars;

        // ATR止损止盈参数
        private int _atrPeriod;
        private decimal _stopLossAtrMultiplier;
        private decimal _takeProfitAtrMultiplier;
        private bool _useTrailingStop;
        private decimal _trailingStopAtrMultiplier;
        private decimal _trailingActivationMultiplier;

        // 交易参数
        private int _tradeMode;
        private int _sendMode;
        private int _maxHoldBars;

        #endregion

        // 状态管理
        private Dictionary<string, TradeState> _stateDict = new Dictionary<string, TradeState>();

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 2;  // 两个副图：MACD和ADX/DI
            sd.UseGlobalCalc = 0;

            // ========== MACD参数 ==========
sd.ArgDescDic["MacdFastPeriod"] = new ArgDesc { Text = "MACD快线周期", Explain = "快速EMA的计算周期", Type = "number" };
            sd.ArgDic["MacdFastPeriod"] = 12;

sd.ArgDescDic["MacdSlowPeriod"] = new ArgDesc { Text = "MACD慢线周期", Explain = "慢速EMA的计算周期", Type = "number" };
            sd.ArgDic["MacdSlowPeriod"] = 26;

sd.ArgDescDic["MacdSignalPeriod"] = new ArgDesc { Text = "MACD信号线周期", Explain = "Signal线的EMA周期", Type = "number" };
            sd.ArgDic["MacdSignalPeriod"] = 9;

            // ========== ADX/DI参数 ==========
sd.ArgDescDic["AdxPeriod"] = new ArgDesc { Text = "ADX周期", Explain = "ADX和DI指标的计算周期", Type = "number" };
            sd.ArgDic["AdxPeriod"] = 14;

sd.ArgDescDic["AdxEntryThreshold"] = new ArgDesc { Text = "ADX入场阈值", Explain = "ADX大于此值时允许入场（建议20-25）", Type = "number" };
            sd.ArgDic["AdxEntryThreshold"] = 20.0;

sd.ArgDescDic["AdxExitThreshold"] = new ArgDesc { Text = "ADX退出阈值", Explain = "ADX低于此值时考虑平仓（建议15-18）", Type = "number" };
            sd.ArgDic["AdxExitThreshold"] = 15.0;

sd.ArgDescDic["AdxStrongThreshold"] = new ArgDesc { Text = "ADX强势阈值", Explain = "ADX大于此值表示强趋势（建议30-40）", Type = "number" };
            sd.ArgDic["AdxStrongThreshold"] = 30.0;

sd.ArgDescDic["DiDiffThreshold"] = new ArgDesc { Text = "DI差值阈值", Explain = "+DI与-DI的最小差值，过滤弱信号", Type = "number" };
            sd.ArgDic["DiDiffThreshold"] = 5.0;

            // ========== 共振模式参数 ==========
            sd.ArgDescDic["ResonanceMode"] = new ArgDesc { Text = "共振模式", Explain = "共振确认方式", Options = "0:严格共振(交叉同时发生)|1:宽松共振(趋势一致即可)|2:MACD主导|3:ADX/DI主导", Type = "select" };
            sd.ArgDic["ResonanceMode"] = 1;

            sd.ArgDescDic["UseZeroAxisFilter"] = new ArgDesc { Text = "零轴过滤", Explain = "仅在零轴同侧交易", Options = "1:启用(多头需MACD>0,空头需MACD<0)|0:禁用", Type = "bool" };
            sd.ArgDic["UseZeroAxisFilter"] = 0;

            sd.ArgDescDic["UseHistogramConfirm"] = new ArgDesc { Text = "柱状图确认", Explain = "要求Histogram方向一致", Options = "1:启用Histogram方向确认|0:禁用", Type = "bool" };
            sd.ArgDic["UseHistogramConfirm"] = 1;

sd.ArgDescDic["HistogramConfirmBars"] = new ArgDesc { Text = "柱状图确认K线数", Explain = "Histogram需连续多少根K线同向", Type = "number" };
            sd.ArgDic["HistogramConfirmBars"] = 2;

            // ========== ATR止损止盈参数 ==========
sd.ArgDescDic["AtrPeriod"] = new ArgDesc { Text = "ATR周期", Explain = "ATR指标的计算周期", Type = "number" };
            sd.ArgDic["AtrPeriod"] = 14;

sd.ArgDescDic["StopLossAtrMultiplier"] = new ArgDesc { Text = "止损ATR倍数", Explain = "止损距离 = ATR × 此倍数", Type = "number" };
            sd.ArgDic["StopLossAtrMultiplier"] = 2.0;

sd.ArgDescDic["TakeProfitAtrMultiplier"] = new ArgDesc { Text = "止盈ATR倍数", Explain = "止盈距离 = ATR × 此倍数", Type = "number" };
            sd.ArgDic["TakeProfitAtrMultiplier"] = 4.0;

            sd.ArgDescDic["UseTrailingStop"] = new ArgDesc { Text = "启用移动止损", Explain = "跟踪最高/低点调整止损", Options = "1:启用|0:禁用", Type = "bool" };
            sd.ArgDic["UseTrailingStop"] = 1;

sd.ArgDescDic["TrailingStopAtrMultiplier"] = new ArgDesc { Text = "移动止损ATR倍数", Explain = "移动止损距离 = ATR × 此倍数", Type = "number" };
            sd.ArgDic["TrailingStopAtrMultiplier"] = 1.5;

sd.ArgDescDic["TrailingActivationMultiplier"] = new ArgDesc { Text = "移动止损激活倍数", Explain = "盈利达到ATR×此倍数后激活移动止损", Type = "number" };
            sd.ArgDic["TrailingActivationMultiplier"] = 1.0;

            // ========== 交易参数 ==========
            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
            sd.ArgDic["lotsMode"] = 1;
sd.ArgDescDic["lots"] = new ArgDesc { Text = "手数", Explain = "固定手数数量", Type = "number" };
            sd.ArgDic["lots"] = 1.0m;
sd.ArgDescDic["money"] = new ArgDesc { Text = "金额", Explain = "固定金额数量", Type = "number" };
            sd.ArgDic["money"] = 10000m;

            sd.ArgDescDic["TradeMode"] = new ArgDesc { Text = "交易模式", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDic["TradeMode"] = 0;

            sd.ArgDescDic["SendMode"] = new ArgDesc { Text = "下单模式", Explain = "下单执行时机", Options = "0:立即下单|1:下根K线开盘下单", Type = "select" };
            sd.ArgDic["SendMode"] = 0;

sd.ArgDescDic["MaxHoldBars"] = new ArgDesc { Text = "最大持仓K线数", Explain = "持仓超过此数量K线强制平仓（0=不限制）", Type = "number" };
            sd.ArgDic["MaxHoldBars"] = 0;

            // ========== 图表颜色配置 ==========
            // 主图
            sd.ColorDic["main-StopLoss"] = "#E91E63";
            sd.ColorDic["main-TakeProfit"] = "#8BC34A";
            sd.ColorDic["main-TrailingStop"] = "#FF9800";

            // 副图0: MACD
            sd.ColorDic["sub0-MACD"] = "#2196F3";
            sd.ColorDic["sub0-Signal"] = "#FF9800";
            sd.ColorDic["sub0-Histogram"] = "#F6465D;#0ECB81";

            // 副图1: ADX/DI
            sd.ColorDic["sub1-ADX"] = "#9C27B0";
            sd.ColorDic["sub1-PDI"] = "#4CAF50";
            sd.ColorDic["sub1-MDI"] = "#F44336";

            // 副图中线
            sd.MidValDic["sub0"] = 0;   // MACD零轴
            sd.MidValDic["sub1"] = 20;  // ADX阈值线

            return sd;
        }

        private void InitParams()
        {
            // MACD参数
            _macdFastPeriod = Convert.ToInt32(ArgDic["MacdFastPeriod"]);
            _macdSlowPeriod = Convert.ToInt32(ArgDic["MacdSlowPeriod"]);
            _macdSignalPeriod = Convert.ToInt32(ArgDic["MacdSignalPeriod"]);

            // ADX/DI参数
            _adxPeriod = Convert.ToInt32(ArgDic["AdxPeriod"]);
            _adxEntryThreshold = Convert.ToDecimal(ArgDic["AdxEntryThreshold"]);
            _adxExitThreshold = Convert.ToDecimal(ArgDic["AdxExitThreshold"]);
            _adxStrongThreshold = Convert.ToDecimal(ArgDic["AdxStrongThreshold"]);
            _diDiffThreshold = Convert.ToDecimal(ArgDic["DiDiffThreshold"]);

            // 共振模式参数
            _resonanceMode = Convert.ToInt32(ArgDic["ResonanceMode"]);
            _useZeroAxisFilter = Convert.ToInt32(ArgDic["UseZeroAxisFilter"]) == 1;
            _useHistogramConfirm = Convert.ToInt32(ArgDic["UseHistogramConfirm"]) == 1;
            _histogramConfirmBars = Convert.ToInt32(ArgDic["HistogramConfirmBars"]);

            // ATR止损止盈参数
            _atrPeriod = Convert.ToInt32(ArgDic["AtrPeriod"]);
            _stopLossAtrMultiplier = Convert.ToDecimal(ArgDic["StopLossAtrMultiplier"]);
            _takeProfitAtrMultiplier = Convert.ToDecimal(ArgDic["TakeProfitAtrMultiplier"]);
            _useTrailingStop = Convert.ToInt32(ArgDic["UseTrailingStop"]) == 1;
            _trailingStopAtrMultiplier = Convert.ToDecimal(ArgDic["TrailingStopAtrMultiplier"]);
            _trailingActivationMultiplier = Convert.ToDecimal(ArgDic["TrailingActivationMultiplier"]);

            // 交易参数
            _tradeMode = Convert.ToInt32(ArgDic["TradeMode"]);
            _sendMode = Convert.ToInt32(ArgDic["SendMode"]);
            _maxHoldBars = Convert.ToInt32(ArgDic["MaxHoldBars"]);
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (!isFinal) return;

            if (ArgDic == null) return;

            InitParams();

            var quotes = tu.QuoteList;
            int minBars = Math.Max(_macdSlowPeriod + _macdSignalPeriod, _adxPeriod) + _histogramConfirmBars + 10;
            if (quotes == null || quotes.Count < minBars) return;

            var stateKey = tu.GetStateKey();
            if (!_stateDict.ContainsKey(stateKey))
            {
                _stateDict[stateKey] = new TradeState();
            }
            var state = _stateDict[stateKey];

            // 计算MACD指标
            var macdList = quotes.GetMacd(_macdFastPeriod, _macdSlowPeriod, _macdSignalPeriod).ToList();

            // 计算ADX/DI指标
            var adxList = quotes.GetAdx(_adxPeriod).ToList();

            int lastIdx = quotes.Count - 1;
            int prevIdx = lastIdx - 1;

            // 获取MACD数据
            var macdCurr = macdList[lastIdx];
            var macdPrev = macdList[prevIdx];

            // 获取ADX/DI数据
            var adxCurr = adxList[lastIdx];
            var adxPrev = adxList[prevIdx];

            // 检查数据有效性
            if (!macdCurr.Macd.HasValue || !macdCurr.Signal.HasValue || !macdCurr.Histogram.HasValue ||
                !macdPrev.Macd.HasValue || !macdPrev.Signal.HasValue || !macdPrev.Histogram.HasValue ||
                !adxCurr.Adx.HasValue || !adxCurr.Pdi.HasValue || !adxCurr.Mdi.HasValue ||
                !adxPrev.Pdi.HasValue || !adxPrev.Mdi.HasValue)
                return;

            // 计算ATR
            decimal atr = CalculateATR(quotes, _atrPeriod);
            if (atr <= 0) return;

            // 提取当前值
            var q = quotes.Last();
            decimal currentPrice = q.Close;
            decimal currentHigh = q.High;
            decimal currentLow = q.Low;

            // MACD值
            double macdValue = macdCurr.Macd.Value;
            double signalValue = macdCurr.Signal.Value;
            double histogramValue = macdCurr.Histogram.Value;
            double prevMacdValue = macdPrev.Macd.Value;
            double prevSignalValue = macdPrev.Signal.Value;
            double prevHistogramValue = macdPrev.Histogram.Value;

            // ADX/DI值
            double adxValue = adxCurr.Adx.Value;
            double pdiCurr = adxCurr.Pdi.Value;
            double mdiCurr = adxCurr.Mdi.Value;
            double pdiPrev = adxPrev.Pdi.Value;
            double mdiPrev = adxPrev.Mdi.Value;

            // 绘制MACD指标
            Plot("sub0", "MACD", PlotType.LINE, macdValue);
            Plot("sub0", "Signal", PlotType.LINE, signalValue);
            Plot("sub0", "Histogram", PlotType.RECTANGLE, histogramValue);

            // 绘制ADX/DI指标
            Plot("sub1", "ADX", PlotType.CURVE, adxValue);
            Plot("sub1", "PDI", PlotType.CURVE, pdiCurr);
            Plot("sub1", "MDI", PlotType.CURVE, mdiCurr);

            // 绘制止损止盈线
            if (state.HasPosition)
            {
                Plot("main", "StopLoss", PlotType.LINE, (double)state.StopLoss);
                Plot("main", "TakeProfit", PlotType.LINE, (double)state.TakeProfit);
                if (_useTrailingStop && state.TrailingStopActivated)
                {
                    Plot("main", "TrailingStop", PlotType.LINE, (double)state.TrailingStopPrice);
                }
            }

            // 更新持仓计数
            if (state.HasPosition)
            {
                state.HoldBars++;
            }

            // 检测信号
            var signals = AnalyzeSignals(
                macdValue, signalValue, histogramValue,
                prevMacdValue, prevSignalValue, prevHistogramValue,
                adxValue, pdiCurr, mdiCurr, pdiPrev, mdiPrev,
                macdList, lastIdx);

            if (state.HasPosition)
            {
                HandleExitLogic(state, tu.MktSymbol, period, currentPrice, currentHigh, currentLow,
                    atr, signals, adxValue);
            }
            else
            {
                HandleEntryLogic(state, tu.MktSymbol, period, currentPrice, atr, signals, macdValue, adxValue);
            }
        }

        /// <summary>
        /// 分析交易信号
        /// </summary>
        private SignalAnalysis AnalyzeSignals(
            double macdValue, double signalValue, double histogramValue,
            double prevMacdValue, double prevSignalValue, double prevHistogramValue,
            double adxValue, double pdiCurr, double mdiCurr, double pdiPrev, double mdiPrev,
            List<MacdResult> macdList, int lastIdx)
        {
            var signals = new SignalAnalysis();

            // MACD信号分析
            signals.MacdGoldenCross = prevMacdValue <= prevSignalValue && macdValue > signalValue;
            signals.MacdDeathCross = prevMacdValue >= prevSignalValue && macdValue < signalValue;
            signals.MacdAboveSignal = macdValue > signalValue;
            signals.MacdBelowSignal = macdValue < signalValue;
            signals.MacdAboveZero = macdValue > 0;
            signals.MacdBelowZero = macdValue < 0;

            // Histogram方向分析
            signals.HistogramIncreasing = histogramValue > prevHistogramValue;
            signals.HistogramDecreasing = histogramValue < prevHistogramValue;
            signals.HistogramPositive = histogramValue > 0;
            signals.HistogramNegative = histogramValue < 0;

            // Histogram连续确认
            if (_useHistogramConfirm && lastIdx >= _histogramConfirmBars)
            {
                signals.HistogramConfirmBullish = true;
                signals.HistogramConfirmBearish = true;

                for (int i = 0; i < _histogramConfirmBars; i++)
                {
                    int idx = lastIdx - i;
                    int prevIdx = idx - 1;
                    if (prevIdx < 0) break;

                    var currHist = macdList[idx].Histogram;
                    var prevHist = macdList[prevIdx].Histogram;

                    if (!currHist.HasValue || !prevHist.HasValue)
                    {
                        signals.HistogramConfirmBullish = false;
                        signals.HistogramConfirmBearish = false;
                        break;
                    }

                    if (currHist.Value <= prevHist.Value)
                        signals.HistogramConfirmBullish = false;

                    if (currHist.Value >= prevHist.Value)
                        signals.HistogramConfirmBearish = false;
                }
            }
            else
            {
                signals.HistogramConfirmBullish = signals.HistogramIncreasing;
                signals.HistogramConfirmBearish = signals.HistogramDecreasing;
            }

            // DI信号分析
            signals.DiGoldenCross = pdiPrev <= mdiPrev && pdiCurr > mdiCurr;
            signals.DiDeathCross = mdiPrev <= pdiPrev && mdiCurr > pdiCurr;
            signals.PdiAboveMdi = pdiCurr > mdiCurr;
            signals.MdiAbovePdi = mdiCurr > pdiCurr;
            signals.DiDiff = Math.Abs(pdiCurr - mdiCurr);

            // ADX信号分析
            signals.AdxAboveEntry = adxValue >= (double)_adxEntryThreshold;
            signals.AdxAboveStrong = adxValue >= (double)_adxStrongThreshold;
            signals.AdxBelowExit = adxValue < (double)_adxExitThreshold;

            // 综合共振信号
            CalculateResonanceSignals(signals);

            return signals;
        }

        /// <summary>
        /// 计算共振信号
        /// </summary>
        private void CalculateResonanceSignals(SignalAnalysis signals)
        {
            bool diDiffOk = signals.DiDiff >= (double)_diDiffThreshold;

            switch (_resonanceMode)
            {
                case 0: // 严格共振：MACD交叉 + DI交叉 + ADX确认
                    signals.BullishResonance = signals.MacdGoldenCross && signals.DiGoldenCross &&
                                               signals.AdxAboveEntry && diDiffOk;
                    signals.BearishResonance = signals.MacdDeathCross && signals.DiDeathCross &&
                                               signals.AdxAboveEntry && diDiffOk;
                    break;

                case 1: // 宽松共振：MACD交叉 + DI趋势一致 + ADX确认
                    bool macdBullish = signals.MacdGoldenCross ||
                                       (signals.MacdAboveSignal && signals.HistogramConfirmBullish);
                    bool macdBearish = signals.MacdDeathCross ||
                                       (signals.MacdBelowSignal && signals.HistogramConfirmBearish);

                    signals.BullishResonance = macdBullish && signals.PdiAboveMdi &&
                                               signals.AdxAboveEntry && diDiffOk;
                    signals.BearishResonance = macdBearish && signals.MdiAbovePdi &&
                                               signals.AdxAboveEntry && diDiffOk;
                    break;

                case 2: // MACD主导：MACD交叉为主，ADX/DI为辅
                    signals.BullishResonance = signals.MacdGoldenCross &&
                                               (signals.PdiAboveMdi || signals.AdxAboveEntry);
                    signals.BearishResonance = signals.MacdDeathCross &&
                                               (signals.MdiAbovePdi || signals.AdxAboveEntry);
                    break;

                case 3: // ADX/DI主导：DI交叉为主，MACD为辅
                    signals.BullishResonance = signals.DiGoldenCross && signals.AdxAboveEntry &&
                                               (signals.MacdAboveSignal || signals.MacdGoldenCross) && diDiffOk;
                    signals.BearishResonance = signals.DiDeathCross && signals.AdxAboveEntry &&
                                               (signals.MacdBelowSignal || signals.MacdDeathCross) && diDiffOk;
                    break;

                default:
                    signals.BullishResonance = false;
                    signals.BearishResonance = false;
                    break;
            }

            // 零轴过滤
            if (_useZeroAxisFilter)
            {
                if (!signals.MacdAboveZero) signals.BullishResonance = false;
                if (!signals.MacdBelowZero) signals.BearishResonance = false;
            }

            // Histogram确认过滤
            if (_useHistogramConfirm)
            {
                if (!signals.HistogramConfirmBullish) signals.BullishResonance = false;
                if (!signals.HistogramConfirmBearish) signals.BearishResonance = false;
            }
        }

        /// <summary>
        /// 处理入场逻辑
        /// </summary>
        private void HandleEntryLogic(TradeState state, string mktSymbol, Period period,
            decimal currentPrice, decimal atr, SignalAnalysis signals, double macdValue, double adxValue)
        {
            // 做多条件
            if (signals.BullishResonance && _tradeMode != 2)
            {
                OpenLongPosition(state, mktSymbol, currentPrice, atr, period, macdValue, adxValue);
            }
            // 做空条件
            else if (signals.BearishResonance && _tradeMode != 1)
            {
                OpenShortPosition(state, mktSymbol, currentPrice, atr, period, macdValue, adxValue);
            }
        }

        /// <summary>
        /// 处理出场逻辑
        /// </summary>
        private void HandleExitLogic(TradeState state, string mktSymbol, Period period,
            decimal currentPrice, decimal currentHigh, decimal currentLow,
            decimal atr, SignalAnalysis signals, double adxValue)
        {
            // 最大持仓时间检查
            if (_maxHoldBars > 0 && state.HoldBars >= _maxHoldBars)
            {
                ClosePosition(state, mktSymbol, currentPrice, period, "MaxHoldBars");
                return;
            }

            if (state.IsLong)
            {
                HandleLongExit(state, mktSymbol, period, currentPrice, currentHigh, currentLow, atr, signals, adxValue);
            }
            else
            {
                HandleShortExit(state, mktSymbol, period, currentPrice, currentHigh, currentLow, atr, signals, adxValue);
            }
        }

        /// <summary>
        /// 处理多头出场
        /// </summary>
        private void HandleLongExit(TradeState state, string mktSymbol, Period period,
            decimal currentPrice, decimal currentHigh, decimal currentLow,
            decimal atr, SignalAnalysis signals, double adxValue)
        {
            // 更新最高价
            if (currentHigh > state.HighestPrice)
            {
                state.HighestPrice = currentHigh;
            }

            // 止损检查
            if (currentPrice <= state.StopLoss)
            {
                ClosePosition(state, mktSymbol, currentPrice, period, "StopLoss");
                return;
            }

            // 移动止损检查
            if (_useTrailingStop && state.TrailingStopActivated)
            {
                if (currentPrice <= state.TrailingStopPrice)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period, "TrailingStop");
                    return;
                }
            }

            // 止盈检查
            if (currentPrice >= state.TakeProfit)
            {
                ClosePosition(state, mktSymbol, currentPrice, period, "TakeProfit");
                return;
            }

            // 反向共振信号
            if (signals.BearishResonance)
            {
                ClosePosition(state, mktSymbol, currentPrice, period, "ReverseResonance");
                return;
            }

            // MACD死叉
            if (signals.MacdDeathCross)
            {
                ClosePosition(state, mktSymbol, currentPrice, period, "MacdCross");
                return;
            }

            // DI死叉
            if (signals.DiDeathCross)
            {
                ClosePosition(state, mktSymbol, currentPrice, period, "DiCross");
                return;
            }

            // ADX跌破退出阈值
            if (signals.AdxBelowExit)
            {
                ClosePosition(state, mktSymbol, currentPrice, period, "AdxWeak");
                return;
            }

            // 更新移动止损
            UpdateTrailingStop(state, currentPrice, atr, true);
        }

        /// <summary>
        /// 处理空头出场
        /// </summary>
        private void HandleShortExit(TradeState state, string mktSymbol, Period period,
            decimal currentPrice, decimal currentHigh, decimal currentLow,
            decimal atr, SignalAnalysis signals, double adxValue)
        {
            // 更新最低价
            if (currentLow < state.LowestPrice)
            {
                state.LowestPrice = currentLow;
            }

            // 止损检查
            if (currentPrice >= state.StopLoss)
            {
                ClosePosition(state, mktSymbol, currentPrice, period, "StopLoss");
                return;
            }

            // 移动止损检查
            if (_useTrailingStop && state.TrailingStopActivated)
            {
                if (currentPrice >= state.TrailingStopPrice)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period, "TrailingStop");
                    return;
                }
            }

            // 止盈检查
            if (currentPrice <= state.TakeProfit)
            {
                ClosePosition(state, mktSymbol, currentPrice, period, "TakeProfit");
                return;
            }

            // 反向共振信号
            if (signals.BullishResonance)
            {
                ClosePosition(state, mktSymbol, currentPrice, period, "ReverseResonance");
                return;
            }

            // MACD金叉
            if (signals.MacdGoldenCross)
            {
                ClosePosition(state, mktSymbol, currentPrice, period, "MacdCross");
                return;
            }

            // DI金叉
            if (signals.DiGoldenCross)
            {
                ClosePosition(state, mktSymbol, currentPrice, period, "DiCross");
                return;
            }

            // ADX跌破退出阈值
            if (signals.AdxBelowExit)
            {
                ClosePosition(state, mktSymbol, currentPrice, period, "AdxWeak");
                return;
            }

            // 更新移动止损
            UpdateTrailingStop(state, currentPrice, atr, false);
        }

        /// <summary>
        /// 更新移动止损
        /// </summary>
        private void UpdateTrailingStop(TradeState state, decimal currentPrice, decimal atr, bool isLong)
        {
            if (!_useTrailingStop) return;

            decimal profit = isLong
                ? currentPrice - state.EntryPrice
                : state.EntryPrice - currentPrice;

            decimal activationDistance = atr * _trailingActivationMultiplier;

            // 检查是否激活移动止损
            if (!state.TrailingStopActivated && profit >= activationDistance)
            {
                state.TrailingStopActivated = true;
                if (isLong)
                {
                    state.TrailingStopPrice = state.HighestPrice - atr * _trailingStopAtrMultiplier;
                }
                else
                {
                    state.TrailingStopPrice = state.LowestPrice + atr * _trailingStopAtrMultiplier;
                }
            }

            // 更新移动止损价格
            if (state.TrailingStopActivated)
            {
                if (isLong)
                {
                    decimal newTrailingStop = state.HighestPrice - atr * _trailingStopAtrMultiplier;
                    if (newTrailingStop > state.TrailingStopPrice)
                    {
                        state.TrailingStopPrice = newTrailingStop;
                    }
                }
                else
                {
                    decimal newTrailingStop = state.LowestPrice + atr * _trailingStopAtrMultiplier;
                    if (newTrailingStop < state.TrailingStopPrice)
                    {
                        state.TrailingStopPrice = newTrailingStop;
                    }
                }
            }
        }

        /// <summary>
        /// 开多仓
        /// </summary>
        private void OpenLongPosition(TradeState state, string mktSymbol, decimal price, decimal atr,
            Period period, double macdValue, double adxValue)
        {
            var num = CalculateLots(mktSymbol, price);
            Trade(mktSymbol, OrderType.BUY, price, num, period, _sendMode);

            state.HasPosition = true;
            state.IsLong = true;
            state.EntryPrice = price;
            state.PositionSize = num;
            state.EntryAtr = atr;
            state.StopLoss = price - atr * _stopLossAtrMultiplier;
            state.TakeProfit = price + atr * _takeProfitAtrMultiplier;
            state.HighestPrice = price;
            state.LowestPrice = price;
            state.HoldBars = 0;
            state.TrailingStopActivated = false;
            state.TrailingStopPrice = 0;
            state.EntryMacd = macdValue;
            state.EntryAdx = adxValue;
        }

        /// <summary>
        /// 开空仓
        /// </summary>
        private void OpenShortPosition(TradeState state, string mktSymbol, decimal price, decimal atr,
            Period period, double macdValue, double adxValue)
        {
            var num = CalculateLots(mktSymbol, price);
            Trade(mktSymbol, OrderType.SELL, price, num, period, _sendMode);

            state.HasPosition = true;
            state.IsLong = false;
            state.EntryPrice = price;
            state.PositionSize = num;
            state.EntryAtr = atr;
            state.StopLoss = price + atr * _stopLossAtrMultiplier;
            state.TakeProfit = price - atr * _takeProfitAtrMultiplier;
            state.HighestPrice = price;
            state.LowestPrice = price;
            state.HoldBars = 0;
            state.TrailingStopActivated = false;
            state.TrailingStopPrice = 0;
            state.EntryMacd = macdValue;
            state.EntryAdx = adxValue;
        }

        /// <summary>
        /// 平仓
        /// </summary>
        private void ClosePosition(TradeState state, string mktSymbol, decimal price, Period period, string reason)
        {
            OrderType ot = state.IsLong ? OrderType.SELL_TO_COVER : OrderType.BUY_TO_COVER;
            Trade(mktSymbol, ot, price, state.PositionSize, period, _sendMode);

            state.Reset();
        }

        /// <summary>
        /// 计算ATR
        /// </summary>
        private decimal CalculateATR(List<SkQuote> quotes, int period)
        {
            if (quotes.Count < period + 1)
                return 0;

            var atrList = quotes.GetAtr(period).ToList();
            int lastIdx = atrList.Count - 1;
            var atr = atrList[lastIdx].Atr;
            return atr.HasValue ? (decimal)atr.Value : 0;
        }

        #region 内部类

        /// <summary>
        /// 信号分析结果
        /// </summary>
        private class SignalAnalysis
        {
            // MACD信号
            public bool MacdGoldenCross { get; set; }
            public bool MacdDeathCross { get; set; }
            public bool MacdAboveSignal { get; set; }
            public bool MacdBelowSignal { get; set; }
            public bool MacdAboveZero { get; set; }
            public bool MacdBelowZero { get; set; }

            // Histogram信号
            public bool HistogramIncreasing { get; set; }
            public bool HistogramDecreasing { get; set; }
            public bool HistogramPositive { get; set; }
            public bool HistogramNegative { get; set; }
            public bool HistogramConfirmBullish { get; set; }
            public bool HistogramConfirmBearish { get; set; }

            // DI信号
            public bool DiGoldenCross { get; set; }
            public bool DiDeathCross { get; set; }
            public bool PdiAboveMdi { get; set; }
            public bool MdiAbovePdi { get; set; }
            public double DiDiff { get; set; }

            // ADX信号
            public bool AdxAboveEntry { get; set; }
            public bool AdxAboveStrong { get; set; }
            public bool AdxBelowExit { get; set; }

            // 共振信号
            public bool BullishResonance { get; set; }
            public bool BearishResonance { get; set; }
        }

        /// <summary>
        /// 交易状态
        /// </summary>
        private decimal CalculateLots(string mktSymbol, decimal price)
        {
            var num = Convert.ToDecimal(ArgDic["lots"]);
            var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            if (lotsMode == 1)
            {
                var s2 = GetSymbol(mktSymbol);
                num = (Convert.ToDecimal(ArgDic["money"]) / (price * s2.multiplier * s2.margin_ratio));
                if (s2.symbol_type == (int)SymbolType.COIN)
                    num = (int)(num * 1000) / 1000.0m;
                else
                    num = (int)num;
            }
            return num;
        }

        private class TradeState
        {
            public bool HasPosition { get; set; } = false;
            public bool IsLong { get; set; } = false;
            public decimal EntryPrice { get; set; } = 0;
            public decimal PositionSize { get; set; } = 0;
            public decimal EntryAtr { get; set; } = 0;
            public decimal StopLoss { get; set; } = 0;
            public decimal TakeProfit { get; set; } = 0;
            public decimal HighestPrice { get; set; } = 0;
            public decimal LowestPrice { get; set; } = decimal.MaxValue;
            public int HoldBars { get; set; } = 0;

            // 移动止损
            public bool TrailingStopActivated { get; set; } = false;
            public decimal TrailingStopPrice { get; set; } = 0;

            // 入场时指标值（用于分析）
            public double EntryMacd { get; set; } = 0;
            public double EntryAdx { get; set; } = 0;

            public void Reset()
            {
                HasPosition = false;
                IsLong = false;
                EntryPrice = 0;
                PositionSize = 0;
                EntryAtr = 0;
                StopLoss = 0;
                TakeProfit = 0;
                HighestPrice = 0;
                LowestPrice = decimal.MaxValue;
                HoldBars = 0;
                TrailingStopActivated = false;
                TrailingStopPrice = 0;
                EntryMacd = 0;
                EntryAdx = 0;
            }
        }

        #endregion
    }
}
