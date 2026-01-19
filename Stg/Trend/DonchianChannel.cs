using Common;
using Model;
using Skender.Stock.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;
using static Model.EnumDef;

namespace QjySDK.Stg
{
    /// <summary>
    /// 唐奇安通道策略 (Donchian Channel Strategy)
    /// 
    /// 策略逻辑：
    /// 1. 上轨 = N周期内的最高价
    /// 2. 下轨 = N周期内的最低价
    /// 3. 中轨 = (上轨 + 下轨) / 2
    /// 
    /// 入场信号：
    /// - 多头入场：价格突破上轨时买入
    /// - 空头入场：价格跌破下轨时卖空
    /// 
    /// 出场信号：
    /// - 多头出场：价格跌破出场下轨（较短周期）或中轨
    /// - 空头出场：价格突破出场上轨（较短周期）或中轨
    /// </summary>
    public class DonchianChannel : StgBase
    {
        private int _entryPeriod = 20;
        private int _exitPeriod = 10;
        private int _exitMode = 0;
        private decimal _positionRatio = 1.0m;

        private Dictionary<string, int> _positionState = new Dictionary<string, int>();

        public DonchianChannel()
        {
        }

        public DonchianChannel(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 10;
            sd.SubChartNum = 0;

            sd.ArgDescDic["entryPeriod"] = new ArgDesc { Text = "入场周期", Explain = "计算入场通道的K线数量" };
            sd.ArgDic["entryPeriod"] = 20;

            sd.ArgDescDic["exitPeriod"] = new ArgDesc { Text = "出场周期", Explain = "计算出场通道的K线数量" };
            sd.ArgDic["exitPeriod"] = 10;

            sd.ArgDescDic["exitMode"] = new ArgDesc { Text = "出场模式", Explain = "0=出场通道, 1=中轨出场" };
            sd.ArgDic["exitMode"] = 0;

            sd.ArgDescDic["positionRatio"] = new ArgDesc { Text = "仓位比例", Explain = "每次交易的仓位比例(0-1)" };
            sd.ArgDic["positionRatio"] = 1.0m;

            sd.ColorDic["main-upperBand"] = "#FF5722";
            sd.ColorDic["main-lowerBand"] = "#2196F3";
            sd.ColorDic["main-middleBand"] = "#9E9E9E";
            sd.ColorDic["main-exitUpper"] = "#FFAB91";
            sd.ColorDic["main-exitLower"] = "#90CAF9";

            return sd;
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            if (ArgDic != null)
            {
                _entryPeriod = Convert.ToInt32(ArgDic["entryPeriod"]);
                _exitPeriod = Convert.ToInt32(ArgDic["exitPeriod"]);
                _exitMode = Convert.ToInt32(ArgDic["exitMode"]);
                _positionRatio = Convert.ToDecimal(ArgDic["positionRatio"]);
            }

            var quotes = tu.QuoteList;
            if (quotes == null || quotes.Count < _entryPeriod + 1)
                return;

            string stateKey = tu.GetStateKey();
            if (!_positionState.ContainsKey(stateKey))
            {
                _positionState[stateKey] = 0;
            }

            var entryDonchian = quotes.GetDonchian(_entryPeriod).ToList();
            var exitDonchian = quotes.GetDonchian(_exitPeriod).ToList();

            var entryChannel = entryDonchian[entryDonchian.Count - 1];
            var exitChannel = exitDonchian[exitDonchian.Count - 1];

            decimal upperBand = (decimal)(entryChannel.UpperBand ?? 0);
            decimal lowerBand = (decimal)(entryChannel.LowerBand ?? 0);
            decimal middleBand = (decimal)(entryChannel.Centerline ?? 0);

            decimal exitUpper = (decimal)(exitChannel.UpperBand ?? 0);
            decimal exitLower = (decimal)(exitChannel.LowerBand ?? 0);

            decimal currentClose = tq.Close;
            decimal prevHigh = quotes[quotes.Count - 2].High;
            decimal prevLow = quotes[quotes.Count - 2].Low;

            Plot("main", "upperBand", PlotType.LINE, (double)upperBand);
            Plot("main", "lowerBand", PlotType.LINE, (double)lowerBand);
            Plot("main", "middleBand", PlotType.LINE, (double)middleBand);

            if (_exitMode == 0)
            {
                Plot("main", "exitUpper", PlotType.LINE, (double)exitUpper);
                Plot("main", "exitLower", PlotType.LINE, (double)exitLower);
            }

            if (!isFinal)
                return;

            int position = _positionState[stateKey];

            if (position == 0)
            {
                if (currentClose > upperBand && prevHigh <= upperBand)
                {
                    Trade(tu.MktSymbol, OrderType.BUY, currentClose, _positionRatio, period, 0);
                    _positionState[stateKey] = 1;
                }
                else if (currentClose < lowerBand && prevLow >= lowerBand)
                {
                    Trade(tu.MktSymbol, OrderType.SELL, currentClose, _positionRatio, period, 0);
                    _positionState[stateKey] = -1;
                }
            }
            else if (position > 0)
            {
                bool exitSignal = false;
                if (_exitMode == 0)
                {
                    exitSignal = currentClose < exitLower;
                }
                else
                {
                    exitSignal = currentClose < middleBand;
                }

                if (exitSignal)
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, currentClose, _positionRatio, period, 0);
                    _positionState[stateKey] = 0;
                }
            }
            else if (position < 0)
            {
                bool exitSignal = false;
                if (_exitMode == 0)
                {
                    exitSignal = currentClose > exitUpper;
                }
                else
                {
                    exitSignal = currentClose > middleBand;
                }

                if (exitSignal)
                {
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, currentClose, _positionRatio, period, 0);
                    _positionState[stateKey] = 0;
                }
            }
        }
    }
}
