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
    /// RUMI交易系统 (Relative Ultimate Momentum Indicator)
    /// 
    /// 策略原理：
    /// RUMI是一种结合动量、趋势和波动率的综合交易系统。
    /// 该系统使用相对动量指标识别趋势方向，结合均线系统确认趋势，
    /// 并使用ATR进行仓位管理和止损设置。
    /// 
    /// 核心组件：
    /// 1. RUMI指标：
    ///    - 计算价格的相对动量 = (Close - SMA(Close, N)) / ATR
    ///    - 对相对动量进行平滑处理得到RUMI线
    ///    - RUMI信号线 = SMA(RUMI, SignalPeriod)
    /// 
    /// 2. 趋势确认：
    ///    - 快速EMA和慢速EMA的交叉确认趋势方向
    ///    - 价格相对于均线的位置作为趋势过滤
    /// 
    /// 3. 入场规则：
    ///    - 做多：RUMI上穿信号线 + 快速EMA > 慢速EMA + 价格 > 慢速EMA
    ///    - 做空：RUMI下穿信号线 + 快速EMA < 慢速EMA + 价格 < 慢速EMA
    /// 
    /// 4. 出场规则：
    ///    - RUMI反向穿越信号线
    ///    - 或触发ATR止损/止盈
    ///    - 或均线死叉/金叉
    /// 
    /// 5. 仓位管理：
    ///    - 基于ATR的风险控制
    ///    - 单笔风险不超过账户的设定比例
    /// </summary>
    public class RUMI : StgBase
    {
        public RUMI(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // ========== RUMI指标参数 ==========
            sd.ArgDic["rumiPeriod"] = 14;             // RUMI计算周期
            sd.ArgDic["rumiSmooth"] = 5;              // RUMI平滑周期
            sd.ArgDic["signalPeriod"] = 9;            // 信号线周期
            sd.ArgDic["rumiThreshold"] = 0.0;         // RUMI阈值（过滤弱信号）

            // ========== 均线参数 ==========
            sd.ArgDic["fastEMA"] = 12;                // 快速EMA周期
            sd.ArgDic["slowEMA"] = 26;                // 慢速EMA周期
            sd.ArgDic["trendEMA"] = 50;               // 趋势过滤EMA周期
            sd.ArgDic["useTrendFilter"] = 1;          // 是否使用趋势过滤 0:否 1:是

            // ========== ATR参数 ==========
            sd.ArgDic["atrPeriod"] = 14;              // ATR计算周期
            sd.ArgDic["atrStopMultiplier"] = 2.0;     // 止损ATR倍数
            sd.ArgDic["atrProfitMultiplier"] = 3.0;   // 止盈ATR倍数
            sd.ArgDic["useTrailingStop"] = 1;         // 是否使用移动止损 0:否 1:是
            sd.ArgDic["trailingATR"] = 1.5;           // 移动止损ATR倍数

            // ========== 仓位管理 ==========
            sd.ArgDic["riskPerTrade"] = 0.02;         // 每笔交易风险比例（账户的2%）
            sd.ArgDic["accountEquity"] = 1000000m;    // 账户权益
            sd.ArgDic["lotsMode"] = 0;                // 0:按风险计算 1:固定手数
            sd.ArgDic["fixedLots"] = 1.0m;            // 固定手数

            // ========== 交易模式 ==========
            sd.ArgDic["mode"] = 0;                    // 0:双向 1:仅做多 2:仅做空
            sd.ArgDic["sendMode"] = 0;                // 发单模式

            // ========== 信号确认 ==========
            sd.ArgDic["confirmBars"] = 1;             // 信号确认K线数
            sd.ArgDic["useVolumeFilter"] = 0;         // 是否使用成交量过滤 0:否 1:是
            sd.ArgDic["volumeMultiplier"] = 1.5;      // 成交量倍数阈值

            // ========== 参数说明 ==========
            sd.ArgDescDic["rumiPeriod"] = new ArgDesc() { Text = "RUMI周期", Explain = "相对动量计算周期" };
            sd.ArgDescDic["rumiSmooth"] = new ArgDesc() { Text = "RUMI平滑", Explain = "RUMI平滑周期" };
            sd.ArgDescDic["signalPeriod"] = new ArgDesc() { Text = "信号线周期", Explain = "RUMI信号线周期" };
            sd.ArgDescDic["rumiThreshold"] = new ArgDesc() { Text = "RUMI阈值", Explain = "过滤弱信号的阈值" };
            sd.ArgDescDic["fastEMA"] = new ArgDesc() { Text = "快速EMA", Explain = "快速EMA周期" };
            sd.ArgDescDic["slowEMA"] = new ArgDesc() { Text = "慢速EMA", Explain = "慢速EMA周期" };
            sd.ArgDescDic["trendEMA"] = new ArgDesc() { Text = "趋势EMA", Explain = "趋势过滤EMA周期" };
            sd.ArgDescDic["useTrendFilter"] = new ArgDesc() { Text = "趋势过滤", Explain = "0:不过滤 1:使用趋势过滤" };
            sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "ATR计算周期" };
            sd.ArgDescDic["atrStopMultiplier"] = new ArgDesc() { Text = "止损ATR倍数", Explain = "止损距离=ATR×倍数" };
            sd.ArgDescDic["atrProfitMultiplier"] = new ArgDesc() { Text = "止盈ATR倍数", Explain = "止盈距离=ATR×倍数" };
            sd.ArgDescDic["useTrailingStop"] = new ArgDesc() { Text = "移动止损", Explain = "0:固定止损 1:移动止损" };
            sd.ArgDescDic["trailingATR"] = new ArgDesc() { Text = "移动止损ATR", Explain = "移动止损距离ATR倍数" };
            sd.ArgDescDic["riskPerTrade"] = new ArgDesc() { Text = "单笔风险比例", Explain = "每笔交易占账户权益的比例" };
            sd.ArgDescDic["accountEquity"] = new ArgDesc() { Text = "账户权益", Explain = "用于计算仓位的账户权益" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0:按风险计算 1:固定手数" };
            sd.ArgDescDic["fixedLots"] = new ArgDesc() { Text = "固定手数", Explain = "固定手数模式下的手数" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易方向", Explain = "0:双向 1:仅做多 2:仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0:立即发单 1:下个开盘发单" };
            sd.ArgDescDic["confirmBars"] = new ArgDesc() { Text = "确认K线数", Explain = "信号确认所需K线数" };
            sd.ArgDescDic["useVolumeFilter"] = new ArgDesc() { Text = "成交量过滤", Explain = "0:不过滤 1:过滤低量信号" };
            sd.ArgDescDic["volumeMultiplier"] = new ArgDesc() { Text = "成交量倍数", Explain = "成交量需超过均量的倍数" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 2;

            // 颜色配置
            sd.ColorDic["rumi-line"] = "#2196F3";         // RUMI线蓝色
            sd.ColorDic["rumi-signal"] = "#FF9800";       // 信号线橙色
            sd.ColorDic["rumi-histogram"] = "#4CAF50";    // 柱状图绿色
            sd.ColorDic["fast-ema"] = "#E91E63";          // 快速EMA粉色
            sd.ColorDic["slow-ema"] = "#9C27B0";          // 慢速EMA紫色
            sd.ColorDic["trend-ema"] = "#607D8B";         // 趋势EMA灰色
            sd.ColorDic["atr"] = "#795548";               // ATR棕色
            sd.ColorDic["stop-loss"] = "#F44336";         // 止损线红色
            sd.ColorDic["take-profit"] = "#4CAF50";       // 止盈线绿色

            return sd;
        }

        #region 内部类定义

        /// <summary>
        /// RUMI指标数据
        /// </summary>
        private class RumiData
        {
            public decimal RUMI { get; set; }             // RUMI值
            public decimal Signal { get; set; }           // 信号线值
            public decimal Histogram { get; set; }        // 柱状图值
            public decimal PrevRUMI { get; set; }         // 前一个RUMI值
            public decimal PrevSignal { get; set; }       // 前一个信号线值
        }

        /// <summary>
        /// 持仓状态
        /// </summary>
        private class RumiState
        {
            public int Direction { get; set; }            // 0:空仓 1:多头 -1:空头
            public decimal EntryPrice { get; set; }       // 入场价格
            public decimal Num { get; set; }              // 持仓数量
            public decimal StopPrice { get; set; }        // 止损价格
            public decimal TakeProfitPrice { get; set; }  // 止盈价格
            public decimal EntryATR { get; set; }         // 入场时的ATR
            public decimal HighestSinceEntry { get; set; } // 入场后最高价（用于移动止损）
            public decimal LowestSinceEntry { get; set; }  // 入场后最低价（用于移动止损）
            public DateTime EntryTime { get; set; }       // 入场时间
            public int SignalConfirmCount { get; set; }   // 信号确认计数
            public int PendingSignal { get; set; }        // 待确认信号 0:无 1:多 -1:空
        }

        #endregion

        #region 状态存储

        private Dictionary<string, RumiState> _stateDic = new Dictionary<string, RumiState>();
        private Dictionary<string, List<decimal>> _rumiHistoryDic = new Dictionary<string, List<decimal>>();
        private Dictionary<string, List<decimal>> _signalHistoryDic = new Dictionary<string, List<decimal>>();
        private Dictionary<string, List<decimal>> _atrHistoryDic = new Dictionary<string, List<decimal>>();
        private Dictionary<string, List<decimal>> _volumeHistoryDic = new Dictionary<string, List<decimal>>();

        #endregion

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 获取参数
            int rumiPeriod = (int)ArgDic["rumiPeriod"];
            int rumiSmooth = (int)ArgDic["rumiSmooth"];
            int signalPeriod = (int)ArgDic["signalPeriod"];
            double rumiThreshold = Convert.ToDouble(ArgDic["rumiThreshold"]);
            int fastEMA = (int)ArgDic["fastEMA"];
            int slowEMA = (int)ArgDic["slowEMA"];
            int trendEMA = (int)ArgDic["trendEMA"];
            int useTrendFilter = (int)ArgDic["useTrendFilter"];
            int atrPeriod = (int)ArgDic["atrPeriod"];
            double atrStopMultiplier = Convert.ToDouble(ArgDic["atrStopMultiplier"]);
            double atrProfitMultiplier = Convert.ToDouble(ArgDic["atrProfitMultiplier"]);
            int useTrailingStop = (int)ArgDic["useTrailingStop"];
            double trailingATR = Convert.ToDouble(ArgDic["trailingATR"]);
            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];
            int confirmBars = (int)ArgDic["confirmBars"];
            int useVolumeFilter = (int)ArgDic["useVolumeFilter"];
            double volumeMultiplier = Convert.ToDouble(ArgDic["volumeMultiplier"]);

            // 确保有足够的数据
            int requiredBars = Math.Max(Math.Max(rumiPeriod + rumiSmooth + signalPeriod, slowEMA), trendEMA) + 5;
            if (tu.QuoteList.Count < requiredBars) return;

            var q = tu.QuoteList.Last();
            string stateKey = tu.GetStateKey();

            // 计算指标
            var atrResult = tu.QuoteList.GetAtr(atrPeriod).ToList();
            var fastEmaResult = tu.QuoteList.GetEma(fastEMA).ToList();
            var slowEmaResult = tu.QuoteList.GetEma(slowEMA).ToList();
            var trendEmaResult = tu.QuoteList.GetEma(trendEMA).ToList();

            if (atrResult.Count == 0 || fastEmaResult.Count == 0 || slowEmaResult.Count == 0) return;

            decimal atr = (decimal)(atrResult.Last().Atr ?? 0);
            decimal fastEmaValue = (decimal)(fastEmaResult.Last().Ema ?? 0);
            decimal slowEmaValue = (decimal)(slowEmaResult.Last().Ema ?? 0);
            decimal trendEmaValue = (decimal)(trendEmaResult.Last().Ema ?? 0);
            decimal prevFastEma = fastEmaResult.Count > 1 ? (decimal)(fastEmaResult[fastEmaResult.Count - 2].Ema ?? 0) : fastEmaValue;
            decimal prevSlowEma = slowEmaResult.Count > 1 ? (decimal)(slowEmaResult[slowEmaResult.Count - 2].Ema ?? 0) : slowEmaValue;

            if (atr <= 0) return;

            // 计算RUMI指标
            RumiData rumiData = CalculateRUMI(stateKey, tu.QuoteList, rumiPeriod, rumiSmooth, signalPeriod, atr);
            if (rumiData == null) return;

            // 更新成交量历史
            UpdateVolumeHistory(stateKey, q.Volume);
            decimal avgVolume = GetAverageVolume(stateKey, 20);

            // 绘制指标
            PlotIndicators(rumiData, fastEmaValue, slowEmaValue, trendEmaValue, atr);

            // 计算单位头寸
            decimal unitSize = CalculateUnitSize(tu, q, atr);
            if (unitSize <= 0) return;

            // 获取或创建状态
            RumiState state = GetOrCreateState(stateKey);

            // 执行交易逻辑
            ExecuteRumiLogic(tu, period, q, state, unitSize, atr, rumiData,
                fastEmaValue, slowEmaValue, trendEmaValue, prevFastEma, prevSlowEma,
                mode, sendMode, useTrendFilter, rumiThreshold,
                atrStopMultiplier, atrProfitMultiplier,
                useTrailingStop, trailingATR,
                confirmBars, useVolumeFilter, volumeMultiplier, avgVolume);
        }

        #region RUMI指标计算

        /// <summary>
        /// 计算RUMI指标
        /// RUMI = SMA(RelativeMomentum, SmoothPeriod)
        /// RelativeMomentum = (Close - SMA(Close, Period)) / ATR
        /// Signal = SMA(RUMI, SignalPeriod)
        /// </summary>
        private RumiData CalculateRUMI(string stateKey, List<SkQuote> quoteList, 
            int period, int smooth, int signalPeriod, decimal atr)
        {
            if (!_rumiHistoryDic.ContainsKey(stateKey))
            {
                _rumiHistoryDic[stateKey] = new List<decimal>();
            }
            if (!_signalHistoryDic.ContainsKey(stateKey))
            {
                _signalHistoryDic[stateKey] = new List<decimal>();
            }

            var rumiHistory = _rumiHistoryDic[stateKey];
            var signalHistory = _signalHistoryDic[stateKey];

            // 计算SMA
            var smaResult = quoteList.GetSma(period).ToList();
            if (smaResult.Count == 0) return null;

            decimal sma = (decimal)(smaResult.Last().Sma ?? 0);
            decimal close = quoteList.Last().Close;

            // 计算相对动量
            decimal relativeMomentum = atr > 0 ? (close - sma) / atr : 0;

            // 添加到历史
            rumiHistory.Add(relativeMomentum);

            // 保持历史长度
            int maxHistory = Math.Max(smooth, signalPeriod) * 3;
            while (rumiHistory.Count > maxHistory)
            {
                rumiHistory.RemoveAt(0);
            }

            // 计算RUMI（平滑后的相对动量）
            decimal rumi = 0;
            if (rumiHistory.Count >= smooth)
            {
                rumi = rumiHistory.Skip(rumiHistory.Count - smooth).Take(smooth).Average();
            }
            else
            {
                rumi = rumiHistory.Average();
            }

            // 添加RUMI到信号历史
            signalHistory.Add(rumi);
            while (signalHistory.Count > maxHistory)
            {
                signalHistory.RemoveAt(0);
            }

            // 计算信号线
            decimal signal = 0;
            if (signalHistory.Count >= signalPeriod)
            {
                signal = signalHistory.Skip(signalHistory.Count - signalPeriod).Take(signalPeriod).Average();
            }
            else
            {
                signal = signalHistory.Average();
            }

            // 获取前值
            decimal prevRumi = signalHistory.Count > 1 ? signalHistory[signalHistory.Count - 2] : rumi;
            decimal prevSignal = signalHistory.Count > signalPeriod + 1 
                ? signalHistory.Skip(signalHistory.Count - signalPeriod - 1).Take(signalPeriod).Average()
                : signal;

            return new RumiData
            {
                RUMI = rumi,
                Signal = signal,
                Histogram = rumi - signal,
                PrevRUMI = prevRumi,
                PrevSignal = prevSignal
            };
        }

        #endregion

        #region 成交量分析

        /// <summary>
        /// 更新成交量历史
        /// </summary>
        private void UpdateVolumeHistory(string stateKey, decimal volume)
        {
            if (!_volumeHistoryDic.ContainsKey(stateKey))
            {
                _volumeHistoryDic[stateKey] = new List<decimal>();
            }

            var volumeHistory = _volumeHistoryDic[stateKey];
            volumeHistory.Add(volume);

            while (volumeHistory.Count > 50)
            {
                volumeHistory.RemoveAt(0);
            }
        }

        /// <summary>
        /// 获取平均成交量
        /// </summary>
        private decimal GetAverageVolume(string stateKey, int period)
        {
            if (!_volumeHistoryDic.ContainsKey(stateKey)) return 0;

            var volumeHistory = _volumeHistoryDic[stateKey];
            if (volumeHistory.Count < period) return volumeHistory.Count > 0 ? volumeHistory.Average() : 0;

            return volumeHistory.Skip(volumeHistory.Count - period).Take(period).Average();
        }

        #endregion

        #region 仓位计算

        /// <summary>
        /// 计算单位头寸大小
        /// </summary>
        private decimal CalculateUnitSize(TableUnit tu, SkQuote q, decimal atr)
        {
            int lotsMode = (int)ArgDic["lotsMode"];

            if (lotsMode == 1)
            {
                return (decimal)ArgDic["fixedLots"];
            }

            decimal accountEquity = (decimal)ArgDic["accountEquity"];
            double riskPerTrade = Convert.ToDouble(ArgDic["riskPerTrade"]);
            double atrStopMultiplier = Convert.ToDouble(ArgDic["atrStopMultiplier"]);

            var symbol = GetSymbol(tu.MktSymbol);
            decimal multiplier = symbol.multiplier;

            // 风险金额 = 账户权益 × 风险比例
            // 单位头寸 = 风险金额 / (ATR × 止损倍数 × 合约乘数)
            decimal riskAmount = accountEquity * (decimal)riskPerTrade;
            decimal stopDistance = atr * (decimal)atrStopMultiplier;
            decimal dollarRisk = stopDistance * multiplier;

            if (dollarRisk <= 0) return 0;

            decimal unitSize = riskAmount / dollarRisk;

            // 根据品种类型取整
            if (symbol.symbol_type == (int)SymbolType.COIN)
            {
                unitSize = Math.Floor(unitSize * 1000) / 1000.0m;
            }
            else
            {
                unitSize = Math.Floor(unitSize);
            }

            return Math.Max(unitSize, 0);
        }

        #endregion

        #region 绘图

        /// <summary>
        /// 绘制指标
        /// </summary>
        private void PlotIndicators(RumiData rumiData, decimal fastEma, decimal slowEma, 
            decimal trendEma, decimal atr)
        {
            // 主图绘制均线
            Plot("main", "FastEMA", PlotType.LINE, (double)fastEma);
            Plot("main", "SlowEMA", PlotType.LINE, (double)slowEma);
            Plot("main", "TrendEMA", PlotType.LINE, (double)trendEma);

            // 副图1绘制RUMI
            Plot("sub0", "RUMI", PlotType.LINE, (double)rumiData.RUMI);
            Plot("sub0", "Signal", PlotType.LINE, (double)rumiData.Signal);
            Plot("sub0", "Histogram", PlotType.RECTANGLE, (double)rumiData.Histogram);
            Plot("sub0", "Zero", PlotType.LINE, 0);

            // 副图2绘制ATR
            Plot("sub1", "ATR", PlotType.LINE, (double)atr);
        }

        #endregion

        #region 状态管理

        /// <summary>
        /// 获取或创建状态
        /// </summary>
        private RumiState GetOrCreateState(string stateKey)
        {
            if (!_stateDic.ContainsKey(stateKey))
            {
                _stateDic[stateKey] = new RumiState();
            }
            return _stateDic[stateKey];
        }

        #endregion

        #region 交易逻辑

        /// <summary>
        /// 执行RUMI交易逻辑
        /// </summary>
        private void ExecuteRumiLogic(TableUnit tu, Period period, SkQuote q, RumiState state,
            decimal unitSize, decimal atr, RumiData rumiData,
            decimal fastEma, decimal slowEma, decimal trendEma, decimal prevFastEma, decimal prevSlowEma,
            int mode, int sendMode, int useTrendFilter, double rumiThreshold,
            double atrStopMultiplier, double atrProfitMultiplier,
            int useTrailingStop, double trailingATR,
            int confirmBars, int useVolumeFilter, double volumeMultiplier, decimal avgVolume)
        {
            // 空仓状态
            if (state.Direction == 0)
            {
                HandleEntrySignal(tu, period, q, state, unitSize, atr, rumiData,
                    fastEma, slowEma, trendEma, prevFastEma, prevSlowEma,
                    mode, sendMode, useTrendFilter, rumiThreshold,
                    atrStopMultiplier, atrProfitMultiplier,
                    confirmBars, useVolumeFilter, volumeMultiplier, avgVolume);
            }
            // 持仓状态
            else
            {
                HandlePosition(tu, period, q, state, atr, rumiData,
                    fastEma, slowEma, prevFastEma, prevSlowEma,
                    sendMode, useTrailingStop, trailingATR);
            }
        }

        /// <summary>
        /// 处理入场信号
        /// </summary>
        private void HandleEntrySignal(TableUnit tu, Period period, SkQuote q, RumiState state,
            decimal unitSize, decimal atr, RumiData rumiData,
            decimal fastEma, decimal slowEma, decimal trendEma, decimal prevFastEma, decimal prevSlowEma,
            int mode, int sendMode, int useTrendFilter, double rumiThreshold,
            double atrStopMultiplier, double atrProfitMultiplier,
            int confirmBars, int useVolumeFilter, double volumeMultiplier, decimal avgVolume)
        {
            // 检测RUMI交叉信号
            bool rumiCrossUp = rumiData.PrevRUMI <= rumiData.PrevSignal && rumiData.RUMI > rumiData.Signal;
            bool rumiCrossDown = rumiData.PrevRUMI >= rumiData.PrevSignal && rumiData.RUMI < rumiData.Signal;

            // 检测均线交叉
            bool emaCrossUp = prevFastEma <= prevSlowEma && fastEma > slowEma;
            bool emaCrossDown = prevFastEma >= prevSlowEma && fastEma < slowEma;

            // 趋势条件
            bool upTrend = fastEma > slowEma;
            bool downTrend = fastEma < slowEma;

            // 趋势过滤条件
            bool trendFilterLong = useTrendFilter == 0 || q.Close > trendEma;
            bool trendFilterShort = useTrendFilter == 0 || q.Close < trendEma;

            // RUMI强度过滤
            bool rumiStrengthLong = rumiData.RUMI > (decimal)rumiThreshold;
            bool rumiStrengthShort = rumiData.RUMI < -(decimal)rumiThreshold;

            // 成交量过滤
            bool volumeOk = useVolumeFilter == 0 || q.Volume > avgVolume * (decimal)volumeMultiplier;

            // 做多信号
            bool longSignal = (rumiCrossUp || emaCrossUp) && upTrend && trendFilterLong && rumiStrengthLong && volumeOk;
            
            // 做空信号
            bool shortSignal = (rumiCrossDown || emaCrossDown) && downTrend && trendFilterShort && rumiStrengthShort && volumeOk;

            // 信号确认逻辑
            if (confirmBars > 1)
            {
                if (longSignal && mode != 2)
                {
                    if (state.PendingSignal == 1)
                    {
                        state.SignalConfirmCount++;
                        if (state.SignalConfirmCount >= confirmBars)
                        {
                            ExecuteLongEntry(tu, period, q, state, unitSize, atr, sendMode, atrStopMultiplier, atrProfitMultiplier);
                        }
                    }
                    else
                    {
                        state.PendingSignal = 1;
                        state.SignalConfirmCount = 1;
                    }
                }
                else if (shortSignal && mode != 1)
                {
                    if (state.PendingSignal == -1)
                    {
                        state.SignalConfirmCount++;
                        if (state.SignalConfirmCount >= confirmBars)
                        {
                            ExecuteShortEntry(tu, period, q, state, unitSize, atr, sendMode, atrStopMultiplier, atrProfitMultiplier);
                        }
                    }
                    else
                    {
                        state.PendingSignal = -1;
                        state.SignalConfirmCount = 1;
                    }
                }
                else
                {
                    // 信号消失，重置确认
                    state.PendingSignal = 0;
                    state.SignalConfirmCount = 0;
                }
            }
            else
            {
                // 无需确认，直接入场
                if (longSignal && mode != 2)
                {
                    ExecuteLongEntry(tu, period, q, state, unitSize, atr, sendMode, atrStopMultiplier, atrProfitMultiplier);
                }
                else if (shortSignal && mode != 1)
                {
                    ExecuteShortEntry(tu, period, q, state, unitSize, atr, sendMode, atrStopMultiplier, atrProfitMultiplier);
                }
            }
        }

        /// <summary>
        /// 执行多头入场
        /// </summary>
        private void ExecuteLongEntry(TableUnit tu, Period period, SkQuote q, RumiState state,
            decimal unitSize, decimal atr, int sendMode, double atrStopMultiplier, double atrProfitMultiplier)
        {
            decimal entryPrice = q.Close;
            decimal stopPrice = entryPrice - (decimal)atrStopMultiplier * atr;
            decimal takeProfitPrice = entryPrice + (decimal)atrProfitMultiplier * atr;

            state.Direction = 1;
            state.EntryPrice = entryPrice;
            state.Num = unitSize;
            state.StopPrice = stopPrice;
            state.TakeProfitPrice = takeProfitPrice;
            state.EntryATR = atr;
            state.HighestSinceEntry = q.High;
            state.LowestSinceEntry = q.Low;
            state.EntryTime = q.Date;
            state.PendingSignal = 0;
            state.SignalConfirmCount = 0;

            Trade(tu.MktSymbol, OrderType.BUY, entryPrice, unitSize, period, sendMode);

            // 绘制止损止盈线
            Plot("main", "StopLoss", PlotType.LINE, (double)stopPrice);
            Plot("main", "TakeProfit", PlotType.LINE, (double)takeProfitPrice);
        }

        /// <summary>
        /// 执行空头入场
        /// </summary>
        private void ExecuteShortEntry(TableUnit tu, Period period, SkQuote q, RumiState state,
            decimal unitSize, decimal atr, int sendMode, double atrStopMultiplier, double atrProfitMultiplier)
        {
            decimal entryPrice = q.Close;
            decimal stopPrice = entryPrice + (decimal)atrStopMultiplier * atr;
            decimal takeProfitPrice = entryPrice - (decimal)atrProfitMultiplier * atr;

            state.Direction = -1;
            state.EntryPrice = entryPrice;
            state.Num = unitSize;
            state.StopPrice = stopPrice;
            state.TakeProfitPrice = takeProfitPrice;
            state.EntryATR = atr;
            state.HighestSinceEntry = q.High;
            state.LowestSinceEntry = q.Low;
            state.EntryTime = q.Date;
            state.PendingSignal = 0;
            state.SignalConfirmCount = 0;

            Trade(tu.MktSymbol, OrderType.SELL, entryPrice, unitSize, period, sendMode);

            // 绘制止损止盈线
            Plot("main", "StopLoss", PlotType.LINE, (double)stopPrice);
            Plot("main", "TakeProfit", PlotType.LINE, (double)takeProfitPrice);
        }

        /// <summary>
        /// 处理持仓
        /// </summary>
        private void HandlePosition(TableUnit tu, Period period, SkQuote q, RumiState state,
            decimal atr, RumiData rumiData, decimal fastEma, decimal slowEma,
            decimal prevFastEma, decimal prevSlowEma, int sendMode,
            int useTrailingStop, double trailingATR)
        {
            if (state.Direction == 1)
            {
                HandleLongPosition(tu, period, q, state, atr, rumiData, fastEma, slowEma,
                    prevFastEma, prevSlowEma, sendMode, useTrailingStop, trailingATR);
            }
            else if (state.Direction == -1)
            {
                HandleShortPosition(tu, period, q, state, atr, rumiData, fastEma, slowEma,
                    prevFastEma, prevSlowEma, sendMode, useTrailingStop, trailingATR);
            }
        }

        /// <summary>
        /// 处理多头持仓
        /// </summary>
        private void HandleLongPosition(TableUnit tu, Period period, SkQuote q, RumiState state,
            decimal atr, RumiData rumiData, decimal fastEma, decimal slowEma,
            decimal prevFastEma, decimal prevSlowEma, int sendMode,
            int useTrailingStop, double trailingATR)
        {
            // 更新最高价
            if (q.High > state.HighestSinceEntry)
            {
                state.HighestSinceEntry = q.High;

                // 移动止损
                if (useTrailingStop == 1)
                {
                    decimal newStop = state.HighestSinceEntry - (decimal)trailingATR * atr;
                    if (newStop > state.StopPrice)
                    {
                        state.StopPrice = newStop;
                    }
                }
            }

            // 检查止损
            if (q.Low <= state.StopPrice)
            {
                ClosePosition(tu, period, q, state, state.StopPrice, sendMode, "止损");
                return;
            }

            // 检查止盈
            if (q.High >= state.TakeProfitPrice)
            {
                ClosePosition(tu, period, q, state, state.TakeProfitPrice, sendMode, "止盈");
                return;
            }

            // 检查RUMI反向交叉
            bool rumiCrossDown = rumiData.PrevRUMI >= rumiData.PrevSignal && rumiData.RUMI < rumiData.Signal;
            if (rumiCrossDown)
            {
                ClosePosition(tu, period, q, state, q.Close, sendMode, "RUMI反转");
                return;
            }

            // 检查均线死叉
            bool emaCrossDown = prevFastEma >= prevSlowEma && fastEma < slowEma;
            if (emaCrossDown)
            {
                ClosePosition(tu, period, q, state, q.Close, sendMode, "均线死叉");
                return;
            }

            // 绘制止损止盈线
            Plot("main", "StopLoss", PlotType.LINE, (double)state.StopPrice);
            Plot("main", "TakeProfit", PlotType.LINE, (double)state.TakeProfitPrice);
        }

        /// <summary>
        /// 处理空头持仓
        /// </summary>
        private void HandleShortPosition(TableUnit tu, Period period, SkQuote q, RumiState state,
            decimal atr, RumiData rumiData, decimal fastEma, decimal slowEma,
            decimal prevFastEma, decimal prevSlowEma, int sendMode,
            int useTrailingStop, double trailingATR)
        {
            // 更新最低价
            if (q.Low < state.LowestSinceEntry)
            {
                state.LowestSinceEntry = q.Low;

                // 移动止损
                if (useTrailingStop == 1)
                {
                    decimal newStop = state.LowestSinceEntry + (decimal)trailingATR * atr;
                    if (newStop < state.StopPrice)
                    {
                        state.StopPrice = newStop;
                    }
                }
            }

            // 检查止损
            if (q.High >= state.StopPrice)
            {
                ClosePosition(tu, period, q, state, state.StopPrice, sendMode, "止损");
                return;
            }

            // 检查止盈
            if (q.Low <= state.TakeProfitPrice)
            {
                ClosePosition(tu, period, q, state, state.TakeProfitPrice, sendMode, "止盈");
                return;
            }

            // 检查RUMI反向交叉
            bool rumiCrossUp = rumiData.PrevRUMI <= rumiData.PrevSignal && rumiData.RUMI > rumiData.Signal;
            if (rumiCrossUp)
            {
                ClosePosition(tu, period, q, state, q.Close, sendMode, "RUMI反转");
                return;
            }

            // 检查均线金叉
            bool emaCrossUp = prevFastEma <= prevSlowEma && fastEma > slowEma;
            if (emaCrossUp)
            {
                ClosePosition(tu, period, q, state, q.Close, sendMode, "均线金叉");
                return;
            }

            // 绘制止损止盈线
            Plot("main", "StopLoss", PlotType.LINE, (double)state.StopPrice);
            Plot("main", "TakeProfit", PlotType.LINE, (double)state.TakeProfitPrice);
        }

        /// <summary>
        /// 平仓
        /// </summary>
        private void ClosePosition(TableUnit tu, Period period, SkQuote q, RumiState state,
            decimal exitPrice, int sendMode, string reason)
        {
            if (state.Direction == 1)
            {
                Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, exitPrice, state.Num, period, sendMode);
            }
            else if (state.Direction == -1)
            {
                Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, exitPrice, state.Num, period, sendMode);
            }

            // 重置状态
            state.Direction = 0;
            state.EntryPrice = 0;
            state.Num = 0;
            state.StopPrice = 0;
            state.TakeProfitPrice = 0;
            state.EntryATR = 0;
            state.HighestSinceEntry = 0;
            state.LowestSinceEntry = 0;
            state.PendingSignal = 0;
            state.SignalConfirmCount = 0;
        }

        #endregion
    }
}
