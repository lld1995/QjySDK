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
        private int _mode = 0;
        private int _sendMode = 0;
        private Dictionary<string, int> _positionState = new Dictionary<string, int>();
        private Dictionary<string, decimal> _entryPrice = new Dictionary<string, decimal>();
        private Dictionary<string, decimal> _holdNum = new Dictionary<string, decimal>();

        public DonchianChannel()
        {
        }

        public DonchianChannel(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 0;

            sd.ArgDescDic["entryPeriod"] = new ArgDesc { Text = "入场周期", Explain = "计算入场通道的K线数量", Type = "number" };
            sd.ArgDic["entryPeriod"] = 20;

            sd.ArgDescDic["exitPeriod"] = new ArgDesc { Text = "出场周期", Explain = "计算出场通道的K线数量", Type = "number" };
            sd.ArgDic["exitPeriod"] = 10;

            sd.ArgDescDic["exitMode"] = new ArgDesc { Text = "出场模式", Explain = "平仓触发条件", Options = "0:出场通道|1:中轨出场", Type = "select" };
            sd.ArgDic["exitMode"] = 0;

            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易方向", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDic["mode"] = 0;
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDic["sendMode"] = 0;
            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
			sd.ArgDescDic["stopLoss"] = new ArgDesc() { Text = "止损%", Explain = "固定止损百分比，0为不启用", Type = "number" };
            sd.ArgDic["stopLoss"] = 0m;
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDescDic["lots"] = new ArgDesc { Text = "手数", Explain = "固定手数数量", Type = "number" };
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDescDic["money"] = new ArgDesc { Text = "金额", Explain = "固定金额数量", Type = "number" };
            sd.ArgDic["money"] = 10000m;

            sd.ColorDic["main-upperBand"] = "#FF5722";
            sd.ColorDic["main-lowerBand"] = "#2196F3";
            sd.ColorDic["main-middleBand"] = "#9E9E9E";
            sd.ColorDic["main-exitUpper"] = "#FFAB91";
            sd.ColorDic["main-exitLower"] = "#90CAF9";

            return sd;
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (!isFinal) return;

            if (ArgDic != null)
            {
                _entryPeriod = Convert.ToInt32(ArgDic["entryPeriod"]);
                _exitPeriod = Convert.ToInt32(ArgDic["exitPeriod"]);
                _exitMode = Convert.ToInt32(ArgDic["exitMode"]);
                _mode = Convert.ToInt32(ArgDic["mode"]);
                _sendMode = Convert.ToInt32(ArgDic["sendMode"]);
            }

            var quotes = tu.QuoteList;
            int minBars = Math.Max(_entryPeriod, _exitPeriod) + 1;
            if (quotes == null || quotes.Count < minBars)
                return;

            string stateKey = tu.GetStateKey();
            if (!_positionState.ContainsKey(stateKey))
            {
                _positionState[stateKey] = 0;
                _entryPrice[stateKey] = 0;
                _holdNum[stateKey] = 0;
            }

            var entryDonchian = quotes.GetDonchian(_entryPeriod).ToList();
            var exitDonchian = quotes.GetDonchian(_exitPeriod).ToList();

            var entryChannel = entryDonchian[entryDonchian.Count - 2];
            var exitChannel = exitDonchian[exitDonchian.Count - 2];

            decimal upperBand = (decimal)(entryChannel.UpperBand ?? 0);
            decimal lowerBand = (decimal)(entryChannel.LowerBand ?? 0);
            decimal middleBand = (decimal)(entryChannel.Centerline ?? 0);

            decimal exitUpper = (decimal)(exitChannel.UpperBand ?? 0);
            decimal exitLower = (decimal)(exitChannel.LowerBand ?? 0);

            var q = quotes.Last();
            decimal currentClose = q.Close;
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

            int position = _positionState[stateKey];

            if (position == 0)
            {
                if (currentClose > upperBand && prevHigh <= upperBand && _mode != 2)
                {
                    var num = CalculateLots(tu, q);
                    Trade(tu.MktSymbol, OrderType.BUY, currentClose, num, period, _sendMode);
                    _positionState[stateKey] = 1;
                    _entryPrice[stateKey] = currentClose;
                    _holdNum[stateKey] = num;
                }
                else if (currentClose < lowerBand && prevLow >= lowerBand && _mode != 1)
                {
                    var num = CalculateLots(tu, q);
                    Trade(tu.MktSymbol, OrderType.SELL, currentClose, num, period, _sendMode);
                    _positionState[stateKey] = -1;
                    _entryPrice[stateKey] = currentClose;
                    _holdNum[stateKey] = num;
                }
            }
            else if (position > 0)
            {
                // 止损检查
                var _sl = ArgDic.ContainsKey("stopLoss") ? Convert.ToDecimal(ArgDic["stopLoss"]) : 0m;
                if (_sl > 0 && _entryPrice[stateKey] > 0 && currentClose < _entryPrice[stateKey] * (1 - _sl / 100m))
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, currentClose, _holdNum[stateKey], period, _sendMode);
                    _positionState[stateKey] = 0;
                    _entryPrice[stateKey] = 0;
                    _holdNum[stateKey] = 0;
                    return;
                }

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
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, currentClose, _holdNum[stateKey], period, _sendMode);
                    _positionState[stateKey] = 0;
                    _entryPrice[stateKey] = 0;
                    _holdNum[stateKey] = 0;
                }
            }
            else if (position < 0)
            {
                // 止损检查
                var _sl2 = ArgDic.ContainsKey("stopLoss") ? Convert.ToDecimal(ArgDic["stopLoss"]) : 0m;
                if (_sl2 > 0 && _entryPrice[stateKey] > 0 && currentClose > _entryPrice[stateKey] * (1 + _sl2 / 100m))
                {
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, currentClose, _holdNum[stateKey], period, _sendMode);
                    _positionState[stateKey] = 0;
                    _entryPrice[stateKey] = 0;
                    _holdNum[stateKey] = 0;
                    return;
                }

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
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, currentClose, _holdNum[stateKey], period, _sendMode);
                    _positionState[stateKey] = 0;
                    _entryPrice[stateKey] = 0;
                    _holdNum[stateKey] = 0;
                }
            }
        }

        private decimal CalculateLots(TableUnit tu, SkQuote q)
        {
            var num = Convert.ToDecimal(ArgDic["lots"]);
            var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            if (lotsMode == 1)
            {
                var s2 = GetSymbol(tu.MktSymbol);
                num = (Convert.ToDecimal(ArgDic["money"]) / (q.Close * s2.multiplier * s2.margin_ratio));
                if (s2.symbol_type == (int)SymbolType.COIN)
                {
                    num = (int)(num * 1000) / 1000.0m;
                }
                else
                {
                    num = (int)num;
                }
            }
            return num;
        }
    }
}
