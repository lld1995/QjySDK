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
        private decimal _unitPositionRatio = 0.25m;

        private Dictionary<string, PositionInfo> _positionInfos = new Dictionary<string, PositionInfo>();

        public DonchianATR(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 10;
            sd.SubChartNum = 1;

            sd.ArgDescDic["entryPeriod"] = new ArgDesc { Text = "入场周期", Explain = "计算入场通道的K线数量" };
            sd.ArgDic["entryPeriod"] = 20;

            sd.ArgDescDic["exitPeriod"] = new ArgDesc { Text = "出场周期", Explain = "计算出场通道的K线数量" };
            sd.ArgDic["exitPeriod"] = 10;

            sd.ArgDescDic["atrPeriod"] = new ArgDesc { Text = "ATR周期", Explain = "计算ATR的K线数量" };
            sd.ArgDic["atrPeriod"] = 14;

            sd.ArgDescDic["atrMultiplierForAdd"] = new ArgDesc { Text = "加仓ATR倍数", Explain = "价格移动多少ATR后加仓" };
            sd.ArgDic["atrMultiplierForAdd"] = 0.5m;

            sd.ArgDescDic["atrMultiplierForStop"] = new ArgDesc { Text = "止损ATR倍数", Explain = "止损距离为多少ATR" };
            sd.ArgDic["atrMultiplierForStop"] = 2.0m;

            sd.ArgDescDic["maxPyramidUnits"] = new ArgDesc { Text = "最大加仓次数", Explain = "最多允许加仓的次数（包括首次建仓）" };
            sd.ArgDic["maxPyramidUnits"] = 4;

            sd.ArgDescDic["unitPositionRatio"] = new ArgDesc { Text = "单位仓位比例", Explain = "每次建仓/加仓的仓位比例(0-1)" };
            sd.ArgDic["unitPositionRatio"] = 0.25m;

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
            if (ArgDic != null)
            {
                _entryPeriod = Convert.ToInt32(ArgDic["entryPeriod"]);
                _exitPeriod = Convert.ToInt32(ArgDic["exitPeriod"]);
                _atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
                _atrMultiplierForAdd = Convert.ToDecimal(ArgDic["atrMultiplierForAdd"]);
                _atrMultiplierForStop = Convert.ToDecimal(ArgDic["atrMultiplierForStop"]);
                _maxPyramidUnits = Convert.ToInt32(ArgDic["maxPyramidUnits"]);
                _unitPositionRatio = Convert.ToDecimal(ArgDic["unitPositionRatio"]);
            }

            var quotes = tu.QuoteList;
            int minBars = Math.Max(_entryPeriod, _atrPeriod) + 1;
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
            Plot("main", "exitUpper", PlotType.LINE, (double)exitUpper);
            Plot("main", "exitLower", PlotType.LINE, (double)exitLower);
            Plot("sub0", "atr", PlotType.LINE, (double)atr);

            if (posInfo.Direction != 0 && posInfo.StopLoss > 0)
            {
                Plot("main", "stopLoss", PlotType.LINE, (double)posInfo.StopLoss);
            }

            if (!isFinal)
                return;

            if (posInfo.Direction == 0)
            {
                if (currentClose > upperBand && prevHigh <= upperBand)
                {
                    OpenPosition(tu.MktSymbol, period, currentClose, atr, 1, posInfo);
                }
                else if (currentClose < lowerBand && prevLow >= lowerBand)
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
                else if (posInfo.Units < _maxPyramidUnits)
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
                else if (posInfo.Units < _maxPyramidUnits)
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
            Trade(mktSymbol, ot, price, _unitPositionRatio, period, 0);

            posInfo.Direction = direction;
            posInfo.Units = 1;
            posInfo.FirstEntryPrice = price;
            posInfo.LastEntryPrice = price;
            posInfo.EntryATR = atr;
            posInfo.TotalPositionRatio = _unitPositionRatio;

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
            Trade(mktSymbol, ot, price, _unitPositionRatio, period, 0);

            posInfo.Units++;
            posInfo.LastEntryPrice = price;
            posInfo.TotalPositionRatio += _unitPositionRatio;

            if (posInfo.Direction > 0)
            {
                posInfo.StopLoss = price - _atrMultiplierForStop * atr;
            }
            else
            {
                posInfo.StopLoss = price + _atrMultiplierForStop * atr;
            }
        }

        private void CloseAllPosition(string mktSymbol, Period period, decimal price, PositionInfo posInfo)
        {
            OrderType ot = posInfo.Direction > 0 ? OrderType.SELL_TO_COVER : OrderType.BUY_TO_COVER;
            Trade(mktSymbol, ot, price, posInfo.TotalPositionRatio, period, 0);

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
