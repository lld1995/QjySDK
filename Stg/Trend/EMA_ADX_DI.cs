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
    /// EMA_ADX_DI 趋势动量策略
    /// 
    /// 策略核心思想：
    /// 利用 DI 交叉作为主要入场信号，EMA 作为趋势过滤器，ADX 确认趋势强度。
    /// 当 DI 发生金叉/死叉且趋势方向与 EMA 一致时入场。
    /// 
    /// 指标体系：
    /// 1. +DI/-DI (Directional Indicators)：
    ///    - +DI 上穿 -DI 产生做多信号
    ///    - -DI 上穿 +DI 产生做空信号
    ///    - DI 差值越大，趋势越明确
    /// 
    /// 2. ADX (Average Directional Index)：
    ///    - ADX > 入场阈值：趋势足够强，允许入场
    ///    - ADX > 强势阈值：趋势非常强，可加仓或持仓
    ///    - ADX 拐头向下：趋势可能减弱，考虑减仓
    /// 
    /// 3. EMA (Exponential Moving Average)：
    ///    - 价格在 EMA 之上：只做多
    ///    - 价格在 EMA 之下：只做空
    ///    - EMA 斜率用于确认趋势方向
    /// 
    /// 入场条件：
    /// 【做多】
    ///    - +DI 上穿 -DI（DI金叉）
    ///    - ADX > 入场阈值（趋势确立）
    ///    - 价格 > EMA（趋势过滤）
    ///    - DI差值 > 最小差值阈值（信号强度）
    /// 
    /// 【做空】
    ///    - -DI 上穿 +DI（DI死叉）
    ///    - ADX > 入场阈值（趋势确立）
    ///    - 价格 < EMA（趋势过滤）
    ///    - DI差值 > 最小差值阈值（信号强度）
    /// 
    /// 出场条件：
    /// 1. DI反向交叉：趋势反转信号
    /// 2. ADX跌破退出阈值：趋势消失
    /// 3. 价格突破EMA反向：趋势结构破坏
    /// 4. ATR动态止损/止盈
    /// 5. 最大持仓时间限制
    /// </summary>
    public class EMA_ADX_DI : StgBase
    {
        // 参数字段
        private int _emaPeriod;
        private int _adxPeriod;
        private decimal _adxEntryThreshold;
        private decimal _adxExitThreshold;
        private decimal _diDiffThreshold;
        private int _atrPeriod;
        private decimal _stopLossAtrMultiplier;
        private decimal _takeProfitAtrMultiplier;
        private decimal _tradeAmount;
        private bool _useTrailingStop;
        private decimal _trailingStopAtrMultiplier;
        private int _maxHoldBars;
        private bool _useEmaSlopeFilter;
        private int _emaSlopePeriod;

        // 状态管理
        private Dictionary<string, TradeState> _stateDict = new Dictionary<string, TradeState>();

        public EMA_ADX_DI()
        {
        }

        public EMA_ADX_DI(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 1;
            sd.UseGlobalCalc = 0;

            // EMA参数
            sd.ArgDescDic["EmaPeriod"] = new ArgDesc { Text = "EMA周期", Explain = "趋势过滤EMA的计算周期" };
            sd.ArgDic["EmaPeriod"] = 20;

            // ADX/DI参数
            sd.ArgDescDic["AdxPeriod"] = new ArgDesc { Text = "ADX周期", Explain = "ADX和DI指标的计算周期" };
            sd.ArgDic["AdxPeriod"] = 14;

            sd.ArgDescDic["AdxEntryThreshold"] = new ArgDesc { Text = "ADX入场阈值", Explain = "ADX大于此值时允许入场（建议20-30）" };
            sd.ArgDic["AdxEntryThreshold"] = 20.0;

            sd.ArgDescDic["AdxExitThreshold"] = new ArgDesc { Text = "ADX退出阈值", Explain = "ADX低于此值时强制平仓（建议15-20）" };
            sd.ArgDic["AdxExitThreshold"] = 15.0;

            sd.ArgDescDic["DiDiffThreshold"] = new ArgDesc { Text = "DI差值阈值", Explain = "+DI与-DI的最小差值，用于过滤弱信号" };
            sd.ArgDic["DiDiffThreshold"] = 5.0;

            // ATR止损止盈参数
            sd.ArgDescDic["AtrPeriod"] = new ArgDesc { Text = "ATR周期", Explain = "ATR指标的计算周期" };
            sd.ArgDic["AtrPeriod"] = 14;

            sd.ArgDescDic["StopLossAtrMultiplier"] = new ArgDesc { Text = "止损ATR倍数", Explain = "止损距离 = ATR × 此倍数" };
            sd.ArgDic["StopLossAtrMultiplier"] = 2.0;

            sd.ArgDescDic["TakeProfitAtrMultiplier"] = new ArgDesc { Text = "止盈ATR倍数", Explain = "止盈距离 = ATR × 此倍数" };
            sd.ArgDic["TakeProfitAtrMultiplier"] = 3.0;

            // 交易参数
            sd.ArgDescDic["TradeAmount"] = new ArgDesc { Text = "交易数量", Explain = "每次交易的数量/比例" };
            sd.ArgDic["TradeAmount"] = 1.0;

            sd.ArgDescDic["UseTrailingStop"] = new ArgDesc { Text = "启用移动止损", Explain = "是否启用移动止损(1=启用,0=禁用)" };
            sd.ArgDic["UseTrailingStop"] = 1;

            sd.ArgDescDic["TrailingStopAtrMultiplier"] = new ArgDesc { Text = "移动止损ATR倍数", Explain = "移动止损距离 = ATR × 此倍数" };
            sd.ArgDic["TrailingStopAtrMultiplier"] = 1.5;

            sd.ArgDescDic["MaxHoldBars"] = new ArgDesc { Text = "最大持仓K线数", Explain = "持仓超过此数量K线强制平仓（0=不限制）" };
            sd.ArgDic["MaxHoldBars"] = 0;

            // EMA斜率过滤
            sd.ArgDescDic["UseEmaSlopeFilter"] = new ArgDesc { Text = "启用EMA斜率过滤", Explain = "是否使用EMA斜率确认趋势(1=启用,0=禁用)" };
            sd.ArgDic["UseEmaSlopeFilter"] = 1;

            sd.ArgDescDic["EmaSlopePeriod"] = new ArgDesc { Text = "EMA斜率周期", Explain = "计算EMA斜率的回看周期" };
            sd.ArgDic["EmaSlopePeriod"] = 3;

            // 颜色配置
            sd.ColorDic["main-EMA"] = "#2196F3";
            sd.ColorDic["sub0-ADX"] = "#FF9800";
            sd.ColorDic["sub0-PDI"] = "#4CAF50";
            sd.ColorDic["sub0-MDI"] = "#F44336";
            sd.ColorDic["main-StopLoss"] = "#E91E63";
            sd.ColorDic["main-TakeProfit"] = "#8BC34A";
            sd.ColorDic["sub0-DiDiff"] = "#9C27B0";

            // 副图中线
            sd.MidValDic["sub0"] = 20;

            return sd;
        }

        private void InitParams()
        {
            _emaPeriod = Convert.ToInt32(ArgDic["EmaPeriod"]);
            _adxPeriod = Convert.ToInt32(ArgDic["AdxPeriod"]);
            _adxEntryThreshold = Convert.ToDecimal(ArgDic["AdxEntryThreshold"]);
            _adxExitThreshold = Convert.ToDecimal(ArgDic["AdxExitThreshold"]);
            _diDiffThreshold = Convert.ToDecimal(ArgDic["DiDiffThreshold"]);
            _atrPeriod = Convert.ToInt32(ArgDic["AtrPeriod"]);
            _stopLossAtrMultiplier = Convert.ToDecimal(ArgDic["StopLossAtrMultiplier"]);
            _takeProfitAtrMultiplier = Convert.ToDecimal(ArgDic["TakeProfitAtrMultiplier"]);
            _tradeAmount = Convert.ToDecimal(ArgDic["TradeAmount"]);
            _useTrailingStop = Convert.ToInt32(ArgDic["UseTrailingStop"]) == 1;
            _trailingStopAtrMultiplier = Convert.ToDecimal(ArgDic["TrailingStopAtrMultiplier"]);
            _maxHoldBars = Convert.ToInt32(ArgDic["MaxHoldBars"]);
            _useEmaSlopeFilter = Convert.ToInt32(ArgDic["UseEmaSlopeFilter"]) == 1;
            _emaSlopePeriod = Convert.ToInt32(ArgDic["EmaSlopePeriod"]);
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (ArgDic == null) return;

            InitParams();

            var quotes = tu.QuoteList;
            int minBars = Math.Max(_emaPeriod, _adxPeriod) + _emaSlopePeriod + 5;
            if (quotes == null || quotes.Count < minBars) return;

            var stateKey = tu.GetStateKey();
            if (!_stateDict.ContainsKey(stateKey))
            {
                _stateDict[stateKey] = new TradeState();
            }
            var state = _stateDict[stateKey];

            // 计算指标
            var emaList = quotes.GetEma(_emaPeriod).ToList();
            var adxList = quotes.GetAdx(_adxPeriod).ToList();

            int lastIdx = quotes.Count - 1;
            int prevIdx = lastIdx - 1;

            var emaCurr = emaList[lastIdx].Ema;
            var emaPrev = emaList[prevIdx].Ema;
            var emaSlopeRef = _emaSlopePeriod > 0 && lastIdx >= _emaSlopePeriod 
                ? emaList[lastIdx - _emaSlopePeriod].Ema 
                : emaPrev;

            var adxCurr = adxList[lastIdx];
            var adxPrev = adxList[prevIdx];

            if (!emaCurr.HasValue || !adxCurr.Adx.HasValue || 
                !adxCurr.Pdi.HasValue || !adxCurr.Mdi.HasValue ||
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
            decimal ema = (decimal)emaCurr.Value;
            double adxValue = adxCurr.Adx.Value;
            double pdiCurr = adxCurr.Pdi.Value;
            double mdiCurr = adxCurr.Mdi.Value;
            double pdiPrev = adxPrev.Pdi.Value;
            double mdiPrev = adxPrev.Mdi.Value;

            // 计算DI差值
            double diDiff = pdiCurr - mdiCurr;

            // 计算EMA斜率
            double emaSlope = emaSlopeRef.HasValue ? (emaCurr.Value - emaSlopeRef.Value) : 0;

            if (!isFinal) return;

            // 绘制指标
            Plot("main", "EMA", PlotType.CURVE, emaCurr);
            Plot("sub0", "ADX", PlotType.CURVE, adxValue);
            Plot("sub0", "PDI", PlotType.CURVE, pdiCurr);
            Plot("sub0", "MDI", PlotType.CURVE, mdiCurr);

            // 绘制止损止盈线
            if (state.HasPosition)
            {
                Plot("main", "StopLoss", PlotType.LINE, (double)state.StopLoss);
                Plot("main", "TakeProfit", PlotType.LINE, (double)state.TakeProfit);
            }

            // 更新持仓计数
            if (state.HasPosition)
            {
                state.HoldBars++;
            }

            // 检测DI交叉
            bool diGoldenCross = pdiPrev <= mdiPrev && pdiCurr > mdiCurr;  // +DI上穿-DI
            bool diDeathCross = mdiPrev <= pdiPrev && mdiCurr > pdiCurr;   // -DI上穿+DI

            if (state.HasPosition)
            {
                HandleExitLogic(state, tu.MktSymbol, period, currentPrice, currentHigh, currentLow,
                    atr, adxValue, pdiCurr, mdiCurr, diGoldenCross, diDeathCross, ema);
            }
            else
            {
                HandleEntryLogic(state, tu.MktSymbol, period, currentPrice, currentHigh, currentLow,
                    atr, adxValue, pdiCurr, mdiCurr, diDiff, diGoldenCross, diDeathCross, ema, emaSlope);
            }
        }

        private void HandleEntryLogic(TradeState state, string mktSymbol, Period period,
            decimal currentPrice, decimal currentHigh, decimal currentLow,
            decimal atr, double adxValue, double pdi, double mdi, double diDiff,
            bool diGoldenCross, bool diDeathCross, decimal ema, double emaSlope)
        {
            // ADX必须大于入场阈值
            if (adxValue < (double)_adxEntryThreshold) return;

            // DI差值必须大于阈值
            if (Math.Abs(diDiff) < (double)_diDiffThreshold) return;

            // 做多条件
            if (diGoldenCross && currentPrice > ema)
            {
                // EMA斜率过滤：斜率必须向上
                if (_useEmaSlopeFilter && emaSlope <= 0) return;

                OpenLongPosition(state, mktSymbol, currentPrice, atr, period);
            }
            // 做空条件
            else if (diDeathCross && currentPrice < ema)
            {
                // EMA斜率过滤：斜率必须向下
                if (_useEmaSlopeFilter && emaSlope >= 0) return;

                OpenShortPosition(state, mktSymbol, currentPrice, atr, period);
            }
        }

        private void HandleExitLogic(TradeState state, string mktSymbol, Period period,
            decimal currentPrice, decimal currentHigh, decimal currentLow,
            decimal atr, double adxValue, double pdi, double mdi,
            bool diGoldenCross, bool diDeathCross, decimal ema)
        {
            // 最大持仓时间检查
            if (_maxHoldBars > 0 && state.HoldBars >= _maxHoldBars)
            {
                ClosePosition(state, mktSymbol, currentPrice, period, "MaxHoldBars");
                return;
            }

            if (state.IsLong)
            {
                // 更新最高价
                if (currentHigh > state.HighestPrice)
                {
                    state.HighestPrice = currentHigh;
                }

                // 止损检查
                if (currentLow <= state.StopLoss)
                {
                    ClosePosition(state, mktSymbol, state.StopLoss, period, "StopLoss");
                    return;
                }

                // 止盈检查
                if (currentHigh >= state.TakeProfit)
                {
                    ClosePosition(state, mktSymbol, state.TakeProfit, period, "TakeProfit");
                    return;
                }

                // DI死叉：趋势反转
                if (diDeathCross)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period, "DiCross");
                    return;
                }

                // ADX跌破退出阈值
                if (adxValue < (double)_adxExitThreshold)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period, "AdxWeak");
                    return;
                }

                // 价格跌破EMA
                if (currentPrice < ema)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period, "EmaBreak");
                    return;
                }

                // 移动止损
                if (_useTrailingStop)
                {
                    decimal newStopLoss = state.HighestPrice - atr * _trailingStopAtrMultiplier;
                    if (newStopLoss > state.StopLoss)
                    {
                        state.StopLoss = newStopLoss;
                    }
                }
            }
            else // 空头持仓
            {
                // 更新最低价
                if (currentLow < state.LowestPrice)
                {
                    state.LowestPrice = currentLow;
                }

                // 止损检查
                if (currentHigh >= state.StopLoss)
                {
                    ClosePosition(state, mktSymbol, state.StopLoss, period, "StopLoss");
                    return;
                }

                // 止盈检查
                if (currentLow <= state.TakeProfit)
                {
                    ClosePosition(state, mktSymbol, state.TakeProfit, period, "TakeProfit");
                    return;
                }

                // DI金叉：趋势反转
                if (diGoldenCross)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period, "DiCross");
                    return;
                }

                // ADX跌破退出阈值
                if (adxValue < (double)_adxExitThreshold)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period, "AdxWeak");
                    return;
                }

                // 价格突破EMA
                if (currentPrice > ema)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period, "EmaBreak");
                    return;
                }

                // 移动止损
                if (_useTrailingStop)
                {
                    decimal newStopLoss = state.LowestPrice + atr * _trailingStopAtrMultiplier;
                    if (newStopLoss < state.StopLoss)
                    {
                        state.StopLoss = newStopLoss;
                    }
                }
            }
        }

        private void OpenLongPosition(TradeState state, string mktSymbol, decimal price, decimal atr, Period period)
        {
            Trade(mktSymbol, OrderType.BUY, price, _tradeAmount, period, 0);

            state.HasPosition = true;
            state.IsLong = true;
            state.EntryPrice = price;
            state.PositionSize = _tradeAmount;
            state.EntryAtr = atr;
            state.StopLoss = price - atr * _stopLossAtrMultiplier;
            state.TakeProfit = price + atr * _takeProfitAtrMultiplier;
            state.HighestPrice = price;
            state.LowestPrice = price;
            state.HoldBars = 0;
        }

        private void OpenShortPosition(TradeState state, string mktSymbol, decimal price, decimal atr, Period period)
        {
            Trade(mktSymbol, OrderType.SELL, price, _tradeAmount, period, 0);

            state.HasPosition = true;
            state.IsLong = false;
            state.EntryPrice = price;
            state.PositionSize = _tradeAmount;
            state.EntryAtr = atr;
            state.StopLoss = price + atr * _stopLossAtrMultiplier;
            state.TakeProfit = price - atr * _takeProfitAtrMultiplier;
            state.HighestPrice = price;
            state.LowestPrice = price;
            state.HoldBars = 0;
        }

        private void ClosePosition(TradeState state, string mktSymbol, decimal price, Period period, string reason)
        {
            OrderType ot = state.IsLong ? OrderType.SELL_TO_COVER : OrderType.BUY_TO_COVER;
            Trade(mktSymbol, ot, price, state.PositionSize, period, 0);

            state.Reset();
        }

        /// <summary>
        /// 计算ATR（使用 Skender.Stock.Indicators）
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
            }
        }
    }
}
