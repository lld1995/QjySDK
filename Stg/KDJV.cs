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
    public class KDJV : StgBase
    {
        public KDJV(string id) : base(id)
        {
        }

		public override StgDesc GetStgDesc()
        {
            var sd=new StgDesc();
            sd.ArgDic["lookbackPeriods"] = 14;
            sd.ArgDic["signalPeriods"] = 3;
            sd.ArgDic["smoothPeriods"] = 3;
            sd.ArgDic["mode"] = 0;
            sd.ArgDic["sendMode"] = 0;
            sd.ArgDic["lowJ"] = 0d;
            sd.ArgDic["highJ"] = 100d;
            sd.ArgDic["lossRate"] = 5m;

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
            sd.MidValDic["kdj"] = 50;
			sd.ColorDic["kdj-K"] = "#F6465D";
			sd.ColorDic["kdj-D"] = "#E0A166";
			sd.ColorDic["kdj-J"] = "#C562A6";
			return sd;
        }

        private class State
        {
            public int Status { get;set; }

            public decimal Num { get; set; }

            public decimal LossPrice { get; set; }
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

		public override void OnBar(Period period, TableUnit tu, bool isFinal,SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (isFinal)
			{
				if (tu.QuoteList.Count > 2)
				{
                    double lowJ = (double)ArgDic["lowJ"];
                    double highJ = (double)ArgDic["highJ"];
                    decimal lossRate = (decimal)ArgDic["lossRate"];
					int mode = (int)ArgDic["mode"];
					int sendMode = (int)ArgDic["sendMode"];
                    var q = tu.QuoteList.Last();

                    var kdj = tu.QuoteList.GetStoch((int)ArgDic["lookbackPeriods"], (int)ArgDic["signalPeriods"], (int)ArgDic["smoothPeriods"]).ToList();

                    var kdj1 = kdj[kdj.Count - 1];

                    Plot("kdj", "K", PlotType.LINE, kdj1.K);
                    Plot("kdj", "D", PlotType.LINE, kdj1.D);
                    Plot("kdj", "J", PlotType.LINE, kdj1.J);

                    var kdj2 = kdj[kdj.Count - 2];
                    var kdj3 = kdj[kdj.Count - 3];

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
						if (kdj2.J < kdj1.J && kdj2.J<kdj3.J && mode!=2 && kdj2.J < lowJ)
						{
                            s.Status = 1;
                            s.Num = num;
                            s.LossPrice = (100 - lossRate) / 100 * q.Close;
                            Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period,sendMode);
                        }
						if (kdj2.J > kdj1.J && kdj2.J > kdj3.J && mode != 1 && kdj2.J > highJ)
						{
                            s.Status = 2;
                            s.Num = num;
                            s.LossPrice = (100 + lossRate) / 100 * q.Close;
                            Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                        }
					}
					else if (s.Status == 1)
					{
                        if (q.Close < s.LossPrice)
                        {
                            var oriNum = s.Num;
                            s.Status = 0;
                            s.Num = 0;
                            Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);
                        }
						else if (kdj2.J > kdj1.J && kdj2.J > kdj3.J && kdj2.J > highJ)
                        {
                            var oriNum = s.Num;
                            Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);
                            if (mode != 1)
                            {
                                s.Status = 2;
                                s.Num = num;
                                s.LossPrice = (100 + lossRate) / 100 * q.Close;
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
                        if (q.Close > s.LossPrice)
                        {
                            var oriNum = s.Num;
                            s.Status = 0;
                            s.Num = 0;
                            Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);
                        }
                        else if (kdj2.J < kdj1.J && kdj2.J < kdj3.J && kdj2.J < lowJ)
                        {
                            var oriNum = s.Num;
                            Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);
                            if (mode != 2)
                            {
                                s.Status = 1;
                                s.Num = num;
                                s.LossPrice = (100 - lossRate) / 100 * q.Close;
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
