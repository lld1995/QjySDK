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

            sd.ColorDic["main-FastMA"] = "#FF5722";
            sd.ColorDic["main-SlowMA"] = "#2196F3";

            return sd;
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
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
            
            if (_prevFastMA.TryGetValue(stateKey, out double? prevFast) && 
                _prevSlowMA.TryGetValue(stateKey, out double? prevSlow) &&
                prevFast.HasValue && prevSlow.HasValue)
            {
                bool prevFastAbove = prevFast.Value > prevSlow.Value;
                bool currFastAbove = fastMA.Value > slowMA.Value;

                if (!prevFastAbove && currFastAbove)
                {
                    Trade(tu.MktSymbol, OrderType.BUY, tq.Close, 1, period, 0);
                }
                else if (prevFastAbove && !currFastAbove)
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, tq.Close, 1, period, 0);
                }
            }

            _prevFastMA[stateKey] = fastMA;
            _prevSlowMA[stateKey] = slowMA;
        }

    }
}
