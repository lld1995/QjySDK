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
    /// EMA_ADX 圣杯策略 (Holy Grail Strategy)
    /// 
    /// 策略核心思想：
    /// 在强趋势市场中，当价格回调到EMA均线附近时入场，顺势交易。
    /// ADX用于确认趋势强度，EMA用于确定趋势方向和入场点位。
    /// 
    /// 指标说明：
    /// 1. ADX (Average Directional Index)：
    ///    - ADX > 阈值（默认25）表示市场处于趋势状态
    ///    - +DI > -DI 表示上升趋势
    ///    - -DI > +DI 表示下降趋势
    /// 
    /// 2. EMA (Exponential Moving Average)：
    ///    - 快速EMA用于判断短期趋势和入场时机
    ///    - 慢速EMA用于确认整体趋势方向
    /// 
    /// 入场条件：
    /// 【做多】
    ///    - ADX > 阈值（强趋势）
    ///    - +DI > -DI（上升趋势）
    ///    - 价格回调触及或接近快速EMA
    ///    - 价格在慢速EMA之上（趋势确认）
    /// 
    /// 【做空】
    ///    - ADX > 阈值（强趋势）
    ///    - -DI > +DI（下降趋势）
    ///    - 价格反弹触及或接近快速EMA
    ///    - 价格在慢速EMA之下（趋势确认）
    /// 
    /// 出场条件：
    /// 1. 止损：基于ATR的动态止损
    /// 2. 止盈：基于ATR的动态止盈或趋势反转
    /// 3. 趋势反转：ADX下降或DI交叉反转
    /// </summary>
    public class EMA_ADX : StgBase
    {
        private int _fastEmaPeriod;
        private int _slowEmaPeriod;
        private int _adxPeriod;
        private decimal _adxThreshold;
        private decimal _emaTouchPercent;
        private decimal _atrPeriod;
        private decimal _stopLossAtrMultiplier;
        private decimal _takeProfitAtrMultiplier;
        private bool _useTrailingStop;
        private decimal _trailingStopAtrMultiplier;

        private Dictionary<string, TradeState> _stateDict = new Dictionary<string, TradeState>();

        public EMA_ADX()
        {
        }

        public EMA_ADX(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 1;
            sd.UseGlobalCalc = 0;

            sd.ArgDescDic["FastEmaPeriod"] = new ArgDesc { Text = "快速EMA周期", Explain = "快速EMA的计算周期，用于入场时机判断" };
            sd.ArgDic["FastEmaPeriod"] = 20;

            sd.ArgDescDic["SlowEmaPeriod"] = new ArgDesc { Text = "慢速EMA周期", Explain = "慢速EMA的计算周期，用于趋势方向确认" };
            sd.ArgDic["SlowEmaPeriod"] = 50;

            sd.ArgDescDic["AdxPeriod"] = new ArgDesc { Text = "ADX周期", Explain = "ADX指标的计算周期" };
            sd.ArgDic["AdxPeriod"] = 14;

            sd.ArgDescDic["AdxThreshold"] = new ArgDesc { Text = "ADX阈值", Explain = "ADX大于此值表示强趋势（建议20-30）" };
            sd.ArgDic["AdxThreshold"] = 25.0;

            sd.ArgDescDic["EmaTouchPercent"] = new ArgDesc { Text = "EMA触及百分比", Explain = "价格距离EMA的百分比阈值，用于判断回调到位" };
            sd.ArgDic["EmaTouchPercent"] = 0.5;

            sd.ArgDescDic["AtrPeriod"] = new ArgDesc { Text = "ATR周期", Explain = "ATR指标的计算周期" };
            sd.ArgDic["AtrPeriod"] = 14;

            sd.ArgDescDic["StopLossAtrMultiplier"] = new ArgDesc { Text = "止损ATR倍数", Explain = "止损距离 = ATR × 此倍数" };
            sd.ArgDic["StopLossAtrMultiplier"] = 2.0;

            sd.ArgDescDic["TakeProfitAtrMultiplier"] = new ArgDesc { Text = "止盈ATR倍数", Explain = "止盈距离 = ATR × 此倍数" };
            sd.ArgDic["TakeProfitAtrMultiplier"] = 4.0;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "0:固定手数 1:固定金额" };
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDescDic["lots"] = new ArgDesc { Text = "手数", Explain = "固定手数数量" };
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDescDic["money"] = new ArgDesc { Text = "金额", Explain = "固定金额数量" };
            sd.ArgDic["money"] = 10000m;

            sd.ArgDescDic["UseTrailingStop"] = new ArgDesc { Text = "启用移动止损", Explain = "是否启用移动止损(1=启用,0=禁用)" };
            sd.ArgDic["UseTrailingStop"] = 1;

            sd.ArgDescDic["TrailingStopAtrMultiplier"] = new ArgDesc { Text = "移动止损ATR倍数", Explain = "移动止损距离 = ATR × 此倍数" };
            sd.ArgDic["TrailingStopAtrMultiplier"] = 1.5;

            sd.ColorDic["main-EMA_Fast"] = "#FF5722";
            sd.ColorDic["main-EMA_Slow"] = "#4ECDC4";
            sd.ColorDic["sub0-ADX"] = "#FFE66D";
            sd.ColorDic["sub0-PDI"] = "#4CAF50";
            sd.ColorDic["sub0-MDI"] = "#F44336";
            sd.ColorDic["main-StopLoss"] = "#FF5722";
            sd.ColorDic["main-TakeProfit"] = "#8BC34A";

            sd.MidValDic["sub0"] = 25;

            return sd;
        }

        private void InitParams()
        {
            _fastEmaPeriod = Convert.ToInt32(ArgDic["FastEmaPeriod"]);
            _slowEmaPeriod = Convert.ToInt32(ArgDic["SlowEmaPeriod"]);
            _adxPeriod = Convert.ToInt32(ArgDic["AdxPeriod"]);
            _adxThreshold = Convert.ToDecimal(ArgDic["AdxThreshold"]);
            _emaTouchPercent = Convert.ToDecimal(ArgDic["EmaTouchPercent"]);
            _atrPeriod = Convert.ToInt32(ArgDic["AtrPeriod"]);
            _stopLossAtrMultiplier = Convert.ToDecimal(ArgDic["StopLossAtrMultiplier"]);
            _takeProfitAtrMultiplier = Convert.ToDecimal(ArgDic["TakeProfitAtrMultiplier"]);
            _useTrailingStop = Convert.ToInt32(ArgDic["UseTrailingStop"]) == 1;
            _trailingStopAtrMultiplier = Convert.ToDecimal(ArgDic["TrailingStopAtrMultiplier"]);
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (!isFinal) return;

            if (ArgDic == null) return;

            InitParams();

            var quotes = tu.QuoteList;
            int minBars = Math.Max(Math.Max(_slowEmaPeriod, _adxPeriod), (int)_atrPeriod) + 5;
            if (quotes == null || quotes.Count < minBars) return;

            var stateKey = tu.GetStateKey();
            if (!_stateDict.ContainsKey(stateKey))
            {
                _stateDict[stateKey] = new TradeState();
            }
            var state = _stateDict[stateKey];

            var emaFastList = quotes.GetEma(_fastEmaPeriod).ToList();
            var emaSlowList = quotes.GetEma(_slowEmaPeriod).ToList();
            var adxList = quotes.GetAdx(_adxPeriod).ToList();

            int lastIdx = quotes.Count - 1;
            int prevIdx = lastIdx - 1;

            var emaFastCurr = emaFastList[lastIdx].Ema;
            var emaSlowCurr = emaSlowList[lastIdx].Ema;
            var emaFastPrev = emaFastList[prevIdx].Ema;

            var adxCurr = adxList[lastIdx];
            var adxPrev = adxList[prevIdx];

            if (!emaFastCurr.HasValue || !emaSlowCurr.HasValue || 
                !adxCurr.Adx.HasValue || !adxCurr.Pdi.HasValue || !adxCurr.Mdi.HasValue) 
                return;

            decimal atr = CalculateATR(quotes, (int)_atrPeriod);
            if (atr <= 0) return;

            var q = quotes.Last();
            decimal currentPrice = q.Close;
            decimal currentHigh = q.High;
            decimal currentLow = q.Low;
            decimal fastEma = (decimal)emaFastCurr.Value;
            decimal slowEma = (decimal)emaSlowCurr.Value;
            double adxValue = adxCurr.Adx.Value;
            double pdi = adxCurr.Pdi.Value;
            double mdi = adxCurr.Mdi.Value;

            Plot("main", "EMA_Fast", PlotType.CURVE, emaFastCurr);
            Plot("main", "EMA_Slow", PlotType.CURVE, emaSlowCurr);
            Plot("sub0", "ADX", PlotType.CURVE, adxValue);
            Plot("sub0", "PDI", PlotType.CURVE, pdi);
            Plot("sub0", "MDI", PlotType.CURVE, mdi);

            if (state.HasPosition)
            {
                if (state.IsLong)
                {
                    Plot("main", "StopLoss", PlotType.LINE, (double)state.StopLoss);
                    Plot("main", "TakeProfit", PlotType.LINE, (double)state.TakeProfit);
                }
                else
                {
                    Plot("main", "StopLoss", PlotType.LINE, (double)state.StopLoss);
                    Plot("main", "TakeProfit", PlotType.LINE, (double)state.TakeProfit);
                }
            }

            if (state.HasPosition)
            {
                HandleExitLogic(state, tu.MktSymbol, period, currentPrice, currentHigh, currentLow, 
                    atr, adxValue, pdi, mdi, fastEma);
            }
            else
            {
                HandleEntryLogic(state, tu.MktSymbol, period, currentPrice, currentHigh, currentLow,
                    atr, adxValue, pdi, mdi, fastEma, slowEma, emaFastPrev);
            }
        }

        private void HandleEntryLogic(TradeState state, string mktSymbol, Period period,
            decimal currentPrice, decimal currentHigh, decimal currentLow,
            decimal atr, double adxValue, double pdi, double mdi,
            decimal fastEma, decimal slowEma, double? emaFastPrev)
        {
            bool isStrongTrend = adxValue > (double)_adxThreshold;
            if (!isStrongTrend) return;

            decimal emaTouchDistance = fastEma * _emaTouchPercent / 100m;
            bool priceTouchedEma = currentLow <= fastEma + emaTouchDistance && currentHigh >= fastEma - emaTouchDistance;
            bool priceNearEma = Math.Abs(currentPrice - fastEma) <= emaTouchDistance;

            bool bullishTrend = pdi > mdi;
            bool bearishTrend = mdi > pdi;

            bool priceAboveSlowEma = currentPrice > slowEma;
            bool priceBelowSlowEma = currentPrice < slowEma;

            if (bullishTrend && priceAboveSlowEma && (priceTouchedEma || priceNearEma))
            {
                if (currentPrice > fastEma)
                {
                    OpenLongPosition(state, mktSymbol, currentPrice, atr, period);
                }
            }
            else if (bearishTrend && priceBelowSlowEma && (priceTouchedEma || priceNearEma))
            {
                if (currentPrice < fastEma)
                {
                    OpenShortPosition(state, mktSymbol, currentPrice, atr, period);
                }
            }
        }

        private void HandleExitLogic(TradeState state, string mktSymbol, Period period,
            decimal currentPrice, decimal currentHigh, decimal currentLow,
            decimal atr, double adxValue, double pdi, double mdi, decimal fastEma)
        {
            if (state.IsLong)
            {
                if (currentPrice <= state.StopLoss)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period);
                    return;
                }

                if (currentPrice >= state.TakeProfit)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period);
                    return;
                }

                if (mdi > pdi || adxValue < (double)_adxThreshold * 0.6)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period);
                    return;
                }

                if (_useTrailingStop)
                {
                    decimal newStopLoss = currentHigh - atr * _trailingStopAtrMultiplier;
                    if (newStopLoss > state.StopLoss)
                    {
                        state.StopLoss = newStopLoss;
                    }
                }
            }
            else
            {
                if (currentPrice >= state.StopLoss)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period);
                    return;
                }

                if (currentPrice <= state.TakeProfit)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period);
                    return;
                }

                if (pdi > mdi || adxValue < (double)_adxThreshold * 0.6)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period);
                    return;
                }

                if (_useTrailingStop)
                {
                    decimal newStopLoss = currentLow + atr * _trailingStopAtrMultiplier;
                    if (newStopLoss < state.StopLoss)
                    {
                        state.StopLoss = newStopLoss;
                    }
                }
            }
        }

        private void OpenLongPosition(TradeState state, string mktSymbol, decimal price, decimal atr, Period period)
        {
            var num = CalculateLots(mktSymbol, price);
            Trade(mktSymbol, OrderType.BUY, price, num, period, 0);

            state.HasPosition = true;
            state.IsLong = true;
            state.EntryPrice = price;
            state.PositionSize = num;
            state.EntryAtr = atr;
            state.StopLoss = price - atr * _stopLossAtrMultiplier;
            state.TakeProfit = price + atr * _takeProfitAtrMultiplier;
            state.HighestPrice = price;
            state.LowestPrice = price;
        }

        private void OpenShortPosition(TradeState state, string mktSymbol, decimal price, decimal atr, Period period)
        {
            var num = CalculateLots(mktSymbol, price);
            Trade(mktSymbol, OrderType.SELL, price, num, period, 0);

            state.HasPosition = true;
            state.IsLong = false;
            state.EntryPrice = price;
            state.PositionSize = num;
            state.EntryAtr = atr;
            state.StopLoss = price + atr * _stopLossAtrMultiplier;
            state.TakeProfit = price - atr * _takeProfitAtrMultiplier;
            state.HighestPrice = price;
            state.LowestPrice = price;
        }

        private void ClosePosition(TradeState state, string mktSymbol, decimal price, Period period)
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
            }
        }
    }
}
