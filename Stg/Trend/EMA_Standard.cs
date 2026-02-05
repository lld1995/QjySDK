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
    /// 标准EMA交易策略
    /// 策略逻辑：
    /// 1. 使用快速EMA和慢速EMA的交叉作为交易信号
    /// 2. 快线上穿慢线时做多，快线下穿慢线时做空
    /// 3. 支持趋势过滤：可选使用更长周期EMA作为趋势判断
    /// 4. 支持止损止盈设置
    /// </summary>
    public class EMA_Standard : StgBase
    {
        private Dictionary<string, TradeState> _stateDict = new Dictionary<string, TradeState>();

        private int _fastPeriod;
        private int _slowPeriod;
        private int _trendPeriod;
        private bool _useTrendFilter;
        private decimal _stopLossPercent;
        private decimal _takeProfitPercent;
        private decimal _tradeAmount;

        public EMA_Standard()
        {
        }

        public EMA_Standard(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 0;
            sd.UseGlobalCalc = 0;

            sd.ArgDescDic["FastPeriod"] = new ArgDesc { Text = "快线周期", Explain = "快速EMA的计算周期" };
            sd.ArgDic["FastPeriod"] = 12;

            sd.ArgDescDic["SlowPeriod"] = new ArgDesc { Text = "慢线周期", Explain = "慢速EMA的计算周期" };
            sd.ArgDic["SlowPeriod"] = 26;

            sd.ArgDescDic["TrendPeriod"] = new ArgDesc { Text = "趋势周期", Explain = "趋势判断EMA的计算周期" };
            sd.ArgDic["TrendPeriod"] = 50;

            sd.ArgDescDic["UseTrendFilter"] = new ArgDesc { Text = "启用趋势过滤", Explain = "是否使用趋势EMA过滤信号(1=启用,0=禁用)" };
            sd.ArgDic["UseTrendFilter"] = 1;

            sd.ArgDescDic["StopLossPercent"] = new ArgDesc { Text = "止损百分比", Explain = "止损百分比(如2表示2%)" };
            sd.ArgDic["StopLossPercent"] = 2.0;

            sd.ArgDescDic["TakeProfitPercent"] = new ArgDesc { Text = "止盈百分比", Explain = "止盈百分比(如5表示5%)" };
            sd.ArgDic["TakeProfitPercent"] = 5.0;

            sd.ArgDescDic["TradeAmount"] = new ArgDesc { Text = "交易数量", Explain = "每次交易的数量" };
            sd.ArgDic["TradeAmount"] = 1.0;

            sd.ColorDic["main-EMA_Fast"] = "#FF5722";
            sd.ColorDic["main-EMA_Slow"] = "#4ECDC4";
            sd.ColorDic["main-EMA_Trend"] = "#FF9800";

            return sd;
        }

        private void InitParams()
        {
            _fastPeriod = Convert.ToInt32(ArgDic["FastPeriod"]);
            _slowPeriod = Convert.ToInt32(ArgDic["SlowPeriod"]);
            _trendPeriod = Convert.ToInt32(ArgDic["TrendPeriod"]);
            _useTrendFilter = Convert.ToInt32(ArgDic["UseTrendFilter"]) == 1;
            _stopLossPercent = Convert.ToDecimal(ArgDic["StopLossPercent"]);
            _takeProfitPercent = Convert.ToDecimal(ArgDic["TakeProfitPercent"]);
            _tradeAmount = Convert.ToDecimal(ArgDic["TradeAmount"]);
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (ArgDic == null) return;

            InitParams();

            var quotes = tu.QuoteList;
            int minBars = Math.Max(_slowPeriod, _trendPeriod) + 5;
            if (quotes == null || quotes.Count < minBars) return;

            var stateKey = tu.GetStateKey();
            if (!_stateDict.ContainsKey(stateKey))
            {
                _stateDict[stateKey] = new TradeState();
            }
            var state = _stateDict[stateKey];

            var emaFastList = quotes.GetEma(_fastPeriod).ToList();
            var emaSlowList = quotes.GetEma(_slowPeriod).ToList();
            var emaTrendList = _useTrendFilter ? quotes.GetEma(_trendPeriod).ToList() : null;

            int lastIdx = quotes.Count - 1;
            int prevIdx = lastIdx - 1;

            var emaFastCurr = emaFastList[lastIdx].Ema;
            var emaSlowCurr = emaSlowList[lastIdx].Ema;
            var emaFastPrev = emaFastList[prevIdx].Ema;
            var emaSlowPrev = emaSlowList[prevIdx].Ema;
            var emaTrendCurr = emaTrendList?[lastIdx].Ema;

            if (!emaFastCurr.HasValue || !emaSlowCurr.HasValue ||
                !emaFastPrev.HasValue || !emaSlowPrev.HasValue) return;

            Plot("main", "EMA_Fast", PlotType.CURVE, emaFastCurr);
            Plot("main", "EMA_Slow", PlotType.CURVE, emaSlowCurr);
            if (_useTrendFilter && emaTrendCurr.HasValue)
            {
                Plot("main", "EMA_Trend", PlotType.CURVE, emaTrendCurr);
            }

            if (!isFinal) return;

            var q = quotes.Last();
            decimal currentPrice = q.Close;

            if (state.HasPosition)
            {
                bool shouldClose = CheckStopLossOrTakeProfit(state, currentPrice);
                if (shouldClose)
                {
                    ClosePosition(state, tu.MktSymbol, currentPrice, period);
                    return;
                }

                bool crossDown = emaFastPrev > emaSlowPrev && emaFastCurr <= emaSlowCurr;
                bool crossUp = emaFastPrev < emaSlowPrev && emaFastCurr >= emaSlowCurr;

                if (state.IsLong && crossDown)
                {
                    ClosePosition(state, tu.MktSymbol, currentPrice, period);
                }
                else if (!state.IsLong && crossUp)
                {
                    ClosePosition(state, tu.MktSymbol, currentPrice, period);
                }
            }
            else
            {
                bool crossUp = emaFastPrev < emaSlowPrev && emaFastCurr >= emaSlowCurr;
                bool crossDown = emaFastPrev > emaSlowPrev && emaFastCurr <= emaSlowCurr;

                bool trendAllowLong = !_useTrendFilter || (emaTrendCurr.HasValue && currentPrice > (decimal)emaTrendCurr.Value);
                bool trendAllowShort = !_useTrendFilter || (emaTrendCurr.HasValue && currentPrice < (decimal)emaTrendCurr.Value);

                if (crossUp && trendAllowLong)
                {
                    OpenLongPosition(state, tu.MktSymbol, currentPrice, period);
                }
                else if (crossDown && trendAllowShort)
                {
                    OpenShortPosition(state, tu.MktSymbol, currentPrice, period);
                }
            }
        }

        private bool CheckStopLossOrTakeProfit(TradeState state, decimal currentPrice)
        {
            if (!state.HasPosition) return false;

            decimal pnlPercent;
            if (state.IsLong)
            {
                pnlPercent = (currentPrice - state.EntryPrice) / state.EntryPrice * 100;
            }
            else
            {
                pnlPercent = (state.EntryPrice - currentPrice) / state.EntryPrice * 100;
            }

            if (pnlPercent <= -_stopLossPercent)
            {
                return true;
            }

            if (pnlPercent >= _takeProfitPercent)
            {
                return true;
            }

            return false;
        }

        private void OpenLongPosition(TradeState state, string mktSymbol, decimal price, Period period)
        {
            Trade(mktSymbol, OrderType.BUY, price, _tradeAmount, period, 0);
            state.HasPosition = true;
            state.IsLong = true;
            state.EntryPrice = price;
            state.PositionSize = _tradeAmount;
        }

        private void OpenShortPosition(TradeState state, string mktSymbol, decimal price, Period period)
        {
            Trade(mktSymbol, OrderType.SELL, price, _tradeAmount, period, 0);
            state.HasPosition = true;
            state.IsLong = false;
            state.EntryPrice = price;
            state.PositionSize = _tradeAmount;
        }

        private void ClosePosition(TradeState state, string mktSymbol, decimal price, Period period)
        {
            if (state.IsLong)
            {
                Trade(mktSymbol, OrderType.SELL_TO_COVER, price, state.PositionSize, period, 0);
            }
            else
            {
                Trade(mktSymbol, OrderType.BUY_TO_COVER, price, state.PositionSize, period, 0);
            }

            state.HasPosition = false;
            state.IsLong = false;
            state.EntryPrice = 0;
            state.PositionSize = 0;
        }

        private class TradeState
        {
            public bool HasPosition { get; set; } = false;
            public bool IsLong { get; set; } = false;
            public decimal EntryPrice { get; set; } = 0;
            public decimal PositionSize { get; set; } = 0;
        }
    }
}
