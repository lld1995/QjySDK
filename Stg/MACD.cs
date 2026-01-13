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
    public class MACD : StgBase
    {
        public MACD(string id) : base(id)
        {
        }

		public override StgDesc GetStgDesc()
        {
            var sd=new StgDesc();
            sd.ArgDic["fastPeriods"] = 12;
            sd.ArgDic["slowPeriods"] = 26;
            sd.ArgDic["signalPeriods"] = 9;
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
            sd.MidValDic["macd"] = 0;
			return sd;
        }

        private class State
        {
            public int Status { get;set; }

            public decimal Num { get; set; }
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

		public override void OnBar(Period period, TableUnit tu, bool isFinal,SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (isFinal)
			{
				if (tu.QuoteList.Count > 1)
				{
					int mode = (int)ArgDic["mode"];
					int sendMode = (int)ArgDic["sendMode"];
                    var q = tu.QuoteList.Last();
					var macd = tu.QuoteList.GetMacd((int)ArgDic["fastPeriods"], (int)ArgDic["slowPeriods"], (int)ArgDic["signalPeriods"]).ToList();
					var macd1 = macd[macd.Count - 1];

					Plot("macd", "histogram", PlotType.RECTANGLE, macd1.Histogram);
					Plot("macd", "macd", PlotType.LINE, macd1.Macd);
					Plot("macd", "signal", PlotType.LINE, macd1.Signal);

					var macd2 = macd[macd.Count - 2];
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

                    State s = null;
                    var sk = tu.GetStateKey();
                    if (_stateDic.ContainsKey(sk))
                    {
                        s = _stateDic[sk];
                    }
                    else
                    {
                        s=new State();
                        _stateDic[sk] = s;
                    }

					if (s.Status == 0)
					{
						if (macd1.Macd > macd1.Signal && macd2.Macd < macd2.Signal && mode!=2)
						{
                            s.Status = 1;
                            s.Num = num;
                            Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period,sendMode);
                        }
						if (macd1.Macd < macd1.Signal && macd2.Macd > macd2.Signal && mode != 1)
						{
                            s.Status = 2;
                            s.Num = num;
                            Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                        }
					}
					else if (s.Status == 1)
					{
						if (macd1.Macd < macd1.Signal && macd2.Macd > macd2.Signal)
						{
                            var oriNum = s.Num;
                            Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);

                            if (mode != 1)
                            {
                                s.Status = 2;
                                s.Num = num;
                                Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                            }
                            else
                            {
                                s.Status = 0;
                                s.Num = 0;
                            }
                        }
					}
					else if (s.Status == 2)
					{
						if (macd1.Macd > macd1.Signal && macd2.Macd < macd2.Signal)
						{
                            var oriNum = s.Num;
                            Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);

                            if (mode != 2)
                            {
                                s.Status = 1;
                                s.Num = num;
                                Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                            }
                            else
                            {
                                s.Status = 0;
                                s.Num = 0;
                            }
                        }
					}
				}
			}
		}
    }
}
