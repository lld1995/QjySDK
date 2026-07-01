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
    /// 唐奇安通道 + ATR加仓策略 (Donchian Channel + ATR Pyramiding Strategy)
    /// 
    /// 策略逻辑：
    /// 1. 唐奇安通道：
    ///    - 上轨 = N周期内的最高价
    ///    - 下轨 = N周期内的最低价
    ///    - 中轨 = (上轨 + 下轨) / 2
    /// 
    /// 2. ATR (Average True Range)：
    ///    - 用于计算加仓间距和止损距离
    ///    - ATR = N周期真实波幅的移动平均
    /// 
    /// 入场信号：
    /// - 多头入场：价格突破上轨时买入
    /// - 空头入场：价格跌破下轨时卖空
    /// 
    /// 加仓规则（金字塔加仓）：
    /// - 每当价格向有利方向移动 0.5*ATR 时加仓
    /// - 最大加仓次数可配置
    /// - 每次加仓后更新止损位
    /// 
    /// 止损规则：
    /// - 初始止损：入场价 - 2*ATR（多头）或 入场价 + 2*ATR（空头）
    /// - 加仓后止损：最后加仓价 - 2*ATR
    /// 
    /// 出场信号：
    /// - 触发止损
    /// - 价格跌破出场通道下轨（多头）或突破出场通道上轨（空头）
    /// </summary>
    public class DonchianATR : StgBase
    {
        private int _entryPeriod = 20;
        private int _exitPeriod = 10;
        private int _atrPeriod = 14;
        private decimal _atrMultiplierForAdd = 0.5m;
        private decimal _atrMultiplierForStop = 2.0m;
        private int _maxPyramidUnits = 4;
        private int _enablePyramiding = 0;
        private int _mode = 0;
        private int _sendMode = 0;
        private Dictionary<string, PositionInfo> _positionInfos = new Dictionary<string, PositionInfo>();

        public DonchianATR()
        {
        }

        public DonchianATR(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 1;

            sd.ArgDescDic["entryPeriod"] = new ArgDesc { Text = "入场周期", Explain = "计算入场通道的K线数量", Type = "number" };
            sd.ArgDic["entryPeriod"] = 20;

            sd.ArgDescDic["exitPeriod"] = new ArgDesc { Text = "出场周期", Explain = "计算出场通道的K线数量", Type = "number" };
            sd.ArgDic["exitPeriod"] = 10;

            sd.ArgDescDic["atrPeriod"] = new ArgDesc { Text = "ATR周期", Explain = "计算ATR的K线数量", Type = "number" };
            sd.ArgDic["atrPeriod"] = 14;

            sd.ArgDescDic["atrMultiplierForAdd"] = new ArgDesc { Text = "加仓ATR倍数", Explain = "价格移动多少ATR后加仓", Type = "number" };
            sd.ArgDic["atrMultiplierForAdd"] = 0.5m;

            sd.ArgDescDic["atrMultiplierForStop"] = new ArgDesc { Text = "止损ATR倍数", Explain = "止损距离为多少ATR", Type = "number" };
            sd.ArgDic["atrMultiplierForStop"] = 2.0m;

            sd.ArgDescDic["maxPyramidUnits"] = new ArgDesc { Text = "最大加仓次数", Explain = "最多允许加仓的次数（包括首次建仓）", Type = "number" };
            sd.ArgDic["maxPyramidUnits"] = 4;

            sd.ArgDescDic["enablePyramiding"] = new ArgDesc { Text = "启用加仓", Explain = "是否启用金字塔加仓", Options = "0:不加仓|1:金字塔加仓", Type = "bool" };
            sd.ArgDic["enablePyramiding"] = 0;

            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易方向", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDic["mode"] = 0;
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDic["sendMode"] = 0;
            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
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
            sd.ColorDic["sub0-atr"] = "#4CAF50";
            sd.ColorDic["main-stopLoss"] = "#F44336";

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
                _atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
                _atrMultiplierForAdd = Convert.ToDecimal(ArgDic["atrMultiplierForAdd"]);
                _atrMultiplierForStop = Convert.ToDecimal(ArgDic["atrMultiplierForStop"]);
                _maxPyramidUnits = Convert.ToInt32(ArgDic["maxPyramidUnits"]);
                _enablePyramiding = Convert.ToInt32(ArgDic["enablePyramiding"]);
                _mode = Convert.ToInt32(ArgDic["mode"]);
                _sendMode = Convert.ToInt32(ArgDic["sendMode"]);
            }

            var quotes = tu.QuoteList;
            int minBars = Math.Max(Math.Max(_entryPeriod, _exitPeriod), _atrPeriod) + 1;
            if (quotes == null || quotes.Count < minBars)
                return;

            string stateKey = tu.GetStateKey();
            if (!_positionInfos.ContainsKey(stateKey))
            {
                _positionInfos[stateKey] = new PositionInfo();
            }

            var posInfo = _positionInfos[stateKey];

            var entryDonchian = quotes.GetDonchian(_entryPeriod).ToList();
            var exitDonchian = quotes.GetDonchian(_exitPeriod).ToList();
            decimal atr = CalculateATR(quotes, _atrPeriod);

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
            Plot("main", "exitUpper", PlotType.LINE, (double)exitUpper);
            Plot("main", "exitLower", PlotType.LINE, (double)exitLower);
            Plot("sub0", "atr", PlotType.LINE, (double)atr);

            if (posInfo.Direction != 0 && posInfo.StopLoss > 0)
            {
                Plot("main", "stopLoss", PlotType.LINE, (double)posInfo.StopLoss);
            }

            if (posInfo.Direction == 0)
            {
                if (currentClose > upperBand && prevHigh <= upperBand && _mode != 2)
                {
                    OpenPosition(tu.MktSymbol, period, currentClose, atr, 1, posInfo);
                }
                else if (currentClose < lowerBand && prevLow >= lowerBand && _mode != 1)
                {
                    OpenPosition(tu.MktSymbol, period, currentClose, atr, -1, posInfo);
                }
            }
            else if (posInfo.Direction > 0)
            {
                if (currentClose <= posInfo.StopLoss)
                {
                    CloseAllPosition(tu.MktSymbol, period, currentClose, posInfo);
                }
                else if (currentClose < exitLower)
                {
                    CloseAllPosition(tu.MktSymbol, period, currentClose, posInfo);
                }
                else if (_enablePyramiding == 1 && posInfo.Units < _maxPyramidUnits)
                {
                    decimal addThreshold = posInfo.LastEntryPrice + _atrMultiplierForAdd * posInfo.EntryATR;
                    if (currentClose >= addThreshold)
                    {
                        AddPosition(tu.MktSymbol, period, currentClose, atr, posInfo);
                    }
                }
            }
            else if (posInfo.Direction < 0)
            {
                if (currentClose >= posInfo.StopLoss)
                {
                    CloseAllPosition(tu.MktSymbol, period, currentClose, posInfo);
                }
                else if (currentClose > exitUpper)
                {
                    CloseAllPosition(tu.MktSymbol, period, currentClose, posInfo);
                }
                else if (_enablePyramiding == 1 && posInfo.Units < _maxPyramidUnits)
                {
                    decimal addThreshold = posInfo.LastEntryPrice - _atrMultiplierForAdd * posInfo.EntryATR;
                    if (currentClose <= addThreshold)
                    {
                        AddPosition(tu.MktSymbol, period, currentClose, atr, posInfo);
                    }
                }
            }
        }

        private void OpenPosition(string mktSymbol, Period period, decimal price, decimal atr, int direction, PositionInfo posInfo)
        {
            OrderType ot = direction > 0 ? OrderType.BUY : OrderType.SELL;
            var num = CalculateLots(mktSymbol, price);
            Trade(mktSymbol, ot, price, num, period, _sendMode);

            posInfo.Direction = direction;
            posInfo.Units = 1;
            posInfo.FirstEntryPrice = price;
            posInfo.LastEntryPrice = price;
            posInfo.EntryATR = atr;
            posInfo.TotalPositionRatio = num;

            if (direction > 0)
            {
                posInfo.StopLoss = price - _atrMultiplierForStop * atr;
            }
            else
            {
                posInfo.StopLoss = price + _atrMultiplierForStop * atr;
            }
        }

        private void AddPosition(string mktSymbol, Period period, decimal price, decimal atr, PositionInfo posInfo)
        {
            OrderType ot = posInfo.Direction > 0 ? OrderType.BUY : OrderType.SELL;
            var num = CalculateLots(mktSymbol, price);
            Trade(mktSymbol, ot, price, num, period, _sendMode);

            posInfo.Units++;
            posInfo.LastEntryPrice = price;
            posInfo.TotalPositionRatio += num;

            if (posInfo.Direction > 0)
            {
                decimal newStopLoss = price - _atrMultiplierForStop * atr;
                posInfo.StopLoss = Math.Max(posInfo.StopLoss, newStopLoss);
            }
            else
            {
                decimal newStopLoss = price + _atrMultiplierForStop * atr;
                posInfo.StopLoss = Math.Min(posInfo.StopLoss, newStopLoss);
            }
        }

        private void CloseAllPosition(string mktSymbol, Period period, decimal price, PositionInfo posInfo)
        {
            OrderType ot = posInfo.Direction > 0 ? OrderType.SELL_TO_COVER : OrderType.BUY_TO_COVER;
            Trade(mktSymbol, ot, price, posInfo.TotalPositionRatio, period, _sendMode);

            posInfo.Reset();
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

        private class PositionInfo
        {
            public int Direction { get; set; }
            public int Units { get; set; }
            public decimal FirstEntryPrice { get; set; }
            public decimal LastEntryPrice { get; set; }
            public decimal EntryATR { get; set; }
            public decimal StopLoss { get; set; }
            public decimal TotalPositionRatio { get; set; }

            public void Reset()
            {
                Direction = 0;
                Units = 0;
                FirstEntryPrice = 0;
                LastEntryPrice = 0;
                EntryATR = 0;
                StopLoss = 0;
                TotalPositionRatio = 0;
            }
        }
    }
}
