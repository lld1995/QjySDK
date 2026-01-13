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
    public class Donchian : StgBase
    {
    
		public override StgDesc GetStgDesc()
        {
            var sd=new StgDesc();
            sd.ArgDic["lookbackPeriods"] = 20;
            sd.ArgDic["atrLookbackPeriods"] = 20;
            sd.ArgDic["maxAddNum"] = 3;
            sd.ArgDic["mode"] = 0;
            sd.ArgDic["sendMode"] = 0;

            //手数控制
            sd.ArgDic["lotsMode"] = 0;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 100000m;

            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "0 标准 1 仅做多 2 仅做空" }; 
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };
            sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
            sd.SubChartNum = 1;
            sd.ColorDic["macd-macd"] = "#BA55D3";
            sd.ColorDic["macd-signal"] = "";
            sd.ColorDic["macd-histogram"] = "#F6465D;#0ECB81";
			return sd;
        }

        private class State
        {
            public int Status { get;set; }

            public decimal Num { get; set; }

            public int AddNum { get; set; }

            public decimal LastOpenPrice { get; set; }
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        public Donchian(string id) : base(id)
        {
        }

        public override void OnSendOrder(TableUnit tu, decimal price)
        {
            base.OnSendOrder(tu, price);
            State s = null;
            var sk = tu.GetStateKey();
            if (_stateDic.ContainsKey(sk))
            {
                s = _stateDic[sk];
            }
            else
            {
                s = new State();
                _stateDic[sk] = s;
            }
            s.LastOpenPrice = price;
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal,SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            State s = null;
            var sk = tu.GetStateKey();
            if (_stateDic.ContainsKey(sk))
            {
                s = _stateDic[sk];
            }
            else
            {
                s = new State();
                _stateDic[sk] = s;
            }

			if (isFinal)
			{
				if (tu.QuoteList.Count > 1)
				{
					int mode = (int)ArgDic["mode"];
					int sendMode = (int)ArgDic["sendMode"];
                    int maxAddNum = (int)ArgDic["maxAddNum"];
                    var q = tu.QuoteList.Last();
					var dca = tu.QuoteList.GetDonchian((int)ArgDic["lookbackPeriods"]).ToList();

					var dca1 = dca[dca.Count - 1];
					Plot("main", "up", PlotType.LINE, (double?)dca1.UpperBand);
					Plot("main", "low", PlotType.LINE, (double?)dca1.LowerBand);

                    var dca2 = dca[dca.Count - 2];
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

                    AtrResult atr1 = null;
                    if (s.Status == 0)
                    {

                    }
                    else
                    {
                        var atr = tu.QuoteList.GetAtr((int)ArgDic["atrLookbackPeriods"]).ToList();
                        atr1 = atr[atr.Count - 1];
                    }

					var lastOpenPrice = s.LastOpenPrice;
					if (s.Status == 0)
					{
						if (q.Close > dca2.UpperBand && mode!=2)
						{
                            s.Status = 1;
                            s.Num = num;
                            Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                        }
						if (q.Close < dca2.LowerBand && mode != 1)
						{
                            s.Status = 2;
                            s.Num = num;
                            Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                        }
					}
					else if (s.Status == 1)
					{
                        if (atr1.Atr.HasValue)
                        {
                            if (q.Close > lastOpenPrice + (decimal)atr1.Atr && s.AddNum < maxAddNum)
                            {
                                s.Num += num;
                                ++s.AddNum;
                                Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                            }
                        }

						if (q.Close < dca2.LowerBand)
                        {
                            var oriNum = s.Num;
                            s.Num = 0;
                            s.Status = 0;
                            s.AddNum = 0;
                            Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);
                        }
                    }
					else if (s.Status == 2)
					{
                        if (atr1.Atr.HasValue)
                        {
                            if (q.Close < lastOpenPrice - (decimal)atr1.Atr && s.AddNum < maxAddNum)
                            {
                                s.Num += num;
                                ++s.AddNum;
                                Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                            }
                        }

                        if (q.Close > dca2.UpperBand)
                        {
                            var oriNum = s.Num;
                            s.Num = 0;
                            s.Status = 0;
                            s.AddNum = 0;
                            Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);
                        }
                    }
				}
			}
		}
    }
}
