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
    public class MACross : StgBase
    {
        private Dictionary<string, double?> _prevFastMA = new Dictionary<string, double?>();
        private Dictionary<string, double?> _prevSlowMA = new Dictionary<string, double?>();
        private Dictionary<string, int> _positionState = new Dictionary<string, int>();
        private Dictionary<string, decimal> _entryPrice = new Dictionary<string, decimal>();

        public MACross()
        {
        }

        public MACross(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 0;

            sd.ArgDescDic["FastPeriod"] = new ArgDesc { Text = "快线周期", Explain = "快速均线的计算周期" };
            sd.ArgDic["FastPeriod"] = 5;

            sd.ArgDescDic["SlowPeriod"] = new ArgDesc { Text = "慢线周期", Explain = "慢速均线的计算周期" };
            sd.ArgDic["SlowPeriod"] = 20;

            sd.ArgDescDic["stopLoss"] = new ArgDesc { Text = "止损%", Explain = "固定止损百分比，0为不启用" };
            sd.ArgDic["stopLoss"] = 5.0m;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "0:固定手数 1:固定金额" };
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDescDic["lots"] = new ArgDesc { Text = "手数", Explain = "固定手数数量" };
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDescDic["money"] = new ArgDesc { Text = "金额", Explain = "固定金额数量" };
            sd.ArgDic["money"] = 10000m;

            sd.ColorDic["main-FastMA"] = "#FF5722";
            sd.ColorDic["main-SlowMA"] = "#2196F3";

            return sd;
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (!isFinal) return;

            var quotes = tu.QuoteList;
            if (quotes == null || quotes.Count < 2) return;

            int fastPeriod = Convert.ToInt32(ArgDic["FastPeriod"]);
            int slowPeriod = Convert.ToInt32(ArgDic["SlowPeriod"]);

            if (quotes.Count < slowPeriod) return;

            // 使用 Skender.Stock.Indicators 计算 SMA
            var fastSmaList = quotes.GetSma(fastPeriod).ToList();
            var slowSmaList = quotes.GetSma(slowPeriod).ToList();

            var fastSma = fastSmaList[fastSmaList.Count - 1].Sma;
            var slowSma = slowSmaList[slowSmaList.Count - 1].Sma;

            if (!fastSma.HasValue || !slowSma.HasValue) return;

            double? fastMA = fastSma.Value;
            double? slowMA = slowSma.Value;

            Plot("main", "FastMA", PlotType.CURVE, fastMA);
            Plot("main", "SlowMA", PlotType.CURVE, slowMA);

            string stateKey = tu.GetStateKey();
            if (!_positionState.ContainsKey(stateKey)) _positionState[stateKey] = 0;
            if (!_entryPrice.ContainsKey(stateKey)) _entryPrice[stateKey] = 0;

            var q = quotes.Last();
            var num = CalculateLots(tu, q);
            int pos = _positionState[stateKey];

            // 止损检查
            var _sl = (decimal)ArgDic["stopLoss"];
            if (pos == 1 && _sl > 0 && _entryPrice[stateKey] > 0 && q.Close < _entryPrice[stateKey] * (1 - _sl / 100m))
            {
                Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, num, period, 0);
                _positionState[stateKey] = 0;
                _entryPrice[stateKey] = 0;
                _prevFastMA[stateKey] = fastMA;
                _prevSlowMA[stateKey] = slowMA;
                return;
            }
            if (pos == -1 && _sl > 0 && _entryPrice[stateKey] > 0 && q.Close > _entryPrice[stateKey] * (1 + _sl / 100m))
            {
                Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, num, period, 0);
                _positionState[stateKey] = 0;
                _entryPrice[stateKey] = 0;
                _prevFastMA[stateKey] = fastMA;
                _prevSlowMA[stateKey] = slowMA;
                return;
            }

            if (_prevFastMA.TryGetValue(stateKey, out double? prevFast) && 
                _prevSlowMA.TryGetValue(stateKey, out double? prevSlow) &&
                prevFast.HasValue && prevSlow.HasValue)
            {
                bool prevFastAbove = prevFast.Value > prevSlow.Value;
                bool currFastAbove = fastMA.Value > slowMA.Value;

                if (!prevFastAbove && currFastAbove)
                {
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, num, period, 0);
                    Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, 0);
                    _positionState[stateKey] = 1;
                    _entryPrice[stateKey] = q.Close;
                }
                else if (prevFastAbove && !currFastAbove)
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, num, period, 0);
                    Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, 0);
                    _positionState[stateKey] = -1;
                    _entryPrice[stateKey] = q.Close;
                }
            }

            _prevFastMA[stateKey] = fastMA;
            _prevSlowMA[stateKey] = slowMA;
        }

        private decimal CalculateLots(TableUnit tu, SkQuote q)
        {
            var num = (decimal)ArgDic["lots"];
            var lotsMode = (int)ArgDic["lotsMode"];
            if (lotsMode == 1)
            {
                var s2 = GetSymbol(tu.MktSymbol);
                num = ((decimal)ArgDic["money"] / (q.Close * s2.multiplier * s2.margin_ratio));
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
